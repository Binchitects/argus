"""Prometheus metrics for the index, so it cannot go stale unnoticed.

WHY THIS EXISTS

The stack had nineteen alert rules -- GPU temperature, disk pressure, engine
down, container restart loops -- and not one of them mentioned the index. An
index that quietly stops updating is the exact failure this whole project
exists to prevent: every answer stays as confident as it was on the day the
data was good, and nothing anywhere says otherwise. `index_status` will report
staleness if a person asks; nobody asks.

Two consumers, one source of truth. The alert rule reads
``argus_index_stale``; the admin panel's Overview reads the same
``stale_repos`` count so the number on screen and the number that pages you
cannot disagree.

WHY THE EXPORTER, NOT THE ALERT, DECIDES WHAT "STALE" MEANS

Because the threshold is a property of the deployment, not of Prometheus: it
depends on the index interval and how much slack the operator wants.
``ARGUS_INDEX_STALE_AFTER`` defaults to 3600s -- four times the documented
900s polling cadence, so a single slow pass does not page anyone, but a
stopped index does within the hour.

The raw timestamps are exported as well, so a rule can be written against
whatever threshold it likes instead of trusting this one.
"""
from __future__ import annotations

import os
import sqlite3
import time
from pathlib import Path

#: How long without a successful pass before a repository counts as stale.
#: Four times the documented `--interval 900`, so one slow or skipped pass is
#: not an incident but an hour of silence is.
DEFAULT_STALE_AFTER = 3600

#: A metric line is prefixed with this so a scrape is obviously Argus's.
PREFIX = "argus"


def stale_after() -> int:
    """Seconds without a pass before a repo is stale. Read per call, not at
    import, so it can be changed and tested like any other setting."""
    try:
        return int(os.environ.get("ARGUS_INDEX_STALE_AFTER", DEFAULT_STALE_AFTER))
    except ValueError:
        return DEFAULT_STALE_AFTER


def _escape(value: object) -> str:
    """Prometheus label values escape backslash, quote and newline only."""
    return (str(value).replace("\\", "\\\\").replace('"', '\\"')
            .replace("\n", "\\n"))


def _line(name: str, value: object, labels: dict | None = None,
          help_text: str = "", kind: str = "gauge") -> list[str]:
    out = []
    if help_text:
        out.append(f"# HELP {name} {help_text}")
        out.append(f"# TYPE {name} {kind}")
    if labels:
        rendered = ",".join(f'{k}="{_escape(v)}"' for k, v in labels.items())
        out.append(f"{name}{{{rendered}}} {value}")
    else:
        out.append(f"{name} {value}")
    return out


def snapshot(db_path: Path | str, now: float | None = None) -> dict:
    """Per-repo freshness and size, plus the aggregate the alert reads.

    One query for the repositories and one grouped count for each of files and
    symbols. All three are index-backed -- measured sub-millisecond on the
    reference index -- so a 15-second scrape costs nothing.
    """
    now = time.time() if now is None else now
    limit = stale_after()
    repos: list[dict] = []
    conn = sqlite3.connect(f"file:{Path(db_path).as_posix()}?mode=ro", uri=True)
    conn.row_factory = sqlite3.Row
    try:
        counts: dict[tuple[int, str], dict[str, int]] = {}
        for table in ("files", "symbols"):
            for row in conn.execute(
                    f"SELECT repo_id, COUNT(*) AS n FROM {table} GROUP BY repo_id"):
                counts.setdefault((row["repo_id"], ""), {})[table] = row["n"]
        for row in conn.execute(
                "SELECT id, path_with_namespace, branch, default_branch,"
                "       last_run_at, last_indexed_at, last_run_timed_out,"
                "       last_run_symbols_failed, last_run_error"
                "  FROM repos ORDER BY path_with_namespace, branch"):
            last_run = row["last_run_at"]
            age = None if last_run is None else max(0.0, now - float(last_run))
            by_table = counts.get((row["id"], ""), {})
            repos.append({
                "repo": row["path_with_namespace"],
                "branch": row["branch"] or row["default_branch"],
                "is_default": (row["branch"] or row["default_branch"])
                              == row["default_branch"],
                "last_run_at": last_run,
                "last_indexed_at": row["last_indexed_at"],
                "age_seconds": age,
                # A repo never indexed is stale by definition: there is nothing
                # to answer from, which is worse than being out of date.
                "stale": age is None or age > limit,
                "timed_out": bool(row["last_run_timed_out"]),
                "symbols_failed": bool(row["last_run_symbols_failed"]),
                "error": row["last_run_error"],
                "files": by_table.get("files", 0),
                "symbols": by_table.get("symbols", 0),
            })
    finally:
        conn.close()

    from .. import __version__ as version

    return {
        "now": now,
        "stale_after": limit,
        "repos": repos,
        "stale_repos": sum(1 for r in repos if r["stale"]),
        "errored_repos": sum(1 for r in repos if r["error"]),
        "version": version,
    }


