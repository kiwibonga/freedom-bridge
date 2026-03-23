#!/usr/bin/env python3
"""
unity_bridge.py — Python client for the Unity Agent Bridge
Usage as a library:
    from unity_bridge import UnityBridge
    bridge = UnityBridge()
    result = bridge.compile_exec('''
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/MyPrefab.prefab");
        Debug.Log(prefab.name);
    ''')
    print(result)

Usage from CLI:
    python unity_bridge.py status
    python unity_bridge.py logs
    python unity_bridge.py exec "Debug.Log(Application.dataPath);"
    python unity_bridge.py compile "Debug.Log(Application.productName);"
    python unity_bridge.py compile-file myscript.cs
    python unity_bridge.py refresh
"""

import json
import sys
import time
import os
import urllib.request
import urllib.error
from typing import Optional

DEFAULT_PORT    = 23456
DEFAULT_TIMEOUT = 120   # seconds to wait for compile result


class UnityBridge:
    def __init__(self, port: int = DEFAULT_PORT, timeout: int = DEFAULT_TIMEOUT):
        self.base = f"http://127.0.0.1:{port}"
        self.timeout = timeout

    # ── Low-level HTTP ────────────────────────────────────────────────────────

    def _get(self, path: str) -> dict:
        url = f"{self.base}{path}"
        try:
            with urllib.request.urlopen(url, timeout=10) as r:
                return json.loads(r.read())
        except urllib.error.URLError as e:
            raise ConnectionError(
                f"Cannot reach Unity Bridge at {self.base}. "
                f"Is the Unity Editor open with the bridge installed?\n  {e}"
            )

    def _post(self, path: str, payload: dict) -> dict:
        url  = f"{self.base}{path}"
        body = json.dumps(payload).encode()
        req  = urllib.request.Request(url, data=body,
                                      headers={"Content-Type": "application/json"})
        try:
            with urllib.request.urlopen(req, timeout=10) as r:
                return json.loads(r.read())
        except urllib.error.URLError as e:
            raise ConnectionError(
                f"Cannot reach Unity Bridge at {self.base}. "
                f"Is the Unity Editor open with the bridge installed?\n  {e}"
            )

    # ── Public API ────────────────────────────────────────────────────────────

    def status(self) -> dict:
        """Check if the bridge is alive. Returns server info dict."""
        return self._get("/status")

    def is_alive(self) -> bool:
        try:
            self.status()
            return True
        except ConnectionError:
            return False

    def logs(self) -> list[str]:
        """Return recent Unity console log lines."""
        return self._get("/logs").get("logs", [])

    def refresh(self) -> dict:
        """Trigger AssetDatabase.Refresh(). Use after file changes outside Unity."""
        return self._post("/refresh", {})

    def exec_roslyn(self, code: str) -> str:
        """
        Execute C# code in-process via Roslyn (no recompile, instant).
        Requires Roslyn DLLs to be installed — see README.
        Falls back with a clear error message if unavailable.
        Returns captured output as a string.
        """
        result = self._post("/exec", {"code": code})
        if not result.get("success", False):
            raise RuntimeError(f"Exec failed: {result.get('error', 'unknown error')}")
        return result.get("output", "")

    def compile_exec(self, code: str, poll_interval: float = 1.5) -> dict:
        """
        Submit C# code for compile-cycle execution.
        Waits for the domain reload to complete and returns the result.

        Returns dict with keys:
            success (bool), output (str if success), error (str if failed),
            logs (list of console messages captured during execution)

        Raises TimeoutError if compile + execute takes longer than self.timeout seconds.
        Raises RuntimeError on submission failure.
        """
        resp = self._post("/compile", {"code": code})

        if "error" in resp:
            raise RuntimeError(f"Submission failed: {resp['error']}")

        job_id    = resp.get("jobId")
        poll_ep   = resp.get("pollEndpoint", f"/result/{job_id}")
        deadline  = time.time() + self.timeout

        while time.time() < deadline:
            time.sleep(poll_interval)
            try:
                result = self._get(poll_ep)
            except ConnectionError:
                # Server is likely reloading domain — retry silently
                time.sleep(poll_interval)
                continue

            if result.get("status") == "pending":
                continue

            # Got a result
            return result

        raise TimeoutError(
            f"Job {job_id} did not complete within {self.timeout}s. "
            f"Check the Unity Console for compile errors."
        )

    def coroutine_exec(self, code: str, timeout_seconds: int = 60, 
                       poll_interval: float = 0.5, is_async: bool = False) -> dict:
        """
        Execute C# coroutine (IEnumerator with yield statements) or async Task via Mono.CSharp.Evaluator.
        No domain reload required - runs at runtime using the same mechanism as /exec.

        The code should be a sequence of yield return statements (for IEnumerator patterns)
        or await statements (for async/await patterns when is_async=True).

        Example (IEnumerator):
            bridge.coroutine_exec("""
                yield return new WaitForSeconds(2f);
                Debug.Log("Done after 2 seconds");
            """)

        Example (async/await):
            bridge.coroutine_exec("""
                await System.Threading.Tasks.Task.Delay(2000);
                Debug.Log("Done after 2 seconds");
            """, is_async=True)

        Returns dict with keys:
            success (bool), output (str if success), error (str if failed),
            logs (list of console messages captured during execution)

        Raises TimeoutError if coroutine takes longer than timeout_seconds.
        Raises RuntimeError on submission failure.
        """
        payload = {"code": code, "timeoutSeconds": timeout_seconds}
        if is_async:
            payload["isAsync"] = True
            
        resp = self._post("/coroutine", payload)

        if "error" in resp:
            raise RuntimeError(f"Coroutine submission failed: {resp['error']}")

        job_id    = resp.get("jobId")
        poll_ep   = resp.get("pollPath", f"/result/{job_id}")
        deadline  = time.time() + timeout_seconds

        while time.time() < deadline:
            time.sleep(poll_interval)
            try:
                result = self._get(poll_ep)
            except ConnectionError as e:
                # Server might be unavailable - retry
                continue

            if result.get("status") == "pending":
                continue

            # Got a final result (success or failure)
            return result

        raise TimeoutError(
            f"Coroutine job {job_id} did not complete within {timeout_seconds}s."
        )

    def compile_exec_file(self, path: str, **kwargs) -> dict:
        """Same as compile_exec but reads code from a file."""
        with open(path, "r", encoding="utf-8") as f:
            return self.compile_exec(f.read(), **kwargs)

    def wait_for_server(self, max_wait: int = 30) -> bool:
        """Block until the server is available or max_wait seconds elapse."""
        deadline = time.time() + max_wait
        while time.time() < deadline:
            if self.is_alive():
                return True
            time.sleep(1)
        return False


