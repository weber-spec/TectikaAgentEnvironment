#!/usr/bin/env python3
"""Live smoke test for the steerable agent tool-loop against Azure AI Foundry.

Verifies the VERIFIED contract end-to-end: tools on the agent definition (flat shape) →
/responses returns a function_call → submit function_call_output into the conversation →
final message. Creates and DELETES a throwaway agent.

Prereqs: `az login` into the subscription that owns proj-agentteam
("Visual Studio Enterprise Subscription - MPN"). Run: python3 tests/live/foundry_loop_smoke.py
"""
import json, subprocess, sys, urllib.request, urllib.error

BASE = "https://aif-agentteam-gbangabggobxy.services.ai.azure.com/api/projects/proj-agentteam"
NAME = "tk-loop-smoke"

def tok():
    return subprocess.check_output(
        ["az", "account", "get-access-token", "--resource", "https://ai.azure.com",
         "--query", "accessToken", "-o", "tsv"], text=True).strip()

H = {"Authorization": f"Bearer {tok()}", "Content-Type": "application/json"}

def call(method, url, body=None):
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(url, data=data, headers=H, method=method)
    try:
        with urllib.request.urlopen(req) as r:
            return r.status, json.loads(r.read().decode() or "{}")
    except urllib.error.HTTPError as e:
        try: return e.code, json.loads(e.read().decode())
        except Exception: return e.code, "(unparseable)"

# Mirror TectikaToolSchema (flat function shape) — a minimal subset is enough to prove the loop.
TOOLS = [
    {"type": "function", "name": "get_board_overview",
     "description": "List every task on this board.",
     "parameters": {"type": "object", "properties": {}, "required": []}},
    {"type": "function", "name": "get_artifact",
     "description": "Get a task's artifact content.",
     "parameters": {"type": "object",
                    "properties": {"taskId": {"type": "string"}, "version": {"type": "integer"}},
                    "required": ["taskId"]}},
]

def main():
    call("DELETE", f"{BASE}/agents/{NAME}?api-version=v1")  # ensure clean slate
    st, _ = call("POST", f"{BASE}/agents?api-version=v1", {
        "name": NAME,
        "definition": {"kind": "prompt", "model": "gpt-4o",
                       "instructions": "You are a helpful agent. Use your tools to inspect the board before answering.",
                       "tools": TOOLS}})
    assert st in (200, 201), f"create agent failed: {st}"

    try:
        _, conv = call("POST", f"{BASE}/conversations?api-version=v1", {})
        conv_id = conv["id"]

        # Round 1 — force a tool call.
        _, r1 = call("POST", f"{BASE}/openai/v1/responses", {
            "input": "Use get_board_overview to see the project, then tell me how many tasks there are.",
            "agent_reference": {"name": NAME, "type": "agent_reference"},
            "conversation": conv_id})
        outs = r1.get("output") or []
        fc = next((o for o in outs if o.get("type") == "function_call"), None)
        assert fc is not None, f"expected function_call, got {[o.get('type') for o in outs]}"
        assert fc["name"] == "get_board_overview", f"unexpected tool {fc['name']}"
        print(f"  round 1: function_call -> {fc['name']} (call_id {fc['call_id']})")

        # Round 2 — submit a canned tool result; expect a final message.
        board = {"boardId": "b1", "boardName": "Smoke", "tasks": [
            {"id": "t1", "title": "A", "status": "Done"}, {"id": "t2", "title": "B", "status": "Backlog"}]}
        _, r2 = call("POST", f"{BASE}/openai/v1/responses", {
            "input": [{"type": "function_call_output", "call_id": fc["call_id"], "output": json.dumps(board)}],
            "agent_reference": {"name": NAME, "type": "agent_reference"},
            "conversation": conv_id})
        text = ""
        for o in (r2.get("output") or []):
            if o.get("type") == "message":
                for c in (o.get("content") or []):
                    if c.get("type") == "output_text":
                        text += c.get("text", "")
        assert text.strip(), f"expected final text, got {r2.get('output')}"
        print(f"  round 2: final message -> {text[:120]!r}")
        print("LOOP OK")
    finally:
        st, _ = call("DELETE", f"{BASE}/agents/{NAME}?api-version=v1")
        print(f"  cleanup: deleted agent (HTTP {st})")

if __name__ == "__main__":
    try:
        main()
    except AssertionError as e:
        print("SMOKE FAILED:", e); sys.exit(1)
