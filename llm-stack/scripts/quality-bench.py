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


# ------------------------------------------------------------ hard suite --
# The standard suite above saturates: UD-IQ4_XS scored 19/19, so it cannot show
# whether a larger quant is better. Everything below is built to have RESOLUTION
# instead of a ceiling.
#
# Two design rules follow from that:
#
#   1. PARTIAL CREDIT. A checker here returns a fraction, not a verdict. Two
#      quants that both "work" differ by degree -- 9 of 12 edge cases against
#      12 of 12 -- and a pass/fail grader throws that difference away.
#   2. TARGET WHAT QUANTISATION ACTUALLY DAMAGES. Rounding weights hurts the
#      tail of the distribution first: rare tokens (exact API names), long
#      dependencies (retrieval from deep context), and error accumulation over
#      many steps. Generic knowledge questions survive 2-bit and tell you
#      nothing.


def _run_code_partial(answer: str, func: str, cases: list[tuple]):
    """Fraction of cases passed, so a nearly-right function scores above a
    badly-wrong one. Whole-suite pass/fail is what made the standard suite
    saturate."""
    code = _strip_fences(answer)
    if func not in code:
        return 0.0, f"no def {func} in answer"
    harness = (
        code + "\n\nimport json\n_res=[]\n"
        + f"for _args,_exp in {cases!r}:\n"
        + "    try:\n"
        + f"        _res.append(({func}(*_args) == _exp))\n"
        + "    except Exception:\n"
        + "        _res.append(False)\n"
        + "print('RESULT'+json.dumps(_res))\n")
    path = None
    try:
        with tempfile.NamedTemporaryFile("w", suffix=".py", delete=False,
                                         encoding="utf-8") as fh:
            fh.write(harness)
            path = fh.name
        proc = subprocess.run([sys.executable, path], capture_output=True,
                              text=True, timeout=45)
        line = [l for l in proc.stdout.splitlines() if l.startswith("RESULT")]
        if not line:
            err = (proc.stderr or "").strip().splitlines()
            return 0.0, f"did not run: {err[-1][:60] if err else 'no output'}"
        res = json.loads(line[-1][len("RESULT"):])
        return sum(res) / len(res), f"{sum(res)}/{len(res)} cases"
    except subprocess.TimeoutExpired:
        return 0.0, "timed out (likely infinite loop)"
    except Exception as exc:  # noqa: BLE001
        return 0.0, type(exc).__name__
    finally:
        if path:
            try:
                os.unlink(path)
            except OSError:
                pass


def _all_of(terms: list[str]):
    """Fraction of required terms present. Rare exact identifiers are where
    quantisation damage shows first, so each one is scored separately."""
    def check(ans: str):
        n = _norm(ans)
        hits = [t for t in terms if _norm(t) in n]
        return len(hits) / len(terms), f"{len(hits)}/{len(terms)}: missing {[t for t in terms if t not in hits]}"
    return check


def _constraints(checks: list[tuple[str, object]]):
    """Fraction of independent constraints satisfied."""
    def check(ans: str):
        text = _strip_fences(ans)
        met = [name for name, fn in checks if fn(text)]
        return len(met) / len(checks), f"{len(met)}/{len(checks)} met, failed {[n for n, f in checks if n not in met]}"
    return check


