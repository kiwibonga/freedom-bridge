// ScriptExecutor.cs
// Handles the compile-cycle execution path:
//   1. Wraps user code in a temp [InitializeOnLoad] class
//   2. Writes it to Assets/Editor/FreedomBridge/_Temp/
//   3. Triggers AssetDatabase.Refresh() → Unity compiles → domain reload
//   4. After reload, [InitializeOnLoad] fires, temp class executes user code
//   5. Result written to a temp JSON file, temp script deleted
//   6. Client polls /result/{jobId} to get the result

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace FreedomBridge
{
    [InitializeOnLoad]
    public static class ScriptExecutor
    {
        private const string TEMP_DIR           = "Assets/FreedomBridge/Editor/_Temp";
        private const string PENDING_JOB_KEY    = "FreedomBridge_PendingJob";

        // In-memory result cache (lives until next domain reload)
        private static readonly Dictionary<string, string> _results = new Dictionary<string, string>();

        // Thread-safe lazy initialization of result directory
        private static readonly object _resultDirLock = new object();
        private static string _cachedResultDir;

        /// <summary>Gets the result directory path, creating it if needed. Thread-safe.</summary>
        private static string ResultDir
        {
            get
            {
                if (_cachedResultDir != null) return _cachedResultDir;

                lock (_resultDirLock)
                {
                    if (_cachedResultDir == null)
                    {
                        _cachedResultDir = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                            "Unity", "FreedomBridge");
                        Directory.CreateDirectory(_cachedResultDir);
                    }
                }
                return _cachedResultDir;
            }
        }

        // ── Called by FreedomBridgeServer [InitializeOnLoad] on every reload ───────

        /// <summary>
        /// Checks if a compile-cycle job was pending before the domain reload.
        /// If so, executes the job code (the temp script already ran its static ctor,
        /// but this handles cleanup and notifies result storage).
        /// Actually, the temp script handles its own execution; this method just
        /// ensures the result file path is registered for the polling endpoint.
        /// </summary>
        public static void ResumePendingJob()
        {
            Debug.Log("[FreedomBridge] ResumePendingJob called after domain reload");
            
            string pending = SessionState.GetString(PENDING_JOB_KEY, "");
            Debug.Log($"[FreedomBridge] Pending job value: '{pending}'");
            
            if (string.IsNullOrEmpty(pending)) 
            {
                Debug.LogWarning("[FreedomBridge] No pending job found - nothing to resume");
                
                // Schedule cleanup of temp dir (in case the temp script didn't clean up)
                EditorApplication.delayCall += CleanupStaleTemp;
                return;
            }

            // The temp script's static constructor has registered for OnUpdate.
            // We should NOT clear the pending flag here because the temp script needs it
            // on its first tick to verify this is still its job before executing.
            // The temp script will consume the pending flag itself when ready to execute.
            
            var parts = pending.Split('|');
            if (parts.Length >= 2)
            {
                string jobId     = parts[0];
                string resultPath = parts[1];
                
                Debug.Log($"[FreedomBridge] Job '{jobId}' detected - waiting for temp script to execute and write result to: {resultPath}");

                // Try to read the result file in case it was already written by a previous run
                LoadResultFromFile(jobId, resultPath);
            }
            else
            {
                Debug.LogError($"[FreedomBridge] Invalid pending format: '{pending}' - expected 'jobId|resultPath'");
            }

            // Schedule cleanup of temp dir (in case the temp script didn't clean up)
            EditorApplication.delayCall += CleanupStaleTemp;
        }

        // ── Submit a new job ──────────────────────────────────────────────────────

        /// <summary>
        /// Writes a temp script that wraps the user's code, registers the job,
        /// and triggers AssetDatabase.Refresh() to start compilation.
        /// Returns (jobId, resultFilePath) — client polls /result/{jobId}.
        /// MUST be called on the main thread.
        /// </summary>
        public static (string jobId, string resultPath) SubmitJob(string userCode)
        {
            Debug.Log("[FreedomBridge] SubmitJob called with code length: " + userCode.Length);
            
            EnsureTempDir();

            var jobId       = Guid.NewGuid().ToString("N").Substring(0, 12);
            var className   = $"AgentJob_{jobId}";
            var tempScript  = $"{TEMP_DIR}/{className}.cs";
            var resultPath  = Path.Combine(ResultDir, $"{jobId}.json");

            Debug.Log($"[FreedomBridge] Job {jobId}: preparing to write temp script to {tempScript}");
            
            // Build the temp script from template
            string script = BuildScript(className, jobId, resultPath, userCode);
            
            Debug.Log($"[FreedomBridge] Job {jobId}: generated script length: {script.Length} chars");
            
            // Write the .cs file directly to disk and flush to ensure it's on disk before reload
            try
            {
                using (var writer = new StreamWriter(tempScript, false, Encoding.UTF8))
                {
                    writer.Write(script);
                    writer.Flush();
                    writer.Close();
                }
                
                // Verify file was written successfully
                if (!File.Exists(tempScript))
                {
                    throw new Exception("Temp script file does not exist after write!");
                }
                
                Debug.Log($"[FreedomBridge] Job {jobId}: temp script written successfully, size: {new FileInfo(tempScript).Length} bytes");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[FreedomBridge] Job {jobId}: FAILED to write temp script: {ex.Message}");
                throw;
            }

            System.GC.Collect();
            System.GC.WaitForPendingFinalizers();
            System.Threading.Thread.Sleep(100); // Give OS time to write to disk
            
            Debug.Log("[FreedomBridge] Job " + jobId + ": saving assets and forcing AssetDatabase.Refresh()...");
            
            // Save any pending changes first
            AssetDatabase.SaveAssets();
            
            // CRITICAL: Refresh the asset database so Unity detects the new file BEFORE requesting reload
            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate | ImportAssetOptions.ImportRecursive);
            
            Debug.Log("[FreedomBridge] Job " + jobId + ": AssetDatabase.Refresh() complete, checking if temp script exists in DB...");
            
            // Verify Unity can see our temp script now
            var guids = AssetDatabase.FindAssets($"t:script {className}", new[] { TEMP_DIR });
            if (guids.Length > 0)
            {
                string foundPath = AssetDatabase.GUIDToAssetPath(guids[0]);
                Debug.Log($"[FreedomBridge] Job {jobId}: SUCCESS - AssetDatabase found temp script at: {foundPath}");
            }
            else
            {
                Debug.LogError("[FreedomBridge] Job " + jobId + ": WARNING - AssetDatabase did NOT find the temp script! This may cause execution to fail.");
                
                // List what's in the temp dir for debugging
                var allScripts = AssetDatabase.FindAssets("t:script", new[] { TEMP_DIR });
                if (allScripts.Length > 0)
                {
                    Debug.LogWarning("[FreedomBridge] Temp directory contains these scripts:");
                    foreach (var g in allScripts)
                    {
                        Debug.LogWarning($"[FreedomBridge]   - {AssetDatabase.GUIDToAssetPath(g)}");
                    }
                }
            }

            // Store pending info across domain reload (before trigger)
            SessionState.SetString(PENDING_JOB_KEY, $"{jobId}|{resultPath}");
            
            Debug.Log($"[FreedomBridge] Job {jobId}: stored pending job flag, forcing asset import and compilation...");

            // Import the temp script with force update to trigger recompilation
            var guid = AssetDatabase.AssetPathToGUID(tempScript);
            if (!string.IsNullOrEmpty(guid))
            {
                Debug.Log($"[FreedomBridge] Job {jobId}: importing asset with GUID {guid} using ForceUpdate...");
                AssetDatabase.ImportAsset(guid, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
                
                // Also request a full script reload as backup
                EditorUtility.RequestScriptReload();
                
                Debug.Log("[FreedomBridge] Job " + jobId + ": ImportAsset complete - compilation should start now");
            }
            else
            {
                Debug.LogError($"[FreedomBridge] Job {jobId}: FAILED to get GUID for temp script at {tempScript}!");
                Debug.LogWarning("[FreedomBridge] Falling back to RequestScriptReload() only...");
                EditorUtility.RequestScriptReload();
            }

            return (jobId, resultPath);
        }

        // ── Poll for result ───────────────────────────────────────────────────────

        /// <summary>Returns JSON result string, or null if still pending.</summary>
        public static string GetResult(string jobId)
        {
            if (_results.TryGetValue(jobId, out var cached))
                return cached;

            // Try the result file (written by the temp script after domain reload)
            var resultPath  = Path.Combine(ResultDir, $"{jobId}.json");
            return LoadResultFromFile(jobId, resultPath);
        }

        private static string LoadResultFromFile(string jobId, string resultPath)
        {
            if (!File.Exists(resultPath)) return null;
            try
            {
                var json = File.ReadAllText(resultPath);
                _results[jobId] = json;
                return json;
            }
            catch { return null; }
        }

        // ── Script template ───────────────────────────────────────────────────────

        private static string BuildScript(string className, string jobId, string resultPath, string userCode)
        {
            // Escape resultPath for C# verbatim string literal (only backslashes need doubling)
            string escapedResultPath = resultPath.Replace("\\", "\\\\");

            return $@"// AUTO-GENERATED by FreedomBridge — DO NOT EDIT
// Job: {jobId}
// This file will delete itself after execution.
using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace FreedomBridge.Temp
{{
    [InitializeOnLoad]
    internal static class {className}
    {{
        private static bool _hasExecuted = false;
        private static int _updateCount = 0;

        // Static constructor only registers the update hook — no editor API calls here.
        // Actual execution happens on EditorApplication.update ticks, after the
        // domain reload is fully settled and all editor systems are ready.
        static {className}()
        {{
            Debug.Log($""[FreedomBridge] TEMP SCRIPT STATIC CTOR: Job class {className} loaded!"");
            
            string pending = SessionState.GetString(""FreedomBridge_PendingJob"", """");
            Debug.Log($""[FreedomBridge] TEMP SCRIPT STATIC CTOR: Pending value = '{{pending}}', expected to start with '{jobId}'"");
            
            if (!pending.StartsWith(""{jobId}"")) 
            {{
                Debug.LogWarning($""[FreedomBridge] TEMP SCRIPT STATIC CTOR: Pending job mismatch - not registering for execution (expected prefix '{jobId}', got '{{pending}}')"");
                return;
            }}
            
            Debug.Log($""[FreedomBridge] TEMP SCRIPT STATIC CTOR: Registering OnUpdate handler for Job {jobId}..."");
            EditorApplication.update += OnUpdate;
        }}

        private static void OnUpdate()
        {{
            _updateCount++;
            
            Debug.Log($""[FreedomBridge] ONUPDATE TICK {{_updateCount}}: Job {jobId} update callback invoked"");

            // First tick: just verify we're still supposed to run
            if (_updateCount == 1)
            {{
                string pending = SessionState.GetString(""FreedomBridge_PendingJob"", """");
                Debug.Log($""[FreedomBridge] ONUPDATE TICK 1: Checking pending value '{{pending}}' for job '{jobId}'"");
                
                if (!pending.StartsWith(""{jobId}""))
                {{
                    // Not our job, unregister and exit
                    Debug.LogWarning($""[FreedomBridge] ONUPDATE TICK 1: Pending mismatch - unregistering (expected prefix '{jobId}', got '{{pending}}')"");
                    EditorApplication.update -= OnUpdate;
                    return;
                }}
                // Consume the pending flag now so other jobs don't interfere
                SessionState.SetString(""FreedomBridge_PendingJob"", """");
                Debug.Log($""[FreedomBridge] Job {jobId} starting execution on tick {{(_updateCount + 1)}}..."");
                // Will execute on next tick to ensure editor is fully initialized
                return;
            }}

            // Second tick: actually run the user code
            if (_updateCount == 2)
            {{
                if (_hasExecuted)
                {{
                    EditorApplication.update -= OnUpdate;
                    return;
                }}
                _hasExecuted = true;

                string resultPath = @""{escapedResultPath}"";
                var logs = new System.Collections.Generic.List<string>();

                Application.LogCallback capture = (msg, stack, type) =>
                    logs.Add($""[{{type}}] {{msg}}"");
                Application.logMessageReceived += capture;

                bool success = false;
                string errorText = null;

                try
                {{
                    Execute();
                    success = true;
                }}
                catch (Exception ex)
                {{
                    errorText = ex.ToString();
                    Debug.LogError($""[FreedomBridge] Job {jobId} failed: {{ex.Message}}"");
                }}
                finally
                {{
                    Application.logMessageReceived -= capture;
                }}

                // Write result file synchronously and flush
                try
                {{
                    Directory.CreateDirectory(Path.GetDirectoryName(resultPath));
                    var sb = new StringBuilder();
                    if (success)
                    {{
                        sb.Append(""{{\""success\"":true,"");
                    }}
                    else
                    {{
                        sb.Append(""{{\""success\"":false,\""error\"":"");
                        sb.Append(EscapeJson(errorText));
                        sb.Append("","");
                    }}
                    sb.Append(""\""logs\"":["");
                    for (int i = 0; i < logs.Count; i++)
                    {{
                        if (i > 0) sb.Append(',');
                        sb.Append(EscapeJson(logs[i]));
                    }}
                    sb.Append(""]}}"");

                    using (var writer = new StreamWriter(resultPath, false, Encoding.UTF8))
                    {{
                        writer.Write(sb.ToString());
                        writer.Flush();
                        writer.Close();
                    }}
                    Debug.Log($""[FreedomBridge] Job {jobId} result written to {resultPath}"");
                }}
                catch (Exception writeEx)
                {{
                    Debug.LogError($""[FreedomBridge] Could not write result: {{writeEx.Message}}"");
                }}

                // Third tick will handle cleanup - unregister from update for now
                EditorApplication.update -= OnUpdate;
                // Re-register for one more tick to do cleanup
                EditorApplication.delayCall += ScheduleCleanup;
                return;
            }}
        }}

        private static void ScheduleCleanup()
        {{
            string tempScript = $""Assets/Editor/FreedomBridge/_Temp/{className}.cs"";
            if (File.Exists(tempScript))
            {{
                Debug.Log($""[FreedomBridge] Job {jobId} cleaning up temp script: {{tempScript}}"");
                AssetDatabase.DeleteAsset(tempScript);
            }}
        }}

        private static string EscapeJson(string s)
        {{
            if (s == null) return ""null"";
            var sb = new StringBuilder();
            sb.Append('""');
            foreach (char c in s)
            {{
                switch (c)
                {{
                    case '\\': sb.Append(@""\\""); break;
                    case '""':  sb.Append(@""\""""""); break;
                    case '\n': sb.Append(@""\n""); break;
                    case '\r': sb.Append(@""\r""); break;
                    case '\t': sb.Append(@""\t""); break;
                    default:   sb.Append(c); break;
                }}
            }}
            sb.Append('""');
            return sb.ToString();
        }}

        // ──────────────────────────────────────────────────────────────────
        // USER CODE (injected by agent)
        // ──────────────────────────────────────────────────────────────────
        private static void Execute()
        {{
{IndentCode(userCode, 12)}
        }}
    }}
}}
";
        }

        private static string IndentCode(string code, int spaces)
        {
            var pad = new string(' ', spaces);
            var lines = code.Replace("\r\n", "\n").Split('\n');
            return string.Join("\n", System.Linq.Enumerable.Select(lines, l => pad + l));
        }

        // ── Housekeeping ──────────────────────────────────────────────────────────

        private static void EnsureTempDir()
        {
            if (!AssetDatabase.IsValidFolder(TEMP_DIR))
            {
                string parent = "Assets/Editor/FreedomBridge";
                AssetDatabase.CreateFolder(parent, "_Temp");
                // Add .gitignore so temp scripts aren't committed
                File.WriteAllText($"{TEMP_DIR}/.gitignore", "*\n");
                File.WriteAllText($"{TEMP_DIR}/.gitkeep", "");
            }
        }

        private static void CleanupStaleTemp()
        {
            if (!Directory.Exists(TEMP_DIR)) return;
            foreach (var f in Directory.GetFiles(TEMP_DIR, "AgentJob_*.cs"))
            {
                // If no pending session matches this file, it's stale
                string pending = SessionState.GetString(PENDING_JOB_KEY, "");
                string name    = Path.GetFileNameWithoutExtension(f);
                if (!pending.Contains(name.Replace("AgentJob_", "")))
                {
                    try { AssetDatabase.DeleteAsset($"{TEMP_DIR}/{Path.GetFileName(f)}"); }
                    catch { /* ignore */ }
                }
            }
        }
    }
}