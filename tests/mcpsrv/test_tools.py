from __future__ import annotations

import asyncio
import json
import threading
import time

import httpx
import pytest
from starlette.testclient import TestClient

from argus import acl
from argus.config import Config, GitLabConfig, IndexConfig
from argus.mcpsrv import tools
from argus.mcpsrv.server import create_app
from argus.store import queries, writes
from argus.store.db import open_db

MCP_PATH = "/mcp"  # FastMCP's default streamable_http_path

JSONRPC_HEADERS = {
    "Accept": "application/json, text/event-stream",
    "Content-Type": "application/json",
}

# A definition + a call site, byte-identical across both repos in every
# fixture below -- the same collision strategy tests/store/test_queries.py's
# `two_repos` fixture uses for find_references, so that text content can
# never be the thing that distinguishes one repo's results from the
# other's; only the allowlist can.
DEF_C = (
    "int SharedFunc(void) {\n"
    "    return 1;\n"
    "}\n"
)
CALLER_C = (
    "void useIt(void) {\n"
    "    SharedFunc();\n"
    "}\n"
)


def _seed_two_repos(conn) -> dict[str, int]:
    """Two repos sharing an identical path (src/a.c) and an identical symbol
    name (SharedName), differing only by content and by definition/caller
    files that collide byte-for-byte too (see DEF_C/CALLER_C above).

    This is deliberately the same collision shape
    tests/store/test_queries.py's `two_repos` fixture uses: that suite
    proves queries.py's own filtering; this one proves a different thing --
    that argus.mcpsrv.tools actually threads identity.allowed_repo_ids
    through to those queries rather than defaulting or omitting it. A
    fixture with only one repo could let a tool that silently drops the
    allowlist pass by accident; two colliding repos close that gap.
    """
    ids: dict[str, int] = {}
    for gid, ns, word in ((101, "g/alpha", "alphaword"), (202, "g/beta", "betaword")):
        rid = writes.upsert_repo(conn, gitlab_id=gid, path_with_namespace=ns,
                                  default_branch="main", http_url=f"https://x/{ns}")
        fid = writes.upsert_file(conn, repo_id=rid, path="src/a.c", lang="c",
                                  size=len(word), blob_sha=f"sha{gid}", content=word)
        writes.replace_symbols(conn, rid, fid, [
            {"name": "SharedName", "kind": "function", "line": 1, "end_line": 2,
             "signature": "(void)", "scope": None, "is_public": 1},
        ], f"sha{gid}")

        def_fid = writes.upsert_file(conn, repo_id=rid, path="src/def.c", lang="c",
                                      size=len(DEF_C), blob_sha=f"def{gid}", content=DEF_C)
        writes.replace_symbols(conn, rid, def_fid, [
            {"name": "SharedFunc", "kind": "function", "line": 1, "end_line": 3,
             "signature": "(void)", "scope": None, "is_public": 1},
        ], f"def{gid}")
        writes.upsert_file(conn, repo_id=rid, path="src/caller.c", lang="c",
                            size=len(CALLER_C), blob_sha=f"caller{gid}", content=CALLER_C)
        ids[ns] = rid
    return ids


@pytest.fixture
def two_repos_db(tmp_path):
    """(db_path, ids) for the two colliding repos, writer connection closed.

    Closed (not held open) so every test below exercises the real
    production path: each tool call opens its own connect_readonly
    connection fresh, exactly as it does when create_app is used.
    """
    db_path = tmp_path / "argus.db"
    conn = open_db(db_path)
    ids = _seed_two_repos(conn)
    conn.close()
    return db_path, ids


def _identity(*repo_ids: int) -> acl.Identity:
    return acl.Identity(user_id=1, username="dev", allowed_repo_ids=list(repo_ids))


# ---------------------------------------------------------------------------
# Unit-level: the *_impl coroutines thread identity.allowed_repo_ids through
# to the matching queries function, for each of the five tools.
# ---------------------------------------------------------------------------

def test_find_symbol_impl_returns_only_callers_repo(two_repos_db):
    db_path, ids = two_repos_db
    rows = asyncio.run(tools.find_symbol_impl(db_path, _identity(ids["g/alpha"]), "SharedName"))
    assert [r["repo_id"] for r in rows] == [ids["g/alpha"]]


def test_find_references_impl_returns_only_callers_repo(two_repos_db):
    db_path, ids = two_repos_db
    rows = asyncio.run(tools.find_references_impl(db_path, _identity(ids["g/beta"]), "SharedFunc"))
    assert rows, "expected at least one occurrence of SharedFunc"
    assert {r["repo"] for r in rows} == {"g/beta"}


