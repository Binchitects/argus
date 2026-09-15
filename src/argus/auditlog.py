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


def denied(*, reason: str, path: str) -> None:
    """A request refused at the auth gate, before any tool or identity."""
    _emit({"event": "denied", "reason": reason, "path": path})


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
              failed: int, up_to_date: int, reason: str | None = None) -> None:
    """The pass is over. `returncode` is the process exit code the caller sees.

    Emitted for EVERY outcome, including the ones that never reach a
    repository -- an unreachable GitLab, a refusal, a missing ctags. Those are
    the runs an operator most needs to see in a chart, and they are exactly the
    ones that would leave no trace if this were only written at the end of a
    successful walk.
    """
    _emit({
        # `outcome` rather than only the numeric code, because it is a label:
        # this is what makes "how many runs failed this week" a one-line query.
        "event": "index_end",
        "outcome": "ok" if returncode == 0 else "error",
        "returncode": returncode,
        "duration_ms": duration_ms,
        "repos": repos, "failed": failed, "up_to_date": up_to_date,
        # Set only on the early exits, where "repos" is 0 and the code alone
        # does not say which of several preconditions was not met.
        "reason": reason,
    })
