#!/usr/bin/env python3
"""
What two people using the stack at the same time actually get.

scripts/benchmark.py sweeps concurrency against one key and reports aggregate
throughput. That answers "how much can the box push", not the question a team
asks: when my colleague is also using it, how much slower is MY answer, does a
long paste from them stall my chat, and does anything leak between us?

So this goes through the path people use -- Traefik, TLS, LiteLLM, each person's
own key -- and runs scenarios rather than a sweep:

  single      one person, the baseline everything else is compared to
  parallel    two people start at the same moment
  staggered   the second person arrives while the first is mid-answer
  longpaste   one person pastes a long document while the other chats
  overflow    three requests on two slots: the third must queue, not fail
  isolation   each person's prompt carries a secret; neither answer may
              contain the other's
  prefix      the same long prompt twice, then from the other person
  ramp        --ramp 1,2,4,8 (or 1..16, doubling): that many at once at each step; aggregate
              tok/s, time to first token p50/p95, and how long the first waited
  prefixshare --prefix-share N people, one long system prompt, each their own question:
              how much of each prompt the engine read from its cache
  returnafter --return-after S: a long conversation, then N others' long prompts and S
              seconds, then its next turn: what a CPU or LMCache tier keeps for it
  killengine  --kill-engine "CMD" run 2 s into a burst (docker restart vllm): failed
              requests, and the seconds until one is answered again

--compare BASELINE.json prints this run's ramp against a stored one (a JSON per run, to
compare settings: KV tier, TP=2x2 against TP=4, FP8 against NVFP4).

Per request it records time to first token, decode speed (tokens after the
first, over the time they took), and wall time. Standard library only.

    python3 scripts/multiuser-bench.py --base-url https://gateway.llm.localhost \\
        --key-a sk-... --key-b sk-... \\
        --model qwen3.8-flash-next --json results.json
"""

import argparse
import json
import subprocess
import ssl
import statistics
import sys
import threading
import time
import urllib.error
import urllib.request
import uuid

ESSAY = ("Write a long, detailed technical essay about how operating systems schedule "
         "threads across CPU cores. Cover run queues, priorities, preemption, cache "
         "affinity and NUMA. Keep writing; do not summarise or conclude early.")


def filler(n_words: int) -> str:
    """A long document that is not one repeated phrase, so it tokenises realistically."""
    words = []
    i = 0
    while len(words) < n_words:
        words.extend(f"Section {i}: the subsystem logs record {i * 7 % 101} events, "
                     f"status code {i * 13 % 997}, owner team-{i % 17}, region r{i % 5}, "
                     f"latency {i * 3 % 250} ms, checksum {(i * 2654435761) % 2**32:08x}.".split())
        i += 1
    return " ".join(words[:n_words])


