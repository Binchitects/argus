# Context condensing and tool-call mending, measured

Asked for: "super optimization on context in code-arena, summarize tool
results, read files etc. and continuously improve tool calling and make it
more reliable and less errors" (2026-11-07). Before this, tool results and
file reads were never summarised: they were cut (start and end and a
pointer) or cleared, and only near the compaction threshold; a file read
stayed whole until then.

What changed (docs/code-arena.md, "What fills the window"; docs/chat.md,
"Results summarised once used"):

- a result two answers behind goes as a summary (what was asked, its key
  lines, the handle that reads it whole), before every step;
- a file read goes as its outline and the lines cited, once the model has
  moved on from the file;
- a file read again after an edit, or the same way, drops the older read;
- tool calls are mended (names, nearly-JSON, argument names and types) or
  refused with the tool's parameters, and counted.

## A replayed long session (no model)

`tests/CodeArena.Tests/ContextBenchmarkTests.cs`,
`A_replayed_long_session_carries_less_with_condensing_and_the_last_task_alike`:
the seven tasks of the benchmark below in one session, the model's steps
given by a fake gateway, every tool real on the shop fixture (each task:
read the cart, grep, read the 900-line catalogue's outline then 300 lines of
it, `cat` two helpers through the shell, edit the cart, read it again).
The same steps both ways, so only what the requests carry differs.

| condensing | requests | characters of messages, all requests | the last request |
|---|---:|---:|---:|
| off (before) | 56 | 6,427,151 | 227,889 |
| on (after) | 56 | 1,896,317 | 51,465 |
| change | | **−70%** | **−77%** |

What it did over the session: 7 results summarised, 7 file reads left as
their outline and the lines cited, 19 older reads of files read again
dropped (~43,500 tokens freed in the history, each saving repeated in every
later request). The test fails if either figure stops being under two
thirds of the before arm.

## With a real model (2026-11-07, RTX 3090 at 125 W)

`evals/run_context_bench.py`, one run of each arm, the live engines stopped for it.

**Qwen3.8 27B (UD-Q4_K_XL, a 65,536 window):**

| arm | passed | prompt tokens / task (uncached) | completion / task | requests / task | seconds / task | tool-call errors |
|---|---:|---:|---:|---:|---:|---:|
| before | 6 / 7 | 65,449 (1,458) | 748 | 6.0 | 96.8 | 0 of 38 |
| after | 7 / 7 | 62,683 (1,796) | 618 | 5.6 | 82.2 | 1 of 34 |

The 27B solves each task in four to eight requests, and its results are short, so condensing had little to work on:
it dropped one older read of a file read again, and summarised nothing. Prompt tokens fell 4% and time 15%; one more
task passed. With one run per arm these differences are within the noise. The saving shows on long sessions (the
scripted session above: −70%), not on tasks this short.

**Qwen3-4B (Q4_K_M, a 32,768 window):** 0 of 7 passed in both arms. The model loops (200 requests on the first task)
or stops without a change; its tool results were about 2,000 characters a task, so nothing was condensed. Prompt
tokens per task fell 26% and time 25% with condensing on, from shorter loops. A 4B model is too small for this
benchmark.

