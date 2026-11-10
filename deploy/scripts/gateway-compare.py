#!/usr/bin/env python3
"""
The app's own model gateway against LiteLLM, on this stack, back to back (docs/design/own-gateway.md §7):
the same model, the same person and key, each workload in Gateway:Mode=litellm and then in Gateway:Mode=own
(switched in the settings, live), a few runs each.

    W1  overhead: short streamed answers one at a time (64 tokens, temperature 0): time to the first token and
        to the end, p50/p95/p99; and the same sent straight to the engine, to see what each gateway adds
    W3  throughput: the same at 1, 4, 8, 16, 32 at once (more with --concurrency): tokens/s, inter-token time,
        errors
    W5  shapes: a non-streamed answer, a tool call; usage present, finish_reason, valid tool JSON
    CPU and memory of the app and LiteLLM's containers while W3 runs (docker stats)
    Ledger: each answer's usage against its line in gateway.requests (tokens equal; the cost at the model's price)
    Limits: a person held to 5 requests a minute: how many of 8 at once pass, and the 429's Retry-After
    Abort: a stream closed after a few tokens: how soon the engine's slot is free again (llama.cpp /slots)

Reads .env beside it (DOMAIN, ADMIN_PASSWORD, ENGINE_KEY, MODEL_NAME); makes a person and a key, removed at the
end; writes the results as JSON (--out) and prints a summary with the gates. Standard library only; runs in the
stack's python image with the host's network and docker:

    docker run --rm --network host -v /srv/arena/deploy:/srv/arena/deploy -v /var/run/docker.sock:/var/run/docker.sock \
      -v "$(command -v docker):/usr/local/bin/docker:ro" python:3.13-slim \
      python3 -I /srv/arena/deploy/scripts/gateway-compare.py --out /srv/arena/deploy/gateway-compare.json
"""

import argparse
import http.cookiejar
import json
import secrets
import ssl
import statistics
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent


def env(key, default=""):
    for line in (ROOT / ".env").read_text(encoding="utf-8").splitlines():
        if line.startswith(key + "="):
            return line.split("=", 1)[1].strip()
    return default


DOM = env("DOMAIN", "llm.localhost")
PORT = env("TRAEFIK_HTTPS_PORT", "443")
SUFFIX = "" if PORT == "443" else f":{PORT}"
APP = f"https://{DOM}{SUFFIX}"
GATEWAY = f"https://gateway.{DOM}{SUFFIX}"
CTX = ssl.create_default_context() if env("ACME_EMAIL") else ssl._create_unverified_context()


def pct(values, p):
    if not values:
        return None
    s = sorted(values)
    k = (len(s) - 1) * p / 100
    f = int(k)
    c = min(f + 1, len(s) - 1)
    return s[f] + (s[c] - s[f]) * (k - f)


class Session:
    """The admin, signed in with cookies, as the pages are."""

    def __init__(self):
        self.jar = http.cookiejar.CookieJar()
        self.opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(self.jar), urllib.request.HTTPSHandler(context=CTX))

    def call(self, method, path, body=None):
        data = None if body is None else json.dumps(body).encode()
        req = urllib.request.Request(APP + path, data=data, method=method,
                                     headers={"Content-Type": "application/json", "X-Requested-With": "gateway-compare"})
        try:
            with self.opener.open(req, timeout=60) as r:
                text = r.read().decode()
                return r.status, (json.loads(text) if text.strip() else None)
        except urllib.error.HTTPError as e:
            text = e.read().decode()
            try:
                return e.code, json.loads(text)
            except ValueError:
                return e.code, text


