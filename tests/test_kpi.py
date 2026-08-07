"""Tests for the health indicators.

Each indicator here exists because it would have caught a defect this project
actually shipped. These tests check that it still would.
"""

from __future__ import annotations

import pytest

from argus import kpi
from argus.store import writes
from argus.store.db import open_db

NOW = 1_000_000


@pytest.fixture
def conn(tmp_path):
    c = open_db(tmp_path / "i.db")
    yield c
    c.close()


def _repo(conn, gid, name, indexed_at=None):
    rid = writes.upsert_repo(conn, gitlab_id=gid, path_with_namespace=name,
                             default_branch="main", http_url="u")
    if indexed_at is not None:
        conn.execute("UPDATE repos SET last_indexed_at = ? WHERE id = ?",
                     (indexed_at, rid))
    conn.commit()
    return rid


def _file(conn, rid, path, sha):
    return writes.upsert_file(conn, repo_id=rid, path=path, lang="c",
                              size=1, blob_sha=sha, content="x")


def test_an_empty_index_reports_zeroes_rather_than_dividing_by_zero(conn):
    data = kpi.collect(conn, now=NOW)
    assert data["repos"] == 0
    assert data["symbols_per_1k_files"] is None
    assert data["resolved_include_rate"] is None
    assert data["stalest_repo_hours"] is None


def test_symbols_per_1k_files_collapses_when_extraction_breaks(conn):
    """The ctags canary. Phase 1 shipped a defect where a missing ctags let
    files be marked indexed with ZERO symbols, permanently -- and every other
    number stayed healthy: file counts rose, no errors were recorded."""
    rid = _repo(conn, 1, "g/a", indexed_at=NOW)
    for i in range(10):
        fid = _file(conn, rid, f"f{i}.c", f"s{i}")
        writes.replace_symbols(conn, rid, fid, [
            {"name": f"fn{i}", "kind": "function", "line": 1, "end_line": 2,
             "signature": "()", "scope": None, "is_public": 1},
        ], f"s{i}")
    conn.commit()
    healthy = kpi.collect(conn, now=NOW)["symbols_per_1k_files"]
    assert healthy == 1000.0

    # ctags stops working: files keep arriving, symbols do not.
    for i in range(10, 20):
        _file(conn, rid, f"f{i}.c", f"s{i}")
    conn.commit()
    broken = kpi.collect(conn, now=NOW)["symbols_per_1k_files"]
    assert broken < healthy / 1.5, (
        f"extraction halved but the canary barely moved: {healthy} -> {broken}")


def test_unhealthy_repos_are_counted_however_they_failed(conn):
    """A repo that has failed to mirror for weeks is invisible in every other
    number here."""
    _repo(conn, 1, "g/ok", indexed_at=NOW)
    bad = _repo(conn, 2, "g/err", indexed_at=NOW)
    timed = _repo(conn, 3, "g/slow", indexed_at=NOW)
    nosyms = _repo(conn, 4, "g/nosyms", indexed_at=NOW)
    conn.execute("UPDATE repos SET last_run_error = 'boom' WHERE id = ?", (bad,))
    conn.execute("UPDATE repos SET last_run_timed_out = 1 WHERE id = ?", (timed,))
    conn.execute("UPDATE repos SET last_run_symbols_failed = 1 WHERE id = ?", (nosyms,))
    conn.commit()

    assert kpi.collect(conn, now=NOW)["repos_unhealthy"] == 3


def test_staleness_reports_the_worst_repo_not_just_the_average(conn):
    """An average hides one repo that stopped updating three weeks ago."""
    _repo(conn, 1, "g/fresh", indexed_at=NOW - 3600)          # 1h
    _repo(conn, 2, "g/fresh2", indexed_at=NOW - 7200)         # 2h
    _repo(conn, 3, "g/forgotten", indexed_at=NOW - 3600 * 500)  # 500h

    data = kpi.collect(conn, now=NOW)
    assert data["median_repo_age_hours"] == 2.0
    assert data["stalest_repo_hours"] == 500.0


def test_a_repo_that_has_never_indexed_is_counted_separately(conn):
    """It has no age at all, so it cannot show up in staleness -- it would be
    invisible without its own counter."""
    _repo(conn, 1, "g/done", indexed_at=NOW)
    _repo(conn, 2, "g/never")

    data = kpi.collect(conn, now=NOW)
    assert data["repos_never_indexed"] == 1
    assert data["repos_indexed"] == 1


def test_include_resolution_rates_are_shares_not_counts(conn):
    """Counts grow with the estate; shares are comparable across time."""
    rid = _repo(conn, 1, "g/a", indexed_at=NOW)
    fid = _file(conn, rid, "a.c", "s")
    for state in ["resolved"] * 6 + ["ambiguous"] * 2 + ["external"] * 2:
        conn.execute(
            "INSERT INTO includes (repo_id, file_id, raw, is_angle, resolution)"
            " VALUES (?, ?, 'x.h', 0, ?)", (rid, fid, state))
    conn.commit()

    data = kpi.collect(conn, now=NOW)
    assert data["resolved_include_rate"] == 0.6
    assert data["ambiguous_include_rate"] == 0.2


def test_every_lower_is_better_name_is_an_actual_indicator(conn):
    """A typo in LOWER_IS_BETTER would silently mislabel a metric's direction,
    or annotate nothing at all -- and nothing else would notice."""
    _repo(conn, 1, "g/a", indexed_at=NOW)
    conn.commit()
    produced = set(kpi.collect(conn, now=NOW))

    unknown = kpi.LOWER_IS_BETTER - produced
    assert not unknown, f"LOWER_IS_BETTER names nothing collect() emits: {unknown}"
