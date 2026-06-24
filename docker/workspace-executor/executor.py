#!/usr/bin/env python3
"""
HTTP executor running inside the ACI workspace container.

POST /run             {"cmd": "...", "timeout": 60, "run_id": "..."}  (run_id optional)
                      -> {"stdout":"...","stderr":"...","exit_code":0}
POST /read            {"path": "src/main.cs", "offset": 0, "limit": 200, "run_id": "..."}
                      -> {"content":"1\t...\n2\t...","total_lines":N,"from_line":1,"to_line":N}
POST /write           {"path": "src/foo.cs", "content": "...", "run_id": "..."}
                      -> {"written": true, "path": "src/foo.cs"}
POST /patch           {"path": "src/foo.cs", "old_string": "...", "new_string": "...", "replace_all": false, "run_id": "..."}
                      -> {"applied": true, "path": "src/foo.cs"} | {"error": "old_string not found in 'src/foo.cs'"}
POST /list            {"path": "src/", "run_id": "..."}
                      -> {"entries": [{"name":"foo.cs","type":"file","size":1234},...], "path":"src/"}
POST /search          {"pattern": "class Auth", "path": "src/", "glob": "*.cs", "run_id": "..."}
                      -> {"matches": [{"file":"src/Auth.cs","line":12,"text":"class AuthService"},...]}
POST /worktree/add    {"run_id": "abc12345", "branch": "agent/abc12345", "can_push": true}
                      -> {"created": true, "path": "/workspace/runs/abc12345", "branch": "agent/abc12345"}
POST /worktree/remove {"run_id": "abc12345"}
                      -> {"removed": true}
GET  /worktree/list                         -> {"worktrees": [{"run_id":"...","path":"...","branch":"..."},...]}
GET  /health                                -> 200 "ok"

Auth: Authorization: Bearer <EXECUTOR_TOKEN>  (env var; if empty, auth is skipped for dev)
File operations are scoped to the run's worktree (/workspace/runs/{run_id}) or /workspace/main.
"""
import os
import json
import signal
import subprocess
from http.server import ThreadingHTTPServer, BaseHTTPRequestHandler

TOKEN = os.environ.get("EXECUTOR_TOKEN", "")
WORKSPACE_ROOT = "/workspace"
WORKSPACE_MAIN = "/workspace/main"
WORKSPACE_RUNS = "/workspace/runs"
MAX_LIMIT = 500


def _resolve_workdir(body: dict) -> str:
    """Return the working directory for this request.
    With run_id → /workspace/runs/{run_id} (must already exist — worktree was created by /worktree/add).
    Without run_id → /workspace/main.
    """
    run_id = body.get("run_id", "")
    if run_id:
        path = os.path.join(WORKSPACE_RUNS, run_id)
        if not os.path.isdir(path):
            raise ValueError(f"Worktree for run_id '{run_id}' does not exist at {path}. Call /worktree/add first.")
        return path
    return WORKSPACE_MAIN


