"""A plugin's own MCP server in the sandbox: the helper authors write it with
(sandbox_tools/mcp_server.py), and one call as the app runs it there
(Chat/Tools/SandboxServerTool.cs: initialize and the request on its input, its
answers on its output)."""

from __future__ import annotations

import io
import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

from support import REPO

SANDBOX = REPO / "deploy" / "services" / "sandbox"
sys.path.insert(0, str(SANDBOX))

from sandbox_tools.mcp_server import Server  # noqa: E402

SERVER = '''
from sandbox_tools.mcp_server import Server
from lengths import FEET

server = Server("units", instructions="Lengths between metres and feet.")

@server.tool("Converts a length.", {"value": {"type": "number"}, "unit": {"type": "string", "enum": ["m", "ft"]}})
def convert(value, unit):
    digits = int(server.settings.get("precision", "2"))
    return f"{value * FEET:.{digits}f} ft" if unit == "m" else f"{value / FEET:.{digits}f} m"

@server.tool("Fails.")
def boom():
    raise ValueError("no")

server.run()
'''


def job(request: dict) -> str:
    """The job as SandboxServerTool.Job writes it."""
    messages = [
        {"jsonrpc": "2.0", "id": 1, "method": "initialize",
         "params": {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "arena", "version": "1"}}},
        {"jsonrpc": "2.0", "method": "notifications/initialized"},
        request,
    ]
    data = "".join(json.dumps(m, separators=(",", ":")) + "\n" for m in messages)
    return ("import io, os, runpy, sys\n"
            "sys.path.insert(0, os.getcwd())\n"
            f"sys.stdin = io.StringIO({json.dumps(data)})\n"
            "sys.argv = ['server.py']\n"
            "runpy.run_path('server.py', run_name='__main__')\n")


class McpServerTests(unittest.TestCase):
    def test_lists_calls_and_says_what_failed(self) -> None:
        server = Server("t", instructions="Hi.")

        @server.tool("Adds.", {"a": {"type": "number"}, "b": {"type": "number"}})
        def add(a, b):
            return {"sum": a + b}

        @server.tool("Fails.")
        def boom():
            raise RuntimeError("bad")

        out = io.StringIO()
        server.run(io.StringIO("\n".join(json.dumps(m) for m in [
            {"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {}},
            {"jsonrpc": "2.0", "method": "notifications/initialized"},
            {"jsonrpc": "2.0", "id": 2, "method": "tools/list"},
            {"jsonrpc": "2.0", "id": 3, "method": "tools/call", "params": {"name": "add", "arguments": {"a": 2, "b": 3}}},
            {"jsonrpc": "2.0", "id": 4, "method": "tools/call", "params": {"name": "boom", "arguments": {}}},
            {"jsonrpc": "2.0", "id": 5, "method": "tools/call", "params": {"name": "nothing"}},
            {"jsonrpc": "2.0", "id": 6, "method": "resources/list"},
        ]) + "\nnot json\n"), out)
        answers = [json.loads(line) for line in out.getvalue().splitlines()]
        by_id = {a["id"]: a for a in answers}
        self.assertEqual("Hi.", by_id[1]["result"]["instructions"])
        self.assertEqual(["add", "boom"], [t["name"] for t in by_id[2]["result"]["tools"]])
        self.assertEqual(["a", "b"], by_id[2]["result"]["tools"][0]["inputSchema"]["required"])
        self.assertEqual('{"sum": 5}', by_id[3]["result"]["content"][0]["text"])
        self.assertEqual((True, "RuntimeError: bad"), (by_id[4]["result"]["isError"], by_id[4]["result"]["content"][0]["text"]))
        self.assertEqual(-32602, by_id[5]["error"]["code"])
        self.assertEqual(-32601, by_id[6]["error"]["code"])
        self.assertEqual(-32700, by_id[None]["error"]["code"])
        self.assertEqual(7, len(answers))

    def test_one_call_as_the_app_runs_it_in_the_sandbox(self) -> None:
        with tempfile.TemporaryDirectory() as work:
            Path(work, "server.py").write_text(SERVER, encoding="utf-8")
            Path(work, "lengths.py").write_text("FEET = 3.28084\n", encoding="utf-8")
            Path(work, "settings.json").write_text('{"precision": "3"}', encoding="utf-8")
            env = {**os.environ, "PYTHONPATH": str(SANDBOX)}

            def ask(request: dict) -> dict:
                done = subprocess.run([sys.executable, "-"], input=job(request), capture_output=True, text=True, cwd=work, env=env, timeout=60, check=True)
                answers = {a.get("id"): a for a in map(json.loads, done.stdout.splitlines())}
                self.assertIn("Lengths", answers[1]["result"]["instructions"])
                return answers[2]

            listed = ask({"jsonrpc": "2.0", "id": 2, "method": "tools/list"})
            self.assertEqual(["convert", "boom"], [t["name"] for t in listed["result"]["tools"]])
            called = ask({"jsonrpc": "2.0", "id": 2, "method": "tools/call", "params": {"name": "convert", "arguments": {"value": 2, "unit": "m"}}})
            self.assertEqual("6.562 ft", called["result"]["content"][0]["text"])
            failed = ask({"jsonrpc": "2.0", "id": 2, "method": "tools/call", "params": {"name": "boom", "arguments": {}}})
            self.assertTrue(failed["result"]["isError"])


if __name__ == "__main__":
    unittest.main()
