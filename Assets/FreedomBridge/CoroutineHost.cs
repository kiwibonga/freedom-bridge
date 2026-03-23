// CoroutineHost.cs
// Hosts coroutines spawned via the /coroutine endpoint without requiring domain reload.
// Persists across frames using a hidden GameObject singleton pattern.
// NOTE: This file must be OUTSIDE the Editor folder to allow AddComponent at runtime.

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using UnityEngine;

namespace FreedomBridge
{
    /// <summary>
    /// Tracks state of an active coroutine job including its log callback for cleanup.
    /// </summary>
    public class CoroutineJobState
    {
        public string JobId { get; set; }
        public bool IsComplete { get; set; } = false;
        public bool Succeeded { get; set; } = true;
        public string Output { get; set; } = "";
        public string Error { get; set; } = "";
        public float StartTime { get; set; }
        public int TimeoutSeconds { get; set; } = 60;
        
        private readonly System.Collections.Generic.List<string> _logs = new();
        public Application.LogCallback LogCapture { get; internal set; }
        
        public string[] GetLogs() => _logs.ToArray();
        public void AddLog(string log) => _logs.Add(log);
    }

    /// <summary>
    /// MonoBehaviour that hosts coroutines for the Freedom Bridge.
    /// Created once at startup and persists across frames (but not domain reloads).
    /// </summary>
    public class CoroutineHost : MonoBehaviour
    {
        private static CoroutineHost _instance;
        
        public static CoroutineHost Instance
        {
            get
            {
                if (_instance == null)
                {
                    if (Application.isPlaying)
                    {
                        // Create hidden GameObject to host this MonoBehaviour
                        var go = new GameObject("[FreedomBridge] CoroutineHost");
                        // Only use DontDestroyOnLoad when actually in play mode
                        // (it throws an exception when called from editor scripts outside play mode)

                            DontDestroyOnLoad(go); // Persist during scene changes in play mode
                        _instance = go.AddComponent<CoroutineHost>();
                        
                        Debug.Log("[FreedomBridge] CoroutineHost created on-demand");
                    }
                }

                return _instance;
            }
        }

        // Active jobs that are still running
        private static readonly ConcurrentDictionary<string, CoroutineJobState> _activeJobs = new();
        
        // Completed job results with timestamps - kept for a short time to allow polling clients to retrieve them
        private static readonly ConcurrentDictionary<string, (string result, float timestamp)> _completedResults = new();
        
        // Time (in seconds) to keep completed results available for retrieval
        private const float COMPLETED_RESULT_TTL_SECONDS = 30f;

        private void OnDestroy()
        {
            if (_instance == this) _instance = null;
            // Note: Jobs remain in dictionaries even after destruction - they'll be cleaned up on completion/timeout/TTL expiry
        }

