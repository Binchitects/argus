#!/usr/bin/env python3
"""Create Open WebUI model presets, one per thinking level.

WHY THIS EXISTS

`llama-server` runs with `--jinja`, so the model's own chat template decides
what thinking means, and it reads that from Jinja variables `reasoning_effort`
and `enable_thinking`. Today those are set once, for the whole deployment, in
`.env` (MODEL_REASONING_EFFORT / MODEL_ENABLE_THINKING) -- so everyone gets the
same thinking budget in every chat, and changing it means editing `.env` and
restarting.

Open WebUI looks like it already has this control: `reasoning_effort` is one of
the params its per-model and per-chat panels accept. It does not work here, and
the way it fails is silent. Measured against this stack, same prompt:

    no override                        reasoning=270 chars
    top-level reasoning_effort=low     reasoning=270 chars   <- ignored
    chat_template_kwargs low           reasoning=217 chars   <- honoured
    chat_template_kwargs thinking off  reasoning=  0 chars   <- honoured

LiteLLM drops a top-level `reasoning_effort` when the backend is a custom
`openai/` api_base, which is what this stack is. So the field Open WebUI offers
does nothing, and nothing says so.

What DOES reach the template is a `chat_template_kwargs` object on the request.
Open WebUI has no field for that, but it has `custom_params`: a dict it
deep-merges into the outgoing body before sending it (see
`apply_model_params_to_body_openai` in `open_webui/utils/payload.py`). A model
whose params contain

    {"custom_params": {"chat_template_kwargs": {"reasoning_effort": "low"}}}

therefore sends exactly what the template reads. Verified end to end through
Open WebUI, on a prompt with something to deliberate about:

    deep (xhigh)   2879 chars of reasoning, 1114 completion tokens
    fast  (low)    1251 chars of reasoning,  667 completion tokens

WHY IT RUNS AS A BACKGROUND JOB

A preset needs an owner (`model.user_id`), and a model with a NULL owner is not
listed at all -- measured, not assumed. On a fresh deployment no user exists
until someone signs in through SSO for the first time, which happens after this
container has started. So this waits, quietly, for an admin row to appear; on
every later boot the wait is over immediately.

It is idempotent: presets are keyed by a stable id, so re-running replaces them
rather than accumulating copies. It never raises -- a preset that could not be
created must not stop Open WebUI from starting.
"""
from __future__ import annotations

import json
import os
import sqlite3
import sys
import time

DB = os.environ.get("WEBUI_DB", "/app/backend/data/webui.db")

#: How long to wait for the first admin to sign in. Generous on purpose: this
#: is a background job and the wait costs nothing, while giving up early means
#: the presets silently never appear.
#:
#: Read at CALL time, not at import. A module-level read cannot be changed by a
#: test, and the first version of this file hung a test suite for thirty
#: minutes because the override was set after import and quietly ignored.
DEFAULT_WAIT_SECONDS = 1800
POLL_SECONDS = 10


def wait_seconds() -> int:
    try:
        return int(os.environ.get("THINKING_PRESETS_WAIT", DEFAULT_WAIT_SECONDS))
    except ValueError:
        return DEFAULT_WAIT_SECONDS


def log(message: str) -> None:
    print(f"seed-presets: {message}", flush=True)


def levels() -> list[tuple[str, str]]:
    """Parse THINKING_PRESETS, e.g. ``xhigh:Deep think,low:Quick,off:None``.

    `off` is not a level the engine knows -- it maps to `enable_thinking:
    false`, which is the switch that actually turns thinking off. The engine
    raises on an unrecognised `reasoning_effort` ("Unexpected reasoning
    effort"), so a typo here is visible in the chat rather than silent.
    """
    raw = os.environ.get("THINKING_PRESETS", "")
    out: list[tuple[str, str]] = []
    for item in raw.split(","):
        item = item.strip()
        if not item:
            continue
        level, _, label = item.partition(":")
        level, label = level.strip().lower(), label.strip()
        if level:
            out.append((level, label or level))
    return out


def preset_params(level: str) -> dict:
    if level == "off":
        return {"custom_params": {"chat_template_kwargs": {"enable_thinking": False}}}
    return {"custom_params": {"chat_template_kwargs": {"reasoning_effort": level}}}


def admin_id(conn: sqlite3.Connection) -> str | None:
    """The first admin's id, or None if there is not one yet.

    A missing `user` table means "not ready", not "broken": Open WebUI creates
    the database file first and the schema moments later, so there is a window
    where the file exists and the table does not. Treating that as an error
    would make this fail on exactly the fresh deployments it exists for.
    """
    try:
        row = conn.execute(
            "SELECT id FROM user WHERE role = 'admin' ORDER BY created_at LIMIT 1"
        ).fetchone()
    except sqlite3.OperationalError:
        return None
    return row[0] if row else None


def seed() -> int:
    model = os.environ.get("MODEL_NAME", "").strip()
    wanted = levels()
    if not model or not wanted:
        log("MODEL_NAME or THINKING_PRESETS is empty; nothing to do")
        return 0

    # The database is created by Open WebUI on its first start, which may be
    # after this process begins.
    deadline = time.time() + wait_seconds()
    while not os.path.exists(DB) and time.time() < deadline:
        time.sleep(POLL_SECONDS)

    conn = sqlite3.connect(DB, timeout=30)
    try:
        owner = admin_id(conn)

        while owner is None and time.time() < deadline:
            time.sleep(POLL_SECONDS)
            owner = admin_id(conn)

        if owner is None:
            log(f"no admin user after {wait_seconds()}s; presets not created. "
                f"They appear after an admin has signed in once and this "
                f"container restarts.")
            return 0

        now = int(time.time())
        for level, label in wanted:
            preset_id = f"{model}-think-{level}".replace("/", "-").lower()
            name = f"{model} · {label}"
            conn.execute(
                "INSERT INTO model (id, user_id, base_model_id, name, params,"
                " meta, created_at, updated_at, is_active)"
                " VALUES (?,?,?,?,?,?,?,?,1)"
                " ON CONFLICT(id) DO UPDATE SET"
                "   user_id=excluded.user_id, base_model_id=excluded.base_model_id,"
                "   name=excluded.name, params=excluded.params,"
                "   updated_at=excluded.updated_at, is_active=1",
                (preset_id, owner, model, name, json.dumps(preset_params(level)),
                 json.dumps({"description":
                             f"{label} — reasoning_effort={level}"
                             if level != "off" else
                             f"{label} — thinking disabled"}), now, now),
            )
            log(f"  {preset_id:34} {name}")
        conn.commit()
        log(f"{len(wanted)} preset(s) ready for {model}")
        return len(wanted)
    finally:
        conn.close()


if __name__ == "__main__":
    try:
        seed()
    except Exception as exc:  # noqa: BLE001 - must never stop Open WebUI
        log(f"could not seed presets: {type(exc).__name__}: {exc}")
    sys.exit(0)
