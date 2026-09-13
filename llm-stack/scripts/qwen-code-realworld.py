#!/usr/bin/env python3
"""
Real work, through Qwen Code, on a large codebase -- scored, not eyeballed.

A chat benchmark says how fast tokens come out. It does not say whether an
agent can find its way around 12,000 files, read a 9,000-line source file
without choking the context window, or rename a function without touching an
unrelated one of the same name. This runs Qwen Code headless against the stack,
exactly as a developer would point it (gateway URL, their own key), on tasks
whose right answers were established beforehand with grep.

Isolation: every task gets a fresh copy of the repository, and Qwen Code runs
in a container that can write ONLY that copy. Its shell tool therefore cannot
touch the host, and each task's diff is exactly what the agent did. Your own
~/.qwen settings are never read or modified: the run uses a throwaway home.

    python3 scripts/qwen-code-realworld.py --repo /path/to/qt-creator \\
        --qwen-home ~/.local/lib/qwen-code --key-a sk-... --key-b sk-... \\
        --workdir /tmp/qwen-rw --json results.json

The task set targets Qt Creator 4.11.2. Point --repo at another codebase and
the scoring will (correctly) fail; write tasks for it instead.
"""

import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import threading
import time

IMAGE_DEFAULT = "argus:server"   # any glibc image with bash, git, grep, rg

TASKS = [
    {
        "id": "T1-navigate-large-file",
        "kind": "read",
        "prompt": (
            "In this repository, find the implementation of the text editor's "
            "'duplicate selection' feature. Reply with the file path and line number of the "
            "function that does the work, and explain in a few sentences how it works and how "
            "the 'duplicate and comment' variant differs. Do not modify any files."
        ),
    },
    {
        "id": "T2-huge-c-file",
        "kind": "read",
        "prompt": (
            "The bundled SQLite amalgamation is at src/libs/3rdparty/sqlite/sqlite3.c (over 200,000 "
            "lines, so do not try to read it whole). What is the default value of "
            "SQLITE_MAX_VARIABLE_NUMBER, at which line is that default defined, and what integer "
            "type does SQLite use for variable numbers (ynVar) if the maximum is raised above 32767? "
            "Answer with the value, the line number and the type. Do not modify any files."
        ),
    },
    {
        "id": "T3-cross-codebase-inventory",
        "kind": "write-report",
        "prompt": (
            "Find every class under src/ that inherits directly from Core::IOptionsPage "
            "(written as 'public Core::IOptionsPage' or 'public IOptionsPage'). Write the list to a "
            "new file options_pages.md in the repository root as a markdown table with columns "
            "Class and File, one row per class, and put the total count on the last line as "
            "'Total: N'. Do not modify any other file."
        ),
    },
    {
        "id": "T4-rename-with-trap",
        "kind": "edit",
        "prompt": (
            "In src/plugins/fakevim/fakevimhandler.cpp, rename the static helper function "
            "startsWithWhitespace to lineStartsWithWhitespace and update every use of it in that "
            "file. Change nothing else in the repository."
        ),
    },
    {
        "id": "T5-fix-in-large-file",
        "kind": "edit",
        "prompt": (
            "In src/plugins/fakevim/fakevimhandler.cpp, the static function getProcessOutput ignores "
            "the case where the process fails to start. Make it return an empty QString when the "
            "process does not start, and keep its behaviour otherwise unchanged. Change nothing "
            "else in the repository."
        ),
    },
]


def git(repo, *args):
    return subprocess.run(["git", "-C", repo, *args], capture_output=True, text=True).stdout


def changed_files(repo):
    out = git(repo, "status", "--porcelain")
    return sorted(line[3:] for line in out.splitlines() if line.strip())


