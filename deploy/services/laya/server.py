"""
Laya, the decision model, over HTTP: a state (a text, or JSON) and typed questions in,
a probability for every option out, in one forward pass on the CPU (the GPU is the chat
model's). The standard library's server; one forward pass at a time, and "busy" (503) once
MAX_WAITING calls wait their turn or one has waited LOCK_WAIT seconds.

    POST /v1/decide  {"state": "...", "questions": {...}, "checkpoint": "auto"}
    GET  /health

The checkpoints are read from LAYA_DIR, the model library's laya folder, which the app
fills (src/Llm.Api/Models/MediaModels.cs): english/ (calibrated) and multilingual/ (100+
languages, uncalibrated). Each is loaded once all its files are there, and warmed up.
Nothing is fetched here (HF_HUB_OFFLINE): the container needs no network but the stack's.
"""
from __future__ import annotations

import json
import os
import re
import select
import socket
import sys
import threading
import time
import traceback
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

LAYA_DIR = os.environ.get("LAYA_DIR", "/models/laya")
PORT = int(os.environ.get("LAYA_PORT", "8000"))
THREADS = int(os.environ.get("LAYA_THREADS", "4"))

# The files a checkpoint needs, as the app fetches them (a ".part" is not one yet).
FILES = ["rl_agent_config.json", "model.safetensors", "encoder/config.json", "tokenizer/tokenizer.json", "tokenizer/tokenizer_config.json"]
CHECKPOINTS = ("english", "multilingual")
CHOICES = ("auto",) + CHECKPOINTS
TYPES = ("choice", "score", "noul")

# What one request may hold. Laya reads 512 tokens of state on the English checkpoint and
# 1024 on the multilingual one, and an option list shares 192 or 256 tokens: more is cut.
MAX_BODY = 256 * 1024
MAX_STATE = 20_000
MAX_QUESTIONS = 20
MAX_OPTIONS = 20
MAX_LEVELS = 10
MAX_ID = 64
MAX_INSTRUCTIONS = 1000
MAX_OPTION = 300

# One forward pass at a time, so calls wait their turn: at most this many (the one being answered
# included), each at most this long. Past either, "busy" at once: at 0.6 s a pass, a caller is never
# kept past Code Arena's 10 seconds by work its callers have given up on.
MAX_WAITING = 16
LOCK_WAIT = 8.0
# A connection that sends nothing for this long is closed (a stalled body holds no thread).
IDLE = 120

NOT_LATIN = re.compile(r"[^\W\d_a-zA-ZÀ-ɏ]")
LETTER = re.compile(r"[^\W\d_]")
CHUNK_SIZE = re.compile(rb"[0-9a-fA-F]{1,8}")


class Refused(Exception):
    """A request that cannot be answered: the status and what to tell the caller."""

    def __init__(self, status: int, message: str):
        super().__init__(message)
        self.status = status


class Gone(Exception):
    """The caller closed its connection before its turn: nobody to answer."""


def busy() -> Refused:
    return Refused(503, "Laya is busy: try again in a moment")


def bad(message: str) -> Refused:
    return Refused(400, message)


def text(value, what: str, limit: int) -> str:
    if not isinstance(value, str) or not value.strip():
        raise bad(f"{what} must be a non-empty string")
    if len(value) > limit:
        raise bad(f"{what} is longer than {limit} characters")
    return value.strip()