def render_error(exc: BaseException) -> str:
    """What to emit when the index cannot be read at all.

    The scrape still has to succeed. A 500 makes Prometheus record ``up 0``,
    which reads as "Argus is down" -- but Argus answered, it just could not
    see its own index, and that is a different incident needing a different
    response. So the response stays 200 and ``argus_index_scrape_ok 0`` is the
    signal, which is also the one series guaranteed to be present on every
    scrape, healthy or not. Alert on it, not on ``up``.
    """
    out = _line(f"{PREFIX}_index_build_info", 0, {"version": "unknown"},
                "Argus build, always 1", "gauge")
    out += _line(f"{PREFIX}_index_scrape_ok", 0,
                 help_text="1 when this scrape read the index successfully")
    # append(), NOT +=: `out` is a list of LINES, and `+=` on a list with a
    # string extends it with that string's individual characters, which joins
    # back into one letter per line. Caught by the test below.
    out.append(f"# argus could not read the index: {type(exc).__name__}")
    return "\n".join(out) + "\n"


def render(db_path: Path | str) -> str:
    """The whole exposition, in Prometheus's text format."""
    snap = snapshot(db_path)
    out = _line(f"{PREFIX}_index_build_info", 1, {"version": snap["version"]},
                "Argus build, always 1", "gauge")
    out += _line(f"{PREFIX}_index_scrape_ok", 1,
                 help_text="1 when this scrape read the index successfully")
    out += _line(f"{PREFIX}_index_repos", len(snap["repos"]),
                 help_text="Repositories (at one branch each) in the index")
    out += _line(f"{PREFIX}_index_stale_repos", snap["stale_repos"],
                 help_text=f"No successful pass within "
                           f"{snap['stale_after']}s, or never indexed")
    out += _line(f"{PREFIX}_index_errored_repos", snap["errored_repos"],
                 help_text="Repositories whose last pass recorded an error")
    out += _line(f"{PREFIX}_index_stale_after_seconds", snap["stale_after"],
                 help_text="Threshold this build applies to the stale gauges")

    for r in snap["repos"]:
        labels = {"repo": r["repo"], "branch": r["branch"]}
        # A repo that has never run exports a timestamp of 0 rather than
        # nothing, so `time() - last_run > limit` is true for it in a rule too
        # and it cannot hide from an alert by being absent.
        out += _line(f"{PREFIX}_index_last_run_timestamp_seconds",
                     r["last_run_at"] or 0, labels,
                     "Unix time of the last pass, 0 if it has never run")
        out += _line(f"{PREFIX}_index_last_indexed_timestamp_seconds",
                     r["last_indexed_at"] or 0, labels,
                     "Unix time of the last pass that changed the index")
        out += _line(f"{PREFIX}_index_files", r["files"], labels,
                     "Files indexed in this repository")
        out += _line(f"{PREFIX}_index_symbols", r["symbols"], labels,
                     "Symbols indexed in this repository")
        out += _line(f"{PREFIX}_index_stale", 1 if r["stale"] else 0, labels,
                     "1 when this repository has gone stale")
        out += _line(f"{PREFIX}_index_timed_out", 1 if r["timed_out"] else 0,
                     labels, "1 when the last pass hit its time budget")
        out += _line(f"{PREFIX}_index_symbols_failed",
                     1 if r["symbols_failed"] else 0, labels,
                     "1 when symbol extraction failed on the last pass")
        out += _line(f"{PREFIX}_index_errored", 1 if r["error"] else 0, labels,
                     "1 when the last pass recorded an error")
    return "\n".join(out) + "\n"
