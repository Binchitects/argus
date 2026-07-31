import inspect
import sqlite3

import pytest

from argus.store.db import open_db
from argus.store import writes, queries


def test_every_public_query_takes_allowlist_first():
    """The enforcement guarantee: a new query cannot forget the allowlist."""
    offenders = []
    for name, fn in inspect.getmembers(queries, inspect.isfunction):
        if name.startswith("_") or fn.__module__ != queries.__name__:
            continue
        params = list(inspect.signature(fn).parameters.values())
        if not params or params[0].name != "allowed_repo_ids":
            offenders.append(f"{name}: first param is not allowed_repo_ids")
        elif params[0].default is not inspect.Parameter.empty:
            offenders.append(f"{name}: allowed_repo_ids has a default")
    assert offenders == []


@pytest.fixture
def two_repos(tmp_path):
    conn = open_db(tmp_path / "i.db")
    ids = {}
    for gid, ns, word in ((1, "g/alpha", "alphaword"), (2, "g/beta", "betaword")):
        rid = writes.upsert_repo(conn, gitlab_id=gid, path_with_namespace=ns,
                                 default_branch="main", http_url=f"https://x/{ns}")
        fid = writes.upsert_file(conn, repo_id=rid, path="src/a.c", lang="c",
                                 size=len(word), blob_sha=f"sha{gid}", content=word)
        writes.replace_symbols(conn, rid, fid, [
            {"name": "SharedName", "kind": "function", "line": 1, "end_line": 2,
             "signature": "(void)", "scope": None, "is_public": 1},
        ], f"sha{gid}")
        writes.set_last_indexed(conn, rid, f"sha{gid}", 1000 + gid)
        ids[ns] = rid
    return conn, ids


def test_find_symbol_excludes_disallowed_repo(two_repos):
    conn, ids = two_repos
    rows = queries.find_symbol([ids["g/alpha"]], conn, "SharedName")
    assert len(rows) == 1
    assert rows[0]["repo_id"] == ids["g/alpha"]


def test_find_symbol_kind_filter(two_repos):
    conn, ids = two_repos
    rid = ids["g/alpha"]
    fid = conn.execute(
        "SELECT id FROM files WHERE repo_id = ?", (rid,)
    ).fetchone()["id"]
    writes.replace_symbols(conn, rid, fid, [
        {"name": "SharedName", "kind": "function", "line": 1, "end_line": 2,
         "signature": "(void)", "scope": None, "is_public": 1},
        {"name": "SharedName", "kind": "variable", "line": 10, "end_line": 10,
         "signature": None, "scope": None, "is_public": 1},
    ], "sha1")

    both = queries.find_symbol([rid], conn, "SharedName")
    assert {r["kind"] for r in both} == {"function", "variable"}

    only_variable = queries.find_symbol([rid], conn, "SharedName", kind="variable")
    assert [r["kind"] for r in only_variable] == ["variable"]


def test_search_code_excludes_disallowed_repo(two_repos):
    conn, ids = two_repos
    assert queries.search_code([ids["g/alpha"]], conn, "betaword") == []
    assert len(queries.search_code([ids["g/beta"]], conn, "betaword")) == 1


def test_get_file_refuses_disallowed_repo(two_repos):
    conn, ids = two_repos
    assert queries.get_file([ids["g/alpha"]], conn, ids["g/beta"], "src/a.c") is None
    assert queries.get_file([ids["g/beta"]], conn, ids["g/beta"], "src/a.c") is not None


def test_get_file_empty_allowlist_returns_none(two_repos):
    conn, ids = two_repos
    assert queries.get_file([], conn, ids["g/alpha"], "src/a.c") is None


def test_index_status_excludes_disallowed_repo(two_repos):
    conn, ids = two_repos
    rows = queries.index_status([ids["g/beta"]], conn)
    assert [r["path_with_namespace"] for r in rows] == ["g/beta"]


def test_empty_allowlist_returns_nothing_not_everything(two_repos):
    conn, _ = two_repos
    assert queries.find_symbol([], conn, "SharedName") == []
    assert queries.search_code([], conn, "alphaword") == []
    assert queries.index_status([], conn) == []


def test_allowlist_must_be_a_sequence_of_ints(two_repos):
    conn, ids = two_repos
    with pytest.raises(TypeError):
        queries.find_symbol("all", conn, "SharedName")
    with pytest.raises(TypeError):
        queries.find_symbol([None], conn, "SharedName")


def test_index_status_reports_last_run_flags(two_repos):
    conn, ids = two_repos
    rid = ids["g/alpha"]
    writes.record_run_state(conn, rid, timed_out=True, symbols_failed=False, ts=1234)
    row = [r for r in queries.index_status([rid], conn)][0]
    assert row["last_run_timed_out"] == 1
    assert row["last_run_symbols_failed"] == 0


