# Tools direct, or as code

Two of our own models, the same ten tasks, tools given each way through Arena's
own chat (2026-10-26, v7.2):

- **direct**: every tool's definition, one call at a time. With Argus's 34
  functions past the chat's budget, the tools go on demand: the system prompt
  lists them, and `load_tools` loads those the answer needs.
- **code**: `run_code`, and the functions a program may call named in the
  system prompt as Python (`arena.argus.docs_lookup(name)`): the model writes
  a short program, its calls go to Argus as the person, only what it prints
  comes back.
- **both**: the tools as direct gives them, and `run_code` beside them.

Produced by `evals/run_tool_modes.py` (results in
`evals/tool-modes-results.json`), as a throwaway person with Argus's
documentation tools and Tools as code, each task in a new chat.

## The tasks

The ten task families of [model-comparison.md](model-comparison.md), one fact
each. Two were dropped before any model ran: the packs loaded here do not say
`/std:c++20` or `/w4` (coding style) nor `/mir` (scripting), and a task whose
answer the packs do not hold is not graded. Two tasks of six lookups each were
added, where a program should pay:

- **irql-table**: the documented IRQL of IoCreateDevice, ExAllocatePool2,
  FltSendMessage, ExInitializeLookasideListEx and IoCompleteRequest (KeSetEvent
  was left out: its entry here says no IRQL);
- **header-library-table**: the header and import library of CreateFileW,
  CryptAcquireContextW, RegOpenKeyExW, WSAStartup, CoCreateInstance and
  MessageBoxW.

Every expected token was looked up in the packs with the same tools first;
each symbol's own line must carry its facts.

## The numbers

| model | mode | pass | prompt tokens (cached) | completion | seconds | calls | calls in programs |
|---|---|---:|---:|---:|---:|---:|---:|
| Qwen3.8-27B (Q4_K_XL) | direct | 4 / 10 | 8,810 (0) | 18,761 | 2,304 | 0 | — |
| Qwen3.8-27B (Q4_K_XL) | **code** | **10 / 10** | 235,288 (165,501) | 10,082 | 1,592 | 36 | 43 |
| Qwen3.8-27B (Q4_K_XL) | both | 6 / 10 | 86,061 (44,576) | 13,600 | 1,839 | 10 | 0 |
| Qwen3-4B (Q4_K_M) | direct | 1 / 10 | 7,175 (6,871) | 7,507 | 206 | 0 | — |
| Qwen3-4B (Q4_K_M) | code | 1 / 10 | 40,542 (39,873) | 10,732 | 347 | 2 | 0 |

Both six-lookup tasks passed only as code (27B: 2 / 2, 11 lookups from its
programs; for the header table one program called `docs_lookup` six times).

## What it says

- **The 27B model checks when it sees what it can call.** As code it called
  in every task (36 programs, 43 lookups) and passed all ten. Direct, it never
  loaded a tool in this run and answered from memory: four tasks right (test
  development, performance, SDK, code safety), none of the other IRQLs or
  headers. An earlier run of the same arm loaded tools in three tasks and
  passed 6 / 10. With both, it loaded tools in two tasks, never wrote a
  program, and passed 6 / 10. Neither table passed but as code: the list of
  tools to load is easy to pass over; functions named with their parameters
  are not.
- **It costs prompt, saves writing.** As code the prompt grew (the functions
  named, each program's output read back: 235k tokens, 70% from the cache),
  and the model wrote half as much (10k tokens against 19k) and finished in
  two thirds of the time: it looked facts up instead of reasoning towards
  them.
- **The small model gains nothing.** Qwen3-4B passed one task either way, wrote
  two programs that called nothing, and spent five times the prompt as code.
  The automatic choice (Admin → Models → Tools) keeps small models direct.
- **So the default follows it**: Admin → Models → Tools is automatically as
  code for models whose names say more than 8B parameters (or none), and
  direct for the smaller ones and the model for small steps.

## Caveats

One run of each arm (a second for the 27B's direct arm), on one machine, both
models at 4-bit on llama.cpp; the 27B model's direct arm varies between runs
because it may or may not load tools. The tasks are documentation lookups; code
over the code graph (callers across repositories) needs a person whose GitLab
rights Argus can check, which a throwaway person has not.
