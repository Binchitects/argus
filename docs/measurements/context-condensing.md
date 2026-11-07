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

## With a real model: not run yet

The benchmark is ready:
`tests/CodeArena.Tests/ContextBenchmarkTests.cs`,
`A_long_session_of_coding_tasks_before_and_after_condensing_and_mending`. It
runs seven real coding tasks on a small C# shop, one after another in one
session: a bug in a sum, a decimal-comma parser, a constant to find, one
price in a 900-line catalogue, a method to add, a report's order, and a
rename across files. After each task a hidden check builds the shop's
`src/` beside its own program and runs it. It records, per task, prompt
tokens (cached apart), completion tokens, requests, seconds and pass or
fail. Per arm it records the tool calls: unknown tool, bad JSON, bad
arguments, edits that missed, other errors, calls mended, and retries (the
same failed call made again). `evals/run_context_bench.py` starts the model
in a throwaway llama.cpp container, runs both arms, and writes
`evals/context-bench-results.json` and the tables for this page:

    python3 evals/run_context_bench.py --model ~/llm-models/image/flux2-klein-4b/Qwen3-4B-Q4_K_M.gguf \
        --alias Qwen3-4B-Q4_K_M --free-gpu llamacpp,vllm,videogen,imagegen --health http://172.26.0.23:8080
    python3 evals/run_context_bench.py --model ~/llm-models/Qwen3.8-27B-UD-Q4_K_XL/Qwen3.8-27B-UD-Q4_K_XL.gguf \
        --alias Qwen3.8-27B --context 65536 --free-gpu llamacpp,vllm,videogen,imagegen --health http://172.26.0.23:8080

It was not run on 2026-11-07. The build host's GPU is held by the live
chat engine (Qwen3.8-27B and Qwen3-4B, ~22 GB of 24 GB). Stopping those
containers to free it was refused by the build session's permission
policy. The live engine's own endpoint needs the stack's key, which is not
read. The real-model numbers (tokens per task, tasks passed, tool-call
error rate before and after) go here once it is run.
