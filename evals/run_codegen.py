"""Code generation and multi-claim analysis, closed book vs retrieval vs verify.

Everything measured so far has been single-fact recall, which grades cleanly
and is the shape `docs_verify` handles most easily. Real work is not that
shape: a code block asserts many facts at once -- this header, that library,
this IRQL, that free function -- and a single wrong `#include` makes the whole
thing not build.

Each task here names two or three APIs drawn from DIFFERENT headers, so a
correct answer must get several independent facts right. Ground truth comes
from the packs, as everywhere in evals/: the expected header and library are
whatever Microsoft publishes, not what the test author remembers.

Grading is per claim, not per task. A task worth four facts contributes four
to the denominator, so an answer that gets three of four right scores 0.75
rather than zero -- the interesting question is whether grounding moves the
per-fact accuracy, and a pass/fail on the whole block would hide that.

The obvious caveat, stated because the number invites the wrong reading: this
checks that the right header and library are NAMED, not that the code
compiles or is correct. It is a proxy, and a plausible-looking program that
names the right headers scores full marks.

    ARGUS_OLLAMA_URL=http://localhost:11435 \\
        python evals/run_codegen.py <packs_dir> [n_tasks]
"""
from __future__ import annotations

import asyncio
import json
import random
import re
import sqlite3
import sys
import urllib.request
from collections import defaultdict
from pathlib import Path

from argus.mcpsrv import tools

GEN_URL = "http://localhost:11435/api/generate"
MODEL = "qwen3.6:35b"
NAME_RE = re.compile(r"\b(?:[A-Z][a-z0-9]+){2,}[A-Za-z0-9_]*\b")
REQ = re.compile(r"(Header|Library):\s*([^;]+)")

BORING = {"windows.h", "winnt.h", "wtypes.h", "kernel32.lib"}

TASK = ("Write a short C function that uses {names}. Include the correct "
        "#include directives, and state in a comment which import libraries "
        "must be linked.")
GROUNDED = ("Reference documentation follows. Use it if it helps; otherwise "
            "rely on what you know.\n\n=== REFERENCE ===\n{ctx}\n"
            "=== END REFERENCE ===\n\n{q}")
REVISE = ("Your draft was:\n\n{draft}\n\nThe official documentation "
          "contradicts it on these points:\n{corrections}\n\nRewrite it "
          "correctly.\n\n{q}")


