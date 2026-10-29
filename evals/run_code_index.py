"""Code Arena with its code index, or with grep alone: the same tasks, the same model, measured.

    grep     Code Arena as it is (codeIndex off): grep, glob, read_file, git and run_shell
    index    the same, with code_search, code_symbol and code_outline (codeIndex on)
    index-1  code_search alone: one broad tool

The tasks come from a repository's own history and text, never from whoever
writes them (evals/README.md), and are made before any model runs:

* **locate**: real commits that touch 1 to 4 non-test source files. The
  repository is checked out at the commit's parent, and the agent is given the
  commit's subject: "name the files and functions you would change; change
  nothing". Graded on the share of the changed files it names, and on whether
  the ranges it cites (path:a-b) meet the commit's hunks.
* **string**: a message that appears exactly once in the code ("where is this
  raised?"): grep finds it at once, and the index must not do worse. Graded on
  naming the file and a line within two of it.

Each run is `code-arena -p --output json` in a home of its own (CODE_ARENA_HOME),
as a throwaway person made through the admin API (its API credit set for the
run), with its key in that home's config (0600), never printed; the person is
removed at the end, whatever happens. The admin password is read from
ARENA_ADMIN_PASSWORD, else from deploy/.env (ADMIN_PASSWORD), and never printed. Recorded per run: pass or fail, the prompt (cached
apart) and completion tokens, the model's rounds, each tool called and the
characters of their results, the seconds. Results: evals/code-index-results.json
and docs/measurements/code-index.md.

    python3 evals/run_code_index.py tasks                 # make the tasks (evals/code-index-tasks.json)
    python3 evals/run_code_index.py tasks --check         # make them again: they must come out the same
    python3 evals/run_code_index.py run --program ./code-arena --arms grep --models Qwen3-4B-Q4_K_M

The corpus is a clone of this repository under ~/measure-sets/code-index/ (made
from the local checkout, so nothing is downloaded and the working tree is never
touched).
"""
from __future__ import annotations

import argparse
import hashlib
import http.cookiejar
import json
import os
import pathlib
import random
import re
import shutil
import ssl
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
import uuid

ROOT = pathlib.Path(__file__).resolve().parent.parent
TASKS = ROOT / "evals" / "code-index-tasks.json"
OUT = ROOT / "evals" / "code-index-results.json"
SETS = pathlib.Path.home() / "measure-sets" / "code-index"
SOURCE = (".cs", ".ts", ".tsx", ".js", ".py")
SEED = 20261030
LOCATE = 60
STRINGS = 20
TIMEOUT = 900


def git(repo: pathlib.Path, *args: str) -> str:
    return subprocess.run(["git", "-C", str(repo), *args], check=True, capture_output=True, text=True).stdout


def is_test(path: str) -> bool:
    name = path.rsplit("/", 1)[-1]
    return (path.startswith("tests/") or "/tests/" in path or name.endswith(("Tests.cs", ".test.ts", ".test.tsx", "_test.go"))
            or name.startswith("test_") or "/e2e/" in path)


def is_generated(path: str) -> bool:
    return path.endswith((".Designer.cs", ".g.cs")) or "/Migrations/" in path or path.endswith("ModelSnapshot.cs")


def corpus(name: str = "llm-service") -> pathlib.Path:
    """The clone the agent works in: made once from this checkout, then reset before each task."""
    path = SETS / name
    if not (path / ".git").exists():
        SETS.mkdir(parents=True, exist_ok=True)
        subprocess.run(["git", "clone", "--quiet", "--no-hardlinks", str(ROOT), str(path)], check=True)
    return path


def hunks(repo: pathlib.Path, commit: str) -> dict[str, list[list[int]]]:
    """The lines a commit changed, in its parent's numbering: where the agent should point."""
    out: dict[str, list[list[int]]] = {}
    current = None
    for line in git(repo, "diff", "--unified=0", f"{commit}^", commit).splitlines():
        if line.startswith("--- "):
            current = line[6:] if line.startswith("--- a/") else None
        elif line.startswith("@@") and current:
            m = re.match(r"@@ -(\d+)(?:,(\d+))?", line)
            if m:
                start, count = int(m.group(1)), int(m.group(2) or "1")
                out.setdefault(current, []).append([start, start + max(count, 1) - 1])
    return out


