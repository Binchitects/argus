#!/usr/bin/env python3
"""
The science lab's mathematics, measured: problems of MATH-500 (Hendrycks et al.'s MATH test set as HuggingFaceH4
publishes it, each with its reference answer) asked through Arena's chat, each model alone (no tools) and in a lab
notebook (the Mathematics field: SymPy's checks, Lean's proofs, Python), graded on the last \\boxed{...} of the
final answer with MATH's own answer normalization. Asked as a throwaway person, removed after (their chats with them).

    python3 evals/run_lab_math.py --models MODEL[,MODEL...] [--arms plain,lab] [--count 60] [--seed 7]

The problems are a sample across the set: as many of each level (1-5) and subject as the count allows, drawn with
the seed, never the first N. The set is downloaded once into evals/.cache (about 450 KB). Writes
lab-math-results.json: per model and arm, the share right, seconds, tokens and the lab's tool calls; per problem
what was answered.
"""
from __future__ import annotations

import argparse
import importlib.util
import json
import random
import re
import signal
import sys
import time
import urllib.request
import uuid
from collections import defaultdict
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent
SET_URL = "https://huggingface.co/datasets/HuggingFaceH4/MATH-500/resolve/main/test.jsonl"
CACHE = HERE / ".cache" / "math-500.jsonl"
ASK = "\n\nGive the final answer as \\boxed{...} at the end."


def problems(count: int, seed: int) -> list[dict]:
    if not CACHE.exists():
        CACHE.parent.mkdir(parents=True, exist_ok=True)
        with urllib.request.urlopen(SET_URL, timeout=60) as r:
            CACHE.write_bytes(r.read())
    rows = [json.loads(line) for line in CACHE.read_text(encoding="utf-8").splitlines() if line.strip()]
    # Across the set: round-robin over (level, subject) groups, each group shuffled with the seed.
    groups: dict[tuple, list] = defaultdict(list)
    for row in rows:
        groups[(row["level"], row["subject"])].append(row)
    rng = random.Random(seed)
    order = sorted(groups)
    for key in order:
        rng.shuffle(groups[key])
    rng.shuffle(order)
    picked: list[dict] = []
    while len(picked) < count and any(groups.values()):
        for key in order:
            if groups[key] and len(picked) < count:
                picked.append(groups[key].pop())
    return picked


# ---- MATH's answer normalization (Hendrycks et al., math_equivalence.py), as the set's own papers grade it.
def _fix_fracs(s: str) -> str:
    parts = s.split("\\frac")
    out = parts[0]
    for sub in parts[1:]:
        out += "\\frac"
        if sub.startswith("{"):
            out += sub
        elif len(sub) >= 2:
            a, b, rest = sub[0], sub[1], sub[2:]
            out += "{" + a + "}" + ("{" + b + "}" + rest if b != "{" else b + rest)
        else:
            return s
    return out


def _fix_a_slash_b(s: str) -> str:
    if s.count("/") != 1:
        return s
    a, b = s.split("/")
    try:
        a_i, b_i = int(a), int(b)
        assert s == f"{a_i}/{b_i}"
        return "\\frac{" + str(a_i) + "}{" + str(b_i) + "}"
    except (ValueError, AssertionError):
        return s


def _remove_right_units(s: str) -> str:
    return s.split("\\text{ ")[0] if "\\text{ " in s else s


def _fix_sqrt(s: str) -> str:
    if "\\sqrt" not in s:
        return s
    parts = s.split("\\sqrt")
    out = parts[0]
    for sub in parts[1:]:
        out += "\\sqrt" + (sub if sub.startswith("{") or not sub else "{" + sub[0] + "}" + sub[1:])
    return out


def normalize(s: str) -> str:
    s = s.replace("\n", "").replace("\\!", "").replace("\\\\", "\\").replace("tfrac", "frac").replace("dfrac", "frac")
    s = s.replace("\\left", "").replace("\\right", "").replace("^{\\circ}", "").replace("^\\circ", "")
    s = s.replace("\\$", "").replace("$", "")
    s = _remove_right_units(s)
    s = s.replace("\\%", "").replace("%", "")
    s = s.replace(" .", " 0.").replace("{.", "{0.")
    if s.startswith("."):
        s = "0" + s
    if len(s.split("=")) == 2 and len(s.split("=")[0]) <= 2:
        s = s.split("=")[1]
    s = _fix_sqrt(s)
    s = s.replace(" ", "")
    s = _fix_fracs(s)
    if s == "0.5":
        s = "\\frac{1}{2}"
    s = _fix_a_slash_b(s)
    return s


def boxed(text: str) -> str | None:
    """The last \\boxed{...} (or \\fbox{...}) of the text, its braces balanced."""
    at = max(text.rfind("\\boxed{"), text.rfind("\\fbox{"))
    if at < 0:
        return None
    i = text.index("{", at) + 1
    depth = 1
    for j in range(i, len(text)):
        depth += {"{": 1, "}": -1}.get(text[j], 0)
        if depth == 0:
            return text[i:j]
    return None