def validate(body) -> tuple[object, dict, str]:
    """The request's state, questions (a clean copy, as Laya takes them) and checkpoint, or Refused."""
    if not isinstance(body, dict):
        raise bad("send a JSON object with state and questions")
    unknown = set(body) - {"state", "questions", "checkpoint"}
    if unknown:
        raise bad(f"unknown fields: {', '.join(sorted(unknown))}")

    state = body.get("state")
    if isinstance(state, str):
        state = text(state, "state", MAX_STATE)
    elif isinstance(state, (dict, list)) and state:
        if len(json.dumps(state, ensure_ascii=False)) > MAX_STATE:
            raise bad(f"state is longer than {MAX_STATE} characters")
    else:
        raise bad("state must be a non-empty string, object or array")

    questions = body.get("questions")
    if not isinstance(questions, dict) or not questions:
        raise bad("questions must be a non-empty object: {id: {type, instructions, criteria}}")
    if len(questions) > MAX_QUESTIONS:
        raise bad(f"at most {MAX_QUESTIONS} questions in one call")
    clean = {}
    for qid, q in questions.items():
        if not qid.strip() or len(qid) > MAX_ID:
            raise bad(f"a question id must be 1 to {MAX_ID} characters")
        if not isinstance(q, dict):
            raise bad(f"question {qid} must be an object")
        extra = set(q) - {"type", "instructions", "criteria"}
        if extra:
            raise bad(f"question {qid}: unknown fields {', '.join(sorted(extra))}")
        kind = q.get("type")
        if kind not in TYPES:
            raise bad(f"question {qid}: type must be choice, score or noul")
        out = {"type": kind, "instructions": text(q.get("instructions"), f"question {qid}: instructions", MAX_INSTRUCTIONS)}
        criteria = q.get("criteria")
        if kind == "choice":
            out["criteria"] = choice_options(qid, criteria)
        elif kind == "score":
            if not isinstance(criteria, list) or not 2 <= len(criteria) <= MAX_LEVELS:
                raise bad(f"question {qid}: a score needs 2 to {MAX_LEVELS} levels in criteria, lowest first")
            out["criteria"] = [text(level, f"question {qid}: a level", MAX_OPTION) for level in criteria]
        elif criteria is not None:
            if not isinstance(criteria, dict) or not criteria or set(criteria) - {"true", "false"}:
                raise bad(f'question {qid}: a noul\'s criteria, when given, is {{"true": "...", "false": "..."}}')
            out["criteria"] = {k: text(v, f"question {qid}: {k}", MAX_OPTION) for k, v in criteria.items()}
        clean[qid] = out

    checkpoint = body.get("checkpoint") or "auto"
    if checkpoint not in CHOICES:
        raise bad("checkpoint must be auto, english or multilingual")
    return state, clean, checkpoint


def choice_options(qid: str, criteria) -> dict | list:
    if isinstance(criteria, dict):
        names = list(criteria)
        options = {text(k, f"question {qid}: an option", MAX_OPTION): text(v, f"question {qid}: option {k}", MAX_OPTION) for k, v in criteria.items()}
    elif isinstance(criteria, list):
        names = criteria
        options = [text(k, f"question {qid}: an option", MAX_OPTION) for k in criteria]
    else:
        raise bad(f"question {qid}: a choice needs criteria, {{option: description}} or a list of options")
    if not 2 <= len(names) <= MAX_OPTIONS:
        raise bad(f"question {qid}: a choice needs 2 to {MAX_OPTIONS} options")
    if len(set(options)) != len(names):
        raise bad(f"question {qid}: the options must differ")
    return options


def is_english(state) -> tuple[bool, str]:
    """Whether the English checkpoint can read the state, and why: Laya's own detector, else the script."""
    flat = state if isinstance(state, str) else json.dumps(state, ensure_ascii=False)
    try:
        from laya.lang import analyse  # noqa: PLC0415 (laya is not there in the tests)

        found = analyse(flat)
        language = f", language {found['language']}" if found.get("language") else ""
        return bool(found.get("is_english")), f"{found.get('script')} script{language}"
    except ImportError:
        letters = len(LETTER.findall(flat))
        other = len(NOT_LATIN.findall(flat))
        english = letters == 0 or other / letters < 0.2
        return english, "Latin script" if english else "not Latin script"