def locate_tasks(repo: pathlib.Path, rng: random.Random) -> list[dict]:
    candidates = []
    for row in git(repo, "log", "--no-merges", "--format=%H%x09%s", "-n", "3000").splitlines():
        commit, subject = row.split("\t", 1)
        try:
            changed = [p for p in git(repo, "diff", "--name-only", f"{commit}^", commit).splitlines() if p]
        except subprocess.CalledProcessError:
            continue
        source = [p for p in changed if p.endswith(SOURCE) and not is_test(p) and not is_generated(p)]
        # A focused change: 1 to 4 source files, and nothing else but tests and docs.
        if not 1 <= len(source) <= 4 or len(subject) < 25:
            continue
        if any(not (p.endswith(SOURCE) or p.endswith(".md")) for p in changed if p not in source and not is_test(p)):
            continue
        where = {p: r for p, r in hunks(repo, commit).items() if p in source}
        if not where:
            continue
        candidates.append({"family": "locate", "commit": commit, "at": f"{commit}^", "files": source, "hunks": where,
                           "prompt": f"{subject}\n\nName the files and the functions you would change for this, each with its path, and why. Change nothing."})
    rng.shuffle(candidates)
    return candidates[:LOCATE]


MESSAGE = re.compile(r'"([A-Z][^"\\\n{}]{24,90}[.:!?])"')


def string_tasks(repo: pathlib.Path, rng: random.Random) -> list[dict]:
    found: dict[str, list[tuple[str, int]]] = {}
    for path in git(repo, "ls-files").splitlines():
        if not path.endswith((".cs", ".ts", ".tsx")) or is_test(path) or is_generated(path):
            continue
        try:
            text = (repo / path).read_text(encoding="utf-8")
        except (OSError, UnicodeDecodeError):
            continue
        for number, line in enumerate(text.splitlines(), 1):
            for m in MESSAGE.finditer(line):
                found.setdefault(m.group(1), []).append((path, number))
    once = sorted((message, places[0]) for message, places in found.items() if len(places) == 1)
    rng.shuffle(once)
    return [{"family": "string", "at": "HEAD", "message": message, "file": path, "line": line,
             "prompt": f"Where in the code is this message written: \"{message}\"? Give its file and line. Change nothing."}
            for message, (path, line) in once[:STRINGS]]


def make_tasks(repo: pathlib.Path) -> dict:
    rng = random.Random(SEED)
    head = git(repo, "rev-parse", "HEAD").strip()
    tasks = locate_tasks(repo, rng) + string_tasks(repo, rng)
    for i, t in enumerate(tasks):
        t["id"] = f"{t['family']}-{i:03d}"
        # Half the tasks tune the index's weights, the other half judge it: by the parity of the commit's (or message's) hash.
        key = t.get("commit") or t["message"]
        t["split"] = "dev" if int(hashlib.sha256(key.encode()).hexdigest(), 16) % 2 == 0 else "test"
    return {"corpus": "llm-service", "head": head, "seed": SEED, "tasks": tasks}


PLACE = re.compile(r"([\w./@-]+\.(?:cs|tsx?|jsx?|py))(?::(\d+)(?:-(\d+))?)?")


def grade(task: dict, answer: str) -> dict:
    cited = [(m.group(1).lstrip("./"), int(m.group(2)) if m.group(2) else None, int(m.group(3) or m.group(2)) if m.group(2) else None)
             for m in PLACE.finditer(answer or "")]
    def names(path: str) -> bool:
        return any(c[0] == path or path.endswith("/" + c[0]) or c[0].endswith("/" + path) for c in cited)
    if task["family"] == "locate":
        recall = sum(1 for f in task["files"] if names(f)) / len(task["files"])
        meets = any(c[1] is not None and any(c[0] == f or f.endswith("/" + c[0]) for f in [path])
                    and any(c[1] <= b and a <= c[2] for a, b in ranges)
                    for c in cited for path, ranges in task["hunks"].items())
        return {"pass": recall >= 0.5, "recall": recall, "hunk": meets}
    near = any((c[0] == task["file"] or task["file"].endswith("/" + c[0])) and c[1] is not None and abs(c[1] - task["line"]) <= 2 for c in cited)
    return {"pass": near, "recall": 1.0 if names(task["file"]) else 0.0, "hunk": near}