def test_index_status_reports_queued_retries(two_repos):
    """queued_retries must count queued PATHS, not index_queue rows.

    index_queue.repo_id is a PRIMARY KEY -- one row per repo, with the paths
    JSON-packed into `reason` -- so COUNT(*) is structurally bounded at 1 and
    a repo with 4,000 stuck paths reported "1". Phase 2 exposes this column
    to a model that will read it as a file count, so assert exact numbers:
    `>= 1` would be satisfied by a literal SELECT 1.
    """
    conn, ids = two_repos
    rid = ids["g/beta"]

    def queued():
        return [r for r in queries.index_status([rid], conn)][0]["queued_retries"]

    assert queued() == 0, "no index_queue row at all must report 0, not NULL"

    writes.enqueue_retry(conn, rid, ["a.c"], "read error", 1234)
    assert queued() == 1

    writes.enqueue_retry(conn, rid, ["a.c", "b.c", "c.c", "d.c"], "read error", 1235)
    assert queued() == 4

    # A row whose reason is not the JSON payload (a hand-written entry, or one
    # written before the payload format existed) must degrade to 0 rather than
    # abort the whole status query with a malformed-JSON error.
    conn.execute("UPDATE index_queue SET reason = 'legacy free text' WHERE repo_id = ?",
                 (rid,))
    conn.commit()
    assert queued() == 0


def test_allowlist_larger_than_sqlite_parameter_limit(two_repos):
    """A developer in a large GitLab group must not raise OperationalError.

    Fail-closed semantics mean an exception here would be an availability bug
    wearing a security costume: the caller cannot distinguish "the store
    blew up" from "you are denied". The allowlist must simply work, and the
    one row that is genuinely allowed must be the one that comes back.

    SQLite's *documented default* SQLITE_MAX_VARIABLE_NUMBER is 999, but
    builds since 3.32.0 (Dec 2019) default to 32766 -- this repo's own
    sqlite3 measures 32766, not 999. A fixed literal like 1500 would pass
    against both the broken and the fixed code on such a build, proving
    nothing. Query the real compiled-in limit via Connection.getlimit and
    exceed *that*, so this test cannot pass by accident.
    """
    conn, ids = two_repos
    host_limit = conn.getlimit(sqlite3.SQLITE_LIMIT_VARIABLE_NUMBER)
    big = list(range(10_000_000, 10_000_000 + host_limit + 500)) + [ids["g/alpha"]]
    rows = queries.find_symbol(big, conn, "SharedName")
    assert len(rows) == 1 and rows[0]["repo_id"] == ids["g/alpha"]


def test_malformed_fts_query_returns_actionable_error(two_repos):
    conn, ids = two_repos
    with pytest.raises(queries.QueryError, match="search syntax"):
        queries.search_code([ids["g/alpha"]], conn, 'unbalanced "quote')


def test_get_file_truncates_and_says_so(two_repos):
    conn, ids = two_repos
    row = queries.get_file([ids["g/alpha"]], conn, ids["g/alpha"], "src/a.c", max_bytes=4)
    assert len(row["content"]) <= 64
    assert row["truncated"] is True


def test_get_file_not_truncated_reports_false(two_repos):
    """The truncated flag must be a real signal, not always True."""
    conn, ids = two_repos
    row = queries.get_file([ids["g/alpha"]], conn, ids["g/alpha"], "src/a.c")
    assert row["truncated"] is False
    assert row["content"] == "alphaword"


def test_get_file_refusal_paths_return_exactly_none(two_repos):
    """A denial must never be confused with a hit.

    get_file used to return sqlite3.Row | None; callers check truthiness.
    If a refused lookup returned an empty dict or an error-carrying dict
    instead of None, every one of those truthiness checks would silently
    invert and a denial would read as a successful fetch. Both refusal
    paths -- disallowed repo and empty allowlist -- must return the
    identical `None` object, not merely something falsy.
    """
    conn, ids = two_repos
    disallowed = queries.get_file([ids["g/alpha"]], conn, ids["g/beta"], "src/a.c")
    empty = queries.get_file([], conn, ids["g/alpha"], "src/a.c")
    assert disallowed is None
    assert empty is None
    assert not isinstance(disallowed, dict)
    assert not isinstance(empty, dict)


def test_index_status_queued_retries_is_per_repo(two_repos):
    """One repo's queue must not be counted against another's."""
    conn, ids = two_repos
    writes.enqueue_retry(conn, ids["g/alpha"], ["a.c", "b.c", "c.c"], "read error", 1)
    writes.enqueue_retry(conn, ids["g/beta"], ["z.c"], "read error", 1)
    rows = {r["path_with_namespace"]: r["queued_retries"]
            for r in queries.index_status(list(ids.values()), conn)}
    assert rows == {"g/alpha": 3, "g/beta": 1}
