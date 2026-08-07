"""Health and progress indicators, computed from a live index.

Chosen on one rule: **every indicator here would have caught a defect this
project actually shipped.** A metric that only ever goes up is decoration; the
useful ones are the ones that would have gone visibly wrong.

* `symbols_per_1k_files` is the ctags canary. Phase 1 shipped a defect where
  a missing ctags let files be marked indexed with zero symbols, permanently.
  Everything else looked healthy -- file counts rose, no errors were recorded.
  This number would have cratered.
* `ambiguous_include_rate` is the leading indicator for `which_repo`. A rising
  share means many repos ship headers with the same basename, so the
  dependency graph thins out and answers degrade -- a property of the `-I`
  layout that no tool tuning fixes. Measured at 1.2% on four real C projects.
* `repos_unhealthy` and `stalest_repo_hours` catch the failure this project
  has hit twice in different forms: something stops working, keeps reporting
  faithfully, and nobody reads the report. A repo that has failed to mirror
  for three weeks is invisible in every other number here.
* `resolved_include_rate` and `cross_repo_edges` are how you notice the graph
  quietly emptying -- the resolver refuses to guess, so a layout change can
  drop edges silently rather than producing wrong ones.

Deliberately not here: anything that cannot be computed from the index. The
retrieval-quality numbers that matter most -- `which_repo` top-1 accuracy,
`docs_search` usefulness -- need a human judging real answers, and are tracked
by hand in `docs/kpis.md`. Inventing an automatic proxy for them would produce
a number that rises while the answers get worse.
"""

from __future__ import annotations

import sqlite3
import time
from pathlib import Path


def collect(conn: sqlite3.Connection, db_path: Path | str | None = None,
            now: int | None = None) -> dict:
    """Compute every automatic indicator from an open index."""
    now = int(time.time()) if now is None else now

    def scalar(sql: str, default=0):
        row = conn.execute(sql).fetchone()
        value = row[0] if row else None
        return default if value is None else value

    repos = scalar("SELECT COUNT(*) FROM repos")
    files = scalar("SELECT COUNT(*) FROM files")
    symbols = scalar("SELECT COUNT(*) FROM symbols")
    includes = scalar("SELECT COUNT(*) FROM includes")

    indexed = scalar("SELECT COUNT(*) FROM repos WHERE last_indexed_at IS NOT NULL")
    unhealthy = scalar(
        "SELECT COUNT(*) FROM repos WHERE last_run_error IS NOT NULL"
        "    OR last_run_timed_out = 1 OR last_run_symbols_failed = 1")

    ages = [
        (now - row[0]) / 3600.0
        for row in conn.execute(
            "SELECT last_indexed_at FROM repos WHERE last_indexed_at IS NOT NULL")
    ]
    ages.sort()

    resolution = {
        row["resolution"]: row["n"]
        for row in conn.execute(
            "SELECT resolution, COUNT(*) AS n FROM includes GROUP BY resolution")
    }
    resolved = resolution.get("resolved", 0)
    ambiguous = resolution.get("ambiguous", 0)

    size_mb = None
    if db_path is not None and Path(db_path).exists():
        size_mb = round(Path(db_path).stat().st_size / (1024 * 1024), 1)

    return {
        # Coverage -- is the index complete?
        "repos": repos,
        "repos_indexed": indexed,
        "repos_never_indexed": repos - indexed,
        "files": files,
        "symbols": symbols,
        # The ctags canary. A collapse here means symbol extraction broke
        # while every other number kept looking healthy.
        "symbols_per_1k_files": _per_1k(symbols, files),
        # Freshness -- is anyone noticing that something stopped working?
        "repos_unhealthy": unhealthy,
        "median_repo_age_hours": _round(_median(ages)),
        "stalest_repo_hours": _round(max(ages)) if ages else None,
        # Graph quality -- the leading indicators for which_repo.
        "includes": includes,
        "resolved_include_rate": _rate(resolved, includes),
        "ambiguous_include_rate": _rate(ambiguous, includes),
        "cross_repo_edges": scalar("SELECT COUNT(*) FROM repo_deps"),
        # Cost.
        "index_mb": size_mb,
        "mb_per_1k_files": _round(size_mb / files * 1000) if size_mb and files else None,
        "collected_at": now,
    }


def _median(values: list[float]) -> float | None:
    if not values:
        return None
    mid = len(values) // 2
    if len(values) % 2:
        return values[mid]
    return (values[mid - 1] + values[mid]) / 2


def _rate(part: int, whole: int) -> float | None:
    return round(part / whole, 4) if whole else None


def _per_1k(part: int, whole: int) -> float | None:
    return round(part / whole * 1000, 1) if whole else None


def _round(value: float | None) -> float | None:
    return None if value is None else round(value, 1)


#: Indicators where a *falling* number is the warning, so a reader does not
#: have to remember which way each one points.
LOWER_IS_BETTER = frozenset({
    "repos_never_indexed", "repos_unhealthy", "ambiguous_include_rate",
    "median_repo_age_hours", "stalest_repo_hours", "mb_per_1k_files",
})
