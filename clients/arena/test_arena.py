"""The arena CLI and the GitLab CI template, against a fake gateway and a fake GitLab on a local socket.

python3 -m unittest discover clients/arena    (no network, no packages; the template's tests need PyYAML and skip without it)
"""

import importlib.machinery
import importlib.util
import io
import json
import os
import re
import shutil
import ssl
import subprocess
import sys
import tempfile
import threading
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, unquote, urlsplit

HERE = os.path.dirname(os.path.abspath(__file__))
CLI = os.path.join(HERE, "arena")
TEMPLATE = os.path.join(HERE, "..", "gitlab-ci", "arena-review.yml")
KEY = "sk-test-key-0123456789"
TOKEN = "glpat-test-token"


def load_cli():
    loader = importlib.machinery.SourceFileLoader("arena_cli", CLI)
    spec = importlib.util.spec_from_loader("arena_cli", loader)
    module = importlib.util.module_from_spec(spec)
    loader.exec_module(module)
    return module


arena = load_cli()


def setUpModule():
    # Every request here goes to 127.0.0.1: a proxy from the environment would get in the way.
    for name in ("http_proxy", "https_proxy", "all_proxy", "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY"):
        os.environ.pop(name, None)


class World:
    """What the fake gateway and the fake GitLab hold, and every request they got."""

    def __init__(self):
        self.requests = []
        self.models = ["FLUX.2-klein-4B", "Big-Model", "Small-Model"]
        self.modes = {"FLUX.2-klein-4B": "image_generation", "Big-Model": "chat", "Small-Model": "chat"}
        self.model_info_status = 200
        self.chat_status = 200
        self.stream_error = None
        self.think = False
        self.answer = None
        self.html = False
        self.mr = {"iid": 7, "title": "Parse the frame header once", "description": "Fixes the double read.",
                   "source_branch": "fix/header", "target_branch": "main", "author": {"username": "dev_alpha"},
                   "sha": "fedcba9876543210", "web_url": "https://gitlab.test/eal/eal-core/-/merge_requests/7"}
        self.diffs = [
            {"old_path": "src/decoder.c", "new_path": "src/decoder.c", "diff": "@@ -1,2 +1,2 @@\n-read(h);\n-read(h);\n+read(h);\n"},
            {"old_path": "src/new.h", "new_path": "src/new.h", "new_file": True, "diff": "@@ -0,0 +1 @@\n+#pragma once\n"},
            {"old_path": "old.txt", "new_path": "old.txt", "deleted_file": True, "diff": "@@ -1 +0,0 @@\n-gone\n"},
        ]
        self.diffs_endpoint = True
        self.notes = []
        self.jobs = [
            {"id": 101, "name": "build", "stage": "build", "status": "failed", "failure_reason": "script_failure"},
            {"id": 102, "name": "lint", "stage": "test", "status": "success"},
            {"id": 103, "name": "arena-explain", "stage": ".post", "status": "failed"},
        ]
        self.traces = {101: "section_start:1700000000:step_script\r\x1b[0K\x1b[32;1m$ make\x1b[0;m\n"
                            "50%\r100%\ncc -c decoder.c\ndecoder.c:12: error: 'frame' undeclared\nmake: *** [all] Error 1\n"
                            "section_end:1700000001:step_script\r\x1b[0K\n"}
        self.open_mrs = []
        self.files = {}

    def gateway(self, method):
        return [r for r in self.requests if r["path"].startswith("/v1/") and r["method"] == method]

    def chats(self):
        return [r["body"] for r in self.requests if r["path"] == "/v1/chat/completions"]

    def gitlab(self):
        return [r for r in self.requests if r["path"].startswith("/api/v4/")]


