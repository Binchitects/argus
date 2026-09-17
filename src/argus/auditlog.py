"""The audit trail as a log stream: one JSON line per tool call or refusal.

The audit table in the sidecar database is the record, and stays one. It is
also invisible to anything that reads logs: an operator watching Grafana could
not see who asked Argus what, which calls were refused, or which were slow.
This writes the same fact to stdout as it is recorded, so a log shipper
(Promtail -> Loki in stack) can chart and search it.

Each line is a single JSON object, prefixed by nothing, e.g.

    {"event": "tool_call", "tool": "find_symbol", "user": "alice", ...}

`ARGUS_AUDIT_LOG=0` turns the stream off; the audit table is unaffected.
"""
from __future__ import annotations

import json
import logging
import os
import sys
import time

_ARG_MAX = 300


class _StdoutHandler(logging.Handler):
    """Writes to whatever sys.stdout is at emit time (a captured stream in tests)."""

    def emit(self, record: logging.LogRecord) -> None:
        try:
            sys.stdout.write(self.format(record) + "\n")
            sys.stdout.flush()
        except Exception:
            self.handleError(record)


logger = logging.getLogger("argus.audit")
if not logger.handlers:
    _handler = _StdoutHandler()
    _handler.setFormatter(logging.Formatter("%(message)s"))
    logger.addHandler(_handler)
    logger.setLevel(logging.INFO)
    # Not through the root logger: FastMCP installs a rich handler there that
    # wraps long lines, which would split one JSON object across several.
    logger.propagate = False


def _enabled() -> bool:
    return os.environ.get("ARGUS_AUDIT_LOG", "1").strip().lower() not in ("0", "false", "no", "off")


def _clip(value):
    if isinstance(value, str) and len(value) > _ARG_MAX:
        return value[:_ARG_MAX] + "..."
    if isinstance(value, list):
        return [_clip(v) for v in value[:20]]
    if isinstance(value, dict):
        return {k: _clip(v) for k, v in list(value.items())[:20]}
    return value


def _emit(fields: dict) -> None:
    if not _enabled():
        return
    fields = {"ts": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()), **fields}
    try:
        logger.info(json.dumps(fields, default=str, ensure_ascii=False))
    except Exception:  # a log line must never break a tool call
        pass


def tool_call(*, tool: str, user: str | None, user_id: int | None, args: dict,
              repos_visible: int | None, duration_ms: float, error: BaseException | None) -> None:
    """One finished tool call. `args` must already be token-free (see tools._with_audit)."""
    if error is None:
        outcome = "ok"
    elif type(error).__name__ == "AccessNotice":
        outcome = "no_access"
    else:
        outcome = "error"
    _emit({
        "event": "tool_call", "tool": tool, "user": user, "user_id": user_id,
        "outcome": outcome, "error": type(error).__name__ if error is not None else None,
        "duration_ms": round(duration_ms, 1), "repos_visible": repos_visible,
        "args": _clip(args),
    })


def denied(*, reason: str, path: str, detail: str | None = None) -> None:
    """A request refused at the auth gate, before any tool or identity.

    `reason` is the short machine-readable class and is what the Grafana
    panels group by. `detail` is the sentence the caller was actually told, and
    it is the difference between a diagnosable refusal and a mystery.

    Measured: Open WebUI reports a 401 from Argus as "failed to connect to
    argus", which sends whoever sees it looking for a network problem. The
    useful answer -- "No GitLab account matches admin@llm.localhost" -- was in
    the response body and nowhere else, so `docker compose logs argus` had
    only `reason=token_rejected` to offer. Both halves are logged now.
    """
    _emit({"event": "denied", "reason": reason, "path": path,
           "detail": detail})


# --- indexing --------------------------------------------------------------
#
# `argus index` runs as a child of the serve process and its output is mirrored
# to the container log, so these lines reach Promtail -> Loki and the operator
# can watch a pass without an ssh session.
#
# `repo` and `branch` are deliberately NOT labels. The Promtail config lifts
# `event` and `outcome` into labels because each has a handful of values, and
# keeps the person in the line for the same reason stated there: a label per
# repository would multiply streams, and an estate can have thousands. Read
# them at query time with `| json | repo="g/alpha"`.


