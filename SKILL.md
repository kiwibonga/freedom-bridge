---
name: freedom-bridge
description: >
  Use this skill whenever you need to interact with a running Unity Editor; for instance, checking if it is running, executing arbitrary C# code, triggering an asset refresh or a script recompilation. This is a generic skill that allows any programmatic operation to be performed instantly inside Unity. There is no domain reload between operations, so we can start and stop play mode and manipulate runtime data. This skill has a dedicated recipe folder for reference to perform common actions. The full SKILL.md file should be read prior to use.
---

# Freedom Bridge

A local HTTP bridge that lets you send C# code to a running Unity Editor, execute it,
and receive the result — without modifying the asset database manually or polling the
console manually.

It's recommended to interact with the bridge using curl.

## Prerequisites

If `GET http://127.0.0.1:23456/status` returns `{"status":"ok",...}`, the bridge is
running. If not, attempt to install it (see Installation below).

## General Precautions

Unity will either be in Play Mode or not. It is not recommended to make code (.cs file) changes while in play mode. Before editing code, it's a good idea to ensure play mode is off using the /exec endpoint. Code changes you make should be followed by calling the /refresh endpoint, at which point you should wait for the status endpoint to become available again. It's a good idea to output some log info in your code whenever you inject with /exec or similar endpoints, to show that there's activity. Using the /log endpoint should tell you if your changes caused a compilation error. If you see a lot of repetitive exceptions/errors that are not intended to be debug messages, the game may be in a corrupted state and may need to be reset. Always be mindful of the current play mode state.

## Recipes Folder

For practical examples and ready-to-use code snippets, refer to the **Recipes folder** located at:

```
Assets/FreedomBridge/Recipes/
```

This folder contains .md files that describe workflows and other useful knowledge about the application that can help respond to user queries. List files in this directory to see what's available, then read the files that seem relevant.

## Snippets Folder

Raw code json snippets are stored at:

```
Assets/FreedomBridge/Snippets/
```

They will typically be referred to by Recipes. (e.g. "To perform this action, run the XYZ snippet with /exec" => This is your cue to find XYZ.json in the Snippets folder by exact match).

The format of these snippets is a simple JSON object with a single "code" string field:

```json
{
    "code" : "Debug.Log(\"Test.\");\nyield return null;"
}
``` 

It's recommended to pass snippets without reading them if possible, assuming the instructions are clear enough about what the snippet does. Example /exec call using a snippet file:

```bash
curl http://127.0.0.1:23456/exec -X POST -H "Content-Type: application/json" --data-binary @my_code_snippet.json 2>&1
```

Only read the snippet if you need to modify it prior to submission, otherwise just reference it by its filename, and favor fast file copy/edit operations over full rewriting.

## Code and Behavior Guidelines

Agents using this skill should keep timeliness in mind.

A game frame is typically 16 milliseconds, but processing a short prompt and calling a tool to execute a line of code will take a few seconds at a minimum, often minutes if code research is involved. So the latency of a simple natural language command is long.

For frame-sensitive work where you need to query Unity responsively, it may be appropriate to pause the game and step through execution for short bursts or single frames before pausing again. But if possible, only pause execution during necessary inference time. For everything else, collect logs and program coroutines.

Stepping and pausing will cause aberrant frame times to be recorded in time-sensitive parts of the engine, so it may be a good idea to adjust the Time settings (temporarily) to guarantee that only one fixed update happens per frame. If stepping proves problematic (because it breaks things like networking, animation, etc.), you may suggest to the user some improvements and refactoring, such as doing a full pass of all scripts that use Unity Time functions, and making them responsive to pausing, timescale changes, etc., possibly by choosing to discard or clamp aberrant delta time values and using fixed timestepping whenever it could be advantageous.

Whenever you have to process a query, consider the timeliness component:

1. Keep the code as succinct as possible, avoid branching or multiple statements if possible, include no comments, include no unnecessary whitespace, make variable names tiny. Use 'var' 'new()' 'default' instead of fully qualified names. 
2. Favor writing multi-line commands to temporary json files so they can be recalled easily by filename.
3. Favor searching for existing solutions in Recipes, but avoid opening ALL files one by one searching for something. Use tools such as grep to look for keywords.
4. If an action requires a sequence of actions to be performed with simple decision chains, it should ideally be grouped as one coroutine.

## Dynamic Watchdogs and Persistent Constructs

Class declarations and instantiations remain persistent after the code has executed, as long as the domain doesn't reload. Arbitrary data can be stored in the game memory, and multiple agents or sub-agents can query it. For instance, one agent could set up a testing harness by following a recipe, and then monitoring of the test could be done by repeatedly running sub-agents with a smaller context that can read the recipe and pull the relevant structured data to make decisions.

## ERROR CS1525