class Decider:
    """The loaded checkpoints, and one forward pass at a time (Laya's agents are not for several threads at once)."""

    def __init__(self, agents: dict | None = None, detect=is_english):
        self.agents = dict(agents or {})
        self.states = {name: ("ready" if name in self.agents else "waiting") for name in CHECKPOINTS}
        self.detect = detect
        self.lock = threading.Lock()
        # How many calls wait for the lock or hold it, counted under its own small lock.
        self.waiting = 0
        self.counting = threading.Lock()

    def choose(self, checkpoint: str, state) -> tuple[str, str]:
        if checkpoint != "auto":
            if checkpoint not in self.agents:
                raise Refused(503, f"the {checkpoint} checkpoint is {self.states[checkpoint]}")
            return checkpoint, "asked for"
        english, why = self.detect(state)
        wanted = "english" if english else "multilingual"
        if wanted in self.agents:
            return wanted, why
        # The multilingual checkpoint reads English too; the English one reads nothing else.
        if english and "multilingual" in self.agents:
            return "multilingual", why + "; the English checkpoint is " + self.states["english"]
        raise Refused(503, f"the {wanted} checkpoint is {self.states[wanted]}")

    def decide(self, body, gone=lambda: False) -> dict:
        """Laya's answers, or Refused; Gone when gone() says the caller left while it waited its turn."""
        state, questions, checkpoint = validate(body)
        name, reason = self.choose(checkpoint, state)
        started = time.perf_counter()
        with self.counting:
            if self.waiting >= MAX_WAITING:
                raise busy()
            self.waiting += 1
        try:
            if not self.lock.acquire(timeout=LOCK_WAIT):
                raise busy()
            try:
                if gone():
                    raise Gone()
                result = self.agents[name].predict(state, questions)
            except ValueError as e:
                # Laya's own refusals: options past its token budget, and the like.
                raise Refused(422, str(e)) from e
            finally:
                self.lock.release()
        finally:
            with self.counting:
                self.waiting -= 1
        ms = (time.perf_counter() - started) * 1000
        return {
            "answers": result.get("answers", {}),
            "usage": result.get("usage"),
            "checkpoint": name,
            "routing": reason,
            "calibrated": name == "english",
            "ms": round(ms, 1),
        }

    def health(self) -> dict:
        ready = [n for n in CHECKPOINTS if n in self.agents]
        status = "ready" if ready else "loading" if "loading" in self.states.values() else "waiting"
        return {"status": status, "ready": ready, "checkpoints": dict(self.states), "device": "cpu", "threads": THREADS}


def complete(name: str) -> bool:
    return all(os.path.isfile(os.path.join(LAYA_DIR, name, f)) for f in FILES)


def load_all(decider: Decider) -> None:
    """Loads each checkpoint once its files are all there (the app may still be fetching them); a failed one is tried again in ten minutes."""
    import torch  # noqa: PLC0415
    import laya  # noqa: PLC0415

    torch.set_num_threads(THREADS)
    failed_at: dict[str, float] = {}
    warm = {"q": {"type": "noul", "instructions": "Is this a test?"}}
    while len(decider.agents) < len(CHECKPOINTS):
        for name in CHECKPOINTS:
            if name in decider.agents or time.time() - failed_at.get(name, 0) < 600 or not complete(name):
                continue
            decider.states[name] = "loading"
            started = time.time()
            try:
                agent = laya.load(os.path.join(LAYA_DIR, name), device="cpu")
                agent.predict("A warm-up request.", warm)
            except Exception as e:  # noqa: BLE001 (a broken checkpoint must not stop the other)
                failed_at[name] = time.time()
                decider.states[name] = f"failed: {e}"
                log(f"{name}: could not load: {e}")
                continue
            decider.agents[name] = agent
            decider.states[name] = "ready"
            log(f"{name}: loaded in {time.time() - started:.1f} s")
        time.sleep(30)


def log(message: str) -> None:
    print(f"laya: {message}", file=sys.stderr, flush=True)


