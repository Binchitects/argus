#!/usr/bin/env python3
"""
The app's own gateway left to run a day under steady use (docs/design/own-gateway.md §7, commit 18's soak): every few
seconds a person's key asks something of the kinds clients send (a streamed answer, a plain one, a tool call,
Anthropic's Messages, OpenAI's Responses, embeddings, now and then speech and its transcription), several at once at
the hour's busiest, and each hour is written down: requests, errors by kind, time to the first token p50/p95/p99, the
app's memory and CPU, and whether the ledger has every answer. At the end, the gates:

    no 5xx at all, errors other than the limits' 429 under 0.1 %
    the last hours' p95 time to the first token within 1.5x the first hours'
    the app's memory not growing hour on hour (the last hour's within 1.25x the second's)
    every answer in the ledger

    docker run -d --name gw-soak --network host -v /srv/arena/deploy:/srv/arena/deploy -v /var/run/docker.sock:/var/run/docker.sock \\
      -v "$(command -v docker):/usr/local/bin/docker:ro" python:3.13-slim \\
      python3 -u -I /srv/arena/deploy/scripts/gateway-soak.py --hours 24 --out /srv/arena/deploy/gateway-soak.json

Reads .env beside it; makes a person and a key, removed at the end. Standard library only.
"""

import argparse
import json
import random
import secrets
import subprocess
import sys
import threading
import time
import uuid
from pathlib import Path
import importlib.util

_here = Path(__file__).resolve().parent
_spec = importlib.util.spec_from_file_location("gateway_compare", _here / "gateway-compare.py")
gc = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(gc)
_spec2 = importlib.util.spec_from_file_location("gateway_scale", _here / "gateway-scale-check.py")
sc = importlib.util.module_from_spec(_spec2)
_spec2.loader.exec_module(sc)

env, GATEWAY, psql = gc.env, gc.GATEWAY, gc.psql


def app_stats():
    """Memory (MiB) and CPU (%) of each app replica, from docker stats."""
    out = subprocess.run(["docker", "stats", "--no-stream", "--format", "{{.Name}}\t{{.MemUsage}}\t{{.CPUPerc}}"], capture_output=True, text=True).stdout
    stats = {}
    for line in out.splitlines():
        name, mem, cpu = (line.split("\t") + ["", "", ""])[:3]
        if name == "app" or name.startswith("arena-app"):
            used = mem.split("/")[0].strip()
            mib = float(used[:-3]) * (1024 if used.endswith("GiB") else 1) if used[-3:] in ("MiB", "GiB") else None
            stats[name] = {"mem_mib": mib, "cpu": cpu}
    return stats


