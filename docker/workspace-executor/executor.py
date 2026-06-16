#!/usr/bin/env python3
"""
Tiny HTTP executor running inside the ACI workspace container.

POST /run   {"cmd": "...", "timeout": 60}  -> {"stdout":"...","stderr":"...","exit_code":0}
GET  /health                               -> 200 "ok"

Auth: Authorization: Bearer <EXECUTOR_TOKEN>  (env var; if empty, auth is skipped for dev)
All commands run with cwd=/workspace (the cloned repo).
"""
import os
import json
import subprocess
from http.server import HTTPServer, BaseHTTPRequestHandler

TOKEN = os.environ.get("EXECUTOR_TOKEN", "")
WORKDIR = "/workspace"


class Handler(BaseHTTPRequestHandler):
    def log_message(self, fmt, *args):
        pass  # suppress default access log noise

    def _auth_ok(self):
        if not TOKEN:
            return True
        return self.headers.get("Authorization", "") == f"Bearer {TOKEN}"

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

        if self.path != "/run":
            self.send_response(404)
            self.end_headers()
            return

        length = int(self.headers.get("Content-Length", 0))
        body = json.loads(self.rfile.read(length) or b"{}")
        cmd = body.get("cmd", "")
        timeout = int(body.get("timeout", 60))

        try:
            proc = subprocess.run(
                cmd,
                shell=True,
                capture_output=True,
                text=True,
                timeout=timeout,
                cwd=WORKDIR,
            )
            resp = {
                "stdout": proc.stdout,
                "stderr": proc.stderr,
                "exit_code": proc.returncode,
            }
        except subprocess.TimeoutExpired:
            resp = {"stdout": "", "stderr": f"Command timed out after {timeout}s", "exit_code": -1}
        except Exception as ex:
            resp = {"stdout": "", "stderr": str(ex), "exit_code": -2}

        payload = json.dumps(resp).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)


if __name__ == "__main__":
    port = int(os.environ.get("EXECUTOR_PORT", "8080"))
    print(f"[executor] listening on 0.0.0.0:{port} workdir={WORKDIR}", flush=True)
    HTTPServer(("0.0.0.0", port), Handler).serve_forever()