class Stream:
    """One streaming chat completion, timed from the client side."""

    def __init__(self, label, base, key, model, prompt, max_tokens, ctx, temperature=0.7):
        self.label, self.base, self.key, self.model = label, base, key, model
        self.prompt, self.max_tokens, self.ctx, self.temperature = prompt, max_tokens, ctx, temperature
        self.t_start = self.t_first = self.t_last = None
        self.chunks = 0
        self.completion_tokens = None
        self.prompt_tokens = None
        self.cached_tokens = None
        self.text = ""
        self.error = ""
        self.status = None

    def run(self):
        body = json.dumps({
            "model": self.model,
            "messages": [{"role": "user", "content": self.prompt}],
            "max_tokens": self.max_tokens,
            "temperature": self.temperature,
            "stream": True,
            "stream_options": {"include_usage": True},
        }).encode()
        req = urllib.request.Request(
            self.base.rstrip("/") + "/v1/chat/completions", data=body, method="POST",
            headers={"Content-Type": "application/json", "Authorization": "Bearer " + self.key})
        self.t_start = time.perf_counter()
        try:
            with urllib.request.urlopen(req, timeout=1800, context=self.ctx) as resp:
                self.status = resp.status
                for raw in resp:
                    line = raw.decode("utf-8", "replace").strip()
                    if not line.startswith("data:"):
                        continue
                    data = line[5:].strip()
                    if data == "[DONE]":
                        break
                    try:
                        obj = json.loads(data)
                    except json.JSONDecodeError:
                        continue
                    usage = obj.get("usage")
                    if usage:
                        self.completion_tokens = usage.get("completion_tokens")
                        self.prompt_tokens = usage.get("prompt_tokens")
                        details = usage.get("prompt_tokens_details") or {}
                        self.cached_tokens = details.get("cached_tokens")
                    for ch in obj.get("choices") or []:
                        delta = ch.get("delta") or {}
                        piece = (delta.get("content") or "") + (delta.get("reasoning_content") or "")
                        if piece:
                            now = time.perf_counter()
                            if self.t_first is None:
                                self.t_first = now
                            self.t_last = now
                            self.chunks += 1
                            self.text += piece
        except urllib.error.HTTPError as exc:
            self.status = exc.code
            self.error = f"HTTP {exc.code}: {exc.read().decode()[:200]}"
        except Exception as exc:  # noqa: BLE001 -- a benchmark reports, it does not crash
            self.error = repr(exc)[:200]
        self.t_end = time.perf_counter()

    @property
    def ttft(self):
        return None if self.t_first is None else self.t_first - self.t_start

    @property
    def decode_tps(self):
        n = self.completion_tokens or self.chunks
        if self.t_first is None or self.t_last is None or n < 2 or self.t_last <= self.t_first:
            return None
        return (n - 1) / (self.t_last - self.t_first)

    def row(self):
        return {
            "label": self.label, "ok": not self.error and self.t_first is not None,
            "error": self.error, "ttft_s": self.ttft, "decode_tok_s": self.decode_tps,
            "wall_s": self.t_end - self.t_start, "completion_tokens": self.completion_tokens,
            "prompt_tokens": self.prompt_tokens, "cached_tokens": self.cached_tokens,
            "start_offset_s": getattr(self, "offset", 0.0),
        }


def launch(streams_with_delay):
    """Start each stream after its delay (seconds from a common zero) and wait for all."""
    zero = time.perf_counter()
    threads = []
    for s, delay in streams_with_delay:
        def go(s=s, delay=delay):
            wait = zero + delay - time.perf_counter()
            if wait > 0:
                time.sleep(wait)
            s.offset = time.perf_counter() - zero
            s.run()
        t = threading.Thread(target=go)
        t.start()
        threads.append(t)
    for t in threads:
        t.join()
    return [s for s, _ in streams_with_delay]


