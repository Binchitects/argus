"""
deploy/services/sandbox/arena: the chat's tools as Python functions for a program run_code runs, each call one JSON
line to the runner's socket, against a fake of that socket.
"""
from __future__ import annotations

import json
import os
import socket
import subprocess
import sys
import tempfile
import threading
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
PACKAGE = HERE.parents[1] / "deploy" / "services" / "sandbox"

DEFINITIONS = [
    {"name": "find_symbol", "group": "argus", "description": "Find where a symbol is defined.\nMore words.",
     "parameters": {"type": "object", "properties": {"name": {"type": "string", "description": "The symbol"}, "repo": {"type": "string"}}, "required": ["name"]}},
    {"name": "web_search", "group": "web", "description": "Search the web.", "parameters": {"type": "object", "properties": {"query": {"type": "string"}}, "required": ["query"]}},
]


class FakeRelay:
    """The runner's socket: answers each call line with what answer(name, args) says, and keeps what was asked."""

    def __init__(self, path: str, answer):
        self.asked: list[dict] = []
        self.sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        self.sock.bind(path)
        self.sock.listen(1)
        self.answer = answer
        threading.Thread(target=self.serve, daemon=True).start()

    def serve(self):
        conn, _ = self.sock.accept()
        with conn, conn.makefile("rwb") as f:
            for line in f:
                asked = json.loads(line)
                self.asked.append(asked)
                f.write((json.dumps(self.answer(asked["name"], asked["args"])) + "\n").encode())
                f.flush()


def run(program: str, answer) -> tuple[subprocess.CompletedProcess, FakeRelay]:
    work = tempfile.mkdtemp()
    Path(work, ".arena-tools.json").write_text(json.dumps(DEFINITIONS))
    relay = FakeRelay(os.path.join(work, ".arena.sock"), answer)
    env = {"PATH": os.environ["PATH"], "ARENA_SOCKET": os.path.join(work, ".arena.sock")}
    code = f"import sys; sys.path.insert(0, {str(PACKAGE)!r})\n" + program
    return subprocess.run([sys.executable, "-I", "-c", code], cwd=work, env=env, capture_output=True, text=True, timeout=30), relay


class ArenaModule(unittest.TestCase):
    def test_The_tools_are_functions_by_group_found_by_their_words_and_described_by_help(self):
        r, _ = run("""
import arena, inspect
print(arena.tools.search("symbol defined"))
print(inspect.signature(arena.argus.find_symbol))
print(arena.argus.find_symbol.__doc__.splitlines()[0])
print(len(arena.tools.list()))
""", lambda n, a: {"ok": True, "result": ""})
        self.assertEqual(0, r.returncode, r.stderr)
        lines = r.stdout.splitlines()
        self.assertEqual("['arena.argus.find_symbol: Find where a symbol is defined.']", lines[0])
        self.assertEqual("(*, name, repo=None)", lines[1])
        self.assertEqual("Find where a symbol is defined.", lines[2])
        self.assertEqual("2", lines[3])

    def test_A_call_goes_to_the_chat_and_its_JSON_comes_back_parsed_and_a_refusal_raises_its_words(self):
        def answer(name, args):
            if name == "web_search":
                return {"ok": False, "error": "The person's rules refuse this search."}
            return {"ok": True, "result": json.dumps({"defined": [{"path": "src/decode.c", "line": 118}]})}
        r, relay = run("""
import arena
hits = arena.argus.find_symbol(name="DecodeFrame")
print(hits["defined"][0]["path"])
try:
    arena.web.web_search(query="secret")
except arena.ToolError as e:
    print("refused:", e)
try:
    arena.argus.find_symbol()
except arena.ToolError as e:
    print("needs:", e)
""", answer)
        self.assertEqual(0, r.returncode, r.stderr)
        self.assertEqual(["src/decode.c", "refused: The person's rules refuse this search.", "needs: find_symbol needs name."], r.stdout.splitlines())
        self.assertEqual([{"name": "find_symbol", "args": {"name": "DecodeFrame"}}, {"name": "web_search", "args": {"query": "secret"}}], relay.asked)

    def test_Outside_run_code_a_call_says_why_it_cannot(self):
        r = subprocess.run([sys.executable, "-I", "-c", f"import sys; sys.path.insert(0, {str(PACKAGE)!r})\nimport arena\ntry:\n    arena.call('x')\nexcept arena.ToolError as e:\n    print(e)"],
                           cwd=tempfile.mkdtemp(), env={"PATH": os.environ["PATH"]}, capture_output=True, text=True, timeout=30)
        self.assertIn("it was not run by run_code", r.stdout)


if __name__ == "__main__":
    unittest.main()
