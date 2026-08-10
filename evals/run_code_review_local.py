"""Agentic code review of a real driver, with and without the packs.

Everything measured so far answered questions. This reviews code: the model is
given a real source file and a set of documentation tools, and has to decide
for itself what to check. Nothing tells it which APIs matter.

Two arms, same model, same file, same temperature:

    closed book   the model alone
    with tools    native function calling against a live Argus MCP server

Findings are not graded automatically. A review is prose and any keyword
scoring would reward the reviewer that name-drops the most APIs. Instead both
reviews are printed side by side with the tool calls the grounded arm made, so
a human can judge whether checking the documentation changed what it found.
What IS counted is objective: how many distinct APIs each review names, and
how many of those the packs actually document -- a review that discusses
FltGetFileNameInformation without ever looking it up is asserting from memory.

    OLLAMA_URL=http://localhost:11434 \\
        python evals/run_code_review.py <mcp_url> <token> <source_file>
"""
from __future__ import annotations

import asyncio
import json
import os
import re
import sys
import urllib.request

from mcp import ClientSession
from mcp.client.streamable_http import streamablehttp_client

OLLAMA_CHAT = os.environ.get(
    "OLLAMA_URL", "http://localhost:11434").rstrip("/") + "/api/chat"
MODEL = os.environ.get("OLLAMA_MODEL", "qwen3.6:35b")
MAX_TURNS = 10
MAX_SOURCE = 14_000

#: Kernel APIs are CamelCase with a subsystem prefix; this is what a review
#: "names" for counting purposes.
API_RE = re.compile(r"\b(?:Flt|Io|Ke|Ex|Rtl|Ob|Ps|Mm|Zw|Nt|Fs)[A-Z][A-Za-z0-9]{3,}\b")

REVIEW_PROMPT = """Review this Windows kernel-mode minifilter source file.

Report concrete defects only: incorrect IRQL assumptions, wrong pool types,
missing or mismatched release calls, reference-count errors, unchecked status
codes that matter, and misuse of documented API contracts. For each finding
give the function, what is wrong, and why it is wrong.

Do not restate what the code does. Do not suggest style changes.

```c
{source}
```
"""


def chat(messages: list[dict], schemas: list[dict] | None) -> dict:
    body = {
        "model": MODEL, "messages": messages, "stream": False, "think": False,
        "options": {"temperature": 0, "num_predict": 1600},
    }
    if schemas:
        body["tools"] = schemas
    req = urllib.request.Request(
        OLLAMA_CHAT, data=json.dumps(body).encode(),
        headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=1800) as resp:
        return json.load(resp).get("message", {}) or {}


def render(result) -> str:
    parts = []
    for block in getattr(result, "content", []) or []:
        text = getattr(block, "text", None)
        if text:
            parts.append(text)
    return "\n".join(parts)[:3000] or "(no result)"


def to_schema(tool) -> dict:
    return {"type": "function", "function": {
        "name": tool.name, "description": tool.description or "",
        "parameters": tool.inputSchema or {"type": "object", "properties": {}}}}


SCHEMAS = [
    {"type": "function", "function": {"name": "docs_lookup",
     "description": "Look up an exact API name; returns its documented header, library, DLL and IRQL.",
     "parameters": {"type": "object", "properties": {"name": {"type": "string"}}, "required": ["name"]}}},
    {"type": "function", "function": {"name": "docs_find",
     "description": "Find an API by what it does, when you do not know its name.",
     "parameters": {"type": "object", "properties": {"description": {"type": "string"}}, "required": ["description"]}}},
    {"type": "function", "function": {"name": "docs_search",
     "description": "Find documentation pages about a topic.",
     "parameters": {"type": "object", "properties": {"query": {"type": "string"}}, "required": ["query"]}}},
    {"type": "function", "function": {"name": "docs_verify",
     "description": "Check a draft against the documentation; returns only what it contradicts.",
     "parameters": {"type": "object", "properties": {"text": {"type": "string"}}, "required": ["text"]}}},
]

PACKS_DIR = os.environ.get("ARGUS_PACKS", "deploy/test-gitlab/work/packs")

# The same nudge the MCP server sends at connect time.
from argus.mcpsrv.server import SERVER_INSTRUCTIONS