class Handler(BaseHTTPRequestHandler):
    """The gateway under /v1, GitLab under /api/v4, and a plain file server under /cli."""

    def log_message(self, *args):
        pass

    def do_GET(self):
        self.route("GET")

    def do_POST(self):
        self.route("POST")

    def route(self, method):
        world = self.server.world
        url = urlsplit(self.path)
        length = int(self.headers.get("Content-Length") or 0)
        raw = self.rfile.read(length) if length else b""
        body = json.loads(raw) if raw else None
        query = {k: v[0] for k, v in parse_qs(url.query).items()}
        world.requests.append({"method": method, "path": url.path, "query": query, "headers": {k.lower(): v for k, v in self.headers.items()}, "body": body})
        if url.path.startswith("/v1/"):
            return self.gateway(world, method, url.path, body)
        if url.path.startswith("/api/v4/"):
            return self.gitlab(world, method, url.path, query, body)
        if url.path.startswith("/cli/"):
            return self.send(200, world.files.get(url.path, b""), "text/plain")
        self.send(404, {"message": "404 Not Found"})

    def send(self, status, payload, kind="application/json", headers=None):
        data = payload if isinstance(payload, bytes) else (payload.encode() if isinstance(payload, str) else json.dumps(payload).encode())
        self.send_response(status)
        self.send_header("Content-Type", kind)
        self.send_header("Content-Length", str(len(data)))
        for name, value in (headers or {}).items():
            self.send_header(name, value)
        self.end_headers()
        self.wfile.write(data)

    def gateway(self, world, method, path, body):
        if world.html:
            return self.send(200, "<!doctype html><title>Sign in</title>", "text/html")
        if self.headers.get("Authorization") != "Bearer " + KEY:
            return self.send(401, {"error": {"message": "Authentication Error, Invalid proxy server token passed.", "type": "auth_error", "code": "401"}})
        if path == "/v1/models":
            return self.send(200, {"data": [{"id": m, "object": "model"} for m in world.models]})
        if path == "/v1/model/info":
            if world.model_info_status != 200:
                return self.send(world.model_info_status, {"error": {"message": "Only admins may see this."}})
            return self.send(200, {"data": [{"model_name": m, "model_info": {"mode": world.modes.get(m)}} for m in world.models]})
        if path == "/v1/chat/completions" and method == "POST":
            if world.chat_status != 200:
                return self.send(world.chat_status, {"error": {"message": "Budget has been exceeded! Current cost: 5.0, Max budget: 5.0", "code": str(world.chat_status)}})
            question = body["messages"][-1]["content"]
            answer = world.answer or "Answer to: " + question.split("\n")[0][:30]
            self.send_response(200)
            self.send_header("Content-Type", "text/event-stream")
            self.end_headers()
            events = [{"choices": [{"delta": {"role": "assistant"}}]}]
            if world.think:
                events.append({"choices": [{"delta": {"reasoning_content": "Let me look."}}]})
            third = max(1, len(answer) // 3)
            events += [{"choices": [{"delta": {"content": answer[i:i + third]}}]} for i in range(0, len(answer), third)]
            if world.stream_error:
                events.append({"error": {"message": world.stream_error}})
            for event in events:
                self.wfile.write(("data: %s\n\n" % json.dumps(event)).encode())
                self.wfile.flush()
            self.wfile.write(b"data: [DONE]\n\n")
            return None
        return self.send(404, {"error": {"message": "Not Found"}})

    def gitlab(self, world, method, path, query, body):
        if self.headers.get("PRIVATE-TOKEN") != TOKEN:
            return self.send(401, {"message": "401 Unauthorized"})
        m = re.match(r"^/api/v4/projects/([^/]+)/(.*)$", path)
        if not m:
            return self.send(404, {"message": "404 Not Found"})
        project, rest = unquote(m.group(1)), m.group(2)
        if project not in ("42", "eal/eal-core", "platform/argus-arena"):
            return self.send(404, {"message": "404 Project Not Found"})
        if rest == "merge_requests/7" and method == "GET":
            return self.send(200, world.mr)
        if rest == "merge_requests/7/diffs":
            if not world.diffs_endpoint:
                return self.send(404, {"error": "404 Not Found"})
            page = int(query.get("page", "1"))
            rows = world.diffs[(page - 1) * 2:page * 2]
            more = len(world.diffs) > page * 2
            return self.send(200, rows, headers={"X-Next-Page": str(page + 1) if more else ""})
        if rest == "merge_requests/7/changes":
            return self.send(200, dict(world.mr, changes=world.diffs))
        if rest == "merge_requests/7/notes" and method == "POST":
            world.notes.append(body["body"])
            return self.send(201, {"id": 900 + len(world.notes), "body": body["body"]})
        if rest == "merge_requests" and method == "GET":
            return self.send(200, [mr for mr in world.open_mrs if mr["source_branch"] == query.get("source_branch") and query.get("state") == "opened"])
        jobs = re.match(r"^pipelines/(\d+)/jobs$", rest)
        if jobs:
            rows = [j for j in world.jobs if query.get("scope[]") in (None, j["status"])] if jobs.group(1) == "555" else []
            return self.send(200, rows, headers={"X-Next-Page": ""})
        trace = re.match(r"^jobs/(\d+)/trace$", rest)
        if trace and int(trace.group(1)) in world.traces:
            return self.send(200, world.traces[int(trace.group(1))], "text/plain")
        raw = re.match(r"^repository/files/([^/]+)/raw$", rest)
        if raw and (project, unquote(raw.group(1)), query.get("ref")) in world.files:
            return self.send(200, world.files[(project, unquote(raw.group(1)), query.get("ref"))], "text/plain")
        return self.send(404, {"message": "404 Not Found"})


class Server:
    """The fakes on 127.0.0.1, over TLS when given a certificate."""

    def __init__(self, cert=None):
        self.httpd = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.httpd.daemon_threads = True
        self.httpd.world = World()
        scheme = "http"
        if cert:
            context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
            context.load_cert_chain(*cert)
            self.httpd.socket = context.wrap_socket(self.httpd.socket, server_side=True)
            scheme = "https"
        self.url = "%s://127.0.0.1:%d" % (scheme, self.httpd.server_address[1])
        self.thread = threading.Thread(target=self.httpd.serve_forever, daemon=True)
        self.thread.start()

    @property
    def world(self):
        return self.httpd.world

    def reset(self):
        self.httpd.world = World()

    def close(self):
        self.httpd.shutdown()
        self.httpd.server_close()


class Case(unittest.TestCase):
    server = None

    @classmethod
    def setUpClass(cls):
        cls.server = Server()

    @classmethod
    def tearDownClass(cls):
        cls.server.close()

    def setUp(self):
        self.server.reset()
        self.tmp = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.tmp)

    @property
    def world(self):
        return self.server.world

    def env(self, **more):
        env = {
            "ARENA_URL": self.server.url, "ARENA_KEY": KEY,
            "CI_SERVER_URL": self.server.url, "CI_API_V4_URL": self.server.url + "/api/v4", "CI_PROJECT_ID": "42",
            "CI_MERGE_REQUEST_IID": "7", "CI_COMMIT_SHA": "0123456789abcdef", "ARENA_GITLAB_TOKEN": TOKEN,
        }
        env.update(more)
        return {k: v for k, v in env.items() if v is not None}

    def arena(self, *argv, stdin="", **env):
        out, err = io.StringIO(), io.StringIO()
        code = arena.main(list(argv), self.env(**env), io.StringIO(stdin), out, err)
        return code, out.getvalue(), err.getvalue()

    def file(self, name, text):
        path = os.path.join(self.tmp, name)
        with open(path, "w", encoding="utf-8") as f:
            f.write(text)
        return path


