#!/usr/bin/env python3
"""
HTTP executor running inside the ACI workspace container.

POST /run     {"cmd": "...", "timeout": 60}
              -> {"stdout":"...","stderr":"...","exit_code":0}
POST /read    {"path": "src/main.cs", "offset": 0, "limit": 200}
              -> {"content":"1\t...\n2\t...","total_lines":N,"from_line":1,"to_line":N}
POST /write   {"path": "src/foo.cs", "content": "..."}
              -> {"written": true, "path": "src/foo.cs"}
POST /patch   {"path": "src/foo.cs", "old_string": "...", "new_string": "...", "replace_all": false}
              -> {"applied": true, "path": "src/foo.cs"} | {"error": "old_string not found in 'src/foo.cs'"}
POST /list    {"path": "src/"}
              -> {"entries": [{"name":"foo.cs","type":"file","size":1234},...], "path":"src/"}
POST /search  {"pattern": "class Auth", "path": "src/", "glob": "*.cs"}
              -> {"matches": [{"file":"src/Auth.cs","line":12,"text":"class AuthService"},...]}
GET  /health                               -> 200 "ok"

Auth: Authorization: Bearer <EXECUTOR_TOKEN>  (env var; if empty, auth is skipped for dev)
All file operations are scoped to /workspace (no path traversal outside it).
"""
import os
import json
import signal
import subprocess
from http.server import ThreadingHTTPServer, BaseHTTPRequestHandler

TOKEN = os.environ.get("EXECUTOR_TOKEN", "")
WORKDIR = "/workspace"
MAX_LIMIT = 500


def _full_path(rel: str) -> str:
    """Resolve a workspace-relative path, rejecting traversal outside /workspace."""
    clean = os.path.normpath(rel.lstrip("/")) if rel else "."
    full = os.path.join(WORKDIR, clean)
    if not os.path.abspath(full).startswith(os.path.abspath(WORKDIR)):
        raise ValueError(f"Path '{rel}' escapes workspace")
    return full


