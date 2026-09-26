---
name: argus-verification
description: "Check API facts against Argus before stating them: headers, import libraries, IRQLs, flags, and where a symbol is defined."
version: 1.0.0
author: Argus
license: MIT
platforms: [linux, macos, windows]
metadata:
  hermes:
    tags: [Argus, MCP, Documentation, WDK, Win32, IRQL, Verification, Code Search]
---

# Verifying facts with Argus

Argus serves indexed documentation (Windows SDK, WDK, MSVC, cppreference,
PowerShell/shell, SQLite, Python, React, algorithms, system design, WinDbg)
and this organisation's own code, over MCP tools named `docs_*` and
`find_symbol` / `search_code` / `code_contracts` / `semantic_search`.

## Why this exists

Measured on this setup, across ten task families:

| | alone | with Argus |
|---|---|---|
| `qwen3.6:27b` | 5/10 | **10/10** |
| `qwen3.6:35b` | 5/10 | **9/10** |

Both models failed **the same five tasks** unaided — the same five, task for
task, across an 8-billion-parameter gap. The failures were not reasoning
failures. Both handled algorithmic complexity and compiler-flag syntax fine.
They missed driver IRQLs, and both gave `windows.h` as the header for
`CreateFileW` when the documented answer is `fileapi.h`.

**These facts are not reliably in any local model's weights, and confidence
about them carries no signal.** The remembered answer is fluent, specific, and
wrong.

## When to use — the trigger is the FACT, not the question

Check before stating any of these, every time, regardless of how certain it
feels:

- an **IRQL** a routine is callable at
- a **header** that declares something
- an **import library** or **DLL** to link
- a **compiler or linker flag**
- a **command-line switch** (robocopy, PowerShell, shell tooling)
- an **error or status code**
- **where a symbol is defined** in this organisation's code

If a sentence you are about to write contains one of those, it needs a
lookup first. That is the trigger — not "the user asked me to check", and not
"this seems obscure".

## Which tool

| you know | use |
|---|---|
| the exact name | `docs_lookup` — exact, case-insensitive, never fuzzy |
| only what it does | `docs_find` — searches one-line descriptions |
| a concept, not a name | `docs_search`, then `docs_get` for the whole page |
| you have a source file | `docs_contracts` — every API's contract in ONE call |
| you have a draft answer | `docs_verify` — reports only what the docs contradict |
| an in-house symbol | `find_symbol`, `code_contracts`, `semantic_search` |

`docs_contracts` is the one to reach for on any code-reading task. Asked to
review a real minifilter without it, a model produced seven findings, every
one resting on a single remembered claim that
`ExAllocateFromLookasideListEx` requires `PASSIVE_LEVEL`. It is documented
`<= DISPATCH_LEVEL`. Seven wrong bug reports from one unchecked fact, and
that call returns the fact in its first line.

## Quote the documented string; do not paraphrase it

When you state a requirement, **copy the documented string verbatim** and
name the API it belongs to.

Measured across five real driver files: contract claims made from memory were
wrong **100%** of the time. Claims made with the documented text present but
restated in the model's own words were wrong **33%** of the time. Quoted
verbatim, 18 claims were wrong **0** times.

Paraphrasing re-enters generation, where a prior like *"an initialisation
routine runs at PASSIVE_LEVEL"* competes with the retrieved fact and often
wins. Copying is a transcription; restating is a prediction, and only one of
those can drift.

## Silence means silence

If Argus returns nothing for an API, say the requirement is **not documented
here**. Do not supply one from memory to fill the gap.

An empty result is also worth one retry with a different tool before you
conclude anything: `docs_lookup` is exact-match, so a name that is slightly
off returns nothing at all. Try `docs_find` by description, or `docs_search`.
Do not pass a `lang` filter you are guessing at — a wrong source name used to
turn a present fact into an empty result.

## Verify after you draft, not only before

If you have already written an answer, code, or a review, pass it to
`docs_verify`. It reports **only** what the documentation contradicts, so it
cannot overwrite something you had right.

The order matters and is measured. Putting retrieved documentation in *front*
of a model before it answered took Win32 accuracy from 5/5 to **1/5** —
retrieved text displaces knowledge the model already had. Verifying
afterwards cannot do that.

Forcing this step took `qwen3.6:35b` from 9/10 to **10/10**, fixing the one
task it had answered in 2.2 seconds with zero tool calls: it was asked what
replaces `wcscpy` in *kernel* code and answered `wcscpy_s` / `<string.h>` /
`ucrt.lib` — a real function, and a user-mode answer to a kernel question.
The documented replacement lives in `ntstrsafe.h` / `Ntstrsafe.lib`.

## Cite what you used

Every result carries `source`, `url`, `license` and `attribution`. Name the
source and give the url rather than presenting the text as your own
knowledge.