# ── CLI ───────────────────────────────────────────────────────────────────────

def _usage():
    print(__doc__)
    sys.exit(1)

def main():
    args = sys.argv[1:]
    if not args:
        _usage()

    port   = int(os.environ.get("UNITY_BRIDGE_PORT", DEFAULT_PORT))
    bridge = UnityBridge(port=port)
    cmd    = args[0]

    try:
        if cmd == "status":
            s = bridge.status()
            print(json.dumps(s, indent=2))

        elif cmd == "logs":
            for line in bridge.logs():
                print(line)

        elif cmd == "refresh":
            print(json.dumps(bridge.refresh(), indent=2))

        elif cmd == "exec":
            if len(args) < 2: _usage()
            out = bridge.exec_roslyn(args[1])
            print(out or "(no output)")

        elif cmd in ("compile", "run"):
            if len(args) < 2: _usage()
            result = bridge.compile_exec(args[1])
            _print_result(result)

        elif cmd == "compile-file":
            if len(args) < 2: _usage()
            result = bridge.compile_exec_file(args[1])
            _print_result(result)

        else:
            print(f"Unknown command: {cmd}")
            _usage()

    except ConnectionError as e:
        print(f"[ERROR] {e}", file=sys.stderr)
        sys.exit(1)
    except TimeoutError as e:
        print(f"[TIMEOUT] {e}", file=sys.stderr)
        sys.exit(2)
    except RuntimeError as e:
        print(f"[FAILED] {e}", file=sys.stderr)
        sys.exit(3)

def _print_result(result: dict):
    if result.get("success"):
        logs = result.get("logs", [])
        if logs:
            print("=== Console logs ===")
            for line in logs:
                print(line)
        print("=== Result: SUCCESS ===")
    else:
        print(f"=== Result: FAILED ===")
        print(result.get("error", "No error message"))
        logs = result.get("logs", [])
        if logs:
            print("=== Console logs ===")
            for line in logs:
                print(line)
        sys.exit(1)

if __name__ == "__main__":
    main()
