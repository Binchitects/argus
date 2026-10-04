"""
deploy/scripts/recovery-check.py: what a backup's dump holds, and the checks
through the API, against a small fake of the app on a real socket.
"""
from __future__ import annotations

import gzip
import importlib.util
import io
import json
import os
import shutil
import subprocess
import sys
import tempfile
import threading
import unittest
from contextlib import redirect_stdout
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

HERE = Path(__file__).resolve().parent
SCRIPT = HERE.parents[1] / "deploy" / "scripts" / "recovery-check.py"
spec = importlib.util.spec_from_file_location("recovery_check", SCRIPT)
rc = importlib.util.module_from_spec(spec)
spec.loader.exec_module(rc)

ADMIN, ANA = "11111111-0000-0000-0000-000000000001", "11111111-0000-0000-0000-000000000002"
DUMP = f"""--
-- PostgreSQL database cluster dump
--
CREATE ROLE arena;
\\connect template1
--
-- Database "litellm" dump
--
\\connect litellm
COPY public."LiteLLM_UserTable" (user_id, user_email) FROM stdin;
u1	admin@example.test
\\.
COPY public.settings (key, value) FROM stdin;
config:Branding:ProductName	Not the app's
\\.
--
-- Database "llmapp" dump
--
\\connect -reuse-previous=on "dbname='llmapp'"
COPY public."AspNetUsers" ("Id", "UserName", "Email", "DisplayName") FROM stdin;
{ADMIN}	admin	admin@example.test	Administrator
{ANA}	ana	ana@example.test	Ana\\tTab
\\.
COPY public.conversations ("Id", "UserId", "Title", "UpdatedAt", "ArchivedAt") FROM stdin;
c1	{ADMIN}	First	2026-10-01 10:00:00+00	\\N
c2	{ADMIN}	Newest	2026-10-03 10:00:00+00	\\N
c3	{ADMIN}	Old	2026-09-01 10:00:00+00	2026-09-02 10:00:00+00
c4	{ANA}	Hers	2026-10-02 10:00:00+00	\\N
\\.
COPY public.chat_messages ("Id", "ConversationId", "Role", "Content") FROM stdin;
m1	c2	user	hello\\nthere
m2	c2	assistant	hi
m3	c1	user	x
\\.
COPY public.settings (key, value, updated_at) FROM stdin;
config:Branding:ProductName	Arena\\\\Lab	2026-10-01
config:Chat:Secret	\\N	2026-10-01
config:Gone:Setting	old	2026-10-01
other:row	x	2026-10-01
\\.
COPY public."__EFMigrationsHistory" ("MigrationId", "ProductVersion") FROM stdin;
20261004040234_TaskTriggers	10.0.0
20261004033208_Plugins	10.0.0
\\.
--
-- PostgreSQL database cluster dump complete
--
"""