class Arena:
    """The admin API, signed in (the password never printed)."""

    def __init__(self, base: str, verify: bool):
        self.base = base.rstrip("/")
        ctx = ssl.create_default_context()
        if not verify:
            # The local stack's own certificate.
            ctx.check_hostname = False
            ctx.verify_mode = ssl.CERT_NONE
        self.web = urllib.request.build_opener(urllib.request.HTTPSHandler(context=ctx), urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))

    def call(self, method: str, path: str, body=None):
        req = urllib.request.Request(self.base + path, method=method, data=None if body is None else json.dumps(body).encode(),
                                     headers={"Content-Type": "application/json", "X-Requested-With": "eval"})
        try:
            with self.web.open(req, timeout=60) as r:
                text = r.read().decode()
                return r.status, json.loads(text) if text else None
        except urllib.error.HTTPError as e:
            return e.code, e.read().decode()[:300]


def throwaway(base: str, verify: bool) -> tuple[Arena, dict]:
    """A person of the run's own, with API credit for it: its id and key."""
    env = dict(line.split("=", 1) for line in (ROOT / "deploy" / ".env").read_text().splitlines() if "=" in line and not line.startswith("#"))
    password = os.environ.get("ARENA_ADMIN_PASSWORD") or env.get("ADMIN_PASSWORD")
    if not password:
        raise SystemExit("No admin password: set ARENA_ADMIN_PASSWORD.")
    arena = Arena(base, verify)
    status, _ = arena.call("POST", "/api/auth/login", {"userName": env.get("ADMIN_USERNAME", "admin"), "password": password})
    if status != 200:
        raise SystemExit(f"The admin could not sign in ({status}).")
    name = "codeindex" + uuid.uuid4().hex[:6]
    status, made = arena.call("POST", "/api/admin/people", {"userName": name, "email": f"{name}@example.test"})
    if status >= 400:
        raise SystemExit(f"No throwaway person ({status}).")
    arena.call("PUT", f"/api/admin/people/{made['id']}/credits/api", {"Credit": 50})
    return arena, made


def home_for(arm: str, model: str, base: pathlib.Path, url: str, key: str) -> pathlib.Path:
    """A home of its own, with the run's person's key (never printed) and the arm's settings."""
    home = pathlib.Path(tempfile.mkdtemp(prefix=f"ci-{arm}-", dir=base))
    (home / "config").mkdir()
    config = {"url": url, "apiKey": key, "model": model, "codeIndex": arm != "grep", "builtInSkills": False, "projectMemory": False}
    mine = pathlib.Path.home() / ".config" / "code-arena" / "config.json"
    if mine.exists() and "ca" in (kept := json.loads(mine.read_text())):
        # The stack's own CA, as the person's Code Arena trusts it.
        config["ca"] = kept["ca"]
    if arm == "index-1":
        config["codeIndexTools"] = ["code_search"]
    path = home / "config" / "config.json"
    path.write_text(json.dumps(config))
    os.chmod(path, 0o600)
    return home