class Ask(Case):
    def test_ask_streams_the_answer_with_piped_text_added_and_the_first_chat_model_the_key_may_use(self):
        self.world.think = True
        code, out, err = self.arena("ask", "What", "does", "this", "do?", stdin="int main() { return 0; }\n")
        self.assertEqual(code, 0, err)
        self.assertEqual(out, "Answer to: What does this do?\n")
        self.assertIn("(thinking)", err)
        chat = self.world.chats()[0]
        self.assertTrue(chat["stream"])
        self.assertEqual(chat["model"], "Big-Model", "the picture model listed first is not a chat model")
        self.assertEqual(chat["messages"], [{"role": "user", "content": "What does this do?\n\nint main() { return 0; }\n"}])
        self.assertEqual(self.world.requests[0]["headers"]["authorization"], "Bearer " + KEY)

    def test_ask_takes_the_model_from_arena_model_or_the_flag_and_the_address_with_v1(self):
        code, _, err = self.arena("ask", "hi", ARENA_MODEL="Small-Model", ARENA_URL=self.server.url + "/v1/")
        self.assertEqual(code, 0, err)
        self.assertEqual(self.world.chats()[-1]["model"], "Small-Model")
        self.assertEqual(self.world.gateway("GET"), [], "a named model needs no lookup")
        code, _, _ = self.arena("ask", "--model", "Other", "--system", "Be brief.", "hi")
        self.assertEqual(code, 0)
        self.assertEqual(self.world.chats()[-1]["model"], "Other")
        self.assertEqual(self.world.chats()[-1]["messages"][0], {"role": "system", "content": "Be brief."})

    def test_ask_uses_the_model_list_alone_when_the_gateway_keeps_model_info_to_admins(self):
        self.world.model_info_status = 403
        self.world.models = ["Small-Model", "Big-Model"]
        code, _, err = self.arena("ask", "hi")
        self.assertEqual(code, 0, err)
        self.assertEqual(self.world.chats()[0]["model"], "Small-Model")

    def test_ask_cuts_piped_text_to_the_budget_and_says_so(self):
        code, _, _ = self.arena("ask", "--max-chars", "100", "Summarise", stdin="x" * 500)
        self.assertEqual(code, 0)
        content = self.world.chats()[0]["messages"][0]["content"]
        self.assertIn("x" * 100 + "\n[cut to fit: the first 100 of 500 characters]", content)
        self.assertNotIn("x" * 101, content)

    def test_ask_with_nothing_to_ask_is_a_usage_error(self):
        code, _, err = self.arena("ask", stdin="  \n")
        self.assertEqual(code, arena.USAGE)
        self.assertIn("Ask something", err)
        self.assertEqual(self.world.requests, [])

    def test_a_refused_key_exits_4_with_the_gateways_words_and_never_prints_the_key(self):
        code, out, err = self.arena("ask", "hi", ARENA_KEY="sk-wrong-key", ARENA_MODEL="Big-Model")
        self.assertEqual(code, arena.GATEWAY)
        self.assertIn("refused the key (401)", err)
        self.assertIn("Invalid proxy server token passed.", err)
        self.assertNotIn("sk-wrong-key", err + out)

    def test_used_up_credit_exits_4_with_the_reason(self):
        self.world.chat_status = 400
        code, _, err = self.arena("ask", "hi")
        self.assertEqual(code, arena.GATEWAY)
        self.assertIn("Budget has been exceeded", err)

    def test_an_error_in_the_stream_exits_4_after_what_was_written(self):
        self.world.stream_error = "The engine stopped."
        code, out, err = self.arena("ask", "hi")
        self.assertEqual(code, arena.GATEWAY)
        self.assertTrue(out.startswith("Answer to"))
        self.assertIn("The engine stopped.", err)

    def test_the_apps_address_instead_of_the_gateways_says_which_address_to_use(self):
        self.world.html = True
        code, _, err = self.arena("ask", "hi")
        self.assertEqual(code, arena.GATEWAY)
        self.assertIn("https://gateway.DOMAIN", err)

    def test_a_gateway_that_cannot_be_reached_exits_4(self):
        code, _, err = self.arena("ask", "hi", ARENA_URL="http://127.0.0.1:9")
        self.assertEqual(code, arena.GATEWAY)
        self.assertIn("cannot be reached", err)

    def test_missing_or_wrong_configuration_exits_3(self):
        code, _, err = self.arena("ask", "hi", ARENA_URL=None, ARENA_KEY=None)
        self.assertEqual(code, arena.CONFIG)
        self.assertIn("ARENA_URL and ARENA_KEY", err)
        code, _, err = self.arena("ask", "hi", ARENA_URL="gateway.example.com")
        self.assertEqual(code, arena.CONFIG)
        self.assertIn("https://", err)
        code, _, err = self.arena("ask", "hi", ARENA_TIMEOUT="soon")
        self.assertEqual(code, arena.CONFIG)

    def test_the_script_runs_as_a_command_and_documents_its_exit_codes(self):
        clean = {"PATH": os.environ.get("PATH", "/usr/bin:/bin")}
        run = subprocess.run([sys.executable, CLI, "ask", "hi"], env=clean, stdin=subprocess.DEVNULL, capture_output=True, text=True, timeout=60)
        self.assertEqual(run.returncode, 3, run.stderr)
        self.assertIn("arena: Set ARENA_URL and ARENA_KEY", run.stderr)
        run = subprocess.run([sys.executable, CLI, "--help"], env=clean, capture_output=True, text=True, timeout=60)
        self.assertEqual(run.returncode, 0)
        for line in ("3    configuration missing", "4    the gateway failed", "5    GitLab failed"):
            self.assertIn(line, run.stdout)
        run = subprocess.run([sys.executable, CLI], env=clean, capture_output=True, text=True, timeout=60)
        self.assertEqual(run.returncode, 2)
        run = subprocess.run([sys.executable, CLI, "ask"], env=dict(clean, ARENA_URL=self.server.url, ARENA_KEY=KEY),
                             input="What is 2+2?", capture_output=True, text=True, timeout=60)
        self.assertEqual(run.returncode, 0, run.stderr)
        self.assertEqual(run.stdout, "Answer to: What is 2+2?\n")


