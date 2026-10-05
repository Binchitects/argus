"""deploy/services/laya/server.py: what a request may hold, which checkpoint reads it, and the HTTP around it, with laya's predict stubbed (no model, no torch)."""
from __future__ import annotations

import http.client
import importlib.util
import json
import socket
import sys
import threading
import time
import types
import unittest
import urllib.error
import urllib.request
from http.server import ThreadingHTTPServer
from pathlib import Path
from unittest import mock

REPO = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("laya_server", REPO / "deploy/services/laya/server.py")
server = importlib.util.module_from_spec(spec)
spec.loader.exec_module(server)


def until(condition, seconds: float = 5) -> None:
    deadline = time.monotonic() + seconds
    while not condition():
        if time.monotonic() > deadline:
            raise AssertionError("timed out waiting")
        time.sleep(0.01)


class FakeAgent:
    """A checkpoint that answers every question: the first option, a middle score, a yes of 0.8. With gate, each pass waits for it."""

    def __init__(self, error: str | Exception | None = None, gate: threading.Event | None = None):
        self.calls = []
        self.error = error
        self.gate = gate

    def predict(self, state, questions):
        self.calls.append((state, questions))
        if self.gate is not None:
            self.gate.wait(10)
        if isinstance(self.error, Exception):
            raise self.error
        if self.error:
            raise ValueError(self.error)
        answers = {}
        for qid, q in questions.items():
            if q["type"] == "choice":
                names = list(q["criteria"])
                answers[qid] = {"choice": names[0], "probabilities": {n: (0.9 if i == 0 else 0.1 / (len(names) - 1)) for i, n in enumerate(names)}, "confidence": 0.7}
            elif q["type"] == "score":
                answers[qid] = {"score": 1.2, "probabilities": [0.1, 0.6, 0.3], "confidence": 0.4}
            else:
                answers[qid] = {"noul": 0.8, "confidence": 0.3}
        return {"answers": answers, "usage": {"input_tokens": 12, "output_tokens": 0}}


TRIAGE = {
    "department": {"type": "choice", "instructions": "Which department should handle this?", "criteria": {"billing": "invoices, refunds", "technical": "bugs, outages"}},
    "urgency": {"type": "score", "instructions": "How urgent is it?", "criteria": ["not urgent", "soon", "blocking"]},
    "churn": {"type": "noul", "instructions": "Does the user threaten to leave?"},
}


class Validation(unittest.TestCase):
    def refused(self, body, words):
        with self.assertRaises(server.Refused) as caught:
            server.validate(body)
        self.assertEqual(400, caught.exception.status)
        self.assertIn(words, str(caught.exception))

    def test_a_good_request_comes_back_clean_with_auto_by_default(self):
        state, questions, checkpoint = server.validate({"state": "  Charged twice  ", "questions": TRIAGE})
        self.assertEqual("Charged twice", state)
        self.assertEqual(TRIAGE, questions)
        self.assertEqual("auto", checkpoint)
        # A JSON state is Laya's to read as it is.
        state, _, checkpoint = server.validate({"state": {"subject": "Refund"}, "questions": TRIAGE, "checkpoint": "multilingual"})
        self.assertEqual({"subject": "Refund"}, state)
        self.assertEqual("multilingual", checkpoint)

    def test_what_a_request_must_hold_is_said_plainly(self):
        self.refused([], "JSON object")
        self.refused({"state": "x", "questions": TRIAGE, "model": "jev-1"}, "unknown fields: model")
        self.refused({"state": "  ", "questions": TRIAGE}, "state must be a non-empty string")
        self.refused({"state": 42, "questions": TRIAGE}, "state must be")
        self.refused({"state": "x" * (server.MAX_STATE + 1), "questions": TRIAGE}, "longer than")
        self.refused({"state": "x", "questions": {}}, "questions must be a non-empty object")
        self.refused({"state": "x", "questions": {f"q{i}": TRIAGE["churn"] for i in range(21)}}, "at most 20 questions")
        self.refused({"state": "x", "questions": {"q": {"type": "maybe", "instructions": "?"}}}, "type must be choice, score or noul")
        self.refused({"state": "x", "questions": {"q": {"type": "noul", "instructions": ""}}}, "instructions must be a non-empty string")
        self.refused({"state": "x", "questions": {"q": {"type": "noul", "instructions": "?", "hooks": []}}}, "unknown fields hooks")
        self.refused({"state": "x", "questions": {"q": {"type": "choice", "instructions": "?", "criteria": {"only": "one"}}}}, "2 to 20 options")
        self.refused({"state": "x", "questions": {"q": {"type": "choice", "instructions": "?"}}}, "a choice needs criteria")
        self.refused({"state": "x", "questions": {"q": {"type": "choice", "instructions": "?", "criteria": ["a", "a"]}}}, "the options must differ")
        self.refused({"state": "x", "questions": {"q": {"type": "choice", "instructions": "?", "criteria": {"a": "", "b": "x"}}}}, "option a must be a non-empty string")
        self.refused({"state": "x", "questions": {"q": {"type": "score", "instructions": "?", "criteria": ["low"]}}}, "2 to 10 levels")
        self.refused({"state": "x", "questions": {"q": {"type": "score", "instructions": "?", "criteria": ["low", None]}}}, "a level must be")
        self.refused({"state": "x", "questions": {"q": {"type": "noul", "instructions": "?", "criteria": {"yes": "x"}}}}, '{"true": "...", "false": "..."}')
        self.refused({"state": "x", "questions": TRIAGE, "checkpoint": "typed-decisions"}, "checkpoint must be auto, english or multilingual")


