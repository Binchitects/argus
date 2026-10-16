"""
deploy/services/vllm/supervisor.sh, run by /bin/sh with tools/fake-vllm as `vllm`: it reconciles the app's desired state
(models added, removed, changed), runs each on its GPUs only, keeps the key off every command line, puts a model to sleep
and wakes it, drains on SIGTERM, backs off after a crash or a failed load, refuses an entry that would leak the key, and
says all of it in its status files.
"""
from __future__ import annotations

import json
import os
import shutil
import signal
import socket
import subprocess
import tempfile
import time
import unittest
import urllib.error
import urllib.request
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
SUPERVISOR = REPO / "deploy" / "services" / "vllm" / "supervisor.sh"
FAKE = REPO / "tools" / "fake-vllm" / "vllm"
KEY = "engine-key-for-supervisor-tests"


def free_port() -> int:
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


class SupervisorTests(unittest.TestCase):
    def setUp(self):
        self.dir = Path(tempfile.mkdtemp(prefix="vllm-sup-"))
        for d in ("presets", "status", "run"):
            (self.dir / d).mkdir()
        self.proc: subprocess.Popen | None = None
        self.addCleanup(self.cleanup)

    def cleanup(self):
        if self.proc and self.proc.poll() is None:
            self.proc.send_signal(signal.SIGTERM)
            try:
                self.proc.wait(timeout=30)
            except subprocess.TimeoutExpired:
                self.proc.kill()
        if self.proc and self.proc.stdout:
            self.proc.stdout.close()
        # Anything left of its servers.
        for f in (self.dir / "run" / "state").glob("*/pid"):
            try:
                os.kill(int(f.read_text()), signal.SIGKILL)
            except (ProcessLookupError, ValueError):
                pass
        shutil.rmtree(self.dir, ignore_errors=True)

    def run_supervisor(self, **env):
        e = {"PATH": os.environ.get("PATH", "/usr/bin:/bin"), "ENGINE_KEY": KEY, "VLLM_BIN": str(FAKE), "VLLM_PRESETS": str(self.dir / "presets"),
             "VLLM_STATUS": str(self.dir / "status"), "VLLM_RUN": str(self.dir / "run"), "VLLM_HOST": "127.0.0.1", "VLLM_TICK": "0.2",
             "VLLM_BACKOFF": "1", "VLLM_DRAIN": "5", "GPU_PROBE": str(self.dir / "no-probe")} | env
        self.proc = subprocess.Popen(["/bin/sh", str(SUPERVISOR)], env=e, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)

    def want(self, *models: dict):
        p = self.dir / "presets" / "models.json"
        p.with_suffix(".tmp").write_text(json.dumps({"models": list(models)}))
        p.with_suffix(".tmp").replace(p)

    def status(self, name: str) -> dict:
        p = self.dir / "status" / f"{name}.json"
        try:
            return json.loads(p.read_text())
        except (OSError, json.JSONDecodeError):
            return {}

    def until(self, check, what: str, seconds: float = 20):
        end = time.time() + seconds
        while time.time() < end:
            if check():
                return
            time.sleep(0.1)
        self.fail(f"not within {seconds} s: {what}")

    def get(self, port: int, path: str, key: str | None = KEY) -> tuple[int, bytes]:
        req = urllib.request.Request(f"http://127.0.0.1:{port}{path}", headers={"Authorization": f"Bearer {key}"} if key else {})
        try:
            with urllib.request.urlopen(req, timeout=5) as r:
                return r.status, r.read()
        except urllib.error.HTTPError as e:
            with e:
                return e.code, e.read()

    @staticmethod
    def model(name: str, port: int, gpus=(1,), sleep=False, extra=()) -> dict:
        return {"name": name, "port": port, "gpus": list(gpus), "sleep": sleep,
                "args": ["fake/Model", "--max-model-len", "4096", "--enable-sleep-mode", *extra]}

    def test_a_model_runs_on_its_gpus_with_the_key_in_its_environment_only_sleeps_wakes_and_goes(self):
        port = free_port()
        self.want(self.model("flash", port, gpus=(1, 2)))
        self.run_supervisor()
        self.until(lambda: self.status("flash").get("phase") == "ready", "flash ready")
        s = self.status("flash")
        self.assertEqual((port, [1, 2]), (s["port"], s["gpus"]))
        self.assertEqual(401, self.get(port, "/v1/models", key=None)[0])
        code, body = self.get(port, "/v1/models")
        self.assertEqual(("flash", 200), (json.loads(body)["data"][0]["id"], code))
        pid = s["pid"]
        cmdline = Path(f"/proc/{pid}/cmdline").read_bytes().split(b"\0")
        environ = Path(f"/proc/{pid}/environ").read_bytes().split(b"\0")
        self.assertFalse(any(KEY.encode() in a for a in cmdline), "the key is on the command line")
        self.assertIn(f"VLLM_API_KEY={KEY}".encode(), environ)
        self.assertIn(b"CUDA_VISIBLE_DEVICES=1,2", environ)
        self.assertIn(b"HIP_VISIBLE_DEVICES=1,2", environ)
        self.assertNotIn(b"ENGINE_KEY", b"\0".join(environ))

        # Asleep and awake, as the app says: the same process.
        self.want(self.model("flash", port, gpus=(1, 2), sleep=True))
        self.until(lambda: self.status("flash").get("phase") == "sleeping", "flash asleep")
        self.assertTrue(json.loads(self.get(port, "/is_sleeping")[1])["is_sleeping"])
        self.want(self.model("flash", port, gpus=(1, 2), sleep=False))
        self.until(lambda: self.status("flash").get("phase") == "ready", "flash awake")
        self.assertEqual(pid, self.status("flash")["pid"])

        # No longer wanted: drained, its status gone, its log kept.
        self.want()
        self.until(lambda: not (self.dir / "status" / "flash.json").exists(), "flash gone")
        self.until(lambda: not Path(f"/proc/{pid}").exists() or b"zombie" in Path(f"/proc/{pid}/status").read_bytes().lower(), "its process gone")
        self.assertIn("starting flash", (self.dir / "status" / "flash.log").read_text())

    def test_a_change_restarts_it_and_another_model_runs_beside_it(self):
        a, b = free_port(), free_port()
        self.want(self.model("a", a))
        self.run_supervisor()
        self.until(lambda: self.status("a").get("phase") == "ready", "a ready")
        first = self.status("a")["pid"]
        # New arguments and a second model.
        self.want(self.model("a", a, extra=("--max-num-seqs", "8")), self.model("b", b, gpus=(3,)))
        self.until(lambda: self.status("a").get("phase") == "ready" and self.status("a").get("pid") != first, "a started again")
        self.until(lambda: self.status("b").get("phase") == "ready", "b ready")
        self.assertEqual([3], self.status("b")["gpus"])
        # Only the sleep switch changed: no restart.
        pid = self.status("a")["pid"]
        self.want(self.model("a", a, sleep=True, extra=("--max-num-seqs", "8")), self.model("b", b, gpus=(3,)))
        self.until(lambda: self.status("a").get("phase") == "sleeping", "a asleep")
        self.assertEqual(pid, self.status("a")["pid"])

    def test_a_crash_and_a_failed_load_back_off_and_say_why(self):
        crash, fail = free_port(), free_port()
        self.want(self.model("crash", crash, extra=("--crash-after", "0.5")), self.model("oom", fail, extra=("--fail",)))
        self.run_supervisor()
        self.until(lambda: self.status("crash").get("phase") == "failed", "crash failed")
        s = self.status("crash")
        self.assertEqual((9, 1), (s["exit"], s["tries"]))
        self.assertGreater(s["next_try"], s["since"] - 1)
        self.until(lambda: self.status("oom").get("phase") == "failed", "oom failed")
        oom = self.status("oom")
        self.assertEqual(1, oom["exit"])
        self.assertTrue(any("out of memory" in line for line in oom["log"]), oom["log"])
        # It starts again after its wait, and waits longer each time.
        self.until(lambda: self.status("crash").get("tries", 0) >= 2, "crash tried again")
        self.until(lambda: self.status("crash").get("tries", 0) >= 3, "crash tried a third time", 30)
        third = self.status("crash")
        self.assertGreaterEqual(third["next_try"] - third["since"], 3)
        # Fixed (its arguments changed): started at once, its tries forgotten.
        self.want(self.model("crash", crash), self.model("oom", fail, extra=("--fail",)))
        self.until(lambda: self.status("crash").get("phase") == "ready", "crash fixed")
        self.assertEqual(0, self.status("crash")["tries"])

    def test_an_entry_that_would_leak_the_key_or_take_what_the_supervisor_owns_never_starts(self):
        good = free_port()
        self.want(self.model("leak", free_port(), extra=("--api-key", KEY)), self.model("port", free_port(), extra=("--port", "9999")),
                  {"name": "../escape", "port": free_port(), "args": []}, self.model("good", good))
        self.run_supervisor()
        self.until(lambda: self.status("good").get("phase") == "ready", "good ready")
        names = sorted(p.stem for p in (self.dir / "status").glob("*.json") if p.stem != "supervisor")
        self.assertEqual(["good"], names)
        # Its heartbeat, for the app.
        self.assertGreater(json.loads((self.dir / "status" / "supervisor.json").read_text())["at"], time.time() - 10)

    def test_sigterm_drains_every_model_and_exits(self):
        port = free_port()
        self.want(self.model("m", port))
        self.run_supervisor()
        self.until(lambda: self.status("m").get("phase") == "ready", "m ready")
        pid = self.status("m")["pid"]
        self.proc.send_signal(signal.SIGTERM)
        self.assertEqual(0, self.proc.wait(timeout=20))
        self.until(lambda: not Path(f"/proc/{pid}").exists(), "its server stopped")
        self.assertFalse((self.dir / "status" / "m.json").exists())

    def test_no_key_no_start(self):
        e = {"PATH": "/usr/bin:/bin", "VLLM_PRESETS": str(self.dir / "presets"), "VLLM_STATUS": str(self.dir / "status")}
        out = subprocess.run(["/bin/sh", str(SUPERVISOR)], env=e, capture_output=True, text=True, timeout=10)
        self.assertNotEqual(0, out.returncode)
        self.assertIn("ENGINE_KEY", out.stderr)


if __name__ == "__main__":
    unittest.main()
