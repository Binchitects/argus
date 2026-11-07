"""Code Arena's context and tool calls over a long session, with a real model: before and after.

    before   results stay whole until the window fills (then cleared, then compacted); calls run as the model wrote them
    after    results the model has used go summarised before each step (a file read as its outline and the lines
             cited, an older read of a file read again dropped), kept whole for read_result; calls mended (a tool
             named as another harness names it, nearly-JSON arguments, arguments named or typed otherwise) or refused
             with the tool's parameters

The session is seven coding tasks on a small C# shop, one after another in one session (the fixture and the hidden
checks are tests/CodeArena.Tests/ContextBenchmarkTests.cs): a bug in a sum, a parser, a constant to find, one price in
a 900-line catalogue, a method to add, a report's order, a rename across files. After each task its check builds the
shop's code beside a program of its own and runs it: pass or fail. Recorded per task: prompt tokens (cached apart),
completion tokens, requests, seconds; per arm: the tool calls and their faults (unknown tool, bad JSON, bad arguments,
edits that missed, other errors), those mended, and the same failed call made again (retries).

The model runs in a throwaway llama.cpp container on the GPU (the image the stack uses, already on the host); the
benchmark runs in the .NET SDK's container (as tools/dn does), told the model's address. With --free-gpu the containers
named are stopped first and started again at the end (docker start: never removed), and the chat engine's /health is
checked. Nothing is downloaded.

    python3 evals/run_context_bench.py --model ~/llm-models/image/flux2-klein-4b/Qwen3-4B-Q4_K_M.gguf --alias Qwen3-4B-Q4_K_M \\
        --free-gpu llamacpp,vllm,videogen,imagegen --runs 1

Results: evals/context-bench-results.json (every run, and the summary) and the table for docs/measurements/context-condensing.md.
"""
from __future__ import annotations

import argparse
import json
import os
import pathlib
import subprocess
import sys
import time
import urllib.error
import urllib.request

ROOT = pathlib.Path(__file__).resolve().parent.parent
OUT = ROOT / "evals" / "context-bench-results.json"
LLAMA_IMAGE = "ghcr.io/ggml-org/llama.cpp:server-cuda"
SDK_IMAGE = "mcr.microsoft.com/dotnet/sdk:10.0"
TEST = "FullyQualifiedName~CodeArena.Tests.ContextBenchmarkTests.A_long_session_of_coding_tasks_before_and_after_condensing_and_mending"
FAULTS = ("unknownTool", "badJson", "badArguments", "editMisses", "otherErrors")


def llama_command(model: pathlib.Path, alias: str, context: int, port: int, name: str, image: str = LLAMA_IMAGE) -> list[str]:
    """The throwaway engine: the model's folder read-only, one slot of the whole context, tools through the chat template."""
    return [
        "docker", "run", "-d", "--rm", "--name", name, "--gpus", "all", "-p", f"127.0.0.1:{port}:8080",
        "-v", f"{model.parent}:/models:ro", image,
        "-m", f"/models/{model.name}", "--alias", alias, "-c", str(context), "-np", "1", "-ngl", "999",
        "--jinja", "--flash-attn", "on", "--host", "0.0.0.0", "--port", "8080",
    ]


def bench_command(url: str, alias: str, context: int, arms: list[str], runs: int, tasks: list[str], out: str,
                  uid: int, gid: int, cache: pathlib.Path, image: str = SDK_IMAGE) -> list[str]:
    """The benchmark in the SDK's container, as tools/dn runs tests, with the model's address and the arms."""
    env = {
        "CODE_ARENA_BENCH_URL": url, "CODE_ARENA_BENCH_MODEL": alias, "CODE_ARENA_BENCH_CONTEXT": str(context),
        "CODE_ARENA_BENCH_ARMS": ",".join(arms), "CODE_ARENA_BENCH_RUNS": str(runs), "CODE_ARENA_BENCH_TASKS": ",".join(tasks),
        "CODE_ARENA_BENCH_OUT": out,
    }
    command = ["docker", "run", "--rm", "-u", f"{uid}:{gid}", "-e", "HOME=/home/dev", "-e", "DOTNET_CLI_TELEMETRY_OPTOUT=1", "-e", "DOTNET_NOLOGO=1"]
    for key, value in env.items():
        command += ["-e", f"{key}={value}"]
    return command + [
        "-v", f"{cache}:/home/dev", "-v", f"{ROOT}:/repo", "-w", "/repo", "--network", "host", image,
        "dotnet", "test", "tests/CodeArena.Tests/CodeArena.Tests.csproj", "-c", "Release", "--filter", TEST,
        "--logger", "console;verbosity=detailed",
    ]


