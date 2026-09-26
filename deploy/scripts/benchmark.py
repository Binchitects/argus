#!/usr/bin/env python3
"""
Load-test an OpenAI-compatible endpoint and report the metrics that matter for
serving: TTFT, inter-token latency, and aggregate throughput.

Standard library only, so it runs in a bare `python:3-slim` container with no
pip install step.

Why sweep concurrency? A single-request benchmark measures latency; it tells you
nothing about capacity. Throughput climbs with concurrency until the GPU
saturates, after which added concurrency only inflates queue time. The knee in
that curve is your real capacity number.

    python benchmark.py --base-url http://vllm:8000 --api-key sk-... \
        --concurrency 1,4,8,16 --requests 16
"""

import argparse
import json
import statistics
import sys
import threading
import time
import urllib.error
import urllib.request
from dataclasses import dataclass, field


@dataclass
class Result:
    ok: bool = False
    ttft: float = 0.0           # seconds until the first token arrived
    total: float = 0.0          # seconds until the stream closed
    output_tokens: int = 0
    error: str = ""
    token_times: list = field(default_factory=list)


def one_request(base_url, api_key, model, prompt, max_tokens) -> Result:
    """Issue one streaming completion, timing the first and last token."""
    payload = json.dumps({
        "model": model,
        "messages": [{"role": "user", "content": prompt}],
        "max_tokens": max_tokens,
        "temperature": 0.7,
        "stream": True,
        # Ask the server for usage numbers in the final SSE frame so we do not
        # have to estimate token counts by counting chunks.
        "stream_options": {"include_usage": True},
    }).encode()

    req = urllib.request.Request(
        f"{base_url.rstrip('/')}/v1/chat/completions",
        data=payload,
        headers={
            "Content-Type": "application/json",
            "Authorization": f"Bearer {api_key}",
        },
        method="POST",
    )

    res = Result()
    start = time.perf_counter()
    try:
        with urllib.request.urlopen(req, timeout=600) as resp:
            for raw in resp:
                line = raw.decode("utf-8", "replace").strip()
                if not line.startswith("data:"):
                    continue
                data = line[5:].strip()
                if data == "[DONE]":
                    break
                try:
                    chunk = json.loads(data)
                except json.JSONDecodeError:
                    continue

                usage = chunk.get("usage")
                if usage and usage.get("completion_tokens"):
                    res.output_tokens = usage["completion_tokens"]

                choices = chunk.get("choices") or []
                if choices:
                    delta = choices[0].get("delta") or {}
                    if delta.get("content"):
                        now = time.perf_counter()
                        if res.ttft == 0.0:
                            res.ttft = now - start
                        res.token_times.append(now)
        res.total = time.perf_counter() - start
        # Fall back to chunk count when the server omits usage.
        if not res.output_tokens:
            res.output_tokens = len(res.token_times)
        res.ok = res.output_tokens > 0
    except urllib.error.HTTPError as exc:
        res.error = f"HTTP {exc.code}: {exc.read()[:200].decode('utf-8', 'replace')}"
    except Exception as exc:  # noqa: BLE001 - report anything the run hits
        res.error = f"{type(exc).__name__}: {exc}"
    return res


def percentile(values, pct):
    if not values:
        return 0.0
    ordered = sorted(values)
    k = (len(ordered) - 1) * pct
    lo, hi = int(k), min(int(k) + 1, len(ordered) - 1)
    return ordered[lo] + (ordered[hi] - ordered[lo]) * (k - lo)


def run_level(concurrency, total_requests, **kw):
    results = []
    lock = threading.Lock()
    pending = list(range(total_requests))

    def worker():
        while True:
            with lock:
                if not pending:
                    return
                pending.pop()
            r = one_request(**kw)
            with lock:
                results.append(r)

    threads = [threading.Thread(target=worker, daemon=True) for _ in range(concurrency)]
    wall_start = time.perf_counter()
    for t in threads:
        t.start()
    for t in threads:
        t.join()
    wall = time.perf_counter() - wall_start
    return results, wall


def main():
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--base-url", default="http://vllm:8000")
    p.add_argument("--api-key", required=True)
    p.add_argument("--model", default="default")
    p.add_argument("--concurrency", default="1,4,8,16",
                   help="comma-separated concurrency levels to sweep")
    p.add_argument("--requests", type=int, default=16,
                   help="requests per concurrency level")
    p.add_argument("--max-tokens", type=int, default=256)
    p.add_argument("--prompt", default=None,
                   help="override the generated prompt")
    p.add_argument("--prompt-words", type=int, default=200,
                   help="approximate prompt length when --prompt is not given")
    args = p.parse_args()

    prompt = args.prompt or (
        "Summarise the following text in three sentences.\n\n"
        + ("The quick brown fox jumps over the lazy dog. " * (args.prompt_words // 9 + 1))
    )

    levels = [int(x) for x in args.concurrency.split(",") if x.strip()]

    print()
    print(f"  endpoint    {args.base_url}")
    print(f"  model       {args.model}")
    print(f"  requests    {args.requests} per level")
    print(f"  max_tokens  {args.max_tokens}")
    print()
    header = (f"  {'conc':>5} {'ok':>4} {'err':>4} {'req/s':>7} {'out tok/s':>10} "
              f"{'ttft p50':>9} {'ttft p95':>9} {'tpot p50':>9} {'e2e p95':>8}")
    print(header)
    print("  " + "-" * (len(header) - 2))

    summary = []
    for conc in levels:
        results, wall = run_level(
            conc, args.requests,
            base_url=args.base_url, api_key=args.api_key,
            model=args.model, prompt=prompt, max_tokens=args.max_tokens,
        )
        ok = [r for r in results if r.ok]
        errs = [r for r in results if not r.ok]

        if not ok:
            print(f"  {conc:>5} {0:>4} {len(errs):>4}   all requests failed")
            if errs:
                print(f"        first error: {errs[0].error}")
            continue

        ttfts = [r.ttft for r in ok]
        e2es = [r.total for r in ok]
        out_tokens = sum(r.output_tokens for r in ok)
        # Time per output token, excluding the prefill phase.
        tpots = [
            (r.total - r.ttft) / max(r.output_tokens - 1, 1)
            for r in ok if r.output_tokens > 1
        ]

        row = {
            "conc": conc,
            "ok": len(ok),
            "err": len(errs),
            "rps": len(ok) / wall,
            "tps": out_tokens / wall,
            "ttft_p50": percentile(ttfts, 0.50),
            "ttft_p95": percentile(ttfts, 0.95),
            "tpot_p50": percentile(tpots, 0.50) if tpots else 0.0,
            "e2e_p95": percentile(e2es, 0.95),
        }
        summary.append(row)
        print(f"  {row['conc']:>5} {row['ok']:>4} {row['err']:>4} {row['rps']:>7.2f} "
              f"{row['tps']:>10.1f} {row['ttft_p50']:>8.2f}s {row['ttft_p95']:>8.2f}s "
              f"{row['tpot_p50']*1000:>7.0f}ms {row['e2e_p95']:>7.2f}s")
        if errs:
            print(f"        first error: {errs[0].error}")

    if len(summary) > 1:
        best = max(summary, key=lambda r: r["tps"])
        print()
        print(f"  Peak generation throughput: {best['tps']:.1f} tok/s at concurrency {best['conc']}")
        print(f"  At that point p95 TTFT is {best['ttft_p95']:.2f}s — if that is too slow for")
        print("  your users, the usable capacity is the level below.")
    print()
    return 0


if __name__ == "__main__":
    sys.exit(main())
