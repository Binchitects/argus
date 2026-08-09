"""The real agentic loop: native function calling, the model drives.

The previous harness used a plain-text action protocol ("CALL tool arg") and
measured 10/20 with the model calling a tool in 6 of 20 tasks and never
verifying. Adding an instruction to check first made it *worse* -- 4/20 --
because the extra prose crowded out the output format rather than changing the
policy.

That was a protocol failure, not a model failure. `/api/chat` with a `tools`
schema is the interface the model was actually trained for: it emits a
structured `tool_calls` entry, there is no format to break, and instructions
about *when* to call something no longer compete with instructions about *how*
to spell it.

Everything the model sees here is the real thing -- these are the same five
tool descriptions the MCP server registers, so a result carries over to
Hermes rather than describing a harness.

    ARGUS_OLLAMA_URL=http://localhost:11435 \\
        python evals/run_agent_native.py <packs_dir> <questions.json> [n] [mode]

`mode` is `free` (the model decides) or `nudged` (a system message saying its
recollection of headers and IRQLs is unreliable). Both are measured because
the text-protocol run could not separate "will not call tools" from "cannot
emit the format".
"""
from __future__ import annotations

import asyncio
import json
import random
import re
import sys
import urllib.request
from collections import Counter

from argus.mcpsrv import tools as argus_tools

CHAT_URL = "http://localhost:11435/api/chat"
MODEL = "qwen3.6:35b"
MAX_TURNS = 6

# The same descriptions the MCP server registers, trimmed to what a schema
# carries. If these do not steer the model, the ones in production will not
# either.
SCHEMA = [
    {"type": "function", "function": {
        "name": "docs_lookup",
        "description": ("Look up an EXACT API, cmdlet or command name in "
                        "official documentation. Returns its documented "
                        "header, import library, DLL and IRQL. Use whenever "
                        "the question names something specific."),
        "parameters": {"type": "object", "properties": {
            "name": {"type": "string", "description": "exact name"}},
            "required": ["name"]}}},
    {"type": "function", "function": {
        "name": "docs_find",
        "description": ("Find an API or command by WHAT IT DOES, when its "
                        "name is unknown. Searches one-line descriptions."),
        "parameters": {"type": "object", "properties": {
            "description": {"type": "string"}}, "required": ["description"]}}},
    {"type": "function", "function": {
        "name": "docs_search",
        "description": "Find documentation pages about a topic.",
        "parameters": {"type": "object", "properties": {
            "query": {"type": "string"}}, "required": ["query"]}}},
    {"type": "function", "function": {
        "name": "docs_get",
        "description": ("Read one whole documentation page, using a doc_path "
                        "returned by an earlier call. Use when the answer is "
                        "a detail on a long reference page."),
        "parameters": {"type": "object", "properties": {
            "doc_path": {"type": "string"}}, "required": ["doc_path"]}}},
    {"type": "function", "function": {
        "name": "docs_verify",
        "description": ("Check a draft answer against the documentation. "
                        "Returns only the points it contradicts."),
        "parameters": {"type": "object", "properties": {
            "text": {"type": "string"}}, "required": ["text"]}}},
]

NUDGE = ("You are answering questions about Windows and C/C++ APIs. Your "
         "recollection of exact header names, import libraries and IRQLs is "
         "unreliable even when it feels certain, and official documentation "
         "is available through the tools. Check before answering.")


