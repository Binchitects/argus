"""Two models, two arms, ten task families -- does Argus close the gap?

    A  closed book   the model alone, no tools at all
    B  with argus    the same model, the same question, native function
                     calling over the live MCP server, and the server's
                     instructions as the system message

Run for `qwen3.6:27b` (dense) and `qwen3.6:35b` (mixture-of-experts). The two
differ in architecture as well as size, so a gap between them is not a clean
parameter-count result and is not reported as one.

**The answer key is verified against the packs before any model runs.** Every
`expect` token must appear in the documentation Argus serves, or the bench
refuses to start. Without that check the grader encodes whatever the author
remembered, which is precisely the failure this project exists to measure --
an answer key written from memory would score a model wrong for being right.

Grading is substring matching on facts, not prose judgement. Each task names
tokens that a correct answer must contain: an import library, a documented
IRQL, a compiler flag. That is checkable, reproducible, and cannot be talked
into agreeing. `forbid` catches the specific confident-wrong answer a model
reaches for when it is working from memory.

    OLLAMA_URL=http://127.0.0.1:11434 ARGUS_URL=http://127.0.0.1:8099/mcp \\
        ARGUS_TOKEN=... python evals/run_model_bench.py
"""
from __future__ import annotations

import asyncio
import json
import os
import pathlib
import sys
import time
import urllib.request

MODELS = os.environ.get("BENCH_MODELS", "qwen3.6:27b,qwen3.6:35b").split(",")
OLLAMA = os.environ.get("OLLAMA_URL", "http://127.0.0.1:11434").rstrip("/")
ARGUS_URL = os.environ.get("ARGUS_URL", "http://127.0.0.1:8099/mcp")
ARGUS_TOKEN = os.environ.get("ARGUS_TOKEN", "argus-admin-token-0001")
PACKS = os.environ.get("ARGUS_PACKS", "packs")
OUT = pathlib.Path(os.environ.get("BENCH_OUT", "evals/model-bench-results.json"))

MAX_TURNS = 6
TIMEOUT = int(os.environ.get("BENCH_TIMEOUT", "1500"))

#: Each task: the question, the tokens a correct answer must contain, and the
#: specific wrong answer worth catching. `probe` is the symbol whose pack entry
#: must contain every `expect` token -- the self-check that keeps this key
#: honest. `probe: None` means the fact is not a symbol contract (a compiler
#: flag, a command-line option), and is checked by full-text search instead.
TASKS = [
    {
        "id": "test-development",
        "prompt": "I am writing a unit test for a driver helper that wraps "
                  "ExInitializeLookasideListEx. At what IRQL must my test "
                  "harness call it? Quote the documented requirement.",
        "expect": ["dispatch_level"],
        # No `forbid: passive_level` here, though the classic wrong answer IS
        # "PASSIVE_LEVEL". It cannot be detected by substring: the documented
        # contract is `<= DISPATCH_LEVEL`, which *includes* PASSIVE_LEVEL, so
        # a fully correct answer says both. Measured -- 27b answered "IRQL:
        # <= DISPATCH_LEVEL, so call it at DISPATCH_LEVEL or below (i.e.
        # PASSIVE_LEVEL or DISPATCH_LEVEL)" and the rule marked it MIXED.
        #
        # The wrong answer is PASSIVE_LEVEL *instead of* DISPATCH_LEVEL, which
        # the `expect` check already catches by the absence of dispatch_level.
        # A rule that fires on a correct answer is worse than no rule.
        "forbid": [],
        "probe": "ExInitializeLookasideListEx",
    },
    {
        "id": "code-review",
        "prompt": "Review this minifilter code:\n\n```c\nVOID OnDpc(PKDPC Dpc)"
                  "\n{\n    FltSendMessage(gFilter, &gPort, &msg, sizeof(msg),"
                  " NULL, NULL, NULL);\n}\n```\nA DPC runs at DISPATCH_LEVEL. "
                  "Is this call legal? Quote FltSendMessage's documented IRQL.",
        "expect": ["apc_level"],
        "forbid": [],
        "probe": "FltSendMessage",
    },
    {
        "id": "performance",
        "prompt": "A hot loop calls std::vector::push_back one million times "
                  "on an empty vector. What is push_back's documented "
                  "complexity, and which single call removes the repeated "
                  "reallocation cost?",
        "expect": ["amortized", "reserve"],
        "forbid": [],
        "probe": None,
    },
    {
        "id": "coding-style",
        "prompt": "Which MSVC compiler option selects the C++20 standard, and "
                  "which option enables warning level 4?",
        "expect": ["/std:c++20", "/w4"],
        "forbid": [],
        "probe": None,
    },
    {
        "id": "sdk-support",
        "prompt": "Which import library must I link to call "
                  "CryptAcquireContextW, and which header declares it?",
        "expect": ["advapi32.lib", "wincrypt.h"],
        "forbid": [],
        "probe": "CryptAcquireContextW",
    },
    {
        "id": "wdk-support",
        "prompt": "At what IRQL is IoCreateDevice callable, and which header "
                  "declares it?",
        "expect": ["apc_level", "wdm.h"],
        "forbid": [],
        "probe": "IoCreateDevice",
    },
    {
        "id": "win32-support",
        "prompt": "Which header declares CreateFileW, and which import library "
                  "must I link for it?",
        "expect": ["fileapi.h", "kernel32.lib"],
        "forbid": [],
        "probe": "CreateFileW",
    },
    {
        "id": "scripting",
        "prompt": "Which robocopy option mirrors a directory tree, including "
                  "deleting files at the destination that no longer exist at "
                  "the source?",
        "expect": ["/mir"],
        "forbid": [],
        "probe": None,
    },
    {
        "id": "security-review",
        "prompt": "Kernel code copies a user-supplied wide string into a fixed "
                  "buffer with wcscpy. Name the documented safe-string "
                  "replacement routine, and the header and library it needs.",
        "expect": ["ntstrsafe.h", "ntstrsafe.lib"],
        "forbid": [],
        "probe": "RtlStringCchCopyW",
    },
    {
        "id": "code-safety",
        "prompt": "Is ExAllocatePool2 safe to call from a DPC, which runs at "
                  "DISPATCH_LEVEL? Quote its documented IRQL requirement and "
                  "the header that declares it.",
        "expect": ["dispatch_level", "wdm.h"],
        "forbid": [],
        "probe": "ExAllocatePool2",
    },
]


