# The science lab's mathematics, measured

Forty problems of MATH-500 (the MATH test set as HuggingFaceH4 publishes it,
each with its reference answer), asked through Arena's own chat
(2026-10-27, v7.3), each in a new chat:

- **alone**: the model with no tools;
- **lab**: a lab notebook in the Mathematics field: the lab's instructions,
  SymPy's math_compute, math_check and math_solve, Lean's lean_check, Python,
  and the lab's check (an answer no tool checked is sent back once to be
  checked with a tool).

Produced by `evals/run_lab_math.py` (results in `evals/lab-math-results.json`)
as a throwaway person, removed after. The problems are a sample across the set,
as many of each level (1 to 5) and of each of its seven subjects as forty
allow (seed 7), never the first forty. Graded on the last `\boxed{...}` of the
final answer with MATH's own answer normalization (Hendrycks et al.'s
`math_equivalence`), as the set's papers grade it.

## The numbers

| model | arm | right | seconds each | tokens written | tool calls | problems with a call |
|---|---|---:|---:|---:|---:|---:|
| Qwen3-4B (Q4_K_M, thinking) | alone | 36 / 40 | 150 | 168,825 | 0 | 0 |
| Qwen3-4B (Q4_K_M, thinking) | lab | 36 / 40 | 211 | 218,379 | 75 | 39 |
| Qwen2.5-Math-7B-Instruct (Q4_K_M) | alone | 32 / 40 | 21 | 24,038 | 0 | 0 |
| Qwen2.5-Math-7B-Instruct (Q4_K_M) | lab | 0 / 40 | 91 | 94,855 | 0 | 0 |

## What it says

- **The lab checks; it does not make the small model cleverer.** Qwen3-4B
  scored the same in both arms, and missed the same four problems. In the lab
  it called a tool in 39 of the 40 problems (75 calls), so 39 of its answers
  carry a check a person can read in the notebook; in a first run, before the
  lab's check existed, it called none. It called a tool on three of its misses
  too (68 for 28 in geometry, 67 for 55° in precalculus) and still answered
  wrong: a check proves the step it is given, not that the problem was set up
  right.
- **It costs time.** The lab took 41% longer (211 s a problem against 150 s)
  and wrote 29% more. One problem (level 5, intermediate algebra) gave no
  boxed answer in the lab: it wrote 30,526 tokens, near its 32,768-token
  context, without a call. Alone it gave a wrong one.
- **The maths model does not stay.** Qwen2.5-Math-7B-Instruct was four
  problems behind the 4B model alone (80% against 90%), though seven times
  faster: it does not think at length. In the lab it answered something other
  than the problem all forty times (a formula, the roots of another equation)
  and called no tool: its context is 4,096 tokens and it was not trained to
  call tools, and with the lab's instructions and tool definitions before the
  problem it lost the problem. It was removed from the live models; the lab
  works with the chat's models that call tools.
- **One miss is the grading's.** Qwen3-4B (in both arms) and Qwen2.5-Math
  alone answered 2516 where the reference is 2516₈ (the base written as a
  subscript): MATH's normalization keeps the subscript, so it counts as wrong.

## Caveats

One run of each arm on one machine, both models at 4-bit on llama.cpp, forty
problems: a difference of one or two problems is within what another sample
would change. MATH-500 asks for an answer, not a proof: the lab's proofs are
tested by `scripts/lean-check.py` and the lab's own tests instead. The bigger
models (Qwen3.8-27B) were not run in this round.