class Review(Case):
    def test_a_review_reads_every_page_of_changes_and_posts_one_note(self):
        code, out, err = self.arena("review")
        self.assertEqual(code, 0, err)
        self.assertTrue(out.startswith("Answer to: Merge request !7"))
        diffs = [r for r in self.world.gitlab() if r["path"].endswith("/diffs")]
        self.assertEqual([r["query"]["page"] for r in diffs], ["1", "2"])
        self.assertTrue(all(r["headers"]["private-token"] == TOKEN for r in self.world.gitlab()))
        chat = self.world.chats()[0]
        self.assertEqual(chat["messages"][0], {"role": "system", "content": arena.REVIEW_PROMPT})
        question = chat["messages"][1]["content"]
        for part in ("Parse the frame header once", "Fixes the double read.", "fix/header into main, by dev_alpha",
                     "--- a/src/decoder.c\n+++ b/src/decoder.c", "--- /dev/null\n+++ b/src/new.h", "--- a/old.txt\n+++ /dev/null"):
            self.assertIn(part, question)
        self.assertNotIn("cut to fit", question)
        self.assertEqual(len(self.world.notes), 1)
        note = self.world.notes[0]
        self.assertTrue(note.startswith("Answer to: Merge request !7"))
        self.assertIn("_Review by Argus Arena (Big-Model) of fedcba98._", note)
        self.assertIn("#note_901", err)

    def test_a_dry_run_prints_the_review_and_posts_nothing(self):
        code, out, err = self.arena("review", "--dry-run")
        self.assertEqual(code, 0, err)
        self.assertIn("Answer to:", out)
        self.assertIn("Dry run: nothing posted", err)
        self.assertEqual(self.world.notes, [])

    def test_a_large_change_is_cut_to_the_budget_with_a_note_to_the_model_and_on_the_merge_request(self):
        self.world.diffs = [
            {"old_path": "a.py", "new_path": "a.py", "diff": "+a = 1\n"},
            {"old_path": "package-lock.json", "new_path": "package-lock.json", "generated_file": True, "diff": "+" + "l" * 900 + "\n"},
            {"old_path": "big.py", "new_path": "big.py", "diff": "+" + "b" * 5000 + "\n"},
            {"old_path": "c.py", "new_path": "c.py", "diff": "+c = 3\n"},
            {"old_path": "huge.py", "new_path": "huge.py", "diff": "+" + "h" * 9000 + "\n"},
        ]
        code, _, err = self.arena("review", "--max-chars", "2100")
        self.assertEqual(code, 0, err)
        question = self.world.chats()[0]["messages"][1]["content"]
        self.assertIn("+a = 1", question)
        self.assertIn("+c = 3", question)
        self.assertIn("l" * 900, question, "the generated file fits after the others")
        self.assertIn("b" * 500 + "\n[cut to fit]", question)
        self.assertNotIn("h" * 10, question)
        self.assertIn("The changes were cut to fit: 3 of 5 files in full; big.py in part; left out: huge.py.", question)
        self.assertIn("It saw 3 of 5 changed files in full, big.py in part; left out: huge.py (ARENA_MAX_CHARS is 2100).", self.world.notes[0])
        self.assertLess(question.index("c.py"), question.index("package-lock.json"), "generated files come last")

    def test_one_file_larger_than_the_budget_is_still_reviewed_in_part(self):
        self.world.diffs = [{"old_path": "huge.py", "new_path": "huge.py", "diff": "+" + "h" * 9000 + "\n"}]
        self.world.mr["description"] = "d" * 5000
        code, _, err = self.arena("review", "--max-chars", "300", "--dry-run")
        self.assertEqual(code, 0, err)
        question = self.world.chats()[0]["messages"][1]["content"]
        self.assertIn("h" * 200 + "\n[cut to fit]", question)
        self.assertIn("0 of 1 files in full; huge.py in part.", question)
        self.assertIn("d" * 4000 + "\n[cut to fit]", question, "a long description is cut too")
        self.assertNotIn("d" * 4001, question)

    def test_an_older_gitlab_without_the_diffs_endpoint_is_read_through_changes(self):
        self.world.diffs_endpoint = False
        code, _, err = self.arena("review")
        self.assertEqual(code, 0, err)
        self.assertIn("src/new.h", self.world.chats()[0]["messages"][1]["content"])
        self.assertEqual(len(self.world.notes), 1)

    def test_the_instructions_come_from_the_flag_a_file_or_the_environment(self):
        self.arena("review", "--dry-run", ARENA_REVIEW_PROMPT="Check the locking only.")
        self.assertEqual(self.world.chats()[-1]["messages"][0]["content"], "Check the locking only.")
        path = self.file("review.md", "Our rules: no globals.\n")
        self.arena("review", "--dry-run", ARENA_REVIEW_PROMPT_FILE=path, ARENA_REVIEW_PROMPT="ignored")
        self.assertEqual(self.world.chats()[-1]["messages"][0]["content"], "Our rules: no globals.")
        self.arena("review", "--dry-run", "--prompt", "From the flag.", ARENA_REVIEW_PROMPT_FILE=path)
        self.assertEqual(self.world.chats()[-1]["messages"][0]["content"], "From the flag.")
        code, _, err = self.arena("review", "--dry-run", "--prompt-file", os.path.join(self.tmp, "missing.md"))
        self.assertEqual(code, arena.CONFIG)
        self.assertIn("missing.md", err)

    def test_a_review_outside_a_merge_request_pipeline_or_without_a_token_exits_3(self):
        code, _, err = self.arena("review", CI_MERGE_REQUEST_IID=None)
        self.assertEqual(code, arena.CONFIG)
        self.assertIn("CI_MERGE_REQUEST_IID", err)
        code, _, err = self.arena("review", ARENA_GITLAB_TOKEN=None, CI_PROJECT_ID=None)
        self.assertEqual(code, arena.CONFIG)
        self.assertIn("CI_PROJECT_ID, ARENA_GITLAB_TOKEN", err)
        self.assertEqual(self.world.requests, [])

    def test_a_token_gitlab_refuses_exits_5_and_nothing_is_asked(self):
        code, _, err = self.arena("review", ARENA_GITLAB_TOKEN="glpat-wrong")
        self.assertEqual(code, arena.GITLAB)
        self.assertIn("GitLab refused ARENA_GITLAB_TOKEN (401)", err)
        self.assertNotIn("glpat-wrong", err)
        self.assertEqual(self.world.chats(), [])

    def test_a_merge_request_that_does_not_exist_exits_5(self):
        code, _, err = self.arena("review", "--mr", "8")
        self.assertEqual(code, arena.GITLAB)
        self.assertIn("(404)", err)

    def test_a_project_path_and_merge_request_from_flags_work_outside_ci(self):
        code, _, err = self.arena("review", "--dry-run", "--project", "eal/eal-core", "--mr", "7", CI_PROJECT_ID=None, CI_MERGE_REQUEST_IID=None)
        self.assertEqual(code, 0, err)
        self.assertEqual(self.world.gitlab()[0]["path"], "/api/v4/projects/eal%2Feal-core/merge_requests/7")

    def test_a_merge_request_without_changes_is_not_sent_to_the_model(self):
        self.world.diffs = []
        code, _, err = self.arena("review")
        self.assertEqual(code, 0)
        self.assertIn("nothing to review", err)
        self.assertEqual(self.world.chats(), [])
        self.assertEqual(self.world.notes, [])

    def test_thinking_written_into_the_answer_stays_out_of_the_note(self):
        self.world.answer = "<think>Hmm, the header.</think>\nRead twice at line 2."
        code, _, err = self.arena("review")
        self.assertEqual(code, 0, err)
        self.assertTrue(self.world.notes[0].startswith("Read twice at line 2."))