def chat(messages: list[dict], with_tools: bool = True) -> dict:
    body = {"model": MODEL, "messages": messages, "stream": False,
            "think": False, "options": {"temperature": 0, "num_predict": 300}}
    if with_tools:
        body["tools"] = SCHEMA
    req = urllib.request.Request(CHAT_URL, data=json.dumps(body).encode(),
                                 headers={"Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=900) as resp:
            return json.load(resp).get("message", {}) or {}
    except Exception:
        return {}


def brief(rows, limit: int = 1200) -> str:
    if rows is None:
        return "(nothing found)"
    if isinstance(rows, dict):
        rows = [rows]
    if not rows:
        return "(nothing found)"
    out = []
    for r in rows[:4]:
        if not isinstance(r, dict):
            out.append(str(r)[:200]); continue
        bits = [f"{k}={r[k]}" for k in
                ("name", "signature", "title", "doc_path", "source") if r.get(k)]
        if r.get("corrections"):
            bits.append("corrections=" + "; ".join(
                f"{c['field']} is {c['documented']}" for c in r["corrections"]))
        if r.get("text"):
            bits.append("text=" + str(r["text"])[:400])
        out.append(" | ".join(bits))
    return "\n".join(out)[:limit]


async def dispatch(packs_dir: str, name: str, args: dict) -> str:
    try:
        if name == "docs_lookup":
            return brief(await argus_tools.docs_lookup_impl(
                packs_dir, str(args.get("name", ""))))
        if name == "docs_find":
            return brief(await argus_tools.docs_find_impl(
                packs_dir, str(args.get("description", "")), limit=4))
        if name == "docs_search":
            return brief(await argus_tools.docs_search_impl(
                packs_dir, str(args.get("query", "")), limit=3))
        if name == "docs_get":
            return brief(await argus_tools.docs_get_impl(
                packs_dir, str(args.get("doc_path", "")), max_chars=2500))
        if name == "docs_verify":
            return brief(await argus_tools.docs_verify_impl(
                packs_dir, str(args.get("text", ""))))
    except Exception as exc:
        return f"(tool error: {type(exc).__name__})"
    return f"(no such tool: {name})"


def correct(answer: str, expected: str) -> bool:
    said = re.sub(r"[^a-z0-9_.:<= /-]+", " ", (answer or "").lower())
    want = expected.lower().strip()
    if want in said:
        return True
    m = re.search(r"([a-z]+_level)", want)
    return bool(m and m.group(1) in said)


async def run_task(packs_dir: str, question: str,
                   nudged: bool) -> tuple[str, list[str], int]:
    messages: list[dict] = []
    if nudged:
        messages.append({"role": "system", "content": NUDGE})
    messages.append({"role": "user", "content":
                     question + "\nAnswer with just the exact value."})

    used: list[str] = []
    for turn in range(MAX_TURNS):
        msg = chat(messages)
        calls = msg.get("tool_calls") or []
        if not calls:
            return (msg.get("content") or "").strip(), used, turn + 1
        messages.append({"role": "assistant", "content": msg.get("content", ""),
                         "tool_calls": calls})
        for call in calls:
            fn = call.get("function", {}) or {}
            name = fn.get("name", "")
            args = fn.get("arguments") or {}
            if isinstance(args, str):
                try:
                    args = json.loads(args)
                except Exception:
                    args = {}
            used.append(name)
            result = await dispatch(packs_dir, name, args)
            messages.append({"role": "tool", "content": result})
    # Out of turns: ask once more without tools so it must commit.
    messages.append({"role": "user", "content":
                     "Give your final answer now, just the exact value."})
    return (chat(messages, with_tools=False).get("content") or "").strip(), used, MAX_TURNS


async def main() -> None:
    packs_dir, qpath = sys.argv[1], sys.argv[2]
    n = int(sys.argv[3]) if len(sys.argv) > 3 else 20
    mode = sys.argv[4] if len(sys.argv) > 4 else "free"

    questions = json.loads(open(qpath, encoding="utf-8").read())
    rng = random.Random(20260810)
    rng.shuffle(questions)
    questions = questions[:n]
    await argus_tools.docs_search_impl(packs_dir, "warmup", limit=1)

    ok = plain_ok = turns_total = used_any = 0
    tool_use: Counter[str] = Counter()

    for i, q in enumerate(questions, 1):
        bare = chat([{"role": "user", "content":
                      q["question"] + "\nAnswer with just the exact value."}],
                    with_tools=False)
        plain_ok += correct(bare.get("content", ""), q["answer"])

        answer, used, turns = await run_task(packs_dir, q["question"],
                                             mode == "nudged")
        ok += correct(answer, q["answer"])
        turns_total += turns
        used_any += bool(used)
        tool_use.update(used)
        if i % 5 == 0:
            print(f"  ... {i}/{len(questions)}", flush=True)

    n_q = len(questions)
    print(f"\n  mode                   : {mode}")
    print(f"  closed book (no tools) : {plain_ok}/{n_q}")
    print(f"  agent with tools       : {ok}/{n_q}")
    print(f"  tasks that called a tool: {used_any}/{n_q}")
    print(f"  average turns          : {turns_total / n_q:.1f}")
    print("  tool calls:", dict(tool_use) or "(none)")


asyncio.run(main())
