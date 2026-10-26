"""Tools direct or as code: the same questions, the same model, the two ways of giving it its tools.

    direct   every tool's definition, one call at a time (Admin -> Models -> Tools: Direct)
    code     run_code, the tools named as Python functions for a program (As code)

Runs through Arena's own chat, as a throwaway person with Argus's documentation
tools and Tools as code, so what is measured is what people get: the system
prompt, the tool notes, the sandbox. For each model and mode, each task in a
new chat; recorded: pass or fail, the model's tokens (prompt, cached,
completion: every round of the answer summed), the seconds, the direct tool
calls and the calls a program made.

Two families of tasks:

* **single**: the ten task families of docs/measurements/model-comparison.md
  (test development ... code safety), one fact each: one or two calls.
* **many**: one question over six symbols (their IRQLs; their headers and
  import libraries): six lookups, where a program should pay.

**The answer key comes from the packs, before any model runs** (evals/README.md):
each expected token is looked up with the same Argus tools the model gets, and a
task whose token the packs do not say is dropped and reported, never graded.
Grading is substring matching on facts; in a "many" task each symbol's own line
must carry its fact.

The models' tool modes are set for the run through the admin API and put back
as they were at the end, and the throwaway person is removed, whatever happens.

    python3 evals/run_tool_modes.py --models Qwen3-4B-Q4_K_M,Qwen3.8-27B-UD-Q4_K_XL

The admin password is read from ARENA_ADMIN_PASSWORD, else from deploy/.env
(ADMIN_PASSWORD), and never printed. Results: evals/tool-modes-results.json.
"""
from __future__ import annotations

import argparse
import http.cookiejar
import json
import os
import pathlib
import re
import ssl
import sys
import time
import urllib.error
import urllib.request
import uuid

ROOT = pathlib.Path(__file__).resolve().parent.parent
OUT = ROOT / "evals" / "tool-modes-results.json"
TIMEOUT = 900

SINGLE = [
    {"id": "test-development", "prompt": "I am writing a unit test for a driver helper that wraps ExInitializeLookasideListEx. At what IRQL must my test harness call it? Quote the documented requirement.",
     "expect": ["dispatch_level"], "probe": "ExInitializeLookasideListEx"},
    {"id": "code-review", "prompt": "Review this minifilter code:\n\n```c\nVOID OnDpc(PKDPC Dpc)\n{\n    FltSendMessage(gFilter, &gPort, &msg, sizeof(msg), NULL, NULL, NULL);\n}\n```\nA DPC runs at DISPATCH_LEVEL. Is this call legal? Quote FltSendMessage's documented IRQL.",
     "expect": ["apc_level"], "probe": "FltSendMessage"},
    {"id": "performance", "prompt": "A hot loop calls std::vector::push_back one million times on an empty vector. What is push_back's documented complexity, and which single call removes the repeated reallocation cost?",
     "expect": ["amortized", "reserve"], "probe": None},
    {"id": "coding-style", "prompt": "Which MSVC compiler option selects the C++20 standard, and which option enables warning level 4?",
     "expect": ["/std:c++20", "/w4"], "probe": None},
    {"id": "sdk-support", "prompt": "Which import library must I link to call CryptAcquireContextW, and which header declares it?",
     "expect": ["advapi32.lib", "wincrypt.h"], "probe": "CryptAcquireContextW"},
    {"id": "wdk-support", "prompt": "At what IRQL is IoCreateDevice callable, and which header declares it?",
     "expect": ["apc_level", "wdm.h"], "probe": "IoCreateDevice"},
    {"id": "win32-support", "prompt": "Which header declares CreateFileW, and which import library must I link for it?",
     "expect": ["fileapi.h", "kernel32.lib"], "probe": "CreateFileW"},
    {"id": "scripting", "prompt": "Which robocopy option mirrors a directory tree, including deleting files at the destination that no longer exist at the source?",
     "expect": ["/mir"], "probe": None},
    {"id": "security-review", "prompt": "Kernel code copies a user-supplied wide string into a fixed buffer with wcscpy. Name the documented safe-string replacement routine, and the header and library it needs.",
     "expect": ["ntstrsafe.h", "ntstrsafe.lib"], "probe": "RtlStringCchCopyW"},
    {"id": "code-safety", "prompt": "Is ExAllocatePool2 safe to call from a DPC, which runs at DISPATCH_LEVEL? Quote its documented IRQL requirement and the header that declares it.",
     "expect": ["dispatch_level", "wdm.h"], "probe": "ExAllocatePool2"},
]