def test_search_code_impl_returns_only_callers_repo(two_repos_db):
    db_path, ids = two_repos_db
    excluded = asyncio.run(tools.search_code_impl(db_path, _identity(ids["g/alpha"]), "betaword"))
    assert excluded == []
    included = asyncio.run(tools.search_code_impl(db_path, _identity(ids["g/beta"]), "betaword"))
    assert len(included) == 1
    assert included[0]["repo_id"] == ids["g/beta"]


def test_get_file_impl_returns_only_callers_repo(two_repos_db):
    db_path, ids = two_repos_db
    identity = _identity(ids["g/alpha"])
    result = asyncio.run(tools.get_file_impl(db_path, identity, ids["g/alpha"], "src/a.c"))
    assert result["content"] == "alphaword"

    with pytest.raises(LookupError):
        asyncio.run(tools.get_file_impl(db_path, identity, ids["g/beta"], "src/a.c"))


def test_index_status_impl_returns_only_callers_repo(two_repos_db):
    db_path, ids = two_repos_db
    rows = asyncio.run(tools.index_status_impl(db_path, _identity(ids["g/beta"])))
    assert [r["path_with_namespace"] for r in rows] == ["g/beta"]


# ---------------------------------------------------------------------------
# search_code's QueryError must reach a caller as actionable text.
# ---------------------------------------------------------------------------

def test_search_code_impl_surfaces_query_error_actionably(two_repos_db):
    db_path, ids = two_repos_db
    with pytest.raises(queries.QueryError) as exc_info:
        asyncio.run(tools.search_code_impl(db_path, _identity(ids["g/alpha"]), 'unbalanced "quote'))
    message = str(exc_info.value)
    assert "traceback" not in message.lower()
    assert "operationalerror" not in message.lower()
    assert "syntax" in message.lower()


# ---------------------------------------------------------------------------
# get_file must report truncation, both ways.
# ---------------------------------------------------------------------------

def test_get_file_impl_reports_truncation(tmp_path):
    db_path = tmp_path / "argus.db"
    conn = open_db(db_path)
    rid = writes.upsert_repo(conn, gitlab_id=1, path_with_namespace="g/big",
                              default_branch="main", http_url="https://x")
    big = "x" * 70_000  # over queries.get_file's 65536-byte default
    small = "hello"
    writes.upsert_file(conn, repo_id=rid, path="big.txt", lang=None,
                        size=len(big), blob_sha="s1", content=big)
    writes.upsert_file(conn, repo_id=rid, path="small.txt", lang=None,
                        size=len(small), blob_sha="s2", content=small)
    conn.close()

    identity = _identity(rid)
    truncated = asyncio.run(tools.get_file_impl(db_path, identity, rid, "big.txt"))
    assert truncated["truncated"] is True
    assert len(truncated["content"]) == 65536

    whole = asyncio.run(tools.get_file_impl(db_path, identity, rid, "small.txt"))
    assert whole["truncated"] is False
    assert whole["content"] == "hello"


# ---------------------------------------------------------------------------
# index_status must distinguish a timed-out (partial) repo from a healthy one.
# ---------------------------------------------------------------------------

def test_index_status_impl_reports_timed_out_repo_as_partial(two_repos_db):
    db_path, ids = two_repos_db
    conn = open_db(db_path)  # migrate() is a no-op re-run; safe to reopen
    writes.record_run_state(conn, ids["g/alpha"], timed_out=True, symbols_failed=False, ts=1234)
    conn.close()

    rows = asyncio.run(tools.index_status_impl(db_path, _identity(ids["g/alpha"], ids["g/beta"])))
    by_ns = {r["path_with_namespace"]: r for r in rows}
    assert by_ns["g/alpha"]["last_run_timed_out"] == 1
    assert by_ns["g/beta"]["last_run_timed_out"] == 0


# ---------------------------------------------------------------------------
# Integration: every tool call must be refused before auth resolves at all.
# ---------------------------------------------------------------------------

def _mock_client(handler):
    return httpx.Client(transport=httpx.MockTransport(handler))


def _explode(request):
    raise AssertionError("must not call GitLab for a rejected tool call")


@pytest.fixture
def empty_cfg(tmp_path):
    db_path = tmp_path / "argus.db"
    open_db(db_path).close()
    return Config(
        gitlab=GitLabConfig(url="https://gl.test", token="service-token"),
        index=IndexConfig(data_dir=tmp_path / "data", db_path=db_path),
    )