class Routing(unittest.TestCase):
    def test_auto_reads_english_with_the_english_checkpoint_and_persian_with_the_multilingual_one(self):
        english, multilingual = FakeAgent(), FakeAgent()
        d = server.Decider({"english": english, "multilingual": multilingual})
        answer = d.decide({"state": "We were billed twice, please refund", "questions": TRIAGE})
        self.assertEqual("english", answer["checkpoint"])
        self.assertTrue(answer["calibrated"])
        self.assertEqual("billing", answer["answers"]["department"]["choice"])
        self.assertEqual({"input_tokens": 12, "output_tokens": 0}, answer["usage"])
        self.assertGreaterEqual(answer["ms"], 0)
        answer = d.decide({"state": "دو بار از من پول گرفته‌اند، لطفاً برگردانید", "questions": TRIAGE})
        self.assertEqual("multilingual", answer["checkpoint"])
        self.assertFalse(answer["calibrated"])
        self.assertEqual(1, len(english.calls))
        self.assertEqual(1, len(multilingual.calls))
        # Asked for by name, whatever the script.
        self.assertEqual("multilingual", d.decide({"state": "Billed twice", "questions": TRIAGE, "checkpoint": "multilingual"})["checkpoint"])

    def test_english_falls_back_to_the_multilingual_checkpoint_but_persian_never_to_the_english_one(self):
        d = server.Decider({"multilingual": FakeAgent()})
        self.assertEqual("multilingual", d.decide({"state": "Billed twice", "questions": TRIAGE})["checkpoint"])
        d = server.Decider({"english": FakeAgent()})
        with self.assertRaises(server.Refused) as caught:
            d.decide({"state": "سلام دنیا", "questions": TRIAGE})
        self.assertEqual(503, caught.exception.status)
        self.assertIn("multilingual checkpoint is waiting", str(caught.exception))
        with self.assertRaises(server.Refused) as caught:
            d.decide({"state": "Hello", "questions": TRIAGE, "checkpoint": "multilingual"})
        self.assertEqual(503, caught.exception.status)

    def test_layas_own_refusals_are_a_422_with_its_words(self):
        d = server.Decider({"english": FakeAgent(error="options exceed head_max_len")})
        with self.assertRaises(server.Refused) as caught:
            d.decide({"state": "Hello", "questions": TRIAGE})
        self.assertEqual(422, caught.exception.status)
        self.assertIn("head_max_len", str(caught.exception))

    def test_laya_detects_the_language_of_a_json_state_from_its_text(self):
        seen = []

        def analyse(text):
            if not isinstance(text, str):
                raise TypeError("analyse reads text")
            seen.append(text)
            return {"is_english": True, "script": "Latin", "language": "en"}

        lang = types.ModuleType("laya.lang")
        lang.analyse = analyse
        package = types.ModuleType("laya")
        package.lang = lang
        with mock.patch.dict(sys.modules, {"laya": package, "laya.lang": lang}):
            self.assertEqual((True, "Latin script, language en"), server.is_english({"subject": "Refund"}))
        self.assertEqual(['{"subject": "Refund"}'], seen)

    def test_health_says_which_checkpoints_answer(self):
        d = server.Decider({"english": FakeAgent()})
        d.states["multilingual"] = "loading"
        health = d.health()
        self.assertEqual("ready", health["status"])
        self.assertEqual(["english"], health["ready"])
        self.assertEqual({"english": "ready", "multilingual": "loading"}, health["checkpoints"])
        loading = server.Decider()
        loading.states["english"] = "loading"
        self.assertEqual("loading", loading.health()["status"])
        empty = server.Decider()
        self.assertEqual("waiting", empty.health()["status"])
        self.assertEqual([], empty.health()["ready"])


