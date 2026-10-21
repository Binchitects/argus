#!/usr/bin/env python3
"""
Many people using the app at once, as they do: each signs in, opens the chat
list, starts a chat, asks something and reads the answer as it streams, then
asks again; through Traefik and TLS, as a browser does.

scripts/multiuser-bench.py measures the model behind the gateway. This
measures the app around it: sign-in, the database, the answers' lines and
queues, the streams, at 50, 100 or 300 people.

  - People arrive over --ramp seconds (an office at 9:00 does not press
    Enter in the same second), each then asks --turns questions, --think
    seconds apart.
  - Each step is timed: sign in, me, list chats, new chat, the answer's first
    words (time to first token: queues included), the whole answer, and the
    chat read back. Reported as p50, p95, p99 and the slowest, with every
    failure by kind (HTTP status, refused, timed out, an answer that ended in
    an error) and how often a "queued" line was shown.
  - --no-chat leaves the model out: only the app's own work.
  - The people it makes (loadtest-<n>, with a credit for their answers) are
    taken away at the end with their chats and gateway keys, unless --keep.

The admin's password comes from deploy/.env (ADMIN_PASSWORD) and is never
printed. Standard library only.

    python3 scripts/people-load.py --people 50 --ramp 60
    python3 scripts/people-load.py --people 300 --ramp 180 --no-chat --json load-300.json
"""

from __future__ import annotations

import argparse
import http.cookiejar
import json
import ssl
import statistics
import sys
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
from collections import Counter
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent


def env(key: str, default: str = "") -> str:
    path = ROOT / ".env"
    if not path.exists():
        return default
    for line in path.read_text(encoding="utf-8").splitlines():
        if line.startswith(key + "="):
            return line.split("=", 1)[1].strip()
    return default


DOM = env("DOMAIN", "llm.localhost")
PORT = env("TRAEFIK_HTTPS_PORT", "443")
BASE = f"https://{DOM}{'' if PORT == '443' else ':' + PORT}"
CTX = ssl.create_default_context() if env("ACME_EMAIL") else ssl._create_unverified_context()


class Person:
    """A cookie jar and a TLS context: a browser."""

    def __init__(self, base: str):
        self.base = base
        self.jar = http.cookiejar.CookieJar()
        self.open = urllib.request.build_opener(urllib.request.HTTPSHandler(context=CTX), urllib.request.HTTPCookieProcessor(self.jar)).open

    def call(self, method: str, path: str, data=None, timeout: float = 60):
        """(status, body text); status 0 when it could not be reached or timed out."""
        body = None if data is None else json.dumps(data).encode()
        req = urllib.request.Request(self.base + path, data=body, method=method,
                                     headers={"Content-Type": "application/json", "Accept": "application/json", "X-Requested-With": "people-load"})
        try:
            with self.open(req, timeout=timeout) as r:
                return r.status, r.read().decode("utf-8", "replace")
        except urllib.error.HTTPError as e:
            return e.code, e.read().decode("utf-8", "replace")
        except (urllib.error.URLError, TimeoutError, ConnectionError, OSError) as e:
            return 0, str(e)

    def ask(self, chat: str, text: str, timeout: float):
        """The answer as it streams: (first words after s, whole answer after s, ended: done | the error | timeout, queued shown)."""
        req = urllib.request.Request(self.base + f"/api/chat/conversations/{chat}/messages", data=json.dumps({"content": text}).encode(), method="POST",
                                     headers={"Content-Type": "application/json", "Accept": "text/event-stream", "X-Requested-With": "people-load"})
        start = time.monotonic()
        first, queued, ended = None, False, "no end"
        try:
            with self.open(req, timeout=timeout) as r:
                for raw in r:
                    line = raw.decode("utf-8", "replace").strip()
                    if not line.startswith("data: "):
                        continue
                    try:
                        event = json.loads(line[6:])
                    except json.JSONDecodeError:
                        continue
                    kind = event.get("type")
                    if kind == "queued":
                        queued = True
                    elif kind in ("content", "reasoning") and first is None:
                        first = time.monotonic() - start
                    elif kind == "done":
                        ended = "done"
                        break
                    elif kind == "error":
                        ended = "error: " + str(event.get("message", ""))[:80]
                        break
        except urllib.error.HTTPError as e:
            ended = f"HTTP {e.code}"
        except (urllib.error.URLError, TimeoutError, ConnectionError, OSError) as e:
            ended = "timeout" if "timed out" in str(e) else f"unreachable: {str(e)[:60]}"
        return first, time.monotonic() - start, ended, queued


class Tally:
    def __init__(self):
        self.lock = threading.Lock()
        self.times: dict[str, list[float]] = {}
        self.failures: Counter = Counter()
        self.queued = 0
        self.answers = 0

    def time(self, step: str, seconds: float):
        with self.lock:
            self.times.setdefault(step, []).append(seconds)

    def fail(self, step: str, why: str):
        with self.lock:
            self.failures[f"{step}: {why}"] += 1


def pct(values: list[float], p: float) -> float:
    if not values:
        return 0.0
    ordered = sorted(values)
    return ordered[min(len(ordered) - 1, max(0, round(p / 100 * len(ordered) + 0.5) - 1))]


def timed(tally: Tally, step: str, call):
    start = time.monotonic()
    status, body = call()
    took = time.monotonic() - start
    if 200 <= status < 300:
        tally.time(step, took)
        return body
    tally.fail(step, "unreachable or timed out" if status == 0 else f"HTTP {status}" + (" (rate limited)" if status == 429 else ""))
    return None


