// FreedomBridgeWindow.cs — Tools > Freedom Bridge
// Manual test console. Not required for the bridge to function.

using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace FreedomBridge
{
    public class FreedomBridgeWindow : EditorWindow
    {
        private enum ExecutionMode { Exec, Compile, Coroutine }

        // Default code examples for each mode
        private const string ExecExample       = "// Write C# editor code here\nDebug.Log(\"This code made it over the bridge!\");";
        private const string CompileExample    = "// Write C# editor code here (compile cycle)\nDebug.Log(\"Compiled and executed successfully!\");";
        private const string CoroutineExample  = "// Coroutine example - runs over multiple frames\nyield return null;\nDebug.Log(\"Coroutine ran after one frame!\");";

        private string      _code       = ExecExample;
        private string      _output     = "";
        private ExecutionMode _mode     = ExecutionMode.Exec;
        private bool        _running    = false;
        private Vector2     _codeScroll;
        private Vector2     _outScroll;

        [MenuItem("Tools/Freedom Bridge")]
        public static void Open() => GetWindow<FreedomBridgeWindow>("Freedom Bridge").Show();

        private void OnGUI()
        {
            GUILayout.Label("Freedom Bridge", EditorStyles.boldLabel);
            EditorGUILayout.Space(4);
            GUILayout.Label($"Server: http://127.0.0.1:{FreedomBridgeServer.PORT}/");
            EditorGUILayout.Space(4);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Restart Server"))
                { FreedomBridgeServer.StopServer(); FreedomBridgeServer.StartServer(); _output = "Restarted."; }
                if (GUILayout.Button("Clear Output")) _output = "";
            }

            EditorGUILayout.Space(4);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("Execution Mode:", EditorStyles.boldLabel);
                var prevMode = _mode;
                _mode = (ExecutionMode)EditorGUILayout.EnumPopup(_mode);
                
                // Update code example when mode changes
                if (_mode != prevMode)
                {
                    switch (_mode)
                    {
                        case ExecutionMode.Exec:
                            _code = ExecExample;
                            break;
                        case ExecutionMode.Compile:
                            _code = CompileExample;
                            break;
                        case ExecutionMode.Coroutine:
                            _code = CoroutineExample;
                            break;
                    }
                }
            }

            GUILayout.Label("Code:", EditorStyles.boldLabel);
            _codeScroll = EditorGUILayout.BeginScrollView(_codeScroll, GUILayout.Height(200));
            _code = EditorGUILayout.TextArea(_code, GUILayout.ExpandHeight(true));
            EditorGUILayout.EndScrollView();

            EditorGUI.BeginDisabledGroup(_running);
            if (GUILayout.Button(_running ? "Running..." : "Run")) RunCode();
            EditorGUI.EndDisabledGroup();

            GUILayout.Label("Output:", EditorStyles.boldLabel);
            _outScroll = EditorGUILayout.BeginScrollView(_outScroll, GUILayout.Height(150));
            EditorGUILayout.TextArea(_output, GUILayout.ExpandHeight(true));
            EditorGUILayout.EndScrollView();
        }

        private async void RunCode()
        {
            _running = true; _output = "Submitting..."; Repaint();
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
                
                // Determine endpoint based on mode
                string endpoint;
                bool needsPolling = false;
                
                switch (_mode)
                {
                    case ExecutionMode.Exec:
                        endpoint = "exec";
                        break;
                    case ExecutionMode.Compile:
                        endpoint = "compile";
                        needsPolling = true;
                        break;
                    case ExecutionMode.Coroutine:
                        endpoint = "coroutine";
                        needsPolling = true;
                        break;
                    default:
                        throw new Exception("Unknown execution mode");
                }

                var payload = $"{{\"code\":{FreedomBridgeServer.JsonString(_code)}}}";
                var content = new StringContent(payload, Encoding.UTF8, "application/json");
                var body = await (await http.PostAsync($"http://127.0.0.1:{FreedomBridgeServer.PORT}/{endpoint}", content))
                                   .Content.ReadAsStringAsync();

                // For non-polling modes (Exec), return immediately
                if (!needsPolling) { _output = body; return; }

                // Extract jobId for polling endpoints (Compile and Coroutine)
                var jobId = FreedomBridgeServer.JsonField(body, "jobId");
                if (jobId == null) { _output = "Error: " + body; return; }

                // Poll for results
                string pollPrefix = _mode == ExecutionMode.Compile ? "Compiling" : "Coroutine";
                
                for (int i = 0; i < 120; i++)
                {
                    await Task.Delay(1000);
                    var r = await (await http.GetAsync($"http://127.0.0.1:{FreedomBridgeServer.PORT}/result/{jobId}"))
                                   .Content.ReadAsStringAsync();
                    if (!r.Contains("\"pending\"")) { _output = r; return; }
                    _output = $"{pollPrefix}... {i+1}s"; Repaint();
                }
                _output = "Timeout.";
            }
            catch (Exception ex) { _output = ex.Message; }
            finally { _running = false; Repaint(); }
        }
    }
}