        /// <summary>
        /// Start a coroutine from an already-compiled IEnumerator or Task.
        /// Returns jobId for polling completion via /result/{jobId}.
        /// </summary>
        public static (string jobId, string pollPath) StartCoroutineFromEnumerator(
            System.Collections.IEnumerator enumerator, 
            int timeoutSeconds = 60)
        {
            var host = Instance;
            if (host == null)
                throw new Exception("No coroutine host");

            // Automatically start play mode if not already playing (Editor-only functionality via reflection)
            if (!Application.isPlaying && !string.IsNullOrEmpty(Application.dataPath))
            {
                try
                {
                    // Use reflection to access UnityEditor.EditorApplication from runtime assembly
                    var assembly = System.AppDomain.CurrentDomain.GetAssemblies()
                        .FirstOrDefault(a => a.GetName().Name == "UnityEditor");
                    
                    if (assembly != null)
                    {
                        var editorAppType = assembly.GetType("UnityEditor.EditorApplication");
                        if (editorAppType != null)
                        {
                            var isPlayingProp = editorAppType.GetProperty("isPlaying");
                            if (isPlayingProp?.GetValue(null) is bool isPlaying && !isPlaying)
                            {
                                Debug.Log("[CoroutineHost] Starting Play Mode automatically for coroutine execution...");
                                isPlayingProp.SetValue(null, true);
                                // Give Unity a moment to transition into play mode
                                System.Threading.Thread.Sleep(100);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[CoroutineHost] Could not auto-start Play Mode: {ex.Message}");
                }
            }

            // Verify we're now in play mode
            if (!Application.isPlaying)
            {
                throw new InvalidOperationException(
                    "Coroutines cannot be started outside of Play Mode. Please enter Play Mode first.");
            }
            
            // Generate unique job ID
            string jobId = System.Guid.NewGuid().ToString("N");
            float startTime = Time.realtimeSinceStartup;

            // Create job state with log capture callback
            var jobState = new CoroutineJobState
            {
                JobId = jobId,
                StartTime = startTime,
                TimeoutSeconds = timeoutSeconds
            };

            _activeJobs[jobId] = jobState;

            // Set up log capture for this coroutine
            Application.LogCallback logCapture = (msg, _, type) =>
            {
                if (!jobState.IsComplete)
                {
                    var prefix = type == LogType.Error || type == LogType.Exception ? "[ERROR]"
                               : type == LogType.Warning ? "[WARN]"
                               : "[LOG]";
                    jobState.AddLog($"{prefix} {msg}");
                }
            };
            jobState.LogCapture = logCapture;
            Application.logMessageReceived += logCapture;

            try
            {
                host.StartCoroutineWithCompletion(enumerator, jobState);
                // Schedule timeout check coroutine
                host.StartCoroutine(host.CheckTimeoutCoroutine(jobState));
            }
            catch (Exception ex)
            {
                jobState.IsComplete = true;
                jobState.Succeeded = false;
                jobState.Error = ex.ToString();
                Application.logMessageReceived -= logCapture;
            }

            return (jobId, $"/result/{jobId}");
        }

        /// <summary>
        /// Start a coroutine from an already-compiled Task.
        /// Returns jobId for polling completion via /result/{jobId}.
        /// </summary>
        public static (string jobId, string pollPath) StartCoroutineFromTask(
            System.Threading.Tasks.Task task, 
            int timeoutSeconds = 60)
        {
            var host = Instance;
            if (host == null)
                throw new Exception("No coroutine host");

            // Automatically start play mode if not already playing (Editor-only functionality via reflection)
            if (!Application.isPlaying && !string.IsNullOrEmpty(Application.dataPath))
            {
                try
                {
                    // Use reflection to access UnityEditor.EditorApplication from runtime assembly
                    var assembly = System.AppDomain.CurrentDomain.GetAssemblies()
                        .FirstOrDefault(a => a.GetName().Name == "UnityEditor");
                    
                    if (assembly != null)
                    {
                        var editorAppType = assembly.GetType("UnityEditor.EditorApplication");
                        if (editorAppType != null)
                        {
                            var isPlayingProp = editorAppType.GetProperty("isPlaying");
                            if (isPlayingProp?.GetValue(null) is bool isPlaying && !isPlaying)
                            {
                                Debug.Log("[CoroutineHost] Starting Play Mode automatically for coroutine execution...");
                                isPlayingProp.SetValue(null, true);
                                // Give Unity a moment to transition into play mode
                                System.Threading.Thread.Sleep(100);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[CoroutineHost] Could not auto-start Play Mode: {ex.Message}");
                }
            }

            // Verify we're now in play mode
            if (!Application.isPlaying)
            {
                throw new InvalidOperationException(
                    "Coroutines cannot be started outside of Play Mode. Please enter Play Mode first.");
            }
            
            // Generate unique job ID
            string jobId = System.Guid.NewGuid().ToString("N");
            float startTime = Time.realtimeSinceStartup;

            // Create job state with log capture callback
            var jobState = new CoroutineJobState
            {
                JobId = jobId,
                StartTime = startTime,
                TimeoutSeconds = timeoutSeconds
            };

            _activeJobs[jobId] = jobState;

            // Set up log capture for this coroutine
            Application.LogCallback logCapture = (msg, _, type) =>
            {
                if (!jobState.IsComplete)
                {
                    var prefix = type == LogType.Error || type == LogType.Exception ? "[ERROR]"
                               : type == LogType.Warning ? "[WARN]"
                               : "[LOG]";
                    jobState.AddLog($"{prefix} {msg}");
                }
            };
            jobState.LogCapture = logCapture;
            Application.logMessageReceived += logCapture;

            try
            {
                IEnumerator Awaiter()
                {
                    while (!task.IsCompleted && !jobState.IsComplete)
                    {
                        CheckTimeout(jobState);
                        yield return null;
                    }
                    
                    if (task.IsFaulted)
                    {
                        jobState.Succeeded = false;
                        jobState.Error = task.Exception?.GetBaseException().Message ?? "Unknown error";
                    }
                    else if (task.GetType().GetProperty("Result") != null)
                    {
                        var res = task.GetType().GetProperty("Result").GetValue(task);
                        if (res != null)
                        {
                            jobState.Output = Convert.ToString(res);
                        }
                    }
                    jobState.IsComplete = true;
                }
                host.StartCoroutine(Awaiter());
            }
            catch (Exception ex)
            {
                jobState.IsComplete = true;
                jobState.Succeeded = false;
                jobState.Error = ex.ToString();
                Application.logMessageReceived -= logCapture;
            }

            return (jobId, $"/result/{jobId}");
        }

        /// <summary>
        /// Wraps an IEnumerator to detect completion and capture the final value.
        /// </summary>
        private void StartCoroutineWithCompletion(System.Collections.IEnumerator enumerator, CoroutineJobState jobState)
        {
            StartCoroutine(WrapEnumerator(enumerator, jobState));
        }

        private System.Collections.IEnumerator WrapEnumerator(System.Collections.IEnumerator inner, CoroutineJobState jobState)
        {
            bool completedSuccessfully = false;
            
            // Main loop - cannot use try/catch with yield inside, so we wrap MoveNext separately
            while (!jobState.IsComplete)
            {
                CheckTimeout(jobState);
                
                // Try to move next and catch any exceptions from the user's coroutine code
                bool hasMore;
                try
                {
                    hasMore = inner.MoveNext();
                }
                catch (Exception ex)
                {
                    jobState.IsComplete = true;
                    jobState.Succeeded = false;
                    jobState.Error = ex.ToString();
                    break;
                }
                
                if (!hasMore)
                {
                    // Enumerator completed normally
                    completedSuccessfully = true;
                    break;
                }
                
                // Get the yielded value and actually yield on it for Unity's coroutine system
                var current = inner.Current;
                if (current != null)
                {
                    jobState.Output = current.ToString();
                    
                    // Yield on the actual value so WaitForSeconds, WaitUntil, etc. work properly
                    yield return current;
                }
                else
                {
                    // No yield value - just continue to next iteration but still pause a frame
                    yield return null;
                }
            }
            
            // Set completion status after loop exits
            if (!jobState.IsComplete && completedSuccessfully)
            {
                jobState.Succeeded = true;
                jobState.IsComplete = true;
            }
            
            CleanupJob(jobState.JobId);
            yield break;
        }

        private System.Collections.IEnumerator CheckTimeoutCoroutine(CoroutineJobState jobState)
        {
            while (!jobState.IsComplete)
            {
                yield return null;
                
                float elapsed = Time.realtimeSinceStartup - jobState.StartTime;
                if (elapsed > jobState.TimeoutSeconds)
                {
                    jobState.IsComplete = true;
                    jobState.Succeeded = false;
                    jobState.Error = $"Coroutine timed out after {jobState.TimeoutSeconds} seconds.";
                    CleanupJob(jobState.JobId);
                    break;
                }
            }
        }

        private static void CheckTimeout(CoroutineJobState jobState)
        {
            float elapsed = Time.realtimeSinceStartup - jobState.StartTime;
            if (elapsed > jobState.TimeoutSeconds && !jobState.IsComplete)
            {
                jobState.IsComplete = true;
                jobState.Succeeded = false;
                jobState.Error = $"Coroutine timed out after {jobState.TimeoutSeconds} seconds.";
            }
        }

        /// <summary>
        /// Get the result of a coroutine job. Returns null if still pending.
        /// </summary>
        public static string GetResult(string jobId)
        {
            // First check active jobs
            if (_activeJobs.TryGetValue(jobId, out var jobState))
            {
                if (!jobState.IsComplete)
                {
                    return $"{{\"status\":\"pending\",\"jobId\":{JsonString(jobId)}}}";
                }

                // Job is complete - build and cache the response with timestamp before cleanup
                string result = BuildJobResponse(jobState);
                
                // Store completed result for later retrieval after cleanup (CleanupJob will also do this)
                _completedResults[jobId] = (result, Time.realtimeSinceStartup);
                
                return result;
            }

            // Check cached completed results (for quick coroutines that finished before first poll)
            if (_completedResults.TryGetValue(jobId, out var cachedEntry))
            {
                return cachedEntry.result;
            }

            return "{\"success\":false,\"error\":\"Job not found\"}";
        }

        private static string BuildJobResponse(CoroutineJobState jobState)
        {
            // Build response with logs
            var sb = new StringBuilder();
            if (jobState.Succeeded)
            {
                sb.Append("{\"success\":true,\"output\":\"").Append(EscapeJson(jobState.Output)).Append("\",\"logs\":[");
            }
            else
            {
                sb.Append("{\"success\":false,\"error\":\"").Append(EscapeJson(jobState.Error)).Append("\",\"logs\":[");
            }

            bool firstLog = true;
            foreach (var log in jobState.GetLogs())
            {
                if (!firstLog) sb.Append(",");
                sb.Append(JsonString(log));
                firstLog = false;
            }
            
            sb.Append("]}");
            return sb.ToString();
        }

        private static void CleanupJob(string jobId)
        {
            // Remove from active jobs and clean up the log callback
            if (_activeJobs.TryRemove(jobId, out var jobState))
            {
                Application.logMessageReceived -= jobState.LogCapture;
                
                // Store completed result for later retrieval (in case coroutine finished before first poll)
                string result = BuildJobResponse(jobState);
                _completedResults[jobId] = (result, Time.realtimeSinceStartup);
            }
            
            // Note: We no longer remove from _completedResults here - they'll expire via TTL cleanup in Update()
        }

        private void Update()
        {
            // Clean up expired completed results (TTL-based garbage collection)
            float currentTime = Time.realtimeSinceStartup;
            
            foreach (var kvp in _completedResults.ToList())
            {
                var (result, timestamp) = kvp.Value;
                if (currentTime - timestamp > COMPLETED_RESULT_TTL_SECONDS)
                {
                    _completedResults.TryRemove(kvp.Key, out _);
                }
            }
        }

        // JSON helper methods for string escaping
        private static string JsonString(string s) => $"\"{EscapeJson(s)}\"";
        
        private static string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            
            System.Text.StringBuilder sb = new StringBuilder(s.Length + 10);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"':  sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if ((int)c < 32)
                            sb.AppendFormat("\\u{0:x4}", (int)c);
                        else
                            sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}