@pytest.mark.parametrize("tool_name,args", [
    ("find_symbol", {"name": "DecodeFrame"}),
    ("find_references", {"name": "DecodeFrame"}),
    ("search_code", {"query": "DecodeFrame"}),
    ("get_file", {"repo_id": 1, "path": "a.txt"}),
    ("index_status", {}),
])
def test_tool_call_refuses_without_auth(empty_cfg, tool_name, args):
    app = create_app(empty_cfg, client=_mock_client(_explode))
    client = TestClient(app.streamable_http_app(), raise_server_exceptions=False)
    resp = client.post(MCP_PATH, json={
        "jsonrpc": "2.0", "id": 1, "method": "tools/call",
        "params": {"name": tool_name, "arguments": args},
    })
    assert resp.status_code == 401
    assert "authorization" in resp.json()["error"].lower()


# ---------------------------------------------------------------------------
# Full-stack round trip: real create_app, real streamable-HTTP JSON-RPC
# protocol, real Bearer auth. Proves register_tools actually wires
# ctx -> identity -> the *_impl functions above, not just that the impls
# work in isolation.
# ---------------------------------------------------------------------------

def _gitlab_ok(projects):
    def handler(request):
        if request.url.path.endswith("/user"):
            return httpx.Response(200, json={"id": 7, "username": "dev"})
        if request.url.path.endswith("/projects"):
            page = dict(request.url.params).get("page", "1")
            return httpx.Response(200, json=projects if page == "1" else [])
        return httpx.Response(404)
    return handler


@pytest.fixture
def two_repo_cfg(tmp_path):
    db_path = tmp_path / "argus.db"
    conn = open_db(db_path)
    ids = _seed_two_repos(conn)
    conn.close()
    cfg = Config(
        gitlab=GitLabConfig(url="https://gl.test", token="service-token"),
        index=IndexConfig(data_dir=tmp_path / "data", db_path=db_path),
    )
    return cfg, ids


def _parse_sse_json(text: str) -> dict:
    for line in text.splitlines():
        if line.startswith("data:"):
            return json.loads(line[len("data:"):].strip())
    raise AssertionError(f"no SSE data line in response: {text!r}")


def _mcp_session(client: TestClient, headers: dict) -> dict:
    """Do the initialize/initialized handshake; return headers carrying the session id."""
    resp = client.post(MCP_PATH, headers=headers, json={
        "jsonrpc": "2.0", "id": 1, "method": "initialize",
        "params": {
            "protocolVersion": "2025-06-18",
            "capabilities": {},
            "clientInfo": {"name": "argus-test", "version": "0"},
        },
    })
    assert resp.status_code == 200, resp.text
    call_headers = {**headers, "Mcp-Session-Id": resp.headers["mcp-session-id"]}
    notif = client.post(MCP_PATH, headers=call_headers, json={
        "jsonrpc": "2.0", "method": "notifications/initialized",
    })
    assert notif.status_code == 202, notif.text
    return call_headers


def _call_tool(client: TestClient, call_headers: dict, name: str, arguments: dict, req_id=2) -> dict:
    resp = client.post(MCP_PATH, headers=call_headers, json={
        "jsonrpc": "2.0", "id": req_id, "method": "tools/call",
        "params": {"name": name, "arguments": arguments},
    })
    assert resp.status_code == 200, resp.text
    return _parse_sse_json(resp.text)["result"]


def test_full_stack_find_symbol_returns_only_callers_repo(two_repo_cfg):
    cfg, ids = two_repo_cfg
    # GitLab grants membership on g/alpha (gitlab_id 101) only.
    app = create_app(cfg, client=_mock_client(_gitlab_ok([{"id": 101}])))
    with TestClient(app.streamable_http_app(), base_url="http://localhost:1234",
                     raise_server_exceptions=True) as client:
        headers = {**JSONRPC_HEADERS, "Authorization": "Bearer dev-token"}
        call_headers = _mcp_session(client, headers)
        result = _call_tool(client, call_headers, "find_symbol", {"name": "SharedName"})

    assert result["isError"] is False
    rows = result["structuredContent"]["result"]
    assert [r["repo_id"] for r in rows] == [ids["g/alpha"]]


def test_full_stack_search_code_query_error_is_actionable_not_a_traceback(two_repo_cfg):
    cfg, ids = two_repo_cfg
    app = create_app(cfg, client=_mock_client(_gitlab_ok([{"id": 101}])))
    with TestClient(app.streamable_http_app(), base_url="http://localhost:1234",
                     raise_server_exceptions=True) as client:
        headers = {**JSONRPC_HEADERS, "Authorization": "Bearer dev-token"}
        call_headers = _mcp_session(client, headers)
        result = _call_tool(client, call_headers, "search_code", {"query": 'unbalanced "quote'})

    assert result["isError"] is True
    text = result["content"][0]["text"]
    assert "traceback" not in text.lower()
    assert "operationalerror" not in text.lower()
    assert "syntax" in text.lower()


