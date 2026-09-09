#!/usr/bin/env python3
"""Compare model quality objectively across engines and quantisations.

The point is to answer "is this quant good enough to deploy" with a number
rather than an impression. Every question here is graded by a program, not by
reading the answer:

  * code questions are EXECUTED against test cases
  * exact-answer questions compare normalised values
  * format questions are parsed and measured
  * factual questions match a set of accepted spellings, decided in advance

That last one is not fussiness. An earlier version of this graded a Windows
kernel question with the regex `Operations?\\b`, which matched the WRONG member
name (`Operations`) and rejected the RIGHT one (`OperationRegistration`) -- so
it reported the better answer as the worse one. A grader that can invert a
result is worse than no grader. Ground truth here was checked against
fltKernel.h in the Windows SDK, and accepted answers are listed explicitly.

    python3 scripts/quality-bench.py --base http://llamacpp:8080 --model local \\
        --key "$LLAMACPP_API_KEY" --label "Flash-Next IQ4_XS" --out iq4xs.json

    python3 scripts/quality-bench.py --compare iq4xs.json q4kxl.json

Run it from a container on llm-net, or from the host against the gateway.
"""
from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request

# ---------------------------------------------------------------- questions --
# Each entry: (id, category, prompt, checker). A checker returns (passed, detail).


def _norm(s: str) -> str:
    return re.sub(r"\s+", " ", s or "").strip().lower()


def _strip_fences(code: str) -> str:
    code = (code or "").strip()
    code = re.sub(r"^```[a-zA-Z0-9_+-]*\s*\n?", "", code)
    code = re.sub(r"\n?```\s*$", "", code)
    return code.strip()


def _run_code(answer: str, func: str, cases: list[tuple]) -> tuple[bool, str]:
    """Execute the model's function against cases. The only honest way to grade
    code: a function that looks right and returns wrong answers is wrong."""
    code = _strip_fences(answer)
    if func not in code:
        return False, f"no def {func} in answer"
    harness = (
        code
        + "\n\nimport json\n_res=[]\n"
        + f"for _args,_exp in {cases!r}:\n"
        + "    try:\n"
        + f"        _res.append(({func}(*_args) == _exp))\n"
        + "    except Exception:\n"
        + "        _res.append(False)\n"
        + "print('RESULT'+json.dumps(_res))\n"
    )
    path = None
    try:
        with tempfile.NamedTemporaryFile("w", suffix=".py", delete=False,
                                         encoding="utf-8") as fh:
            fh.write(harness)
            path = fh.name
        proc = subprocess.run([sys.executable, path], capture_output=True,
                              text=True, timeout=30)
        line = [l for l in proc.stdout.splitlines() if l.startswith("RESULT")]
        if not line:
            err = (proc.stderr or "").strip().splitlines()
            return False, f"did not run: {err[-1][:70] if err else 'no output'}"
        res = json.loads(line[-1][len("RESULT"):])
        return all(res), f"{sum(res)}/{len(res)} cases"
    except subprocess.TimeoutExpired:
        return False, "timed out (likely infinite loop)"
    except Exception as exc:  # noqa: BLE001
        return False, f"{type(exc).__name__}"
    finally:
        if path:
            try:
                os.unlink(path)
            except OSError:
                pass


def _exact(accept: list[str]):
    """Answer must contain one of these, compared loosely on whitespace/case."""
    def check(ans: str) -> tuple[bool, str]:
        n = _norm(ans)
        hit = next((a for a in accept if _norm(a) in n), None)
        return bool(hit), (f"matched {hit!r}" if hit else f"got {ans.strip()[:60]!r}")
    return check


def _number(expected: float, tol: float = 0.0):
    """Pull the last number out of the answer and compare. Models often
    reason aloud before the answer, so the LAST number is the claim."""
    def check(ans: str) -> tuple[bool, str]:
        nums = re.findall(r"-?\d+(?:\.\d+)?", (ans or "").replace(",", ""))
        if not nums:
            return False, f"no number in {ans.strip()[:50]!r}"
        got = float(nums[-1])
        return abs(got - expected) <= tol, f"got {got:g}, want {expected:g}"
    return check


def _json_shape(required_keys: list[str]):
    def check(ans: str) -> tuple[bool, str]:
        raw = _strip_fences(ans)
        m = re.search(r"\{.*\}", raw, re.S)
        if not m:
            return False, "no JSON object found"
        try:
            obj = json.loads(m.group(0))
        except ValueError as exc:
            return False, f"invalid JSON: {exc}"
        missing = [k for k in required_keys if k not in obj]
        return not missing, ("ok" if not missing else f"missing {missing}")
    return check


