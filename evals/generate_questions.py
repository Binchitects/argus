"""Generate the question set FROM the packs, so ground truth is never mine.

Every previous attempt wrote questions from memory and checked them
afterwards, and the last one marked the packs wrong for being right: I expected
PASSIVE_LEVEL for IoCreateDevice, Microsoft documents `<= APC_LEVEL`, and the
grounded answer was scored a failure for following the documentation.

Here the fact and the question are extracted from the same pack page, so the
expected answer is whatever Microsoft, cppreference or tldr actually publish.

That is not circular. The claim being tested is "does retrieval make the agent
agree with the official documentation", which is what a reference is for, and
the closed-book arm measures how often the model already knows the documented
fact. If the docs were wrong, both arms would be wrong together.

Selection is deliberately biased toward the hard end:

* names long enough to be distinctive (a short name is often a common word);
* facts that are unambiguous in the page -- one header, one library;
* generic headers dropped, since "windows.h" is guessable and tests nothing;
* names sampled across the alphabet rather than the first N, which on an
  alphabetically-sorted corpus means everything starts with "A".

    python gen_questions.py <packs_dir> <out.json> [per_pack]
"""
import json
import random
import re
import sqlite3
import sys
from pathlib import Path

# Guessable from context alone; asking about them measures nothing.
BORING_HEADERS = {"windows.h", "winnt.h", "wtypes.h", "basetsd.h", "sdkddkver.h"}
BORING_LIBS = {"kernel32.lib"}

REQ = re.compile(r"(Header|Library|DLL|IRQL):\s*([^;]+)")


def facts(signature: str) -> dict[str, str]:
    return {k.lower(): v.strip() for k, v in REQ.findall(signature or "")}


def from_api_pack(path: Path, name: str, n: int, rng: random.Random) -> list[dict]:
    """Header / library / IRQL questions from win32 and wdk requirement lines."""
    conn = sqlite3.connect(f"file:{path}?mode=ro", uri=True)
    conn.row_factory = sqlite3.Row
    rows = conn.execute(
        "SELECT s.name, s.kind, s.signature FROM api_symbols s "
        "WHERE s.signature LIKE 'Header:%' AND length(s.name) >= 9"
    ).fetchall()
    conn.close()

    out: list[dict] = []
    rng.shuffle(rows)
    for row in rows:
        f = facts(row["signature"])
        header = f.get("header", "").lower()
        if not header or header in BORING_HEADERS or " " in header:
            continue
        kinds = [("header", f"Which header declares {row['name']}?", header,
                  "one header file name")]
        lib = f.get("library", "").lower()
        if lib and lib.endswith(".lib") and lib not in BORING_LIBS:
            kinds.append(("library",
                          f"Which import library must be linked to use "
                          f"{row['name']}?", lib, "one .lib file name"))
        irql = f.get("irql", "").lower()
        if irql and "level" in irql and "see " not in irql:
            kinds.append(("irql",
                          f"At what IRQL may {row['name']} be called?",
                          irql.replace("<=", "").strip(), "one IRQL name"))
        pick = rng.choice(kinds)
        out.append({"pack": name, "kind": pick[0], "question": pick[1],
                    "answer": pick[2], "shape": pick[3]})
        if len(out) >= n:
            break
    return out


def from_cpp_pack(path: Path, n: int, rng: random.Random) -> list[dict]:
    """Which standard header provides a given std:: entity."""
    conn = sqlite3.connect(f"file:{path}?mode=ro", uri=True)
    conn.row_factory = sqlite3.Row
    rows = conn.execute(
        "SELECT name, namespace FROM api_symbols "
        "WHERE name LIKE 'std::%' AND namespace != '' AND length(name) >= 10"
    ).fetchall()
    conn.close()
    rng.shuffle(rows)
    out = []
    for row in rows:
        header = row["namespace"].strip().lower()
        if not header or "/" in header or " " in header or len(header) < 3:
            continue
        out.append({
            "pack": "cpp", "kind": "header",
            "question": f"Which C++ standard library header provides "
                        f"{row['name']}?",
            "answer": header, "shape": "one header name"})
        if len(out) >= n:
            break
    return out


def from_scripting_pack(path: Path, n: int, rng: random.Random) -> list[dict]:
    """Reverse lookup: given what a command does, name the command.

    Harder than header recall and closer to how a scripting question actually
    arrives -- the developer knows the goal, not the name.
    """
    conn = sqlite3.connect(f"file:{path}?mode=ro", uri=True)
    conn.row_factory = sqlite3.Row
    rows = conn.execute(
        "SELECT name, kind, signature FROM api_symbols "
        "WHERE length(signature) >= 40 AND length(name) >= 5"
    ).fetchall()
    conn.close()
    rng.shuffle(rows)
    out = []
    seen = set()
    for row in rows:
        name = row["name"]
        if name.lower() in seen:
            continue
        summary = row["signature"].strip().rstrip(".")
        # Descriptions that name the command give the answer away.
        if name.lower() in summary.lower():
            continue
        seen.add(name.lower())
        out.append({
            "pack": "scripting", "kind": "reverse",
            "question": f"Which command or cmdlet is described as: "
                        f"\"{summary[:180]}\"?",
            "answer": name.lower(), "shape": "one command or cmdlet name"})
        if len(out) >= n:
            break
    return out


def main() -> None:
    packs_dir = Path(sys.argv[1])
    out_path = Path(sys.argv[2])
    per_pack = int(sys.argv[3]) if len(sys.argv) > 3 else 25
    rng = random.Random(20260809)      # fixed, so the set is reproducible

    questions: list[dict] = []
    for pack in ("win32", "wdk"):
        p = packs_dir / f"{pack}.arguspack"
        if p.exists():
            questions += from_api_pack(p, pack, per_pack, rng)
    for pack, fn in (("cpp", from_cpp_pack), ("scripting", from_scripting_pack)):
        p = packs_dir / f"{pack}.arguspack"
        if p.exists():
            questions += (fn(p, per_pack, rng) if pack == "cpp"
                          else fn(p, per_pack, rng))

    out_path.write_text(json.dumps(questions, indent=1), encoding="utf-8")
    by_pack: dict[str, int] = {}
    for q in questions:
        by_pack[q["pack"]] = by_pack.get(q["pack"], 0) + 1
    print(f"wrote {len(questions)} questions to {out_path}")
    for pack, count in sorted(by_pack.items()):
        print(f"  {pack:10} {count}")
    for q in questions[:4]:
        print(f"\n  [{q['pack']}/{q['kind']}] {q['question'][:96]}")
        print(f"     answer: {q['answer']}")


main()
