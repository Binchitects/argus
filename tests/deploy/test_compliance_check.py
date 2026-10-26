"""
deploy/scripts/compliance-check.py: how it judges what a stack answers (its
headers, the session cookie, the guard against cross-site requests and the
app's own checks), and its exit code, against fakes of the answers.
"""
from __future__ import annotations

import importlib.util
import io
import json
import unittest
from contextlib import redirect_stdout
from email.message import Message
from pathlib import Path
from unittest import mock

HERE = Path(__file__).resolve().parent
SCRIPT = HERE.parents[1] / "deploy" / "scripts" / "compliance-check.py"
spec = importlib.util.spec_from_file_location("compliance_check", SCRIPT)
cc = importlib.util.module_from_spec(spec)
spec.loader.exec_module(cc)

GOOD = {
    "Content-Security-Policy": "default-src 'self'; script-src 'self'; frame-ancestors 'none'",
    "X-Content-Type-Options": "nosniff",
    "X-Frame-Options": "DENY",
    "Referrer-Policy": "same-origin",
    "Permissions-Policy": "camera=(), microphone=()",
}


def headers(values):
    m = Message()
    for k, v in values.items():
        m[k] = v
    return m


class FakeStack:
    """Answers each (method, path) as told: (status, body, headers)."""

    def __init__(self, answers):
        self.answers = answers
        self.base = "https://arena.test"
        self.asked = []

    def req(self, method, path, body=None, headers_=None):
        self.asked.append((method, path))
        return self.answers[(method, path)]


class Headers(unittest.TestCase):
    def test_The_pages_and_the_API_need_every_header_and_HSTS_off_is_a_warning(self):
        both = {("GET", "/"): (200, "", headers(GOOD)), ("GET", "/api/info"): (200, "", headers({**GOOD, "Strict-Transport-Security": "max-age=0"}))}
        state, detail = cc.check_headers(FakeStack(both))
        self.assertEqual(cc.WARN, state)
        self.assertIn("No HSTS", detail)

        hsts = {**GOOD, "Strict-Transport-Security": "max-age=31536000"}
        state, _ = cc.check_headers(FakeStack({("GET", "/"): (200, "", headers(hsts)), ("GET", "/api/info"): (200, "", headers(hsts))}))
        self.assertEqual(cc.OK, state)

        framed = {k: v for k, v in GOOD.items() if k != "X-Frame-Options"}
        state, detail = cc.check_headers(FakeStack({("GET", "/"): (200, "", headers(framed)), ("GET", "/api/info"): (200, "", headers(hsts))}))
        self.assertEqual(cc.FAIL, state)
        self.assertIn("/ X-Frame-Options: missing", detail)

    def test_A_server_error_is_unknown_not_a_pass(self):
        state, _ = cc.check_headers(FakeStack({("GET", "/"): (502, "", headers({}))}))
        self.assertEqual(cc.UNKNOWN, state)


class Cookies(unittest.TestCase):
    def test_The_session_cookie_needs_Secure_HttpOnly_and_SameSite(self):
        self.assertEqual(cc.OK, cc.check_cookies("llm_session=x; path=/; secure; samesite=lax; httponly", None)[0])
        state, detail = cc.check_cookies("llm_session=x; path=/; samesite=lax", None)
        self.assertEqual(cc.FAIL, state)
        self.assertIn("secure, httponly", detail)
        self.assertEqual(cc.FAIL, cc.check_cookies("llm_session=x; secure; httponly; samesite=none", None)[0])

    def test_Without_a_sign_in_it_is_unknown_and_says_why(self):
        self.assertEqual((cc.UNKNOWN, "ADMIN_PASSWORD is not in .env."), cc.check_cookies(None, "ADMIN_PASSWORD is not in .env."))