class ExplainFailure(Case):
    def test_a_log_file_is_cleaned_cut_to_its_end_and_explained(self):
        log = self.file("job.log", "\n".join("line %d" % i for i in range(2000)) + "\n\x1b[31mERROR: disk full\x1b[0m\n")
        code, out, err = self.arena("explain-failure", log, "--max-chars", "1500")
        self.assertEqual(code, 0, err)
        self.assertTrue(out.startswith("Answer to: The end of job.log"))
        chat = self.world.chats()[0]
        self.assertEqual(chat["messages"][0]["content"], arena.EXPLAIN_PROMPT)
        question = chat["messages"][1]["content"]
        self.assertIn("[the start of the log is cut to fit]", question)
        self.assertIn("ERROR: disk full", question)
        self.assertNotIn("\x1b", question)
        self.assertNotIn("line 5\n", question)
        self.assertEqual(self.world.gitlab(), [], "a log file needs no GitLab")

    def test_a_log_on_stdin_is_explained(self):
        code, _, err = self.arena("explain-failure", "-", stdin="npm ERR! missing script: test\n", CI_API_V4_URL=None)
        self.assertEqual(code, 0, err)
        self.assertIn("npm ERR! missing script: test", self.world.chats()[0]["messages"][1]["content"])

    def test_the_pipelines_failed_jobs_are_read_without_this_job_and_posted_on_the_merge_request(self):
        code, _, err = self.arena("explain-failure", "--post", CI_PIPELINE_ID="555", CI_JOB_ID="103", CI_PROJECT_PATH="eal/eal-core", CI_COMMIT_REF_NAME="fix/header")
        self.assertEqual(code, 0, err)
        traces = [r["path"] for r in self.world.gitlab() if r["path"].endswith("/trace")]
        self.assertEqual(traces, ["/api/v4/projects/42/jobs/101/trace"])
        question = self.world.chats()[0]["messages"][1]["content"]
        self.assertIn("Pipeline #555 of eal/eal-core failed on fix/header. Failed jobs: build (stage build, script failure).", question)
        self.assertIn("$ make\n100%\ncc -c decoder.c\ndecoder.c:12: error: 'frame' undeclared", question)
        self.assertNotIn("section_", question)
        self.assertNotIn("arena-explain", question)
        self.assertEqual(len(self.world.notes), 1)
        self.assertIn("_Explanation by Argus Arena (Big-Model) of pipeline #555._", self.world.notes[0])

    def test_in_a_branch_pipeline_the_explanation_goes_on_the_merge_request_open_from_the_branch(self):
        self.world.open_mrs = [{"iid": 7, "source_branch": "fix/header"}]
        code, _, err = self.arena("explain-failure", "--post", CI_MERGE_REQUEST_IID=None, CI_PIPELINE_ID="555", CI_JOB_ID="103", CI_COMMIT_BRANCH="fix/header")
        self.assertEqual(code, 0, err)
        self.assertEqual(len(self.world.notes), 1)
        self.world.notes.clear()
        code, _, err = self.arena("explain-failure", "--post", CI_MERGE_REQUEST_IID=None, CI_PIPELINE_ID="555", CI_JOB_ID="103", CI_COMMIT_BRANCH="main")
        self.assertEqual(code, 0, err)
        self.assertIn("in this job's log only", err)
        self.assertEqual(self.world.notes, [])

    def test_without_post_nothing_is_posted(self):
        code, _, err = self.arena("explain-failure", CI_PIPELINE_ID="555", CI_JOB_ID="103")
        self.assertEqual(code, 0, err)
        self.assertEqual(self.world.notes, [])

    def test_a_job_that_failed_before_it_ran_is_named_with_its_reason_and_no_log(self):
        # The running job itself is not failed, and a job stuck without a runner has no log.
        code, _, err = self.arena("explain-failure", CI_PIPELINE_ID="555", CI_JOB_ID="999")
        self.assertEqual(code, 0, err)
        self.world.jobs[2]["failure_reason"] = "stuck_or_timeout_failure"
        self.arena("explain-failure", CI_PIPELINE_ID="555", CI_JOB_ID="999")
        question = self.world.chats()[-1]["messages"][1]["content"]
        self.assertIn("arena-explain (stage .post, stuck or timeout failure)", question)
        self.assertIn("(no log: the job failed before it ran)", question)

    def test_a_pipeline_without_a_failed_job_has_nothing_to_explain(self):
        code, _, err = self.arena("explain-failure", CI_PIPELINE_ID="556")
        self.assertEqual(code, 0)
        self.assertIn("nothing to explain", err)
        self.assertEqual(self.world.chats(), [])

    def test_no_log_and_no_pipeline_exits_3_and_a_missing_log_too(self):
        code, _, err = self.arena("explain-failure")
        self.assertEqual(code, arena.CONFIG)
        self.assertIn("CI_PIPELINE_ID", err)
        code, _, err = self.arena("explain-failure", os.path.join(self.tmp, "nope.log"))
        self.assertEqual(code, arena.CONFIG)
        self.assertIn("nope.log", err)