def test_full_stack_get_file_reports_truncation(tmp_path):
    db_path = tmp_path / "argus.db"
    conn = open_db(db_path)
    rid = writes.upsert_repo(conn, gitlab_id=101, path_with_namespace="g/big",
                              default_branch="main", http_url="https://x")
    writes.upsert_file(conn, repo_id=rid, path="big.txt", lang=None,
                        size=70_000, blob_sha="s1", content="x" * 70_000)
    conn.close()
    cfg = Config(
        gitlab=GitLabConfig(url="https://gl.test", token="service-token"),
        index=IndexConfig(data_dir=tmp_path / "data", db_path=db_path),
    )
    app = create_app(cfg, client=_mock_client(_gitlab_ok([{"id": 101}])))
    with TestClient(app.streamable_http_app(), base_url="http://localhost:1234",
                     raise_server_exceptions=True) as client:
        headers = {**JSONRPC_HEADERS, "Authorization": "Bearer dev-token"}
        call_headers = _mcp_session(client, headers)
        result = _call_tool(client, call_headers, "get_file", {"repo_id": rid, "path": "big.txt"})

    assert result["isError"] is False
    payload = result["structuredContent"]
    assert payload["truncated"] is True
    assert len(payload["content"]) == 65536


def test_tools_list_descriptions_are_load_bearing(two_repo_cfg):
    """The descriptions are what a weaker model uses to pick the right tool;
    each must state the property that most changes how its output should be
    read. Assert the specific phrases the brief calls out, not just "some
    non-empty description exists".
    """
    cfg, _ids = two_repo_cfg
    app = create_app(cfg, client=_mock_client(_gitlab_ok([{"id": 101}])))
    with TestClient(app.streamable_http_app(), base_url="http://localhost:1234",
                     raise_server_exceptions=True) as client:
        headers = {**JSONRPC_HEADERS, "Authorization": "Bearer dev-token"}
        call_headers = _mcp_session(client, headers)
        resp = client.post(MCP_PATH, headers=call_headers, json={
            "jsonrpc": "2.0", "id": 3, "method": "tools/list", "params": {},
        })
        assert resp.status_code == 200, resp.text
        tools_list = _parse_sse_json(resp.text)["result"]["tools"]

    by_name = {t["name"]: t["description"] for t in tools_list}
    assert set(by_name) == {
        "find_symbol", "find_references", "search_code", "get_file", "index_status",
    }
    assert "name-based" in by_name["find_references"].lower() or \
        "name" in by_name["find_references"].lower() and "not" in by_name["find_references"].lower()
    assert "macro" in by_name["find_references"].lower()
    assert "function pointer" in by_name["find_references"].lower()
    assert "last_run_timed_out" in by_name["index_status"]
    assert "last_run_symbols_failed" in by_name["index_status"]
    assert "truncated" in by_name["get_file"].lower()


# ---------------------------------------------------------------------------
# The event-loop hazard: a tool handler's sqlite work must run off the event
# loop, or one slow query call stalls every other in-flight request.
# ---------------------------------------------------------------------------

def test_slow_tool_query_does_not_block_other_requests(two_repo_cfg, monkeypatch):
    """Mirrors test_server.py's test_slow_acl_resolution_does_not_block_other_requests
    for the tool-call path: if a tool handler ran its sqlite query directly on
    the request coroutine instead of through run_in_threadpool, this
    artificially slow query would stall the whole single-threaded event loop,
    and the concurrent /healthz request below would queue up behind it
    instead of returning promptly.
    """
    cfg, ids = two_repo_cfg
    delay = 0.3
    real_find_symbol = queries.find_symbol

    def slow_find_symbol(*args, **kwargs):
        time.sleep(delay)
        return real_find_symbol(*args, **kwargs)

    monkeypatch.setattr(queries, "find_symbol", slow_find_symbol)

    app = create_app(cfg, client=_mock_client(_gitlab_ok([{"id": 101}])))
    with TestClient(app.streamable_http_app(), base_url="http://localhost:1234",
                     raise_server_exceptions=True) as client:
        headers = {**JSONRPC_HEADERS, "Authorization": "Bearer dev-token"}
        call_headers = _mcp_session(client, headers)

        healthz_elapsed = []

        def call_slow_tool():
            _call_tool(client, call_headers, "find_symbol", {"name": "SharedName"})

        def call_healthz():
            time.sleep(delay / 6)  # give the slow tool call a head start
            start = time.perf_counter()
            resp = client.get("/healthz")
            healthz_elapsed.append(time.perf_counter() - start)
            assert resp.status_code == 200

        t_tool = threading.Thread(target=call_slow_tool)
        t_healthz = threading.Thread(target=call_healthz)
        t_tool.start()
        t_healthz.start()
        t_tool.join()
        t_healthz.join()

    assert healthz_elapsed[0] < delay / 2