def stream(base, key, model, prompt, max_tokens, path="/v1/chat/completions", close_after=None, extra=None, auth_header="Authorization"):
    """One streamed answer: its time to the first token, to the end, each token's time, its usage, errors."""
    body = {"model": model, "messages": [{"role": "user", "content": prompt}], "max_tokens": max_tokens, "temperature": 0, "stream": True,
            "stream_options": {"include_usage": True}}
    body.update(extra or {})
    headers = {"Content-Type": "application/json"}
    if key:
        headers[auth_header] = f"Bearer {key}" if auth_header == "Authorization" else key
    req = urllib.request.Request(base.rstrip("/") + path, data=json.dumps(body).encode(), headers=headers, method="POST")
    r = {"ok": False, "ttft": None, "total": None, "times": [], "usage": None, "error": None, "status": None}
    start = time.perf_counter()
    try:
        with urllib.request.urlopen(req, timeout=600, context=CTX if base.startswith("https") else None) as res:
            r["status"] = res.status
            tokens = 0
            for raw in res:
                line = raw.decode("utf-8", "replace").strip()
                if not line.startswith("data:"):
                    continue
                data = line[5:].strip()
                if data == "[DONE]":
                    break
                try:
                    chunk = json.loads(data)
                except ValueError:
                    continue
                if chunk.get("usage"):
                    r["usage"] = chunk["usage"]
                for c in chunk.get("choices") or []:
                    d = c.get("delta") or {}
                    if d.get("content") or d.get("reasoning_content") or d.get("tool_calls"):
                        now = time.perf_counter() - start
                        if r["ttft"] is None:
                            r["ttft"] = now
                        r["times"].append(now)
                        tokens += 1
                if close_after and tokens >= close_after:
                    break
            r["total"] = time.perf_counter() - start
            r["ok"] = True
    except urllib.error.HTTPError as e:
        r["status"] = e.code
        r["error"] = e.read().decode(errors="replace")[:300]
        r["retry_after"] = e.headers.get("Retry-After")
    except Exception as e:  # noqa: BLE001 - a network error is a result here
        r["error"] = str(e)[:300]
    return r


def plain(base, key, model, body_extra):
    body = {"model": model, "messages": [{"role": "user", "content": "What is 2+2? Answer with the number."}], "max_tokens": 64, "temperature": 0}
    body.update(body_extra)
    req = urllib.request.Request(base.rstrip("/") + "/v1/chat/completions", data=json.dumps(body).encode(),
                                 headers={"Content-Type": "application/json", "Authorization": f"Bearer {key}"}, method="POST")
    try:
        with urllib.request.urlopen(req, timeout=300, context=CTX) as res:
            return res.status, json.loads(res.read().decode())
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode(errors="replace")[:300]


def summary(results):
    ok = [r for r in results if r["ok"]]
    ttft = [r["ttft"] for r in ok if r["ttft"] is not None]
    total = [r["total"] for r in ok if r["total"] is not None]
    itl = []
    for r in ok:
        t = r["times"]
        itl += [b - a for a, b in zip(t, t[1:])]
    out_tokens = sum((r["usage"] or {}).get("completion_tokens", 0) for r in ok)
    return {
        "requests": len(results), "errors": len(results) - len(ok),
        "ttft_p50": pct(ttft, 50), "ttft_p95": pct(ttft, 95), "ttft_p99": pct(ttft, 99),
        "total_p50": pct(total, 50), "total_p95": pct(total, 95),
        "itl_p50": pct(itl, 50), "itl_p95": pct(itl, 95), "output_tokens": out_tokens,
    }


def at_once(n, total, fn):
    results, lock = [], threading.Lock()
    todo = list(range(total))

    def worker():
        while True:
            with lock:
                if not todo:
                    return
                todo.pop()
            r = fn()
            with lock:
                results.append(r)

    start = time.perf_counter()
    threads = [threading.Thread(target=worker) for _ in range(n)]
    for t in threads:
        t.start()
    for t in threads:
        t.join()
    return results, time.perf_counter() - start


