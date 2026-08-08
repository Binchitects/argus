"""Branch selection through the MCP tool layer."""
import pytest

from argus import acl
from argus.mcpsrv import tools
from argus.store.db import open_db
from argus.store import queries, writes


@pytest.fixture
def index(tmp_path):
    path = tmp_path / "i.db"
    conn = open_db(path)
    ids = {}
    for branch, body in (("main", "int NewApi(void);\n"),
                         ("v1", "int OldApi(void);\n")):
        rid = writes.upsert_repo(conn, gitlab_id=1, path_with_namespace="g/app",
                                 default_branch="main", branch=branch, http_url="u")
        ids[branch] = rid
        fid = writes.upsert_file(conn, repo_id=rid, path="src/a.c", lang="c",
                                 size=len(body), blob_sha=f"{branch}-sha", content=body)
        name = "NewApi" if branch == "main" else "OldApi"
        conn.execute("INSERT INTO symbols (repo_id, file_id, name, kind, line) "
                     "VALUES (?, ?, ?, 'function', 1)", (rid, fid, name))
    conn.commit()
    conn.close()
    return path, ids


@pytest.mark.anyio
async def test_an_unqualified_question_is_answered_from_the_default_branch(index):
    path, ids = index
    identity = acl.Identity(1, "u", list(ids.values()))
    assert await tools.find_symbol_impl(path, identity, "NewApi")
    assert await tools.find_symbol_impl(path, identity, "OldApi") == [], \
        "a release branch answered a question that named no branch"


@pytest.mark.anyio
async def test_naming_a_branch_answers_from_it(index):
    path, ids = index
    identity = acl.Identity(1, "u", list(ids.values()))
    rows = await tools.find_symbol_impl(path, identity, "OldApi", branch="v1")
    assert rows and rows[0]["repo_id"] == ids["v1"]


@pytest.mark.anyio
async def test_an_unknown_branch_is_an_error_not_an_empty_answer(index):
    """The failure this feature exists to prevent. A developer who asks about
    a branch that is not indexed and receives [] reads it as 'no such symbol'
    and concludes the code does not exist -- when in fact nobody looked."""
    path, ids = index
    identity = acl.Identity(1, "u", list(ids.values()))
    with pytest.raises(tools.UnknownBranch) as exc:
        await tools.find_symbol_impl(path, identity, "NewApi", branch="v9")
    assert "v9" in str(exc.value)
    assert "main" in str(exc.value), "the error must name the branches that exist"


@pytest.mark.anyio
async def test_a_branch_cannot_be_used_to_reach_an_unpermitted_repo(index, tmp_path):
    """Branch selection narrows an allowlist access control already produced.
    It must never widen one -- naming a branch of a repo the caller cannot
    read has to stay empty, not become a way in."""
    path, ids = index
    conn = open_db(path)
    secret = writes.upsert_repo(conn, gitlab_id=2, path_with_namespace="g/secret",
                                default_branch="main", branch="main", http_url="u")
    fid = writes.upsert_file(conn, repo_id=secret, path="s.c", lang="c", size=9,
                             blob_sha="s", content="int Secret(void);\n")
    conn.execute("INSERT INTO symbols (repo_id, file_id, name, kind, line) "
                 "VALUES (?, ?, 'Secret', 'function', 1)", (secret, fid))
    conn.commit(); conn.close()

    identity = acl.Identity(1, "u", list(ids.values()))       # no `secret`
    for branch in (None, "main", "v1"):
        rows = await tools.find_symbol_impl(path, identity, "Secret", branch=branch)
        assert rows == [], f"branch={branch!r} reached an unpermitted repo"


def test_a_reference_is_stamped_with_its_own_branch_repo_id(tmp_path):
    """The chain that breaks silently: find_references(branch="v2") returns
    rows correctly scoped to v2, but repo_id was looked up in a dict keyed on
    path_with_namespace built from the WHOLE allowlist. A project indexed at
    two refs owns two rows with that same name, so the dict kept whichever
    came last -- and get_file(repo_id, path) then read main's copy of the
    file while claiming to answer about v2.
    """
    import asyncio

    db = tmp_path / "index.db"
    conn = open_db(db)
    try:
        main_id = writes.upsert_repo(
            conn, gitlab_id=1, path_with_namespace="g/app",
            default_branch="main", http_url="u", branch="main")
        v2_id = writes.upsert_repo(
            conn, gitlab_id=1, path_with_namespace="g/app",
            default_branch="main", http_url="u", branch="v2")
        for rid, body in ((main_id, "int helper(void);\n"),
                          (v2_id, "int helper(void);\n")):
            writes.upsert_file(conn, repo_id=rid, path="src/a.c", lang="c",
                               size=len(body), blob_sha=f"sha-{rid}", content=body)
        conn.commit()
    finally:
        conn.close()

    identity = acl.Identity(user_id=1, username="dev",
                            allowed_repo_ids=[main_id, v2_id])
    # Ask for TRUNK on purpose. The buggy dict keeps whichever row the query
    # returned last, which is the higher rowid -- v2. Asking for v2 therefore
    # passes even with the bug present, and an earlier version of this test
    # did exactly that and proved nothing. Asking for main makes the stale
    # entry the wrong answer.
    rows = asyncio.run(tools.find_references_impl(db, identity, "helper",
                                                  branch="main"))
    assert rows, "no references found -- the test proves nothing"
    assert {r["repo_id"] for r in rows} == {main_id}, (
        "a trunk reference was stamped with the v2 branch's repo_id")


def test_index_status_says_which_branch_each_row_is(tmp_path):
    """One row per ref, all sharing a path_with_namespace. Without the ref an
    operator sees the same repo listed three times and cannot tell which is
    trunk."""
    db = tmp_path / "index.db"
    conn = open_db(db)
    try:
        main_id = writes.upsert_repo(
            conn, gitlab_id=1, path_with_namespace="g/app",
            default_branch="main", http_url="u", branch="main")
        v2_id = writes.upsert_repo(
            conn, gitlab_id=1, path_with_namespace="g/app",
            default_branch="main", http_url="u", branch="v2")
        conn.commit()
        rows = {r["repo_id"]: r for r in
                queries.index_status([main_id, v2_id], conn)}
        assert rows[main_id]["branch"] == "main"
        assert rows[v2_id]["branch"] == "v2"
        assert rows[v2_id]["default_branch"] == "main", (
            "nothing distinguishes the release row from trunk")
    finally:
        conn.close()
