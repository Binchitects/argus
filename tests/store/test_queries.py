import inspect
import pytest

from codeindex.store.db import open_db
from codeindex.store import writes, queries


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
        ])
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
    ])

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