class Stats:
    """docker stats of some containers, sampled every second while a workload runs: mean CPU %, peak memory."""

    def __init__(self, names):
        self.names, self.samples, self.stop = names, [], threading.Event()

    def __enter__(self):
        def run():
            while not self.stop.is_set():
                try:
                    out = subprocess.run(["docker", "stats", "--no-stream", "--format", "{{.Name}} {{.CPUPerc}} {{.MemUsage}}", *self.names],
                                         capture_output=True, text=True, timeout=20).stdout
                    for line in out.splitlines():
                        name, cpu, mem = line.split(" ", 2)
                        self.samples.append((name, float(cpu.rstrip("%")), mem.split("/")[0].strip()))
                except Exception:  # noqa: BLE001
                    pass
                self.stop.wait(1)

        self.thread = threading.Thread(target=run, daemon=True)
        self.thread.start()
        return self

    def __exit__(self, *_):
        self.stop.set()
        self.thread.join(timeout=30)

    def result(self):
        def mib(s):
            units = {"KiB": 1 / 1024, "MiB": 1, "GiB": 1024, "B": 1 / 1048576}
            for u, f in units.items():
                if s.endswith(u):
                    return float(s[: -len(u)]) * f
            return 0.0

        out = {}
        for name in {n for n, _, _ in self.samples}:
            mine = [(c, mib(m)) for n, c, m in self.samples if n == name]
            out[name] = {"cpu_mean_percent": round(statistics.mean(c for c, _ in mine), 1), "memory_peak_mib": round(max(m for _, m in mine), 1), "samples": len(mine)}
        return out


def psql(sql):
    out = subprocess.run(["docker", "exec", "postgres", "psql", "-U", "arena", "-d", "llmapp", "-At", "-F", "\t", "-c", sql], capture_output=True, text=True, timeout=60)
    return [line.split("\t") for line in out.stdout.splitlines() if line.strip()]


def engine_network():
    out = subprocess.run(["docker", "inspect", "-f", "{{range $k, $v := .NetworkSettings.Networks}}{{$k}} {{end}}", "llamacpp"], capture_output=True, text=True)
    return (out.stdout.split() or [None])[0]


def direct_baseline(model, n, max_tokens):
    """W1 straight to the engine, from a container on its network: what the gateways add is measured against it."""
    net = engine_network()
    if not net:
        return None
    code = f"""
import json, time, urllib.request
def one():
    body = {{"model": {json.dumps(model)}, "messages": [{{"role": "user", "content": "Count from one to twenty in words."}}], "max_tokens": {max_tokens}, "temperature": 0, "stream": True}}
    req = urllib.request.Request("http://llamacpp:8080/v1/chat/completions", data=json.dumps(body).encode(), headers={{"Content-Type": "application/json", "Authorization": "Bearer " + __import__("os").environ.get("K", "")}}, method="POST")
    s = time.perf_counter(); first = None
    with urllib.request.urlopen(req, timeout=600) as r:
        for raw in r:
            line = raw.decode().strip()
            if line.startswith("data:") and '"content"' in line and first is None:
                first = time.perf_counter() - s
    return first, time.perf_counter() - s
print(json.dumps([one() for _ in range({n})]))
"""
    out = subprocess.run(["docker", "run", "--rm", "--network", net, "-e", "K", "python:3.13-slim", "python3", "-c", code],
                         capture_output=True, text=True, timeout=3600, env={"K": env("ENGINE_KEY"), "PATH": "/usr/local/bin:/usr/bin:/bin"})
    try:
        pairs = json.loads(out.stdout.strip().splitlines()[-1])
    except (ValueError, IndexError):
        return {"error": out.stderr[-300:]}
    ttft = [p[0] for p in pairs if p[0] is not None]
    return {"ttft_p50": pct(ttft, 50), "ttft_p95": pct(ttft, 95), "ttft_p99": pct(ttft, 99), "total_p50": pct([p[1] for p in pairs], 50)}