def _full_path(rel: str, workdir: str) -> str:
    """Resolve a workspace-relative path, rejecting traversal outside workdir."""
    clean = os.path.normpath(rel.lstrip("/")) if rel else "."
    full = os.path.join(workdir, clean)
    if not os.path.abspath(full).startswith(os.path.abspath(workdir)):
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
        if not self._auth_ok():
            self.send_response(401)
            self.end_headers()
            return
        if self.path == "/health":
            self.send_response(200)
            self.end_headers()
            self.wfile.write(b"ok")
        elif self.path == "/worktree/list":
            try:
                self._json_response(self._handle_worktree_list())
            except Exception as ex:
                self._json_response({"error": str(ex)}, 500)
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
            "/run":            self._handle_run,
            "/read":           self._handle_read,
            "/write":          self._handle_write,
            "/patch":          self._handle_patch,
            "/list":           self._handle_list,
            "/search":         self._handle_search,
            "/worktree/add":   self._handle_worktree_add,
            "/worktree/remove": self._handle_worktree_remove,
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

    # ── File/shell handlers ────────────────────────────────────────────────────

    def _handle_run(self, body):
        cmd = body.get("cmd", "")
        timeout = int(body.get("timeout", 60))
        workdir = _resolve_workdir(body)
        # stdin=DEVNULL makes the sandbox genuinely non-interactive: any read from stdin gets EOF
        # immediately instead of blocking forever. start_new_session puts the command in its own
        # process group so a timeout kills the entire tree, not just the shell.
        try:
            proc = subprocess.Popen(
                cmd, shell=True, cwd=workdir,
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
        workdir = _resolve_workdir(body)
        full = _full_path(path, workdir)
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
        workdir = _resolve_workdir(body)
        full = _full_path(path, workdir)
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
        workdir = _resolve_workdir(body)
        full = _full_path(path, workdir)
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
        workdir = _resolve_workdir(body)
        full = _full_path(path, workdir)
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
        workdir = _resolve_workdir(body)
        full = _full_path(search_path, workdir)
        cmd = ["grep", "-rn", "--include", glob if glob else "*", "--", pattern, full]
        proc = subprocess.run(cmd, capture_output=True, text=True, timeout=30)
        matches = []
        for ln in proc.stdout.splitlines():
            parts = ln.split(":", 2)
            if len(parts) >= 3:
                try:
                    rel = os.path.relpath(parts[0], workdir)
                    matches.append({"file": rel, "line": int(parts[1]), "text": parts[2].strip()})
                except (ValueError, OSError):
                    pass
        return {"matches": matches, "pattern": pattern}

    # ── Worktree handlers ─────────────────────────────────────────────────────

    def _handle_worktree_add(self, body):
        run_id = body.get("run_id", "")
        if not run_id:
            raise ValueError("run_id is required")
        branch = body.get("branch", f"agent/{run_id}")
        can_push = bool(body.get("can_push", True))

        worktree_path = os.path.join(WORKSPACE_RUNS, run_id)

        # idempotent — if worktree already exists, just return success
        if os.path.isdir(worktree_path):
            return {"created": False, "already_exists": True, "path": worktree_path, "branch": branch}

        os.makedirs(WORKSPACE_RUNS, exist_ok=True)

        proc = subprocess.run(
            ["git", "worktree", "add", worktree_path, "-b", branch],
            capture_output=True, text=True, cwd=WORKSPACE_MAIN,
        )
        if proc.returncode != 0:
            raise RuntimeError(f"git worktree add failed: {proc.stderr.strip()}")

        if not can_push:
            subprocess.run(
                ["git", "remote", "set-url", "--push", "origin", "no-push://disabled"],
                cwd=worktree_path,
            )

        return {"created": True, "path": worktree_path, "branch": branch}

    def _handle_worktree_remove(self, body):
        run_id = body.get("run_id", "")
        if not run_id:
            raise ValueError("run_id is required")

        worktree_path = os.path.join(WORKSPACE_RUNS, run_id)

        # idempotent — if already gone, return success
        if not os.path.isdir(worktree_path):
            return {"removed": False, "already_gone": True}

        proc = subprocess.run(
            ["git", "worktree", "remove", "--force", worktree_path],
            capture_output=True, text=True, cwd=WORKSPACE_MAIN,
        )
        if proc.returncode != 0:
            # fallback: prune + rm if worktree is in a broken state
            subprocess.run(["git", "worktree", "prune"], cwd=WORKSPACE_MAIN)
            import shutil
            shutil.rmtree(worktree_path, ignore_errors=True)

        return {"removed": True}

    def _handle_worktree_list(self):
        if not os.path.isdir(WORKSPACE_MAIN):
            return {"worktrees": []}
        proc = subprocess.run(
            ["git", "worktree", "list", "--porcelain"],
            capture_output=True, text=True, cwd=WORKSPACE_MAIN,
        )
        worktrees = []
        current = {}
        for line in proc.stdout.splitlines():
            if line.startswith("worktree "):
                if current:
                    worktrees.append(current)
                current = {"path": line[len("worktree "):]}
            elif line.startswith("branch "):
                current["branch"] = line[len("branch "):]
        if current:
            worktrees.append(current)

        result = []
        for wt in worktrees:
            path = wt.get("path", "")
            if path.startswith(WORKSPACE_RUNS + "/"):
                run_id = path[len(WORKSPACE_RUNS) + 1:]
                result.append({
                    "run_id": run_id,
                    "path": path,
                    "branch": wt.get("branch", ""),
                })
        return {"worktrees": result}


if __name__ == "__main__":
    port = int(os.environ.get("EXECUTOR_PORT", "8080"))
    print(f"[executor] listening on 0.0.0.0:{port} workdir={WORKSPACE_ROOT}", flush=True)
    # ThreadingHTTPServer so a slow/long /run never blocks /health or a concurrent request — a single
    # stuck command must not make the whole sandbox look dead to the orchestrator.
    ThreadingHTTPServer(("0.0.0.0", port), Handler).serve_forever()
