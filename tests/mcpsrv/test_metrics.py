"""The index exposition.

Two consumers read this: the alert rule that pages someone when the index stops
updating, and the admin panel's Overview. They must not disagree, which is why
both go through `snapshot`.
"""
from __future__ import annotations

import sqlite3
import time

import pytest

from argus.mcpsrv import metrics
from argus.store import writes
from argus.store.db import open_db


@pytest.fixture
def index_db(tmp_path):
    """Two repositories: one fresh, one that has not run in a week."""
    path = tmp_path / "index.db"
    conn = open_db(path)
    # Real time, because render() uses the real clock -- timestamps from 2023
    # made every repo look years stale and proved nothing about the threshold.
    now = int(time.time())
    fresh = writes.upsert_repo(conn, gitlab_id=1, path_with_namespace="g/fresh",
                               default_branch="main", branch="main",
                               http_url="http://x/g/fresh.git")
    stale = writes.upsert_repo(conn, gitlab_id=2, path_with_namespace="g/stale",
                               default_branch="main", branch="main",
                               http_url="http://x/g/stale.git")
    writes.upsert_file(conn, repo_id=fresh, path="a.c", lang="c", size=3,
                       blob_sha="s1", content="int a;")
    writes.upsert_file(conn, repo_id=fresh, path="b.c", lang="c", size=3,
                       blob_sha="s2", content="int b;")
    writes.replace_symbols(conn, fresh, 1, [
        {"name": "Alpha", "kind": "function", "line": 1, "end_line": 2,
         "signature": "(void)", "scope": None, "is_public": 1}], "s1")
    writes.set_last_indexed(conn, fresh, "sha-fresh", now)
    writes.record_run_state(conn, fresh, timed_out=False, symbols_failed=False, ts=now)
    writes.set_last_indexed(conn, stale, "sha-stale", now - 604800)
    writes.record_run_state(conn, stale, timed_out=False, symbols_failed=False,
                            ts=now - 604800)
    conn.commit()
    conn.close()
    return path, now


def test_a_repo_that_has_not_run_recently_is_stale(index_db, monkeypatch):
    path, now = index_db
    monkeypatch.setenv("ARGUS_INDEX_STALE_AFTER", "3600")
    snap = metrics.snapshot(path, now=now)
    by_name = {r["repo"]: r for r in snap["repos"]}
    assert by_name["g/fresh"]["stale"] is False
    assert by_name["g/stale"]["stale"] is True
    assert snap["stale_repos"] == 1


def test_a_repo_that_has_never_run_counts_as_stale(index_db, monkeypatch):
    """Nothing to answer from is worse than out of date, and a rule written as
    `time() - last_run > limit` sees a missing sample as no alert at all."""
    path, now = index_db
    conn = sqlite3.connect(path)
    conn.execute("UPDATE repos SET last_run_at = NULL WHERE path_with_namespace = 'g/stale'")
    conn.commit()
    conn.close()
    monkeypatch.setenv("ARGUS_INDEX_STALE_AFTER", "3600")
    snap = metrics.snapshot(path, now=now)
    stale = next(r for r in snap["repos"] if r["repo"] == "g/stale")
    assert stale["stale"] is True
    # Exported as 0 rather than omitted, so a PromQL rule can still see it.
    assert stale["last_run_at"] is None
    assert "argus_index_last_run_timestamp_seconds{repo=\"g/stale\",branch=\"main\"} 0" \
        in metrics.render(path)


def test_the_threshold_is_read_per_call(index_db, monkeypatch):
    """A module-level read cannot be changed by a test -- and the first version
    of the seeder hung a suite for thirty minutes proving that."""
    path, now = index_db
    monkeypatch.setenv("ARGUS_INDEX_STALE_AFTER", "999999999")
    assert metrics.snapshot(path, now=now)["stale_repos"] == 0
    monkeypatch.setenv("ARGUS_INDEX_STALE_AFTER", "1")
    # Only the one a week behind; the fresh repo ran at `now`, so its age is 0.
    assert metrics.snapshot(path, now=now)["stale_repos"] == 1


def test_the_exposition_is_parseable_and_named_for_argus(index_db, monkeypatch):
    path, _ = index_db
    monkeypatch.setenv("ARGUS_INDEX_STALE_AFTER", "3600")
    text = metrics.render(path)
    assert text.endswith("\n")
    for line in text.splitlines():
        if line.startswith("#") or not line.strip():
            continue
        name = line.split("{")[0].split(" ")[0]
        assert name.startswith("argus_"), f"a metric escaped the prefix: {name}"
        assert line.rsplit(" ", 1)[-1] not in ("", "None"), f"no value: {line}"
    for wanted in ("argus_index_repos 2",
                   "argus_index_stale_repos 1",
                   "argus_index_scrape_ok 1",
                   "argus_index_build_info{version=",
                   'argus_index_stale{repo="g/stale",branch="main"} 1',
                   'argus_index_stale{repo="g/fresh",branch="main"} 0',
                   'argus_index_files{repo="g/fresh",branch="main"} 2'):
        assert wanted in text, f"missing: {wanted}"


def test_label_values_are_escaped(index_db):
    """A repository name is operator-controlled and can contain a quote; an
    unescaped one produces an exposition Prometheus rejects wholesale, so a
    single badly named repo would blind the whole alert."""
    assert metrics._escape('a"b\\c\nd') == 'a\\"b\\\\c\\nd'


def test_an_unreadable_index_does_not_look_like_a_dead_target(tmp_path):
    """The route catches this and answers 200 with a comment, because
    Prometheus reads a failed scrape as `up == 0` -- a different incident from
    "the index cannot be read"."""
    with pytest.raises(sqlite3.OperationalError):
        metrics.snapshot(tmp_path / "does-not-exist.db")


def test_the_failure_exposition_is_still_a_valid_scrape(tmp_path):
    """What the route serves when the index cannot be read. It has to be a
    complete exposition with the signal in it, because `up` stays 1 and the
    ArgusIndexUnreadable rule has nothing else to fire on."""
    try:
        metrics.snapshot(tmp_path / "does-not-exist.db")
    except sqlite3.OperationalError as exc:
        text = metrics.render_error(exc)
    else:                                   # pragma: no cover - would be a bug
        raise AssertionError("the fixture was supposed to fail")

    assert text.endswith("\n")
    assert "argus_index_scrape_ok 0" in text
    assert "# argus could not read the index: OperationalError" in text
    # Not even one of the per-repo series sneaks in claiming a healthy value.
    assert "argus_index_repos " not in text
    assert "argus_index_stale_repos " not in text
    # Every non-comment line is still a metric line Prometheus can parse.
    for line in text.splitlines():
        if line.startswith("#") or not line.strip():
            continue
        assert line.rsplit(" ", 1)[-1] not in ("", "None"), f"no value: {line}"
