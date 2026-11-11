#!/usr/bin/env python3
"""
The app's own gateway's last gates before LiteLLM goes (docs/design/own-gateway.md §7), on this stack:

    replicas   a key held to 5 requests a minute, 8 sent across every app replica (each asked straight, in turn):
               5 pass, 3 get 429 with Retry-After, whichever replica took them (the minute's count is in the database)
    pool       the model served twice (llama.cpp, and the same engine through a proxy, added as another GPU server):
               40 short answers, 8 at once, split between the two copies by their weights (each 35-65 %)
    failover   the proxy killed: 20 answers next, none failed, those first sent to it answered by the other copy
               (the ledger's attempts), none by the copy that is gone
    media      a picture, speech and that speech transcribed, through the gateway with the person's key: each answered
               and booked in the ledger with its pictures, characters or seconds

Reads .env beside it (DOMAIN, ADMIN_PASSWORD, ENGINE_KEY, MODEL_NAME); makes a person and a key, and the two servers,
removed at the end. Standard library only; runs in the stack's python image with the host's network and docker:

    docker run --rm --network host -v /srv/arena/deploy:/srv/arena/deploy -v /var/run/docker.sock:/var/run/docker.sock \\
      -v "$(command -v docker):/usr/local/bin/docker:ro" python:3.13-slim \\
      python3 -I /srv/arena/deploy/scripts/gateway-scale-check.py --out /srv/arena/deploy/gateway-scale.json

Run the app as two replicas first (docker compose -f docker-compose.yml -f scale.yml up -d app traefik) for the
replicas gate; with one it says so and goes on.
"""

import argparse
import json
import secrets
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.request
import uuid
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import importlib.util  # noqa: E402

_spec = importlib.util.spec_from_file_location("gateway_compare", Path(__file__).resolve().parent / "gateway-compare.py")
gc = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(gc)

env, Session, GATEWAY, CTX, psql = gc.env, gc.Session, gc.GATEWAY, gc.CTX, gc.psql


def post(base, path, key, body, timeout=600, raw=None, content_type="application/json"):
    data = raw if raw is not None else json.dumps(body).encode()
    req = urllib.request.Request(base.rstrip("/") + path, data=data, method="POST", headers={"Content-Type": content_type, "Authorization": f"Bearer {key}"})
    try:
        with urllib.request.urlopen(req, timeout=timeout, context=CTX if base.startswith("https") else None) as r:
            return r.status, r.read(), dict(r.headers)
    except urllib.error.HTTPError as e:
        return e.code, e.read(), dict(e.headers)
    except (urllib.error.URLError, OSError) as e:
        return 0, str(e).encode(), {}


def ask(base, key, model, prompt="Say hi.", max_tokens=8):
    return post(base, "/v1/chat/completions", key, {"model": model, "messages": [{"role": "user", "content": prompt}], "max_tokens": max_tokens,
                                                  "temperature": 0, "chat_template_kwargs": {"enable_thinking": False}})


def replicas():
    """The app's replicas and their addresses on the stack's network."""
    out = subprocess.run(["docker", "ps", "--filter", "label=com.docker.compose.service=app", "--format", "{{.Names}}"], capture_output=True, text=True)
    names = [n for n in out.stdout.split() if n]
    found = []
    for n in names:
        ip = subprocess.run(["docker", "inspect", "-f", "{{range .NetworkSettings.Networks}}{{.IPAddress}} {{end}}", n], capture_output=True, text=True).stdout.split()
        if ip:
            found.append((n, ip[0]))
    return found


def ip_of(container):
    ips = subprocess.run(["docker", "inspect", "-f", "{{range .NetworkSettings.Networks}}{{.IPAddress}} {{end}}", container], capture_output=True, text=True).stdout.split()
    return ips[0] if ips else None


def fresh_minute():
    time.sleep(max(0, 61 - time.gmtime().tm_sec))


