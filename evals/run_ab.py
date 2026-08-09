"""Run the pack-derived question set, closed book versus with retrieval.

Ground truth comes from the pack pages (see gen_questions.py), so the expected
answer is what the upstream project publishes rather than what I remember.

Grading reads only the final `VERDICT:` line. Everything earlier is reasoning
or hedging, and matching against the whole reply is how an earlier run scored a
false 10/10 -- with a long answer, the right word appears somewhere whether or
not it is the conclusion.

    python run_big.py <packs_dir> <questions.json>
"""
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
    # IRQL answers are written many ways: "PASSIVE_LEVEL", "<= APC_LEVEL",
    # "APC_LEVEL or below". Compare the level token alone.
    m = re.search(r"([a-z]+_level)", want)
    return bool(m and m.group(1) in said)


async def context_for(packs_dir: str, question: str) -> str:
    parts = []
    for name in dict.fromkeys(NAME_RE.findall(question))[:4] if False else \
            list(dict.fromkeys(NAME_RE.findall(question)))[:4]:
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


async def main() -> None:
    packs_dir, qpath = sys.argv[1], sys.argv[2]
    questions = json.loads(open(qpath, encoding="utf-8").read())
    await tools.docs_search_impl(packs_dir, "warmup", limit=1)

    tally = defaultdict(lambda: [0, 0, 0])     # pack -> [plain, packs, n]
    kinds = defaultdict(lambda: [0, 0, 0])
    fixed = broke = 0
    for i, q in enumerate(questions, 1):
        asked = q["question"] + FORMAT.format(shape=q["shape"])
        p_ok = correct(ask(asked), q["answer"])
        ctx = await context_for(packs_dir, q["question"])
        g_ok = correct(ask(GROUNDED.format(ctx=ctx, q=asked)), q["answer"])
        for d, key in ((tally, q["pack"]), (kinds, q["kind"])):
            d[key][0] += p_ok; d[key][1] += g_ok; d[key][2] += 1
        fixed += g_ok and not p_ok
        broke += p_ok and not g_ok
        if i % 20 == 0:
            print(f"  ... {i}/{len(questions)}", flush=True)

    print(f"\n{'pack':12}{'closed book':>14}{'with packs':>14}")
    tp = tg = tn = 0
    for pack, (p, g, n) in sorted(tally.items()):
        print(f"{pack:12}{f'{p}/{n}':>14}{f'{g}/{n}':>14}")
        tp += p; tg += g; tn += n
    print(f"{'TOTAL':12}{f'{tp}/{tn}':>14}{f'{tg}/{tn}':>14}")
    print(f"\n{'question kind':12}{'closed book':>14}{'with packs':>14}")
    for kind, (p, g, n) in sorted(kinds.items()):
        print(f"{kind:12}{f'{p}/{n}':>14}{f'{g}/{n}':>14}")
    print(f"\n  packs fixed {fixed}   packs broke {broke}")


asyncio.run(main())
