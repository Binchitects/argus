"""Rendering checks for the admin panel.

Plain asserts rather than pytest. This image holds the LiteLLM master key and
writes Authelia's user database; pulling a test framework into it to check a
handful of strings is not a trade worth making, and a script that exits
non-zero gates the build just as well.

What these exist for, concretely: the Indexing card's log block rendered the
LIST OF REPOSITORIES instead of the run log, because the closure that built it
referenced a name that a later line rebound to the repos-table slice. Python
resolves that name when the closure runs, so the card looked plausible and was
wrong. Nothing else in the deployment could have caught it.
"""
from __future__ import annotations

import html as _html

import app as panel

FAILURES: list[str] = []


def check(condition: bool, message: str) -> None:
    if not condition:
        FAILURES.append(message)


def status(*, state="idle", tail=None, repos=None, returncode=0,
           finished=1_700_000_000.0, allow_partial=False):
    return {
        "job": {"state": state, "branches": [], "started": 1_699_999_000.0,
                "finished": finished, "returncode": returncode,
                "tail": tail or [], "allow_partial": allow_partial},
        "repos": repos or [],
    }


REPOS = [
    {"repo": "group/alpha", "branch": "main", "default_branch": "main",
     "last_run_at": 1_700_000_000, "timed_out": False, "symbols_failed": 0},
    {"repo": "group/beta", "branch": "main", "default_branch": "main",
     "last_run_at": 1_700_000_000, "timed_out": True, "symbols_failed": 3},
]
TAIL = ['{"ts": "2026-01-01T00:00:00Z", "event": "index_start", "repos": 2}',
        "group/alpha: up to date",
        '{"ts": "2026-01-01T00:00:01Z", "event": "index_end", "returncode": 1}']

panel.ARGUS_ADMIN_TOKEN = "test-token"


def card(payload, *, is_admin=True):
    panel._argus = lambda path, body=None: payload
    return panel.indexing_card(is_admin=is_admin)


# --- the bug this file exists for ------------------------------------------

html = card(status(tail=TAIL, repos=REPOS, returncode=1))
check("group/alpha: up to date" in html, "the run log is not in the card")
check("index_end" in html, "the structured lines are not in the log block")
# Unescape first. The first version of this check compared against raw quotes
# and passed against the bug: the card HTML-escapes what it renders, so the
# giveaway was present as `{&#x27;repo&#x27;` and the assertion looked for
# `{'repo'`. `default_branch` is the clean discriminator -- it is a key of the
# repository rows and appears in no log line, escaped or not.
flat = _html.unescape(html)
check("{'repo': 'group/alpha'" not in flat,
      "the log block rendered the repository LIST instead of the log")
check("default_branch" not in flat,
      "the log block rendered the repository LIST instead of the log")
check("group/alpha" in html, "the per-repo table lost its rows")

# --- the log survives the run ----------------------------------------------

check("Log from the last run" in html,
      "the log is dropped once the run finishes, which is when it is needed")
check("index_end" in html, "the log is empty after the run")

running = card(status(state="running", tail=TAIL, repos=REPOS))
check("Run log" in running, "a running pass should label the log as live")
check("refresh" in running, "a running pass should keep refreshing the page")
check("disabled" in running, "the button must be disabled while a pass runs")

# --- exit codes are explained, not just printed ----------------------------

check("exit 1" in html and "unhealthy" in html,
      "exit 1 is shown as a bare number instead of naming what it means")
check("completed" in card(status(returncode=0)),
      "exit 0 should read as completed")
check("ctags" in card(status(returncode=4)),
      "exit 4 should name the preflight cause")

# --- the partial-enumeration opt-in ----------------------------------------

refused = card(status(returncode=3))
check("Index what the token can see" in refused,
      "there is no way to opt into a partial index from the panel")
check("admin rights" in refused,
      "an exit-3 refusal should say what to do about it")
check("checked" in card(status(allow_partial=True)),
      "the checkbox does not reflect the mode the last run used")
check("checked" not in card(status(allow_partial=False)),
      "partial enumeration must not look enabled by default")

# --- errors and gating ------------------------------------------------------

check("Could not read per-repo" not in card(status()),
      "the card warns about a per-repo read failure that did not happen")
with_error = {"job": {"state": "idle", "repos_error": "NameError: nope"},
              "repos": []}
check("NameError" in card(with_error),
      "a swallowed status-route error is hidden from the operator")
check(card(status(), is_admin=False) == "",
      "the indexing card must not render for a non-admin")

panel.ARGUS_ADMIN_TOKEN = ""
check(card(status()) == "",
      "the card must not render when Argus's admin surface is unconfigured")

if FAILURES:
    for line in FAILURES:
        print(f"FAIL: {line}")
    raise SystemExit(1)
print(f"admin panel: {len(FAILURES)} failures, all rendering checks passed")
