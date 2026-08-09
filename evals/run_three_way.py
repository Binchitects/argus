"""Three ways to use the packs, on the same questions and the same model.

    closed book      the model alone
    retrieval-first  pack context in the prompt, then the model answers
    verify-after     the model answers, then docs_verify checks the draft and
                     it revises only what the documentation contradicts

The third arm exists because the second was measured doing harm: pack context
in front of the model took win32 from 5/5 to 1/5, since retrieved text
displaces knowledge the model already had. Verify-after cannot do that -- it
has no mechanism for replacing an answer, only for contradicting a specific
stated claim.

The draft is verified together with the question. An answer like "winuser.h"
contains no API name on its own; the name is in what was asked, and an agent
checking its own work has both.

    ARGUS_OLLAMA_URL=http://localhost:11435 \\
        python evals/run_three_way.py <packs_dir> <questions.json>
"""
from __future__ import annotations

import asyncio
import json
import re
import sys
import urllib.request
from collections import defaultdict

from argus.mcpsrv import tools

GEN_URL = "http://localhost:11435/api/generate"
MODEL = "qwen3.6:35b"
NAME_RE = re.compile(r"\b(?:[A-Z][a-z0-9]+){2,}[A-Za-z0-9_]*\b"
                     r"|\b[A-Za-z]+_[A-Za-z0-9_]{3,}\b"
                     r"|\b[A-Z][a-z]+-[A-Z][a-z]+\b")
VERDICT_RE = re.compile(r"verdict\s*:\s*(.+)", re.IGNORECASE)

FORMAT = ("\n\nAnswer with ONE final line, exactly:\nVERDICT: <answer>\n"
          "The answer must be {shape}. If you do not know, write "
          "VERDICT: unknown")
GROUNDED = ("Reference material follows. Use it if it answers the question; "
            "otherwise rely on what you know.\n\n=== REFERENCE ===\n{ctx}\n"
            "=== END REFERENCE ===\n\n{q}")
REVISE = ("Your draft answer was:\n{draft}\n\nThe official documentation "
          "contradicts it on these points:\n{corrections}\n\nGive the "
          "corrected answer to the original question.\n\n{q}")


def ask(prompt: str) -> str:
    body = json.dumps({
        "model": MODEL, "prompt": prompt, "stream": False, "think": False,
        "options": {"temperature": 0, "num_predict": 120},
    }).encode()
    req = urllib.request.Request(GEN_URL, data=body,
                                 headers={"Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=600) as resp:
            return json.load(resp).get("response", "")
    except Exception:
        return ""


def correct(reply: str, answer: str) -> bool:
    found = VERDICT_RE.findall(reply or "")
    if not found:
        return False
    said = re.sub(r"[^a-z0-9_.:<= -]+", " ", found[-1].lower())
    want = answer.lower().strip()
    if want in said:
        return True
    m = re.search(r"([a-z]+_level)", want)
    return bool(m and m.group(1) in said)


async def retrieval_context(packs_dir: str, question: str) -> str:
    parts = []
    for name in list(dict.fromkeys(NAME_RE.findall(question)))[:4]:
        for hit in (await tools.docs_lookup_impl(packs_dir, name))[:2]:
            parts.append(f"[{hit.get('source')}] {hit.get('name')} -- "
                         f"{hit.get('signature')} ({hit.get('title')})")
    seen = set()
    for h in await tools.docs_search_impl(packs_dir, question, limit=3):
        key = (h.get("source"), h.get("doc_path"))
        if key in seen:
            continue
        seen.add(key)
        if len(seen) <= 2:
            full = await tools.docs_get_impl(packs_dir, str(h.get("doc_path")),
                                             source=str(h.get("source")),
                                             max_chars=8000)
            if full:
                parts.append(f"[{full.get('source')}] {full.get('title')}\n"
                             f"{full.get('text')}")
                continue
        parts.append(f"[{h.get('source')}] {h.get('title')}\n"
                     f"{str(h.get('text'))[:700]}")
    return "\n\n".join(parts) or "(nothing found)"


async def verify_after(packs_dir: str, question: str, draft: str,
                       asked: str) -> tuple[str, bool]:
    """Return the possibly-revised answer, and whether anything was corrected."""
    findings = await tools.docs_verify_impl(packs_dir, f"{question}\n{draft}")
    lines = [f"- {f['name']}: documented {c['field']} is {c['documented']}"
             for f in findings for c in f["corrections"]]
    if not lines:
        return draft, False
    revised = ask(REVISE.format(draft=draft.strip(),
                                corrections="\n".join(lines), q=asked))
    return revised, True


async def main() -> None:
    packs_dir, qpath = sys.argv[1], sys.argv[2]
    questions = json.loads(open(qpath, encoding="utf-8").read())
    await tools.docs_search_impl(packs_dir, "warmup", limit=1)

    tally = defaultdict(lambda: [0, 0, 0, 0])   # pack -> [closed, first, after, n]
    revised_total = revised_helped = revised_hurt = 0

    for i, q in enumerate(questions, 1):
        asked = q["question"] + FORMAT.format(shape=q["shape"])

        draft = ask(asked)
        closed_ok = correct(draft, q["answer"])

        ctx = await retrieval_context(packs_dir, q["question"])
        first_ok = correct(ask(GROUNDED.format(ctx=ctx, q=asked)), q["answer"])

        final, changed = await verify_after(packs_dir, q["question"], draft, asked)
        after_ok = correct(final, q["answer"])
        if changed:
            revised_total += 1
            revised_helped += after_ok and not closed_ok
            revised_hurt += closed_ok and not after_ok

        row = tally[q["pack"]]
        row[0] += closed_ok; row[1] += first_ok; row[2] += after_ok; row[3] += 1
        if i % 20 == 0:
            print(f"  ... {i}/{len(questions)}", flush=True)

    print(f"\n{'pack':12}{'closed book':>13}{'retr-first':>13}{'verify-after':>14}")
    tc = tf = ta = tn = 0
    for pack, (c, f, a, n) in sorted(tally.items()):
        print(f"{pack:12}{f'{c}/{n}':>13}{f'{f}/{n}':>13}{f'{a}/{n}':>14}")
        tc += c; tf += f; ta += a; tn += n
    print(f"{'TOTAL':12}{f'{tc}/{tn}':>13}{f'{tf}/{tn}':>13}{f'{ta}/{tn}':>14}")
    print(f"\n  drafts revised by docs_verify: {revised_total}")
    print(f"    revision fixed a wrong answer : {revised_helped}")
    print(f"    revision broke a right answer : {revised_hurt}")


asyncio.run(main())
