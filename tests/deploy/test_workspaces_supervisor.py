"""deploy/services/workspaces/supervisor.py: what a workspace is asked with, its slot, who may drive it, and its engine
started and stopped, with git, the engine and the users' processes stubbed (no root here)."""
from __future__ import annotations

import importlib.util
import io
import json
import os
import subprocess
import tempfile
import unittest
from pathlib import Path
from unittest import mock

REPO = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("workspaces_supervisor", REPO / "deploy/services/workspaces/supervisor.py")
sup = importlib.util.module_from_spec(spec)
spec.loader.exec_module(sup)

WID = "0123456789abcdef0123456789abcdef"
GOOD = {"repo": "https://gitlab.example.test/team/shop.git", "branch": "main", "git_user": "dana", "git_token": "glpat-SECRET-123",
        "arena_url": "https://llm.example.test", "api_key": "sk-0123456789abcdef"}


class Base(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        patches = [mock.patch.object(sup, "WORKSPACES", self.dir.name), mock.patch.object(sup, "_own"), mock.patch.object(sup, "_kill_user"),
                   mock.patch.dict(sup._running, {}, clear=True)]
        for p in patches:
            p.start()
            self.addCleanup(p.stop)
        self.addCleanup(self.dir.cleanup)


class Asked(Base):
    def test_a_workspace_needs_a_project_a_branch_name_arenas_address_and_a_key(self):
        sup._check(GOOD)
        # The app over the stack's own network, and a company's GitLab on http.
        sup._check({**GOOD, "arena_url": "http://app:8080", "repo": "http://gitlab.corp:8929/team/shop.git"})
        for bad, why in [({"repo": "git@host:team/shop.git"}, "repo"), ({"repo": "https://host/x; rm -rf /"}, "repo"),
                         ({"branch": "--upload-pack=x y"}, "branch"), ({"arena_url": "ftp://llm"}, "arena_url"), ({"arena_url": "app:8080"}, "arena_url"), ({"api_key": "short"}, "api_key")]:
            with self.assertRaises(sup.Refused) as refused:
                sup._check({**GOOD, **bad})
            self.assertEqual(refused.exception.status, 400)
            self.assertIn(why, str(refused.exception))

    def test_each_workspace_keeps_its_slot_and_a_new_one_takes_the_first_free(self):
        a, b = "a" * 32, "b" * 32
        self.assertEqual(sup._slot(a), 0)
        self.assertEqual(sup._slot(b), 1)
        self.assertEqual(sup._slot(a), 0)
        with mock.patch.object(sup, "SLOTS", 2), self.assertRaises(sup.Refused) as full:
            sup._slot("c" * 32)
        self.assertEqual(full.exception.status, 503)


class Cloned(Base):
    def test_a_clone_that_fails_says_why_without_the_token(self):
        failed = subprocess.CompletedProcess([], 128, "", "fatal: Authentication failed for 'https://oauth2:glpat-SECRET-123@gitlab.example.test/team/shop.git'")
        with mock.patch.object(sup.subprocess, "run", return_value=failed) as run, self.assertRaises(sup.Refused) as refused:
            sup._clone(WID, 30000, GOOD)
        self.assertEqual(refused.exception.status, 502)
        self.assertNotIn("glpat-SECRET-123", str(refused.exception))
        self.assertIn("Authentication failed", str(refused.exception))
        # The token is read through the askpass helper from the workspace's home, never on git's command line.
        cmd = run.call_args.args[0]
        self.assertNotIn("glpat-SECRET-123", " ".join(cmd))
        self.assertEqual(cmd[-3:], ["--", GOOD["repo"], os.path.join(self.dir.name, WID, "repo")])
        home = Path(self.dir.name, WID, "home")
        self.assertEqual((home / ".git-token").read_text(), "glpat-SECRET-123")
        self.assertEqual(oct((home / ".git-token").stat().st_mode & 0o777), "0o600")
        self.assertEqual(run.call_args.kwargs["env"]["GIT_ASKPASS"], str(home / ".git-askpass"))


class Started(Base):
    def test_a_workspace_is_cloned_once_its_engine_started_with_its_own_key_and_the_persons_access_and_stopped(self):
        cloned = subprocess.CompletedProcess([], 0, "", "")

        def clone(cmd, **kw):
            os.makedirs(os.path.join(cmd[-1], ".git"))
            return cloned

        engine = mock.Mock()
        engine.poll.return_value = None
        engine.pid = 4321
        with mock.patch.object(sup.subprocess, "run", side_effect=clone) as run, mock.patch.object(sup.subprocess, "Popen", return_value=engine) as popen, \
                mock.patch.object(sup, "_listening", return_value=True):
            first = sup.start(WID, GOOD)
            self.assertEqual((first["port"], first["cloned"]), (sup.FIRST_PORT, True))
            self.assertGreaterEqual(len(first["key"]), 32)
            cmd, kw = popen.call_args.args[0], popen.call_args.kwargs
            self.assertEqual(cmd[1:], ["web", "--serve", "--port", str(sup.FIRST_PORT)])
            self.assertEqual(kw["cwd"], os.path.join(self.dir.name, WID, "repo"))
            self.assertEqual((kw["env"]["CODE_ARENA_TOKEN"], kw["env"]["ARENA_API_KEY"], kw["env"]["ARENA_URL"]), (first["key"], GOOD["api_key"], GOOD["arena_url"]))
            self.assertNotIn("glpat-SECRET-123", json.dumps(kw["env"]))
            # Asked again while it runs: the same engine.
            self.assertEqual(sup.start(WID, GOOD), {"port": first["port"], "key": first["key"], "cloned": False})
            self.assertEqual(run.call_count, 1)
            self.assertEqual([w["id"] for w in sup.running()], [WID])

            with mock.patch.object(sup.os, "killpg") as killpg:
                sup.stop(WID)
            killpg.assert_called_once()
            sup._kill_user.assert_called_with(30000)
            self.assertEqual(sup.running(), [])
            # Started again: not cloned again, a new key.
            again = sup.start(WID, GOOD)
            self.assertEqual(again["cloned"], False)
            self.assertNotEqual(again["key"], first["key"])

    def test_an_engine_that_does_not_listen_is_killed_and_what_it_said_is_told(self):
        engine = mock.Mock()

        def said(cmd, **kw):
            kw["stdout"].write(b"\xe2\x9c\x97 Port 4100 is taken:\n  choose another\n")
            return engine

        with mock.patch.object(sup, "_clone", return_value=False), mock.patch.object(sup.subprocess, "Popen", side_effect=said), \
                mock.patch.object(sup, "_listening", return_value=False), self.assertRaises(sup.Refused) as refused:
            sup.start(WID, GOOD)
        engine.kill.assert_called_once()
        self.assertEqual(refused.exception.status, 502)
        self.assertEqual(str(refused.exception), "The workspace's engine did not start: \u2717 Port 4100 is taken: choose another")


class Driven(Base):
    def _ask(self, method, path, body=None, key="k" * 40):
        handler = sup.Handler.__new__(sup.Handler)
        data = json.dumps(body).encode() if body is not None else b""
        handler.path = path
        handler.headers = {"Authorization": f"Bearer {key}", "Content-Length": str(len(data))}
        handler.rfile = io.BytesIO(data)
        handler.wfile = io.BytesIO()
        answered = {}

        def answer(status, payload):
            answered["status"], answered["body"] = status, payload
        handler._answer = answer
        with mock.patch.object(sup, "KEY", "k" * 40):
            handler._handle(method)
        return answered["status"], answered["body"]

    def test_only_arenas_key_drives_it_and_health_is_open(self):
        self.assertEqual(self._ask("GET", "/health", key="x")[0], 200)
        self.assertEqual(self._ask("GET", "/v1/workspaces", key="wrong")[0], 401)
        self.assertEqual(self._ask("GET", "/v1/workspaces"), (200, []))
        self.assertEqual(self._ask("PUT", "/v1/workspaces/not-an-id", GOOD)[0], 404)
        self.assertEqual(self._ask("PUT", f"/v1/workspaces/{WID}", {**GOOD, "repo": "ftp://x"})[0], 400)
        with mock.patch.object(sup, "delete") as delete:
            self.assertEqual(self._ask("DELETE", f"/v1/workspaces/{WID}"), (200, {"deleted": True}))
        delete.assert_called_once_with(WID)


if __name__ == "__main__":
    unittest.main()