def index_start(*, branches: list[str], allow_partial: bool, repos: int) -> None:
    """A pass has begun, after enumeration and the preflight checks."""
    _emit({
        "event": "index_start",
        "repos": repos,
        "branches": list(branches),
        # Worth a field of its own: `true` means the index may be silently
        # partial, which is the one thing about a run that nothing downstream
        # can detect after the fact.
        "allow_partial": bool(allow_partial),
    })


def index_repo(*, repo: str, branch: str, outcome: str,
               duration_ms: float | None = None, indexed: int | None = None,
               deleted: int | None = None, skipped: int | None = None,
               errors: int | None = None, timed_out: bool | None = None,
               symbols_failed: bool | None = None,
               error: str | None = None) -> None:
    """One repository, at one branch. `outcome` is what a dashboard groups by.

    `up_to_date` is separated from `ok` on purpose: a pass where every repo is
    up to date and a pass that reindexed everything both exit 0, and an
    operator watching a dashboard needs to tell "nothing changed" from "it is
    doing work" without reading the log text.
    """
    _emit({
        "event": "index_repo", "repo": repo, "branch": branch,
        "outcome": outcome, "duration_ms": duration_ms,
        "indexed": indexed, "deleted": deleted, "skipped": skipped,
        "errors": errors, "timed_out": timed_out,
        "symbols_failed": symbols_failed, "error": error,
    })


def index_end(*, returncode: int, duration_ms: float, repos: int,
              failed: int, up_to_date: int, reason: str | None = None,
              empty: int = 0) -> None:
    """The pass is over. `returncode` is the process exit code the caller sees.

    Emitted for EVERY outcome, including the ones that never reach a
    repository -- an unreachable GitLab, a refusal, a missing ctags. Those are
    the runs an operator most needs to see in a chart, and they are exactly the
    ones that would leave no trace if this were only written at the end of a
    successful walk.

    `repos` counts PROJECTS and `up_to_date` counts REFS, which is why `empty`
    exists: a project with no branches produces neither, and without it a run
    could report four repositories, three up to date and one nowhere.
    """
    _emit({
        # `outcome` rather than only the numeric code, because it is a label:
        # this is what makes "how many runs failed this week" a one-line query.
        "event": "index_end",
        "outcome": "ok" if returncode == 0 else "error",
        "returncode": returncode,
        "duration_ms": duration_ms,
        "repos": repos, "failed": failed, "up_to_date": up_to_date,
        # Enumerated but with no refs: an empty repository. Not a failure, and
        # not something to subtract from a health percentage either.
        "empty": empty,
        # Set only on the early exits, where "repos" is 0 and the code alone
        # does not say which of several preconditions was not met.
        "reason": reason,
    })


def index_webhook(*, repo: str, started: bool = False, queued: int = 0,
                  collapsed: int = 0) -> None:
    """A push webhook asked for a repository to be indexed.

    One event per decision rather than one per request, so "the webhook is
    firing but nothing is being indexed" is answerable from the log: a
    `queued` with no `started` after it means passes are not completing, and a
    `collapsed` means the queue overflowed and the next pass covers everything
    instead. `repo` is `*` for a full pass.
    """
    fields: dict = {"event": "index_webhook", "repo": repo}
    if started:
        fields["outcome"] = "started"
    elif collapsed:
        fields["outcome"] = "collapsed"
        fields["collapsed_from"] = collapsed
    else:
        fields["outcome"] = "queued"
        fields["queued"] = queued
    _emit(fields)


def index_scheduled(*, interval: int, first_pass_in: float | None = None,
                    reason: str | None = None, skipped: str | None = None) -> None:
    """The automatic reindex timer, saying what it decided.

    Two things an operator otherwise has to guess at. `first_pass_in` answers
    "it has been up for ten minutes, why is nothing indexed?" -- and `reason`
    says whether that pass is happening because the timer came round or
    because the index was found stale at startup, which is the difference
    between a normal tick and a deployment that came up behind.
    `skipped` records a tick that found a run already in flight: not an error,
    but the only evidence that the interval is shorter than a pass takes.
    """
    fields: dict = {"event": "index_scheduled", "interval": interval}
    if first_pass_in is not None:
        fields["first_pass_in"] = first_pass_in
        fields["reason"] = reason or "the index is current"
    if skipped is not None:
        fields["skipped"] = skipped
        fields["outcome"] = "skipped"
    _emit(fields)