class Handler(BaseHTTPRequestHandler):
    def log_message(self, fmt, *args):
        pass  # suppress default access log noise

    def _auth_ok(self):
        if not TOKEN:
            return True
        return self.headers.get("Authorization", "") == f"Bearer {TOKEN}"

    def _json_response(self, obj, status=200):
        payload = json.dumps(obj).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)

    def do_GET(self):
        if self.path == "/health":
            self.send_response(200)
            self.end_headers()
            self.wfile.write(b"ok")
        else:
            self.send_response(404)
            self.end_headers()

    def do_POST(self):
        if not self._auth_ok():
            self.send_response(401)
            self.end_headers()
            return

        length = int(self.headers.get("Content-Length", 0))
        body = json.loads(self.rfile.read(length) or b"{}")

        dispatch = {
            "/run":    self._handle_run,
            "/read":   self._handle_read,
            "/write":  self._handle_write,
            "/patch":  self._handle_patch,
            "/list":   self._handle_list,
            "/search": self._handle_search,
        }
        handler = dispatch.get(self.path)
        if handler is None:
            self.send_response(404)
            self.end_headers()
            return

        try:
            self._json_response(handler(body))
        except Exception as ex:
            self._json_response({"error": str(ex)}, 500)

    # ── Handlers ──────────────────────────────────────────────────────────────

    def _handle_run(self, body):
        cmd = body.get("cmd", "")
        timeout = int(body.get("timeout", 60))
        # stdin=DEVNULL makes the sandbox genuinely non-interactive: any read from stdin gets EOF
        # immediately instead of blocking forever. Without this an interactive program (e.g. `dotnet run`
        # on a console menu calling Console.ReadKey/ReadLine) hangs until the timeout and piles up
        # resources, which has wedged the whole container. start_new_session puts the command in its own
        # process group so a timeout kills the entire tree (the shell AND `dotnet run`'s child app),
        # not just the shell.
        try:
            proc = subprocess.Popen(
                cmd, shell=True, cwd=WORKDIR,
                stdin=subprocess.DEVNULL,
                stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                text=True, start_new_session=True,
            )
        except Exception as ex:
            return {"stdout": "", "stderr": f"failed to start command: {ex}", "exit_code": -1}
        try:
            stdout, stderr = proc.communicate(timeout=timeout)
            return {"stdout": stdout, "stderr": stderr, "exit_code": proc.returncode}
        except subprocess.TimeoutExpired:
            try:
                os.killpg(os.getpgid(proc.pid), signal.SIGKILL)
            except (ProcessLookupError, PermissionError):
                pass
            try:
                stdout, stderr = proc.communicate(timeout=5)
            except subprocess.TimeoutExpired:
                stdout, stderr = "", ""
            return {
                "stdout": stdout or "",
                "stderr": (stderr or "") + f"\nCommand timed out after {timeout}s (process group killed)",
                "exit_code": -1,
            }

    def _handle_read(self, body):
        path = body.get("path", "")
        offset = max(0, int(body.get("offset", 0)))
        limit = min(MAX_LIMIT, max(1, int(body.get("limit", 200))))
        full = _full_path(path)
        with open(full, encoding="utf-8", errors="replace") as f:
            lines = f.readlines()
        total = len(lines)
        chunk = lines[offset:offset + limit]
        content = "".join(f"{offset + i + 1}\t{l}" for i, l in enumerate(chunk))
        return {
            "content": content,
            "total_lines": total,
            "from_line": offset + 1,
            "to_line": offset + len(chunk),
        }

    def _handle_write(self, body):
        path = body.get("path", "")
        content = body.get("content", "")
        full = _full_path(path)
        parent = os.path.dirname(full)
        if parent:
            os.makedirs(parent, exist_ok=True)
        with open(full, "w", encoding="utf-8") as f:
            f.write(content)
        return {"written": True, "path": path}

    def _handle_patch(self, body):
        path = body.get("path", "")
        old_string = body.get("old_string", "")
        new_string = body.get("new_string", "")
        replace_all = bool(body.get("replace_all", False))
        full = _full_path(path)
        with open(full, encoding="utf-8", errors="replace") as f:
            text = f.read()
        if old_string not in text:
            return {"error": f"old_string not found in '{path}'"}
        result = text.replace(old_string, new_string) if replace_all else text.replace(old_string, new_string, 1)
        with open(full, "w", encoding="utf-8") as f:
            f.write(result)
        return {"applied": True, "path": path}

    def _handle_list(self, body):
        path = body.get("path", "")
        full = _full_path(path)
        entries = sorted(os.scandir(full), key=lambda e: (e.is_file(), e.name))
        return {
            "entries": [
                {"name": e.name, "type": "dir" if e.is_dir() else "file",
                 "size": e.stat().st_size if e.is_file() else None}
                for e in entries
            ],
            "path": path,
        }

    def _handle_search(self, body):
        pattern = body.get("pattern", "")
        search_path = body.get("path", ".")
        glob = body.get("glob", "")
        full = _full_path(search_path)
        cmd = ["grep", "-rn", "--include", glob if glob else "*", "--", pattern, full]
        proc = subprocess.run(cmd, capture_output=True, text=True, timeout=30)
        matches = []
        for ln in proc.stdout.splitlines():
            parts = ln.split(":", 2)
            if len(parts) >= 3:
                try:
                    rel = os.path.relpath(parts[0], WORKDIR)
                    matches.append({"file": rel, "line": int(parts[1]), "text": parts[2].strip()})
                except (ValueError, OSError):
                    pass
        return {"matches": matches, "pattern": pattern}


if __name__ == "__main__":
    port = int(os.environ.get("EXECUTOR_PORT", "8080"))
    print(f"[executor] listening on 0.0.0.0:{port} workdir={WORKDIR}", flush=True)
    # ThreadingHTTPServer so a slow/long /run never blocks /health or a concurrent request — a single
    # stuck command must not make the whole sandbox look dead to the orchestrator.
    ThreadingHTTPServer(("0.0.0.0", port), Handler).serve_forever()
