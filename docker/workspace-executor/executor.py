#!/usr/bin/env python3
"""
HTTP executor running inside the ACI workspace container.

POST /run             {"cmd": "...", "timeout": 60, "run_id": "..."}  (run_id optional)
                      -> {"stdout":"...","stderr":"...","exit_code":0}
POST /read            {"path": "src/main.cs", "offset": 0, "limit": 200, "run_id": "..."}
                      -> {"content":"<exact raw file text>","total_lines":N,"from_line":1,"to_line":N}
                      content is the file's EXACT bytes (no line-number prefixes, leading BOM stripped) so
                      it can be copied verbatim into /patch old_string; line positions live in the envelope.
POST /write           {"path": "src/foo.cs", "content": "...", "run_id": "..."}
                      -> {"written": true, "path": "src/foo.cs"}
POST /patch           {"path": "src/foo.cs", "old_string": "...", "new_string": "...", "replace_all": false, "run_id": "..."}
                      -> {"applied": true, "path": "src/foo.cs"} | {"error": "old_string not found in 'src/foo.cs'"}
POST /list            {"path": "src/", "run_id": "..."}
                      -> {"entries": [{"name":"foo.cs","type":"file","size":1234},...], "path":"src/"}
POST /search          {"pattern": "class Auth", "path": "src/", "glob": "*.cs", "run_id": "..."}
                      -> {"matches": [{"file":"src/Auth.cs","line":12,"text":"class AuthService"},...]}
POST /worktree/add    {"run_id": "abc12345", "branch": "agent/abc12345", "can_push": true, "base_ref": "main"}
                      -> {"created": true, "path": "/workspace/runs/abc12345", "branch": "agent/abc12345"}
                      base_ref (optional) = branch to fork from; refreshed from origin first. Defaults to
                      the main clone's current branch (the repo default).
POST /worktree/remove {"run_id": "abc12345"}
                      -> {"removed": true}
POST /worktree/merge  {"run_id": "abc12345"}   (no-repo boards: fold the run branch into local main)
                      -> {"merged": true, "commit": "<sha>"}
                       | {"merged": false, "conflict": true, "files": ["a.cs", ...], "detail": "..."}
POST /bundle          {}   (no-repo durable snapshot: git-bundle /workspace/main)
                      -> {"bundle": "<base64>", "bytes": N}
POST /restore         {"bundle": "<base64>"}   (restore /workspace/main from a bundle on provision)
                      -> {"restored": true, "branch": "main"}
GET  /worktree/list                         -> {"worktrees": [{"run_id":"...","path":"...","branch":"..."},...]}
POST /job/start       {"cmd":"...","run_id":"...","job_id":"...","env":{"ANTHROPIC_API_KEY":"..."}}
                      -> {"started": true, "job_id":"..."}   (detached; no timeout-kill — for `claude -p`)
                      env is injected into the CHILD process only (never this server's environ).
POST /job/status      {"job_id":"..."}      -> {"status":"running"|"exited"|"unknown","exit_code"?:N}
POST /job/result      {"job_id":"...","run_id":"..."}  -> {"stdout":"...","stderr":"...","exit_code":N}
GET  /health                                -> 200 "ok"

Auth: Authorization: Bearer <EXECUTOR_TOKEN>  (env var; if empty, auth is skipped for dev)
File operations are scoped to the run's worktree (/workspace/runs/{run_id}) or /workspace/main.
"""
import os
import json
import signal
import subprocess
import threading
import base64
import shutil
import tempfile
from http.server import ThreadingHTTPServer, BaseHTTPRequestHandler

TOKEN = os.environ.get("EXECUTOR_TOKEN", "")
WORKSPACE_ROOT = "/workspace"
WORKSPACE_MAIN = "/workspace/main"
WORKSPACE_RUNS = "/workspace/runs"
MAX_LIMIT = 500
_MERGE_LOCK = threading.Lock()   # serialize merges into /workspace/main (one container per board)

# Detached long-running jobs (e.g. `claude -p`), keyed by job_id. The synchronous /run path SIGKILLs on
# its timeout, so it can't host a multi-minute agent run; /job/* spawns a detached process, captures its
# stdout/stderr to files, and lets the caller poll. job_id → {"proc", "out", "err"}.
_JOBS = {}
_JOBS_LOCK = threading.Lock()


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