def score(task, repo, answer):
    """Return (passed: bool, notes: list[str]). Ground truth was fixed beforehand."""
    notes, ok = [], True
    files = changed_files(repo)

    def check(cond, msg):
        nonlocal ok
        notes.append(("PASS " if cond else "FAIL ") + msg)
        ok = ok and cond

    if task["id"] == "T1-navigate-large-file":
        check("texteditor.cpp" in answer, "names src/plugins/texteditor/texteditor.cpp")
        lines = [int(n) for n in re.findall(r"\b(6[6-9]\d\d)\b", answer)]
        check(any(abs(n - 6779) <= 5 for n in lines), "gives line ~6779 (TextEditorWidgetPrivate::duplicateSelection)")
        check(re.search(r"comment", answer, re.I) is not None, "explains the comment variant")
        check(files == [], f"modified no files (changed: {files})")
    elif task["id"] == "T2-huge-c-file":
        check(re.search(r"\b999\b", answer) is not None, "default is 999")
        check(re.search(r"\b1294[23]\b", answer) is not None, "default defined at line 12942-12943")
        # sqlite3.c:17298-17302 -- "typedef i16 ynVar" under #if MAX<=32767, else "typedef int ynVar".
        check(re.search(r"\b(int|i32|int32|32-bit)\b", answer, re.I) is not None, "ynVar becomes int above 32767 (line 17301)")
        check(files == [], f"modified no files (changed: {files})")
    elif task["id"] == "T3-cross-codebase-inventory":
        truth = subprocess.run(
            ["grep", "-rhoE", r"class [A-Za-z_][A-Za-z0-9_]* (final )?: public (Core::)?IOptionsPage\b",
             "--include=*.h", "--include=*.cpp", "src"], cwd=repo, capture_output=True, text=True).stdout
        truth_names = {re.match(r"class (\S+)", l).group(1) for l in truth.splitlines() if l}
        path = os.path.join(repo, "options_pages.md")
        check(os.path.exists(path), "wrote options_pages.md")
        body = open(path, encoding="utf-8", errors="replace").read() if os.path.exists(path) else ""
        found = {n for n in truth_names if re.search(r"\b%s\b" % re.escape(n), body)}
        recall = len(found) / max(len(truth_names), 1)
        m = re.search(r"Total:\s*(\d+)", body)
        check(recall >= 0.9, f"lists {len(found)}/{len(truth_names)} true classes ({recall:.0%})")
        check(bool(m) and abs(int(m.group(1)) - len(truth_names)) <= 2,
              f"total line {m.group(1) if m else 'missing'} vs truth {len(truth_names)}")
        check(files == ["options_pages.md"], f"only options_pages.md added (changed: {files})")
    elif task["id"] == "T4-rename-with-trap":
        src = open(os.path.join(repo, "src/plugins/fakevim/fakevimhandler.cpp"), encoding="utf-8", errors="replace").read()
        check(len(re.findall(r"\bstartsWithWhitespace\(", src)) == 0, "no old name left in fakevimhandler.cpp")
        check(len(re.findall(r"\blineStartsWithWhitespace\(", src)) == 2, "new name at definition + its use (2)")
        check(files == ["src/plugins/fakevim/fakevimhandler.cpp"],
              f"only fakevimhandler.cpp changed, unrelated same-named function untouched (changed: {files})")
        stat = git(repo, "diff", "--numstat")
        check(stat.startswith("2\t2\t"), f"exactly two lines changed ({stat.strip() or 'none'})")
    elif task["id"] == "T5-fix-in-large-file":
        diff = git(repo, "diff", "-U0")
        check(files == ["src/plugins/fakevim/fakevimhandler.cpp"], f"only fakevimhandler.cpp changed (changed: {files})")
        src = open(os.path.join(repo, "src/plugins/fakevim/fakevimhandler.cpp"), encoding="utf-8", errors="replace").read()
        m = re.search(r"static QString getProcessOutput\([^)]*\)\s*\{(.*?)\n\}", src, re.S)
        body = m.group(1) if m else ""
        check(re.search(r"if\s*\(\s*!\s*proc\.waitForStarted\(", body) is not None, "checks waitForStarted() result")
        check(re.search(r"return\s+(QString\(\)|\{\}|QString\{\}|\"\")", body) is not None, "returns an empty QString on failure")
        check("fromLocalEncoding(proc.readAllStandardOutput())" in body, "normal path unchanged")
        hunks = diff.count("\n@@")
        check(1 <= hunks <= 2, f"change is local ({hunks} hunk(s))")
    return ok, notes


