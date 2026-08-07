"""Branch selection through the MCP tool layer."""
import pytest

from argus import acl
from argus.mcpsrv import tools
from argus.store.db import open_db
from argus.store import writes


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
