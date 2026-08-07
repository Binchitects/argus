"""Branch selection at query time."""
from argus.store.db import open_db
from argus.store import queries, writes


def _repos(conn):
    ids = {}
    for gid, name in ((1, "g/alpha"), (2, "g/beta")):
        for branch in ("main", "v2"):
            ids[(name, branch)] = writes.upsert_repo(
                conn, gitlab_id=gid, path_with_namespace=name,
                default_branch="main", branch=branch, http_url="u")
    return ids


def test_no_branch_named_means_the_default_branch(tmp_path):
    conn = open_db(tmp_path / "i.db")
    try:
        ids = _repos(conn)
        allowed = list(ids.values())
        got = set(queries.scope_to_branch(allowed, conn))
        assert got == {ids[("g/alpha", "main")], ids[("g/beta", "main")]}
    finally:
        conn.close()


def test_naming_a_branch_selects_it_across_projects(tmp_path):
    conn = open_db(tmp_path / "i.db")
    try:
        ids = _repos(conn)
        got = set(queries.scope_to_branch(list(ids.values()), conn, "v2"))
        assert got == {ids[("g/alpha", "v2")], ids[("g/beta", "v2")]}
    finally:
        conn.close()


def test_scoping_can_never_widen_the_allowlist(tmp_path):
    """The security property. Branch selection narrows what access control
    already permitted; a repo the caller may not read must stay unreachable
    no matter which branch is requested."""
    conn = open_db(tmp_path / "i.db")
    try:
        ids = _repos(conn)
        permitted = [ids[("g/alpha", "main")], ids[("g/alpha", "v2")]]
        for branch in (None, "main", "v2"):
            got = queries.scope_to_branch(permitted, conn, branch)
            assert set(got) <= set(permitted), (branch, got)
            assert ids[("g/beta", "main")] not in got
            assert ids[("g/beta", "v2")] not in got
    finally:
        conn.close()


def test_an_unknown_branch_answers_with_nothing_not_with_trunk(tmp_path):
    """Silently answering from main when someone asked about v9 is the exact
    failure this feature exists to prevent -- a confident answer about the
    wrong code, with nothing in it saying so."""
    conn = open_db(tmp_path / "i.db")
    try:
        ids = _repos(conn)
        assert queries.scope_to_branch(list(ids.values()), conn, "v9") == []
    finally:
        conn.close()


def test_an_empty_allowlist_stays_empty(tmp_path):
    conn = open_db(tmp_path / "i.db")
    try:
        _repos(conn)
        assert queries.scope_to_branch([], conn) == []
        assert queries.scope_to_branch([], conn, "main") == []
    finally:
        conn.close()


def test_available_branches_are_reported_default_first(tmp_path):
    conn = open_db(tmp_path / "i.db")
    try:
        ids = _repos(conn)
        assert queries._branches_available(list(ids.values()), conn) == ["main", "v2"]
    finally:
        conn.close()