def slot_watcher(seconds=30):
    """A helper on the engine's network watching llama.cpp's /slots every 50 ms: the times they went from busy to idle."""
    net = engine_network()
    if not net:
        return None
    code = f"""
import json, os, time, urllib.request
busy, end = False, time.time() + {seconds}
while time.time() < end:
    try:
        req = urllib.request.Request("http://llamacpp:8080/slots", headers={{"Authorization": "Bearer " + os.environ.get("K", "")}})
        now = any(s.get("is_processing") for s in json.loads(urllib.request.urlopen(req, timeout=2).read()))
        if busy and not now:
            print(time.time(), flush=True)
        busy = now
    except Exception:
        pass
    time.sleep(0.05)
"""
    return subprocess.Popen(["docker", "run", "--rm", "--network", net, "-e", "K", "python:3.13-slim", "python3", "-c", code], stdout=subprocess.PIPE, text=True,
                            env={"K": env("ENGINE_KEY"), "PATH": "/usr/local/bin:/usr/bin:/bin"})


def set_mode(admin, mode):
    status, body = admin.call("PUT", "/api/admin/config", {"changes": [{"key": "Gateway:Mode", "value": mode}]})
    if status != 200:
        sys.exit(f"could not set Gateway:Mode={mode}: {status} {body}")
    time.sleep(3)


def ledger_check(person, since):
    """Each request's tokens in the ledger, in order, for the person since a time (the mirror of LiteLLM's log takes a few seconds)."""
    time.sleep(12)
    return psql(f"select prompt_tokens, cached_tokens, completion_tokens, cost::text, status, coalesce(refusal,'') from gateway.requests "
                f"where person = '{person}' and started_at >= '{since}' order by started_at")


