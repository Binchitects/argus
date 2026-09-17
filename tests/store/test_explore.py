"""The admin index explorer: unfiltered reads, and the fence around them.

WHY THIS EXISTS

`store/queries.py` has sixteen functions that take `allowed_repo_ids` as their
first positional argument. That is the access-control mechanism, not a
convention -- a tool cannot read a repository without passing the caller's
allowlist, because there is no way to call the function without one.

The explorer needs the opposite: an operator looking at the whole estate. So it
lives in its own module, and the fence is tested rather than assumed. If an
unfiltered query were reachable from a tool path, every ACL guarantee in this
project would be void and nothing else in the suite would notice -- the tool
would simply return more rows.
"""
from __future__ import annotations

import re
import sqlite3
from pathlib import Path

import pytest

from argus.store import explore
from argus.store.db import open_db
from argus.store import writes as store_writes

ROOT = Path(__file__).resolve().parent.parent.parent


@pytest.fixture
def db(tmp_path):
    path = tmp_path / "argus.db"
    conn = open_db(path)
    rid = store_writes.upsert_repo(conn, gitlab_id=1, path_with_namespace="g/alpha",
                                   default_branch="main", http_url="x")
    other = store_writes.upsert_repo(conn, gitlab_id=2, path_with_namespace="g/beta",
                                     default_branch="main", http_url="x")
    conn.execute("INSERT INTO files (repo_id, path, lang, size, blob_sha, content) VALUES (?,?,?,?,?,?)",
                 (rid, "src/decoder.c", "c", 120, "aa", "int DecodeFrame(void){return 0;}\n"))
    fid = conn.execute("SELECT id FROM files WHERE path = 'src/decoder.c'").fetchone()[0]
    for name, public in (("DecodeFrame", 1), ("HelperOnly", 0)):
        conn.execute(
            "INSERT INTO symbols (repo_id, file_id, name, kind, line, end_line,"
            " signature, scope, is_public) VALUES (?,?,?,?,?,?,?,?,?)",
            (rid, fid, name, "function", 3, 4, f"int {name}(void)", "global", public))
    # A file indexed with NO symbols: the case the file list exists to show.
    conn.execute("INSERT INTO files (repo_id, path, lang, size, blob_sha, content) VALUES (?,?,?,?,?,?)",
                 (other, "notes.unknown", "", 10, "bb", "nothing extractable\n"))
    conn.commit()
    conn.close()
    return path


def test_repos_lists_every_repository_with_its_counts(db):
    conn = sqlite3.connect(db)
    conn.row_factory = sqlite3.Row
    try:
        rows = explore.repos(conn)
    finally:
        conn.close()
    by_name = {r["path_with_namespace"]: r for r in rows}
    assert set(by_name) == {"g/alpha", "g/beta"}
    assert by_name["g/alpha"]["files"] == 1
    assert by_name["g/alpha"]["symbols"] == 2
    assert by_name["g/alpha"]["public_symbols"] == 1


def test_symbols_match_a_fragment_not_an_exact_name(db):
    conn = sqlite3.connect(db)
    conn.row_factory = sqlite3.Row
    try:
        # `find_symbol` needs the exact name; the operator is guessing.
        out = explore.symbols(conn, pattern="Frame")
    finally:
        conn.close()
    assert [r["name"] for r in out["rows"]] == ["DecodeFrame"]
    assert out["rows"][0]["path"] == "src/decoder.c"
    assert out["rows"][0]["is_public"] == 1
    assert out["capped"] is False


def test_symbols_reports_that_it_truncated(db):
    """A result set that silently stops at the limit reads as "that is all
    there is", which is how an operator concludes a symbol is absent."""
    conn = sqlite3.connect(db)
    conn.row_factory = sqlite3.Row
    try:
        out = explore.symbols(conn, limit=1)
    finally:
        conn.close()
    assert len(out["rows"]) == 1
    assert out["capped"] is True, "truncation was silent"


def test_symbols_can_be_restricted_to_one_repository(db):
    conn = sqlite3.connect(db)
    conn.row_factory = sqlite3.Row
    try:
        assert explore.symbols(conn, repo="g/alpha")["rows"]
        assert explore.symbols(conn, repo="g/beta")["rows"] == []
    finally:
        conn.close()


def test_a_like_metacharacter_is_searched_for_literally(db):
    """Without escaping, `%` matches everything and `_` matches any character.
    The operator then sees a result set that does not contain what they typed
    and concludes the index is wrong."""
    conn = sqlite3.connect(db)
    conn.row_factory = sqlite3.Row
    try:
        assert explore.symbols(conn, pattern="%")["rows"] == []
        assert explore.symbols(conn, pattern="_")["rows"] == []
        assert explore.symbols(conn, pattern="Decode_rame")["rows"] == []
    finally:
        conn.close()


def test_files_show_a_file_indexed_with_no_symbols(db):
    """The difference between "the agent cannot find it" and "it is not in the
    index" -- a file the extractor did not recognise has zero symbols, and that
    number is the whole reason to look."""
    conn = sqlite3.connect(db)
    conn.row_factory = sqlite3.Row
    try:
        out = explore.files(conn, pattern="notes")
    finally:
        conn.close()
    assert [r["path"] for r in out["rows"]] == ["notes.unknown"]
    assert out["rows"][0]["symbols"] == 0


