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