def summarize(report: dict) -> dict:
    """Per arm (its runs together): tasks passed, tokens and seconds per task, and the tool calls' faults as rates."""
    arms: dict[str, dict] = {}
    for run in report.get("arms", []):
        arm = arms.setdefault(run["arm"], {"runs": 0, "tasks": 0, "passed": 0, "prompt": 0, "cached": 0, "completion": 0,
                                           "requests": 0, "seconds": 0.0, "calls": {k: 0 for k in ("calls", "mended", "retries", *FAULTS)},
                                           "condensed": {"summarised": 0, "outlined": 0, "dropped": 0, "freedChars": 0}, "byTask": {}})
        arm["runs"] += 1
        for task in run["tasks"]:
            arm["tasks"] += 1
            arm["passed"] += 1 if task["passed"] else 0
            for key in ("prompt", "cached", "completion", "requests"):
                arm[key] += int(task[key])
            arm["seconds"] += float(task["seconds"])
            per = arm["byTask"].setdefault(task["task"], {"passed": 0, "runs": 0, "prompt": 0})
            per["runs"] += 1
            per["passed"] += 1 if task["passed"] else 0
            per["prompt"] += int(task["prompt"])
        for key in arm["calls"]:
            arm["calls"][key] += int(run["calls"].get(key, 0))
        for key in arm["condensed"]:
            arm["condensed"][key] += int(run["calls"].get("condensed", {}).get(key, 0))
    for arm in arms.values():
        n = max(1, arm["tasks"])
        calls = max(1, arm["calls"]["calls"])
        faults = sum(arm["calls"][k] for k in FAULTS)
        arm["perTask"] = {
            "prompt": round(arm["prompt"] / n), "uncached": round((arm["prompt"] - arm["cached"]) / n),
            "completion": round(arm["completion"] / n), "requests": round(arm["requests"] / n, 1), "seconds": round(arm["seconds"] / n, 1),
        }
        arm["passRate"] = round(arm["passed"] / n, 3)
        arm["errorRate"] = round(faults / calls, 3)
        arm["callFaultRate"] = round(sum(arm["calls"][k] for k in ("unknownTool", "badJson", "badArguments")) / calls, 3)
        for per in arm["byTask"].values():
            per["prompt"] = round(per["prompt"] / max(1, per["runs"]))
    return arms


def change(before: float, after: float) -> str:
    """after against before, as a signed share (−38%); "—" when before is 0."""
    if not before:
        return "—"
    return f"{(after - before) / before * 100:+.0f}%".replace("-", "−")


def table(summary: dict, model: str) -> str:
    """The numbers as docs/measurements has them: per arm, then per task's prompt tokens, then the calls."""
    lines = [f"Model: {model}", "",
             "| arm | passed | prompt tokens / task (uncached) | completion / task | requests / task | seconds / task | tool-call error rate | call faults | mended | retries |",
             "|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|"]
    for name, arm in summary.items():
        p, c = arm["perTask"], arm["calls"]
        lines.append(f"| {name} | {arm['passed']} / {arm['tasks']} | {p['prompt']:,} ({p['uncached']:,}) | {p['completion']:,} | {p['requests']} | {p['seconds']} | "
                     f"{arm['errorRate']:.1%} | {arm['callFaultRate']:.1%} | {c['mended']} | {c['retries']} |")
    if "before" in summary and "after" in summary:
        b, a = summary["before"], summary["after"]
        lines += ["", f"After against before: prompt tokens per task {change(b['perTask']['prompt'], a['perTask']['prompt'])}, "
                      f"uncached {change(b['perTask']['uncached'], a['perTask']['uncached'])}, seconds {change(b['perTask']['seconds'], a['perTask']['seconds'])}; "
                      f"passed {b['passed']} → {a['passed']} of {a['tasks']}; tool-call error rate {b['errorRate']:.1%} → {a['errorRate']:.1%}."]
        lines += ["", "| task | before: passed, prompt tokens | after: passed, prompt tokens |", "|---|---:|---:|"]
        for task, bt in b["byTask"].items():
            at = a["byTask"].get(task, {"passed": 0, "runs": 0, "prompt": 0})
            lines.append(f"| {task} | {bt['passed']} / {bt['runs']}, {bt['prompt']:,} | {at['passed']} / {at['runs']}, {at['prompt']:,} |")
    lines += ["", "| arm | calls | unknown tool | bad JSON | bad arguments | edit missed | other errors | summarised | outlined | dropped |", "|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|"]
    for name, arm in summary.items():
        c, d = arm["calls"], arm["condensed"]
        lines.append(f"| {name} | {c['calls']} | {c['unknownTool']} | {c['badJson']} | {c['badArguments']} | {c['editMisses']} | {c['otherErrors']} | "
                     f"{d['summarised']} | {d['outlined']} | {d['dropped']} |")
    return "\n".join(lines)


