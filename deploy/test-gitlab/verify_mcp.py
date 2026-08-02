#!/usr/bin/env python3
"""Task 11 -- verify the MCP surface with real developer tokens over the wire.

`verify.py` proves the ACL and the queries behave, but it calls them in-process.
This drives the actual MCP protocol through the actual server with the actual
official client, using two real GitLab personal access tokens, because that is
the only thing that proves the *system* filters rather than the *code*.

Run after seed.py and verify.py:

    python deploy/test-gitlab/verify_mcp.py

Starts `argus serve` itself, tears it down at the end, and exits non-zero on any
failed check.
"""
from __future__ import annotations

import asyncio
import json
import os
import pathlib
import subprocess
import sys
import time

import httpx

HERE = pathlib.Path(__file__).resolve().parent
ROOT = HERE.parent.parent
WORK = HERE / "work"
SEEDED = HERE / "seeded.json"
HOST, PORT = "127.0.0.1", 7761
BASE = f"http://{HOST}:{PORT}"

results: list[tuple[str, bool, str]] = []


def check(name: str, ok: bool, detail: str = "") -> bool:
    results.append((name, ok, detail))
    print(f"  [{'PASS' if ok else 'FAIL'}] {name}" + (f" -- {detail}" if detail else ""))
    return ok


def start_server() -> subprocess.Popen:
    env = dict(os.environ)
    proc = subprocess.Popen(
        [sys.executable, "-m", "argus.cli", "serve",
         "--config", str(WORK / "config.yaml"),
         "--host", HOST, "--port", str(PORT),
         # The bind host and the Host header are different things: the SDK's
         # DNS-rebinding allowlist is fixed at construction and validates the
         # inbound Host, which for these calls is "127.0.0.1:7761".
         "--allowed-host", f"{HOST}:{PORT}", "--allowed-host", HOST],
        cwd=ROOT, env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True,
    )
    for _ in range(60):
        time.sleep(1)
        if proc.poll() is not None:
            print(proc.stdout.read() if proc.stdout else "")
            raise RuntimeError("server exited during startup")
        try:
            if httpx.get(f"{BASE}/healthz", timeout=2).status_code == 200:
                return proc
        except Exception:
            pass
    raise TimeoutError("server did not become healthy")


async def call_tools(token: str) -> dict:
    """Drive a full MCP session as the official client would."""
    from mcp import ClientSession
    from mcp.client.streamable_http import streamablehttp_client

    out: dict = {}
    async with streamablehttp_client(
        f"{BASE}/mcp", headers={"Authorization": f"Bearer {token}"}
    ) as (read, write, _):
        async with ClientSession(read, write) as session:
            await session.initialize()
            listed = await session.list_tools()
            out["tools"] = sorted(t.name for t in listed.tools)

            async def call(name, args):
                r = await session.call_tool(name, args)
                # Read structuredContent, NOT the text blocks. FastMCP emits one
                # text block PER ROW, so joining them yields several concatenated
                # JSON objects -- not valid JSON. Parsing that silently produced
                # an empty result set and made every isolation assertion below
                # pass vacuously, which is a bug in this script rather than in
                # the server. structuredContent carries the real list.
                sc = getattr(r, "structuredContent", None)
                if isinstance(sc, dict) and "result" in sc:
                    return sc["result"]
                return {"_isError": r.isError,
                        "_raw": [getattr(c, "text", "") for c in r.content]}

            out["find_symbol"] = await call("find_symbol", {"name": "DecodeFrame"})
            out["find_references"] = await call("find_references", {"name": "DecodeFrame"})
            out["index_status"] = await call("index_status", {})
    return out


def repos_in(payload) -> set[str]:
    rows = payload if isinstance(payload, list) else payload.get("result", payload)
    if not isinstance(rows, list):
        return set()
    found = set()
    for r in rows:
        if isinstance(r, dict):
            v = r.get("path_with_namespace") or r.get("repo")
            if v:
                found.add(v)
    return found


async def main_async() -> int:
    seeded = json.loads(SEEDED.read_text(encoding="utf-8"))
    users = seeded["users"]

    print("== starting argus serve ==")
    proc = start_server()
    try:
        print("\n== unauthenticated access ==")
        r = httpx.post(f"{BASE}/mcp", json={"jsonrpc": "2.0", "id": 1, "method": "initialize"},
                       timeout=10)
        check("an unauthenticated MCP call is refused", r.status_code == 401,
              f"got {r.status_code}")
        check("/healthz needs no auth", httpx.get(f"{BASE}/healthz", timeout=5).status_code == 200)
        r = httpx.post(f"{BASE}/mcp", headers={"Authorization": "Bearer not-a-real-token"},
                       json={"jsonrpc": "2.0", "id": 1, "method": "initialize"}, timeout=30)
        check("a garbage token is refused", r.status_code == 401, f"got {r.status_code}")

        print("\n== per-developer MCP sessions ==")
        per_user = {}
        for username, u in users.items():
            res = await call_tools(u["token"])
            per_user[username] = res
            print(f"  {username}: tools={res['tools']}")
            check(f"{username} sees all five tools",
                  len(res["tools"]) == 5,
                  ", ".join(res["tools"]))

        a, b = per_user["dev_alpha"], per_user["dev_beta"]
        a_sym, b_sym = repos_in(a["find_symbol"]), repos_in(b["find_symbol"])
        a_ref, b_ref = repos_in(a["find_references"]), repos_in(b["find_references"])
        print(f"  find_symbol     alpha={sorted(a_sym)} beta={sorted(b_sym)}")
        print(f"  find_references alpha={sorted(a_ref)} beta={sorted(b_ref)}")

        # Guard against a vacuous pass: if BOTH are empty, disjointness is
        # trivially true and proves nothing. This is the exact failure this
        # project has hit repeatedly, including in verify.py itself.
        check("results are non-empty, so isolation below is meaningful",
              bool(a_ref) and bool(b_ref),
              f"alpha={len(a_ref)} beta={len(b_ref)} reference-bearing repos")
        check("OVER THE WIRE: the two developers' results are disjoint",
              not (a_ref & b_ref), f"overlap={sorted(a_ref & b_ref)}")
        check("dev_alpha only ever sees eal-core",
              all("eal-core" in x for x in a_ref | a_sym), f"{sorted(a_ref | a_sym)}")
        check("dev_beta only ever sees etl-decoder",
              all("etl-decoder" in x for x in b_ref | b_sym), f"{sorted(b_ref | b_sym)}")
        check("driver-shim is invisible to BOTH developers",
              not any("driver-shim" in x for x in a_ref | a_sym | b_ref | b_sym))
        check("index_status is scoped per developer",
              repos_in(a["index_status"]) != repos_in(b["index_status"]))
    finally:
        proc.terminate()
        try:
            proc.wait(timeout=15)
        except subprocess.TimeoutExpired:
            proc.kill()

    passed = sum(1 for _, ok, _ in results if ok)
    print(f"\n{passed}/{len(results)} checks passed")
    return 0 if passed == len(results) else 1


if __name__ == "__main__":
    sys.path.insert(0, str(ROOT))
    sys.exit(asyncio.run(main_async()))