#: Six lookups each. The facts are read from each symbol's pack entry (its signature line) before the run.
MANY = [
    {"id": "irql-table", "fact": "irql",
     "symbols": ["IoCreateDevice", "ExAllocatePool2", "FltSendMessage", "ExInitializeLookasideListEx", "KeSetEvent", "IoCompleteRequest"],
     "prompt": "For each of these kernel routines, give its documented IRQL requirement, one line per routine: {symbols}."},
    {"id": "header-library-table", "fact": "header+library",
     "symbols": ["CreateFileW", "CryptAcquireContextW", "RegOpenKeyExW", "WSAStartup", "CoCreateInstance", "MessageBoxW"],
     "prompt": "For each of these Windows functions, give the header that declares it and the import library to link, one line per function: {symbols}."},
]

IRQL = re.compile(r"IRQL:\s*([^;\n]+?)(?:\s--|;|$)")
HEADER = re.compile(r"Header:\s*([\w.]+)")
LIBRARY = re.compile(r"Library:\s*([\w.]+)")
LEVEL = re.compile(r"(PASSIVE_LEVEL|APC_LEVEL|DISPATCH_LEVEL|DIRQL|HIGH_LEVEL)", re.I)


class Arena:
    """Arena's API with a session of its own (cookies), and Arena MCP with a key."""

    def __init__(self, base: str, verify: bool):
        self.base = base.rstrip("/")
        ctx = ssl.create_default_context()
        if not verify:
            # The local stack's own certificate.
            ctx.check_hostname = False
            ctx.verify_mode = ssl.CERT_NONE
        self.ctx = ctx
        self.web = urllib.request.build_opener(urllib.request.HTTPSHandler(context=ctx), urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))

    def call(self, method: str, path: str, body=None, headers=None, opener=None, timeout=180):
        req = urllib.request.Request(self.base + path, method=method, data=None if body is None else json.dumps(body).encode(),
                                     headers={"Content-Type": "application/json", "X-Requested-With": "eval", **(headers or {})})
        try:
            with (opener or self.web).open(req, timeout=timeout) as r:
                return r.status, r.read().decode(), dict(r.headers)
        except urllib.error.HTTPError as e:
            return e.code, e.read().decode(), dict(e.headers)

    def json(self, method: str, path: str, body=None):
        status, text, _ = self.call(method, path, body)
        if status >= 400:
            raise RuntimeError(f"{method} {path}: {status} {text[:300]}")
        return json.loads(text) if text else None