class Csrf(unittest.TestCase):
    def test_Only_the_apps_own_refusal_passes(self):
        refused = FakeStack({("POST", "/api/auth/logout"): (400, '{"status":"csrf"}', headers({}))})
        self.assertEqual(cc.OK, cc.check_csrf(refused)[0])
        taken = FakeStack({("POST", "/api/auth/logout"): (204, "", headers({}))})
        self.assertEqual(cc.FAIL, cc.check_csrf(taken)[0])


class AppChecks(unittest.TestCase):
    def test_Each_of_the_apps_checks_is_reported_by_its_id(self):
        body = json.dumps({"checks": [{"id": "backups", "state": "fail", "detail": "No backup yet."}, {"id": "sessions", "state": "ok", "detail": "Idle 1 hour."}]})
        got = cc.app_checks(FakeStack({("GET", "/api/admin/compliance"): (200, body, headers({}))}), "llm_session=x", None)
        self.assertEqual([("app:backups", "fail", "No backup yet."), ("app:sessions", "ok", "Idle 1 hour.")], got)
        missing = cc.app_checks(FakeStack({("GET", "/api/admin/compliance"): (404, "", headers({}))}), "llm_session=x", None)
        self.assertEqual("unknown", missing[0][1])


class Disk(unittest.TestCase):
    def answers(self, source, types):
        def run(args, **_):
            out = source if args[0] == "findmnt" else "\n".join(types)
            return mock.Mock(stdout=out)
        return mock.patch.object(cc.subprocess, "run", run)

    def test_The_data_on_a_dm_crypt_device_is_encrypted_and_otherwise_says_what_to_do(self):
        with self.answers("/dev/mapper/data", ["crypt", "part", "disk"]):
            self.assertEqual(cc.OK, cc.check_disk("/srv/arena")[0])
        with self.answers("/dev/nvme0n1p2", ["part", "disk"]):
            state, detail = cc.check_disk("/srv/arena")
            self.assertEqual(cc.WARN, state)
            self.assertIn("LUKS", detail)
        with self.answers("tank/arena", []):
            self.assertEqual(cc.WARN, cc.check_disk("/srv/arena")[0])
        self.assertEqual(cc.UNKNOWN, cc.check_disk("")[0])


class ExitCode(unittest.TestCase):
    def run_with(self, states, *args):
        def fake(state):
            return lambda *_: (state, "said")

        patches = [mock.patch.object(cc, f"check_{n}", fake(s)) for n, s in zip(("tls", "headers", "csrf", "disk", "cookies"), states)]
        patches += [mock.patch.object(cc, "sign_in", lambda _: ("llm_session=x", None)),
                    mock.patch.object(cc, "app_checks", lambda *_: [("app:backups", states[5], "said")]),
                    mock.patch.object(cc.Stack, "req", lambda *_a, **_k: (204, "", headers({}))),
                    mock.patch("sys.argv", ["compliance-check.py", *args])]
        for p in patches:
            p.start()
        try:
            with redirect_stdout(io.StringIO()) as out, self.assertRaises(SystemExit) as done:
                cc.main()
            return done.exception.code, out.getvalue()
        finally:
            for p in patches:
                p.stop()

    def test_A_failed_check_fails_the_run_and_warnings_only_with_strict(self):
        self.assertEqual(0, self.run_with(["ok", "warn", "ok", "ok", "ok", "unknown"])[0])
        self.assertEqual(1, self.run_with(["ok", "warn", "ok", "ok", "ok", "unknown"], "--strict")[0])
        code, said = self.run_with(["ok", "ok", "ok", "ok", "ok", "fail"])
        self.assertEqual(1, code)
        self.assertIn("failing: app:backups", said)

    def test_The_ids_are_those_controls_yaml_names(self):
        self.assertEqual(("tls", "headers", "cookies", "csrf", "disk"), cc.LIVE_CHECKS)
        _, said = self.run_with(["ok"] * 6, "--json")
        self.assertEqual(["live:tls", "live:headers", "live:csrf", "live:disk", "live:cookies", "app:backups"], [r["id"] for r in json.loads(said)])


if __name__ == "__main__":
    unittest.main()