def closed(sock) -> bool:
    """Whether the other end has closed the connection: readable, with nothing to read."""
    try:
        readable, _, _ = select.select([sock], [], [], 0)
        return bool(readable) and sock.recv(1, socket.MSG_PEEK) == b""
    except (OSError, ValueError):
        return True


class Handler(BaseHTTPRequestHandler):
    decider: Decider
    protocol_version = "HTTP/1.1"
    timeout = IDLE

    def do_GET(self):
        if self.path.split("?")[0] == "/health":
            self.reply(200, self.decider.health())
        else:
            self.reply(404, {"error": "not found"})

    def do_POST(self):
        if self.path.split("?")[0] != "/v1/decide":
            self.reply(404, {"error": "not found"})
            return
        try:
            raw = self.body()
        except Refused as e:
            # What is left of the body is not read: the connection cannot carry another request.
            self.close_connection = True
            self.reply(e.status, {"error": str(e)})
            return
        try:
            try:
                body = json.loads(raw)
            except (UnicodeDecodeError, json.JSONDecodeError) as e:
                raise bad(f"the body is not JSON: {e}") from e
            answer = self.decider.decide(body, gone=lambda: closed(self.connection))
        except Refused as e:
            self.reply(e.status, {"error": str(e)})
            return
        except Gone:
            self.close_connection = True
            return
        except Exception as e:  # noqa: BLE001 (every request is answered)
            # Only the kind and the place go to the log: the message may quote what was asked.
            where = traceback.extract_tb(e.__traceback__)[-1]
            log(f"{type(e).__name__} at {os.path.basename(where.filename)}:{where.lineno}")
            self.reply(500, {"error": f"Laya could not answer this request ({type(e).__name__})"})
            return
        # What was asked stays private: only its shape and the time go to the log.
        log(f"{answer['checkpoint']}: {len(answer['answers'])} questions in {answer['ms']} ms")
        self.reply(200, answer)

    def body(self) -> bytes:
        """The request's body, by its Content-Length or in chunks (as .NET's JSON content sends it), at most MAX_BODY."""
        too_big = Refused(413, f"send a JSON body of at most {MAX_BODY // 1024} KiB")
        if "chunked" in (self.headers.get("Transfer-Encoding") or "").lower():
            data = b""
            while True:
                # Hex digits only: int() would also take a sign or underscores, and read(-1) reads to the end.
                field = self.rfile.readline(1024).split(b";")[0].strip() or b"0"
                if not CHUNK_SIZE.fullmatch(field):
                    raise bad("the body's chunks are malformed")
                size = int(field, 16)
                if not size:
                    break
                if len(data) + size > MAX_BODY:
                    raise too_big
                data += self.rfile.read(size)
                self.rfile.readline(1024)
            # Trailers, if any, up to the blank line that ends the request.
            while self.rfile.readline(1024).strip():
                pass
            if not data:
                raise bad("send a JSON body with state and questions")
            return data
        try:
            length = int(self.headers.get("Content-Length") or 0)
        except ValueError as e:
            raise bad("Content-Length is not a number") from e
        if length > MAX_BODY:
            raise too_big
        if length <= 0:
            raise bad("send a JSON body with state and questions")
        return self.rfile.read(length)

    def reply(self, status: int, body: dict) -> None:
        data = json.dumps(body, ensure_ascii=False).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        if self.close_connection:
            self.send_header("Connection", "close")
        try:
            self.end_headers()
            self.wfile.write(data)
        except (BrokenPipeError, ConnectionResetError):
            # The caller gave up waiting: nobody to tell.
            self.close_connection = True

    def log_message(self, format, *args):  # noqa: A002 (the base class's name)
        pass


def main() -> None:
    decider = Decider()
    Handler.decider = decider
    threading.Thread(target=load_all, args=(decider,), daemon=True).start()
    server = ThreadingHTTPServer(("0.0.0.0", PORT), Handler)
    log(f"listening on :{PORT}, checkpoints from {LAYA_DIR}")
    server.serve_forever()


if __name__ == "__main__":
    main()
