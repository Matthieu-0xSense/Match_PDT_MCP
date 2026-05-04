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
import json
import os
import subprocess
import sys
import threading
from typing import Any


_PROC: subprocess.Popen | None = None
_LOCK = threading.Lock()
_NEXT_ID = 1
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

    # Drain stdout until "Ready." — anything before is PDT's dialog noise.
    while True:
        line = proc.stdout.readline() if proc.stdout else ""
        if not line:
            stderr = proc.stderr.read() if proc.stderr else ""
            raise HelperError(
                f"dotnet-helper-v2 exited before Ready (rc={proc.returncode}). "
                f"stderr tail:\n{stderr[-2000:]}"
            )
        if line.strip() == "Ready.":
            break

    return proc


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
    global _NEXT_ID
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
        line = proc.stdout.readline()

    if not line:
        raise HelperError("helper closed stdout unexpectedly")
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
