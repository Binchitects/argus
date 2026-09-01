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
VLLM = os.environ.get("VLLM_URL", "http://vllm:8000")
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


def main() -> int:
    stamp = int(time.time())
    email = f"e2e-{stamp}@example.com"
    print(f"\n{DIM}  identity under test: {email}{OFF}\n", flush=True)

    # --- the engine ------------------------------------------------------
    try:
        served = [m["id"] for m in call(VLLM, "/v1/models").get("data", [])]
        record("vLLM is serving", bool(served), f"models={served}")
    except Exception as exc:
        record("vLLM is serving", False, type(exc).__name__)
        served = []

    # --- the gateway advertises it ---------------------------------------
    gw_models = [m["id"] for m in call(GW, "/v1/models", token=MASTER).get("data", [])]
    record("gateway advertises models", bool(gw_models), f"{gw_models}")
    # The name clients use must resolve to something the engine actually has,
    # or every request 404s at the engine with a healthy-looking gateway.
    record("gateway names match the engine",
           any(m in gw_models for m in ("local", *served)),
           f"engine={served}")

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
         {"model": model, "messages": [{"role": "user", "content": "Say hi"}],
          "max_tokens": 24}, token=key)
    after = before
    for _ in range(25):
        time.sleep(3)
        after = spend(email)
        if after > before:
            break
    record("API usage attributed to the person", after > before,
           f"${before:.6f} -> ${after:.6f}")

    # --- chat path attributes to the SAME person -------------------------
    try:
        tok = call(WEBUI, "/api/v1/auths/signup",
                   {"name": "E2E", "email": email, "password": "Str0ng-E2E-Passw0rd"})["token"]
        before = spend(email)
        call(WEBUI, "/api/chat/completions",
             {"model": model, "messages": [{"role": "user", "content": "Say hi"}],
              "max_tokens": 24, "stream": False}, token=tok)
        after = before
        for _ in range(25):
            time.sleep(3)
            after = spend(email)
            if after > before:
                break
        record("chat usage attributed to the same person", after > before,
               f"${before:.6f} -> ${after:.6f}")

        # --- and the ceiling binds there too -----------------------------
        refused = False
        for _ in range(5):
            try:
                call(WEBUI, "/api/chat/completions",
                     {"model": model,
                      "messages": [{"role": "user", "content": "hi"}],
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
