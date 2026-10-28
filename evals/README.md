# Evaluations

Not unit tests. They need a running Argus with packs loaded, and a model, so
they live outside `tests/` and are run deliberately.

| harness | what it measures | writes |
|---|---|---|
| `generate_questions.py <packs_dir> <out.json> [per_pack]` | builds a question set **from the packs**, each question with its answer taken from the same page | the file you name |
| `run_hermes_packs.py [pack ...]` | whether the real Hermes CLI answers each loaded pack's question correctly, over MCP, graded on a token from the pack | `hermes-packs-results.json` |
| `run_code_review.py <url> <token> <file>` | an agentic review of a real source file, closed book against with Argus's tools, printed side by side for a person to judge | stdout |
| `run_load_test.py --url ... --token ...` | where the single-SQLite index stops scaling: concurrent readers, with and without a writer | `load-test-results.json` |
| `run_tool_modes.py --models A,B` | the same tasks through Arena's chat with each model's tools direct and as code (run_code): pass, tokens, seconds, calls; the answer key looked up in the packs first | `tool-modes-results.json` |
| `run_lab_math.py --models A,B [--count 40]` | MATH-500 problems through Arena's chat, each model alone and in a science lab notebook (Mathematics: SymPy, Lean, Python, the lab's check): right on MATH's own grading, seconds, tokens, tool calls | `lab-math-results.json` |
| `run_lab_quantum.py --models A,B [--count 0]` | Qiskit HumanEval through Arena's chat, each model alone and in a lab notebook (Quantum computing: Qiskit, Aer, Python): passing the set's own tests in the sandbox's image, seconds, tokens, tool calls | `lab-quantum-results.json` |
| `run_security_review.py --suite owasp\|juliet-java\|juliet-csharp --root DIR` | the security review on the OWASP Benchmark and the Juliet suite, their answers hidden (comments out, names replaced): precision and recall per weakness for the scanners alone, the model alone, the model with the security pack, and both as shipped | `security-review-<suite>-results.json` |

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