def run_task(task, args, key, label, results):
    run_dir = os.path.join(args.workdir, f"{task['id']}-{label}")
    shutil.rmtree(run_dir, ignore_errors=True)
    subprocess.run(["cp", "-a", args.repo, run_dir], check=True)
    home = os.path.join(args.workdir, f"home-{task['id']}-{label}")
    shutil.rmtree(home, ignore_errors=True)
    os.makedirs(os.path.join(home, ".qwen"))
    with open(os.path.join(home, ".qwen", "settings.json"), "w") as f:
        json.dump({
            "env": {"LOCAL_LLM_API_KEY": key},
            "modelProviders": {"openai": [{
                "id": args.model, "name": "[Local] stack",
                "baseUrl": args.base_url, "envKey": "LOCAL_LLM_API_KEY",
                "generationConfig": {"contextWindowSize": args.context},
            }]},
            "security": {"auth": {"selectedType": "openai"}},
            "privacy": {"usageStatisticsEnabled": False},
            "telemetry": {"enabled": False},
        }, f, indent=2)

    uid = f"{os.getuid()}:{os.getgid()}"
    host = re.sub(r"^https?://([^/:]+).*", r"\1", args.base_url)
    cmd = ["docker", "run", "--rm", "--label", "qwen-realworld=1", "--network", "host", "--user", uid,
           "--add-host", f"{host}:127.0.0.1",
           "-e", "HOME=/qwenhome", "-e", "NODE_EXTRA_CA_CERTS=/certs/tls.crt",
           "-v", f"{home}:/qwenhome", "-v", f"{args.ca}:/certs/tls.crt:ro",
           "-v", f"{args.qwen_home}:/opt/qwen-code:ro", "-v", f"{run_dir}:/work", "-w", "/work",
           "--entrypoint", "/opt/qwen-code/bin/qwen", args.image,
           "-m", args.model, "--auth-type", "openai", "--yolo", "-o", "json",
           "--max-wall-time", args.max_wall, "--max-tool-calls", str(args.max_tool_calls),
           task["prompt"]]
    t0 = time.time()
    proc = subprocess.run(cmd, capture_output=True, text=True)
    wall = time.time() - t0

    answer, stats = proc.stdout, {}
    try:
        parsed = json.loads(proc.stdout.strip().splitlines()[-1] if proc.stdout.strip().startswith("{") is False
                            else proc.stdout)
        if isinstance(parsed, list):
            parsed = next((x for x in reversed(parsed) if isinstance(x, dict) and x.get("type") == "result"), parsed[-1])
        answer = parsed.get("result") or parsed.get("response") or proc.stdout
        stats = {k: parsed.get(k) for k in ("num_turns", "duration_ms", "usage", "is_error", "stats") if k in parsed}
    except Exception:
        pass
    passed, notes = score(task, run_dir, answer if isinstance(answer, str) else json.dumps(answer))
    rec = {"task": task["id"], "user": label, "passed": passed, "wall_s": round(wall, 1),
           "exit_code": proc.returncode, "notes": notes, "stats": stats,
           "answer_tail": (answer if isinstance(answer, str) else json.dumps(answer))[-1500:],
           "stderr_tail": proc.stderr[-800:], "diff": git(run_dir, "diff")[:4000]}
    results.append(rec)
    print(f"\n[{task['id']} as {label}] {'PASSED' if passed else 'FAILED'} in {wall:.0f}s (exit {proc.returncode})", flush=True)
    for n in notes:
        print("   ", n, flush=True)
    return rec


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--repo", required=True, help="baseline git checkout; never modified")
    p.add_argument("--qwen-home", required=True, help="Qwen Code install dir (contains bin/qwen and node/)")
    p.add_argument("--base-url", default="https://gateway.llm.localhost/v1")
    p.add_argument("--ca", required=True, help="the stack's certificate (config/traefik/certs/tls.crt)")
    p.add_argument("--key-a", required=True)
    p.add_argument("--key-b", required=True, help="second person's key, for the concurrent tasks")
    p.add_argument("--model", required=True, help="MODEL_NAME from .env, e.g. Qwen3.8-Flash-Next")
    p.add_argument("--context", type=int, default=262144)
    p.add_argument("--image", default=IMAGE_DEFAULT)
    p.add_argument("--workdir", required=True)
    p.add_argument("--tasks", default="T1,T2,T3,T4+T5")
    p.add_argument("--max-wall", default="25m")
    p.add_argument("--max-tool-calls", type=int, default=120)
    p.add_argument("--json", default="")
    args = p.parse_args()
    args.repo, args.ca, args.qwen_home = map(os.path.abspath, (args.repo, args.ca, args.qwen_home))
    os.makedirs(args.workdir, exist_ok=True)

    by_id = {t["id"].split("-")[0]: t for t in TASKS}
    results = []
    for group in args.tasks.split(","):
        ids = group.split("+")
        if len(ids) == 1:
            run_task(by_id[ids[0]], args, args.key_a, "user-a", results)
        else:
            # Two developers at once, each with their own key and their own checkout.
            print(f"\n=== concurrently: {' and '.join(ids)} ===", flush=True)
            threads = [threading.Thread(target=run_task, args=(by_id[i], args, k, lbl, results))
                       for i, k, lbl in zip(ids, (args.key_a, args.key_b), ("user-a", "user-b"))]
            for t in threads:
                t.start()
            for t in threads:
                t.join()

    passed = sum(r["passed"] for r in results)
    print(f"\n=== {passed}/{len(results)} tasks passed ===")
    for r in results:
        print(f"  {r['task']:<30} {r['user']:<7} {'PASS' if r['passed'] else 'FAIL'}  {r['wall_s']:>6.0f}s")
    if args.json:
        with open(args.json, "w") as f:
            json.dump(results, f, indent=2)
    return 0 if passed == len(results) else 1


if __name__ == "__main__":
    sys.exit(main())