def verify_answer_key() -> list[str]:
    """Check every expected token against the packs. Returns complaints.

    A symbol-contract fact is checked against that symbol's own entry. A fact
    with no owning symbol -- a compiler flag, a robocopy switch -- is checked
    with full-text search, which is weaker but still refuses a token the
    corpus has never seen.
    """
    from argus.store import packs as packs_store

    opened = packs_store.open_packs(
        sorted(pathlib.Path(PACKS).glob("*.arguspack")))
    problems: list[str] = []
    try:
        for task in TASKS:
            probe = task["probe"]
            if probe:
                hits = packs_store.lookup_symbol(opened, probe, limit=1)
                if not hits:
                    problems.append(f"{task['id']}: probe {probe!r} not in packs")
                    continue
                blob = json.dumps(hits[0]).lower()
                for token in task["expect"]:
                    if token.lower() not in blob:
                        problems.append(
                            f"{task['id']}: {token!r} absent from {probe}'s "
                            f"pack entry -- the answer key is wrong, not the model")
            else:
                for token in task["expect"]:
                    # Quoted as an FTS5 phrase: `/std:c++20` is punctuation
                    # that FTS5 reads as query syntax and rejects outright.
                    # Quoting makes it a phrase over the same tokens, which is
                    # what "does the corpus contain this flag" means anyway.
                    phrase = '"' + token.replace('"', "") + '"'
                    try:
                        found = packs_store.search_text(opened, phrase, limit=1)
                    except Exception as exc:
                        problems.append(
                            f"{task['id']}: could not verify {token!r}: {exc}")
                        continue
                    if not found:
                        problems.append(
                            f"{task['id']}: {token!r} found nowhere in the packs")
    finally:
        packs_store.close_packs(opened)
    return problems


def grade(task: dict, answer: str) -> tuple[str, list[str]]:
    low = (answer or "").lower()
    if not low.strip():
        return "EMPTY", []
    missing = [t for t in task["expect"] if t.lower() not in low]
    hit_forbidden = [t for t in task["forbid"] if t.lower() in low]
    if missing:
        return "FAIL", missing
    if hit_forbidden:
        # Contains the right answer AND the classic wrong one: recorded rather
        # than passed, because a reader cannot tell which the model meant.
        return "MIXED", hit_forbidden
    return "PASS", []


def chat(model: str, messages: list, tools: list | None) -> dict:
    body = {"model": model, "messages": messages, "stream": False,
            "think": False, "options": {"temperature": 0, "num_predict": 1200}}
    if tools:
        body["tools"] = tools
    req = urllib.request.Request(
        OLLAMA + "/api/chat", data=json.dumps(body).encode(),
        headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=TIMEOUT) as resp:
        return json.load(resp).get("message", {}) or {}


def closed_book(model: str, task: dict) -> tuple[str, int]:
    msg = chat(model, [{"role": "user", "content": task["prompt"]}], None)
    return msg.get("content", ""), 0