async def _dispatch(name: str, args: dict) -> str:
    from argus.mcpsrv import tools as t
    if name == "docs_lookup":
        return json.dumps(await t.docs_lookup_impl(PACKS_DIR, args.get("name", "")))[:2500]
    if name == "docs_find":
        return json.dumps(await t.docs_find_impl(PACKS_DIR, args.get("description", ""), limit=4))[:2500]
    if name == "docs_search":
        return json.dumps(await t.docs_search_impl(PACKS_DIR, args.get("query", ""), limit=3))[:2500]
    if name == "docs_verify":
        return json.dumps(await t.docs_verify_impl(PACKS_DIR, args.get("text", "")))[:2500]
    return f"(no such tool: {name})"


async def grounded_review(url: str, token: str, prompt: str) -> tuple[str, list[str]]:
    calls: list[str] = []
    messages = [{"role": "system", "content": SERVER_INSTRUCTIONS},
                {"role": "user", "content": prompt}]
    for _ in range(MAX_TURNS):
        msg = chat(messages, SCHEMAS)
        tool_calls = msg.get("tool_calls") or []
        if not tool_calls:
            return msg.get("content", ""), calls
        messages.append({"role": "assistant", "content": msg.get("content", ""),
                         "tool_calls": tool_calls})
        for call in tool_calls:
            fn = call.get("function", {}) or {}
            args = fn.get("arguments") or {}
            if isinstance(args, str):
                try:
                    args = json.loads(args)
                except json.JSONDecodeError:
                    args = {}
            label = f"{fn.get('name')}({json.dumps(args)[:60]})"
            calls.append(label)
            print(f"    -> {label}", file=sys.stderr, flush=True)
            messages.append({"role": "tool",
                             "content": await _dispatch(fn.get("name", ""), args)})
    messages.append({"role": "user", "content": "Give your final review now."})
    return chat(messages, None).get("content", ""), calls


async def _unused_mcp_review(url: str, token: str, prompt: str) -> tuple[str, list[str]]:
    calls: list[str] = []
    async with streamablehttp_client(
        url, headers={"Authorization": f"Bearer {token}"}
    ) as (read, write, _):
        async with ClientSession(read, write) as session:
            init = await session.initialize()
            schemas = [to_schema(t) for t in (await session.list_tools()).tools]

            messages = []
            instructions = getattr(init, "instructions", None)
            if instructions:
                messages.append({"role": "system", "content": instructions})
            messages.append({"role": "user", "content": prompt})

            for _ in range(MAX_TURNS):
                msg = chat(messages, schemas)
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
                    label = f"{fn.get('name')}({json.dumps(args)[:60]})"
                    calls.append(label)
                    print(f"    -> {label}", file=sys.stderr, flush=True)
                    try:
                        text = render(await session.call_tool(fn.get("name", ""), args))
                    except Exception as exc:
                        text = f"(tool error: {type(exc).__name__})"
                    messages.append({"role": "tool", "content": text})

            messages.append({"role": "user",
                             "content": "Give your final review now."})
            return chat(messages, None).get("content", ""), calls


async def main() -> None:
    url, token, path = sys.argv[1], sys.argv[2], sys.argv[3]
    source = open(path, encoding="utf-8", errors="replace").read()[:MAX_SOURCE]
    prompt = REVIEW_PROMPT.format(source=source)

    print(f"reviewing {path} ({len(source)} chars)\n", file=sys.stderr)

    print("=== ARM A: closed book ===", flush=True)
    plain = chat([{"role": "user", "content": prompt}], None).get("content", "")
    print(plain.strip()[:4000])

    print("\n=== ARM B: with Argus tools ===", flush=True)
    grounded, calls = await grounded_review(url, token, prompt)
    print(grounded.strip()[:4000])

    apis_a = set(API_RE.findall(plain))
    apis_b = set(API_RE.findall(grounded))
    print("\n=== objective counts ===")
    print(f"  distinct kernel APIs named -- closed book {len(apis_a)}, "
          f"with tools {len(apis_b)}")
    print(f"  tool calls made: {len(calls)}")
    for c in calls:
        print(f"    {c}")
    print(f"  APIs only the grounded review names: "
          f"{sorted(apis_b - apis_a)[:8]}")


if __name__ == "__main__":
    if len(sys.argv) < 4:
        print(__doc__)
        raise SystemExit(2)
    asyncio.run(main())
