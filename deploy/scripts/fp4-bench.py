#!/usr/bin/env python3
"""
FP4 against FP8 and BF16: each copy of a model served by the same engine on the same GPUs, measured the same way.
For each one it says the memory its weights and its token cache take, its decoding speed with one person and with many
at once (tokens a second, each and in all, and the time to the first token), and a small quality check.

Standard library only (and docker, for `run`). Nothing is fetched: the snapshots are in the library already.

    # Each model in a throwaway vLLM container of its own on the GPUs named, one after another (the stack's engines must
    # leave those GPUs free while it runs):
    python3 fp4-bench.py run --image arena-vllm --library /srv/models --gpus 0 \\
        --model bf16=hf/Qwen/Qwen3-8B@REV --model fp8=hf/Qwen/Qwen3-8B-FP8@REV --model nvfp4=hf/nvidia/Qwen3-8B-NVFP4@REV \\
        --out fp4-3090.json

    # Models the stack serves already (through the gateway, or an engine's own port):
    python3 fp4-bench.py endpoint --base-url https://gateway.example --api-key sk-... --model flash-fp8 --model flash-nvfp4

    python3 fp4-bench.py table fp4-3090.json      # the results as a table again

The quality check is 60 multi-step arithmetic questions made from a fixed seed, answered greedily with thinking off
(the same for every model, so an FP4 copy is held to its BF16 or FP8 one): the share it answers right, and the share of
answers that are the same as the first model's. It finds a quantisation that broke a model, not small differences in
knowledge: run the company's own evaluation (Admin -> Quality) for those.
"""
from __future__ import annotations

import argparse
import json
import random
import re
import statistics
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.request

PROMPT = ("You are helping an engineer review a design. " * 40).strip() + " Summarise the review in detail."


def post(base: str, key: str, body: dict, timeout: float = 600) -> urllib.request.addinfourl:
    req = urllib.request.Request(f"{base.rstrip('/')}/v1/chat/completions", data=json.dumps(body).encode(), method="POST",
                                 headers={"Content-Type": "application/json", **({"Authorization": f"Bearer {key}"} if key else {})})
    return urllib.request.urlopen(req, timeout=timeout)


def stream(base: str, key: str, model: str, max_tokens: int) -> dict:
    """One streamed answer of max_tokens tokens: its time to the first token, its tokens and its decoding time."""
    start = time.perf_counter()
    first = None
    tokens = 0
    body = {"model": model, "messages": [{"role": "user", "content": PROMPT}], "max_tokens": max_tokens, "temperature": 0, "stream": True,
            "ignore_eos": True, "stream_options": {"include_usage": True}, "chat_template_kwargs": {"enable_thinking": False}}
    try:
        with post(base, key, body) as r:
            for raw in r:
                line = raw.decode(errors="replace").strip()
                if not line.startswith("data:") or line == "data: [DONE]":
                    continue
                chunk = json.loads(line[5:])
                if chunk.get("usage"):
                    tokens = chunk["usage"].get("completion_tokens", tokens)
                delta = (chunk.get("choices") or [{}])[0].get("delta") or {}
                if first is None and (delta.get("content") or delta.get("reasoning_content") or delta.get("reasoning")):
                    first = time.perf_counter()
    except (OSError, urllib.error.URLError, json.JSONDecodeError) as e:
        return {"ok": False, "error": str(e)}
    end = time.perf_counter()
    first = first or end
    return {"ok": tokens > 0, "ttft": first - start, "decode": max(1e-6, end - first), "tokens": tokens, "total": end - start}


def sweep(base: str, key: str, model: str, levels: list[int], max_tokens: int) -> list[dict]:
    """Each level of people at once: twice that many answers, that many at a time."""
    out = []
    for c in levels:
        results: list[dict] = []
        lock = threading.Lock()
        todo = list(range(2 * c))

        def worker():
            while True:
                with lock:
                    if not todo:
                        return
                    todo.pop()
                r = stream(base, key, model, max_tokens)
                with lock:
                    results.append(r)

        start = time.perf_counter()
        threads = [threading.Thread(target=worker) for _ in range(c)]
        for t in threads:
            t.start()
        for t in threads:
            t.join()
        wall = time.perf_counter() - start
        ok = [r for r in results if r["ok"]]
        each = [r["tokens"] / r["decode"] for r in ok]
        out.append({
            "people": c, "answers": len(results), "failed": len(results) - len(ok),
            "each_tok_s": round(statistics.median(each), 1) if each else None,
            "all_tok_s": round(sum(r["tokens"] for r in ok) / wall, 1) if ok else None,
            "ttft_s": round(statistics.median(r["ttft"] for r in ok), 3) if ok else None,
        })
        print(f"  {model}: {c} at once: {out[-1]['each_tok_s']} tok/s each, {out[-1]['all_tok_s']} in all, first token {out[-1]['ttft_s']} s", flush=True)
    return out


