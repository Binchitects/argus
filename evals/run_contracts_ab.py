"""Do the contracts tools stop a model inventing API facts?

The failure they were built for, measured: asked to review a real minifilter,
qwen3.6:35b produced seven findings, every one resting on one remembered claim
that ExAllocateFromLookasideListEx requires PASSIVE_LEVEL. It is documented
`<= DISPATCH_LEVEL`.

Three arms, same model, same file, same temperature:

    A  closed book        no tools at all
    B  tools offered      docs_lookup/find/search/get/verify + contracts,
                          native function calling, model chooses
    C  contracts injected the contract sheet is in the prompt already

B and C are separated deliberately, because every earlier round turned on the
difference. A tool the model does not call is worth nothing, and on the review
task it called nothing at all -- so "offered" and "used" have to be measured
apart or a null result is unreadable.

Grading is not prose judgement. Every finding that asserts an IRQL, header or
library is extracted and checked against the packs, so what is counted is
objective: how many asserted facts are WRONG.

    OLLAMA_URL=http://localhost:11434 \\
        python evals/run_contracts_ab.py <source_file>
"""
from __future__ import annotations

import asyncio
import json
import os
import re
import sys

import urllib.request

from argus.mcpsrv import tools
from argus.store import packs as packs_store

OLLAMA_CHAT = os.environ.get(
    "OLLAMA_URL", "http://localhost:11434").rstrip("/") + "/api/chat"
MODEL = os.environ.get("OLLAMA_MODEL", "qwen3.6:27b")
PACKS = os.environ.get("ARGUS_PACKS", "deploy/test-gitlab/work/packs")
MAX_TURNS = 8
MAX_SOURCE = 12_000

REVIEW = """Review this Windows kernel-mode source file.

Report concrete defects only: wrong IRQL assumptions, wrong pool types, missing
release calls, and misuse of documented API contracts. For each finding name
the API and state its documented requirement explicitly.

```c
{source}
```
"""

#: A CONTRACT claim: the review asserts what IRQL an API requires.
#:
#: The first version accepted any API within 120 characters of an IRQL token,
#: and over-counted badly. Driver code is full of
#: `NT_ASSERT(KeGetCurrentIrql() == PASSIVE_LEVEL)`, so a review correctly
#: describing that assertion scored as a false claim about
#: KeGetCurrentIrql's own contract -- a statement about a CALL SITE, not about
#: a requirement. It accounted for at least 3 of one arm's 10 "errors".
#:
#: A contract verb between the two is what separates them: "must be called at",
#: "requires", "is callable at". A bare mention no longer counts.
CLAIM_RE = re.compile(
    r"\b((?:Flt|Io|Ke|Ex|Rtl|Ob|Ps|Mm|Zw|Nt|Fs)[A-Z][A-Za-z0-9]{3,})\b"
    r"[^.\n]{0,80}?"
    r"\b(?:must (?:be )?(?:only )?(?:be )?call\w*|requires?|required|callable|"
    r"can only be called|may only be called|is documented|documented as|"
    r"runs? at|executes? at|IRQL(?: requirement)?(?: is)?)\b"
    r"[^.\n]{0,60}?\b(PASSIVE_LEVEL|APC_LEVEL|DISPATCH_LEVEL)\b")

#: KeGetCurrentIrql READS the IRQL; a review naming it beside a level is
#: almost always describing an assertion in the code rather than claiming a
#: requirement of the routine. Its documented contract is "Any level", so any
#: level named near it scores wrong under a naive rule.
_NOT_A_CONTRACT_SUBJECT = frozenset({"KeGetCurrentIrql", "KeRaiseIrql",
                                     "KeLowerIrql"})

SCHEMAS = [
    {"type": "function", "function": {"name": "docs_lookup",
     "description": "Look up an exact API name; returns documented header, library, DLL and IRQL.",
     "parameters": {"type": "object", "properties": {"name": {"type": "string"}}, "required": ["name"]}}},
    {"type": "function", "function": {"name": "docs_contracts",
     "description": ("Paste a source file and get the documented header, library, DLL and "
                     "IRQL of every API it calls, in one call. Use FIRST on any code task."),
     "parameters": {"type": "object", "properties": {"source": {"type": "string"}}, "required": ["source"]}}},
    {"type": "function", "function": {"name": "docs_verify",
     "description": "Check a draft against the documentation; returns only what it contradicts.",
     "parameters": {"type": "object", "properties": {"text": {"type": "string"}}, "required": ["text"]}}},
]


