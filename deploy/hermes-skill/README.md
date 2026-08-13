# The Argus skill for Hermes

Native Hermes skill that tells the agent **when** to check a fact against
Argus and **which** tool to use. Install once per workstation:

```bash
cp -r deploy/hermes-skill/argus "$HERMES_HOME/skills/"
```

On Windows, `HERMES_HOME` is `%LOCALAPPDATA%\hermes`.

Hermes indexes `skills/<category>/<name>/SKILL.md` into the `<available_skills>`
block of its system prompt, and the agent opens one with `skill_view`. Living in
`HERMES_HOME` rather than in the Hermes source tree, **it survives a Hermes
update** — unlike the three patches in `docs/deployment.md`, which do not.

## What it encodes

Measured findings, not advice:

- **The trigger is the fact, not the question.** An IRQL, a header, an import
  library, a compiler flag, a command switch, an error code, or the definition
  site of an in-house symbol — each needs a lookup before it is stated, however
  certain it feels. Both reference models failed the *same five* tasks unaided
  across an 8B parameter gap, and both gave `windows.h` for `CreateFileW` where
  the documented header is `fileapi.h`.
- **Which tool for which question shape**, including `docs_contracts` as the
  first call on any code-reading task.
- **Quote the documented string verbatim.** From memory, contract claims were
  wrong 100% of the time; paraphrased from retrieved text, 33%; quoted
  verbatim, 0 of 18.
- **Silence means silence** — and retry with a different tool before
  concluding, because `docs_lookup` is exact-match and a guessed `lang` filter
  turns a present fact into an empty result.
- **Verify after drafting.** Retrieval placed *before* an answer took Win32
  accuracy from 5/5 to 1/5 by displacing what the model already knew;
  `docs_verify` afterwards speaks only where the docs contradict, and forcing
  that step took `qwen3.6:35b` from 9/10 to 10/10.

## What it cannot do

A skill is guidance the model chooses to open, not a hook that fires on its
behalf. It improves *when* and *how* the agent reaches for Argus; it cannot
compel a model that decides not to look.

**That limit is measured, not assumed.** Same question, same Hermes, skill
installed, through `hermes -z`:

| model | looked it up | answer |
|---|---|---|
| `qwen3.6:27b` | yes, cited the MS URL | `RtlStringCchCopyW` / `ntstrsafe.h` / `Ntstrsafe.lib` — correct |
| `qwen3.6:35b` | **no tool calls, no citation** | `wcscpy_s` / `<wchar.h>` / `ucrtbase.lib` — a user-mode answer to a kernel question |

The first run looked like a success and was not evidence: Hermes now defaults
to 27b, which already passed this task before the skill existed, so two
variables had changed at once. The decisive test was the model that *failed*
it, and it failed the same way.

By then 35b had been given every offer this project can make — native
function-calling tool schemas, Argus's 1,803-character instructions in its
system prompt, and this skill, whose first line is *"check before stating any
of these, every time, regardless of how certain it feels"*. It read past all
three and answered from memory in seconds.

So the skill helps a model that already checks reach for the right tool. It
does not create the impulse. The only measured lever for that is removing the
choice: forced verify-after took 35b from 9/10 to **10/10** on this exact
task — see `docs/roadmap.md`, milestone 2.1.