class Mcp:
    """Arena MCP as the throwaway person: the answer key is looked up with the tools the model gets."""

    def __init__(self, arena: Arena, key: str):
        self.arena, self.n = arena, 0
        self.opener = urllib.request.build_opener(urllib.request.HTTPSHandler(context=arena.ctx))
        self.headers = {"Authorization": "Bearer " + key, "Accept": "application/json, text/event-stream", "MCP-Protocol-Version": "2025-06-18"}
        self.rpc("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "eval", "version": "1"}})

    def rpc(self, method: str, params: dict):
        self.n += 1
        status, text, headers = self.arena.call("POST", "/mcp", {"jsonrpc": "2.0", "id": self.n, "method": method, "params": params}, self.headers, self.opener)
        if headers.get("Content-Type", "").startswith("text/event-stream"):
            text = [line[6:] for line in text.split("\n\n") if line.startswith("data: ") and '"id"' in line][-1]
        return json.loads(text)

    def tool(self, name: str, args: dict) -> str:
        result = self.rpc("tools/call", {"name": name, "arguments": args}).get("result") or {}
        return "".join(c.get("text", "") for c in result.get("content") or [])


def signature(mcp: Mcp, symbol: str) -> str:
    """A symbol's pack entry: its signature line (header, library, IRQL), of the entry named exactly so."""
    try:
        hits = json.loads(mcp.tool("docs_lookup", {"name": symbol}))
    except ValueError:
        return ""
    exact = [h for h in hits if h.get("name") == symbol] or hits
    return " ".join(h.get("signature") or "" for h in exact[:1])


def answer_key(mcp: Mcp) -> tuple[list[dict], list[dict], list[str]]:
    """The tasks whose facts the packs say, with the facts; and what was dropped, with why."""
    dropped = []
    single = []
    for task in SINGLE:
        if task["probe"]:
            blob = json.dumps(json.loads(mcp.tool("docs_lookup", {"name": task["probe"]}) or "[]")).lower()
            missing = [t for t in task["expect"] if t.lower() not in blob]
        else:
            missing = [t for t in task["expect"] if t.lower() not in mcp.tool("docs_search", {"query": f'"{t}"'}).lower()]
        if missing:
            dropped.append(f"{task['id']}: the packs do not say {', '.join(missing)}")
        else:
            single.append(task)
    many = []
    for task in MANY:
        facts = {}
        for symbol in task["symbols"]:
            sig = signature(mcp, symbol)
            if task["fact"] == "irql":
                level = IRQL.search(sig)
                levels = LEVEL.findall(level.group(1)) if level else []
                if levels:
                    facts[symbol] = [levels[-1].lower()]
            else:
                header, library = HEADER.search(sig), LIBRARY.search(sig)
                if header and library:
                    facts[symbol] = [header.group(1).lower(), library.group(1).lower()]
        lost = [s for s in task["symbols"] if s not in facts]
        if lost:
            dropped.append(f"{task['id']}: no {task['fact']} in the packs for {', '.join(lost)}, left out of the question")
        if len(facts) >= 3:
            many.append({**task, "facts": facts, "prompt": task["prompt"].format(symbols=", ".join(facts))})
        else:
            dropped.append(f"{task['id']}: fewer than three symbols with their facts")
    return single, many, dropped


def grade(task: dict, answer: str) -> tuple[str, list[str]]:
    low = (answer or "").lower()
    if not low.strip():
        return "EMPTY", []
    if "facts" not in task:
        missing = [t for t in task["expect"] if t.lower() not in low]
        return ("FAIL", missing) if missing else ("PASS", [])
    # Each symbol's own line carries its facts.
    lines = low.splitlines()
    missing = []
    for symbol, facts in task["facts"].items():
        own = " ".join(line for line in lines if symbol.lower() in line)
        missing += [f"{symbol}:{f}" for f in facts if f not in own]
    return ("FAIL", missing) if missing else ("PASS", [])


def ask(arena: Arena, person, model: str, prompt: str) -> dict:
    """One task in a new chat of the person's: the answer's text after its last tool result, and what it took."""
    chat = person.json("POST", "/api/chat/conversations", {"model": model, "tools": ["argus", "code"], "useArgus": True})
    started = time.monotonic()
    status, text, _ = person.call("POST", f"/api/chat/conversations/{chat['id']}/messages", {"content": prompt}, timeout=TIMEOUT)
    seconds = round(time.monotonic() - started, 1)
    if status >= 400:
        return {"error": f"{status} {text[:200]}", "seconds": seconds}
    events = [json.loads(line[6:]) for line in text.split("\n\n") if line.startswith("data: ")]
    prompt_tokens = sum(e.get("prompt") or 0 for e in events if e.get("type") == "usage")
    cached = sum(e.get("cached") or 0 for e in events if e.get("type") == "usage")
    completion = sum(e.get("completion") or 0 for e in events if e.get("type") == "usage")
    direct = [e.get("name") for e in events if e.get("type") == "tool_call"]
    # What a program called: its result's "calls" ("docs_lookup ×6, docs_search").
    in_code = 0
    for e in events:
        if e.get("type") == "tool_result" and e.get("name") == "run_code":
            try:
                said = json.loads(e.get("text") or "{}").get("calls") or ""
            except (ValueError, AttributeError):
                said = ""
            for part in filter(None, (p.strip() for p in said.split(","))):
                count = re.search(r"×(\d+)$", part)
                in_code += int(count.group(1)) if count else 1
    last = max((i for i, e in enumerate(events) if e.get("type") == "tool_result"), default=-1)
    answer = "".join(e.get("text") or "" for e in events[last + 1:] if e.get("type") == "content")
    error = next((e.get("error") or e.get("text") for e in events if e.get("type") == "error"), None)
    person.call("DELETE", f"/api/chat/conversations/{chat['id']}")
    return {"answer": answer, "seconds": seconds, "prompt": prompt_tokens, "cached": cached, "completion": completion,
            "direct_calls": len(direct), "calls": direct, "code_calls": in_code, "error": error}


def report(rows: list[dict]) -> None:
    print(f"\n  {'model':28} {'mode':7} {'family':7} {'pass':>6} {'prompt':>9} {'compl.':>7} {'s':>7} {'calls':>6} {'in code':>8}")
    keys = sorted({(r["model"], r["mode"], r["family"]) for r in rows})
    for model, mode, family in keys:
        sub = [r for r in rows if (r["model"], r["mode"], r["family"]) == (model, mode, family) and r["verdict"] != "ERROR"]
        ok = sum(r["verdict"] == "PASS" for r in sub)
        print(f"  {model[:28]:28} {mode:7} {family:7} {ok:>3}/{len(sub):<2} {sum(r['prompt'] for r in sub):>9} {sum(r['completion'] for r in sub):>7} "
              f"{sum(r['seconds'] for r in sub):>7.0f} {sum(r['direct_calls'] for r in sub):>6} {sum(r['code_calls'] for r in sub):>8}")
    errors = [r for r in rows if r["verdict"] == "ERROR"]
    if errors:
        print(f"\n  not graded, the run failed ({len(errors)}):")
        for r in errors:
            print(f"    {r['model']} {r['mode']} {r['task']}: {r.get('error')}")


def main() -> int:
    ap = argparse.ArgumentParser(description="The same tasks with tools direct and as code, through Arena's chat.")
    ap.add_argument("--models", required=True, help="comma-separated chat models, as Admin -> Models names them")
    ap.add_argument("--modes", default="direct,code")
    ap.add_argument("--families", default="single,many")
    ap.add_argument("--tasks", default=None, help="only these task ids (comma-separated)")
    ap.add_argument("--url", default=None, help="Arena's address (default: https://DOMAIN from deploy/.env)")
    ap.add_argument("--verify-tls", action="store_true", help="check the certificate (a public one; the local stack's own is not)")
    ap.add_argument("--out", default=str(OUT))
    a = ap.parse_args()

    env = {}
    dotenv = ROOT / "deploy" / ".env"
    if dotenv.exists():
        env = dict(line.split("=", 1) for line in dotenv.read_text().splitlines() if "=" in line and not line.startswith("#"))
    password = os.environ.get("ARENA_ADMIN_PASSWORD") or env.get("ADMIN_PASSWORD")
    if not password:
        print("No admin password: set ARENA_ADMIN_PASSWORD.", file=sys.stderr)
        return 2
    arena = Arena(a.url or f"https://{env.get('DOMAIN', 'localhost')}", a.verify_tls)
    status, text, _ = arena.call("POST", "/api/auth/login", {"userName": env.get("ADMIN_USERNAME", "admin"), "password": password})
    if status != 200:
        print(f"Admin sign-in refused: {status}", file=sys.stderr)
        return 2
    models = [m.strip() for m in a.models.split(",") if m.strip()]
    modes = [m.strip() for m in a.modes.split(",") if m.strip()]
    listed = {m["name"]: m for m in arena.json("GET", "/api/admin/models")["models"]}
    unknown = [m for m in models if m not in listed or not listed[m].get("toolMode")]
    if unknown:
        print(f"Not chat models here: {', '.join(unknown)}", file=sys.stderr)
        return 2
    before = {m: listed[m]["toolMode"] for m in models}

    name = "eval" + uuid.uuid4().hex[:8]
    made = arena.json("POST", "/api/admin/people", {"userName": name, "email": f"{name}@example.test"})
    rows: list[dict] = []
    try:
        person = Arena(arena.base, a.verify_tls)
        status, text, _ = person.call("POST", "/api/auth/login", {"userName": name, "password": made["password"]})
        if status != 200:
            raise RuntimeError(f"the throwaway person's sign-in was refused: {status} {text[:200]}")
        # Its notice, as any new person accepts it before the chat (Compliance), while one is on.
        notice = person.json("GET", "/api/account/notice")
        if notice.get("on"):
            person.json("POST", "/api/account/notice/accept", {"version": notice.get("version")})
        mcp = Mcp(arena, made["apiKey"])
        single, many, dropped = answer_key(mcp)
        for d in dropped:
            print(f"  answer key: {d}")
        families = {"single": single, "many": many}
        only = {t.strip() for t in a.tasks.split(",")} if a.tasks else None
        tasks = [(f, t) for f in a.families.split(",") for t in families.get(f.strip(), []) if only is None or t["id"] in only]
        for model in models:
            # The first answer may wait for the model to load: not measured.
            ask(arena, person, model, "Say ready.")
            for mode in modes:
                arena.json("PUT", f"/api/admin/models/{model}/tools", {"mode": mode})
                for family, task in tasks:
                    took = ask(arena, person, model, task["prompt"])
                    verdict, missing = ("ERROR", []) if took.get("error") and not took.get("answer") else grade(task, took.get("answer", ""))
                    rows.append({"model": model, "mode": mode, "family": family, "task": task["id"], "verdict": verdict, "missing": missing,
                                 **{k: took.get(k, 0) for k in ("seconds", "prompt", "cached", "completion", "direct_calls", "code_calls")},
                                 "calls": took.get("calls", []), "error": took.get("error"), "answer": (took.get("answer") or "")[:2000]})
                    print(f"  {model} {mode:6} {task['id']:22} {verdict:5} {took.get('seconds', 0):>6}s "
                          f"tokens {took.get('prompt', 0)}+{took.get('completion', 0)} calls {took.get('direct_calls', 0)}+{took.get('code_calls', 0)}", flush=True)
        pathlib.Path(a.out).write_text(json.dumps({"dropped": dropped, "rows": rows}, indent=1) + "\n")
        report(rows)
    finally:
        for model, was in before.items():
            arena.call("PUT", f"/api/admin/models/{model}/tools", {"mode": was["mode"] if was["set"] else None})
        arena.call("DELETE", f"/api/admin/people/{made['id']}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