async def with_argus(model: str, task: dict) -> tuple[str, int]:
    """Native function calling against the live MCP server."""
    from mcp import ClientSession
    from mcp.client.streamable_http import streamablehttp_client

    async with streamablehttp_client(
            ARGUS_URL, headers={"Authorization": f"Bearer {ARGUS_TOKEN}"}
    ) as (read, write, _):
        async with ClientSession(read, write) as session:
            init = await session.initialize()
            listed = await session.list_tools()
            schemas = [{"type": "function", "function": {
                "name": t.name,
                "description": (t.description or "")[:900],
                "parameters": t.inputSchema,
            }} for t in listed.tools]

            messages = [
                {"role": "system", "content": init.instructions or ""},
                {"role": "user", "content": task["prompt"]},
            ]
            calls = 0
            for _ in range(MAX_TURNS):
                msg = chat(model, messages, schemas)
                tool_calls = msg.get("tool_calls") or []
                if not tool_calls:
                    return msg.get("content", ""), calls
                messages.append({"role": "assistant",
                                 "content": msg.get("content", ""),
                                 "tool_calls": tool_calls})
                for call in tool_calls:
                    fn = call.get("function", {}) or {}
                    args = fn.get("arguments") or {}
                    if isinstance(args, str):
                        try:
                            args = json.loads(args)
                        except json.JSONDecodeError:
                            args = {}
                    calls += 1
                    try:
                        result = await session.call_tool(fn.get("name", ""), args)
                        text = "".join(
                            getattr(c, "text", "") for c in (result.content or []))
                    except Exception as exc:
                        text = f"(tool error: {exc})"
                    messages.append({"role": "tool", "content": text[:4000]})
            messages.append({"role": "user", "content": "Answer now."})
            return chat(model, messages, None).get("content", ""), calls


async def main() -> None:
    problems = verify_answer_key()
    if problems:
        print("ANSWER KEY REJECTED -- refusing to grade models against it:")
        for p in problems:
            print("  " + p)
        sys.exit(1)
    print(f"answer key verified against the packs: "
          f"{sum(len(t['expect']) for t in TASKS)} tokens, all present\n")

    # Arms are selectable because the ACL cache backing the test scaffold has
    # a one-hour stale-grace window, and the full with-argus matrix can exceed
    # it. Running a batch that fits the window beats discovering that the last
    # few rows are 401s wearing a FAIL badge.
    wanted = os.environ.get("BENCH_ARMS", "closed-book,with-argus").split(",")
    existing = json.loads(OUT.read_text(encoding="utf-8")) if (
        OUT.exists() and os.environ.get("BENCH_APPEND")) else []
    rows = [r for r in existing
            if not (r["model"] in MODELS and r["arm"] in wanted)]

    for model in MODELS:
        for arm, runner in (("closed-book", None), ("with-argus", with_argus)):
            if arm not in wanted:
                continue
            for task in TASKS:
                started = time.time()
                try:
                    if runner is None:
                        answer, calls = closed_book(model, task)
                    else:
                        answer, calls = await runner(model, task)
                except Exception as exc:
                    answer, calls = f"<ERROR: {exc}>", 0
                verdict, detail = grade(task, answer)
                elapsed = round(time.time() - started, 1)
                # Stored generously: grading happens here on the FULL answer,
                # but a truncated record cannot be re-graded later without
                # silently turning a pass into a fail when the token sat past
                # the cap. Measured -- that flipped a real 35b PASS.
                rows.append({"model": model, "arm": arm, "task": task["id"],
                             "verdict": verdict, "missing": detail,
                             "tool_calls": calls, "seconds": elapsed,
                             "answer": (answer or "")[:8000]})
                print(f"  {model:14} {arm:12} {task['id']:18} {verdict:6} "
                      f"{elapsed:7.1f}s calls={calls}"
                      + (f"  missing={detail}" if detail else ""), flush=True)
                OUT.write_text(json.dumps(rows, indent=2), encoding="utf-8")

    print("\n##### AGGREGATE\n")
    print(f"  {'model':14} {'arm':12} {'pass':>5} {'of':>4}  {'tool calls':>10}")
    for model in MODELS:
        for arm in ("closed-book", "with-argus"):
            sub = [r for r in rows if r["model"] == model and r["arm"] == arm]
            ok = sum(1 for r in sub if r["verdict"] == "PASS")
            print(f"  {model:14} {arm:12} {ok:5} {len(sub):4}  "
                  f"{sum(r['tool_calls'] for r in sub):10}")
    print(f"\n  results: {OUT}")


if __name__ == "__main__":
    asyncio.run(main())