def _current_branch(repo_dir: str):
    """Current branch of the main clone (the repo's default branch after a fresh clone), or None."""
    if not os.path.isdir(repo_dir):
        return None
    proc = subprocess.run(
        ["git", "rev-parse", "--abbrev-ref", "HEAD"],
        capture_output=True, text=True, cwd=repo_dir,
    )
    name = proc.stdout.strip()
    return name if proc.returncode == 0 and name and name != "HEAD" else None


def _refresh_base(base_ref: str):
    """Fast-forward /workspace/main's local <base_ref> to origin so new worktrees fork from the latest
    integrated state. Best-effort — a fetch failure leaves the existing local tip in place rather than
    aborting worktree creation. Safe because /workspace/main is never edited directly."""
    if not os.path.isdir(WORKSPACE_MAIN):
        return
    fetch = subprocess.run(
        ["git", "fetch", "origin", base_ref],
        capture_output=True, text=True, cwd=WORKSPACE_MAIN,
    )
    if fetch.returncode != 0:
        print(f"[worktree] base refresh: fetch origin {base_ref} failed: {fetch.stderr.strip()}", flush=True)
        return
    subprocess.run(["git", "checkout", base_ref], capture_output=True, text=True, cwd=WORKSPACE_MAIN)
    subprocess.run(["git", "reset", "--hard", f"origin/{base_ref}"], capture_output=True, text=True, cwd=WORKSPACE_MAIN)


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
            "/worktree/merge": self._handle_worktree_merge,
            "/bundle":  self._handle_bundle,
            "/restore": self._handle_restore,
            "/job/start":  self._handle_job_start,
            "/job/status": self._handle_job_status,
            "/job/result": self._handle_job_result,
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

    # ── Detached job handlers (long-running agents like `claude -p`) ───────────

    def _handle_job_start(self, body):
        cmd = body.get("cmd", "")
        job_id = body.get("job_id", "")
        env_extra = body.get("env") or {}
        if not job_id:
            return {"error": "job_id is required"}
        workdir = _resolve_workdir(body)  # raises if the run's worktree is missing
        job_dir = os.path.join(workdir, ".claude-job")
        os.makedirs(job_dir, exist_ok=True)
        out_path = os.path.join(job_dir, f"{job_id}.out")
        err_path = os.path.join(job_dir, f"{job_id}.err")

        # Per-invocation secrets (e.g. ANTHROPIC_API_KEY) are injected into the CHILD env only — never into
        # this server's os.environ — so they can't leak to a later /run on the shared container.
        child_env = {**os.environ, **{str(k): str(v) for k, v in env_extra.items()}}

        out_f = open(out_path, "w")
        err_f = open(err_path, "w")
        try:
            # Detached: no communicate(), no timeout-kill. stdin=DEVNULL keeps it non-interactive;
            # start_new_session isolates the process group. stdout/stderr go to SEPARATE files so the
            # JSON envelope on stdout stays clean (stderr noise doesn't corrupt it).
            proc = subprocess.Popen(
                cmd, shell=True, cwd=workdir,
                stdin=subprocess.DEVNULL, stdout=out_f, stderr=err_f,
                text=True, start_new_session=True, env=child_env,
            )
        except Exception as ex:
            out_f.close(); err_f.close()
            return {"started": False, "error": f"failed to start job: {ex}"}

        with _JOBS_LOCK:
            _JOBS[job_id] = {"proc": proc, "out": out_path, "err": err_path, "out_f": out_f, "err_f": err_f}
        return {"started": True, "job_id": job_id}

    def _handle_job_status(self, body):
        job_id = body.get("job_id", "")
        with _JOBS_LOCK:
            job = _JOBS.get(job_id)
        if job is None:
            return {"status": "unknown"}
        rc = job["proc"].poll()
        if rc is None:
            return {"status": "running"}
        return {"status": "exited", "exit_code": rc}

    def _handle_job_result(self, body):
        job_id = body.get("job_id", "")
        with _JOBS_LOCK:
            job = _JOBS.get(job_id)
        if job is None:
            return {"error": f"unknown job_id '{job_id}'"}
        rc = job["proc"].poll()
        if rc is None:
            return {"error": "job still running"}
        # Flush + close the redirect handles, then read the captured streams.
        for key in ("out_f", "err_f"):
            try: job[key].close()
            except Exception: pass
        stdout = stderr = ""
        try:
            with open(job["out"], encoding="utf-8", errors="replace") as f:
                stdout = f.read()
        except OSError:
            pass
        try:
            with open(job["err"], encoding="utf-8", errors="replace") as f:
                stderr = f.read()
        except OSError:
            pass
        with _JOBS_LOCK:
            _JOBS.pop(job_id, None)
        return {"stdout": stdout, "stderr": stderr, "exit_code": rc}

    def _handle_read(self, body):
        path = body.get("path", "")
        offset = max(0, int(body.get("offset", 0)))
        limit = min(MAX_LIMIT, max(1, int(body.get("limit", 200))))
        workdir = _resolve_workdir(body)
        full = _full_path(path, workdir)
        # utf-8-sig strips a leading BOM so `content` is the file's true text; return it RAW (no
        # "N\t" line-number prefixes) so the model can copy it verbatim into edit_file/old_string.
        # Line positions are reported in the envelope (from_line/to_line/total_lines), not inline.
        with open(full, encoding="utf-8-sig", errors="replace") as f:
            lines = f.readlines()
        total = len(lines)
        chunk = lines[offset:offset + limit]
        return {
            "content": "".join(chunk),
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
        # Match against the same view read_file shows: BOM stripped (decoded as utf-8-sig). Preserve the
        # file's original BOM on write so we don't silently change its encoding.
        with open(full, "rb") as f:
            raw = f.read()
        had_bom = raw.startswith(b"\xef\xbb\xbf")
        text = raw.decode("utf-8-sig" if had_bom else "utf-8", errors="replace")
        if old_string not in text:
            # Backstop: tolerate a stray leading BOM the model may have carried into old_string.
            stripped = old_string.lstrip("\ufeff")
            if stripped and stripped in text:
                old_string = stripped
            else:
                return {"error": f"old_string not found in '{path}'. Match against the EXACT text "
                        f"read_file returns — raw file content, no 'N\\t' line-number prefixes and no "
                        f"leading byte-order mark. Re-read the file and copy the target text verbatim, "
                        f"including indentation and whitespace."}
        result = text.replace(old_string, new_string) if replace_all else text.replace(old_string, new_string, 1)
        with open(full, "w", encoding="utf-8-sig" if had_bom else "utf-8") as f:
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
        base_ref = body.get("base_ref") or _current_branch(WORKSPACE_MAIN) or "main"

        worktree_path = os.path.join(WORKSPACE_RUNS, run_id)

        # idempotent — if worktree already exists, just return success
        if os.path.isdir(worktree_path):
            return {"created": False, "already_exists": True, "path": worktree_path, "branch": branch}

        os.makedirs(WORKSPACE_RUNS, exist_ok=True)

        # Refresh the base branch from origin BEFORE forking the worktree. Completed upstream tasks are
        # merged into the default branch server-side, but this container's local clone goes stale; without
        # this the new worktree would fork from an outdated base and never see upstream deliverables.
        # /workspace/main is never edited directly (all work happens in per-run worktrees), so a hard reset
        # to origin/<base> is safe. Best-effort: a fetch blip falls back to the current local base tip.
        _refresh_base(base_ref)

        proc = subprocess.run(
            ["git", "worktree", "add", worktree_path, "-b", branch, base_ref],
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

    def _handle_worktree_merge(self, body):
        run_id = body.get("run_id", "")
        if not run_id:
            return {"error": "run_id required"}
        branch = f"agent/{run_id}"
        worktree = os.path.join(WORKSPACE_RUNS, run_id)

        # Serialize merges into /workspace/main — one container per board ⇒ one process, so a process
        # lock is sufficient to prevent two completing runs racing on the shared index.
        with _MERGE_LOCK:
            # Commit any uncommitted work in the run's worktree so its branch is complete.
            if os.path.isdir(worktree):
                subprocess.run(["git", "add", "-A"], cwd=worktree)
                subprocess.run(["git", "commit", "-m", f"agent run {run_id}"], cwd=worktree)  # no-op if nothing staged

            # Merge into whatever branch /workspace/main is on (main or master).
            target = subprocess.run(
                ["git", "branch", "--show-current"],
                cwd=WORKSPACE_MAIN, capture_output=True, text=True).stdout.strip() or "main"
            merge = subprocess.run(
                ["git", "merge", "--no-ff", "-m", f"merge {branch}", branch],
                cwd=WORKSPACE_MAIN, capture_output=True, text=True)

            if merge.returncode != 0:
                files = subprocess.run(
                    ["git", "diff", "--name-only", "--diff-filter=U"],
                    cwd=WORKSPACE_MAIN, capture_output=True, text=True).stdout.split()
                subprocess.run(["git", "merge", "--abort"], cwd=WORKSPACE_MAIN)
                return {"merged": False, "conflict": True, "files": files,
                        "detail": (merge.stderr or merge.stdout).strip()[:500]}

            commit = subprocess.run(
                ["git", "rev-parse", "HEAD"],
                cwd=WORKSPACE_MAIN, capture_output=True, text=True).stdout.strip()
            # Push the advanced main line if a remote exists (best-effort; local main is the truth).
            if subprocess.run(["git", "remote"], cwd=WORKSPACE_MAIN, capture_output=True, text=True).stdout.strip():
                push = subprocess.run(
                    ["git", "push", "origin", f"HEAD:refs/heads/{target}"],
                    cwd=WORKSPACE_MAIN, capture_output=True, text=True)
                if push.returncode != 0:
                    print(f"[worktree/merge] push failed: {push.stderr.strip()}", flush=True)
            return {"merged": True, "commit": commit}

    def _handle_bundle(self, body):
        """Durable snapshot (no-repo boards): git-bundle ALL refs of /workspace/main and return the bytes
        (base64). A workflow activity uploads this to blob storage so the board's files survive ACI destroy."""
        if not os.path.isdir(os.path.join(WORKSPACE_MAIN, ".git")):
            return {"error": "no git repo at /workspace/main"}
        fd, path = tempfile.mkstemp(suffix=".bundle")
        os.close(fd)
        try:
            with _MERGE_LOCK:   # don't bundle mid-merge
                proc = subprocess.run(["git", "bundle", "create", path, "--all"],
                                      cwd=WORKSPACE_MAIN, capture_output=True, text=True)
            if proc.returncode != 0:
                return {"error": f"git bundle failed: {(proc.stderr or proc.stdout).strip()[:300]}"}
            with open(path, "rb") as f:
                data = f.read()
            return {"bundle": base64.b64encode(data).decode(), "bytes": len(data)}
        finally:
            os.remove(path)

    def _handle_restore(self, body):
        """Restore /workspace/main from a git bundle (base64), replacing the empty init created on
        provision. Used on a no-repo board's first run after its ACI was recycled."""
        b64 = body.get("bundle", "")
        if not b64:
            return {"error": "bundle (base64) required"}
        fd, path = tempfile.mkstemp(suffix=".bundle")
        os.close(fd)
        restored_dir = os.path.join(WORKSPACE_ROOT, ".restore_tmp")
        try:
            with open(path, "wb") as f:
                f.write(base64.b64decode(b64))
            # verify needs a git-repo cwd; /workspace/main is always one (entrypoint git-inits it).
            if not os.path.isdir(os.path.join(WORKSPACE_MAIN, ".git")) or \
               subprocess.run(["git", "bundle", "verify", path], cwd=WORKSPACE_MAIN,
                              capture_output=True, text=True).returncode != 0:
                return {"error": "invalid bundle"}
            with _MERGE_LOCK:
                shutil.rmtree(restored_dir, ignore_errors=True)
                clone = subprocess.run(["git", "clone", path, restored_dir], capture_output=True, text=True)
                if clone.returncode != 0:
                    return {"error": f"git clone from bundle failed: {clone.stderr.strip()[:300]}"}
                subprocess.run(["git", "remote", "remove", "origin"], cwd=restored_dir, capture_output=True, text=True)
                shutil.rmtree(WORKSPACE_MAIN, ignore_errors=True)
                shutil.move(restored_dir, WORKSPACE_MAIN)
            branch = subprocess.run(["git", "branch", "--show-current"],
                                    cwd=WORKSPACE_MAIN, capture_output=True, text=True).stdout.strip()
            return {"restored": True, "branch": branch}
        finally:
            os.remove(path)
            shutil.rmtree(restored_dir, ignore_errors=True)

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