def equivalent(answer: str | None, reference: str) -> bool:
    if answer is None:
        return False
    try:
        return normalize(answer) == normalize(reference)
    except Exception:  # a malformed answer is graded as given
        return answer == reference


# ---- Arena, as a throwaway person.
def browser():
    spec = importlib.util.spec_from_file_location("ft", ROOT / "deploy" / "scripts" / "functional-test.py")
    ft = importlib.util.module_from_spec(spec)
    argv, sys.argv = sys.argv, ["ft"]
    spec.loader.exec_module(ft)
    sys.argv = argv
    return ft


def main() -> int:
    p = argparse.ArgumentParser()
    p.add_argument("--models", required=True)
    p.add_argument("--arms", default="plain,lab")
    p.add_argument("--count", type=int, default=60)
    p.add_argument("--seed", type=int, default=7)
    p.add_argument("--out", default=str(HERE / "lab-math-results.json"))
    a = p.parse_args()
    # Stopped (SIGTERM) as on Ctrl-C: the throwaway person is removed all the same.
    signal.signal(signal.SIGTERM, lambda *_: sys.exit(143))
    ft = browser()
    password = next(line.split("=", 1)[1].strip() for line in (ROOT / "deploy" / ".env").read_text().splitlines() if line.startswith("ADMIN_PASSWORD="))
    admin = ft.Browser()
    assert admin.login("admin", password)
    name = "labmath" + uuid.uuid4().hex[:6]
    code, made = admin.app("POST", "/api/admin/people", {"userName": name, "email": f"{name}@example.test", "displayName": "Lab maths eval"})
    assert code in (200, 201), (code, made)
    results = {"set": "MATH-500 (HuggingFaceH4)", "grading": "MATH's answer normalization on the last \\boxed{}", "count": a.count, "seed": a.seed, "runs": []}
    try:
        me = ft.Browser()
        assert me.login(name, made["password"])
        todo = problems(a.count, a.seed)
        for model in a.models.split(","):
            for arm in a.arms.split(","):
                run = {"model": model, "arm": arm, "right": 0, "asked": 0, "seconds": 0.0, "tokens": 0, "tool_calls": 0, "problems": []}
                results["runs"].append(run)
                for i, row in enumerate(todo, 1):
                    body = {"model": model, **({"lab": "mathematics"} if arm == "lab" else {"tools": []})}
                    code, chat = me.app("POST", "/api/chat/conversations", body)
                    if code not in (200, 201):
                        print(f"{model} {arm}: could not start a chat ({code} {chat})", flush=True)
                        break
                    started = time.time()
                    st, _, _, _ = me.req(ft.u("", f"/api/chat/conversations/{chat['id']}/messages"), {"content": row["problem"] + ASK},
                                         headers={"X-Requested-With": "eval"}, timeout=900)
                    seconds = time.time() - started
                    code, conv = me.app("GET", f"/api/chat/conversations/{chat['id']}")
                    msgs = conv.get("messages", [])
                    answers = [m for m in msgs if m["role"] == "assistant"]
                    final = answers[-1]["content"] if answers else ""
                    got = boxed(final)
                    ok = equivalent(got, row["answer"])
                    calls = sum(len(m.get("toolCalls") or []) for m in answers)
                    tokens = sum((m.get("completionTokens") or 0) for m in answers)
                    run["asked"] += 1
                    run["right"] += ok
                    run["seconds"] += seconds
                    run["tokens"] += tokens
                    run["tool_calls"] += calls
                    run["problems"].append({"id": row["unique_id"], "level": row["level"], "subject": row["subject"], "reference": row["answer"],
                                            "answered": got, "right": ok, "seconds": round(seconds, 1), "tokens": tokens, "tool_calls": calls})
                    me.app("DELETE", f"/api/chat/conversations/{chat['id']}")
                    print(f"{model} {arm} {i}/{len(todo)} {'✓' if ok else '✗'} {row['unique_id']} ({run['right']}/{run['asked']})", flush=True)
                    Path(a.out).write_text(json.dumps(results, indent=1, ensure_ascii=False))
                run["share"] = round(run["right"] / max(run["asked"], 1), 3)
                Path(a.out).write_text(json.dumps(results, indent=1, ensure_ascii=False))
    finally:
        admin.app("DELETE", f"/api/admin/people/{made['id']}")
    for run in results["runs"]:
        print(f"{run['model']:40} {run['arm']:6} {run['right']}/{run['asked']}  {run['seconds'] / max(run['asked'], 1):.0f} s each  {run['tool_calls']} tool calls")
    return 0


if __name__ == "__main__":
    sys.exit(main())
