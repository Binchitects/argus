# Forced verify-after for Hermes

Runs `docs_verify` on a finished answer whether the model wanted it or not,
and lets it revise only where the documentation contradicts.

```bash
cp deploy/hermes-patch/argus_verify.py "$HERMES/agent/"
```

Then add to `agent/turn_finalizer.py`, immediately after
`from agent.conversation_loop import logger` inside `finalize_turn`:

```python
    try:
        from agent.argus_verify import apply as _argus_verify_apply

        final_response = _argus_verify_apply(agent, final_response)
    except Exception:
        logger.debug("Argus verify-after unavailable", exc_info=True)
```

Enable per run:

```bash
ARGUS_VERIFY_AFTER=1 ARGUS_URL=http://127.0.0.1:8099/mcp ARGUS_TOKEN=... hermes
```

## Why here

`run_conversation` is ~7,700 lines with 27 sites touching `final_response`.
`finalize_turn` is the one synchronous chokepoint every path funnels into,
with a single return — so a turn cannot ship an unverified claim by taking a
different branch.

It is synchronous but may run inside an async gateway where `asyncio.run`
raises, so the MCP call gets a dedicated thread with its own loop and a
bounded join. A hung Argus delays a turn; it does not hang one.

## What it fixes, measured

`qwen3.6:35b` drafted `RtlStringCchCopyW` and claimed it needed **no** library
linking. Verification contradicted that, and the revised answer opens: *"the
previous answer incorrectly stated that `RtlStringCchCopyW` … require no
library linking."* 9/10 → 10/10 on the ten-task bench.

## What it does NOT fix

**Verify-after catches wrong facts about the APIs a draft names. It cannot
catch the wrong choice of API.**

Same model, same question, through Hermes: 35b drafted `wcscpy_s` /
`<wchar.h>` / the C runtime — a user-mode answer to a question about *kernel*
code. `docs_verify` returned **zero bytes**, correctly: every statement in
that draft is true. `wcscpy_s` really is declared in `<wchar.h>`. The draft is
not false, it is irrelevant, and fact-checking has no purchase on that.

The two failures are indistinguishable in the output and completely different
in kind. Only the empty `docs_verify` response tells them apart.

So this closes one class of error and leaves another open. Retrieval-first
would catch the second — and is measured making things worse, taking Win32
accuracy from 5/5 to 1/5 by displacing what the model already knew. That
trade has not been made here.

## Cost and default

Off unless `ARGUS_VERIFY_AFTER` is set. It adds a round trip to every turn,
including the ones already answered correctly from memory — ~11 s measured,
which buys nothing against a model that checks and buys the answer against
one that does not.

Only `contradicted` triggers a revision. `docs_verify` also reports
`confirmed` and `unstated`; re-prompting on those invites a correct answer to
become a different one, which is a regression dressed as an improvement.