If you spot error CS1525 about an unexpected symbol, typically that indicates an issue with the previous line being incomplete. Check that the previous statement before the error is valid, rather than the line with the reported error. Often, this happens when a "using" directive is included in the code; they should be omitted.

---

## HTTP API Reference

Base URL: `http://127.0.0.1:23456`  (port configurable in FreedomBridgeServer.cs via `FreedomBridgeServer.PORT`)

### GET /status
Health check. Returns:
```json
{"status":"ok","version":"1.0","port":23456}
```

### POST /exec
Main endpoint for executing code in the running editor immediately using Mono.CSharp.Evaluator (no domain reload).

**Request body:**
```json
{"code": "// Your C# code here\nDebug.Log(\"hi\");"}
```

**Recommended usage with file payload:**
For complex multi-line code, save your JSON to a file and use `--data-binary`:
```bash
curl http://127.0.0.1:23456/exec -X POST -H "Content-Type: application/json" --data-binary @query.json | jq .
```

**Expected Response Format:**
```json
{"success":true,"output":"Some output will be here"}
```
or
```json
{"success":false,"error":"NullReferenceException: ...","logs":["[Error] ..."]}
```

### Mono.CSharp.Evaluator Limitations

The `/exec` endpoint uses Mono's C# evaluator which has restrictions:
- No local functions: Cannot define `void Helper() {}` inside another method
- String interpolation: May require careful escaping in JSON payloads; string concatenation is safer
- Works fine: Standard loops, conditionals, reflection, property/field access via reflection

### POST /compile
Secondary endpoint that has the same input and response format as /exec, but does so by writing a temporary script. This causes the asset database to refresh, domain to reload, and the server restarts after compilation succeeds. If compilation fails, it may be necessary to clean up manually. This path is not recommended but exists for legacy reasons.

You will receive an immediate response when you submit the query. Example:
```json
{"status":"compiling","jobId":"a3f1b8c2d4e5","pollEndpoint":"/result/a3f1b8c2d4e5"}
```

You can poll GET /result/{jobId} repeatedly to find out the status of the query. It will return status: "pending" if it is still awaiting processing.

### GET /logs
Returns recent Unity console output (up to 200 lines):
```json
{"logs":["[Log] Hello","[Error] Something failed"]}
```

### POST /coroutine
Starts a coroutine (IEnumerator) or async Task that runs over multiple frames without blocking the HTTP response. Uses Mono.CSharp.Evaluator like `/exec`, so no domain reload is required.

**Request body:**
```json
{
  "code": "// Your coroutine code here\nyield return new WaitForSeconds(2f);\nDebug.Log(\"Done after 2 seconds\");",
  "timeoutSeconds": 60,
  "isAsync": false
}
```

- `code`: The coroutine body - yield statements for IEnumerator patterns, or await statements for async/await patterns
- `timeoutSeconds` (optional): Maximum execution time in seconds before auto-cancellation (default: 60)
- `isAsync` (optional): Set to true if using async/await instead of yield-based coroutines

**Response:**
```json
{
  "status": "started",
  "jobId": "abc123def456",
  "pollPath": "/result/abc123def456"
}
```

Poll the returned `/result/{jobId}` endpoint until completion. The response will be either:
- Pending: `{"status":"pending","jobId":"..."}`  
- Completed successfully: `{"success":true,"output":"...","logs":["[LOG] ..."]}`
- Failed: `{"success":false,"error":"...","logs":["[ERROR] ..."]}`

The "Using the Python client" section below has some examples of valid coroutine code

### POST /refresh
Triggers `AssetDatabase.Refresh()`. This must be called after modifying assets or scripts through the filesystem.
```json
{"success":true,"message":"Refresh queued..."}
```

---

## Code Patterns for the Compile Cycle

Your code goes into a `private static void Execute()` method body. You have full access
to `UnityEngine`, `UnityEditor`, `System.IO`, `System.Linq`, and everything in the project.

### Creating a Prefab
```csharp
var go = new GameObject("MyNewPrefab");
go.AddComponent<Rigidbody>();
go.AddComponent<BoxCollider>();

string prefabPath = "Assets/Prefabs/MyNewPrefab.prefab";
System.IO.Directory.CreateDirectory("Assets/Prefabs");
PrefabUtility.SaveAsPrefabAsset(go, prefabPath);
UnityEngine.Object.DestroyImmediate(go);
AssetDatabase.Refresh();
Debug.Log($"Prefab created at {prefabPath}");
```

