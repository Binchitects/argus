"""The audit trail as a log stream: one JSON line per tool call or refusal.

The audit table in the sidecar database is the record, and stays one. It is
also invisible to anything that reads logs: an operator watching Grafana could
not see who asked Argus what, which calls were refused, or which were slow.
This writes the same fact to stdout as it is recorded, so a log shipper
(Promtail -> Loki in llm-stack) can chart and search it.

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
