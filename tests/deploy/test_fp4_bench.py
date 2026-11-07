"""
deploy/scripts/fp4-bench.py, the FP4 against FP8 and BF16 measurement: its questions the same for every model, the
answers read from what a model says, vLLM's memory lines read from its log, and a sweep and the quality check run against
the fake vLLM (tools/fake-vllm), whose speed is set; and fp4-h200.sh skipping what the library does not have.
"""
from __future__ import annotations

import importlib.util
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

SCRIPTS = REPO / "deploy" / "scripts"


def bench():
    spec = importlib.util.spec_from_file_location("fp4_bench", SCRIPTS / "fp4-bench.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def free_port() -> int:
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


class Fp4BenchTests(unittest.TestCase):
    def test_the_questions_are_the_same_every_time_and_answers_are_read_from_what_a_model_says(self):
        b = bench()
        self.assertEqual(b.questions(), b.questions())
        self.assertEqual(60, len(b.questions()))
        q, want = b.questions()[0]
        self.assertIn("Answer: N", q)
        self.assertIsInstance(want, int)
        self.assertEqual(1234, b.answer_of("It is 12, then 1,234.\nAnswer: **1,234**"))
        self.assertEqual(7, b.answer_of("so 3 + 4 = 7"))
        self.assertIsNone(b.answer_of("no number here"))

    def test_vllms_memory_lines_and_kernels_are_read_from_its_log(self):
        log = ("INFO Model loading took 0.53 GiB memory and 4.1 seconds\nINFO Available KV cache memory: 19.21 GiB\n"
               "INFO GPU KV cache size: 179,856 tokens\nINFO Using MarlinLinearKernel for NVFP4\n")
        m = bench().logged(log)
        self.assertEqual((0.53, 19.21, 179856), (m["weights_gib"], m["cache_gib"], m["cache_tokens"]))
        self.assertTrue(any("Marlin" in k for k in m["kernels"]))

    def test_a_sweep_and_the_quality_check_run_against_the_fake_vllm(self):
        port = free_port()
        fake = subprocess.Popen([sys.executable, str(REPO / "tools" / "fake-vllm" / "fake_vllm.py"), "serve", "m", "--port", str(port), "--host", "127.0.0.1",
                                 "--served-model-name", "fp4", "--ttft", "0.01", "--tps", "500"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                                env=dict(os.environ, VLLM_API_KEY=""))
        try:
            for _ in range(100):
                try:
                    with urllib.request.urlopen(f"http://127.0.0.1:{port}/health", timeout=1):
                        break
                except OSError:
                    time.sleep(0.05)
            with tempfile.TemporaryDirectory() as d:
                out = Path(d) / "r.json"
                code = bench().main(["endpoint", "--base-url", f"http://127.0.0.1:{port}", "--model", "fp4", "--levels", "1,4", "--tokens", "20", "--out", str(out)])
                report = json.loads(out.read_text())
            self.assertEqual(0, code)
            speed = report["results"][0]["speed"]
            self.assertEqual([1, 4], [s["people"] for s in speed])
            self.assertTrue(all(s["failed"] == 0 and s["each_tok_s"] > 0 and s["all_tok_s"] > 0 for s in speed))
            q = report["results"][0]["quality"]
            self.assertEqual((60, 1.0), (q["of"], q["same_as_first"]))
        finally:
            fake.terminate()
            fake.wait(10)

    def test_the_h200_plan_skips_what_the_library_does_not_have(self):
        with tempfile.TemporaryDirectory() as lib:
            run = subprocess.run(["sh", str(SCRIPTS / "fp4-h200.sh"), lib, "0,1", lib], capture_output=True, text=True, timeout=60)
        self.assertEqual(0, run.returncode, run.stderr)
        self.assertIn("flash: Qwen/Qwen3.8-Flash-Next-FP8 is not in", run.stdout)
        self.assertIn("flash: none of its snapshots is here", run.stdout)
        self.assertIn("small-mxfp4: none of its snapshots is here", run.stdout)


if __name__ == "__main__":
    unittest.main()
