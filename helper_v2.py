"""Long-lived client for dotnet-helper-v2 over stdio JSON-RPC.

The v2 helper spends ~15-25 seconds bootstrapping PDT's headless host on each
launch. Spawning a fresh process per MCP tool call would dominate the user
experience, so this module runs one helper for the lifetime of the server
process and routes every `add_custom_error` (and future v2 verbs) through it.

The helper writes some non-JSON dialog noise to stdout before printing the
literal `Ready.` line; after that it speaks newline-delimited JSON-RPC. We
drain the pre-Ready noise once, then push requests/read responses for the
rest of the session.

Usage:
    from helper_v2 import call as helper_v2_call
    result = helper_v2_call("add_custom_error", {...})
"""

from __future__ import annotations

import atexit
import collections
import json
import os
import subprocess
import sys
import threading
from typing import Any


_PROC: subprocess.Popen | None = None
_LOCK = threading.Lock()
_NEXT_ID = 1
# Ring buffer of the helper's stderr, filled by a drain thread. The helper logs
# every operation to stderr; with stderr=PIPE and nobody reading, the OS pipe
# buffer fills and the helper blocks forever on its own WriteLog. That looks
# exactly like a hung RPC call. Keep the pipe drained and the tail available
# for error messages.
_STDERR_TAIL: collections.deque = collections.deque(maxlen=400)
_REPO_ROOT = os.path.dirname(os.path.abspath(__file__))
_HELPER_EXE = os.path.join(
    _REPO_ROOT, "dotnet-helper-v2", "bin", "Debug", "net48",
    "Match_PDT_Helper_v2.exe",
)


class HelperError(RuntimeError):
    """Raised when the v2 helper returns an RPC error or can't be reached."""


def _start(hdb_path: str, pdt_dir: str) -> subprocess.Popen:
    """Spawn the helper and wait for the `Ready.` line on stdout.

    `pdt_dir` becomes the helper's working directory so its
    AppDomain.AssemblyResolve hook can find the PDT DLLs.
    """
    if not os.path.exists(_HELPER_EXE):
        raise HelperError(
            f"dotnet-helper-v2 not built. Build it with:\n"
            f"  dotnet build {os.path.join(_REPO_ROOT, 'dotnet-helper-v2', 'Match_PDT_Helper_v2.csproj')}"
        )

    proc = subprocess.Popen(
        [_HELPER_EXE, hdb_path],
        cwd=pdt_dir,
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        bufsize=1,  # line-buffered
    )

    _STDERR_TAIL.clear()
    _start_stderr_drain(proc)

    # Drain stdout until "Ready." — anything before is PDT's dialog noise.
    while True:
        line = proc.stdout.readline() if proc.stdout else ""
        if not line:
            raise HelperError(
                f"dotnet-helper-v2 exited before Ready (rc={proc.returncode}). "
                f"stderr tail:\n{stderr_tail()}"
            )
        if line.strip() == "Ready.":
            break

    return proc


def _start_stderr_drain(proc: subprocess.Popen) -> None:
    """Keep the helper's stderr pipe empty so it can never block writing to it."""
    def drain() -> None:
        stream = proc.stderr
        if stream is None:
            return
        for line in iter(stream.readline, ""):
            _STDERR_TAIL.append(line.rstrip("\n"))
        try:
            stream.close()
        except Exception:
            pass

    threading.Thread(target=drain, daemon=True, name="helper_v2-stderr").start()


def stderr_tail(n: int = 40) -> str:
    """Last n lines the helper logged — useful when a call fails."""
    return "\n".join(list(_STDERR_TAIL)[-n:])


def _ensure(hdb_path: str, pdt_dir: str) -> subprocess.Popen:
    """Get the running helper process, starting it on first use."""
    global _PROC
    with _LOCK:
        if _PROC is None or _PROC.poll() is not None:
            _PROC = _start(hdb_path, pdt_dir)
            atexit.register(_shutdown)
        return _PROC