def run_mode(admin, mode, key, person, model, args, out):
    set_mode(admin, mode)
    res = {"mode": mode}
    since = psql("select now()")[0][0]
    # Warm the model (its first request after a switch loads nothing new, but a cache may be cold).
    stream(GATEWAY, key, model, "Say hello.", 8)
    w1 = [stream(GATEWAY, key, model, "Count from one to twenty in words.", 64) for _ in range(args.requests)]
    res["w1"] = summary(w1)
    usages = [r["usage"] for r in w1 if r["ok"]]
    res["w3"] = {}
    containers = ["app"] + [n for n in subprocess.run(["docker", "ps", "--format", "{{.Names}}"], capture_output=True, text=True).stdout.split() if n.startswith("litellm") or n.startswith("arena-litellm")]
    for n in [int(x) for x in args.concurrency.split(",")]:
        with Stats(containers) as st:
            rs, took = at_once(n, max(n * 2, 8), lambda: stream(GATEWAY, key, model, "Write a short paragraph about the sea.", 128))
        s = summary(rs)
        s["tokens_per_second"] = round(s["output_tokens"] / took, 1)
        s["resources"] = st.result()
        res["w3"][str(n)] = s
        usages += [r["usage"] for r in rs if r["ok"]]
    status, answer = plain(GATEWAY, key, model, {})
    res["w5_plain"] = {"status": status, "usage": isinstance(answer, dict) and "usage" in answer,
                       "finish_reason": isinstance(answer, dict) and answer.get("choices", [{}])[0].get("finish_reason")}
    tool = {"tools": [{"type": "function", "function": {"name": "add", "description": "Adds two numbers", "parameters": {"type": "object", "properties": {"a": {"type": "number"}, "b": {"type": "number"}}, "required": ["a", "b"]}}}],
            "tool_choice": "required", "messages": [{"role": "user", "content": "Add 2 and 3 with the tool."}]}
    status, answer = plain(GATEWAY, key, model, tool)
    calls = isinstance(answer, dict) and answer.get("choices", [{}])[0].get("message", {}).get("tool_calls") or []
    valid = True
    for c in calls:
        try:
            json.loads(c["function"]["arguments"])
        except (ValueError, KeyError, TypeError):
            valid = False
    res["w5_tool"] = {"status": status, "calls": len(calls), "valid_json": valid}
    # The ledger against what each answer said of its usage.
    rows = ledger_check(person, since)
    done = [r for r in rows if r[4] == "done"]
    said = sorted((u.get("prompt_tokens", 0), u.get("completion_tokens", 0)) for u in usages if u)
    booked = sorted((int(r[0]), int(r[2])) for r in done)
    missing = [u for u in said if u not in booked]
    res["ledger"] = {"answers": len(said), "rows_done": len(done), "usage_not_in_ledger": len(missing)}
    # Limits: 5 a minute, 8 sent at once.
    admin.call("PUT", f"/api/admin/people/{out['person_id']}/limits", {"requestsPerMinute": 5, "tokensPerMinute": None})
    time.sleep(max(0, 61 - time.gmtime().tm_sec))  # a fresh minute
    rs, _ = at_once(8, 8, lambda: stream(GATEWAY, key, model, "Hi", 4))
    res["limits"] = {"passed": sum(1 for r in rs if r["ok"]), "refused_429": sum(1 for r in rs if r["status"] == 429),
                     "retry_after": sorted({r.get("retry_after") for r in rs if r["status"] == 429} - {None})}
    admin.call("PUT", f"/api/admin/people/{out['person_id']}/limits", {"requestsPerMinute": None, "tokensPerMinute": None})
    # Abort: closed after 5 tokens of a long answer; then how soon llama.cpp's slots are all idle again (watched from its
    # network by a helper started first, which says when each time they went idle).
    watcher = slot_watcher()
    time.sleep(3)
    stream(GATEWAY, key, model, "Write a very long story about a lighthouse.", 2000, close_after=5)
    closed_at = time.time()
    idle = []
    if watcher:
        try:
            idle = [float(x) for x in watcher.communicate(timeout=90)[0].split()]
        except (subprocess.TimeoutExpired, ValueError):
            watcher.kill()
    freed = next((round(t - closed_at, 2) for t in idle if t >= closed_at - 0.05), None)
    res["abort"] = {"slot_free_after_s": freed}
    return res


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--model", default=env("MODEL_NAME"))
    p.add_argument("--requests", type=int, default=50, help="W1's answers per run")
    p.add_argument("--runs", type=int, default=3)
    p.add_argument("--concurrency", default="1,4,8,16,32")
    p.add_argument("--modes", default="litellm,own")
    p.add_argument("--out", default=str(ROOT / "gateway-compare.json"))
    args = p.parse_args()
    admin = Session()
    status, _ = admin.call("POST", "/api/auth/login", {"userName": "admin", "password": env("ADMIN_PASSWORD")})
    if status != 200:
        sys.exit(f"the admin could not sign in: {status}")
    name = "gwcmp" + secrets.token_hex(3)
    status, made = admin.call("POST", "/api/admin/people", {"userName": name, "email": f"{name}@example.test"})
    if status not in (200, 201):
        sys.exit(f"could not make the test person: {status} {made}")
    out = {"model": args.model, "person": f"{name}@example.test", "person_id": made["id"], "runs": []}
    key = made["apiKey"]
    try:
        out["direct"] = direct_baseline(args.model, args.requests, 64)
        for run in range(args.runs):
            for mode in args.modes.split(","):
                r = run_mode(admin, mode, key, out["person"], args.model, args, out)
                r["run"] = run + 1
                out["runs"].append(r)
                print(json.dumps({"mode": mode, "run": run + 1, "w1": r["w1"], "ledger": r["ledger"], "limits": r["limits"]}), flush=True)
    finally:
        set_mode(admin, "litellm")
        admin.call("DELETE", f"/api/admin/people/{made['id']}")
        Path(args.out).write_text(json.dumps(out, indent=2), encoding="utf-8")
    print(f"written: {args.out}")


if __name__ == "__main__":
    main()
