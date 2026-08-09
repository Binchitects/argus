"""A real agent loop: the model chooses the tools, not the harness.

Every earlier harness hard-coded the sequence -- lookup, then search, then get,
then verify -- so it measured the tools while assuming the judgement that
selects them. An agent has to decide *which* tool, with what argument, and when
it has enough to answer. That is the part a deployment actually depends on and
the part nothing here has tested.

The loop is deliberately plain: the model emits one action per turn as a line,
the harness executes it and appends the result, and the model either calls
another tool or answers. No function-calling API, so this works with any model
and every decision is visible in the transcript.

What it measures beyond correctness:

* **tool choice** -- does it reach for docs_find when it knows only a
  behaviour, or fall back on docs_search out of habit?
* **unprompted verification** -- docs_verify is offered but never demanded.
  Whether a model uses it when nothing insists is the whole question behind
  building it.
* **steps** -- an agent that needs six calls to answer what one lookup would
  settle is expensive even when it is right.

    ARGUS_OLLAMA_URL=http://localhost:11435 \\
        python evals/run_agent_loop.py <packs_dir> <questions.json> [n]
"""
from __future__ import annotations

import asyncio
import json
import random
import re
import sys
import urllib.request
from collections import Counter

from argus.mcpsrv import tools

GEN_URL = "http://localhost:11435/api/generate"
MODEL = "qwen3.6:35b"
MAX_STEPS = 6

TOOLS = """Available tools, one call per turn:

docs_lookup(name)        exact API/cmdlet name -> its documented header,
                         library, DLL and IRQL. Use when you know the name.
docs_find(description)   find an API by WHAT IT DOES, when you do not know
                         its name.
docs_search(query)       find documentation pages about a topic.
docs_get(doc_path)       read one whole page, using a doc_path a previous
                         call returned.
docs_verify(text)        check a draft answer against the documentation;
                         returns only what it contradicts.
"""

SYSTEM = """You are answering a technical question. You may call tools to
check facts against official documentation.

Reply with EXACTLY ONE line, either:
  CALL <tool> <argument>
or
  ANSWER <your final answer>

""" + TOOLS + """
You are answering about APIs whose exact headers, libraries and IRQLs are
easy to misremember. You MUST check at least one fact with a tool before
answering -- your recollection is not sufficient evidence, however
confident it feels. Prefer docs_lookup when the question names an API,
docs_find when it only describes behaviour.

Keep the final answer to the exact value asked for.
"""

CALL_RE = re.compile(r"^\s*CALL\s+(\w+)\s+(.+)$", re.IGNORECASE | re.MULTILINE)
ANSWER_RE = re.compile(r"^\s*ANSWER\s+(.+)$", re.IGNORECASE | re.MULTILINE)


def ask(prompt: str, budget: int = 200) -> str:
    body = json.dumps({
        "model": MODEL, "prompt": prompt, "stream": False, "think": False,
        "options": {"temperature": 0, "num_predict": budget},
    }).encode()
    req = urllib.request.Request(GEN_URL, data=body,
                                 headers={"Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=600) as resp:
            return json.load(resp).get("response", "")
    except Exception:
        return ""


def brief(rows, limit: int = 700) -> str:
    """Tool output the model can read without drowning in it."""
    if rows is None:
        return "(nothing found)"
    if isinstance(rows, dict):
        rows = [rows]
    if not rows:
        return "(nothing found)"
    out = []
    for r in rows[:4]:
        if not isinstance(r, dict):
            out.append(str(r)[:200])
            continue
        bits = [f"{k}={r[k]}" for k in
                ("name", "signature", "title", "doc_path", "source")
                if r.get(k)]
        if r.get("corrections"):
            bits.append("corrections=" + "; ".join(
                f"{c['field']} is {c['documented']}" for c in r["corrections"]))
        if r.get("text"):
            bits.append("text=" + str(r["text"])[:300])
        out.append(" | ".join(bits))
    return "\n".join(out)[:limit * 4]


async def call_tool(packs_dir: str, name: str, arg: str) -> str:
    name = name.lower()
    try:
        if name == "docs_lookup":
            return brief(await tools.docs_lookup_impl(packs_dir, arg.strip()))
        if name == "docs_find":
            return brief(await tools.docs_find_impl(packs_dir, arg, limit=4))
        if name == "docs_search":
            return brief(await tools.docs_search_impl(packs_dir, arg, limit=3))
        if name == "docs_get":
            return brief(await tools.docs_get_impl(packs_dir, arg.strip(),
                                                   max_chars=2500))
        if name == "docs_verify":
            return brief(await tools.docs_verify_impl(packs_dir, arg))
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


async def run_task(packs_dir: str, question: str) -> tuple[str, list[str], int]:
    history = ""
    used: list[str] = []
    for step in range(MAX_STEPS):
        reply = ask(f"{SYSTEM}\nQuestion: {question}\n{history}\nYour move:\n")
        answered = ANSWER_RE.search(reply)
        called = CALL_RE.search(reply)
        # A model that emits both usually called a tool and guessed the result;
        # honour the call so the guess is checked rather than trusted.
        if called and (not answered or called.start() < answered.start()):
            tool, arg = called.group(1), called.group(2).strip().strip('"')
            used.append(tool.lower())
            result = await call_tool(packs_dir, tool, arg)
            history += f"\nYou called {tool}({arg[:60]}) and got:\n{result}\n"
            continue
        if answered:
            return answered.group(1).strip(), used, step + 1
        # Neither -- treat the whole reply as the answer rather than looping.
        return reply.strip()[:200], used, step + 1
    return "(no answer within step limit)", used, MAX_STEPS


async def main() -> None:
    packs_dir, qpath = sys.argv[1], sys.argv[2]
    n = int(sys.argv[3]) if len(sys.argv) > 3 else 20
    questions = json.loads(open(qpath, encoding="utf-8").read())
    rng = random.Random(20260810)
    rng.shuffle(questions)
    questions = questions[:n]

    await tools.docs_search_impl(packs_dir, "warmup", limit=1)

    agent_ok = plain_ok = 0
    steps_total = 0
    tool_use: Counter[str] = Counter()
    verified_any = 0

    for i, q in enumerate(questions, 1):
        plain = ask(f"Answer with just the value.\nQuestion: {q['question']}\n")
        plain_ok += correct(plain, q["answer"])

        answer, used, steps = await run_task(packs_dir, q["question"])
        agent_ok += correct(answer, q["answer"])
        steps_total += steps
        tool_use.update(used)
        verified_any += "docs_verify" in used
        if i % 5 == 0:
            print(f"  ... {i}/{len(questions)}", flush=True)

    n_q = len(questions)
    print(f"\n  closed book (no tools) : {plain_ok}/{n_q}")
    print(f"  agent choosing tools   : {agent_ok}/{n_q}")
    print(f"\n  average steps per task : {steps_total / n_q:.1f}")
    print(f"  tasks where it verified: {verified_any}/{n_q}")
    print("  tool calls:", dict(tool_use) or "(none)")


asyncio.run(main())
