// FreedomBridgeServer.cs
// Part of the Freedom Bridge — drop this into Assets/Editor/FreedomBridge/
// Starts an HTTP server on localhost:23456 when the Unity Editor opens.
// Survives domain reloads via [InitializeOnLoad].

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace FreedomBridge
{
    [InitializeOnLoad]
    public static class FreedomBridgeServer
    {
        public const int PORT = 23456;
        private const string TEMP_DIR = "Assets/FreedomBridge/Editor/_Temp";

        private static HttpListener _listener;
        private static Thread _listenerThread;
        private static readonly ConcurrentQueue<Action> _mainThreadQueue = new ConcurrentQueue<Action>();
        private static volatile bool _running = false;
        private static System.DateTime _lastDomainReloadTime = System.DateTime.Now;
        private static int _errorCount = 0;
        private static readonly List<string> _compilationLogs = new List<string>();
        private static readonly HashSet<string> _seenMessages = new HashSet<string>();
        private static readonly object _logsLock = new object();

        // ── Lifecycle ────────────────────────────────────────────────────────────

     static FreedomBridgeServer()
        {
            EditorApplication.update += Tick;
            Application.logMessageReceived += CaptureLog;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeReload;
            AssemblyReloadEvents.afterAssemblyReload += OnAfterReload;

            // Resume any pending compile-cycle job that survived the domain reload
            ScriptExecutor.ResumePendingJob();

            StartServer();
        }

        private static void OnBeforeReload()
        {
            _lastDomainReloadTime = System.DateTime.Now;
            // Don't clear logs here - capture errors that happen during compilation
            StopServer();
            EditorApplication.update -= Tick;
            // Don't detach CaptureLog - we want to capture compile errors even during reload
        }

     private static void OnAfterReload()
        {
            // Re-attach Tick handler
            EditorApplication.update += Tick;

            // Start the server
            StartServer();
        }

        // ── ScriptCompilation callbacks ───────────────────────────────────────────

        private static void OnScriptCompilationStarted()
        {
            Debug.Log("[FreedomBridge] Script compilation started");
            _errorCount = 0;
            lock (_logsLock)
            {
                _compilationLogs.Clear();
            }
        }

        // LSP doesn't know CompiledScript but this compiles at runtime
        // Method signature must be: void(object[], bool, string) for the callback
        private static void OnScriptCompilationCompleted(object scripts, bool successful, string logFile)
        {
            Debug.Log($"[FreedomBridge] Script compilation completed, successful={successful}, logFile={logFile}");
            
            if (!successful && !string.IsNullOrEmpty(logFile))
            {
                try
                {
                    if (System.IO.File.Exists(logFile))
                    {
                        var logContent = System.IO.File.ReadAllText(logFile);
                        foreach (var line in logContent.Split('\n'))
                        {
                            var trimmed = line.Trim();
                            if (!string.IsNullOrWhiteSpace(trimmed))
                            {
                                lock (_logsLock)
                                {
                                    _compilationLogs.Add(trimmed);
                                }
                                
                                if (trimmed.Contains(("error CS")))
                                {
                                    _errorCount++;
                                }
                            }
                        }
                        
                        Debug.Log($"[FreedomBridge] Loaded {_compilationLogs.Count} compilation log entries, {_errorCount} errors");
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[FreedomBridge] Failed to read compilation log: {ex.Message}");
                }
            }
        }

        // ── Server start/stop ────────────────────────────────────────────────────

        public static void StartServer()
        {
            if (_running) return;

            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://127.0.0.1:{PORT}/");
                _listener.Start();
                _running = true;

                _listenerThread = new Thread(ListenLoop) { IsBackground = true, Name = "FreedomBridge-HTTP" };
                _listenerThread.Start();

                Debug.Log($"[FreedomBridge] Server started on http://127.0.0.1:{PORT}/");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[FreedomBridge] Failed to start server: {ex.Message}");
            }
        }

        public static void StopServer()
        {
            _running = false;
            try { _listener?.Stop(); } catch { }
            _listener = null;
        }

        // ── Main-thread dispatcher ────────────────────────────────────────────────

        private static void Tick()
        {
            while (_mainThreadQueue.TryDequeue(out Action action))
            {
                try { action(); }
                catch (Exception ex) { Debug.LogError($"[FreedomBridge] Main-thread action failed: {ex}"); }
            }
        }

        public static void RunOnMainThread(Action action) => _mainThreadQueue.Enqueue(action);

        // ── Log capture ───────────────────────────────────────────────────────────

           private static void CaptureLog(string message, string stackTrace, LogType type)
        {
            // Create a formatted log entry
            string prefix = type == LogType.Error || type == LogType.Exception ? "[ERROR]"
                        : type == LogType.Warning ? "[WARN]"
                        : "[LOG]";
            string formatted = $"{prefix} {message}";
            
            // Use deduplication - only add if we haven't seen this exact message before
            lock (_logsLock)
            {
                if (_seenMessages.Contains(formatted))
                {
                    return; // Already added this message
                }
                _seenMessages.Add(formatted);
                
                // Count errors
                if (type == LogType.Error || type == LogType.Exception)
                {
                    _errorCount++;
                }
                
                // Add to logs
                _compilationLogs.Add(formatted);
                
                // Keep only the last 200 entries
                while (_compilationLogs.Count > 200)
                {
                    _compilationLogs.RemoveAt(0);
                }
                
                // Also remove old entries from seenMessages
                if (_seenMessages.Count > 500)
                {
                    // Rebuild seenMessages from current logs
                    _seenMessages.Clear();
                    foreach (var log in _compilationLogs)
                    {
                        _seenMessages.Add(log);
                    }
                }
            }
        }

        public static int GetErrorCount() => _errorCount;

        // ── HTTP Listener loop ────────────────────────────────────────────────────

        private static void ListenLoop()
        {
            while (_running)
            {
                HttpListenerContext ctx;
                try { ctx = _listener.GetContext(); }
                catch { break; }

                ThreadPool.QueueUserWorkItem(_ => HandleRequest(ctx));
            }
        }

        private static void HandleRequest(HttpListenerContext ctx)
        {
            try
            {
                var req  = ctx.Request;
                var path = req.Url.AbsolutePath.TrimEnd('/');

                if (req.HttpMethod == "GET" && path == "/status")     { HandleStatus(ctx); return; }
                if (req.HttpMethod == "GET" && path == "/logs")       { HandleLogs(ctx);   return; }
                if (req.HttpMethod == "POST" && path == "/exec")      { HandleExec(ctx);    return; }
                if (req.HttpMethod == "POST" && path == "/compile")   { HandleCompile(ctx); return; }
                if (req.HttpMethod == "POST" && path == "/coroutine") { HandleCoroutine(ctx); return; }
                if (req.HttpMethod == "GET"  && path.StartsWith("/result/")) { HandleResult(ctx, path.Substring("/result/".Length)); return; }
                if (req.HttpMethod == "POST" && path == "/refresh")   { HandleRefresh(ctx); return; }

                RespondJson(ctx, Error("Unknown endpoint"), 404);
            }
            catch (Exception ex)
            {
                try { RespondJson(ctx, Error(ex.ToString()), 500); } catch { }
            }
        }

        private static void HandleStatus(HttpListenerContext ctx)
        {
            string result = null;
            var done = new ManualResetEventSlim(false);

            RunOnMainThread(() =>
            {
                try { result = Status(); }
                catch (Exception ex) { result = Error(ex.ToString()); }
                finally { done.Set(); }
            });

            bool completed = done.Wait(TimeSpan.FromSeconds(5));
            if (!completed) { RespondJson(ctx, Error("Status check timed out"), 504); return; }
            RespondJson(ctx, result);
        }

        private static void HandleLogs(HttpListenerContext ctx)
        {
            string result = null;
            var done = new ManualResetEventSlim(false);

            RunOnMainThread(() =>
            {
                try { result = Logs(); }
                catch (Exception ex) { result = Error(ex.ToString()); }
                finally { done.Set(); }
            });

            bool completed = done.Wait(TimeSpan.FromSeconds(5));
            if (!completed) { RespondJson(ctx, Error("Logs fetch timed out"), 504); return; }
            RespondJson(ctx, result);
        }

        // ── Route handlers ────────────────────────────────────────────────────────

        private static void HandleExec(HttpListenerContext ctx)
        {
            var body = ReadBody(ctx);
            var code = JsonField(body, "code");
            if (string.IsNullOrEmpty(code)) { RespondJson(ctx, Error("Missing 'code' field"), 400); return; }

            // Block this thread until main thread finishes execution (with timeout)
            string result = null;
            string error  = null;
            var done = new ManualResetEventSlim(false);

            RunOnMainThread(() =>
            {
                try   { result = Evaluator.Execute(code); }
                catch (Exception ex) { error = ex.ToString(); }
                finally { done.Set(); }
            });

            bool completed = done.Wait(TimeSpan.FromSeconds(30));
            if (!completed) { RespondJson(ctx, Error("Execution timed out (30s)"), 504); return; }
            if (error != null) { RespondJson(ctx, Error(error)); return; }

            RespondJson(ctx, $"{{\"success\":true,\"output\":{JsonString(result ?? "")}}}");
        }

        private static void HandleCompile(HttpListenerContext ctx)
        {
            Debug.Log("[FreedomBridgeServer] HandleCompile: received POST request to /compile");
            
            // Write temp .cs file → AssetDatabase.Refresh → domain reload → execute → result file
            var body      = ReadBody(ctx);
            var code      = JsonField(body, "code");
            var method    = JsonField(body, "method") ?? "Execute"; // method name inside user code block
            
            Debug.Log($"[FreedomBridgeServer] HandleCompile: extracted code (length={body.Length}), code field length={(string.IsNullOrEmpty(code) ? 0 : code.Length)}");

            if (string.IsNullOrEmpty(code)) { RespondJson(ctx, Error("Missing 'code' field"), 400); return; }

            string jobId = null;
            string pollPath = null;
            string submitError = null;
            var done = new ManualResetEventSlim(false);

            RunOnMainThread(() =>
            {
                try 
                { 
                    Debug.Log("[FreedomBridgeServer] HandleCompile: calling ScriptExecutor.SubmitJob on main thread...");
                    (jobId, pollPath) = ScriptExecutor.SubmitJob(code); 
                    Debug.Log($"[FreedomBridgeServer] HandleCompile: SubmitJob returned jobId={jobId}, pollPath={pollPath}");
                }
                catch (Exception ex) { 
                    Debug.LogError($"[FreedomBridgeServer] HandleCompile: SubmitJob threw exception: {ex.ToString()}");
                    submitError = ex.ToString(); 
                }
                finally { done.Set(); }
            });

            bool completed = done.Wait(TimeSpan.FromSeconds(10));
            
            if (!completed) 
            {
                Debug.LogWarning("[FreedomBridgeServer] HandleCompile: timeout waiting for main thread submission");
                RespondJson(ctx, Error("Timeout submitting job"), 504); 
                return;
            }

            if (submitError != null) { RespondJson(ctx, Error(submitError)); return; }

            // Return immediately — client must poll /result/{jobId}
            var response = $"{{\"status\":\"compiling\",\"jobId\":{JsonString(jobId)},\"pollPath\":{JsonString(pollPath)},\"pollEndpoint\":\"/result/{jobId}\"}}";
            Debug.Log($"[FreedomBridgeServer] HandleCompile: returning response to client: {response}");
            RespondJson(ctx, response);
        }

        private static void HandleResult(HttpListenerContext ctx, string jobId)
        {
            Debug.Log($"[FreedomBridgeServer] HandleResult: polling for job '{jobId}'...");
            
            // First try ScriptExecutor (for /compile jobs), then CoroutineHost (for /coroutine jobs)
            var result = ScriptExecutor.GetResult(jobId);
            
            if (result == null)
            {
                // Try coroutine host as fallback
                result = CoroutineHost.GetResult(jobId);
            }
            
            if (result == null)
            {
                Debug.LogWarning($"[FreedomBridgeServer] HandleResult: job '{jobId}' still pending - no result yet");
                RespondJson(ctx, $"{{\"status\":\"pending\",\"jobId\":{JsonString(jobId)}}}");
            }
            else
            {
                Debug.Log($"[FreedomBridgeServer] HandleResult: job '{jobId}' completed with result length={result.Length} bytes");
                RespondJson(ctx, result);
            }
        }

        private static void HandleRefresh(HttpListenerContext ctx)
        {
            // Clear logs and error count before triggering refresh
            // This ensures we only capture errors from this compilation cycle
            _errorCount = 0;
            lock (_logsLock)
            {
                _compilationLogs.Clear();
                _seenMessages.Clear();
            }

            // Force AssetDatabase refresh and wait for it to complete (best-effort)
            string error = null;
            var done = new ManualResetEventSlim(false);

            RunOnMainThread(() =>
            {
                try
                {
                    AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
                }
                catch (Exception ex) { error = ex.ToString(); }
                finally { done.Set(); }
            });

            done.Wait(TimeSpan.FromSeconds(5));

            if (error != null) { RespondJson(ctx, Error(error)); return; }
            RespondJson(ctx, "{\"success\":true,\"message\":\"Refresh queued. Domain reload may follow if scripts changed.\"}");
        }

        // ── Coroutine endpoint handler ───────────────────────────────────────────────

        private static void HandleCoroutine(HttpListenerContext ctx)
        {
            var body = ReadBody(ctx);
            var code = JsonField(body, "code");
            
            if (string.IsNullOrEmpty(code)) 
            { 
                RespondJson(ctx, Error("Missing 'code' field"), 400); 
                return; 
            }

            // Parse optional timeout parameter
            int timeoutSeconds = 60;
            try
            {
                var timeoutStr = JsonField(body, "timeoutSeconds");
                if (!string.IsNullOrEmpty(timeoutStr))
                    timeoutSeconds = int.Parse(timeoutStr);
            }
            catch { /* use default */ }

            // Check if async flag is set
            bool isAsyncAwaitable = false;
            try
            {
                var asyncFlag = JsonField(body, "isAsyncAwaitable");
                isAsyncAwaitable = !string.IsNullOrEmpty(asyncFlag) && (asyncFlag.ToLower() == "true" || asyncFlag == "1");
            }
            catch { /* use default */ }

            string jobId = null;
            string pollPath = null;
            string error = null;
            var done = new ManualResetEventSlim(false);

            RunOnMainThread(() =>
            {
                try
                {
                    // Use Evaluator to compile the coroutine code and get an IEnumerator or Task
                    object compiledCoroutine = Evaluator.ExecuteCoroutineCode(code, isAsyncAwaitable);
                    
                    if (compiledCoroutine is System.Collections.IEnumerator ie)
                    {
                        (jobId, pollPath) = CoroutineHost.StartCoroutineFromEnumerator(ie, timeoutSeconds);
                    }
                    else if (compiledCoroutine is System.Threading.Tasks.Task task)
                    {
                        (jobId, pollPath) = CoroutineHost.StartCoroutineFromTask(task, timeoutSeconds);
                    }
                    else
                    {
                        error = $"Evaluator returned unexpected type: {compiledCoroutine?.GetType().Name ?? "null"}";
                    }
                }
                catch (Exception ex) 
                { 
                    error = ex.ToString(); 
                }
                finally { done.Set(); }
            });

            bool completed = done.Wait(TimeSpan.FromSeconds(10));
            
            if (!completed) 
            {
                RespondJson(ctx, Error("Timeout starting coroutine"), 504); 
                return;
            }

            if (error != null) { RespondJson(ctx, Error(error)); return; }

            // Return immediately — client must poll /result/{jobId}
            var response = $"{{\"status\":\"started\",\"jobId\":{JsonString(jobId)},\"pollPath\":{JsonString(pollPath)}}}";
            Debug.Log($"[FreedomBridgeServer] HandleCoroutine: started job {jobId}, polling at {pollPath}");
            RespondJson(ctx, response);
        }

        // ── Simple response helpers ───────────────────────────────────────────────

        private static string Status()
        {
            bool isPlaying = false;
            float playTime = 0f;
            string projectName = "Unknown";
            bool isImporting = false;
            
            try { isPlaying = Application.isPlaying; } catch { }
            try { projectName = Application.productName; } catch { }
            try { isImporting = EditorApplication.isUpdating; } catch { }
            
            if (isPlaying)
            {
                try { playTime = Time.realtimeSinceStartup; } catch { }
            }

            string lastReload = _lastDomainReloadTime.ToString("o");
            int errorCount = GetErrorCount();

            return $"{{\"status\":\"ok\",\"version\":\"1.0\",\"port\":{PORT}," +
                   $"\"isPlaying\":{isPlaying.ToString().ToLower()}," +
                   $"\"playTimeSeconds\":{playTime}," +
                   $"\"projectName\":{JsonString(projectName)}," +
                   $"\"lastDomainReload\":\"{lastReload}\"," +
                   $"\"isImporting\":{isImporting.ToString().ToLower()}," +
                   $"\"errorCount\":{errorCount}}}";
        }

        private static string Logs()
        {
            // Return our captured logs (compile errors + runtime logs)
            var logs = new System.Collections.Generic.List<string>();
            lock (_logsLock)
            {
                logs.AddRange(_compilationLogs);
            }

            var sb = new StringBuilder("{\"logs\":[");
            bool first = true;
            foreach (var log in logs)
            {
                if (!first) sb.Append(",");
                sb.Append(JsonString(log));
                first = false;
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private static string Error(string msg, bool fatal = false) =>
            $"{{\"success\":false,\"error\":{JsonString(msg)}}}";

        private static void RespondJson(HttpListenerContext ctx, string json, int statusCode = 200)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            ctx.Response.StatusCode = statusCode;
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.OutputStream.Close();
        }

        // ── Tiny JSON helpers (no external dep) ──────────────────────────────────

        private static string ReadBody(HttpListenerContext ctx)
        {
            using var sr = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
            return sr.ReadToEnd();
        }

        /// <summary>Extract a top-level string value from a hand-crafted JSON payload.</summary>
        public static string JsonField(string json, string key)
        {
            // Handles: {"key":"value"} and {"key": "multi\nline"}
            // Not a full parser — sufficient for our controlled protocol.
            var search = $"\"{key}\"";
            int idx = json.IndexOf(search, StringComparison.Ordinal);
            if (idx < 0) return null;
            idx += search.Length;
            while (idx < json.Length && (json[idx] == ' ' || json[idx] == ':')) idx++;
            if (idx >= json.Length || json[idx] != '"') return null;
            idx++; // skip opening quote
            var sb = new StringBuilder();
            while (idx < json.Length && json[idx] != '"')
            {
                if (json[idx] == '\\' && idx + 1 < json.Length)
                {
                    idx++;
                    sb.Append(json[idx] switch { 'n' => '\n', 'r' => '\r', 't' => '\t', '"' => '"', '\\' => '\\', _ => json[idx] });
                }
                else sb.Append(json[idx]);
                idx++;
            }
            return sb.ToString();
        }

        public static string JsonString(string s)
        {
            if (s == null) return "null";
            return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"")
                           .Replace("\n", "\\n").Replace("\r", "\\r")
                           .Replace("\t", "\\t") + "\"";
        }
    }
}
