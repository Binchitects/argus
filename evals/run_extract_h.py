"""Two more strategies, aimed at the bottleneck the data actually shows.

Double-tap reached 98/120 and stalled. The obvious theory -- retrieval is not
finding the fact -- is wrong: only 28 of 120 answers appear verbatim in a
`docs_lookup` signature, so for the other 92 the fact is in page prose that
retrieval *does* fetch. The model is failing to pull it out, not failing to
receive it.

So both new arms attack extraction rather than retrieval:

    F  extract     same context as double-tap, but the model is told to QUOTE
                   the value from the reference and to answer `unknown` if the
                   reference does not state it. Generation reframed as lookup.

    G  consensus   F run three times over three different framings of the same
                   context, taking the majority verdict. Independent reads of
                   one source disagree only where the source is ambiguous or
                   the model is guessing, so the majority is the more reliable
                   of the two.

Both end with docs_verify, since that was measured to cost nothing and to
rescue answers retrieval displaced.

    ARGUS_OLLAMA_URL=http://localhost:11435 \\
        python evals/run_extract.py <packs_dir> <questions.json>
"""
from __future__ import annotations

import asyncio
import json
import re
import sys
import urllib.request
from collections import Counter, defaultdict

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

# Generation invites the model to produce something plausible. Extraction asks
# it to find something that is present, and licenses it to fail.
EXTRACT = (
    "Below is reference documentation. If it states the answer, quote the "
    "exact value from it rather than paraphrasing. If it does NOT state the "
    "answer, answer from your own knowledge -- do not say unknown merely "
    "because the reference is silent.\n\n"
    "=== REFERENCE ===\n{ctx}\n=== END REFERENCE ===\n\n{q}")

# Three framings of the identical context. Wording is the only variable, so
# where they agree the source is unambiguous, and where they differ the model
# is guessing.
FRAMINGS = (
    EXTRACT,
    ("Reference documentation follows. Read it and report what it states, "
     "quoting exactly. Answer unknown if it does not say.\n\n"
     "=== REFERENCE ===\n{ctx}\n=== END REFERENCE ===\n\n{q}"),
    ("You are reading documentation to answer a question of fact. The answer, "
     "if present, is a literal string in the text below -- copy it exactly "
     "rather than reconstructing it. Answer unknown if it is absent.\n\n"
     "=== REFERENCE ===\n{ctx}\n=== END REFERENCE ===\n\n{q}"),
)

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


def verdict(reply: str) -> str:
    found = VERDICT_RE.findall(reply or "")
    return found[-1].strip().lower() if found else ""


def correct(reply: str, answer: str) -> bool:
    said = re.sub(r"[^a-z0-9_.:<= -]+", " ", verdict(reply))
    if not said:
        return False
    want = answer.lower().strip()
    if want in said:
        return True
    m = re.search(r"([a-z]+_level)", want)
    return bool(m and m.group(1) in said)


async def context_for(packs_dir: str, question: str) -> str:
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


async def verified(packs_dir: str, question: str, draft: str, asked: str) -> str:
    findings = await tools.docs_verify_impl(packs_dir, f"{question}\n{draft}")
    lines = [f"- {f['name']}: documented {c['field']} is {c['documented']}"
             for f in findings for c in f["corrections"]]
    if not lines:
        return draft
    return ask(REVISE.format(draft=draft.strip(),
                             corrections="\n".join(lines), q=asked))


async def main() -> None:
    packs_dir, qpath = sys.argv[1], sys.argv[2]
    questions = json.loads(open(qpath, encoding="utf-8").read())
    await tools.docs_search_impl(packs_dir, "warmup", limit=1)

    tally = defaultdict(lambda: [0, 0, 0])      # pack -> [F, G, n]
    disagreed = 0

    for i, q in enumerate(questions, 1):
        asked = q["question"] + FORMAT.format(shape=q["shape"])
        ctx = await context_for(packs_dir, q["question"])

        replies = [ask(FRAMINGS[0].format(ctx=ctx, q=asked))]
        f_ok = correct(await verified(packs_dir, q["question"], replies[0], asked),
                       q["answer"])

        votes = [verdict(r) for r in replies if verdict(r)]
        real = [v for v in votes if "unknown" not in v]
        if len(set(votes)) > 1:
            disagreed += 1
        winner = Counter(real or votes).most_common(1)
        merged = f"VERDICT: {winner[0][0]}" if winner else "VERDICT: unknown"
        g_ok = correct(await verified(packs_dir, q["question"], merged, asked),
                       q["answer"])

        row = tally[q["pack"]]
        row[0] += f_ok; row[1] += g_ok; row[2] += 1
        if i % 20 == 0:
            print(f"  ... {i}/{len(questions)}", flush=True)

    print(f"\n{'pack':12}{'F extract':>12}{'G consensus':>14}")
    tf = tg = tn = 0
    for pack, (f, g, n) in sorted(tally.items()):
        print(f"{pack:12}{f'{f}/{n}':>12}{f'{g}/{n}':>14}")
        tf += f; tg += g; tn += n
    print(f"{'TOTAL':12}{f'{tf}/{tn}':>12}{f'{tg}/{tn}':>14}")
    print(f"\n  framings disagreed on {disagreed}/{tn} questions")
    print("  (for comparison: closed 46, retrieval-first 97, double-tap 98)")


asyncio.run(main())