def visit(n: int, name: str, password: str, args, tally: Tally):
    p = Person(BASE)
    if timed(tally, "sign in", lambda: p.call("POST", "/api/auth/login", {"userName": name, "password": password})) is None:
        return
    timed(tally, "me", lambda: p.call("GET", "/api/auth/me"))
    timed(tally, "list chats", lambda: p.call("GET", "/api/chat/conversations"))
    made = timed(tally, "new chat", lambda: p.call("POST", "/api/chat/conversations", {"thinking": "off", "useArgus": False, "tools": []}))
    if made is None:
        return
    chat = json.loads(made)["id"]
    for turn in range(args.turns):
        if args.no_chat:
            timed(tally, "read chat", lambda: p.call("GET", f"/api/chat/conversations/{chat}"))
        else:
            first, whole, ended, queued = p.ask(chat, f"{args.message} (person {n}, turn {turn + 1})", args.answer_timeout)
            with tally.lock:
                tally.answers += 1
                tally.queued += queued
            if ended == "done":
                if first is not None:
                    tally.time("first words", first)
                tally.time("whole answer", whole)
            else:
                tally.fail("answer", ended)
            timed(tally, "read chat", lambda: p.call("GET", f"/api/chat/conversations/{chat}"))
        if turn + 1 < args.turns:
            time.sleep(args.think)
    timed(tally, "list chats", lambda: p.call("GET", "/api/chat/conversations"))


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--people", type=int, default=50)
    ap.add_argument("--ramp", type=float, default=60, help="seconds over which they arrive")
    ap.add_argument("--turns", type=int, default=2, help="questions each asks")
    ap.add_argument("--think", type=float, default=5, help="seconds between one's questions")
    ap.add_argument("--message", default="Reply with one short sentence: what is a load test?")
    ap.add_argument("--answer-timeout", type=float, default=600)
    ap.add_argument("--no-chat", action="store_true", help="the app's own work only: no question to the model")
    ap.add_argument("--keep", action="store_true", help="keep the people it made")
    ap.add_argument("--json", help="write the report here too")
    args = ap.parse_args()

    password = env("ADMIN_PASSWORD")
    if not password:
        print("ADMIN_PASSWORD is not in deploy/.env", file=sys.stderr)
        return 2
    admin = Person(BASE)
    status, _ = admin.call("POST", "/api/auth/login", {"userName": env("ADMIN_USERNAME", "admin"), "password": password})
    if status != 200:
        print(f"the admin could not sign in (HTTP {status})", file=sys.stderr)
        return 2

    run = uuid.uuid4().hex[:6]
    people: list[tuple[str, str, str]] = []
    print(f"making {args.people} people …", flush=True)
    for n in range(args.people):
        name = f"loadtest-{run}-{n:03d}"
        status, body = admin.call("POST", "/api/admin/people", {"userName": name, "email": f"{name}@loadtest.invalid", "credits": {"chat": 5, "api": 0}})
        if status not in (200, 201):
            print(f"  could not make {name}: HTTP {status} {body[:120]}", file=sys.stderr)
            break
        made = json.loads(body)
        people.append((made["id"], name, made["password"]))

    tally = Tally()
    threads = []
    started = time.monotonic()
    print(f"{len(people)} people arrive over {args.ramp:.0f} s, {args.turns} turn(s) each{' (no model)' if args.no_chat else ''} …", flush=True)
    try:
        for n, (_, name, pw) in enumerate(people):
            t = threading.Thread(target=visit, args=(n, name, pw, args, tally), daemon=True)
            at = started + (args.ramp * n / max(1, len(people) - 1) if len(people) > 1 else 0)
            time.sleep(max(0.0, at - time.monotonic()))
            t.start()
            threads.append(t)
        for t in threads:
            t.join()
    finally:
        took = time.monotonic() - started
        if not args.keep:
            print("taking the people away …", flush=True)
            for pid, _, _ in people:
                admin.call("DELETE", f"/api/admin/people/{pid}", timeout=120)

    steps = ["sign in", "me", "list chats", "new chat", "first words", "whole answer", "read chat"]
    report = {
        "people": len(people), "ramp_seconds": args.ramp, "turns": args.turns, "model": not args.no_chat, "seconds": round(took, 1),
        "answers": tally.answers, "answers_queued": tally.queued,
        "steps": {s: {"n": len(v), "p50": round(statistics.median(v), 3), "p95": round(pct(v, 95), 3), "p99": round(pct(v, 99), 3), "max": round(max(v), 3)}
                  for s in steps if (v := tally.times.get(s))},
        "failures": dict(tally.failures.most_common()),
    }
    print(f"\n{report['people']} people, {report['seconds']} s" + (f", {tally.answers} answers ({tally.queued} shown a queue)" if not args.no_chat else ""))
    print(f"  {'step':<14}{'n':>6}{'p50':>9}{'p95':>9}{'p99':>9}{'max':>9}")
    for s, v in report["steps"].items():
        print(f"  {s:<14}{v['n']:>6}{v['p50']:>9.2f}{v['p95']:>9.2f}{v['p99']:>9.2f}{v['max']:>9.2f}")
    if report["failures"]:
        print("  failures:")
        for why, count in report["failures"].items():
            print(f"    {count:>5}  {why}")
    else:
        print("  no failures")
    if args.json:
        Path(args.json).write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    return 1 if report["failures"] else 0


if __name__ == "__main__":
    sys.exit(main())