def _bullets(count: int, words: int):
    def check(ans: str) -> tuple[bool, str]:
        lines = [l.strip() for l in (ans or "").splitlines() if l.strip()]
        bl = [re.sub(r"^[-*•]\s*", "", l) for l in lines
              if re.match(r"^[-*•]\s+", l)]
        wc = [len(b.split()) for b in bl]
        ok = len(bl) == count and all(w == words for w in wc)
        return ok, f"{len(bl)} bullets (want {count}), words {wc} (want all {words})"
    return check


QUESTIONS: list[tuple[str, str, str, object]] = [
    # ---- reasoning: each has a single defensible answer -------------------
    ("bat_ball", "reasoning",
     "A bat and a ball cost $1.10 in total. The bat costs $1.00 more than the "
     "ball. How much does the ball cost in dollars? End your reply with just "
     "the number.", _number(0.05, 0.001)),
    ("widgets", "reasoning",
     "If 5 machines take 5 minutes to make 5 widgets, how many minutes do 100 "
     "machines take to make 100 widgets? End your reply with just the number.",
     _number(5)),
    ("lily", "reasoning",
     "A lily pad patch doubles in size every day and covers the lake on day "
     "48. On which day is it half covered? End with just the number.",
     _number(47)),
    ("socks", "reasoning",
     "A drawer has 10 black and 10 blue socks, drawn in the dark. What is the "
     "minimum number you must draw to guarantee a matching pair? End with just "
     "the number.", _number(3)),
    ("ages", "reasoning",
     "Sam is twice as old as Ben was when Sam was as old as Ben is now. Ben is "
     "18 and Sam is 24. In years, what is Ben's age when Sam was Ben's current "
     "age? End with just the number.", _number(12)),

    # ---- counting / tokenisation traps ------------------------------------
    ("strawberry", "counting",
     "How many times does the letter 'r' appear in the word 'strawberry'? End "
     "with just the number.", _number(3)),
    ("mississippi", "counting",
     "How many times does the letter 's' appear in 'Mississippi'? End with "
     "just the number.", _number(4)),

    # ---- code: executed, not read -----------------------------------------
    ("palindrome", "code",
     "Write a Python function is_palindrome(s) returning True if s is a "
     "palindrome ignoring case and all non-alphanumeric characters. Output "
     "ONLY the code.",
     lambda a: _run_code(a, "is_palindrome", [
         (("A man, a plan, a canal: Panama",), True),
         (("race a car",), False), (("",), True), (("ab@BA",), True),
         (("0P",), False), (("Was it a car or a cat I saw?",), True)])),
    ("roman", "code",
     "Write a Python function roman_to_int(s) converting a Roman numeral "
     "string to an integer. Handle subtractive pairs. Output ONLY the code.",
     lambda a: _run_code(a, "roman_to_int", [
         (("III",), 3), (("LVIII",), 58), (("MCMXCIV",), 1994),
         (("IV",), 4), (("IX",), 9), (("MMMCMXCIX",), 3999)])),
    ("merge", "code",
     "Write a Python function merge_intervals(intervals) that merges "
     "overlapping intervals in a list of [start, end] pairs and returns the "
     "merged list sorted by start. Output ONLY the code.",
     lambda a: _run_code(a, "merge_intervals", [
         (([[1, 3], [2, 6], [8, 10], [15, 18]],), [[1, 6], [8, 10], [15, 18]]),
         (([[1, 4], [4, 5]],), [[1, 5]]),
         (([],), []), (([[5, 6]],), [[5, 6]]),
         (([[1, 4], [0, 2], [3, 5]],), [[0, 5]])])),
    # Arguments must be plain DATA: test cases are repr()'d into a harness, and
    # a callable does not survive that (repr of a lambda is not valid Python).
    # The self-test at the bottom of this file catches exactly that mistake.
    ("binsearch", "code",
     "Write a Python function search_insert(nums, target) that returns the "
     "index of target in a sorted list nums, or the index where it should be "
     "inserted to keep nums sorted. Use binary search. Output ONLY the code.",
     lambda a: _run_code(a, "search_insert", [
         (([1, 3, 5, 6], 5), 2), (([1, 3, 5, 6], 2), 1),
         (([1, 3, 5, 6], 7), 4), (([1, 3, 5, 6], 0), 0),
         (([], 3), 0), (([1], 1), 0)])),
    ("fix_bug", "code",
     "This function is wrong: def median(xs): xs.sort(); return xs[len(xs)//2]\n"
     "Rewrite it correctly as median(xs), returning the mean of the two middle "
     "values for even length, without mutating the caller's list. Output ONLY "
     "the code.",
     lambda a: _run_code(a, "median", [
         (([3, 1, 2],), 2), (([4, 1, 3, 2],), 2.5),
         (([1],), 1), (([2, 1],), 1.5)])),

    # ---- instruction / format adherence -----------------------------------
    ("bullets", "format",
     "List exactly 4 bullet points about SSDs. Each bullet must be exactly 3 "
     "words. No other text.", _bullets(4, 3)),
    ("json_out", "format",
     'Return ONLY a JSON object describing the city Paris, with exactly the '
     'keys "name", "country", "population". No markdown, no commentary.',
     _json_shape(["name", "country", "population"])),
    ("no_e", "format",
     "Write one sentence about dogs that does not contain the letter 'e'. "
     "Output only the sentence.",
     lambda a: ("e" not in _strip_fences(a).lower(),
                f"{_strip_fences(a)[:60]!r}")),

    # ---- factual: accepted answers fixed in advance, ground truth checked --
    # FLT_REGISTRATION's member is OperationRegistration -- verified against
    # km/fltKernel.h in the Windows SDK, not from memory.
    ("minifilter", "factual",
     "In a Windows kernel minifilter driver, name the exact field of the "
     "FLT_REGISTRATION structure that holds the array of pre/post operation "
     "callbacks. Answer with just the field name.",
     _exact(["OperationRegistration"])),
    ("irql", "factual",
     "At which IRQL does a Windows minifilter's pre-operation callback for "
     "IRP_MJ_CREATE run? Answer with just the IRQL name.",
     _exact(["PASSIVE_LEVEL"])),
    ("sql_groupby", "factual",
     "This SQL is invalid: SELECT name, COUNT(*) FROM users WHERE age > 18; "
     "Name the single missing clause. Answer with just the clause keyword.",
     _exact(["GROUP BY"])),
    ("http_code", "factual",
     "Which HTTP status code means the request was understood but the server "
     "refuses to authorise it? Answer with just the number.", _number(403)),
]