class FakeApp:
    """Just enough of the app's API: sign-in, people, groups, chats and settings."""

    def __init__(self):
        self.people = [{"userName": "admin"}, {"userName": "ana"}]
        self.warning = None
        self.groups: list[dict] = []
        self.chats = {"c1": {"id": "c1", "title": "First", "messages": [{"content": "x"}]},
                      "c2": {"id": "c2", "title": "Newest", "messages": [{"content": "hello"}, {"content": "hi"}]}}
        self.settings = {"Branding:ProductName": {"value": "Arena\\Lab", "source": "saved"},
                         "Chat:Secret": {"value": None, "source": "saved"},
                         "Branding:SignInHeadline": {"value": None, "source": "default"},
                         "Branding:SupportContact": {"value": None, "source": "default"}}
        self.login_state = "ok"
        app = self

        class Handler(BaseHTTPRequestHandler):
            def log_message(self, *a):
                pass

            def reply(self, status, body=None, cookie=False):
                data = json.dumps(body).encode() if body is not None else b""
                self.send_response(status)
                self.send_header("Content-Type", "application/json")
                if cookie:
                    self.send_header("Set-Cookie", "session=1; Path=/")
                self.end_headers()
                self.wfile.write(data)

            def body(self):
                n = int(self.headers.get("Content-Length") or 0)
                return json.loads(self.rfile.read(n) or b"null")

            def signed_in(self):
                return "session=1" in (self.headers.get("Cookie") or "")

            def do_GET(self):
                p = self.path
                if p == "/api/info":
                    return self.reply(200, {"name": "Arena", "version": "4.1.0"})
                if not self.signed_in():
                    return self.reply(401)
                if p == "/api/admin/people":
                    return self.reply(200, {"warning": app.warning, "people": app.people})
                if p == "/api/admin/groups":
                    return self.reply(200, app.groups)
                if p == "/api/chat/conversations":
                    return self.reply(200, [{"id": c["id"], "title": c["title"]} for c in app.chats.values()])
                if p.startswith("/api/chat/conversations/"):
                    c = app.chats.get(p.rsplit("/", 1)[1])
                    return self.reply(200, c) if c else self.reply(404)
                if p == "/api/admin/config":
                    rows = [{"key": k, **v} for k, v in app.settings.items()]
                    return self.reply(200, {"groups": [{"title": "All", "settings": rows}]})
                return self.reply(404)

            def do_POST(self):
                body = self.body()
                if self.path == "/api/auth/login":
                    if body.get("password") != "right":
                        return self.reply(401)
                    return self.reply(200, {"status": app.login_state}, cookie=app.login_state == "ok")
                if not self.signed_in():
                    return self.reply(401)
                if self.path == "/api/admin/people":
                    app.people.append({"userName": body["userName"]})
                    return self.reply(201, {"id": "p", "password": "generated"})
                if self.path == "/api/admin/groups":
                    app.groups.append({"id": "g", "name": body["name"]})
                    return self.reply(201, {"id": "g"})
                if self.path == "/api/chat/conversations":
                    cid = f"c{len(app.chats) + 10}"
                    app.chats[cid] = {"id": cid, "title": "New chat", "messages": []}
                    return self.reply(201, {"id": cid})
                return self.reply(404)

            def do_PATCH(self):
                c = app.chats.get(self.path.rsplit("/", 1)[1])
                if not self.signed_in() or not c:
                    return self.reply(404)
                c["title"] = self.body()["title"]
                return self.reply(204)

            def do_PUT(self):
                if self.path != "/api/admin/config" or not self.signed_in():
                    return self.reply(404)
                for ch in self.body()["changes"]:
                    app.settings[ch["key"]] = {"value": ch["value"], "source": "saved"}
                return self.reply(200, {})

        self.server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        threading.Thread(target=self.server.serve_forever, daemon=True).start()
        self.url = f"http://arena.test:{self.server.server_address[1]}"

    def stop(self):
        self.server.shutdown()
        self.server.server_close()


class FactsTests(unittest.TestCase):
    def test_The_facts_come_from_the_apps_database_only(self):
        f = rc.facts(io.StringIO(DUMP))
        self.assertEqual(f["database"], "llmapp")
        self.assertEqual(f["migration"], "20261004040234_TaskTriggers")
        self.assertEqual([p["userName"] for p in f["people"]], ["admin", "ana"])
        self.assertEqual(f["settings"], {"Branding:ProductName": "Arena\\Lab", "Chat:Secret": None, "Gone:Setting": "old"})
        mine = {c["id"]: c for c in f["chats"][ADMIN]}
        self.assertEqual(set(mine), {"c1", "c2", "c3"})
        self.assertTrue(mine["c3"]["archived"])
        self.assertEqual((mine["c2"]["messages"], mine["c1"]["messages"]), (2, 1))
        self.assertEqual([c["id"] for c in f["chats"][ANA]], ["c4"])

    def test_COPY_fields_are_unescaped_as_Postgres_writes_them(self):
        self.assertIsNone(rc.unescape(r"\N"))
        self.assertEqual(rc.unescape(r"a\tb\nc\\d"), "a\tb\nc\\d")
        self.assertEqual(rc.unescape(r"\101\x42"), "AB")
        self.assertEqual(rc.ident('public."AspNetUsers"'), "AspNetUsers")
        self.assertEqual(rc.ident("public.settings"), "settings")

    def test_A_dump_without_the_app_is_refused(self):
        with self.assertRaises(SystemExit):
            rc.facts(io.StringIO("\\connect litellm\nCOPY public.x (a) FROM stdin;\n1\n\\.\n"))

    def test_The_command_reads_a_gzipped_dump(self):
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, "postgres.sql.gz")
            with gzip.open(path, "wt") as f:
                f.write(DUMP)
            r = subprocess.run([sys.executable, str(SCRIPT), "facts", path], capture_output=True, text=True)
            self.assertEqual(r.returncode, 0, r.stderr)
            self.assertEqual(json.loads(r.stdout)["database"], "llmapp")