class Load(unittest.TestCase):
    """One forward pass at a time: past MAX_WAITING calls, or LOCK_WAIT seconds, a call is told Laya is busy."""

    ASK = {"state": "Billed twice", "questions": TRIAGE, "checkpoint": "english"}

    def test_past_max_waiting_calls_a_call_is_refused_as_busy_at_once(self):
        gate = threading.Event()
        agent = FakeAgent(gate=gate)
        d = server.Decider({"english": agent})
        with mock.patch.object(server, "MAX_WAITING", 2):
            calls = [threading.Thread(target=d.decide, args=(self.ASK,)) for _ in range(2)]
            for call in calls:
                call.start()
            until(lambda: d.waiting == 2)
            started = time.monotonic()
            with self.assertRaises(server.Refused) as caught:
                d.decide(self.ASK)
            self.assertLess(time.monotonic() - started, 1)
            self.assertEqual(503, caught.exception.status)
            self.assertIn("busy", str(caught.exception))
            gate.set()
            for call in calls:
                call.join(5)
        self.assertEqual(0, d.waiting)
        self.assertEqual(2, len(agent.calls))
        # With the queue gone, calls are answered again.
        self.assertEqual("english", d.decide(self.ASK)["checkpoint"])

    def test_a_call_that_waits_its_turn_past_lock_wait_is_refused_as_busy(self):
        gate = threading.Event()
        d = server.Decider({"english": FakeAgent(gate=gate)})
        holder = threading.Thread(target=d.decide, args=(self.ASK,))
        holder.start()
        until(d.lock.locked)
        with mock.patch.object(server, "LOCK_WAIT", 0.2), self.assertRaises(server.Refused) as caught:
            d.decide(self.ASK)
        self.assertEqual(503, caught.exception.status)
        gate.set()
        holder.join(5)
        self.assertEqual(0, d.waiting)
        self.assertFalse(d.lock.locked())

    def test_a_caller_gone_by_its_turn_costs_no_forward_pass(self):
        agent = FakeAgent()
        d = server.Decider({"english": agent})
        with self.assertRaises(server.Gone):
            d.decide(self.ASK, gone=lambda: True)
        self.assertEqual([], agent.calls)
        self.assertEqual(0, d.waiting)
        self.assertFalse(d.lock.locked())


