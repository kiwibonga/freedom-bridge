// Evaluator.cs
// In-process C# execution using the Mono.CSharp.Evaluator bundled with the Unity Editor.
// No NuGet packages, no external DLLs — uses the compiler Unity already ships.
// The bridge locates Mono.CSharp.dll from the Unity Editor install automatically at startup.
// If unavailable (e.g., non-Editor build), /exec returns 503 and agents fall back to /compile.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace FreedomBridge
{
    public static class Evaluator
    {
        public static bool Available => _evaluator != null;

        // Evaluator instance — created once, reused across calls (REPL-style state)
        private static object   _evaluator;      // Mono.CSharp.Evaluator
        private static MethodInfo _runMethod;    // Evaluator.Run(string)
        private static MethodInfo _refMethod;    // Evaluator.ReferenceAssembly(Assembly)
        private static StringWriter _reportWriter;

        static Evaluator()
        {
            try   { Init(); }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FreedomBridge] Mono.CSharp.Evaluator init failed: {ex.Message}\n" +
                                  "The /exec endpoint will be unavailable. /compile still works.");
            }
        }

        private static void Init()
        {
            var monoCs = FindMonoCSharpDll();
            if (monoCs == null)
            {
                Debug.LogWarning("[FreedomBridge] Could not locate Mono.CSharp.dll in the Unity install. " +
                                 "Checked candidate paths — see console for details.");
                return;
            }

            var asm = Assembly.LoadFrom(monoCs);
            Debug.Log($"[FreedomBridge] Loaded Mono.CSharp from: {monoCs}");

            // ── Build CompilerContext ──────────────────────────────────────────
            // API: new CompilerContext(CompilerSettings, ReportPrinter)
            var settingsType = asm.GetType("Mono.CSharp.CompilerSettings")
                ?? throw new Exception("Mono.CSharp.CompilerSettings not found");
            var settings = Activator.CreateInstance(settingsType);

            // Use a TextWriterReportPrinter so we can capture compiler errors
            _reportWriter = new StringWriter();
            var printerType = asm.GetType("Mono.CSharp.StreamReportPrinter")
                           ?? asm.GetType("Mono.CSharp.TextWriterReportPrinter")
                           ?? asm.GetType("Mono.CSharp.ConsoleReportPrinter");
            if (printerType == null)
                throw new Exception("No suitable ReportPrinter type found in Mono.CSharp");

            // StreamReportPrinter / TextWriterReportPrinter take a TextWriter ctor arg
            // ConsoleReportPrinter takes no args
            object printer;
            var twCtor = printerType.GetConstructor(new[] { typeof(TextWriter) });
            printer = twCtor != null
                ? Activator.CreateInstance(printerType, _reportWriter)
                : Activator.CreateInstance(printerType);

            var contextType = asm.GetType("Mono.CSharp.CompilerContext")
                ?? throw new Exception("Mono.CSharp.CompilerContext not found");
            var context = Activator.CreateInstance(contextType, settings, printer);

            // ── Create Evaluator ──────────────────────────────────────────────
            var evalType = asm.GetType("Mono.CSharp.Evaluator")
                ?? throw new Exception("Mono.CSharp.Evaluator not found");
            _evaluator = Activator.CreateInstance(evalType, context);

            // Cache method infos
            _runMethod  = evalType.GetMethod("Run",  new[] { typeof(string) });
            _refMethod  = evalType.GetMethod("ReferenceAssembly", new[] { typeof(Assembly) });

            if (_runMethod == null || _refMethod == null)
                throw new Exception("Could not find required methods on Mono.CSharp.Evaluator");

            // ── Reference all currently loaded assemblies ─────────────────────
            ReferenceLoadedAssemblies();

            // ── Pre-import common namespaces ──────────────────────────────────
            // Skip System.* to avoid mscorlib duplicate type errors (Evaluator loads it internally)
            var usings = new[]
            {
                "using UnityEngine;",
                "using UnityEditor;",
                "using UnityEditor.SceneManagement;",
                "using UnityEngine.SceneManagement;"
            };
            foreach (var u in usings)
                _runMethod.Invoke(_evaluator, new object[] { u });

            Debug.Log("[FreedomBridge] Mono.CSharp.Evaluator ready.");
        }

        // Unity ships a monolithic UnityEngine.dll alongside modular DLLs like
        // UnityEngine.CoreModule.dll. Both are loaded at runtime, causing CS0433
        // "type defined multiple times" errors if we reference all assemblies naively.
        // Skip the known facade assemblies — their types are all in the modular DLLs.
        private static readonly HashSet<string> _facadeNames = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase)
        {
            "UnityEngine",           // monolithic facade for all UnityEngine.* modules
            "UnityEngine.Networking", // legacy networking facade
            "Unity.Analytics",
        };

        private static readonly HashSet<string> _referencedPaths = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);

        private static void ReferenceLoadedAssemblies()
        {
            bool hasModular = AppDomain.CurrentDomain.GetAssemblies()
                .Any(a => a.GetName().Name == "UnityEngine.CoreModule");

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                string name = null;
                try
                {
                    if (asm.IsDynamic) continue;

                    name = asm.GetName().Name;

                    // Skip mscorlib - Mono.CSharp.Evaluator already loads it internally
                    if (name.Equals("mscorlib", StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Skip monolithic facades when modular equivalents are loaded
                    if (hasModular && _facadeNames.Contains(name)) continue;

                    // Skip already-referenced paths
                    var loc = asm.Location;
                    if (string.IsNullOrEmpty(loc)) continue;
                    if (!_referencedPaths.Add(loc)) continue;

                    _refMethod.Invoke(_evaluator, new object[] { asm });
                }
                catch (Exception ex) 
                { 
                    Debug.Log($"[FreedomBridge] Skipping assembly reference ({name ?? "unknown"}): {ex.Message}"); 
                }
            }
        }

        /// <summary>
        /// Execute C# code via Mono.CSharp.Evaluator.
        /// Logs written to the Unity console during execution are captured and returned.
        /// </summary>
        public static string Execute(string code)
        {
            if (_evaluator == null)
                throw new InvalidOperationException("Evaluator not initialized.");

            // Reference any assemblies loaded since Init() (e.g. after a prior /compile)
            ReferenceLoadedAssemblies();

            var logs = new List<string>();
            Application.LogCallback capture = (msg, _, type) =>
                logs.Add($"[{type}] {msg}");
            Application.logMessageReceived += capture;

            _reportWriter?.GetStringBuilder().Clear();

            try
            {
                _runMethod.Invoke(_evaluator, new object[] { code });

                // Collect compiler diagnostics and Unity console output
                var compilerOut = _reportWriter?.ToString().Trim();

                var sb = new StringBuilder();
                if (!string.IsNullOrEmpty(compilerOut)) sb.AppendLine(compilerOut);
                foreach (var log in logs) sb.AppendLine(log);
                return sb.ToString().TrimEnd() ?? "";
            }
            finally
            {
                Application.logMessageReceived -= capture;
            }
        }

        /// <summary>Reset evaluator state (clears all declared variables and methods).</summary>
        public static void ResetSession()
        {
            try   { Init(); }
            catch (Exception ex) { Debug.LogError($"[FreedomBridge] Evaluator reset failed: {ex}"); }
        }

        /// <summary>
        /// Execute coroutine code via Mono.CSharp.Evaluator.
        /// For IEnumerator patterns, the code should be a sequence of yield statements that form an enumerator body.
        /// For async/await patterns, the code can use await keywords with Unity's AsyncOperation or System.Threading.Tasks.Task.
        /// Returns either an IEnumerator for direct StartCoroutine usage, or a Task for async operations.
        /// </summary>
        public static object ExecuteCoroutineCode(string code, bool isAsync = false)
        {
            if (_evaluator == null)
                throw new InvalidOperationException("Evaluator not initialized.");

            _reportWriter?.GetStringBuilder().Clear();

            try
            {
                string className = $"__Coro_{System.Guid.NewGuid():N}";

                string classDefinition = isAsync
                    ? $@"public static class {className}
{{
    public static async global::System.Threading.Tasks.Task Run()
    {{
        using Task = global::System.Threading.Tasks.Task;
{IndentCode(code)}
    }}
}}"
            : $@"public static class {className}
{{
    public static System.Collections.IEnumerator Run()
    {{
{IndentCode(code)}
    }}
}}";

                Debug.Log($"[FreedomBridge] Defining coroutine class:\n{classDefinition}");

                if (_runMethod == null)
                    throw new InvalidOperationException("_runMethod is null");

                _reportWriter?.GetStringBuilder().Clear();
                _runMethod.Invoke(_evaluator, new object[] { classDefinition });

                var compilerOut = _reportWriter?.ToString().Trim();
                if (!string.IsNullOrEmpty(compilerOut))
                    Debug.LogWarning($"[FreedomBridge] Compiler output: {compilerOut}");

                // Find the compiled type in the AppDomain (most recently loaded first)
                Type dynType = AppDomain.CurrentDomain
                    .GetAssemblies()
                    .Reverse() // most recently loaded first — our class will be near the end
                    .SelectMany(a =>
                    {
                        try { return a.GetTypes(); }
                        catch { return System.Array.Empty<Type>(); }
                    })
                    .FirstOrDefault(t => t.Name == className);

                if (dynType == null)
                {
                    var errorMsg = $"[FreedomBridge] Could not find compiled type '{className}' in AppDomain.";
                    if (!string.IsNullOrEmpty(compilerOut))
                        errorMsg += $"\nCompiler errors:\n{compilerOut}";
                    throw new Exception(errorMsg);
                }

                var runMethod = dynType.GetMethod("Run", BindingFlags.Public | BindingFlags.Static);
                if (runMethod == null)
                    throw new Exception($"[FreedomBridge] Found type '{className}' but could not find static 'Run' method.");

                object result = runMethod.Invoke(null, null);

                if (isAsync)
                {
                    if (result is System.Threading.Tasks.Task task)
                    {
                        Debug.Log("[FreedomBridge] Async task created successfully.");
                        return task;
                    }
                    throw new Exception($"[FreedomBridge] Expected Task from async Run(), got: {result?.GetType().Name ?? "null"}");
                }
                else
                {
                    if (result is IEnumerator enumerator)
                    {
                        Debug.Log("[FreedomBridge] Coroutine created successfully: IEnumerator");
                        return enumerator;
                    }
                    throw new Exception($"[FreedomBridge] Expected IEnumerator from Run(), got: {result?.GetType().Name ?? "null"}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[FreedomBridge] ExecuteCoroutineCode failed: {ex.Message}\n{ex.StackTrace}");
                throw;
            }
        }

        private static string IndentCode(string code)
        {
            // Normalise and indent each line by 8 spaces to sit cleanly inside the method body
            return string.Join("\n", code
                .Split('\n')
                .Select(line => "        " + line.TrimEnd()));
        }

        /// <summary>
        /// Check if Mono.CSharp.Evaluator is available for coroutine execution.
        /// </summary>
        public static bool CanExecuteCoroutines() => _evaluator != null;

        // ── Finding Mono.CSharp.dll ───────────────────────────────────────────────

        private static string FindMonoCSharpDll()
        {
            // EditorApplication.applicationContentsPath = e.g.
            //   Linux: /home/user/Unity/Hub/Editor/2022.3.x/Editor/Data
            //   Windows: C:\Program Files\Unity\Hub\Editor\2022.3.x\Editor\Data
            var root = EditorApplication.applicationContentsPath;

            var candidates = new[]
            {
                // Unity 2019+ MonoBleedingEdge layout
                Path.Combine(root, "MonoBleedingEdge", "lib", "mono", "4.5",            "Mono.CSharp.dll"),
                Path.Combine(root, "MonoBleedingEdge", "lib", "mono", "unityjit",       "Mono.CSharp.dll"),
                Path.Combine(root, "MonoBleedingEdge", "lib", "mono", "unityjit-linux", "Mono.CSharp.dll"),
                Path.Combine(root, "MonoBleedingEdge", "lib", "mono", "unity",          "Mono.CSharp.dll"),
                // Older layout
                Path.Combine(root, "Mono",             "lib", "mono", "4.5",            "Mono.CSharp.dll"),
                Path.Combine(root, "Mono",             "lib", "mono", "2.0",            "Mono.CSharp.dll"),
                // Fallback: walk MonoBleedingEdge looking for it
            };

            foreach (var c in candidates)
            {
                if (File.Exists(c)) return c;
            }

            // Deep search fallback under MonoBleedingEdge
            var bleeding = Path.Combine(root, "MonoBleedingEdge");
            if (Directory.Exists(bleeding))
            {
                foreach (var f in Directory.GetFiles(bleeding, "Mono.CSharp.dll", SearchOption.AllDirectories))
                {
                    Debug.Log($"[FreedomBridge] Found Mono.CSharp.dll via deep search: {f}");
                    return f;
                }
            }

            foreach (var c in candidates)
                Debug.Log($"[FreedomBridge] Not found: {c}");

            return null;
        }
    }
}