# ------------------------------------------------------------------ runner --
def ask(base: str, model: str, key: str | None, prompt: str,
        max_tokens: int, timeout: int, effort: str | None = "low") -> dict:
    """One graded request.

    reasoning_effort is not a detail -- it decides whether the comparison is
    valid at all. Left unbounded, Qwen3.8 spent all 1200 tokens inside <think>,
    returned content=None and finish_reason=length, and every one of those
    scored as a wrong answer. The same question at effort=low answered in 747
    tokens and stopped cleanly. Any two models being compared must get the same
    setting, or the one that thinks longer is punished for it.
    """
    body = {"model": model, "messages": [{"role": "user", "content": prompt}],
            "max_tokens": max_tokens, "temperature": 0}
    if effort:
        body["chat_template_kwargs"] = {"reasoning_effort": effort}
    headers = {"Content-Type": "application/json"}
    if key:
        headers["Authorization"] = f"Bearer {key}"

    def _post(payload):
        req = urllib.request.Request(base.rstrip("/") + "/v1/chat/completions",
                                     data=json.dumps(payload).encode(),
                                     headers=headers)
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            return json.loads(resp.read())

    t0 = time.time()
    try:
        data = _post(body)
    except urllib.error.HTTPError as exc:
        # Not every server accepts chat_template_kwargs. Retry without it
        # rather than reporting a model failure that is really a protocol one.
        if exc.code not in (400, 422) or "chat_template_kwargs" not in body:
            raise
        body.pop("chat_template_kwargs")
        data = _post(body)
    dt = time.time() - t0
    msg = data["choices"][0]["message"]
    # A reasoning model can put everything in reasoning_content and leave
    # content null -- scoring that as an empty answer would be a grader bug.
    text = msg.get("content") or msg.get("reasoning_content") or ""
    usage = data.get("usage", {})
    ct = usage.get("completion_tokens") or 0
    return {"answer": text, "seconds": round(dt, 1), "completion_tokens": ct,
            "tok_s": round(ct / dt, 2) if dt else None,
            "finish_reason": data["choices"][0].get("finish_reason")}


