import sqlite3

import pytest

from argus import resolve
from argus.store import graph
from argus.store.db import open_db


@pytest.fixture
def db(tmp_path):
    conn = open_db(tmp_path / "index.db")
    yield conn
    conn.close()


def _repo(conn, gitlab_id, name):
    return conn.execute(
        "INSERT INTO repos (gitlab_id, path_with_namespace, default_branch, "
        "http_url) VALUES (?, ?, 'main', 'u')", (gitlab_id, name)).lastrowid


def _file(conn, repo_id, path):
    return conn.execute(
        "INSERT INTO files (repo_id, path, size, blob_sha, content) "
        "VALUES (?, ?, 1, 'sha', '')", (repo_id, path)).lastrowid


def _resolved(conn, from_repo, from_file, to_repo, to_file):
    conn.execute(
        "INSERT INTO includes (repo_id, file_id, raw, is_angle, "
        "resolved_file_id, resolved_repo_id, is_external, resolution) "
        "VALUES (?, ?, 'x.h', 0, ?, ?, 0, ?)",
        (from_repo, from_file, to_file, to_repo, resolve.Resolution.RESOLVED))


def test_cross_repo_edges_are_materialised_with_weights(db):
    a, b = _repo(db, 1, "g/app"), _repo(db, 2, "g/eal")
    f1, f2 = _file(db, a, "src/one.c"), _file(db, a, "src/two.c")
    hdr = _file(db, b, "include/x.h")
    _resolved(db, a, f1, b, hdr)
    _resolved(db, a, f2, b, hdr)
    # A second include row from a file that already has one (f1 includes
    # hdr twice). This makes COUNT(*) (3 rows) diverge from
    # COUNT(DISTINCT file_id) (2 files), so the weight == 2 assertion below
    # can actually catch a regression to COUNT(*).
    _resolved(db, a, f1, b, hdr)
    db.commit()

    assert graph.rebuild_repo_deps(db) == 1
    row = db.execute("SELECT from_repo_id, to_repo_id, weight FROM repo_deps").fetchone()
    assert (row["from_repo_id"], row["to_repo_id"]) == (a, b)
    assert row["weight"] == 2, "weight counts distinct including files"


def test_same_repo_includes_are_not_edges(db):
    a = _repo(db, 1, "g/app")
    src, hdr = _file(db, a, "src/one.c"), _file(db, a, "src/x.h")
    _resolved(db, a, src, a, hdr)
    db.commit()

    assert graph.rebuild_repo_deps(db) == 0


def test_unresolved_and_ambiguous_includes_contribute_no_edge(db):
    a, b = _repo(db, 1, "g/app"), _repo(db, 2, "g/eal")
    src = _file(db, a, "src/one.c")
    db.execute("INSERT INTO includes (repo_id, file_id, raw, is_angle, resolution) "
               "VALUES (?, ?, 'util.h', 0, ?)",
               (a, src, resolve.Resolution.AMBIGUOUS))
    db.commit()

    assert graph.rebuild_repo_deps(db) == 0


def test_rebuild_replaces_rather_than_accumulates(db):
    a, b = _repo(db, 1, "g/app"), _repo(db, 2, "g/eal")
    src, hdr = _file(db, a, "src/one.c"), _file(db, b, "include/x.h")
    _resolved(db, a, src, b, hdr)
    db.commit()

    graph.rebuild_repo_deps(db)
    graph.rebuild_repo_deps(db)
    assert db.execute("SELECT count(*) FROM repo_deps").fetchone()[0] == 1


def test_a_failed_rebuild_leaves_the_previous_graph_intact(db, monkeypatch):
    """A half-updated graph is worse than a stale one: centrality would be
    computed from a mixture of two passes.

    The failure is engineered to happen *inside* the `with conn:` block,
    after the DELETE has run: `_edge_rows` is patched to return a row with
    the wrong arity (two values where the INSERT expects three), so
    `executemany` raises a `sqlite3.ProgrammingError` mid-transaction. This
    proves the DELETE gets rolled back rather than merely proving the
    function never got far enough to run it."""
    a, b = _repo(db, 1, "g/app"), _repo(db, 2, "g/eal")
    src, hdr = _file(db, a, "src/one.c"), _file(db, b, "include/x.h")
    _resolved(db, a, src, b, hdr)
    db.commit()
    graph.rebuild_repo_deps(db)

    monkeypatch.setattr(graph, "_edge_rows", lambda conn: [(1, 2)])
    with pytest.raises(sqlite3.ProgrammingError):
        graph.rebuild_repo_deps(db)

    assert db.execute("SELECT count(*) FROM repo_deps").fetchone()[0] == 1