def ask(prompt: str, budget: int = 700) -> str:
    body = json.dumps({
        "model": MODEL, "prompt": prompt, "stream": False, "think": False,
        "options": {"temperature": 0, "num_predict": budget},
    }).encode()
    req = urllib.request.Request(GEN_URL, data=body,
                                 headers={"Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=900) as resp:
            return json.load(resp).get("response", "")
    except Exception:
        return ""


def build_tasks(packs_dir: Path, n: int, rng: random.Random) -> list[dict]:
    """Group APIs from different headers into multi-claim tasks."""
    by_header: dict[str, list[tuple[str, str, str]]] = defaultdict(list)
    for pack in ("win32", "wdk"):
        path = packs_dir / f"{pack}.arguspack"
        if not path.exists():
            continue
        conn = sqlite3.connect(f"file:{path}?mode=ro", uri=True)
        conn.row_factory = sqlite3.Row
        rows = conn.execute(
            "SELECT name, signature FROM api_symbols "
            "WHERE signature LIKE 'Header:%' AND signature LIKE '%Library:%' "
            "AND length(name) >= 9").fetchall()
        conn.close()
        for row in rows:
            facts = {k.lower(): v.strip() for k, v in REQ.findall(row["signature"])}
            header, lib = facts.get("header", "").lower(), facts.get("library", "").lower()
            if not header or not lib or header in BORING or lib in BORING:
                continue
            if " " in header or not lib.endswith(".lib"):
                continue
            by_header[header].append((row["name"], header, lib))

    headers = [h for h, v in by_header.items() if v]
    rng.shuffle(headers)
    tasks = []
    for i in range(0, len(headers) - 1, 2):
        picked = [rng.choice(by_header[headers[i]]),
                  rng.choice(by_header[headers[i + 1]])]
        claims = []
        for name, header, lib in picked:
            claims.append({"kind": "header", "api": name, "value": header})
            claims.append({"kind": "library", "api": name, "value": lib})
        tasks.append({
            "names": " and ".join(p[0] for p in picked),
            "claims": claims,
        })
        if len(tasks) >= n:
            break
    return tasks


def scored(reply: str, claims: list[dict]) -> int:
    low = (reply or "").lower()
    return sum(1 for c in claims if c["value"] in low)


async def context_for(packs_dir: str, question: str) -> str:
    parts = []
    for name in list(dict.fromkeys(NAME_RE.findall(question)))[:4]:
        for hit in (await tools.docs_lookup_impl(packs_dir, name))[:1]:
            parts.append(f"[{hit.get('source')}] {hit.get('name')} -- "
                         f"{hit.get('signature')}")
    for h in (await tools.docs_search_impl(packs_dir, question, limit=2)):
        parts.append(f"[{h.get('source')}] {h.get('title')}\n"
                     f"{str(h.get('text'))[:600]}")
    return "\n\n".join(parts) or "(nothing found)"


async def main() -> None:
    packs_dir = Path(sys.argv[1])
    n = int(sys.argv[2]) if len(sys.argv) > 2 else 25
    rng = random.Random(20260810)
    tasks = build_tasks(packs_dir, n, rng)
    print(f"{len(tasks)} tasks, {sum(len(t['claims']) for t in tasks)} claims\n")
    await tools.docs_search_impl(str(packs_dir), "warmup", limit=1)

    totals = [0, 0, 0, 0]
    denom = 0
    verify_fired = verify_gained = verify_lost = 0
    dbl_fired = dbl_gained = dbl_lost = 0

    for i, t in enumerate(tasks, 1):
        q = TASK.format(names=t["names"])
        claims = t["claims"]
        denom += len(claims)

        draft = ask(q)
        a = scored(draft, claims)

        ctx = await context_for(str(packs_dir), q)
        b_reply = ask(GROUNDED.format(ctx=ctx, q=q))
        b = scored(b_reply, claims)

        findings = await tools.docs_verify_impl(str(packs_dir), f"{q}\n{draft}")
        lines = [f"- {f['name']}: documented {c['field']} is {c['documented']}"
                 for f in findings for c in f["corrections"]]
        if lines:
            verify_fired += 1
            c_reply = ask(REVISE.format(draft=draft.strip(),
                                        corrections="\n".join(lines), q=q))
        else:
            c_reply = draft
        c = scored(c_reply, claims)
        verify_gained += max(0, c - a)
        verify_lost += max(0, a - c)

        # Double-tap: verify the RETRIEVED answer, not the cold draft. Best
        # strategy on single facts and, until now, untested on code -- which
        # is the shape verification was designed for, since a block asserts
        # several facts at once and each can be checked independently.
        findings_d = await tools.docs_verify_impl(str(packs_dir), f"{q}\n{b_reply}")
        lines_d = [f"- {f['name']}: documented {c2['field']} is {c2['documented']}"
                   for f in findings_d for c2 in f["corrections"]]
        if lines_d:
            dbl_fired += 1
            d_reply = ask(REVISE.format(draft=b_reply.strip(),
                                        corrections="\n".join(lines_d), q=q))
        else:
            d_reply = b_reply
        d = scored(d_reply, claims)
        dbl_gained += max(0, d - b)
        dbl_lost += max(0, b - d)

        totals[0] += a; totals[1] += b; totals[2] += c; totals[3] += d
        if i % 5 == 0:
            print(f"  ... {i}/{len(tasks)}", flush=True)

    print(f"\n{'strategy':22}{'claims correct':>16}")
    for label, got in zip(("closed book", "retrieval-first",
                           "verify-after", "double-tap"), totals):
        pct = 100.0 * got / denom if denom else 0
        print(f"{label:22}{f'{got}/{denom}':>12} {pct:5.0f}%")
    print(f"\n  docs_verify fired on {verify_fired}/{len(tasks)} drafts")
    print(f"    claims gained {verify_gained}, claims lost {verify_lost}")
    print(f"  double-tap verified {dbl_fired}/{len(tasks)} retrieved answers")
    print(f"    claims gained {dbl_gained}, claims lost {dbl_lost}")


asyncio.run(main())
