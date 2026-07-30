import pytest
from argus.store.db import open_db
from argus.store import writes


@pytest.fixture
def conn(tmp_path):
    return open_db(tmp_path / "i.db")


@pytest.fixture
def repo_id(conn):
    return writes.upsert_repo(
        conn, gitlab_id=1, path_with_namespace="g/a",
        default_branch="main", http_url="https://x/g/a",
    )


def test_upsert_repo_is_idempotent(conn):
    a = writes.upsert_repo(conn, gitlab_id=7, path_with_namespace="g/a",
                           default_branch="main", http_url="https://x/g/a")
    b = writes.upsert_repo(conn, gitlab_id=7, path_with_namespace="g/a-renamed",
                           default_branch="trunk", http_url="https://x/g/a")
    assert a == b
    row = conn.execute("SELECT * FROM repos WHERE id = ?", (a,)).fetchone()
    assert row["path_with_namespace"] == "g/a-renamed"
    assert row["default_branch"] == "trunk"


def test_upsert_file_replaces_content(conn, repo_id):
    f1 = writes.upsert_file(conn, repo_id=repo_id, path="a.c", lang="c",
                            size=3, blob_sha="aaa", content="old")
    f2 = writes.upsert_file(conn, repo_id=repo_id, path="a.c", lang="c",
                            size=3, blob_sha="bbb", content="new")
    assert f1 == f2
    assert conn.execute("SELECT content FROM files").fetchone()["content"] == "new"


def test_fts_reflects_updates_and_deletes(conn, repo_id):
    writes.upsert_file(conn, repo_id=repo_id, path="a.c", lang="c",
                       size=9, blob_sha="aaa", content="alphaword")
    assert _fts_count(conn, "alphaword") == 1

    writes.upsert_file(conn, repo_id=repo_id, path="a.c", lang="c",
                       size=8, blob_sha="bbb", content="betaword")
    assert _fts_count(conn, "alphaword") == 0
    assert _fts_count(conn, "betaword") == 1

    writes.delete_file(conn, repo_id, "a.c")
    assert _fts_count(conn, "betaword") == 0


def _fts_count(conn, term):
    return conn.execute(
        "SELECT COUNT(*) c FROM files_fts WHERE files_fts MATCH ?", (term,)
    ).fetchone()["c"]


def test_replace_symbols_clears_previous(conn, repo_id):
    fid = writes.upsert_file(conn, repo_id=repo_id, path="a.c", lang="c",
                             size=1, blob_sha="aaa", content="x")
    writes.replace_symbols(conn, repo_id, fid, [
        {"name": "Old", "kind": "function", "line": 1, "end_line": 3,
         "signature": "(void)", "scope": None, "is_public": 1},
    ], "aaa")
    writes.replace_symbols(conn, repo_id, fid, [
        {"name": "New", "kind": "function", "line": 5, "end_line": 9,
         "signature": "(int)", "scope": None, "is_public": 0},
    ], "aaa")
    names = [r["name"] for r in conn.execute("SELECT name FROM symbols")]
    assert names == ["New"]


def test_delete_file_cascades_symbols(conn, repo_id):
    fid = writes.upsert_file(conn, repo_id=repo_id, path="a.c", lang="c",
                             size=1, blob_sha="aaa", content="x")
    writes.replace_symbols(conn, repo_id, fid, [
        {"name": "F", "kind": "function", "line": 1, "end_line": 2,
         "signature": None, "scope": None, "is_public": 1},
    ], "aaa")
    writes.delete_file(conn, repo_id, "a.c")
    assert conn.execute("SELECT COUNT(*) c FROM symbols").fetchone()["c"] == 0


def test_replace_symbols_records_the_blob_sha(conn, repo_id):
    fid = writes.upsert_file(conn, repo_id=repo_id, path="a.c", lang="c",
                             size=1, blob_sha="aaa", content="x")
    writes.replace_symbols(conn, repo_id, fid, [
        {"name": "F", "kind": "function", "line": 1, "end_line": 2,
         "signature": None, "scope": None, "is_public": 1},
    ], "aaa")
    assert conn.execute("SELECT symbols_sha FROM files WHERE id = ?",
                        (fid,)).fetchone()["symbols_sha"] == "aaa"


def test_replace_symbols_records_the_sha_even_when_empty(conn, repo_id):
    """Zero symbols is a valid successful result, not an incomplete one."""
    fid = writes.upsert_file(conn, repo_id=repo_id, path="b.c", lang="c",
                             size=1, blob_sha="bbb", content="x")
    writes.replace_symbols(conn, repo_id, fid, [], "bbb")
    assert conn.execute("SELECT symbols_sha FROM files WHERE id = ?",
                        (fid,)).fetchone()["symbols_sha"] == "bbb"


def test_clear_symbols_for_paths_nulls_symbols_sha(conn, repo_id):
    """A stale symbols_sha must not survive clear_symbols_for_paths.

    If content is later edited back to be byte-identical to the blob whose
    symbols were just cleared, the recomputed blob_sha would equal that old
    symbols_sha again. Simulate the coincidence directly: blob_sha is never
    changed here, so if clear_symbols_for_paths left symbols_sha holding its
    old value, it would still equal blob_sha and worker._already_current
    would wrongly report the file complete despite having zero symbol rows.
    """
    fid = writes.upsert_file(conn, repo_id=repo_id, path="a.c", lang="c",
                             size=1, blob_sha="aaa", content="x")
    writes.replace_symbols(conn, repo_id, fid, [
        {"name": "F", "kind": "function", "line": 1, "end_line": 2,
         "signature": None, "scope": None, "is_public": 1},
    ], "aaa")

    writes.clear_symbols_for_paths(conn, repo_id, ["a.c"])

    row = conn.execute("SELECT symbols_sha FROM files WHERE id = ?", (fid,)).fetchone()
    assert row["symbols_sha"] is None

    from argus import worker
    assert worker._already_current(conn, repo_id, "a.c", "aaa") is False


def test_replace_includes(conn, repo_id):
    fid = writes.upsert_file(conn, repo_id=repo_id, path="a.c", lang="c",
                             size=1, blob_sha="aaa", content="x")
    writes.replace_includes(conn, repo_id, fid, [
        {"raw": "eal/decoder.h", "is_angle": 0},
        {"raw": "stdio.h", "is_angle": 1},
    ])
    rows = conn.execute("SELECT raw, is_angle FROM includes ORDER BY raw").fetchall()
    assert [(r["raw"], r["is_angle"]) for r in rows] == [
        ("eal/decoder.h", 0), ("stdio.h", 1),
    ]