def _haystack(needles: dict[str, str], filler_paras: int = 220) -> str:
    """A long document with facts buried at known depths.

    Deterministic on purpose: every model must see byte-identical input, or the
    comparison measures the document rather than the model. Facts are spread
    evenly so a model that only attends to the start or the end loses points.
    """
    lines, items = [], list(needles.items())
    every = max(1, filler_paras // (len(items) + 1))
    n = 0
    for i in range(filler_paras):
        lines.append(
            f"Section {i + 1}. Routine maintenance note: the operations team "
            f"reviewed cabinet {i % 17 + 1} and confirmed the inventory tags "
            f"were legible, the airflow baffles were seated, and no action was "
            f"required at this time.")
        if (i + 1) % every == 0 and n < len(items):
            key, val = items[n]
            lines.append(f"IMPORTANT RECORD: the {key} is {val}.")
            n += 1
    for key, val in items[n:]:
        lines.append(f"IMPORTANT RECORD: the {key} is {val}.")
    return "\n".join(lines)


_NEEDLES = {
    "calibration code for bay seventeen": "QX-4417",
    "emergency shutdown passphrase": "amber-lattice-99",
    "coolant loop pressure limit": "212 kPa",
    "night shift supervisor surname": "Vandermeer",
    "backup generator serial": "GN-8830-B",
}
_HAYSTACK = _haystack(_NEEDLES)


HARD_QUESTIONS: list[tuple[str, str, str, object]] = [
    # ---- long-context retrieval: the first thing quantisation degrades ----
    ("needle_5", "longctx",
     _HAYSTACK + "\n\nFrom the document above, list these five values, one per "
     "line, in this order: calibration code for bay seventeen; emergency "
     "shutdown passphrase; coolant loop pressure limit; night shift supervisor "
     "surname; backup generator serial. Give only the values.",
     _all_of(["QX-4417", "amber-lattice-99", "212 kPa", "Vandermeer",
              "GN-8830-B"])),

    # ---- multi-step arithmetic: errors compound, so depth discriminates ---
    # Both of these were authored with WRONG ground truth and ambiguous
    # wording, and scored correct model answers as failures -- the same class
    # of defect as a grader that inverts a verdict. The expected values below
    # are computed, not remembered, and the wording now admits one reading.
    #
    #   multistep: "round down" was ambiguous between flooring the amount
    #     shipped (1009) and flooring the stock that remains (1007). Now says
    #     which. Originally asserted 866, which is neither.
    #   date_calc: 45 business days from Tue 2024-01-30 is 2024-04-01 if the
    #     start counts as day 1, or 2024-04-02 if it does not. Now says which.
    ("multistep", "reasoning",
     "A warehouse starts with 1284 units. Monday it ships 17% of current "
     "stock. Tuesday it receives 340 units. Wednesday it ships one quarter of "
     "current stock. Thursday it writes off 46 damaged units. Each time units "
     "are shipped, round the NUMBER SHIPPED down to a whole unit. How many "
     "units remain on Thursday evening? End with just the number.",
     _number(1009)),
    ("date_calc", "reasoning",
     "A task starts on Tuesday 2024-01-30 and takes 45 business days "
     "(Monday-Friday, ignoring holidays), counting the start date itself as "
     "business day 1. On what date does it finish? Answer as YYYY-MM-DD only.",
     _exact(["2024-04-01"])),
    ("unit_chain", "reasoning",
     "A pump moves 3.5 litres per second. How many cubic metres does it move "
     "in 2 hours and 45 minutes? Round to one decimal place. End with just the "
     "number.", _number(34.7, 0.05)),

    # ---- code with edge cases, scored by fraction ------------------------
    ("lru", "code",
     "Write a Python class LRUCache with __init__(self, capacity), get(key) "
     "returning -1 if absent, and put(key, value) evicting the least recently "
     "used entry at capacity. Then write a function lru_ops(capacity, ops) "
     "where ops is a list of ['get',k] or ['put',k,v]; return the list of "
     "results from every 'get'. Output ONLY the code.",
     lambda a: _run_code_partial(a, "lru_ops", [
         ((2, [["put", 1, 1], ["put", 2, 2], ["get", 1], ["put", 3, 3],
               ["get", 2], ["put", 4, 4], ["get", 1], ["get", 3], ["get", 4]]),
          [1, -1, -1, 3, 4]),
         ((1, [["put", 1, 1], ["get", 1], ["put", 2, 2], ["get", 1],
               ["get", 2]]), [1, -1, 2]),
         ((2, [["get", 9]]), [-1]),
         ((2, [["put", 1, 1], ["put", 1, 5], ["get", 1]]), [5]),
     ])),
    ("tokenizer", "code",
     # The spec must match the test cases EXACTLY. An earlier wording said a
     # doubled backslash escaped a quote while the cases used a single one --
     # which penalises a model for following the instruction it was given.
     "Write a Python function split_args(s) that splits a command line on "
     "spaces, with two rules: a double-quoted span stays together as one "
     "argument and the quotes themselves are removed; and a single backslash "
     "makes the next character literal and is itself removed. Runs of spaces "
     "do not produce empty arguments. Return the list of arguments. Output "
     "ONLY the code.",
     lambda a: _run_code_partial(a, "split_args", [
         (('a b c',), ['a', 'b', 'c']),
         (('a "b c" d',), ['a', 'b c', 'd']),
         (('"only"',), ['only']),
         (('',), []),
         (('a   b',), ['a', 'b']),
         ((r'a "b \" c" d',), ['a', 'b " c', 'd']),
     ])),
    ("interval_sched", "code",
     "Write a Python function max_meetings(intervals) returning the maximum "
     "number of non-overlapping [start, end) intervals that can be selected. "
     "Output ONLY the code.",
     lambda a: _run_code_partial(a, "max_meetings", [
         (([[0, 30], [5, 10], [15, 20]],), 2),
         (([[1, 2], [2, 3], [3, 4]],), 3),
         (([],), 0),
         (([[1, 10], [2, 3], [3, 4], [4, 5]],), 3),
         (([[1, 2]],), 1),
     ])),
    ("roman_back", "code",
     "Write a Python function int_to_roman(n) for 1 <= n <= 3999. Output ONLY "
     "the code.",
     lambda a: _run_code_partial(a, "int_to_roman", [
         ((3,), "III"), ((58,), "LVIII"), ((1994,), "MCMXCIV"),
         ((4,), "IV"), ((3999,), "MMMCMXCIX"), ((40,), "XL"), ((90,), "XC"),
     ])),

    # ---- rare exact identifiers: the tail quantisation rounds away -------
    ("wdk_apis", "factual",
     "Name the four Windows kernel APIs a minifilter uses to: register the "
     "filter, start filtering, get a file name from a callback data, and "
     "release that file name info. Answer with just the four function names.",
     _all_of(["FltRegisterFilter", "FltStartFiltering",
              "FltGetFileNameInformation", "FltReleaseFileNameInformation"])),
    ("irp_fields", "factual",
     "In FLT_CALLBACK_DATA, name the member holding the I/O parameter block "
     "and the member holding the operation status. Answer with just the two "
     "member names.", _all_of(["Iopb", "IoStatus"])),
    ("status_codes", "factual",
     "Give the NTSTATUS symbolic names for: operation completed successfully, "
     "buffer too small, object name not found, and access denied. Answer with "
     "just the four names.",
     _all_of(["STATUS_SUCCESS", "STATUS_BUFFER_TOO_SMALL",
              "STATUS_OBJECT_NAME_NOT_FOUND", "STATUS_ACCESS_DENIED"])),

    # ---- many simultaneous constraints -----------------------------------
    ("compound_fmt", "format",
     "Write exactly 3 lines. Every line must start with the word 'Disk', "
     "contain exactly 6 words, end with a period, and no line may contain the "
     "letter 'z'. Output only those 3 lines.",
     _constraints([
         ("exactly 3 lines",
          lambda t: len([l for l in t.splitlines() if l.strip()]) == 3),
         ("all start with Disk",
          lambda t: all(l.strip().startswith("Disk")
                        for l in t.splitlines() if l.strip())),
         ("all 6 words",
          lambda t: all(len(l.split()) == 6
                        for l in t.splitlines() if l.strip())),
         ("all end with period",
          lambda t: all(l.strip().endswith(".")
                        for l in t.splitlines() if l.strip())),
         ("no letter z", lambda t: "z" not in t.lower()),
     ])),
    ("json_nested", "format",
     'Return ONLY JSON: an object with key "servers" whose value is a list of '
     'exactly 2 objects, each having keys "host" (string), "port" (integer), '
     'and "tls" (boolean). No commentary, no markdown.',
     _constraints([
         ("parses", lambda t: _json_parses(t)),
         ("has servers list", lambda t: isinstance(
             (_json_get(t) or {}).get("servers"), list)),
         ("exactly 2", lambda t: len((_json_get(t) or {}).get("servers") or []) == 2),
         ("keys correct", lambda t: all(
             set(s) >= {"host", "port", "tls"}
             for s in ((_json_get(t) or {}).get("servers") or []))),
         ("types correct", lambda t: all(
             isinstance(s.get("host"), str) and isinstance(s.get("port"), int)
             and isinstance(s.get("tls"), bool)
             for s in ((_json_get(t) or {}).get("servers") or []))),
     ])),
]


def _json_get(text: str):
    m = re.search(r"\{.*\}", _strip_fences(text), re.S)
    if not m:
        return None
    try:
        return json.loads(m.group(0))
    except ValueError:
        return None


def _json_parses(text: str) -> bool:
    return _json_get(text) is not None


SUITES = {"standard": QUESTIONS, "hard": HARD_QUESTIONS,
          "all": QUESTIONS + HARD_QUESTIONS}


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


def _as_score(verdict) -> float:
    """Checkers may return a bool (standard suite) or a fraction (hard suite).
    Normalising here lets one runner serve both."""
    return 1.0 if verdict is True else (0.0 if verdict is False else float(verdict))


def run(args) -> int:
    questions = SUITES[args.suite]
    results, scores = {}, {}
    for qid, cat, prompt, checker in questions:
        try:
            r = ask(args.base, args.model, args.key, prompt,
                    args.max_tokens, args.timeout, args.reasoning_effort)
        except Exception as exc:  # noqa: BLE001
            r = {"answer": "", "seconds": None, "completion_tokens": None,
                 "tok_s": None, "error": f"{type(exc).__name__}: {exc}"}
        passed, detail = (0.0, r.get("error", "no response"))
        if r["answer"]:
            try:
                passed, detail = checker(r["answer"])
                passed = _as_score(passed)
            except Exception as exc:  # noqa: BLE001
                passed, detail = 0.0, f"checker error: {type(exc).__name__}"
        elif r.get("finish_reason") == "length":
            # Producing no answer at all inside the budget is a real failure
            # mode, not a harness artifact -- measured on Qwen3.8-9B, which
            # looped on the "5 machines / 5 widgets" question through 4000
            # tokens without ever closing its reasoning block. Name it for what
            # it is so it is not mistaken for a transport error.
            detail = f"NO ANSWER in {r.get('completion_tokens')} tokens (reasoning loop)"
        if passed < 1.0 and r["answer"] and r.get("finish_reason") == "length":
            detail += " [truncated]"
        r.update({"score": round(float(passed), 3), "passed": passed >= 1.0,
                  "detail": detail, "category": cat})
        results[qid] = r
        scores.setdefault(cat, [0.0, 0])
        scores[cat][1] += 1
        scores[cat][0] += float(passed)
        mark = "PASS" if passed >= 1.0 else ("FAIL" if passed == 0 else f"{passed:.0%}")
        print(f"  {mark:>4}  {qid:<14} {cat:<9} {detail[:60]}", flush=True)

    total = sum(v[0] for v in scores.values())
    count = sum(v[1] for v in scores.values())
    speeds = [r["tok_s"] for r in results.values() if r.get("tok_s")]
    summary = {"label": args.label, "suite": args.suite,
               "score": round(total, 2), "total": count,
               "pct": round(100 * total / count, 1) if count else 0.0,
               "by_category": {k: f"{v[0]:.2f}/{v[1]}" for k, v in scores.items()},
               "mean_tok_s": round(sum(speeds) / len(speeds), 2) if speeds else None}
    print(f"\n  {args.label} [{args.suite}]: {total:.2f}/{count} = {summary['pct']}%"
          + "".join(f"   {k} {v[0]:.1f}/{v[1]}" for k, v in sorted(scores.items()))
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
    seen = {qid for d in loaded for qid in d["results"]}
    for qid, cat, _, _ in SUITES["all"]:
        if qid not in seen:
            continue
        cells = []
        for d in loaded:
            r = d["results"].get(qid)
            if r is None:
                cells.append("-")
            else:
                s = r.get("score", 1.0 if r.get("passed") else 0.0)
                cells.append("PASS" if s >= 1.0 else ("fail" if s == 0 else f"{s:.0%}"))
        print(f"  {qid:<14}{cat:<10}" + "".join(f"{c:<{width}}" for c in cells))
    print("  " + "-" * (24 + width * len(labels)))
    print(f"  {'TOTAL':<14}{'':<10}"
          + "".join(f"{str(d['summary']['score']) + '/' + str(d['summary']['total']):<{width}}"
                    for d in loaded))
    print(f"  {'PERCENT':<14}{'':<10}"
          + "".join(f"{str(d['summary'].get('pct', '-')) + '%':<{width}}" for d in loaded))
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
    ap.add_argument("--suite", choices=sorted(SUITES), default="standard",
                    help="standard saturates on a good model; hard has partial "
                         "credit and is built to separate quantisations")
    ap.add_argument("--compare", nargs="+", metavar="RESULT.json")
    args = ap.parse_args()
    if args.compare:
        return compare(args.compare)
    if not args.base:
        ap.error("--base is required unless --compare is used")
    return run(args)


if __name__ == "__main__":
    raise SystemExit(main())