def openssl(*args, cwd):
    subprocess.run(["openssl", *args], cwd=cwd, check=True, capture_output=True, timeout=60)


@unittest.skipUnless(shutil.which("openssl"), "openssl makes the test CA")
class PrivateCa(unittest.TestCase):
    """A gateway behind a certificate from a CA of its own, as scripts/make-cert.sh makes."""

    @classmethod
    def setUpClass(cls):
        cls.dir = tempfile.mkdtemp()
        with open(os.path.join(cls.dir, "server.ext"), "w") as f:
            f.write("basicConstraints=CA:FALSE\nkeyUsage=critical,digitalSignature,keyEncipherment\nextendedKeyUsage=serverAuth\n"
                    "subjectAltName=IP:127.0.0.1,DNS:localhost\nsubjectKeyIdentifier=hash\nauthorityKeyIdentifier=keyid,issuer\n")
        openssl("req", "-x509", "-newkey", "rsa:2048", "-nodes", "-days", "2", "-subj", "/CN=arena test CA", "-keyout", "ca.key", "-out", "ca.crt",
                "-addext", "basicConstraints=critical,CA:TRUE", "-addext", "keyUsage=critical,keyCertSign,cRLSign", cwd=cls.dir)
        openssl("req", "-newkey", "rsa:2048", "-nodes", "-subj", "/CN=127.0.0.1", "-keyout", "server.key", "-out", "server.csr", cwd=cls.dir)
        openssl("x509", "-req", "-in", "server.csr", "-CA", "ca.crt", "-CAkey", "ca.key", "-CAcreateserial", "-days", "2",
                "-extfile", "server.ext", "-out", "server.crt", cwd=cls.dir)
        cls.ca = os.path.join(cls.dir, "ca.crt")
        cls.server = Server((os.path.join(cls.dir, "server.crt"), os.path.join(cls.dir, "server.key")))

    @classmethod
    def tearDownClass(cls):
        cls.server.close()
        shutil.rmtree(cls.dir)

    def ask(self, **env):
        out, err = io.StringIO(), io.StringIO()
        env = dict({"ARENA_URL": self.server.url, "ARENA_KEY": KEY, "ARENA_MODEL": "Big-Model"}, **env)
        code = arena.main(["ask", "hi"], env, io.StringIO(""), out, err)
        return code, err.getvalue()

    def test_an_untrusted_certificate_exits_4_and_says_to_set_arena_ca_cert(self):
        code, err = self.ask()
        self.assertEqual(code, arena.GATEWAY)
        self.assertIn("certificate is not trusted", err)
        self.assertIn("ARENA_CA_CERT", err)

    def test_the_ca_from_arena_ca_cert_as_a_file_or_as_its_text_or_from_ssl_cert_file_is_trusted(self):
        with open(self.ca) as f:
            pem = f.read()
        for env in ({"ARENA_CA_CERT": self.ca}, {"ARENA_CA_CERT": pem}, {"SSL_CERT_FILE": self.ca}):
            code, err = self.ask(**env)
            self.assertEqual(code, 0, "%s: %s" % (list(env), err))

    def test_a_ca_file_that_cannot_be_read_exits_3(self):
        code, err = self.ask(ARENA_CA_CERT=os.path.join(self.dir, "missing.crt"))
        self.assertEqual(code, arena.CONFIG)
        self.assertIn("ARENA_CA_CERT", err)


