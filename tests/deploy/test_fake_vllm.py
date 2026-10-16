"""
tools/fake-vllm: the stand-in for vLLM that the app's tests, the supervisor's tests and deploy/fake-gpu.yml run.
It must answer as vLLM does where the app looks: health, the model list, streamed answers at a known pace,
metrics under vLLM's names, sleep and wake, the key from the environment, a failed load, a crash, a drain.
"""
from __future__ import annotations

import json
import os
import signal
import socket
import subprocess
import sys
import time
import unittest
import urllib.error
import urllib.request
from pathlib import Path

FAKE = Path(__file__).resolve().parents[2] / "tools" / "fake-vllm" / "fake_vllm.py"


def free_port() -> int:
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


class FakeVllmTests(unittest.TestCase):
    def start(self, *args: str, env: dict | None = None) -> tuple[subprocess.Popen, str]:
        port = free_port()
        e = {"PATH": os.environ.get("PATH", "/usr/bin:/bin")} | (env or {})
        proc = subprocess.Popen([sys.executable, str(FAKE), "serve", "org/Model", "--host", "127.0.0.1", "--port", str(port), *args],
                                env=e, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        self.addCleanup(self.stop, proc)
        return proc, f"http://127.0.0.1:{port}"

    @staticmethod
    def stop(proc: subprocess.Popen):
        if proc.poll() is None:
            proc.kill()
        proc.wait(timeout=10)
        for f in (proc.stdout, proc.stderr):
            if f:
                f.close()

    def get(self, url: str, key: str | None = None) -> tuple[int, bytes]:
        req = urllib.request.Request(url, headers={"Authorization": f"Bearer {key}"} if key else {})
        try:
            with urllib.request.urlopen(req, timeout=10) as r:
                return r.status, r.read()
        except urllib.error.HTTPError as e:
            with e:
                return e.code, e.read()

    def post(self, url: str, body: dict | None = None, key: str | None = None) -> tuple[int, bytes]:
        headers = {"Content-Type": "application/json"} | ({"Authorization": f"Bearer {key}"} if key else {})
        req = urllib.request.Request(url, data=json.dumps(body or {}).encode(), headers=headers, method="POST")
        try:
            with urllib.request.urlopen(req, timeout=20) as r:
                return r.status, r.read()
        except urllib.error.HTTPError as e:
            with e:
                return e.code, e.read()

    def ready(self, base: str, seconds: float = 10):
        end = time.time() + seconds
        while time.time() < end:
            try:
                if self.get(base + "/health")[0] == 200:
                    return
            except OSError:
                pass
            time.sleep(0.05)
        self.fail("not healthy in time")

    def test_it_lists_its_model_answers_streamed_at_its_pace_and_counts_in_vllm_metrics(self):
        _, base = self.start("--served-model-name", "flash", "--max-model-len", "65536", "--ttft", "0.2", "--tps", "50",
                             "--gpu-memory-utilization", "0.8", "--kv-cache-dtype", "fp8")
        self.ready(base)
        code, body = self.get(base + "/v1/models")
        self.assertEqual(200, code)
        model = json.loads(body)["data"][0]
        self.assertEqual(("flash", 65536), (model["id"], model["max_model_len"]))

        began = time.time()
        req = urllib.request.Request(base + "/v1/chat/completions", method="POST", headers={"Content-Type": "application/json"},
                                     data=json.dumps({"model": "flash", "stream": True, "max_tokens": 10, "messages": [{"role": "user", "content": "hi"}]}).encode())
        first = None
        events = []
        with urllib.request.urlopen(req, timeout=10) as r:
            for line in r:
                line = line.decode().strip()
                if line.startswith("data: "):
                    first = first or time.time()
                    events.append(line[6:])
        ended = time.time()
        self.assertEqual("[DONE]", events[-1])
        chunks = [json.loads(e) for e in events[:-1]]
        self.assertEqual("tok", chunks[0]["choices"][0]["delta"]["content"])
        self.assertEqual(10, chunks[-1]["usage"]["completion_tokens"])
        self.assertGreaterEqual(first - began, 0.18)
        self.assertGreaterEqual(ended - first, 0.15)

        code, text = self.get(base + "/metrics")
        text = text.decode()
        self.assertIn('vllm:num_requests_running{model_name="flash"} 0', text)
        self.assertIn('vllm:generation_tokens_total{model_name="flash"} 10', text)
        self.assertIn("vllm:prefix_cache_queries_total", text)
        self.assertIn("vllm:kv_cache_usage_perc", text)

        # Tests set what other clients would be doing.
        self.post(base + "/fake/state", {"running": 3, "waiting": 2, "kv_usage": 0.5})
        text = self.get(base + "/metrics")[1].decode()
        self.assertIn('vllm:num_requests_running{model_name="flash"} 3', text)
        self.assertIn('vllm:num_requests_waiting{model_name="flash"} 2', text)

    def test_the_key_comes_from_the_environment_and_guards_only_the_api(self):
        _, base = self.start(env={"VLLM_API_KEY": "secret-key"})
        self.ready(base)
        self.assertEqual(401, self.get(base + "/v1/models")[0])
        self.assertEqual(200, self.get(base + "/v1/models", key="secret-key")[0])
        self.assertEqual(200, self.get(base + "/health")[0])
        self.assertEqual(200, self.get(base + "/metrics")[0])

    def test_sleep_needs_sleep_mode_and_a_sleeping_model_refuses_until_woken(self):
        _, plain = self.start()
        self.ready(plain)
        self.assertEqual(400, self.post(plain + "/sleep?level=1")[0])
        _, base = self.start("--enable-sleep-mode")
        self.ready(base)
        self.assertEqual(200, self.post(base + "/sleep?level=1")[0])
        self.assertTrue(json.loads(self.get(base + "/is_sleeping")[1])["is_sleeping"])
        self.assertEqual(503, self.post(base + "/v1/chat/completions", {"model": "org/Model", "messages": []})[0])
        self.assertEqual(200, self.post(base + "/wake_up")[0])
        self.assertEqual(200, self.post(base + "/v1/chat/completions", {"model": "org/Model", "messages": []})[0])

    def test_a_load_that_fails_exits_with_the_error_and_nothing_listens_while_it_loads(self):
        proc, base = self.start("--load-delay", "0.5")
        time.sleep(0.2)
        with self.assertRaises(OSError):
            urllib.request.urlopen(base + "/health", timeout=1)
        self.ready(base)
        failing, _ = self.start("--fail")
        self.assertEqual(1, failing.wait(timeout=10))
        self.assertIn("out of memory", failing.stderr.read())

    def test_a_crash_exits_and_sigterm_drains_running_answers(self):
        crashing, base = self.start("--crash-after", "0.3")
        self.assertEqual(9, crashing.wait(timeout=10))

        proc, base = self.start("--tps", "20")
        self.ready(base)
        result = {}

        def ask():
            result["answer"] = self.post(base + "/v1/chat/completions", {"model": "org/Model", "max_tokens": 10, "messages": []})

        import threading
        t = threading.Thread(target=ask)
        t.start()
        time.sleep(0.2)
        proc.send_signal(signal.SIGTERM)
        t.join(timeout=10)
        self.assertEqual(200, result["answer"][0])
        self.assertEqual(0, proc.wait(timeout=10))


if __name__ == "__main__":
    unittest.main()