def run_one(program: str, repo: pathlib.Path, task: dict, arm: str, model: str, base: pathlib.Path, url: str, key: str) -> dict:
    subprocess.run(["git", "-C", str(repo), "checkout", "--quiet", "--force", task["at"]], check=True)
    subprocess.run(["git", "-C", str(repo), "clean", "-fdq"], check=True)
    home = home_for(arm, model, base, url, key)
    started = time.monotonic()
    try:
        done = subprocess.run([program, "-p", task["prompt"], "--output", "json", "--permission-mode", "plan"], cwd=repo,
                              env={**os.environ, "CODE_ARENA_HOME": str(home)}, capture_output=True, text=True, timeout=TIMEOUT)
        seconds = time.monotonic() - started
        try:
            result = json.loads(done.stdout.strip().splitlines()[-1])
        except (json.JSONDecodeError, IndexError):
            result = {"is_error": True, "error": (done.stderr or "no answer")[-400:]}
    except subprocess.TimeoutExpired:
        seconds, result = TIMEOUT, {"is_error": True, "error": "timed out"}
    finally:
        shutil.rmtree(home, ignore_errors=True)
    usage = result.get("usage") or {}
    return {"task": task["id"], "family": task["family"], "split": task["split"], "arm": arm, "model": model, "seconds": round(seconds, 1),
            "error": result.get("error") if result.get("is_error") else None, **grade(task, result.get("result") or ""),
            "prompt_tokens": usage.get("prompt_tokens"), "cached_tokens": usage.get("cached_tokens"), "completion_tokens": usage.get("completion_tokens"),
            "requests": usage.get("requests"), "tool_calls": usage.get("tool_calls"), "tool_chars": usage.get("tool_chars")}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n", 1)[0])
    sub = parser.add_subparsers(dest="command", required=True)
    tasks = sub.add_parser("tasks", help="make the tasks from the corpus's history")
    tasks.add_argument("--check", action="store_true", help="make them again and compare with the file")
    run = sub.add_parser("run", help="run the tasks")
    run.add_argument("--program", required=True, help="the code-arena program to run")
    run.add_argument("--arms", default="grep,index", help="grep, index, index-1")
    run.add_argument("--models", required=True, help="models, comma separated")
    run.add_argument("--runs", type=int, default=3, help="runs of each task per arm and model")
    run.add_argument("--family", default="", help="only this family")
    run.add_argument("--limit", type=int, default=0, help="only the first N tasks (a smoke run)")
    run.add_argument("--url", default="https://llm.localhost", help="Arena's address")
    run.add_argument("--verify-tls", action="store_true", help="check the stack's certificate")
    args = parser.parse_args()

    repo = corpus()
    if args.command == "tasks":
        git(repo, "fetch", "--quiet", str(ROOT), "HEAD")
        git(repo, "checkout", "--quiet", "--force", "FETCH_HEAD")
        made = make_tasks(repo)
        if args.check:
            kept = json.loads(TASKS.read_text())
            again = make_tasks(repo) if kept["head"] == made["head"] else None
            same = again is not None and again["tasks"] == kept["tasks"] and made["tasks"] == kept["tasks"]
            print("tasks: the same" if same else "tasks: they differ (or the corpus moved: make them again)")
            return 0 if same else 1
        TASKS.write_text(json.dumps(made, indent=1) + "\n")
        print(f"tasks: {len(made['tasks'])} ({sum(t['family'] == 'locate' for t in made['tasks'])} locate, "
              f"{sum(t['family'] == 'string' for t in made['tasks'])} string) at {made['head'][:10]}")
        return 0

    kept = json.loads(TASKS.read_text())
    chosen = [t for t in kept["tasks"] if not args.family or t["family"] == args.family]
    if args.limit:
        chosen = chosen[:args.limit]
    results = json.loads(OUT.read_text()) if OUT.exists() else {"runs": []}
    base = pathlib.Path(tempfile.mkdtemp(prefix="code-index-"))
    arena, person = throwaway(args.url, args.verify_tls)
    try:
        for model in args.models.split(","):
            for arm in args.arms.split(","):
                for task in chosen:
                    for n in range(args.runs):
                        row = run_one(args.program, repo, task, arm, model, base, args.url, person["apiKey"])
                        row["run"] = n
                        results["runs"].append(row)
                        print(f"{model} {arm} {task['id']} #{n}: {'pass' if row['pass'] else 'fail'} · {row['prompt_tokens']} in · "
                              f"{row['requests']} rounds · {row['tool_calls']} · {row['seconds']}s", flush=True)
                        OUT.write_text(json.dumps(results, indent=1) + "\n")
    finally:
        arena.call("DELETE", f"/api/admin/people/{person['id']}")
        shutil.rmtree(base, ignore_errors=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
