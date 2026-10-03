#!/usr/bin/env python3
"""
Many people at once: the chat (its fair-use queue), the API (each person's key),
and the Python sandbox (a burst of jobs), against the deployed stack.

    python3 scripts/scale-test.py                     # 12 people, 24 sandbox jobs
    python3 scripts/scale-test.py --users 30 --sandbox-jobs 60 --json scale.json

It signs in as the admin (ADMIN_PASSWORD in .env), makes the test people and
removes them at the end. Per scenario it reports successes, the time to the first
word and to the end (p50, p95, max), how many answers had to wait their turn and
for how long, and whether any answer carried another person's secret. Secrets
are never printed. Exit code 0 only when nothing failed.
"""
from __future__ import annotations

import argparse
import concurrent.futures as cf
import http.cookiejar
import json
import ssl
import statistics
import subprocess
import sys
import time
import urllib.error
import urllib.request
import uuid
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
CA = ROOT / "config" / "traefik" / "certs" / "ca.crt"
TLS = ssl.create_default_context(cafile=str(CA)) if CA.exists() else ssl.create_default_context()


def env(key: str, default: str = "") -> str:
    for line in (ROOT / ".env").read_text().splitlines():
        if line.startswith(key + "="):
            return line.split("=", 1)[1].strip()
    return default


class Session:
    def __init__(self, app: str):
        self.app = app
        self.open = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()), urllib.request.HTTPSHandler(context=TLS))

    def call(self, path: str, body: object | None = None, method: str | None = None, timeout: int = 900, stream=None):
        req = urllib.request.Request(self.app + path, data=json.dumps(body).encode() if body is not None else None, method=method or ("POST" if body is not None else "GET"))
        req.add_header("Content-Type", "application/json")
        req.add_header("X-Requested-With", "fetch")
        try:
            with self.open.open(req, timeout=timeout) as r:
                if stream:
                    return r.status, stream(r)
                text = r.read().decode()
                return r.status, (json.loads(text) if text else None)
        except urllib.error.HTTPError as e:
            return e.code, e.read().decode()[:300]
        except (urllib.error.URLError, TimeoutError, ConnectionError) as e:
            return 0, str(e)


def pct(values: list[float], p: float) -> float:
    if not values:
        return 0.0
    v = sorted(values)
    return v[min(len(v) - 1, int(round(p / 100 * (len(v) - 1))))]


def summary(name: str, times: dict[str, list[float]], ok: int, total: int, extra: str = "") -> dict:
    line = f"  {name}: {ok}/{total} ok"
    for k, v in times.items():
        if v:
            line += f" · {k} p50 {pct(v, 50):.1f}s p95 {pct(v, 95):.1f}s max {max(v):.1f}s"
    print(line + (f" · {extra}" if extra else ""), flush=True)
    return {"ok": ok, "total": total, **{k: {"p50": pct(v, 50), "p95": pct(v, 95), "max": max(v) if v else 0} for k, v in times.items()}}


def chat_scenario(app: str, people: list[dict]) -> dict:
    """Everyone sends a message at the same moment; each answer must come, in turn, with only its own secret."""
    def one(p: dict):
        s = Session(app)
        s.call("/api/auth/login", {"userName": p["name"], "password": p["password"]})
        _, chat = s.call("/api/chat/conversations", {"thinking": "off", "tools": []})
        start = time.time()
        first, queued, waited_until, text = None, 0, None, ""

        def read(r):
            nonlocal first, queued, waited_until, text
            for raw in r:
                line = raw.decode().strip()
                if not line.startswith("data: "):
                    continue
                e = json.loads(line[6:])
                if e.get("type") == "queued":
                    queued = max(queued, e.get("ahead", 0) + 1)
                elif e.get("type") == "assistant":
                    waited_until = time.time()
                elif e.get("type") == "content":
                    first = first or time.time()
                    text += e.get("text", "")
            return text

        status, _ = s.call(f"/api/chat/conversations/{chat['id']}/messages",
                           {"content": f"Repeat this code exactly, and nothing else: {p['secret']}"}, stream=read)
        end = time.time()
        return {"ok": status == 200 and p["secret"] in text, "leak": any(q["secret"] in text for q in people if q is not p),
                "first": (first or end) - start, "total": end - start, "queued": queued, "wait": (waited_until or start) - start}

    with cf.ThreadPoolExecutor(len(people)) as pool:
        runs = list(pool.map(one, people))
    leaks = sum(r["leak"] for r in runs)
    waited = [r for r in runs if r["queued"]]
    res = summary("chat, everyone at once", {"first word": [r["first"] for r in runs], "whole answer": [r["total"] for r in runs]},
                  sum(r["ok"] for r in runs), len(runs),
                  f"{len(waited)} waited their turn (up to {max((r['queued'] for r in waited), default=0)} ahead, {max((r['wait'] for r in waited), default=0):.0f}s) · {leaks} leaks")
    return {**res, "leaks": leaks, "waited": len(waited), "failed": len(runs) - res["ok"] + leaks}