def chat(messages, schemas):
    body = {"model": MODEL, "messages": messages, "stream": False,
            "think": False, "options": {"temperature": 0, "num_predict": 1400}}
    if schemas:
        body["tools"] = schemas
    req = urllib.request.Request(
        OLLAMA_CHAT, data=json.dumps(body).encode(),
        headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=1800) as resp:
        return json.load(resp).get("message", {}) or {}


async def dispatch(name: str, args: dict) -> str:
    if name == "docs_lookup":
        return json.dumps(await tools.docs_lookup_impl(PACKS, args.get("name", "")))[:2000]
    if name == "docs_contracts":
        return json.dumps(await tools.docs_contracts_impl(PACKS, args.get("source", "")))[:3000]
    if name == "docs_verify":
        return json.dumps(await tools.docs_verify_impl(PACKS, args.get("text", "")))[:2000]
    return f"(no such tool: {name})"


async def wrong_claims(review: str) -> tuple[int, int, list[str]]:
    """How many asserted IRQL facts the packs contradict."""
    opened = packs_store.open_packs(
        sorted(__import__("pathlib").Path(PACKS).glob("*.arguspack")))
    try:
        asserted = wrong = 0
        detail = []
        for api, level in CLAIM_RE.findall(review or ""):
            if api in _NOT_A_CONTRACT_SUBJECT:
                continue
            hits = packs_store.lookup_symbol(opened, api, limit=1)
            if not hits:
                continue
            documented = ""
            for part in str(hits[0].get("signature", "")).split(";"):
                if "IRQL" in part:
                    documented = part.split(":", 1)[-1].strip()
            if not documented or "level" not in documented.lower():
                continue
            asserted += 1
            if level.lower() not in documented.lower():
                wrong += 1
                detail.append(f"{api}: said {level}, documented {documented}")
        return asserted, wrong, detail
    finally:
        packs_store.close_packs(opened)


async def agent_review(prompt: str) -> tuple[str, list[str]]:
    calls: list[str] = []
    from argus.mcpsrv.server import SERVER_INSTRUCTIONS
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
            calls.append(str(fn.get("name")))
            print(f"    -> {fn.get('name')}", file=sys.stderr, flush=True)
            messages.append({"role": "tool",
                             "content": await dispatch(fn.get("name", ""), args)})
    messages.append({"role": "user", "content": "Give your final review now."})
    return chat(messages, None).get("content", ""), calls


async def main() -> None:
    path = sys.argv[1]
    source = open(path, encoding="utf-8", errors="replace").read()[:MAX_SOURCE]
    prompt = REVIEW.format(source=source)

    print("=== A: closed book ===", flush=True)
    a = chat([{"role": "user", "content": prompt}], None).get("content", "")

    print("=== B: tools offered, model chooses ===", flush=True)
    b, calls = await agent_review(prompt)

    print("=== C: contracts injected ===", flush=True)
    sheet = await tools.docs_contracts_impl(PACKS, source)
    lines = "\n".join(
        f"{r['name']}: {r['signature']}" for r in sheet)
    c = chat([{"role": "user",
               "content": f"Documented contracts for every API in this file:\n"
                          f"{lines}\n\n{prompt}"}], None).get("content", "")

    print("=== D: contracts injected + quote verbatim ===", flush=True)
    # The failure arm C exposed: the model paraphrases a documented contract
    # into a review sentence, and its prior ("initialisation routine ->
    # PASSIVE_LEVEL") competes with the fact and wins about half the time.
    # This forbids the paraphrase. Quoting is a copy; restating is a
    # completion, and only one of the two can be wrong.
    quote_rule = (
        "RULE: whenever you state an IRQL, header or library requirement, "
        "COPY the exact string from the contract list verbatim, in backticks, "
        "and name the API it belongs to. Never restate a requirement in your "
        "own words and never infer one from what the routine appears to do. "
        "If an API is not in the list, say its requirement is not documented "
        "here rather than supplying one.\n\n")
    d_text = chat([{"role": "user",
                    "content": f"Documented contracts for every API in this "
                               f"file:\n{lines}\n\n{quote_rule}{prompt}"}],
                  None).get("content", "")

    print("\n=== asserted IRQL facts that the packs CONTRADICT ===")
    for label, text in (("A closed book", a), ("B tools offered", b),
                        ("C contracts injected", c),
                        ("D contracts + quote", d_text)):
        asserted, wrong, detail = await wrong_claims(text)
        print(f"  {label:22} {wrong} wrong of {asserted} asserted")
        for d in detail[:3]:
            print(f"      {d}")
    print(f"\n  arm B tool calls: {calls or '(none)'}")


if __name__ == "__main__":
    # Guarded so the grader can be imported and unit-tested without the whole
    # run firing on import -- which is how a sanity check of CLAIM_RE turned
    # into an IndexError on sys.argv.
    asyncio.run(main())