def fmt(v, unit="", nd=1):
    return "-" if v is None else f"{v:.{nd}f}{unit}"


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--base-url", required=True)
    p.add_argument("--key-a", required=True)
    p.add_argument("--key-b", required=True)
    p.add_argument("--model", required=True)
    p.add_argument("--ca", default="", help="CA bundle for a private TLS certificate")
    p.add_argument("--rounds", type=int, default=3)
    p.add_argument("--max-tokens", type=int, default=400)
    p.add_argument("--paste-words", type=int, default=12000,
                   help="size of the long document in 'longpaste' and 'prefix'")
    p.add_argument("--scenarios", default="single,parallel,staggered,longpaste,overflow,isolation,prefix")
    p.add_argument("--json", default="")
    p.add_argument("--ramp", default="1,2,4,8", help="concurrency steps: 1,2,4,8 or 1..16 (doubling)")
    p.add_argument("--prefix-share", type=int, default=8, help="people sharing one long system prompt")
    p.add_argument("--return-after", type=float, default=30, help="seconds before the conversation comes back")
    p.add_argument("--evict-with", type=int, default=6, help="others' long prompts sent meanwhile, to push it out of the GPU cache")
    p.add_argument("--kill-engine", default="", help="a command that restarts the engine (killengine scenario)")
    p.add_argument("--compare", default="", help="a baseline JSON of an earlier run")
    args = p.parse_args()

    ctx = ssl.create_default_context(cafile=args.ca) if args.ca else ssl._create_unverified_context()
    wanted = args.scenarios.split(",")
    results = {}

    def S(label, key, prompt, max_tokens=None, temperature=0.7):
        return Stream(label, args.base_url, key, args.model, prompt,
                      max_tokens or args.max_tokens, ctx, temperature)

    def report(name, streams):
        rows = [s.row() for s in streams]
        results.setdefault(name, []).extend(rows)
        for r in rows:
            print(f"  {r['label']:<22} start+{r['start_offset_s']:5.1f}s  ttft {fmt(r['ttft_s'], 's', 2):>7}  "
                  f"decode {fmt(r['decode_tok_s'], ' tok/s'):>11}  wall {fmt(r['wall_s'], 's'):>7}  "
                  f"out {r['completion_tokens']}  in {r['prompt_tokens']}"
                  f"{'  cached ' + str(r['cached_tokens']) if r['cached_tokens'] else ''}"
                  f"{'  ERROR ' + r['error'] if r['error'] else ''}", flush=True)
        return rows

    # A throwaway request so the first measured one does not pay for cold pages.
    print("warm-up ...", flush=True)
    launch([(S("warmup", args.key_a, "Say hello.", 16), 0)])

    if "single" in wanted:
        print("\n[single] one person, baseline")
        for i in range(args.rounds):
            report("single", launch([(S(f"A r{i}", args.key_a, ESSAY), 0)]))

    if "parallel" in wanted:
        print("\n[parallel] two people start together")
        for i in range(args.rounds):
            report("parallel", launch([(S(f"A r{i}", args.key_a, ESSAY), 0),
                                       (S(f"B r{i}", args.key_b, ESSAY), 0)]))

    if "staggered" in wanted:
        print("\n[staggered] B arrives 8 s into A's answer")
        for i in range(args.rounds):
            report("staggered", launch([(S(f"A r{i}", args.key_a, ESSAY), 0),
                                        (S(f"B r{i}", args.key_b, ESSAY), 8)]))

    if "longpaste" in wanted:
        print(f"\n[longpaste] A pastes ~{args.paste_words} words; B asks a short question 2 s later")
        doc = filler(args.paste_words)
        # A fresh nonce per run so the prefix cache cannot make the paste free.
        a = S("A paste", args.key_a, f"[{uuid.uuid4()}]\n{doc}\n\nSummarise the document above in five bullets.", 200)
        b = S("B chat", args.key_b, "In two sentences, what is a mutex?", 120)
        report("longpaste", launch([(a, 0), (b, 2)]))

    if "overflow" in wanted:
        print("\n[overflow] three requests on two slots")
        report("overflow", launch([(S("A #1", args.key_a, ESSAY, 200), 0),
                                   (S("B #2", args.key_b, ESSAY, 200), 0),
                                   (S("A #3 (must queue)", args.key_a, ESSAY, 200), 1)]))

    if "isolation" in wanted:
        print("\n[isolation] secrets must not cross between concurrent users")
        leaks = 0
        for i in range(args.rounds):
            sa, sb = f"ALPHA-{uuid.uuid4().hex[:10]}", f"BRAVO-{uuid.uuid4().hex[:10]}"
            ask = "Your secret code is {}. Reply with only the secret code, nothing else."
            a = S(f"A r{i}", args.key_a, ask.format(sa), 60, temperature=0)
            b = S(f"B r{i}", args.key_b, ask.format(sb), 60, temperature=0)
            report("isolation", launch([(a, 0), (b, 0)]))
            ok_a, ok_b = sa in a.text and sb not in a.text, sb in b.text and sa not in b.text
            leaked = (sb in a.text) or (sa in b.text)
            leaks += leaked
            print(f"    A got own secret: {sa in a.text}  B got own secret: {sb in b.text}  "
                  f"cross-leak: {leaked}")
            results.setdefault("isolation_checks", []).append(
                {"a_own": sa in a.text, "b_own": sb in b.text, "leak": leaked, "a_ok": ok_a, "b_ok": ok_b})
        print(f"    leaks across {args.rounds} rounds: {leaks}")

    if "prefix" in wanted:
        print(f"\n[prefix] the same ~{args.paste_words // 2}-word prompt: A cold, A again, then B")
        doc = f"[{uuid.uuid4()}]\n" + filler(args.paste_words // 2) + "\n\nWhich region appears most often? One word."
        for label, key in (("A cold", args.key_a), ("A repeat", args.key_a), ("B same prompt", args.key_b)):
            report("prefix", launch([(S(label, key, doc, 40, temperature=0), 0)]))

    def pct(vals, q):
        vals = sorted(v for v in vals if v is not None)
        return vals[min(len(vals) - 1, int(round(q * (len(vals) - 1))))] if vals else None

    if "ramp" in wanted:
        steps = []
        if ".." in args.ramp:
            lo, hi = (int(x) for x in args.ramp.split(".."))
            n = max(1, lo)
            while n <= hi:
                steps.append(n)
                n *= 2
        else:
            steps = [int(x) for x in args.ramp.split(",") if x.strip()]
        print(f"\n[ramp] {', '.join(map(str, steps))} at once")
        for n in steps:
            t0 = time.perf_counter()
            streams = launch([(S(f"{n}x #{i}", args.key_a if i % 2 == 0 else args.key_b, ESSAY), 0) for i in range(n)])
            wall = time.perf_counter() - t0
            rows = [s.row() for s in streams]
            ok = [r for r in rows if r["ok"]]
            tokens = sum((r["completion_tokens"] or 0) for r in ok)
            step = {"concurrency": n, "ok": len(ok), "failed": n - len(ok), "aggregate_tok_s": tokens / wall if wall else None,
                    "ttft_p50_s": pct([r["ttft_s"] for r in ok], 0.5), "ttft_p95_s": pct([r["ttft_s"] for r in ok], 0.95),
                    "queue_wait_s": max((r["ttft_s"] or 0) for r in ok) - min((r["ttft_s"] or 0) for r in ok) if ok else None}
            results.setdefault("ramp", []).append(step)
            results.setdefault("ramp_requests", []).extend(rows)
            print(f"  {n:>3} at once  {fmt(step['aggregate_tok_s'], ' tok/s'):>12}  ttft p50 {fmt(step['ttft_p50_s'], 's', 2)}  "
                  f"p95 {fmt(step['ttft_p95_s'], 's', 2)}  failed {step['failed']}", flush=True)

    if "prefixshare" in wanted:
        n = args.prefix_share
        system = f"[{uuid.uuid4()}] You answer questions about this reference.\n" + filler(args.paste_words // 2)
        print(f"\n[prefixshare] {n} people, one ~{args.paste_words // 2}-word system prompt")
        # One first, so the prefix is in the cache; then everyone at once.
        report("prefixshare", launch([(S("first", args.key_a, system + "\n\nQuestion 0: which region is first?", 40, 0), 0)]))
        rows = report("prefixshare", launch([(S(f"person {i}", args.key_a if i % 2 else args.key_b,
                                                  system + f"\n\nQuestion {i + 1}: which owner team appears in section {i}?", 40, 0), 0)
                                                for i in range(n)]))
        prompt = sum(r["prompt_tokens"] or 0 for r in rows)
        cached = sum(r["cached_tokens"] or 0 for r in rows)
        results["prefixshare_hit"] = cached / prompt if prompt else None
        print(f"    prompt tokens read from the cache: {fmt(100 * cached / prompt if prompt else None, '%', 0)}")

    if "returnafter" in wanted:
        print(f"\n[returnafter] a conversation comes back after {args.evict_with} others' long prompts and {args.return_after:.0f} s")
        doc = f"[{uuid.uuid4()}]\n" + filler(args.paste_words)
        report("returnafter", launch([(S("turn 1", args.key_a, doc + "\n\nWhich region appears most often?", 40, 0), 0)]))
        launch([(S(f"other {i}", args.key_b, f"[{uuid.uuid4()}]\n" + filler(args.paste_words) + "\n\nOne word.", 8, 0), 0)
                for i in range(args.evict_with)])
        time.sleep(max(0.0, args.return_after))
        rows = report("returnafter", launch([(S("turn 2", args.key_a, doc + "\n\nWhich region appears most often?", 40, 0), 0)]))
        r = rows[0]
        results["returnafter_cached"] = (r["cached_tokens"] or 0) / r["prompt_tokens"] if r["prompt_tokens"] else None
        print(f"    on its return: {fmt(r['ttft_s'], 's', 2)} to the first token, "
              f"{fmt(100 * results['returnafter_cached'] if results['returnafter_cached'] is not None else None, '%', 0)} of its prompt from a cache")

    if "killengine" in wanted:
        if not args.kill_engine:
            print("\n[killengine] skipped: --kill-engine names no command")
        else:
            print(f"\n[killengine] a burst, and `{args.kill_engine}` 2 s into it")
            burst = [(S(f"burst #{i}", args.key_a if i % 2 else args.key_b, ESSAY, 120), 0) for i in range(4)]
            killer = threading.Thread(target=lambda: (time.sleep(2), subprocess.run(args.kill_engine, shell=True, timeout=600)))
            killer.start()
            rows = report("killengine_requests", launch(burst))
            killer.join()
            t0 = time.perf_counter()
            recovered = None
            while time.perf_counter() - t0 < 900:
                probe = S("probe", args.key_a, "Say hello.", 8)
                probe.run()
                if probe.t_first is not None and not probe.error:
                    recovered = time.perf_counter() - t0
                    break
                time.sleep(1)
            results["killengine"] = {"failed": sum(1 for r in rows if not r["ok"]), "recovered_s": recovered}
            print(f"    failed during the restart: {results['killengine']['failed']}, answering again after {fmt(recovered, 's', 1)}")

    if args.compare and "ramp" in results:
        try:
            with open(args.compare) as f:
                base = {s["concurrency"]: s for s in json.load(f).get("ramp", [])}
        except (OSError, json.JSONDecodeError) as e:
            base = {}
            print(f"  (no baseline: {e})")
        if base:
            print("\n=== against the baseline ===")
            for s in results["ramp"]:
                b = base.get(s["concurrency"])
                if b and b.get("aggregate_tok_s") and s.get("aggregate_tok_s"):
                    print(f"  {s['concurrency']:>3} at once  {100 * s['aggregate_tok_s'] / b['aggregate_tok_s']:.0f}% of the baseline's tok/s, "
                          f"ttft p95 {fmt(s['ttft_p95_s'], 's', 2)} against {fmt(b.get('ttft_p95_s'), 's', 2)}")

    # Summary: the comparison people actually want.
    def med(name, field, label_prefix=""):
        vals = [r[field] for r in results.get(name, []) if r["ok"] and r[field] is not None
                and r["label"].startswith(label_prefix)]
        return statistics.median(vals) if vals else None

    print("\n=== summary (medians) ===")
    single_d, par_d = med("single", "decode_tok_s"), med("parallel", "decode_tok_s")
    print(f"  one person      decode {fmt(single_d, ' tok/s')}  ttft {fmt(med('single', 'ttft_s'), 's', 2)}")
    print(f"  two together    decode {fmt(par_d, ' tok/s')} each  ttft {fmt(med('parallel', 'ttft_s'), 's', 2)}")
    if single_d and par_d:
        print(f"  per-person speed kept: {100 * par_d / single_d:.0f}%   "
              f"combined output: {2 * par_d:.1f} tok/s ({2 * par_d / single_d:.2f}x one person)")
    # Requests failing while the engine restarts on purpose (killengine) are its measure, not errors.
    errors = [r for name, rows in results.items() if isinstance(rows, list) and name not in ("killengine", "killengine_requests") for r in rows
              if isinstance(r, dict) and r.get("error")]
    print(f"  failed requests: {len(errors)}")

    if args.json:
        with open(args.json, "w") as f:
            json.dump(results, f, indent=2)
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
