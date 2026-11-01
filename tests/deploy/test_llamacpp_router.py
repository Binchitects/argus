"""
deploy/services/llamacpp/router.sh, run by /bin/sh with a fake llama-server: an admin's restart file (a new gen=)
starts the server again, as a change to the model list does, and a server that ignores TERM is killed once the
drain is over, so that it never holds the router.
"""
from __future__ import annotations

import os
import re
import shutil
import signal
import socket
import subprocess
import tempfile
import time
import unittest
from pathlib import Path

SCRIPT = Path(__file__).resolve().parents[2] / "deploy" / "services" / "llamacpp" / "router.sh"

# The fake llama-server: its pid, a line each start; /health and /models answered; TERM ends it, unless
# FAKE_DIR/stubborn is there.
FAKE = r'''#!/usr/bin/env python3
import http.server, os, signal, sys
d = os.environ["FAKE_DIR"]
with open(os.path.join(d, "starts"), "a") as f:
    f.write(f"{os.getpid()}\n")
if os.path.exists(os.path.join(d, "stubborn")):
    signal.signal(signal.SIGTERM, signal.SIG_IGN)
port = int(sys.argv[sys.argv.index("--port") + 1])

class H(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        body = b'{"data": []}' if self.path.startswith("/models") else b"{}"
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, *_):
        pass

http.server.ThreadingHTTPServer(("127.0.0.1", port), H).serve_forever()
'''


def free_port() -> int:
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


class RouterTests(unittest.TestCase):
    def setUp(self):
        self.dir = Path(tempfile.mkdtemp(prefix="router-"))
        (self.dir / "presets").mkdir()
        (self.dir / "presets" / "models.ini").write_text("[m]\nmodel = /library/m.gguf\n")
        (self.dir / "presets" / "max").write_text("1\n")
        server = self.dir / "llama-server"
        server.write_text(FAKE)
        server.chmod(0o755)
        self.port = free_port()
        self.proc: subprocess.Popen | None = None
        self.addCleanup(self.cleanup)

    def cleanup(self):
        if self.proc:
            # The router, its servers and its background loops: one session, all of it.
            try:
                os.killpg(self.proc.pid, signal.SIGKILL)
            except ProcessLookupError:
                pass
            self.proc.wait(timeout=10)
            if self.proc.stdout:
                self.proc.stdout.close()
        for pid in self.starts():
            try:
                os.kill(pid, signal.SIGKILL)
            except ProcessLookupError:
                pass
        shutil.rmtree(self.dir, ignore_errors=True)

    def run_router(self, drain: int = 2):
        # The script's paths, under this test's folder.
        text = re.sub(r"(?<!presets)/kvcache", str(self.dir / "kvcache"), SCRIPT.read_text())
        text = text.replace("/presets/", str(self.dir / "presets") + "/").replace("/app/llama-server", str(self.dir / "llama-server")).replace("8080", str(self.port))
        script = self.dir / "router.sh"
        script.write_text(text)
        env = {"PATH": os.environ.get("PATH", "/usr/bin:/bin"), "ENGINE_KEY": "engine-key-for-router-tests", "FAKE_DIR": str(self.dir),
               "ROUTER_TICK": "0.2", "ROUTER_DRAIN": str(drain)}
        self.proc = subprocess.Popen(["/bin/sh", str(script)], env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True,
                                     start_new_session=True)

    def starts(self) -> list[int]:
        p = self.dir / "starts"
        return [int(x) for x in p.read_text().split()] if p.exists() else []

    def until(self, check, what: str, seconds: float = 20):
        end = time.time() + seconds
        while time.time() < end:
            if check():
                return
            time.sleep(0.1)
        self.fail(f"not within {seconds} s: {what}")

    @staticmethod
    def gone(pid: int) -> bool:
        try:
            return b"zombie" in Path(f"/proc/{pid}/status").read_bytes().lower()
        except FileNotFoundError:
            return True

    def ask(self, gen: int, by: str = "ada"):
        p = self.dir / "presets" / "llamacpp.restart"
        p.with_suffix(".tmp").write_text(f"gen={gen}\naction=restart\nby={by}\nat={int(time.time())}\n")
        p.with_suffix(".tmp").replace(p)

    def output(self) -> str:
        os.killpg(self.proc.pid, signal.SIGKILL)
        self.proc.wait(timeout=10)
        return self.proc.stdout.read()

    def test_a_restart_asked_starts_llama_server_again_and_the_same_ask_does_not_again(self):
        # An ask there before the router starts is no restart: it is where it starts from.
        self.ask(1)
        self.run_router()
        self.until(lambda: len(self.starts()) == 1, "llama-server started")
        first = self.starts()[0]
        time.sleep(1)
        self.assertEqual(1, len(self.starts()), "the ask it started with is not done again")
        self.ask(2, by="grace")
        self.until(lambda: len(self.starts()) == 2, "llama-server started again")
        self.assertTrue(self.gone(first), "the first server stopped")
        time.sleep(1)
        self.assertEqual(2, len(self.starts()), "once for one ask")
        self.assertIn("router: a restart was asked by grace; restarting llama-server", self.output())

    def test_a_server_that_ignores_term_is_killed_once_the_drain_is_over(self):
        (self.dir / "stubborn").write_text("")
        self.run_router(drain=2)
        self.until(lambda: len(self.starts()) == 1, "llama-server started")
        first = self.starts()[0]
        asked = time.time()
        self.ask(1)
        self.until(lambda: len(self.starts()) == 2, "llama-server started again", seconds=15)
        self.assertGreaterEqual(time.time() - asked, 2, "it had its drain first")
        self.assertTrue(self.gone(first), "the stubborn server was killed")
        self.assertIn("llama-server did not stop within 2 s; killing it", self.output())

    def test_sigterm_stops_a_stubborn_server_too_and_ends_the_router(self):
        (self.dir / "stubborn").write_text("")
        self.run_router(drain=2)
        self.until(lambda: len(self.starts()) == 1, "llama-server started")
        first = self.starts()[0]
        self.proc.send_signal(signal.SIGTERM)
        self.assertEqual(0, self.proc.wait(timeout=15))
        self.until(lambda: self.gone(first), "its server killed")


if __name__ == "__main__":
    unittest.main()
