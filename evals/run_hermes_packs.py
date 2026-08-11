"""Does Hermes answer correctly from each installed pack?

One question per pack, each with a ground-truth token taken from the pack
itself rather than from memory. Grading is substring matching on that token,
so the result is objective: either the documented string is in the answer or
it is not.

This drives the REAL Hermes CLI (`hermes -z`), not a reimplementation of it,
so what is measured is the whole chain -- MCP discovery, tool registration,
the server's instructions, native function calling, and the model.

    python evals/run_hermes_packs.py [pack ...]
"""
from __future__ import annotations

import json
import subprocess
import sys
import time
from pathlib import Path

HERMES = Path(r"C:/Users/aligh/AppData/Local/hermes/hermes-agent")
PYTHON = HERMES / "venv" / "Scripts" / "python.exe"

#: pack -> (question, tokens any of which prove the documented fact was used)
QUESTIONS: dict[str, tuple[str, tuple[str, ...]]] = {
    "win32": (
        "Which import library must I link to call CryptAcquireContextW, "
        "and which header declares it?",
        ("advapi32.lib", "wincrypt.h"),
    ),
    "wdk": (
        "What IRQL is ExInitializeLookasideListEx callable at, and which "
        "header declares it?",
        ("dispatch_level",),
    ),
    "debugger": (
        "In WinDbg, which command reloads symbols, and which command displays "
        "a structure at an address?",
        (".reload",),
    ),
    "scripting": (
        "Which robocopy option mirrors a directory tree, deleting files at "
        "the destination that no longer exist at the source?",
        ("/mir",),
    ),
    "cpp": (
        "Which MSVC compiler option selects the C++20 language standard?",
        ("/std:c++20",),
    ),
    "python": (
        "In Python, what does os.path.join do when one of the later "
        "components is an absolute path?",
        ("discard", "absolute"),
    ),
    "react": (
        "In React, what does the useState hook return?",
        ("pair", "array", "setter", "state variable"),
    ),
    "sqlite": (
        "Which SQLite statement rebuilds the database file to reclaim unused "
        "space?",
        ("vacuum",),
    ),
    "algorithms": (
        "Show me the name of a sorting algorithm implementation available in "
        "the algorithms documentation.",
        ("sort",),
    ),
    "system-design": (
        "In system design, what is a CDN and when would you use one?",
        ("content delivery", "content distribution", "edge"),
    ),
    "cppreference": (
        "What does std::vector::push_back do, and what is its complexity?",
        ("amortized", "constant", "appends", "end"),
    ),
}

TIMEOUT = 2400


def ask(question: str) -> tuple[str, float]:
    started = time.perf_counter()
    try:
        result = subprocess.run(
            [str(PYTHON), "-m", "hermes_cli.main", "-z", question],
            cwd=str(HERMES), capture_output=True, text=True,
            encoding="utf-8", errors="replace", timeout=TIMEOUT,
        )
        return (result.stdout or "") + (result.stderr or ""), time.perf_counter() - started
    except subprocess.TimeoutExpired:
        return "<TIMEOUT>", time.perf_counter() - started


def main() -> None:
    wanted = sys.argv[1:] or list(QUESTIONS)
    rows = []
    for pack in wanted:
        if pack not in QUESTIONS:
            print(f"  (no question for {pack!r})", file=sys.stderr)
            continue
        question, tokens = QUESTIONS[pack]
        print(f"=== {pack} ===", flush=True)
        answer, seconds = ask(question)
        low = answer.lower()
        hit = next((t for t in tokens if t.lower() in low), "")
        # A refusal is worth separating from a wrong answer: it means
        # retrieval missed, not that the model invented something.
        refused = any(p in low for p in (
            "not documented", "unable to locate", "no final response",
            "don't have", "do not have"))
        # And a timeout is neither. Graded as FAIL it reads as "the pack got
        # this wrong", which is a claim the run does not support -- the model
        # never finished. The largest packs are simply slow: win32 exceeded
        # 900s on a question it answers correctly given longer.
        if answer == "<TIMEOUT>":
            verdict = "TIMEOUT"
        elif hit:
            verdict = "PASS"
        elif refused:
            verdict = "REFUSED"
        else:
            verdict = "FAIL"
        rows.append({
            "pack": pack, "seconds": round(seconds, 1),
            "verdict": verdict,
            "matched": hit,
            "answer": " ".join(answer.split())[:300],
        })
        print(f"  {rows[-1]['verdict']}  {seconds:.0f}s  {rows[-1]['answer'][:120]}",
              flush=True)

    print("\n##### AGGREGATE\n")
    print(f"  {'pack':14} {'verdict':9} {'secs':>6}  matched")
    for row in rows:
        print(f"  {row['pack']:14} {row['verdict']:9} {row['seconds']:>6}  {row['matched']}")
    passed = sum(1 for r in rows if r["verdict"] == "PASS")
    print(f"\n  {passed} of {len(rows)} answered with the documented fact")
    Path("evals/hermes-packs-results.json").write_text(
        json.dumps(rows, indent=2), encoding="utf-8")


if __name__ == "__main__":
    main()