def test_the_limit_is_clamped(db):
    conn = sqlite3.connect(db)
    conn.row_factory = sqlite3.Row
    try:
        assert explore.symbols(conn, limit=10_000)["limit"] == explore.MAX_LIMIT
        assert explore.symbols(conn, limit=0)["limit"] == explore.DEFAULT_LIMIT
        assert explore.symbols(conn, limit=-5)["limit"] == explore.DEFAULT_LIMIT
    finally:
        conn.close()


# --- the fence -----------------------------------------------------------------

def test_no_mcp_tool_module_imports_the_unfiltered_queries():
    """The mistake worth making impossible rather than merely unlikely.

    Every ACL guarantee in this project rests on the query layer requiring an
    allowlist. One import of `store.explore` from a tool path voids all of it,
    and nothing else in the suite would fail -- the tool would just return more
    rows than it should, which looks like a working tool.
    """
    offenders = []
    for module in sorted((ROOT / "src" / "argus" / "mcpsrv").glob("*.py")):
        text = module.read_text(encoding="utf-8")
        if re.search(r"store\s+import\s+.*\bexplore\b", text) or \
           re.search(r"from\s+\.\.store\.explore\s+import", text):
            # `server.py` is the admin surface, which is allowed to have it --
            # and it must be the ONLY place.
            if module.name != "server.py":
                offenders.append(module.name)
    assert not offenders, (
        f"{offenders} import the unfiltered index queries. They bypass every "
        f"per-caller allowlist; move the read into the admin surface or scope "
        f"it with allowed_repo_ids.")


def test_the_admin_surface_is_the_only_importer():
    """Stated separately so the rule above cannot be satisfied by nobody
    importing it at all, which would mean the explorer had been quietly
    disconnected from its only caller."""
    text = (ROOT / "src" / "argus" / "mcpsrv" / "server.py").read_text(encoding="utf-8")
    assert "from ..store import explore, writes" in text
    assert "ADMIN_PREFIX + \"explore\"" in text


# --- the route -----------------------------------------------------------------

def test_the_route_returns_rows_not_tuples(tmp_path, monkeypatch):
    """The bug this route shipped with, and the reason it has a test at all.

    The status route next to it clears `row_factory` because it reads rows
    positionally. Copying that here made every row a plain tuple, so
    `dict(row)` raised "cannot convert dictionary update sequence element #0 to
    a sequence" -- and the route answers 200 with the error in the body, so the
    console drew "Nothing is indexed yet" while the index held seventy symbols.
    An empty page and an empty index look the same; only a test can tell them
    apart.
    """
    import httpx
    from starlette.testclient import TestClient
    from argus.config import Config, GitLabConfig, IndexConfig
    from argus.mcpsrv.server import create_app

    db_path = tmp_path / "argus.db"
    conn = open_db(db_path)
    rid = store_writes.upsert_repo(conn, gitlab_id=1, path_with_namespace="g/alpha",
                                   default_branch="main", http_url="x")
    conn.execute("INSERT INTO files (repo_id, path, lang, size, blob_sha, content)"
                 " VALUES (?,?,?,?,?,?)",
                 (rid, "src/decoder.c", "c", 10, "aa", "int x;\n"))
    conn.commit()
    conn.close()

    monkeypatch.setenv("ARGUS_ADMIN_TOKEN", "admin-secret")
    cfg = Config(gitlab=GitLabConfig(url="https://gl.test", token="t"),
                 index=IndexConfig(data_dir=tmp_path / "d", db_path=db_path))
    app = create_app(cfg, client=httpx.Client(
        transport=httpx.MockTransport(lambda r: httpx.Response(404))))
    client = TestClient(app.streamable_http_app(), raise_server_exceptions=False)

    r = client.get("/admin/explore", headers={"x-argus-admin-token": "admin-secret"})
    assert r.status_code == 200
    body = r.json()
    assert "error" not in body, f"the explore route failed: {body.get('error')}"
    assert [x["path_with_namespace"] for x in body["repos"]] == ["g/alpha"]
    assert [x["path"] for x in body["files"]["rows"]] == ["src/decoder.c"]
    assert body["files"]["rows"][0]["symbols"] == 0


def test_the_route_needs_the_admin_token(tmp_path, monkeypatch):
    import httpx
    from starlette.testclient import TestClient
    from argus.config import Config, GitLabConfig, IndexConfig
    from argus.mcpsrv.server import create_app

    db_path = tmp_path / "argus.db"
    open_db(db_path).close()
    monkeypatch.setenv("ARGUS_ADMIN_TOKEN", "admin-secret")
    cfg = Config(gitlab=GitLabConfig(url="https://gl.test", token="t"),
                 index=IndexConfig(data_dir=tmp_path / "d", db_path=db_path))
    app = create_app(cfg, client=httpx.Client(
        transport=httpx.MockTransport(lambda r: httpx.Response(404))))
    client = TestClient(app.streamable_http_app(), raise_server_exceptions=False)

    assert client.get("/admin/explore").status_code == 403
    assert client.get("/admin/explore",
                      headers={"x-argus-admin-token": "wrong"}).status_code == 403