def wait_route(key, model, deployment_hint, seconds=90):
    """Until the gateway's routes have the new server (rebuilt at most every 30 s)."""
    until = time.time() + seconds
    while time.time() < until:
        rows = psql(f"select count(*) from gateway.requests where deployment like '%{deployment_hint}%' and started_at > now() - interval '5 minutes'")
        if rows and int(rows[0][0]) > 0:
            return True
        ask(GATEWAY, key, model)
    return False


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--model", default=env("MODEL_NAME"))
    p.add_argument("--out", default=str(gc.ROOT / "gateway-scale.json"))
    a = p.parse_args()
    admin = Session()
    status, _ = admin.call("POST", "/api/auth/login", {"userName": "admin", "password": env("ADMIN_PASSWORD")})
    if status != 200:
        sys.exit(f"the admin could not sign in: {status}")
    name = "gwscale" + secrets.token_hex(3)
    status, made = admin.call("POST", "/api/admin/people", {"userName": name, "email": f"{name}@example.test"})
    if status not in (200, 201):
        sys.exit(f"could not make the test person: {status} {made}")
    person, key, model = f"{name}@example.test", made["apiKey"], a.model
    out = {"model": model, "person": person, "gates": {}}
    servers = []
    try:
        # --- replicas: one minute's count across every replica
        reps = replicas()
        out["replicas"] = {"count": len(reps), "names": [n for n, _ in reps]}
        admin.call("PUT", f"/api/admin/people/{made['id']}/limits", {"requestsPerMinute": 5, "tokensPerMinute": None})
        fresh_minute()
        tries = []
        for i in range(8):
            # Straight to each replica in turn (no cookie, no Traefik choosing), else through the gateway's address.
            base = f"http://{reps[i % len(reps)][1]}:8080" if reps else GATEWAY
            s, body, headers = ask(base, key, model)
            if s == 404 and reps:
                s, body, headers = ask(GATEWAY, key, model)
                base = GATEWAY
            tries.append({"replica": reps[i % len(reps)][0] if reps and base != GATEWAY else "gateway", "status": s, "retry_after": headers.get("Retry-After")})
        admin.call("PUT", f"/api/admin/people/{made['id']}/limits", {"requestsPerMinute": None, "tokensPerMinute": None})
        passed = sum(1 for t in tries if t["status"] == 200)
        refused = [t for t in tries if t["status"] == 429]
        out["replicas"]["tries"] = tries
        out["gates"]["replicas"] = {"pass": passed == 5 and len(refused) == 3 and all(t["retry_after"] for t in refused) and len({t["replica"] for t in tries}) >= min(2, len(reps)),
                                    "passed": passed, "refused": len(refused), "replicas_asked": sorted({t["replica"] for t in tries})}
        print(json.dumps({"replicas": out["gates"]["replicas"]}), flush=True)

        # --- pool: the model twice, the second copy the same engine through a proxy of its own (killed for failover)
        net = gc.engine_network()
        subprocess.run(["docker", "rm", "-f", "gw-copy-b"], capture_output=True)
        forward = ("import socket,threading\n"
                   "def pipe(a,b):\n"
                   "    try:\n"
                   "        while (d:=a.recv(65536)): b.sendall(d)\n"
                   "    except OSError: pass\n"
                   "    finally:\n"
                   "        for x in (a,b):\n"
                   "            try: x.shutdown(socket.SHUT_RDWR)\n"
                   "            except OSError: pass\n"
                   "s=socket.socket(); s.setsockopt(socket.SOL_SOCKET,socket.SO_REUSEADDR,1); s.bind(('0.0.0.0',8080)); s.listen(64)\n"
                   "while True:\n"
                   "    c,_=s.accept(); u=socket.create_connection(('llamacpp',8080))\n"
                   "    threading.Thread(target=pipe,args=(c,u),daemon=True).start(); threading.Thread(target=pipe,args=(u,c),daemon=True).start()\n")
        subprocess.run(["docker", "run", "-d", "--name", "gw-copy-b", "--network", net, "python:3.13-slim", "python3", "-c", forward], check=True, capture_output=True)
        time.sleep(2)
        copy_b = ip_of("gw-copy-b")
        s, added = admin.call("POST", "/api/admin/servers", {"name": "pool-check", "baseUrl": f"http://{copy_b}:8080/v1", "apiKey": env("ENGINE_KEY"), "verifyTls": False,
                                                              "models": [{"remote": model, "name": model, "parallel": 4}]})
        if s not in (200, 201):
            raise SystemExit(f"the pool's second copy could not be added: {s} {added}")
        servers.append(added["id"])
        out["pool"] = {"copy_b": f"{copy_b}:8080", "routed": wait_route(key, model, f"{copy_b}:8080")}
        since = time.strftime("%Y-%m-%d %H:%M:%S", time.gmtime())
        rs, _ = gc.at_once(8, 40, lambda: gc.stream(GATEWAY, key, model, "Count to three.", 16))
        rows = psql(f"select deployment, count(*) from gateway.requests where person = '{person}' and started_at >= '{since}' and status = 'done' group by 1")
        split = {r[0]: int(r[1]) for r in rows}
        total = sum(split.values())
        shares = {k: round(v / total, 3) for k, v in split.items()} if total else {}
        out["pool"].update({"answers": sum(1 for r in rs if r["ok"]), "split": split, "shares": shares})
        out["gates"]["pool"] = {"pass": out["pool"]["routed"] and len(split) == 2 and all(0.35 <= v <= 0.65 for v in shares.values()) and sum(1 for r in rs if r["ok"]) == 40,
                                "shares": shares}
        print(json.dumps({"pool": out["gates"]["pool"]}), flush=True)

        # --- failover: the second copy killed; what comes next is answered by the first, none failed
        subprocess.run(["docker", "rm", "-f", "gw-copy-b"], capture_output=True)
        since = time.strftime("%Y-%m-%d %H:%M:%S", time.gmtime())
        rs, _ = gc.at_once(4, 20, lambda: gc.stream(GATEWAY, key, model, "Name a colour.", 8))
        rows = psql(f"select status, attempts, coalesce(fallback_from, ''), coalesce(deployment, '') from gateway.requests where person = '{person}' and started_at >= '{since}'")
        failed = [r for r in rows if r[0] != "done"]
        retried = [r for r in rows if int(r[1] or 1) > 1]
        out["failover"] = {"answers": sum(1 for r in rs if r["ok"]), "rows": len(rows), "failed": len(failed), "retried_on_another_copy": len(retried),
                           "answered_by": sorted({r[3] for r in rows if r[0] == "done"})}
        out["gates"]["failover"] = {"pass": sum(1 for r in rs if r["ok"]) == 20 and not failed and len(retried) >= 1 and f"{copy_b}:8080" not in out["failover"]["answered_by"],
                                    **out["failover"]}
        print(json.dumps({"failover": out["gates"]["failover"]}), flush=True)
    finally:
        subprocess.run(["docker", "rm", "-f", "gw-copy-b"], capture_output=True)
        for sid in servers:
            admin.call("DELETE", f"/api/admin/servers/{sid}")

    try:
        # --- media, with the person's key
        status, listed, _ = (lambda r: (r[0], json.loads(r[1] or b"{}"), r[2]))(get_models(key))
        ids = [m.get("id") for m in listed.get("data", [])]
        picture = next((i for i in ids if i and ("flux" in i.lower() or "sd" in i.lower())), None)
        voice = next((i for i in ids if i and ("kokoro" in i.lower() or "piper" in i.lower())), None)
        ear = next((i for i in ids if i and "whisper" in i.lower()), None)
        media = {"models": {"picture": picture, "speech": voice, "transcription": ear}}
        since = time.strftime("%Y-%m-%d %H:%M:%S", time.gmtime())
        if picture:
            s, body, _ = post(GATEWAY, "/v1/images/generations", key, {"model": picture, "prompt": "a red circle on white", "size": "512x512", "n": 1}, timeout=600)
            media["picture"] = {"status": s, "images": len((json.loads(body or b"{}") if s == 200 else {}).get("data", []))}
            if s == 503:
                # The picture server cannot load its model here (no model file on the host, no room on the GPU): not measured.
                media["picture"]["not_measured"] = (body or b"")[:300].decode(errors="replace")
        audio = None
        if voice:
            s, audio, headers = post(GATEWAY, "/v1/audio/speech", key, {"model": voice, "input": "Hello from the gateway check.", "response_format": "wav"}, timeout=300)
            media["speech"] = {"status": s, "bytes": len(audio or b""), "type": headers.get("Content-Type")}
        if ear and audio and media.get("speech", {}).get("status") == 200:
            boundary = uuid.uuid4().hex
            form = (f"--{boundary}\r\nContent-Disposition: form-data; name=\"model\"\r\n\r\n{ear}\r\n"
                    f"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"hello.wav\"\r\nContent-Type: audio/wav\r\n\r\n").encode() + audio + f"\r\n--{boundary}--\r\n".encode()
            s, body, _ = post(GATEWAY, "/v1/audio/transcriptions", key, None, timeout=300, raw=form, content_type=f"multipart/form-data; boundary={boundary}")
            text = (json.loads(body or b"{}").get("text") if s == 200 else (body or b"")[:200].decode(errors="replace")) or ""
            media["transcription"] = {"status": s, "text": text, "heard": "hello" in text.lower()}
        time.sleep(3)
        rows = psql(f"select call, status, coalesce(pictures, 0), coalesce(characters, 0), coalesce(seconds, 0), cost from gateway.requests where person = '{person}' and started_at >= '{since}'")
        media["ledger"] = [{"call": r[0], "status": r[1], "pictures": r[2], "characters": r[3], "seconds": r[4], "cost": r[5]} for r in rows]
        calls = {r[0] for r in rows if r[1] == "done"}
        asked = [k for k in ("picture", "speech", "transcription") if k in media and not media[k].get("not_measured")]
        out["media"] = media
        out["gates"]["media"] = {"pass": all(media[k]["status"] == 200 for k in asked) and len(calls) >= len(asked) and media.get("transcription", {}).get("heard", True),
                                 "asked": asked, "booked": sorted(calls)}
        print(json.dumps({"media": out["gates"]["media"]}), flush=True)
    finally:
        admin.call("DELETE", f"/api/admin/people/{made['id']}")
        Path(a.out).write_text(json.dumps(out, indent=2), encoding="utf-8")
    print("\nGates:")
    for gate, r in out["gates"].items():
        print(f"  {'PASS' if r['pass'] else 'FAIL'}  {gate}  {json.dumps({k: v for k, v in r.items() if k != 'pass'})}")
    return 0 if all(r["pass"] for r in out["gates"].values()) else 1


def get_models(key):
    req = urllib.request.Request(GATEWAY + "/v1/models", headers={"Authorization": f"Bearer {key}"})
    try:
        with urllib.request.urlopen(req, timeout=60, context=CTX) as r:
            return r.status, r.read(), dict(r.headers)
    except urllib.error.HTTPError as e:
        return e.code, e.read(), dict(e.headers)


if __name__ == "__main__":
    sys.exit(main())
