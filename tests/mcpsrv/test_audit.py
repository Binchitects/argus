from __future__ import annotations

import json
import sqlite3

import httpx
import pytest
from starlette.testclient import TestClient

import argus.mcpsrv.server as server_mod
from argus.config import Config, GitLabConfig, IndexConfig
from argus.mcpsrv.server import create_app
from argus.store import writes
from argus.store.db import connect_readonly, open_db

MCP_PATH = "/mcp"  # FastMCP's default streamable_http_path

JSONRPC_HEADERS = {
    "Accept": "application/json, text/event-stream",
    "Content-Type": "application/json",
}

# A distinctive, obviously-secret-shaped string -- used as the Bearer token
# in the no-credential test so that finding it ANYWHERE in a serialised audit
# row is unambiguous proof of a leak, never a coincidental substring match.
SECRET_TOKEN = "s3cr3t-dev-token-do-not-log-me-98216"


def _mock_client(handler):
    return httpx.Client(transport=httpx.MockTransport(handler))


def _gitlab_ok(projects):
    def handler(request):
        if request.url.path.endswith("/user"):
            return httpx.Response(200, json={"id": 7, "username": "dev"})
        if request.url.path.endswith("/projects"):
            page = dict(request.url.params).get("page", "1")
            return httpx.Response(200, json=projects if page == "1" else [])
        return httpx.Response(404)

    return handler


def _gitlab_revokes(request):
    return httpx.Response(401, json={"message": "401 Unauthorized"})


def _parse_sse_json(text: str) -> dict:
    for line in text.splitlines():
        if line.startswith("data:"):
            return json.loads(line[len("data:"):].strip())
    raise AssertionError(f"no SSE data line in response: {text!r}")


def _mcp_session(client: TestClient, headers: dict) -> dict:
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


def _audit_rows(db_path) -> list[dict]:
    """Read the audit SIDECAR, not the index.

    The rows moved out of the index deliberately: an audit row is the only
    write a query makes, and in WAL a writer waits on the indexer's write
    lock even though the read did not. Measured, that coupling cost
    76.1 -> 7.2 req/s at 4 concurrent readers while indexing.

    Pointed at the sidecar rather than accepting either location, so a
    regression that quietly writes back into the index fails here instead of
    passing against the vestigial `audit` table migration 007 still creates.
    """
    from argus.store.db import audit_db_path

    sidecar = audit_db_path(db_path)
    if not sidecar.exists():
        return []
    conn = connect_readonly(sidecar)
    try:
        return [dict(r) for r in conn.execute("SELECT * FROM audit ORDER BY id").fetchall()]
    finally:
        conn.close()


@pytest.fixture
def repo_cfg(tmp_path):
    db_path = tmp_path / "argus.db"
    conn = open_db(db_path)
    rid = writes.upsert_repo(conn, gitlab_id=101, path_with_namespace="g/a",
                              default_branch="main", http_url="https://x/g/a")
    writes.upsert_file(conn, repo_id=rid, path="a.c", lang="c", size=3,
                        blob_sha="s1", content="int")
    conn.close()
    cfg = Config(
        gitlab=GitLabConfig(url="https://gl.test", token="service-token"),
        index=IndexConfig(data_dir=tmp_path / "data", db_path=db_path),
    )
    return cfg, rid


# ---------------------------------------------------------------------------
# writes.record_audit itself: the raw insert, on a plain read-write connection.
# ---------------------------------------------------------------------------

def test_record_audit_writes_one_row(tmp_path):
    conn = open_db(tmp_path / "i.db")
    writes.record_audit(
        conn, ts=1000, user_id=7, username="dev", tool="find_symbol",
        args_json=json.dumps({"name": "Foo"}), repo_ids_json=json.dumps([1, 2]),
    )
    rows = conn.execute("SELECT * FROM audit").fetchall()
    assert len(rows) == 1
    row = rows[0]
    assert row["tool"] == "find_symbol"
    assert row["user_id"] == 7
    assert row["username"] == "dev"
    assert json.loads(row["args_json"]) == {"name": "Foo"}
    assert json.loads(row["repo_ids_json"]) == [1, 2]


# ---------------------------------------------------------------------------
# A successful tool call, through the real stack (create_app, real bearer
# auth, real JSON-RPC dispatch) writes exactly one row naming that tool and
# that caller.
# ---------------------------------------------------------------------------

def test_successful_tool_call_writes_one_audit_row(repo_cfg):
    cfg, rid = repo_cfg
    app = create_app(cfg, client=_mock_client(_gitlab_ok([{"id": 101}])))
    with TestClient(app.streamable_http_app(), base_url="http://localhost:1234",
                     raise_server_exceptions=True) as client:
        headers = {**JSONRPC_HEADERS, "Authorization": "Bearer dev-token"}
        call_headers = _mcp_session(client, headers)
        result = _call_tool(client, call_headers, "index_status", {})

    assert result["isError"] is False
    rows = _audit_rows(cfg.index.db_path)
    assert len(rows) == 1
    assert rows[0]["tool"] == "index_status"
    assert rows[0]["username"] == "dev"
    assert rows[0]["user_id"] == 7
    assert json.loads(rows[0]["repo_ids_json"]) == [rid]


# ---------------------------------------------------------------------------
# A denied call -- rejected at the auth gate, before any tool is dispatched --
# is exactly the thing an audit log exists to capture, and must write a row
# too, not just successful calls.
# ---------------------------------------------------------------------------