class ApiChecks(unittest.TestCase):
    def setUp(self):
        self.app = FakeApp()
        self.dir = tempfile.mkdtemp(prefix="recovery-check-")
        self.facts = os.path.join(self.dir, "facts.json")
        with open(self.facts, "w") as f:
            json.dump(rc.facts(io.StringIO(DUMP)), f)
        os.environ["RECOVERY_PASSWORD"] = "right"
        rc.results.clear()

    def tearDown(self):
        self.app.stop()
        os.environ.pop("RECOVERY_PASSWORD", None)
        shutil.rmtree(self.dir, ignore_errors=True)

    def run_main(self, *args: str) -> tuple[int, str]:
        out = io.StringIO()
        rc.results.clear()
        with redirect_stdout(out):
            code = rc.main(list(args))
        return code, out.getvalue()

    def restored(self) -> tuple[int, str]:
        return self.run_main("restored", "--url", self.app.url, "--connect", "127.0.0.1", "--facts", self.facts, "--user", "admin")

    def test_A_restore_that_holds_everything_passes(self):
        code, out = self.restored()
        self.assertEqual(code, 0, out)
        for line in ("admin signs in", "every person in the backup is there (2)", "spend and credit read from the restored gateway",
                     "admin's chats in the backup are there (2)", "the newest chat opens with its messages",
                     "every setting saved in the backup reads back saved, with its value (2)"):
            self.assertIn(f"PASS  {line}", out)
        self.assertNotIn("right", out)

    def test_A_person_lost_in_the_restore_fails_it(self):
        self.app.people.pop()
        code, out = self.restored()
        self.assertEqual(code, 1)
        self.assertIn("FAIL  every person in the backup is there (2)", out)
        self.assertIn("missing: ana", out)

    def test_A_chat_or_a_setting_lost_fails_it(self):
        del self.app.chats["c1"]
        self.app.settings["Branding:ProductName"] = {"value": "Arena", "source": "default"}
        code, out = self.restored()
        self.assertEqual(code, 1)
        self.assertIn("FAIL  admin's chats in the backup are there (2)", out)
        self.assertIn("not saved: Branding:ProductName", out)

    def test_Two_step_sign_in_and_a_wrong_password_are_named(self):
        self.app.login_state = "2fa"
        code, out = self.restored()
        self.assertEqual(code, 1)
        self.assertIn("two-step sign-in is on", out)
        os.environ["RECOVERY_PASSWORD"] = "wrong"
        self.app.login_state = "ok"
        code, out = self.restored()
        self.assertEqual(code, 1)
        self.assertIn("FAIL  admin signs in", out)

    def test_Wait_answers_at_the_version_asked_for_or_fails(self):
        code, out = self.run_main("wait", "--url", self.app.url, "--connect", "127.0.0.1", "--version", "4.1.0", "--minutes", "0.05")
        self.assertEqual(code, 0, out)
        code, out = self.run_main("wait", "--url", self.app.url, "--connect", "127.0.0.1", "--version", "4.0.0", "--minutes", "0.02")
        self.assertEqual(code, 1)

    def test_What_is_seeded_verifies_and_shows_up_as_present_when_it_should_be_absent(self):
        before = os.path.join(self.dir, "before.json")
        code, out = self.run_main("seed", "--url", self.app.url, "--connect", "127.0.0.1", "--state", before, "--label", "before", "--setting", "Branding:SignInHeadline")
        self.assertEqual(code, 0, out)
        state = json.loads(Path(before).read_text())
        self.assertEqual(len(state["people"]) + len(state["groups"]) + len(state["chats"]) + len(state["settings"]), 4)
        code, out = self.run_main("verify", "--url", self.app.url, "--connect", "127.0.0.1", "--state", before, "--when", "after the upgrade")
        self.assertEqual(code, 0, out)
        self.assertIn("PASS  after the upgrade: the chat is there, with its title", out)

        after = os.path.join(self.dir, "after.json")
        self.run_main("seed", "--url", self.app.url, "--connect", "127.0.0.1", "--state", after, "--label", "after", "--setting", "Branding:SupportContact")
        # Nothing was rolled back here: what went in after is still there, and the check says so.
        code, out = self.run_main("verify", "--url", self.app.url, "--connect", "127.0.0.1", "--state", before, "--absent", after, "--when", "after the rollback")
        self.assertEqual(code, 1)
        self.assertIn("FAIL  after the rollback: the person", out)
        self.assertIn("FAIL  after the rollback: the setting Branding:SupportContact saved after it is not", out)
        # Rolled back: the later person, group, chat and setting gone.
        later = json.loads(Path(after).read_text())
        self.app.people = [p for p in self.app.people if p["userName"] not in later["people"]]
        self.app.groups = [g for g in self.app.groups if g["name"] not in later["groups"]]
        for ch in later["chats"]:
            del self.app.chats[ch["id"]]
        self.app.settings["Branding:SupportContact"] = {"value": None, "source": "default"}
        code, out = self.run_main("verify", "--url", self.app.url, "--connect", "127.0.0.1", "--state", before, "--absent", after, "--when", "after the rollback")
        self.assertEqual(code, 0, out)


if __name__ == "__main__":
    unittest.main()
