"""Five ways to use the packs, measured on one question set and one model.

    A  closed book       the model alone
    B  retrieval-first   pack context in the prompt, then answer
    C  verify-after      answer, then correct only what docs contradict
    D  hybrid            verify if the model committed, retrieve if it did not
    E  double-tap        retrieval-first, then verify that answer too

D and E exist because A/B/C measured a clean split rather than a winner:

* retrieval-first fixed 51 and BROKE 3, because retrieved text displaces
  knowledge the model already had;
* verify-after broke 0 but fixed only 14, because it can contradict a stated
  claim and can do nothing at all when the model answered "unknown" -- on the
  scripting pack the model does not commit, and verify-after scored exactly
  its closed-book result.

The two failure modes are different. Ignorance needs retrieval; error needs
verification. D routes on which one the draft exhibits. E applies both
unconditionally and pays two model calls for it.

    ARGUS_OLLAMA_URL=http://localhost:11435 \\
        python evals/run_hybrid.py <packs_dir> <questions.json>
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

#: A draft that says any of these is not asserting a fact, so there is nothing
#: for verification to check and the model needs retrieval instead.
HEDGES = ("unknown", "not sure", "unsure", "cannot determine", "i don't know",
          "i do not know", "n/a", "none", "unclear", "depends")

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


def committed(reply: str) -> bool:
    """Did the draft assert something a reference could contradict?"""
    v = verdict(reply)
    return bool(v) and not any(h in v for h in HEDGES)


async def retrieval_context(packs_dir: str, question: str) -> str:
    parts = []
    # Behaviour-first: when the question describes what something does rather
    # than naming it, docs_lookup cannot fire at all. Measured on the 30
    # scripting reverse-lookup questions, searching symbol descriptions
    # answers 29 where page search reached 18.
    for hit in (await tools.docs_find_impl(packs_dir, question, limit=3)):
        parts.append(f"[{hit.get('source')}] {hit.get('name')} -- "
                     f"{hit.get('signature')}")
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


async def verified(packs_dir: str, question: str, draft: str,
                   asked: str) -> tuple[str, bool]:
    findings = await tools.docs_verify_impl(packs_dir, f"{question}\n{draft}")
    lines = [f"- {f['name']}: documented {c['field']} is {c['documented']}"
             for f in findings for c in f["corrections"]]
    if not lines:
        return draft, False
    return ask(REVISE.format(draft=draft.strip(),
                             corrections="\n".join(lines), q=asked)), True


async def main() -> None:
    packs_dir, qpath = sys.argv[1], sys.argv[2]
    questions = json.loads(open(qpath, encoding="utf-8").read())
    await tools.docs_search_impl(packs_dir, "warmup", limit=1)

    tally = defaultdict(lambda: [0] * 6)     # pack -> [A,B,C,D,E,n]
    routed_verify = routed_retrieve = 0
    e_rescued = e_broke = 0

    for i, q in enumerate(questions, 1):
        asked = q["question"] + FORMAT.format(shape=q["shape"])

        draft = ask(asked)
        a_ok = correct(draft, q["answer"])

        ctx = await retrieval_context(packs_dir, q["question"])
        b_reply = ask(GROUNDED.format(ctx=ctx, q=asked))
        b_ok = correct(b_reply, q["answer"])

        c_reply, _ = await verified(packs_dir, q["question"], draft, asked)
        c_ok = correct(c_reply, q["answer"])

        # D: route on whether the draft asserted anything.
        if committed(draft):
            routed_verify += 1
            d_ok = c_ok
        else:
            routed_retrieve += 1
            d_ok = b_ok

        # E: retrieval-first, then verify THAT answer.
        e_reply, _ = await verified(packs_dir, q["question"], b_reply, asked)
        e_ok = correct(e_reply, q["answer"])
        e_rescued += e_ok and not b_ok
        e_broke += b_ok and not e_ok

        row = tally[q["pack"]]
        for j, ok in enumerate((a_ok, b_ok, c_ok, d_ok, e_ok)):
            row[j] += ok
        row[5] += 1
        if i % 20 == 0:
            print(f"  ... {i}/{len(questions)}", flush=True)

    names = ["A closed", "B retr-1st", "C verify", "D hybrid", "E double"]
    print(f"\n{'pack':12}" + "".join(f"{n:>12}" for n in names))
    totals = [0] * 5
    n_all = 0
    for pack, row in sorted(tally.items()):
        print(f"{pack:12}" + "".join(f"{f'{row[j]}/{row[5]}':>12}" for j in range(5)))
        for j in range(5):
            totals[j] += row[j]
        n_all += row[5]
    print(f"{'TOTAL':12}" + "".join(f"{f'{totals[j]}/{n_all}':>12}" for j in range(5)))
    print(f"\n  D routed to verify {routed_verify}, to retrieval {routed_retrieve}")
    print(f"  E rescued {e_rescued} that retrieval-first got wrong, "
          f"broke {e_broke}")


asyncio.run(main())