def api_scenario(base: str, model: str, keys: list[str]) -> dict:
    """Each person's key at once, through the gateway, streaming."""
    def one(key: str):
        body = {"model": model, "stream": True, "max_tokens": 64, "chat_template_kwargs": {"enable_thinking": False},
                "messages": [{"role": "user", "content": "Count from 1 to 10, separated by commas."}]}
        req = urllib.request.Request(f"{base}/chat/completions", data=json.dumps(body).encode(), method="POST",
                                     headers={"Content-Type": "application/json", "Authorization": f"Bearer {key}"})
        start, first, text = time.time(), None, ""
        try:
            with urllib.request.urlopen(req, context=TLS, timeout=900) as r:
                for raw in r:
                    line = raw.decode().strip()
                    if line.startswith("data:") and line != "data: [DONE]":
                        piece = ((json.loads(line[5:]).get("choices") or [{}])[0].get("delta") or {}).get("content") or ""
                        if piece:
                            first = first or time.time()
                            text += piece
            return {"status": 200, "ok": "10" in text, "first": (first or time.time()) - start, "total": time.time() - start}
        except urllib.error.HTTPError as e:
            return {"status": e.code, "ok": False, "first": 0, "total": time.time() - start}

    with cf.ThreadPoolExecutor(len(keys)) as pool:
        runs = list(pool.map(one, keys))
    codes = {c: sum(1 for r in runs if r["status"] == c) for c in {r["status"] for r in runs}}
    res = summary("API, every key at once", {"first token": [r["first"] for r in runs if r["ok"]], "whole answer": [r["total"] for r in runs if r["ok"]]},
                  sum(r["ok"] for r in runs), len(runs), f"statuses {codes}")
    return {**res, "statuses": codes, "failed": len(runs) - res["ok"]}


def sandbox_scenario(jobs: int) -> dict:
    """A burst of Python jobs straight into the sandbox's queue: every one must finish, right, within its slots."""
    ids = []
    start = time.time()
    for i in range(jobs):
        jid = uuid.uuid4().hex
        code = f"import time, hashlib\nt=time.time()\nh=hashlib.sha256(b'{i}'*200000).hexdigest()\nwhile time.time()-t<1: pass\nprint({i}, h[:8])"
        job = json.dumps({"code": code, "timeout": 60, "files": []})
        subprocess.run(["docker", "exec", "-i", "-u", "1000", "sandbox", "sh", "-c",
                        f"mkdir -p /jobs/in/.tmp-{jid}/files && cat > /jobs/in/.tmp-{jid}/job.json && mv /jobs/in/.tmp-{jid} /jobs/in/{jid}"],
                       input=job, text=True, check=True)
        ids.append((i, jid))
    done, right, durations = 0, 0, []
    deadline = time.time() + 60 + jobs * 10
    pending = dict(ids)
    while pending and time.time() < deadline:
        for i, jid in list(pending.items()):
            out = subprocess.run(["docker", "exec", "sandbox", "cat", f"/jobs/out/{jid}/result.json"], capture_output=True, text=True)
            if out.returncode == 0:
                r = json.loads(out.stdout)
                done += 1
                right += r.get("exit_code") == 0 and r.get("stdout", "").startswith(f"{i} ")
                durations.append(r.get("duration_ms", 0) / 1000)
                subprocess.run(["docker", "exec", "-u", "1000", "sandbox", "rm", "-rf", f"/jobs/out/{jid}"], capture_output=True)
                del pending[i]
        time.sleep(0.5)
    wall = time.time() - start
    slots = env("SANDBOX_SLOTS", "2") or "2"
    res = summary("sandbox, a burst of jobs", {"each job": durations}, right, jobs, f"all done in {wall:.0f}s on {slots} slots · {len(pending)} lost")
    return {**res, "wall": wall, "lost": len(pending), "failed": jobs - right}


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--users", type=int, default=12)
    ap.add_argument("--sandbox-jobs", type=int, default=24)
    ap.add_argument("--json")
    args = ap.parse_args()
    domain = env("LLM_DOMAIN", "llm.localhost") or "llm.localhost"
    app, base = f"https://{domain}", f"https://gateway.{domain}/v1"
    admin = Session(app)
    status, _ = admin.call("/api/auth/login", {"userName": "admin", "password": env("ADMIN_PASSWORD")})
    if status != 200:
        print("the admin cannot sign in")
        return 1
    _, cfg = admin.call("/api/chat/config")
    model = next((m["name"] for m in cfg["models"] if m.get("loaded")), None)
    print(f"Scale test against {domain}: {args.users} people, model {model}, {args.sandbox_jobs} sandbox jobs")
    people, report = [], {}
    stamp = int(time.time())
    try:
        for i in range(args.users):
            name = f"scale{stamp}{i:03d}"
            _, made = admin.call("/api/admin/people", {"userName": name, "email": f"{name}@example.test"})
            _, keys = admin.call(f"/api/admin/people/{made['id']}/key", {})
            people.append({"id": made["id"], "name": name, "password": made["password"], "key": keys["apiKey"], "secret": f"ZX-{uuid.uuid4().hex[:10].upper()}"})
        report["chat"] = chat_scenario(app, people)
        report["api"] = api_scenario(base, model, [p["key"] for p in people])
        if args.sandbox_jobs:
            report["sandbox"] = sandbox_scenario(args.sandbox_jobs)
    finally:
        for p in people:
            admin.call(f"/api/admin/people/{p['id']}", method="DELETE")
    if args.json:
        Path(args.json).write_text(json.dumps(report, indent=2))
    failed = sum(r.get("failed", 0) for r in report.values())
    print(f"\n{'nothing failed' if failed == 0 else f'{failed} failed'}")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
