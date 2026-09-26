"""Forced verify-after: check a finished answer against Argus before it ships.

Every mechanism that *offers* verification has been measured failing on at
least one model. `qwen3.6:35b` was given native function-calling tool schemas,
Argus's 1,803-character instructions in its system prompt, AND a skill whose
first line is "check before stating any of these, every time, regardless of
how certain it feels" -- and answered a kernel question with a user-mode API
in 2.2 seconds, with zero tool calls, three times running.

Removing the choice is the only thing that worked: forced verification took
that model from 9/10 to 10/10 on the same bench.

So this runs whether the model wanted it or not. The draft is finished;
`docs_verify` reports only what the documentation CONTRADICTS; and only if
there is a contradiction does the model get one chance to revise.

**The order is the whole design, and both directions are measured.** Putting
retrieved documentation in FRONT of a model took Win32 accuracy from 5/5 to
1/5 -- retrieved text displaces knowledge the model already had. Verifying
afterwards cannot do that: silence from the packs leaves the draft untouched,
so an answer that was already right is never re-opened.

Installed into `agent/turn_finalizer.py`, which is the one synchronous
chokepoint every path through `run_conversation` funnels into.
"""

from __future__ import annotations

import json
import logging
import os
import threading
import urllib.request

log = logging.getLogger("agent.conversation_loop")

#: Off unless explicitly enabled, because it costs a round trip on every turn
#: -- including the ones the model already answered correctly from memory.
#: Measured: ~11s added to a model that was already checking, which buys
#: nothing; ~11s added to one that was not, which buys the answer.
ENABLED = os.environ.get("ARGUS_VERIFY_AFTER", "").strip().lower() in {
    "1", "true", "yes", "on"}

ARGUS_URL = os.environ.get("ARGUS_URL", "http://127.0.0.1:8099/mcp")
ARGUS_TOKEN = os.environ.get("ARGUS_TOKEN", "")
VERIFY_TIMEOUT = float(os.environ.get("ARGUS_VERIFY_TIMEOUT", "90"))

#: Below this, a "draft" is an acknowledgement or a clarifying question, not a
#: claim worth checking. Verifying "Sure, which file?" wastes a round trip.
MIN_CHARS = 80


def _verify_via_mcp(text: str) -> str:
    """Call docs_verify on its own event loop, in its own thread.

    `finalize_turn` is synchronous but may be called from inside a running
    loop (the gateway is async), where `asyncio.run` raises. A dedicated
    thread gets a clean loop either way, and `join(timeout)` bounds the cost
    so a hung server delays a turn rather than hanging it forever.
    """
    box: dict = {}

    def _worker() -> None:
        import asyncio

        async def _call() -> str:
            from mcp import ClientSession
            from mcp.client.streamable_http import streamablehttp_client

            headers = {"Authorization": f"Bearer {ARGUS_TOKEN}"} if ARGUS_TOKEN else {}
            async with streamablehttp_client(ARGUS_URL, headers=headers) as (r, w, _):
                async with ClientSession(r, w) as session:
                    await session.initialize()
                    result = await session.call_tool("docs_verify", {"text": text})
                    return "".join(
                        getattr(c, "text", "") for c in (result.content or []))

        try:
            box["out"] = asyncio.run(_call())
        except BaseException as exc:
            # anyio wraps transport failures in a TaskGroup whose str() says
            # nothing; unwrap so the log names the real cause.
            subs = getattr(exc, "exceptions", None)
            inner = subs[0] if subs else exc
            box["err"] = f"{type(inner).__name__}: {inner}"

    thread = threading.Thread(target=_worker, name="argus-verify", daemon=True)
    thread.start()
    thread.join(timeout=VERIFY_TIMEOUT)
    if thread.is_alive():
        log.warning("Argus verify timed out after %ss; answer unchanged", VERIFY_TIMEOUT)
        return ""
    if "err" in box:
        log.warning("Argus verify unavailable (%s); answer unchanged", box["err"])
        return ""
    return box.get("out", "") or ""


def _has_contradiction(findings: str) -> bool:
    """True only when the packs actually disagree with the draft.

    `docs_verify` marks each identifier `confirmed`, `contradicted` or
    `unstated`. Only `contradicted` is a reason to re-open a finished answer;
    re-prompting on the others invites a correct answer to become a different
    one, which is a regression dressed as an improvement.
    """
    if not findings or not findings.strip() or findings.strip() in ("[]", "{}"):
        return False
    return "contradicted" in findings.lower()


def _revise(agent, draft: str, findings: str) -> str:
    """One correction pass, through the same model the turn used."""
    base = (getattr(agent, "base_url", "") or "").rstrip("/")
    model = getattr(agent, "model", "") or ""
    if not base or not model:
        return draft
    prompt = (
        "You wrote this answer:\n\n" + draft + "\n\n"
        "The documentation was then checked against it. Below is what it says.\n"
        "Where it CONTRADICTS your answer, correct that part and quote the "
        "documented string verbatim. Where it is silent, keep what you wrote "
        "-- silence is not disagreement. Return the corrected answer only.\n\n"
        + findings[:6000]
    )
    body = {
        "model": model,
        "messages": [{"role": "user", "content": prompt}],
        "stream": False,
        "temperature": 0,
    }
    url = base + ("/chat/completions" if base.endswith("/v1") else "/v1/chat/completions")
    try:
        req = urllib.request.Request(
            url, data=json.dumps(body).encode(),
            headers={"Content-Type": "application/json"})
        with urllib.request.urlopen(req, timeout=VERIFY_TIMEOUT) as resp:
            data = json.load(resp)
        revised = (data["choices"][0]["message"].get("content") or "").strip()
    except Exception as exc:
        log.warning("Argus verify revision failed (%s); answer unchanged", exc)
        return draft
    # An empty revision is a failed revision, not an improved answer.
    return revised or draft


def apply(agent, final_response):
    """Return the answer to ship: the draft, or its corrected form.

    Never raises. A verification that cannot run must cost the turn nothing --
    shipping the draft unverified is worse than shipping nothing only if you
    believe the verification, and a failed one says nothing either way.
    """
    if not ENABLED:
        return final_response
    try:
        text = (final_response or "").strip()
        if len(text) < MIN_CHARS:
            return final_response
        findings = _verify_via_mcp(text)
        if not _has_contradiction(findings):
            return final_response
        log.info("Argus contradicted the draft; revising")
        return _revise(agent, text, findings)
    except Exception:
        log.warning("Argus verify-after failed; answer unchanged", exc_info=True)
        return final_response