def call(method: str, params: dict[str, Any] | None = None,
         hdb_path: str | None = None, pdt_dir: str | None = None,
         timeout: float = 60.0) -> Any:
    """Send a JSON-RPC request and return the result.

    Falls back to environment variables / parser.py-style discovery for
    hdb_path and pdt_dir. Raises HelperError on RPC error.
    """
    if hdb_path is None or pdt_dir is None:
        # Local import to avoid a hard dependency from this module on parser.
        from parser import HDB_PATH, _resolve_pdt_dir
        hdb_path = hdb_path or HDB_PATH
        pdt_dir = pdt_dir or _resolve_pdt_dir(hdb_path)
    if not hdb_path:
        raise HelperError("HDB_PATH not set; cannot start dotnet-helper-v2")

    proc = _ensure(hdb_path, pdt_dir)
    global _NEXT_ID, _PROC
    with _LOCK:
        req_id = _NEXT_ID
        _NEXT_ID += 1
        req = {"jsonrpc": "2.0", "id": req_id, "method": method}
        if params is not None:
            req["params"] = params
        if proc.stdin is None or proc.stdout is None:
            raise HelperError("helper stdin/stdout unavailable")
        proc.stdin.write(json.dumps(req) + "\n")
        proc.stdin.flush()
        # PDT's dialog adapter occasionally writes blank/non-JSON lines to stdout
        # mid-session. Skip anything that doesn't start with '{'.
        #
        # readline() blocks forever, so `timeout` was previously decorative: a
        # wedged helper hung the caller indefinitely. Read on a worker thread and
        # enforce the deadline. On expiry the helper is killed so the next call
        # respawns a clean one.
        box: dict[str, Any] = {}

        def _read() -> None:
            try:
                while True:
                    raw = proc.stdout.readline()
                    if not raw:
                        box["err"] = HelperError("helper closed stdout unexpectedly")
                        return
                    if raw.lstrip().startswith("{"):
                        box["line"] = raw
                        return
                    print(f"[helper_v2 skip] {raw!r}", file=sys.stderr)
            except Exception as exc:  # noqa: BLE001 - surfaced to the caller
                box["err"] = exc

        # NOTE: killing mid-save is not free. HostBootstrap.SaveProject writes the
        # project and only afterwards patches info.xml's PdtVersionString back from
        # PDT's own stamp (1.0.0.0, our assembly version). A kill landing between
        # those two steps leaves the .hdb unloadable by PDT and by the next helper
        # launch. Repair by rewriting info.xml, or make the save atomic.
        reader = threading.Thread(target=_read, daemon=True, name="helper_v2-read")
        reader.start()
        reader.join(timeout)
        if reader.is_alive():
            try:
                proc.kill()
            except Exception:
                pass
            _PROC = None
            raise HelperError(
                f"helper did not answer within {timeout}s; killed so the next call "
                f"respawns it. stderr tail:\n{stderr_tail()}"
            )
        if "err" in box:
            raise box["err"]
        line = box["line"]
    try:
        resp = json.loads(line)
    except json.JSONDecodeError as e:
        raise HelperError(f"non-JSON response: {line!r} ({e})")

    if "error" in resp and resp["error"] is not None:
        err = resp["error"]
        raise HelperError(f"{err.get('message', '')} (code={err.get('code')})")
    return resp.get("result")


def _shutdown() -> None:
    """Send shutdown then wait briefly for graceful exit."""
    global _PROC
    proc = _PROC
    _PROC = None
    if proc is None or proc.poll() is not None:
        return
    try:
        if proc.stdin is not None:
            proc.stdin.write(json.dumps({"jsonrpc": "2.0", "id": 0, "method": "shutdown"}) + "\n")
            proc.stdin.flush()
    except Exception:
        pass
    try:
        proc.wait(timeout=10)
    except subprocess.TimeoutExpired:
        try:
            proc.kill()
        except Exception:
            pass


def is_running() -> bool:
    return _PROC is not None and _PROC.poll() is None