try:
    import yaml
except ImportError:
    yaml = None


@unittest.skipUnless(yaml, "PyYAML reads the template")
class Template(Case):
    """The CI template's jobs, run with sh as a runner would, against the same fakes."""

    def setUp(self):
        super().setUp()
        with open(TEMPLATE, encoding="utf-8") as f:
            self.ci = yaml.safe_load(f)
        with open(CLI, "rb") as f:
            self.world.files[("platform/argus-arena", "clients/arena/arena", "v4.2.0")] = f.read()
        # A PATH with python3 and mktemp only: no arena installed.
        self.bin = os.path.join(self.tmp, "bin")
        os.mkdir(self.bin)
        for tool in ("mktemp",):
            os.symlink(shutil.which(tool), os.path.join(self.bin, tool))
        os.symlink(sys.executable, os.path.join(self.bin, "python3"))

    def job(self, name, **env):
        job = self.ci[name]
        base = self.ci[job["extends"]]
        script = "\n".join(base["before_script"] + job["script"])
        run = subprocess.run(["/bin/sh", "-e", "-c", script], cwd=self.tmp, env=dict(self.env(**env), PATH=self.bin),
                             stdin=subprocess.DEVNULL, capture_output=True, text=True, timeout=120)
        return run

    def test_the_jobs_review_merge_requests_and_explain_failures_without_blocking(self):
        review, explain = self.ci["arena-review"], self.ci["arena-explain"]
        self.assertEqual(review["rules"], [{"if": '$CI_PIPELINE_SOURCE == "merge_request_event"'}])
        self.assertEqual(explain["rules"], [{"when": "on_failure"}])
        self.assertEqual(explain["stage"], ".post")
        self.assertTrue(review["allow_failure"] and explain["allow_failure"])
        self.assertEqual(review["needs"], [])
        self.assertIn("python", self.ci[".arena"]["image"])
        self.assertEqual(sorted(self.ci), [".arena", "arena-explain", "arena-review"],
                         "no stages, variables, default or workflow that would change the including pipeline")

    def test_the_review_job_fetches_the_cli_from_arena_project_and_posts_the_review(self):
        run = self.job("arena-review", ARENA_PROJECT="platform/argus-arena", ARENA_REF="v4.2.0")
        self.assertEqual(run.returncode, 0, run.stderr)
        fetch = [r for r in self.world.gitlab() if "/repository/files/" in r["path"]]
        self.assertEqual(len(fetch), 1)
        self.assertEqual(fetch[0]["path"], "/api/v4/projects/platform%2Fargus-arena/repository/files/clients%2Farena%2Farena/raw")
        self.assertEqual(fetch[0]["query"], {"ref": "v4.2.0"})
        self.assertEqual(len(self.world.notes), 1)
        self.assertIn("Review by Argus Arena (Big-Model)", self.world.notes[0])

    def test_the_review_job_runs_a_cli_kept_in_the_repository(self):
        shutil.copy(CLI, os.path.join(self.tmp, "arena-cli"))
        run = self.job("arena-review", ARENA_CLI="arena-cli")
        self.assertEqual(run.returncode, 0, run.stderr)
        self.assertEqual([r for r in self.world.gitlab() if "/repository/files/" in r["path"]], [])
        self.assertEqual(len(self.world.notes), 1)

    def test_the_gitlab_token_is_not_sent_to_another_host_serving_the_cli(self):
        with open(CLI, "rb") as f:
            self.world.files["/cli/arena"] = f.read()
        run = self.job("arena-review", ARENA_CLI=self.server.url + "/cli/arena", CI_SERVER_URL="https://gitlab.invalid")
        self.assertEqual(run.returncode, 0, run.stderr)
        fetch = [r for r in self.world.requests if r["path"] == "/cli/arena"]
        self.assertEqual(len(fetch), 1)
        self.assertNotIn("private-token", fetch[0]["headers"])

    def test_without_a_way_to_the_cli_the_job_says_what_to_set(self):
        run = self.job("arena-review")
        self.assertNotEqual(run.returncode, 0)
        self.assertIn("set ARENA_PROJECT", run.stderr)

    def test_the_explain_job_explains_the_failed_jobs_and_posts_only_when_asked(self):
        run = self.job("arena-explain", ARENA_PROJECT="platform/argus-arena", ARENA_REF="v4.2.0", CI_PIPELINE_ID="555", CI_JOB_ID="103")
        self.assertEqual(run.returncode, 0, run.stderr)
        self.assertIn("Answer to: Pipeline #555", run.stdout)
        self.assertEqual(self.world.notes, [])
        run = self.job("arena-explain", ARENA_PROJECT="platform/argus-arena", ARENA_REF="v4.2.0", CI_PIPELINE_ID="555", CI_JOB_ID="103",
                       ARENA_EXPLAIN_POST="true")
        self.assertEqual(run.returncode, 0, run.stderr)
        self.assertEqual(len(self.world.notes), 1)


if __name__ == "__main__":
    unittest.main()
