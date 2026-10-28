#!/usr/bin/env python3
"""
The science lab's quantum computing, measured: Qiskit HumanEval (qiskit-community/qiskit-human-eval, Apache-2.0: Qiskit
problems written by Qiskit's advocates, each a function to write with its canonical solution and its tests) asked
through Arena's chat, each model alone (no tools) and in a lab notebook (the Quantum computing field: Qiskit and Aer,
Python), graded by the set's own tests run in the sandbox's image (no network). Asked as a throwaway person, removed
after (their chats with them).

    python3 evals/run_lab_quantum.py --models MODEL[,MODEL...] [--arms plain,lab] [--count 0] [--seed 7]

Only the problems whose canonical solution passes its tests in the sandbox are asked (the set targets Qiskit 2.4 with
qiskit-ibm-runtime's fake devices, which the sandbox does not have): how many were left out, and why, is written too.
--count 0 asks every one; a count is drawn across the difficulties with the seed, never the first N. The set is
downloaded once into evals/.cache (about 210 KB). Writes lab-quantum-results.json: per model and arm, the share
passing, seconds, tokens and the lab's tool calls; per problem whether it passed and why not.
"""
from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import random
import re
import signal
import subprocess
import sys
import time
import urllib.request
import uuid
from collections import defaultdict
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent
SET_URL = "https://raw.githubusercontent.com/qiskit-community/qiskit-human-eval/main/dataset/dataset_qiskit_test_human_eval.json"
SET_SHA256 = "631f212e0130627f39fab788d1e02d15ff1de5ef9e0e7acfd6079b8498399b06"
CACHE = HERE / ".cache" / "qiskit-human-eval.json"
SANDBOX = "arena-sandbox"
TIMEOUT = 120

# Runs each program in its own process with a time limit, in the sandbox's image: the results as JSON.
RUNNER = r"""
import json, os, subprocess, sys, tempfile
out = {}
for row in json.load(sys.stdin):
    with tempfile.TemporaryDirectory() as d:
        try:
            p = subprocess.run([sys.executable, "-c", row["program"]], cwd=d, capture_output=True, text=True, timeout=row["timeout"],
                               env={**os.environ, "MPLBACKEND": "Agg", "HOME": d})
            out[row["id"]] = {"ok": p.returncode == 0, "error": (p.stderr or p.stdout)[-600:] if p.returncode else ""}
        except subprocess.TimeoutExpired:
            out[row["id"]] = {"ok": False, "error": f"took over {row['timeout']} s"}
print(json.dumps(out))
"""


def problems() -> list[dict]:
    if not CACHE.exists():
        CACHE.parent.mkdir(parents=True, exist_ok=True)
        with urllib.request.urlopen(SET_URL, timeout=60) as r:
            data = r.read()
        assert hashlib.sha256(data).hexdigest() == SET_SHA256, "the set is not the one measured: check what changed upstream"
        CACHE.write_bytes(data)
    return json.loads(CACHE.read_text(encoding="utf-8"))


def run(programs: dict[str, str]) -> dict[str, dict]:
    """Each program run in the sandbox's image, as nobody, with no network and nothing kept."""
    rows = [{"id": k, "program": v, "timeout": TIMEOUT} for k, v in programs.items()]
    p = subprocess.run(["docker", "run", "--rm", "-i", "--network", "none", "--user", "65534:65534", "--read-only", "--tmpfs", "/tmp:size=1g",
                        "--cap-drop", "ALL", "--security-opt", "no-new-privileges:true", "-e", "TMPDIR=/tmp", "--entrypoint", "python", SANDBOX,
                        "-c", RUNNER], input=json.dumps(rows), capture_output=True, text=True, timeout=TIMEOUT * len(rows) + 120)
    if p.returncode != 0:
        raise RuntimeError(f"the sandbox's image did not run them: {p.stderr[-500:]}")
    return json.loads(p.stdout)


def program(row: dict, code: str) -> str:
    """The answer with the set's tests: a whole function after the prompt's imports, or the prompt's function continued."""
    head = row["prompt"].split("\ndef ", 1)[0]
    body = code if re.search(rf"^def {re.escape(row['entry_point'])}\s*\(", code, re.MULTILINE) else row["prompt"] + code
    return f"{head}\n{body}\n\n{row['test']}\n\ncheck({row['entry_point']})\n"


def answer_code(text: str) -> str | None:
    """The last Python block of the answer."""
    blocks = re.findall(r"```(?:python|py)?[ \t]*\n(.*?)```", text, re.DOTALL)
    return blocks[-1] if blocks else None