def test_denied_call_writes_audit_row(repo_cfg):
    cfg, _rid = repo_cfg
    app = create_app(cfg, client=_mock_client(_gitlab_revokes))
    client = TestClient(app.streamable_http_app(), raise_server_exceptions=False)

    resp = client.get(MCP_PATH, headers={"Authorization": "Bearer revoked-token"})
    assert resp.status_code == 401

    rows = _audit_rows(cfg.index.db_path)
    assert len(rows) == 1
    assert rows[0]["user_id"] is None
    assert rows[0]["username"] is None


# ---------------------------------------------------------------------------
# No credential may ever be recorded. Assert against the FULL serialised row
# (every column), not just args_json -- a token leaking into username, tool,
# or repo_ids_json by some future edit must be caught here too.
# ---------------------------------------------------------------------------

def test_recorded_row_contains_no_credential(repo_cfg):
    cfg, rid = repo_cfg
    app = create_app(cfg, client=_mock_client(_gitlab_ok([{"id": 101}])))
    with TestClient(app.streamable_http_app(), base_url="http://localhost:1234",
                     raise_server_exceptions=True) as client:
        headers = {**JSONRPC_HEADERS, "Authorization": f"Bearer {SECRET_TOKEN}"}
        call_headers = _mcp_session(client, headers)
        _call_tool(client, call_headers, "get_file", {"repo_id": rid, "path": "a.c"})

    rows = _audit_rows(cfg.index.db_path)
    assert rows, "expected at least one audit row"
    for row in rows:
        serialised = json.dumps(row)
        assert SECRET_TOKEN not in serialised


# ---------------------------------------------------------------------------
# A failing audit write must not break the tool call it accompanies: a
# denied disk or a locked database must not turn a real, correct query
# result into a failure for the developer.
# ---------------------------------------------------------------------------

def test_audit_write_failure_does_not_break_tool_call(repo_cfg, monkeypatch):
    cfg, rid = repo_cfg
    import argus.mcpsrv.tools as tools_mod

    def _boom(conn, **kwargs):
        raise sqlite3.OperationalError("database is locked")

    monkeypatch.setattr(tools_mod.writes, "record_audit", _boom)

    app = create_app(cfg, client=_mock_client(_gitlab_ok([{"id": 101}])))
    with TestClient(app.streamable_http_app(), base_url="http://localhost:1234",
                     raise_server_exceptions=True) as client:
        headers = {**JSONRPC_HEADERS, "Authorization": "Bearer dev-token"}
        call_headers = _mcp_session(client, headers)
        result = _call_tool(client, call_headers, "get_file", {"repo_id": rid, "path": "a.c"})

    assert result["isError"] is False
    assert result["structuredContent"]["content"] == "int"


# ---------------------------------------------------------------------------
# The `token is None` branch -- a missing Authorization header, a non-Bearer
# scheme, and a blank Bearer token -- rejects before acl.resolve is ever
# called, so no Identity, and previously no audit row, ever existed for it.
# An anonymous or garbage-token prober is exactly the traffic an audit log
# exists to catch, so each of these three shapes must write one row too.
# ---------------------------------------------------------------------------

def _assert_single_denied_audit_row(cfg) -> dict:
    rows = _audit_rows(cfg.index.db_path)
    assert len(rows) == 1
    assert rows[0]["user_id"] is None
    assert rows[0]["username"] is None
    assert rows[0]["tool"] == server_mod._DENIED_AT_GATE_TOOL
    return rows[0]


def test_missing_auth_header_writes_audit_row(repo_cfg):
    cfg, _rid = repo_cfg
    app = create_app(cfg, client=_mock_client(_gitlab_ok([{"id": 101}])))
    client = TestClient(app.streamable_http_app(), raise_server_exceptions=False)

    resp = client.get(MCP_PATH)
    assert resp.status_code == 401

    row = _assert_single_denied_audit_row(cfg)
    assert SECRET_TOKEN not in json.dumps(row)


def test_non_bearer_scheme_writes_audit_row(repo_cfg):
    cfg, _rid = repo_cfg
    app = create_app(cfg, client=_mock_client(_gitlab_ok([{"id": 101}])))
    client = TestClient(app.streamable_http_app(), raise_server_exceptions=False)

    # The credential-shaped value after "Basic" is exactly what must never
    # reach the audit row: a non-Bearer scheme is rejected by _extract_bearer
    # without ever being treated as a token, but the row itself must prove it.
    resp = client.get(MCP_PATH, headers={"Authorization": f"Basic {SECRET_TOKEN}"})
    assert resp.status_code == 401

    row = _assert_single_denied_audit_row(cfg)
    assert SECRET_TOKEN not in json.dumps(row)


def test_blank_bearer_token_writes_audit_row(repo_cfg):
    cfg, _rid = repo_cfg
    app = create_app(cfg, client=_mock_client(_gitlab_ok([{"id": 101}])))
    client = TestClient(app.streamable_http_app(), raise_server_exceptions=False)

    resp = client.get(MCP_PATH, headers={"Authorization": "Bearer "})
    assert resp.status_code == 401

    row = _assert_single_denied_audit_row(cfg)
    assert SECRET_TOKEN not in json.dumps(row)


# ---------------------------------------------------------------------------
# A failing audit write at the `token is None` gate must not turn an
# already-decided 401 into a 500, exactly like the existing AclDenied path.
# ---------------------------------------------------------------------------

def test_audit_write_failure_at_gate_still_returns_401(repo_cfg, monkeypatch):
    cfg, _rid = repo_cfg

    def _boom(conn, **kwargs):
        raise sqlite3.OperationalError("database is locked")

    monkeypatch.setattr(server_mod.writes, "record_audit", _boom)

    app = create_app(cfg, client=_mock_client(_gitlab_ok([{"id": 101}])))
    client = TestClient(app.streamable_http_app(), raise_server_exceptions=False)

    resp = client.get(MCP_PATH)  # no Authorization header at all

    assert resp.status_code == 401
