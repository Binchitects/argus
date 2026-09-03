#!/usr/bin/env python3
"""End-to-end check of the whole stack, from inside the compose network.

Asserts the things that are easy to believe without evidence:

  * the engine serves the model the gateway advertises
  * an API call attributes spend to the person whose key made it
  * a chat through Open WebUI attributes to the SAME person, so their total
    spans both surfaces rather than being two unrelated numbers
  * a person over their ceiling is refused on the chat path, not merely
    recorded -- attribution without enforcement is the failure this stack
    spent the longest getting wrong

Run it through a throwaway container on llm-net:

    docker run --rm --network llm-net -e MK=... -v "$PWD/scripts:/s:ro" \\
        python:3.13-slim python /s/e2e-check.py
"""
from __future__ import annotations

import json
import os
import sys
import time
import urllib.error
import urllib.request

GW = os.environ.get("GATEWAY_URL", "http://litellm:4000")
WEBUI = os.environ.get("WEBUI_URL", "http://open-webui:8080")
# The engine is whichever one is actually serving. vLLM runs as a compose
# service; Ollama runs on the HOST and is reached through the Docker gateway.
# Naming only vLLM here made this check engine-specific when its intent --
# "something is really serving the models the gateway advertises" -- is not.
ENGINES = [
    ("vLLM", os.environ.get("VLLM_URL", "http://vllm:8000") + "/v1/models",
     os.environ.get("VLLM_API_KEY")),
    ("Ollama", os.environ.get("OLLAMA_URL",
                              "http://host.docker.internal:11434") + "/v1/models",
     None),
]
ARGUS = os.environ.get("ARGUS_URL", "http://argus:7700")
MASTER = os.environ["MK"]

GREEN, RED, YELLOW, DIM, OFF = (
    "\033[32m", "\033[31m", "\033[33m", "\033[90m", "\033[0m")
results: list[tuple[str, bool, str]] = []


def record(name: str, passed: bool, detail: str = "") -> bool:
    mark = f"{GREEN}PASS{OFF}" if passed else f"{RED}FAIL{OFF}"
    print(f"  [{mark}] {name}" + (f"  {DIM}{detail}{OFF}" if detail else ""),
          flush=True)
    results.append((name, passed, detail))
    return passed


def call(base, path, payload=None, token=None, headers=None, timeout=600):
    head = {"Content-Type": "application/json"}
    if token:
        head["Authorization"] = f"Bearer {token}"
    head.update(headers or {})
    req = urllib.request.Request(
        base + path, method="POST" if payload is not None else "GET",
        headers=head,
        data=json.dumps(payload).encode() if payload is not None else None)
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        body = resp.read().decode("utf-8", "replace")
    try:
        return json.loads(body)
    except ValueError:
        return body


def spend(user_id: str) -> float:
    got = call(GW, f"/user/info?user_id={user_id}", token=MASTER)
    return float((got.get("user_info", got)).get("spend") or 0)