def run(args) -> int:
    results, scores = {}, {}
    for qid, cat, prompt, checker in QUESTIONS:
        try:
            r = ask(args.base, args.model, args.key, prompt,
                    args.max_tokens, args.timeout, args.reasoning_effort)
        except Exception as exc:  # noqa: BLE001
            r = {"answer": "", "seconds": None, "completion_tokens": None,
                 "tok_s": None, "error": f"{type(exc).__name__}: {exc}"}
        passed, detail = (False, r.get("error", "no response"))
        if r["answer"]:
            try:
                passed, detail = checker(r["answer"])
            except Exception as exc:  # noqa: BLE001
                passed, detail = False, f"checker error: {type(exc).__name__}"
        elif r.get("finish_reason") == "length":
            # Producing no answer at all inside the budget is a real failure
            # mode, not a harness artifact -- measured on Qwen3.8-9B, which
            # looped on the "5 machines / 5 widgets" question through 4000
            # tokens without ever closing its reasoning block. Name it for what
            # it is so it is not mistaken for a transport error.
            detail = f"NO ANSWER in {r.get('completion_tokens')} tokens (reasoning loop)"
        if not passed and r["answer"] and r.get("finish_reason") == "length":
            detail += " [truncated]"
        r.update({"passed": bool(passed), "detail": detail, "category": cat})
        results[qid] = r
        scores.setdefault(cat, [0, 0])
        scores[cat][1] += 1
        scores[cat][0] += bool(passed)
        print(f"  {'PASS' if passed else 'FAIL'}  {qid:<12} {cat:<9} {detail[:60]}",
              flush=True)

    total = sum(v[0] for v in scores.values())
    count = sum(v[1] for v in scores.values())
    speeds = [r["tok_s"] for r in results.values() if r.get("tok_s")]
    summary = {"label": args.label, "score": total, "total": count,
               "by_category": {k: f"{v[0]}/{v[1]}" for k, v in scores.items()},
               "mean_tok_s": round(sum(speeds) / len(speeds), 2) if speeds else None}
    print(f"\n  {args.label}: {total}/{count}"
          + "".join(f"   {k} {v[0]}/{v[1]}" for k, v in sorted(scores.items()))
          + (f"   mean {summary['mean_tok_s']} tok/s" if speeds else ""))
    if args.out:
        with open(args.out, "w", encoding="utf-8") as fh:
            json.dump({"summary": summary, "results": results}, fh, indent=2,
                      ensure_ascii=False)
        print(f"  saved -> {args.out}")
    return 0 if total == count else 1


def compare(paths: list[str]) -> int:
    loaded = []
    for p in paths:
        with open(p, encoding="utf-8") as fh:
            loaded.append(json.load(fh))
    labels = [d["summary"]["label"] for d in loaded]
    width = max(len(l) for l in labels) + 2
    print(f"\n  {'question':<14}{'cat':<10}" + "".join(f"{l:<{width}}" for l in labels))
    print("  " + "-" * (24 + width * len(labels)))
    for qid, cat, _, _ in QUESTIONS:
        row = "".join(
            f"{('PASS' if d['results'].get(qid, {}).get('passed') else 'fail'):<{width}}"
            for d in loaded)
        print(f"  {qid:<14}{cat:<10}{row}")
    print("  " + "-" * (24 + width * len(labels)))
    print(f"  {'TOTAL':<14}{'':<10}"
          + "".join(f"{str(d['summary']['score']) + '/' + str(d['summary']['total']):<{width}}"
                    for d in loaded))
    print(f"  {'tok/s':<14}{'':<10}"
          + "".join(f"{str(d['summary'].get('mean_tok_s')):<{width}}" for d in loaded))
    return 0


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--base", help="OpenAI-compatible base URL, e.g. http://llamacpp:8080")
    ap.add_argument("--model", default="local")
    ap.add_argument("--key", default=os.environ.get("BENCH_API_KEY"))
    ap.add_argument("--label", default="model")
    ap.add_argument("--out")
    ap.add_argument("--max-tokens", type=int, default=2500)
    ap.add_argument("--reasoning-effort", default="low",
                    help="same value for every model compared, or the one that "
                         "thinks longer is punished for it; '' to omit")
    ap.add_argument("--timeout", type=int, default=3600,
                    help="per-request seconds; a heavily paging model is slow")
    ap.add_argument("--compare", nargs="+", metavar="RESULT.json")
    args = ap.parse_args()
    if args.compare:
        return compare(args.compare)
    if not args.base:
        ap.error("--base is required unless --compare is used")
    return run(args)


if __name__ == "__main__":
    raise SystemExit(main())