### Batch-Converting Assets
```csharp
var guids = AssetDatabase.FindAssets("t:Texture2D", new[] { "Assets/Textures" });
foreach (var guid in guids)
{
    string path = AssetDatabase.GUIDToAssetPath(guid);
    var importer = (TextureImporter)AssetImporter.GetAtPath(path);
    if (importer == null) continue;
    importer.textureCompression = TextureImporterCompression.Compressed;
    importer.SaveAndReimport();
    Debug.Log($"Compressed: {path}");
}
Debug.Log($"Done: {guids.Length} textures processed.");
```

### Reading Scene Contents
```csharp
foreach (var go in UnityEngine.SceneManagement.SceneManager
                               .GetActiveScene().GetRootGameObjects())
{
    Debug.Log($"{go.name} — children: {go.transform.childCount}");
}
```

### Modifying a ScriptableObject
```csharp
var so = AssetDatabase.LoadAssetAtPath<YourScriptableObject>("Assets/Data/Config.asset");
if (so == null) { Debug.LogError("Not found"); return; }
so.someValue = 42;
EditorUtility.SetDirty(so);
AssetDatabase.SaveAssets();
Debug.Log("Config updated.");
```

### Running a Menu Item
```csharp
EditorApplication.ExecuteMenuItem("File/Save Project");
```

---

## Triggering a Domain Reload

After modifying assets and code files in the Unity project folder, it's important to refresh the asset database.

```http
POST /refresh
{}
```

This calls `AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate)`. The HTTP server will stop temporarily until the domain reload completes. If there are any compile errors, the server will be unavailable! If you do not have a way to see the editor log or compile log somehow, you will have to beg the user to paste you the logs from the Unity window.

---

## Checking Compilation Status After a Refresh

```python
# Poll until the bridge server comes back up (domain reload takes 2–30s)
for _ in range(60):
    try:
        resp = GET("/status")
        if resp["status"] == "ok":
            print("Compilation complete, domain reloaded.")
            break
    except ConnectionError:
        pass  # server is reloading, normal
    time.sleep(1)
```

---

## Using the Python Client

```python
from unity_bridge import UnityBridge
bridge = UnityBridge()

# Check connection
print(bridge.status())

# Compile-cycle execution (requires domain reload)
result = bridge.compile_exec("""
    var guids = AssetDatabase.FindAssets("t:Prefab", new[] {"Assets"});
    Debug.Log($"Found {guids.Length} prefabs");
""")
print(result)  # {"success": true, "logs": ["[Log] Found 42 prefabs"]}

# Coroutine execution (no domain reload - uses Mono.CSharp.Evaluator at runtime)
result = bridge.coroutine_exec("""
    yield return new WaitForSeconds(2f);
    Debug.Log("Done after 2 seconds!");
""")
print(result)  # {"success": true, "output": "", "logs": ["[LOG] Done after 2 seconds!"]}

# Async/await pattern (set is_async=True for async Task patterns)
result = bridge.coroutine_exec("""
    await System.Threading.Tasks.Task.Delay(1000);
    Debug.Log("Async completed");
""", is_async=True)
print(result)

# Get recent console logs
for line in bridge.logs():
    print(line)
```

**Port conflict?** Change `PORT = 23456` in [`FreedomBridgeServer.cs`](Assets/Editor/FreedomBridge/FreedomBridgeServer.cs) and set
`UNITY_BRIDGE_PORT` env var for the python client.

---

## Installation

Delegate this installation workflow task to another agent preferably:

1. Copy the folder `FreedomBridge/` located in the same folder as this SKILL.md into the Unity project's `Assets/` folder
2. Ask the user to tab to Unity to trigger compilation; the server should start on port 23456. If it is not possible (e.g. if we are in headless mode, or auto-accept, or skip permissions, or user doesn't have arms), use any means at your disposal to trigger a script refresh.
3. Confirm by querying the /status endpoint. If the endpoint doesn't become reachable, ask the user if any errors are visible and offer to troubleshoot. In unattended mode, you may want to investigate the Editor.log -- Use standard troubleshooting steps and don't hesitate to restart Unity if all else fails.

## Emergency Unity Focus

If the server is down and requires a code fix before it can start up, as is often the case on first install, you may attempt to focus the Unity window by using the following shell commands. Make sure to replace projectname with the unity project folder name, with proper case.

Linux: 

```
wmctrl -a "Unity - projectname"
```

or

```
xdotool search --name "Unity - projectname" windowactivate --sync
```

Windows:

```
powershell -Command "Add-Type -AssemblyName Microsoft.VisualBasic; [Microsoft.VisualBasic.Interaction]::AppActivate('projectname - ')"
```

MacOS (untested):

```
osascript -e 'tell application "Unity - projectname" to activate'
```

Note that if this is unsuccessful, a slow, but surefire cross-platform approach is to simply kill the unity process (make sure you get unity editor itself and not the hub), then reopen the project; if there are no compile errors, the server should start. It's a good idea to set up a bash script that will be able to run long and poll continuously, stopping when successful.