def ask(row: dict) -> str:
    return ("Write this Python function with Qiskit 2. Give the whole function, with its imports, in one ```python block at the end of your "
            f"answer.\n\n```python\n{row['prompt'].rstrip()}\n```")


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
    p.add_argument("--count", type=int, default=0)
    p.add_argument("--seed", type=int, default=7)
    p.add_argument("--out", default=str(HERE / "lab-quantum-results.json"))
    a = p.parse_args()
    signal.signal(signal.SIGTERM, lambda *_: sys.exit(143))
    rows = problems()
    canonical = run({r["task_id"]: program(r, r["prompt"] + r["canonical_solution"]) for r in rows})
    usable = [r for r in rows if canonical[r["task_id"]]["ok"]]
    left_out = {r["task_id"]: canonical[r["task_id"]]["error"].strip().splitlines()[-1:] for r in rows if not canonical[r["task_id"]]["ok"]}
    print(f"{len(usable)} of {len(rows)} problems pass their own tests in the sandbox; the rest are left out", flush=True)
    todo = usable
    if a.count:
        groups = defaultdict(list)
        for r in usable:
            groups[r["difficulty_scale"]].append(r)
        rng = random.Random(a.seed)
        for g in groups.values():
            rng.shuffle(g)
        todo, i = [], 0
        while len(todo) < min(a.count, len(usable)):
            for key in sorted(groups):
                if i < len(groups[key]) and len(todo) < a.count:
                    todo.append(groups[key][i])
            i += 1
    ft = browser()
    admin = ft.Browser()
    assert admin.login(ft.env("ADMIN_USERNAME", "admin"), ft.env("ADMIN_PASSWORD")), "the admin could not sign in"
    name = "labquantum" + uuid.uuid4().hex[:6]
    code, made = admin.app("POST", "/api/admin/people", {"userName": name, "email": f"{name}@example.test", "displayName": "Lab quantum eval"})
    assert code in (200, 201), code
    results = {"set": "Qiskit HumanEval (qiskit-community/qiskit-human-eval, Apache-2.0)", "set_sha256": SET_SHA256,
               "grading": "the set's own tests, run in the sandbox's image", "problems": len(rows), "usable": len(usable),
               "left_out": left_out, "count": len(todo), "seed": a.seed, "runs": []}
    try:
        me = ft.Browser()
        assert me.login(name, made["password"])
        for model in a.models.split(","):
            for arm in a.arms.split(","):
                run_ = {"model": model, "arm": arm, "passed": 0, "asked": 0, "seconds": 0.0, "tokens": 0, "tool_calls": 0, "problems": []}
                results["runs"].append(run_)
                answers = {}
                for i, row in enumerate(todo, 1):
                    body = {"model": model, **({"lab": "quantum"} if arm == "lab" else {"tools": []})}
                    code, chat = me.app("POST", "/api/chat/conversations", body)
                    if code not in (200, 201):
                        print(f"{model} {arm}: could not start a chat ({code} {chat})", flush=True)
                        break
                    started = time.time()
                    me.req(ft.u("", f"/api/chat/conversations/{chat['id']}/messages"), {"content": ask(row)}, headers={"X-Requested-With": "eval"}, timeout=1800)
                    seconds = time.time() - started
                    code, conv = me.app("GET", f"/api/chat/conversations/{chat['id']}")
                    said = [m for m in conv.get("messages", []) if m["role"] == "assistant"]
                    final = said[-1]["content"] if said else ""
                    answers[row["task_id"]] = answer_code(final)
                    run_["problems"].append({"id": row["task_id"], "difficulty": row["difficulty_scale"], "seconds": round(seconds, 1),
                                             "tokens": sum((m.get("completionTokens") or 0) for m in said),
                                             "tool_calls": sum(len(m.get("toolCalls") or []) for m in said), "answered": answers[row["task_id"]] is not None})
                    me.app("DELETE", f"/api/chat/conversations/{chat['id']}")
                    print(f"{model} {arm} {i}/{len(todo)} {row['task_id']} ({seconds:.0f} s)", flush=True)
                graded = run({k: program(next(r for r in todo if r["task_id"] == k), v) for k, v in answers.items() if v is not None})
                for prob in run_["problems"]:
                    g = graded.get(prob["id"], {"ok": False, "error": "no Python block in the answer"})
                    prob["passed"], prob["error"] = g["ok"], g["error"].strip().splitlines()[-1:] if g["error"] else []
                    run_["asked"] += 1
                    run_["passed"] += g["ok"]
                    run_["seconds"] += prob["seconds"]
                    run_["tokens"] += prob["tokens"]
                    run_["tool_calls"] += prob["tool_calls"]
                run_["share"] = round(run_["passed"] / max(run_["asked"], 1), 3)
                for level in sorted({prob["difficulty"] for prob in run_["problems"]}):
                    mine = [prob for prob in run_["problems"] if prob["difficulty"] == level]
                    run_.setdefault("by_difficulty", {})[level] = f"{sum(prob['passed'] for prob in mine)}/{len(mine)}"
                Path(a.out).write_text(json.dumps(results, indent=1, ensure_ascii=False))
    finally:
        admin.app("DELETE", f"/api/admin/people/{made['id']}")
    Path(a.out).write_text(json.dumps(results, indent=1, ensure_ascii=False))
    for r in results["runs"]:
        print(f"{r['model']:40} {r['arm']:6} {r['passed']}/{r['asked']}  {r['seconds'] / max(r['asked'], 1):.0f} s each  {r['tool_calls']} tool calls  "
              f"{r.get('by_difficulty')}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
