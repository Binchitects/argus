"""
deploy/services/sandbox/runner.py (the Python sandbox's runner, Blender's and Lean's too), run in this process on
folders of the test's own: an admin's reset stops the job running and answers the one waiting, each result saying
so, and the runner starts again in place; a main loop that hangs ends the runner. Jobs run as the test's own user
(the runner's switch to a user of its own needs root, so it is left out here), with the job's real limits.
"""
from __future__ import annotations

import importlib.util
import json
import os
import shutil
import subprocess
import sys
import tempfile
import threading
import time
import unittest
from pathlib import Path
from unittest import mock

RUNNER = Path(__file__).resolve().parents[2] / "deploy" / "services" / "sandbox" / "runner.py"


class Started(Exception):
    """os.execv in place: the runner would start again here."""


class RunnerBase(unittest.TestCase):
    def setUp(self):
        self.dir = Path(tempfile.mkdtemp(prefix="sandbox-runner-"))
        env = {"SANDBOX_JOBS_DIR": str(self.dir / "jobs"), "SANDBOX_WORK_DIR": str(self.dir / "work"), "SANDBOX_PRIVATE_DIR": str(self.dir / "private"),
               "SANDBOX_SLOTS": "1", "SANDBOX_OWNER_UID": str(os.getuid()), "SANDBOX_OWNER_GID": str(os.getgid())}
        (self.dir / "jobs").mkdir()
        with mock.patch.dict(os.environ, env):
            spec = importlib.util.spec_from_file_location(f"runner_{id(self)}", RUNNER)
            self.runner = importlib.util.module_from_spec(spec)
            spec.loader.exec_module(self.runner)
        self.runner.FIRST_UID = os.getuid()
        self.said: list[str] = []
        self.runner.log = lambda *parts: self.said.append(" ".join(str(p) for p in parts))
        self.addCleanup(shutil.rmtree, self.dir, True)

    @staticmethod
    def as_ourselves(popen):
        """subprocess.Popen without the switch to the job's user, group and groups (it needs root)."""
        def run(*args, **kwargs):
            for k in ("user", "group", "extra_groups"):
                kwargs.pop(k, None)
            return popen(*args, **kwargs)
        return run

    def job(self, job_id: str, code: str):
        draft = self.dir / "jobs" / "in" / f".tmp-{job_id}"
        (draft / "files").mkdir(parents=True)
        (draft / "job.json").write_text(json.dumps({"code": code, "timeout": 60, "files": []}))
        draft.rename(self.dir / "jobs" / "in" / job_id)

    def result(self, job_id: str) -> dict:
        return json.loads((self.dir / "jobs" / "out" / job_id / "result.json").read_text())

    def until(self, check, what: str, seconds: float = 20):
        end = time.time() + seconds
        while time.time() < end:
            if check():
                return
            time.sleep(0.05)
        self.fail(f"not within {seconds} s: {what}")