def one(key, model, voice, ear, kind):
    """One request of a kind: its status, time to the first token (streams) or to the end, and whether it is an answer the ledger should have."""
    t = time.perf_counter()
    if kind == "stream":
        r = gc.stream(GATEWAY, key, model, f"In one sentence: what is {random.choice(['a queue', 'a cache', 'a lock', 'a log'])}?", 48,
                      extra={"chat_template_kwargs": {"enable_thinking": False}})
        return {"kind": kind, "status": r["status"] or (200 if r["ok"] else 0), "ok": r["ok"], "ttft": r["ttft"], "error": r["error"], "booked": True}
    if kind == "plain":
        s, body = gc.plain(GATEWAY, key, model, {"chat_template_kwargs": {"enable_thinking": False}, "max_tokens": 32})
    elif kind == "tool":
        s, body = gc.plain(GATEWAY, key, model, {"messages": [{"role": "user", "content": "What is the weather in Tehran? Use the tool."}], "max_tokens": 256,
                                                 "tools": [{"type": "function", "function": {"name": "get_weather", "parameters": {"type": "object", "properties": {"city": {"type": "string"}}}}}],
                                                 "chat_template_kwargs": {"enable_thinking": False}})
    elif kind == "anthropic":
        st, raw, _ = sc.post(GATEWAY, "/v1/messages", key, {"model": model, "max_tokens": 64, "messages": [{"role": "user", "content": "Say hi."}]})
        s, body = st, raw
    elif kind == "responses":
        st, raw, _ = sc.post(GATEWAY, "/v1/responses", key, {"model": model, "input": "Say hi.", "max_output_tokens": 256})
        s, body = st, raw
    elif kind == "embeddings":
        st, raw, _ = sc.post(GATEWAY, "/v1/embeddings", key, {"model": "nomic-embed-text", "input": "a sentence to embed"})
        s, body = st, raw
    elif kind == "speech" and voice:
        st, audio, _ = sc.post(GATEWAY, "/v1/audio/speech", key, {"model": voice, "input": "A short line read aloud.", "response_format": "wav"}, timeout=300)
        s, body = st, b""
        if st == 200 and ear:
            boundary = uuid.uuid4().hex
            form = (f"--{boundary}\r\nContent-Disposition: form-data; name=\"model\"\r\n\r\n{ear}\r\n--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; "
                    f"filename=\"a.wav\"\r\nContent-Type: audio/wav\r\n\r\n").encode() + audio + f"\r\n--{boundary}--\r\n".encode()
            s, body, _ = sc.post(GATEWAY, "/v1/audio/transcriptions", key, None, timeout=300, raw=form, content_type=f"multipart/form-data; boundary={boundary}")
    else:
        return None
    return {"kind": kind, "status": s, "ok": s == 200, "ttft": time.perf_counter() - t if kind in ("plain", "tool") else None,
            "error": None if s == 200 else str(body)[:200], "booked": kind not in ("speech",)}


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--hours", type=float, default=24)
    p.add_argument("--model", default=env("MODEL_NAME"))
    p.add_argument("--every", type=float, default=4, help="seconds between one person's requests, on average")
    p.add_argument("--people", type=int, default=3, help="keys asking side by side")
    p.add_argument("--out", default=str(gc.ROOT / "gateway-soak.json"))
    a = p.parse_args()
    admin = gc.Session()
    if admin.call("POST", "/api/auth/login", {"userName": "admin", "password": env("ADMIN_PASSWORD")})[0] != 200:
        sys.exit("the admin could not sign in")
    people = []
    for i in range(a.people):
        name = f"gwsoak{secrets.token_hex(3)}"
        st, made = admin.call("POST", "/api/admin/people", {"userName": name, "email": f"{name}@example.test"})
        if st not in (200, 201):
            sys.exit(f"could not make a test person: {st}")
        people.append((made["id"], f"{name}@example.test", made["apiKey"]))
    st, listed, _ = sc.get_models(people[0][2])
    ids = [m.get("id") for m in json.loads(listed or b"{}").get("data", [])]
    voice = next((i for i in ids if i and ("kokoro" in i.lower() or "piper" in i.lower())), None)
    ear = next((i for i in ids if i and "whisper" in i.lower()), None)
    kinds = ["stream"] * 10 + ["plain"] * 3 + ["tool", "anthropic", "responses", "embeddings"] + (["speech"] if voice else [])
    out = {"model": a.model, "hours": a.hours, "people": a.people, "started": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()), "hourly": []}
    lock = threading.Lock()
    bucket = []
    stop = time.time() + a.hours * 3600
    started = time.strftime("%Y-%m-%d %H:%M:%S", time.gmtime())

    def asker(key):
        while time.time() < stop:
            r = one(key, a.model, voice, ear, random.choice(kinds))
            if r:
                with lock:
                    bucket.append(r)
            time.sleep(random.expovariate(1 / a.every))

    threads = [threading.Thread(target=asker, args=(k,), daemon=True) for _, _, k in people]
    for t in threads:
        t.start()

    def write():
        Path(a.out).write_text(json.dumps(out, indent=2), encoding="utf-8")

    hour = 0
    try:
        while time.time() < stop:
            time.sleep(min(3600, max(1, stop - time.time())))
            hour += 1
            with lock:
                got, bucket[:] = list(bucket), []
            ttft = [r["ttft"] for r in got if r["ttft"] is not None and r["ok"]]
            errors = {}
            for r in got:
                if not r["ok"]:
                    k = f"{r['kind']} {r['status']}"
                    errors[k] = errors.get(k, 0) + 1
            h = {"hour": hour, "requests": len(got), "ok": sum(1 for r in got if r["ok"]), "errors": errors,
                 "ttft_p50": gc.pct(ttft, 50), "ttft_p95": gc.pct(ttft, 95), "ttft_p99": gc.pct(ttft, 99), "app": app_stats(), "at": time.strftime("%H:%M:%SZ", time.gmtime())}
            out["hourly"].append(h)
            print(json.dumps(h), flush=True)
            write()
        for t in threads:
            t.join(timeout=700)
        # The ledger against what was answered.
        emails = ",".join(f"'{e}'" for _, e, _ in people)
        rows = psql(f"select status, count(*) from gateway.requests where person in ({emails}) and started_at >= '{started}' group by 1")
        out["ledger"] = {r[0]: int(r[1]) for r in rows}
        answered = sum(x["ok"] for x in out["hourly"])
        total = sum(x["requests"] for x in out["hourly"])
        fives = sum(v for x in out["hourly"] for k, v in x["errors"].items() if k.split()[-1].startswith("5") or k.split()[-1] == "0")
        others = sum(v for x in out["hourly"] for k, v in x["errors"].items() if not k.endswith(" 429"))
        first = [x["ttft_p95"] for x in out["hourly"][:3] if x["ttft_p95"]]
        last = [x["ttft_p95"] for x in out["hourly"][-3:] if x["ttft_p95"]]
        mem = [sum(v["mem_mib"] or 0 for v in x["app"].values()) for x in out["hourly"]]
        out["gates"] = {
            "no_5xx": {"pass": fives == 0, "count": fives},
            "errors_under_0.1%": {"pass": total > 0 and others / total < 0.001, "errors": others, "requests": total},
            "latency_steady": {"pass": bool(first and last) and max(last) <= 1.5 * max(first), "first_p95": first, "last_p95": last},
            "memory_steady": {"pass": len(mem) < 3 or mem[-1] <= 1.25 * mem[1], "mib_by_hour": mem},
            "every_answer_booked": {"pass": out["ledger"].get("done", 0) >= answered, "done_rows": out["ledger"].get("done", 0), "answered": answered},
        }
        write()
        print("\nGates:")
        for g, r in out["gates"].items():
            print(f"  {'PASS' if r['pass'] else 'FAIL'}  {g}  {json.dumps({k: v for k, v in r.items() if k != 'pass'})}")
        return 0 if all(r["pass"] for r in out["gates"].values()) else 1
    finally:
        # Signed in again: a day outlives the session.
        admin = gc.Session()
        admin.call("POST", "/api/auth/login", {"userName": "admin", "password": env("ADMIN_PASSWORD")})
        for pid, _, _ in people:
            admin.call("DELETE", f"/api/admin/people/{pid}")


if __name__ == "__main__":
    sys.exit(main())
