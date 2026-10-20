"""
deploy/scripts/multiuser-bench.py's new scenarios against the fake vLLM (tools/fake-vllm), as CI runs them: a concurrency
ramp with aggregate tok/s and time to first token, many people sharing one long system prompt, a conversation coming
back after others, an engine killed mid-burst and the seconds until it answers again, and a run compared with a baseline.
Real numbers come from the real hardware; this checks the measuring.
"""
from __future__ import annotations

import json
import os
import socket
import subprocess
import sys
import tempfile
import time
import unittest
import urllib.request
from pathlib import Path

from support import REPO

BENCH = REPO / "deploy" / "scripts" / "multiuser-bench.py"
FAKE = REPO / "tools" / "fake-vllm" / "fake_vllm.py"


def free_port() -> int:
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


class BenchTests(unittest.TestCase):
    def setUp(self):
        self.dir = Path(tempfile.mkdtemp(prefix="bench-"))
        self.port = free_port()
        self.procs: list[subprocess.Popen] = []
        self.start_fake()

    def tearDown(self):
        for p in self.procs:
            if p.poll() is None:
                p.kill()
            p.wait(timeout=10)
        pid = self.dir / "fake.pid"
        if pid.exists():
            try:
                os.kill(int(pid.read_text()), 9)
            except (ProcessLookupError, ValueError):
                pass

    def start_fake(self):
        p = subprocess.Popen([sys.executable, str(FAKE), "serve", "m", "--served-model-name", "m", "--host", "127.0.0.1", "--port", str(self.port),
                              "--ttft", "0.02", "--tps", "400"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        self.procs.append(p)
        (self.dir / "fake.pid").write_text(str(p.pid))
        end = time.time() + 10
        while time.time() < end:
            try:
                urllib.request.urlopen(f"http://127.0.0.1:{self.port}/health", timeout=1).close()
                return
            except OSError:
                time.sleep(0.05)
        self.fail("the fake vLLM never answered")

    def bench(self, *args: str) -> tuple[int, dict, str]:
        out = self.dir / "run.json"
        r = subprocess.run([sys.executable, str(BENCH), "--base-url", f"http://127.0.0.1:{self.port}", "--key-a", "a", "--key-b", "b", "--model", "m",
                            "--max-tokens", "16", "--paste-words", "300", "--rounds", "1", "--json", str(out), *args],
                           capture_output=True, text=True, timeout=300)
        return r.returncode, json.loads(out.read_text()) if out.exists() else {}, r.stdout + r.stderr

    def test_a_ramp_shared_prefixes_and_a_return_are_measured_and_compared(self):
        code, res, out = self.bench("--scenarios", "ramp,prefixshare,returnafter", "--ramp", "1..8", "--prefix-share", "4",
                                    "--return-after", "0.2", "--evict-with", "2")
        self.assertEqual(0, code, out)
        self.assertEqual([1, 2, 4, 8], [s["concurrency"] for s in res["ramp"]])
        self.assertTrue(all(s["failed"] == 0 and s["aggregate_tok_s"] > 0 and s["ttft_p95_s"] >= s["ttft_p50_s"] for s in res["ramp"]), res["ramp"])
        self.assertEqual(5, len(res["prefixshare"]))
        self.assertIn("prefixshare_hit", res)
        self.assertEqual(["turn 1", "turn 2"], [r["label"] for r in res["returnafter"]])
        baseline = self.dir / "baseline.json"
        baseline.write_text(json.dumps(res))
        code, _, out = self.bench("--scenarios", "ramp", "--ramp", "1,2", "--compare", str(baseline))
        self.assertEqual(0, code, out)
        self.assertIn("of the baseline's tok/s", out)

    def test_an_engine_killed_mid_burst_is_counted_and_its_recovery_timed(self):
        restart = self.dir / "restart.sh"
        restart.write_text(f"""#!/bin/sh
kill -9 "$(cat {self.dir}/fake.pid)"
sleep 1
nohup {sys.executable} {FAKE} serve m --served-model-name m --host 127.0.0.1 --port {self.port} --ttft 0.02 --tps 400 > /dev/null 2>&1 &
echo $! > {self.dir}/fake.pid
""")
        restart.chmod(0o755)
        code, res, out = self.bench("--scenarios", "killengine", "--kill-engine", str(restart))
        # Requests cut by the restart are its measure, not the run's errors.
        self.assertEqual(0, code, out)
        self.assertIsNotNone(res["killengine"]["recovered_s"], out)
        self.assertLess(res["killengine"]["recovered_s"], 60)
        self.assertEqual(4, len(res["killengine_requests"]))


if __name__ == "__main__":
    unittest.main()