class ResetTests(RunnerBase):
    def test_a_reset_stops_the_job_running_answers_the_one_waiting_and_starts_the_runner_again(self):
        r = self.runner
        done = threading.Event()

        def main():
            try:
                r.main()
            except Started:
                done.set()

        popen = subprocess.Popen
        with mock.patch.object(r, "heartbeat", lambda: None), mock.patch.object(r, "kill_user") as kill_user, \
                mock.patch.object(r.os, "chown"), mock.patch.object(r.subprocess, "Popen", self.as_ourselves(popen)), \
                mock.patch.object(r.os, "execv", side_effect=Started) as execv:
            threading.Thread(target=main, daemon=True).start()
            # One slot: the first job runs, the second waits behind it.
            self.job("00000000000000000000000000000001", "import time\ntime.sleep(60)\n")
            self.until(lambda: r.busy == 1 and r.taken.get(0) == "00000000000000000000000000000001", "the first job runs")
            time.sleep(0.5)
            self.job("00000000000000000000000000000002", "print('never')\n")
            time.sleep(0.3)
            self.assertTrue((self.dir / "jobs" / "in" / "00000000000000000000000000000002").exists(), "the second waits")

            (self.dir / "jobs" / "reset").write_text("gen=1\naction=reset\nby=ada\nat=1\n")
            self.assertTrue(done.wait(20), "the runner started again")

        running = self.result("00000000000000000000000000000001")
        self.assertEqual(("running", True), (running["reset"], running["cancelled"]))
        self.assertNotIn("killed", running, "stopped by the reset, not killed for its memory")
        waiting = self.result("00000000000000000000000000000002")
        self.assertEqual(("waiting", True, ""), (waiting["reset"], waiting["cancelled"], waiting["stdout"]))
        self.assertFalse((self.dir / "jobs" / "in" / "00000000000000000000000000000002").exists())
        self.assertFalse((self.dir / "jobs" / "reset").exists(), "the reset is done")
        self.assertEqual([], os.listdir(self.dir / "work"))
        # What was left of the slot's user killed; then the same command again, in place.
        kill_user.assert_any_call(os.getuid())
        execv.assert_called_once_with(sys.executable, sys.orig_argv)
        self.assertIn("reset asked by ada: stopping 1 running, 1 waiting answered", self.said)

    def test_a_job_written_while_a_reset_runs_is_answered_too_and_not_left_for_the_runner_after(self):
        r = self.runner
        done = threading.Event()
        written = threading.Event()

        def main():
            try:
                r.main()
            except Started:
                done.set()

        read_stream = r.read_stream

        def slow(path):
            # The job stopped takes its time to give its result: meanwhile the app writes another.
            written.wait(20)
            return read_stream(path)

        popen = subprocess.Popen
        with mock.patch.object(r, "heartbeat", lambda: None), mock.patch.object(r, "kill_user"), mock.patch.object(r.os, "chown"), \
                mock.patch.object(r.subprocess, "Popen", self.as_ourselves(popen)), mock.patch.object(r, "read_stream", slow), \
                mock.patch.object(r.os, "execv", side_effect=Started):
            threading.Thread(target=main, daemon=True).start()
            self.job("00000000000000000000000000000001", "import time\ntime.sleep(60)\n")
            self.until(lambda: r.busy == 1, "the job runs")
            (self.dir / "jobs" / "reset").write_text("gen=1\naction=reset\nby=ada\nat=1\n")
            self.until(lambda: any(s.startswith("reset asked") for s in self.said), "the reset answered what waited")
            self.job("00000000000000000000000000000009", "print('late')\n")
            written.set()
            self.assertTrue(done.wait(20), "the runner started again")

        self.assertEqual("running", self.result("00000000000000000000000000000001")["reset"])
        late = self.result("00000000000000000000000000000009")
        self.assertEqual(("waiting", True, ""), (late["reset"], late["cancelled"], late["stdout"]))
        self.assertFalse((self.dir / "jobs" / "in" / "00000000000000000000000000000009").exists(), "nothing left for the runner that starts again")
        self.assertIn("reset: 1 more waiting answered", self.said)

    def test_a_runner_says_when_it_started_before_it_takes_a_job(self):
        # The app tells a job this runner took from one the runner before it lost by the "started" it reads then.
        r = self.runner
        done = threading.Event()
        seen = []
        rename = os.rename

        def main():
            try:
                r.main()
            except Started:
                done.set()

        def take(src, dst):
            if os.path.dirname(dst) == r.RUN:
                heartbeat = self.dir / "jobs" / "heartbeat"
                seen.append(json.loads(heartbeat.read_text())["started"] if heartbeat.exists() else None)
            return rename(src, dst)

        popen = subprocess.Popen
        with mock.patch.object(r, "heartbeat", lambda: None), mock.patch.object(r, "kill_user"), mock.patch.object(r.os, "chown"), \
                mock.patch.object(r.subprocess, "Popen", self.as_ourselves(popen)), mock.patch.object(r.os, "rename", take), \
                mock.patch.object(r.os, "execv", side_effect=Started):
            # Waiting already when the runner starts, as one written while it started again.
            self.job("00000000000000000000000000000004", "print('hello')\n")
            threading.Thread(target=main, daemon=True).start()
            self.until(lambda: (self.dir / "jobs" / "out" / "00000000000000000000000000000004").exists(), "the job ran")
            (self.dir / "jobs" / "reset").write_text("gen=1\naction=reset\nby=ada\nat=1\n")
            self.assertTrue(done.wait(20), "the runner started again")

        self.assertEqual([r.STARTED], seen)
        self.assertEqual("hello\n", self.result("00000000000000000000000000000004")["stdout"])

    def test_a_job_that_ends_on_its_own_says_nothing_of_a_reset(self):
        r = self.runner
        r.prepare()
        self.job("00000000000000000000000000000003", "print('hello')\n")
        os.rename(self.dir / "jobs" / "in" / "00000000000000000000000000000003", self.dir / "jobs" / "run" / "00000000000000000000000000000003")
        with mock.patch.object(r, "kill_user"), mock.patch.object(r.os, "chown"), mock.patch.object(r.subprocess, "Popen", self.as_ourselves(subprocess.Popen)):
            r.run_job(0, "00000000000000000000000000000003")
        done = self.result("00000000000000000000000000000003")
        self.assertEqual(("hello\n", 0, False), (done["stdout"], done["exit_code"], done["cancelled"]))
        self.assertNotIn("reset", done)


class WatchdogTests(RunnerBase):
    def test_a_main_loop_that_hangs_ends_the_runner_and_one_that_turns_does_not(self):
        r = self.runner
        r.prepare()
        with mock.patch.object(r.os, "_exit", side_effect=SystemExit) as end, mock.patch.object(r.os, "chown"):
            r.last_tick = time.monotonic()
            r.beat()
            end.assert_not_called()
            self.assertIn("started", json.loads((self.dir / "jobs" / "heartbeat").read_text()))
            r.last_tick = time.monotonic() - r.STALL_LIMIT - 1
            with self.assertRaises(SystemExit):
                r.beat()
            end.assert_called_once_with(70)


if __name__ == "__main__":
    unittest.main()