def healthy(url: str, timeout: float = 600) -> bool:
    """Waits for an engine's /health to answer 200."""
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        try:
            with urllib.request.urlopen(url + "/health", timeout=5) as res:
                if res.status == 200:
                    return True
        except (urllib.error.URLError, OSError):
            pass
        time.sleep(2)
    return False


def running(names: list[str]) -> list[str]:
    """Of the containers named, those running now (only those are stopped, and started again)."""
    out = subprocess.run(["docker", "ps", "--format", "{{.Names}}"], capture_output=True, text=True, check=True).stdout.split()
    return [n for n in names if n in out]


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--model", required=True, type=pathlib.Path, help="the GGUF file")
    parser.add_argument("--alias", required=True, help="the model's name in the results")
    parser.add_argument("--context", type=int, default=32768)
    parser.add_argument("--arms", default="before,after")
    parser.add_argument("--runs", type=int, default=1)
    parser.add_argument("--tasks", default="", help="only these tasks (ids, comma-separated)")
    parser.add_argument("--port", type=int, default=18080)
    parser.add_argument("--free-gpu", default="", help="containers to stop first and start again after (docker start)")
    parser.add_argument("--health", default="", help="an engine to check answers /health after they start again (http://host:port)")
    parser.add_argument("--out", type=pathlib.Path, default=OUT)
    args = parser.parse_args(argv)

    name = "code-arena-bench-llama"
    stopped = running([n for n in args.free_gpu.split(",") if n])
    cache = pathlib.Path(os.environ.get("DN_CACHE", pathlib.Path.home() / ".cache" / "llm-app-dotnet"))
    raw = ROOT / "evals" / ".context-bench-run.json"
    started = time.monotonic()
    try:
        if stopped:
            print(f"Freeing the GPU: stopping {', '.join(stopped)}", flush=True)
            subprocess.run(["docker", "stop", *stopped], check=True, capture_output=True)
        subprocess.run(llama_command(args.model.expanduser(), args.alias, args.context, args.port, name), check=True, capture_output=True)
        url = f"http://127.0.0.1:{args.port}"
        if not healthy(url):
            print("The engine did not come up:", subprocess.run(["docker", "logs", "--tail", "40", name], capture_output=True, text=True).stderr, file=sys.stderr)
            return 1
        command = bench_command(url, args.alias, args.context, [a for a in args.arms.split(",") if a], args.runs, [t for t in args.tasks.split(",") if t],
                                "/repo/evals/" + raw.name, os.getuid(), os.getgid(), cache)
        result = subprocess.run(command, text=True, capture_output=True)
        sys.stdout.write("\n".join(l for l in result.stdout.splitlines() if " #" in l or "Passed!" in l or "Failed!" in l) + "\n")
        if result.returncode != 0 or not raw.exists():
            sys.stderr.write(result.stdout[-4000:] + result.stderr[-2000:])
            return 1
    finally:
        subprocess.run(["docker", "stop", name], capture_output=True)
        if stopped:
            subprocess.run(["docker", "start", *stopped], check=False, capture_output=True)
            print(f"Started again: {', '.join(stopped)} after {time.monotonic() - started:.0f} s", flush=True)
            if args.health:
                print("Health:", "ok" if healthy(args.health, 900) else "NOT answering", flush=True)
    report = json.loads(raw.read_text())
    raw.unlink()
    summary = summarize(report)
    everything = json.loads(args.out.read_text()) if args.out.exists() else {"runs": []}
    everything["runs"].append({**report, "summary": summary})
    args.out.write_text(json.dumps(everything, indent=2) + "\n")
    print(table(summary, args.alias))
    return 0


if __name__ == "__main__":
    sys.exit(main())