def questions(n: int = 60, seed: int = 7) -> list[tuple[str, int]]:
    """Multi-step arithmetic word problems with their answers, the same for every model."""
    rng = random.Random(seed)
    out = []
    for _ in range(n):
        a, b, c, d = rng.randint(12, 97), rng.randint(3, 9), rng.randint(5, 60), rng.randint(2, 6)
        q = (f"A warehouse has {a} boxes. Each day for {b} days it receives {c} more boxes, then it ships out a {d}th of what it "
             f"holds, rounded down. How many boxes does it hold after it ships? Work it out, then end with 'Answer: N'.")
        held = a + b * c
        out.append((q, held - held // d))
    return out


def answer_of(text: str) -> int | None:
    m = re.findall(r"Answer:\s*\**\s*(-?\d[\d,]*)", text or "")
    nums = m or re.findall(r"-?\d[\d,]*", text or "")
    try:
        return int(nums[-1].replace(",", "")) if nums else None
    except ValueError:
        return None


def quality(base: str, key: str, model: str, reasoning_tokens: int) -> dict:
    asked = questions()
    answers = []
    for q, _ in asked:
        body = {"model": model, "messages": [{"role": "user", "content": q}], "max_tokens": 384 + reasoning_tokens, "temperature": 0,
                "chat_template_kwargs": {"enable_thinking": False}, "reasoning_effort": "low"}
        try:
            with post(base, key, body) as r:
                msg = json.load(r)["choices"][0]["message"]
            answers.append(answer_of(msg.get("content") or ""))
        except (OSError, urllib.error.URLError, json.JSONDecodeError, KeyError, IndexError):
            answers.append(None)
    right = sum(1 for a, (_, want) in zip(answers, asked) if a == want)
    print(f"  {model}: {right} of {len(asked)} right", flush=True)
    return {"right": right, "of": len(asked), "answers": answers}


def nvidia_used(gpus: str) -> dict[str, int]:
    try:
        out = subprocess.run(["nvidia-smi", "--query-gpu=index,memory.used", "--format=csv,noheader,nounits"], capture_output=True, text=True, timeout=20).stdout
    except (OSError, subprocess.TimeoutExpired):
        return {}
    want = set(gpus.split(","))
    return {i.strip(): int(m) for i, m in (line.split(",") for line in out.strip().splitlines()) if i.strip() in want}


def logged(log: str) -> dict:
    """What vLLM said of its memory as it started: its weights and the room for its token cache, in GiB."""
    weights = re.findall(r"Model loading took ([\d.]+) GiB", log)
    cache = re.findall(r"Available KV cache memory: ([\d.]+) GiB", log)
    tokens = re.findall(r"GPU KV cache size: ([\d,]+) tokens", log)
    quant = re.findall(r"(Using [A-Za-z0-9_ ]*(?:Marlin|FlashInfer|CUTLASS|Triton)[^\n]*)", log)
    return {"weights_gib": float(weights[-1]) if weights else None, "cache_gib": float(cache[-1]) if cache else None,
            "cache_tokens": int(tokens[-1].replace(",", "")) if tokens else None, "kernels": sorted(set(q.strip()[:160] for q in quant))[:6]}


def run_one(a, name: str, path: str, port: int) -> dict:
    container = f"fp4-bench-{name}"
    subprocess.run(["docker", "rm", "-f", container], capture_output=True)
    cmd = ["docker", "run", "-d", "--name", container, "--gpus", f"\"device={a.gpus}\"", "--ipc", "host", "-p", f"127.0.0.1:{port}:8000",
           "-v", f"{a.library}:/library:ro", "-e", "HF_HUB_OFFLINE=1", "--entrypoint", "vllm", a.image,
           "serve", f"/library/{path}", "--served-model-name", name, "--max-model-len", str(a.context), "--tensor-parallel-size", str(len(a.gpus.split(","))),
           "--gpu-memory-utilization", str(a.share), "--max-num-seqs", str(max(a.levels)), *a.extra]
    print(f"{name}: {path}", flush=True)
    subprocess.run(cmd, check=True, capture_output=True)
    base = f"http://127.0.0.1:{port}"
    try:
        deadline = time.time() + a.load_timeout
        while time.time() < deadline:
            state = subprocess.run(["docker", "inspect", "-f", "{{.State.Running}}", container], capture_output=True, text=True).stdout.strip()
            if state != "true":
                raise RuntimeError(f"{name} stopped as it loaded: " + subprocess.run(["docker", "logs", "--tail", "30", container], capture_output=True, text=True).stderr[-3000:])
            try:
                with urllib.request.urlopen(base + "/health", timeout=3):
                    break
            except OSError:
                time.sleep(3)
        else:
            raise RuntimeError(f"{name} did not answer in {a.load_timeout} s")
        log = subprocess.run(["docker", "logs", container], capture_output=True, text=True)
        result = {"name": name, "path": path, "memory": logged(log.stdout + log.stderr), "gpu_used_mib": nvidia_used(a.gpus)}
        stream(base, "", name, 16)
        result["speed"] = sweep(base, "", name, a.levels, a.tokens)
        result["quality"] = quality(base, "", name, a.reasoning)
        return result
    finally:
        subprocess.run(["docker", "rm", "-f", container], capture_output=True)


def agreement(results: list[dict]) -> None:
    base = results[0].get("quality", {}).get("answers")
    for r in results:
        mine = r.get("quality", {}).get("answers")
        if base and mine:
            r["quality"]["same_as_first"] = sum(1 for x, y in zip(base, mine) if x == y) / len(base)


def table(results: list[dict]) -> str:
    rows = ["| copy | weights (GiB) | cache (GiB, tokens) | 1 at once (tok/s) | most at once (tok/s each / in all) | first token (s) | right | same as first |",
            "|---|---|---|---|---|---|---|---|"]
    for r in results:
        m, s, q = r.get("memory", {}), r.get("speed", []), r.get("quality", {})
        one, most = (s[0], s[-1]) if s else ({}, {})
        rows.append(f"| {r['name']} | {m.get('weights_gib') or '-'} | {m.get('cache_gib') or '-'}, {m.get('cache_tokens') or '-'} | {one.get('each_tok_s')} "
                    f"| {most.get('each_tok_s')} / {most.get('all_tok_s')} ({most.get('people')}) | {one.get('ttft_s')} | {q.get('right')}/{q.get('of')} "
                    f"| {round(100 * q['same_as_first']) if 'same_as_first' in q else '-'}% |")
    return "\n".join(rows)


def main(argv: list[str]) -> int:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest="how", required=True)
    for how in ("run", "endpoint"):
        s = sub.add_parser(how)
        s.add_argument("--model", action="append", required=True, help="run: NAME=PATH in the library; endpoint: the model's name")
        s.add_argument("--levels", default="1,8,32", help="people at once, comma separated")
        s.add_argument("--tokens", type=int, default=256, help="tokens each answer decodes")
        s.add_argument("--reasoning", type=int, default=0, help="more tokens for a model that reasons first (gpt-oss: 1024)")
        s.add_argument("--out", default=None)
        if how == "run":
            s.add_argument("--image", default="arena-vllm")
            s.add_argument("--library", required=True)
            s.add_argument("--gpus", default="0")
            s.add_argument("--context", type=int, default=8192)
            s.add_argument("--share", type=float, default=0.90)
            s.add_argument("--port", type=int, default=18080)
            s.add_argument("--load-timeout", type=int, default=1800)
            s.add_argument("--extra", nargs=argparse.REMAINDER, default=[], help="more `vllm serve` arguments, last")
        else:
            s.add_argument("--base-url", required=True)
            s.add_argument("--api-key", default="")
    t = sub.add_parser("table")
    t.add_argument("file")
    a = p.parse_args(argv)
    if a.how == "table":
        print(table(json.load(open(a.file))["results"]))
        return 0
    a.levels = [int(x) for x in a.levels.split(",")]
    results = []
    for spec in a.model:
        try:
            if a.how == "run":
                name, _, path = spec.partition("=")
                results.append(run_one(a, name, path, a.port))
            else:
                base = a.base_url
                stream(base, a.api_key, spec, 16)
                results.append({"name": spec, "speed": sweep(base, a.api_key, spec, a.levels, a.tokens), "quality": quality(base, a.api_key, spec, a.reasoning)})
        except (RuntimeError, subprocess.CalledProcessError) as e:
            print(f"{spec}: {e}", file=sys.stderr, flush=True)
            results.append({"name": spec.partition("=")[0], "error": str(e)[-2000:]})
    agreement([r for r in results if "quality" in r] or results)
    report = {"at": time.strftime("%Y-%m-%dT%H:%M:%S"), "how": a.how, "levels": a.levels, "tokens": a.tokens, "results": results}
    if a.out:
        with open(a.out, "w") as f:
            json.dump(report, f, indent=2)
    print(table(results))
    return 0 if all("error" not in r for r in results) else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