class Http(unittest.TestCase):
    def setUp(self):
        self.agent = FakeAgent()
        self.decider = server.Decider({"english": self.agent})
        handler = type("H", (server.Handler,), {"decider": self.decider})
        self.httpd = ThreadingHTTPServer(("127.0.0.1", 0), handler)
        threading.Thread(target=self.httpd.serve_forever, daemon=True).start()
        self.base = f"http://127.0.0.1:{self.httpd.server_address[1]}"

    def tearDown(self):
        self.httpd.shutdown()
        self.httpd.server_close()

    def call(self, method, path, body=None, raw=None):
        data = raw if raw is not None else (json.dumps(body).encode() if body is not None else None)
        req = urllib.request.Request(self.base + path, data=data, method=method, headers={"Content-Type": "application/json"})
        try:
            with urllib.request.urlopen(req, timeout=5) as res:
                return res.status, json.loads(res.read())
        except urllib.error.HTTPError as e:
            with e:
                return e.code, json.loads(e.read())

    def test_decide_answers_and_health_reports_over_http(self):
        status, health = self.call("GET", "/health")
        self.assertEqual(200, status)
        self.assertEqual(["english"], health["ready"])
        status, answer = self.call("POST", "/v1/decide", {"state": "Billed twice", "questions": TRIAGE, "checkpoint": "english"})
        self.assertEqual(200, status)
        self.assertEqual("english", answer["checkpoint"])
        self.assertEqual(0.8, answer["answers"]["churn"]["noul"])
        self.assertEqual(("Billed twice", TRIAGE), self.agent.calls[0])

    def test_a_body_sent_in_chunks_is_read_whole(self):
        data = json.dumps({"state": "Billed twice", "questions": TRIAGE}).encode()
        conn = http.client.HTTPConnection("127.0.0.1", self.httpd.server_address[1], timeout=5)
        try:
            conn.request("POST", "/v1/decide", body=iter([data[:20], data[20:]]), headers={"Content-Type": "application/json"}, encode_chunked=True)
            res = conn.getresponse()
            self.assertEqual(200, res.status)
            self.assertEqual("billing", json.loads(res.read())["answers"]["department"]["choice"])
            # The connection carries the next request too.
            conn.request("GET", "/health")
            res = conn.getresponse()
            self.assertEqual(200, res.status)
            res.read()
        finally:
            conn.close()
        self.assertEqual(("Billed twice", TRIAGE), self.agent.calls[0])

    def raw(self, head: bytes, body: bytes = b"") -> socket.socket:
        sock = socket.create_connection(("127.0.0.1", self.httpd.server_address[1]), timeout=5)
        sock.sendall(head + body)
        return sock

    def test_a_chunk_size_that_is_not_plain_hex_is_refused(self):
        # int() takes "-1", and read(-1) would read the connection to its end, past MAX_BODY.
        for size in (b"-1", b"+10", b"1_0", b"zz"):
            with self.raw(b"POST /v1/decide HTTP/1.1\r\nHost: laya\r\nTransfer-Encoding: chunked\r\n\r\n" + size + b"\r\n", b"{" * 100) as sock:
                status = sock.recv(4096).split(b"\r\n")[0]
            self.assertIn(b" 400 ", status, size)
        self.assertEqual([], self.agent.calls)

    def test_a_caller_that_leaves_while_waiting_its_turn_is_not_computed(self):
        gate = threading.Event()
        self.agent.gate = gate
        first = threading.Thread(target=self.call, args=("POST", "/v1/decide", {"state": "first", "questions": TRIAGE}))
        first.start()
        until(lambda: len(self.agent.calls) == 1)
        body = json.dumps({"state": "second", "questions": TRIAGE}).encode()
        left = self.raw(b"POST /v1/decide HTTP/1.1\r\nHost: laya\r\nContent-Type: application/json\r\nContent-Length: %d\r\n\r\n" % len(body), body)
        until(lambda: self.decider.waiting == 2)
        left.close()
        gate.set()
        first.join(5)
        until(lambda: self.decider.waiting == 0)
        self.assertEqual(["first"], [state for state, _ in self.agent.calls])

    def test_an_error_inside_laya_is_answered_with_its_kind_only(self):
        self.agent.error = RuntimeError("the tokenizer choked on: Billed twice")
        status, body = self.call("POST", "/v1/decide", {"state": "Billed twice", "questions": TRIAGE})
        self.assertEqual(500, status)
        self.assertEqual("Laya could not answer this request (RuntimeError)", body["error"])
        # The server still answers.
        self.agent.error = None
        self.assertEqual(200, self.call("POST", "/v1/decide", {"state": "Billed twice", "questions": TRIAGE})[0])

    def test_bad_requests_are_refused_before_the_model_reads_them(self):
        self.assertEqual(400, self.call("POST", "/v1/decide", raw=b"{not json")[0])
        status, body = self.call("POST", "/v1/decide", {"state": "x", "questions": {"q": {"type": "maybe", "instructions": "?"}}})
        self.assertEqual(400, status)
        self.assertIn("type must be", body["error"])
        self.assertEqual(413, self.call("POST", "/v1/decide", raw=b" " * (server.MAX_BODY + 1))[0])
        self.assertEqual(503, self.call("POST", "/v1/decide", {"state": "Hi", "questions": TRIAGE, "checkpoint": "multilingual"})[0])
        self.assertEqual(404, self.call("GET", "/v1/decide")[0])
        self.assertEqual(404, self.call("POST", "/predict", {"state": "x"})[0])
        self.assertEqual([], self.agent.calls)


if __name__ == "__main__":
    unittest.main()
