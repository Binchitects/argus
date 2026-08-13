# Evaluations

Not unit tests. These need a running Ollama, a built model and installed
packs, so they live outside `tests/` and are run deliberately.

```bash
python evals/generate_questions.py <packs_dir> evals/questions-120.json 30
ARGUS_OLLAMA_URL=http://localhost:11435 \
    python evals/run_ab.py <packs_dir> evals/questions-120.json
```

## The one rule these exist to enforce

**Ground truth comes from the packs, never from the person writing the test.**

Five earlier rounds were thrown away because of the alternative. The worst
marked the packs *wrong for being right*: the expected IRQL for
`IoCreateDevice` was PASSIVE_LEVEL from memory, Microsoft publishes
`<= APC_LEVEL`, and the grounded answer was scored a failure for agreeing with
the documentation.

`generate_questions.py` extracts the question and its answer from the same
pack page, so the expected answer is whatever upstream actually publishes.
That is not circular: the claim under test is *does retrieval make the agent
agree with the official documentation*, and the closed-book arm measures how
often the model already knows the documented fact. Were the docs wrong, both
arms would be wrong together.

## Other traps these harnesses encode

* **Grade one line, not the whole reply.** An early run scored 10/10 on both
  arms because with thinking enabled, `dispatch_level` appears somewhere in
  any driver discussion. Only the final `VERDICT:` line is read.
* **Check accept before reject.** Reject lists are substrings of real answers:
  `kereleasespinlock` is a prefix of the correct
  `kereleasespinlockfromdpclevel`, and `free` sits inside `wtsfreememory`.
  Checking reject first scored two right answers as failures.
* **Reasoning models need a token budget.** qwen3.6 with 160 tokens spent all
  of them in `thinking` and returned an empty `response`; both arms scored
  0/6, measuring nothing but the budget.
* **Sample across the corpus.** Taking the first N of an alphabetical list
  means every question starts with "A".
* **Test the whole pack.** The `cpp` pack looked worthless at 26/30 -> 25/30
  until the questions came from its MSVC half rather than its STL half:
  3/40 -> 40/40. The first set measured 20% of the pack.

---

## Comparing models against your Argus setup

`run_model_bench.py` scores any Ollama model twice — alone, then with Argus —
across ten task families: test development, code review, performance, coding
style, SDK, WDK, win32, scripting, security review, code safety.

```bash
python evals/run_model_bench.py --models qwen3.6:35b,gpt-oss:20b --token "$ARGUS_TOKEN"
```

```bash
python evals/run_model_bench.py --report
```

| flag | |
|---|---|
| `--models` | comma-separated Ollama tags; any model Ollama can serve |
| `--arms` | `closed-book`, `with-argus`, or `both` |
| `--url` / `--token` | your Argus endpoint and bearer token |
| `--ollama` | Ollama base URL |
| `--packs` | pack directory, used to verify the answer key |
| `--append` | keep rows for models/arms you are not re-running |
| `--report` | print the comparison for existing results, run nothing |

### What makes the score trustworthy

**The answer key is verified against your packs before any model runs.**
Every expected token must appear in the documentation Argus actually serves,
or the bench refuses to start. An answer key written from memory would score a
model wrong for being right, which is the exact failure this project exists to
measure. It has already caught itself once: `/std:c++20` raised an FTS5 syntax
error rather than silently reporting "not found", which would have read as a
gap in the corpus.

**Grading is substring matching on facts, never prose judgement.** A correct
answer must contain the documented import library, IRQL or compiler flag.
That is checkable, reproducible, and cannot be talked into agreeing.

**Zero-tool-call passes are surfaced separately.** A model that answers
correctly without calling anything answered from memory, and got lucky. The
report lists those rows explicitly, because that is how the one with-argus
failure on the reference run happened: `qwen3.6:35b` answered a kernel
question in 2.2 s with no tool calls, confidently, and wrongly.

### Reading the output

The aggregate hides the finding that matters most. On the reference run both
models scored 5/10 closed book — and failed **the same five tasks**, task for
task, across an 8-billion-parameter gap and a different architecture. That is
why the report prints the per-task grid: two models scoring alike look
interchangeable until you see they fail identically, which says the gap is
knowledge rather than capability, and that scale will not close it.

### Adding your own tasks

Append to `TASKS`. Each needs a `prompt`, the `expect` tokens a correct answer
must contain, and a `probe` — the symbol whose pack entry must contain those
tokens, or `None` for a fact with no owning symbol (a compiler flag, a
command-line switch), which is verified by full-text search instead. If your
task's fact is not in the packs, the bench will tell you before it wastes an
hour of inference on it.