def wait_for_spend(user_id: str, baseline: float, seconds: int = 180) -> float:
    """Poll until spend rises above `baseline`, or give up.

    Generous, because LiteLLM writes spend asynchronously and caches the user
    record. A short window here does not just flake -- it mis-attributes: the
    API call's spend arrives during the CHAT step and the two checks swap
    verdicts, which is exactly what happened at 75s.
    """
    latest = baseline
    for _ in range(max(1, seconds // 3)):
        time.sleep(3)
        latest = spend(user_id)
        if latest > baseline:
            return latest
    return latest


def main() -> int:
    stamp = int(time.time())
    email = f"e2e-{stamp}@example.com"
    # Unique per run, and that is load-bearing. litellm_settings.cache is on,
    # so an identical prompt is served from Redis at zero cost and records no
    # spend -- the attribution checks below then fail against a stack that is
    # working perfectly. The first run of this file passed and every rerun
    # failed, which is exactly what a cache hit looks like from the outside.
    prompt = f"Say hi. Request {stamp}."
    print(f"\n{DIM}  identity under test: {email}{OFF}\n", flush=True)

    # --- the engine ------------------------------------------------------
    # vLLM enforces its own api key (VLLM_API_KEY); without it the engine
    # answers 401 and this reads as "not serving".
    served, engine, why = [], None, []
    for name, url, token in ENGINES:
        try:
            served = [m["id"] for m in
                      call(url, "", token=token, timeout=30).get("data", [])]
        except Exception as exc:
            why.append(f"{name}:{type(exc).__name__}")
            continue
        if served:
            engine = name
            break
        why.append(f"{name}:no-models")
    record("an engine is serving", bool(served),
           f"{engine} models={served}" if engine else " ".join(why))

    # --- the gateway advertises it ---------------------------------------
    gw_models = [m["id"] for m in call(GW, "/v1/models", token=MASTER).get("data", [])]
    record("gateway advertises models", bool(gw_models), f"{gw_models}")
    # Every advertised name is EXERCISED, not just matched against the
    # engine's list. An alias like `local` passes a name comparison while
    # mapping to a checkpoint the engine does not serve, so the gateway looks
    # healthy and every real request 404s behind it. That is not theoretical:
    # a crash mid-`--force-recreate` left the engine on the old model while
    # the gateway advertised the new one, and this check passed anyway
    # because `local` was present in both.
    unusable = []
    for name in gw_models:
        try:
            call(GW, "/chat/completions",
                 {"model": name,
                  "messages": [{"role": "user", "content": f"ping {stamp}"}],
                  "max_tokens": 1}, token=MASTER)
        except urllib.error.HTTPError as exc:
            unusable.append(f"{name}:{exc.code}")
        except Exception as exc:
            unusable.append(f"{name}:{type(exc).__name__}")
    record("every advertised name actually answers", not unusable,
           f"engine={served}" if not unusable else f"unusable={unusable}")

    # --- provision one person, both records ------------------------------
    for path, body in (
        ("/user/new", {"user_id": email, "user_email": email,
                       "user_role": "internal_user", "max_budget": 1.0}),
        ("/end_user/new", {"user_id": email, "max_budget": 0.000004}),
    ):
        try:
            call(GW, path, body, token=MASTER)
        except urllib.error.HTTPError:
            pass
    key = call(GW, "/key/generate",
               {"user_id": email, "key_alias": f"e2e-{stamp}"}, token=MASTER)["key"]
    record("per-person key minted", bool(key))

    model = "local" if "local" in gw_models else (gw_models[0] if gw_models else "local")

    # --- API path attributes ---------------------------------------------
    before = spend(email)
    call(GW, "/chat/completions",
         {"model": model, "messages": [{"role": "user", "content": prompt}],
          "max_tokens": 24}, token=key)
    after = wait_for_spend(email, before)
    record("API usage attributed to the person", after > before,
           f"${before:.6f} -> ${after:.6f}")

    # --- chat path attributes to the SAME person -------------------------
    try:
        tok = call(WEBUI, "/api/v1/auths/signup",
                   {"name": "E2E", "email": email, "password": "Str0ng-E2E-Passw0rd"})["token"]
        before = spend(email)
        call(WEBUI, "/api/chat/completions",
             {"model": model, "messages": [{"role": "user", "content": prompt}],
              "max_tokens": 24, "stream": False}, token=tok)
        after = wait_for_spend(email, before)
        record("chat usage attributed to the same person", after > before,
               f"${before:.6f} -> ${after:.6f}")

        # --- and the ceiling binds there too -----------------------------
        refused = False
        for _ in range(5):
            try:
                call(WEBUI, "/api/chat/completions",
                     {"model": model,
                      "messages": [{"role": "user", "content": prompt + " again"}],
                      "max_tokens": 16, "stream": False}, token=tok)
            except urllib.error.HTTPError as exc:
                refused = "budget" in exc.read().decode("utf-8", "replace").lower()
                break
            time.sleep(5)
        record("over-budget person refused in chat", refused,
               "attribution without enforcement is the failure mode this catches")
    except urllib.error.HTTPError as exc:
        record("chat path", False, f"HTTP {exc.code}")

    # --- argus, if it is running -----------------------------------------
    try:
        health = call(ARGUS, "/healthz", timeout=20)
        record("argus reachable", bool(health), str(health)[:40])
    except Exception:
        print(f"  [{YELLOW}SKIP{OFF}] argus not running", flush=True)

    passed = sum(1 for _n, ok, _d in results if ok)
    print(f"\n  {passed}/{len(results)} checks passed\n", flush=True)
    return 0 if passed == len(results) else 1


if __name__ == "__main__":
    sys.exit(main())
