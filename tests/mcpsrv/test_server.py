from __future__ import annotations

import sqlite3
import threading
import time

import httpx
import pytest
from starlette.requests import Request
from starlette.responses import JSONResponse
from starlette.testclient import TestClient

import argus.mcpsrv.server as server_mod
from argus.config import Config, GitLabConfig, IndexConfig
from argus.mcpsrv.server import create_app
from argus.store import writes
from argus.store.db import connect_readonly, open_db
from argus.store.db import migrate as db_migrate

MCP_PATH = "/mcp"  # FastMCP's default streamable_http_path


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


def _slow_gitlab(projects, delay_seconds):
    """A GitLab double whose `/user` response takes `delay_seconds` to
    return, simulating the round-trip a real cache-miss auth makes."""

    def handler(request):
        if request.url.path.endswith("/user"):
            time.sleep(delay_seconds)
            return httpx.Response(200, json={"id": 7, "username": "dev"})
        if request.url.path.endswith("/projects"):
            page = dict(request.url.params).get("page", "1")
            return httpx.Response(200, json=projects if page == "1" else [])
        return httpx.Response(404)

    return handler


@pytest.fixture
def cfg(tmp_path):
    db_path = tmp_path / "argus.db"
    conn = open_db(db_path)
    writes.upsert_repo(conn, gitlab_id=101, path_with_namespace="g/a",
                        default_branch="main", http_url="x")
    conn.close()
    return Config(
        gitlab=GitLabConfig(url="https://gl.test", token="service-token"),
        index=IndexConfig(data_dir=tmp_path / "data", db_path=db_path),
    )


def _client_for(cfg, handler):
    """Build a FastMCP app wired to a mocked GitLab and wrap it in a
    TestClient that returns 5xx as a plain response instead of raising --
    the point of several tests below is to see what status code comes back
    when auth is missing entirely, including the accidental-500 case.
    """
    app = create_app(cfg, client=_mock_client(handler))
    return app, TestClient(app.streamable_http_app(), raise_server_exceptions=False)


def _local_repo_id(cfg, gitlab_id):
    conn = connect_readonly(cfg.index.db_path)
    try:
        return conn.execute(
            "SELECT id FROM repos WHERE gitlab_id = ?", (gitlab_id,)
        ).fetchone()["id"]
    finally:
        conn.close()


def test_unauthenticated_call_is_rejected(cfg):
    _app, client = _client_for(cfg, _gitlab_ok([{"id": 101}]))
    resp = client.get(MCP_PATH)
    assert resp.status_code == 401
    assert "authorization" in resp.json()["error"].lower()


def test_unauthenticated_post_to_mcp_is_rejected(cfg):
    """The five tests around this one all exercise auth with `client.get`,
    proving the routing layer refuses an unauthenticated request but never
    the tool-dispatch layer real MCP clients actually use: a JSON-RPC POST.
    This sends one, unauthenticated, to confirm the middleware -- which runs
    ahead of all routing -- refuses it before any tool call is dispatched.
    """
    def explode(request):
        raise AssertionError("must not call GitLab for a rejected POST")

    _app, client = _client_for(cfg, explode)
    resp = client.post(
        MCP_PATH,
        json={"jsonrpc": "2.0", "id": 1, "method": "tools/list", "params": {}},
    )
    assert resp.status_code == 401
    assert "authorization" in resp.json()["error"].lower()


def test_malformed_non_bearer_header_is_rejected(cfg):
    _app, client = _client_for(cfg, _gitlab_ok([{"id": 101}]))
    resp = client.get(MCP_PATH, headers={"Authorization": "Basic dXNlcjpwYXNz"})
    assert resp.status_code == 401
    assert "authorization" in resp.json()["error"].lower()


def test_blank_bearer_token_is_rejected(cfg):
    """A `Bearer` header with no (or all-whitespace) token must be caught by
    _extract_bearer's own guard, never fall through to acl.resolve.

    The status code alone (401) does not distinguish the two paths -- both
    reject. The body does: _extract_bearer's guard produces the same
    "...Authorization..." message as the other malformed-header cases,
    while acl.resolve's fallback ("No credential was sent...") contains no
    mention of "authorization" at all. That difference is exactly what this
    assertion -- shared with the two sibling tests above -- pins down: it
    passes today and fails if _extract_bearer's `or None` (dropping a blank
    token) is ever removed, at which point a blank Bearer token would reach
    acl.resolve and get denied there instead, on a message this assertion
    does not accept.
    """
    _app, client = _client_for(cfg, _gitlab_ok([{"id": 101}]))
    resp = client.get(MCP_PATH, headers={"Authorization": "Bearer "})
    assert resp.status_code == 401
    assert "authorization" in resp.json()["error"].lower()


def test_healthz_needs_no_auth(cfg):
    def explode(request):
        raise AssertionError("must not call GitLab for /healthz")

    _app, client = _client_for(cfg, explode)
    resp = client.get("/healthz")
    assert resp.status_code == 200
    assert resp.json() == {"status": "ok"}


def test_acl_denied_becomes_401_carrying_its_message(cfg):
    _app, client = _client_for(cfg, _gitlab_revokes)
    resp = client.get(MCP_PATH, headers={"Authorization": "Bearer revoked-token"})
    assert resp.status_code == 401
    assert "token" in resp.json()["error"].lower()


def test_valid_token_reaches_handler_with_correct_identity(cfg):
    app, _ = _client_for(cfg, _gitlab_ok([{"id": 101}]))

    @app.custom_route("/whoami", methods=["GET"])
    async def whoami(request: Request) -> JSONResponse:
        ident = request.state.identity
        return JSONResponse({
            "user_id": ident.user_id,
            "username": ident.username,
            "allowed_repo_ids": ident.allowed_repo_ids,
        })

    client = TestClient(app.streamable_http_app(), raise_server_exceptions=False)
    resp = client.get("/whoami", headers={"Authorization": "Bearer dev-token"})

    assert resp.status_code == 200
    body = resp.json()
    assert body["user_id"] == 7
    assert body["username"] == "dev"
    assert body["allowed_repo_ids"] == [_local_repo_id(cfg, 101)]


def test_lowercase_bearer_scheme_is_accepted(cfg):
    """RFC 7235 makes the auth scheme case-insensitive. Hermes always sends
    the canonical `Bearer`, but another MCP client sending `bearer` must not
    get rejected on scheme case alone -- only the token stays case-sensitive.
    """
    app, _ = _client_for(cfg, _gitlab_ok([{"id": 101}]))

    @app.custom_route("/whoami2", methods=["GET"])
    async def whoami2(request: Request) -> JSONResponse:
        return JSONResponse({"user_id": request.state.identity.user_id})

    client = TestClient(app.streamable_http_app(), raise_server_exceptions=False)
    resp = client.get("/whoami2", headers={"Authorization": "bearer dev-token"})

    assert resp.status_code == 200
    assert resp.json()["user_id"] == 7


def test_slow_acl_resolution_does_not_block_other_requests(cfg):
    """A cache-miss auth makes a synchronous, several-hundred-ms-worst-case
    GitLab round-trip. On a single-event-loop server (uvicorn), running that
    synchronously in the request coroutine stalls every other in-flight
    connection for the duration -- one slow login blocks the whole server.

    This reproduces that shape: one request's ACL resolution is artificially
    slow, and a concurrent, auth-exempt `/healthz` request must still return
    promptly rather than queuing up behind it.
    """
    delay = 0.3
    _app, client = _client_for(cfg, _slow_gitlab([{"id": 101}], delay))

    with client:
        healthz_elapsed = []

        def call_healthz():
            # Give the auth request a head start so it is mid-flight
            # (blocked on the mocked GitLab call) when healthz fires.
            time.sleep(delay / 6)
            start = time.perf_counter()
            resp = client.get("/healthz")
            healthz_elapsed.append(time.perf_counter() - start)
            assert resp.status_code == 200

        def call_auth():
            client.get(MCP_PATH, headers={"Authorization": "Bearer dev-token"})

        t_auth = threading.Thread(target=call_auth)
        t_healthz = threading.Thread(target=call_healthz)
        t_auth.start()
        t_healthz.start()
        t_auth.join()
        t_healthz.join()

    # healthz must come back well before the slow ACL round-trip finishes --
    # it must not be stuck waiting behind it on the event loop.
    assert healthz_elapsed[0] < delay / 2


def test_create_app_migrates_schema_before_any_request(tmp_path):
    """`create_app` must apply schema before serving traffic.

    A never-migrated database is an empty sqlite file with no tables at
    all -- if migration only happened lazily on the first request, this
    reproduces the trap the reviewer flagged: the very first inbound
    request would be the one applying schema, and until then (or if the
    server never gets an authenticated request) the file has no acl_cache
    table for acl.resolve's cache lookup to use.

    No request is issued here at all: the schema must already be present
    the moment create_app returns.
    """
    db_path = tmp_path / "argus.db"
    sqlite3.connect(db_path).close()  # bare file, no migration applied

    cfg = Config(
        gitlab=GitLabConfig(url="https://gl.test", token="service-token"),
        index=IndexConfig(data_dir=tmp_path / "data", db_path=db_path),
    )

    create_app(cfg, client=_mock_client(_gitlab_ok([])))

    conn = connect_readonly(db_path)
    try:
        names = {r["name"] for r in conn.execute(
            "SELECT name FROM sqlite_master WHERE type = 'table'"
        )}
    finally:
        conn.close()
    assert {"acl_cache", "repos"} <= names


def test_middleware_does_not_migrate_per_request(cfg, monkeypatch):
    """Schema migration is a startup concern, not a per-request one.

    `open_db` (connect + migrate) previously ran on every incoming request
    via the middleware; that gives untrusted inbound traffic the ability to
    trigger a schema change -- e.g. the first request after deploying a
    build carrying a new migration applies it. Assert migrate is invoked
    exactly once (at create_app time) no matter how many requests follow.
    """
    calls = []

    def counting_migrate(conn):
        calls.append(None)
        return db_migrate(conn)

    # raising=False: if a reverted implementation no longer imports `migrate`
    # into this module at all, this simply adds an inert attribute rather
    # than erroring, so the assertion below fails cleanly on its own merits.
    monkeypatch.setattr(server_mod, "migrate", counting_migrate, raising=False)

    app = create_app(cfg, client=_mock_client(_gitlab_ok([{"id": 101}])))
    client = TestClient(app.streamable_http_app(), raise_server_exceptions=False)
    for _ in range(3):
        client.get(MCP_PATH, headers={"Authorization": "Bearer dev-token"})

    assert len(calls) == 1


# --- the admin index surface ----------------------------------------------
#
# The panel's Indexing card reads all three of these. Two of them were broken
# in ways only a live deployment showed: the per-repo list raised NameError
# inside a broad except that stored the message in a field the panel did not
# render -- so the table was silently empty and "never worked" looked exactly
# like "nothing indexed yet".


@pytest.fixture
def admin_cfg(cfg, monkeypatch):
    monkeypatch.setenv("ARGUS_ADMIN_TOKEN", "admin-secret")
    return cfg


def _admin_client(admin_cfg):
    app = create_app(admin_cfg, client=_mock_client(_gitlab_ok([])))
    return TestClient(app.streamable_http_app(), raise_server_exceptions=False)


def test_index_status_lists_indexed_repos(admin_cfg):
    """The regression: `connect_readonly` was not imported, so this raised
    NameError and the panel's per-repo progress table was always empty."""
    client = _admin_client(admin_cfg)
    r = client.get("/admin/index/status",
                   headers={"x-argus-admin-token": "admin-secret"})
    assert r.status_code == 200
    job = r.json()["job"]
    assert "repos_error" not in job, f"status route failed: {job.get('repos_error')}"
    assert [row["repo"] for row in r.json()["repos"]] == ["g/a"]


def test_index_status_needs_the_admin_token(admin_cfg):
    client = _admin_client(admin_cfg)
    assert client.get("/admin/index/status").status_code == 403
    assert client.get("/admin/index/status",
                      headers={"x-argus-admin-token": "wrong"}).status_code == 403


def test_allow_partial_is_passed_through_to_the_child(admin_cfg, monkeypatch):
    """The panel's opt-in has to reach the command line, or the refusal tells
    the operator to re-run with a flag the UI cannot supply."""
    seen = {}

    class FakeProc:
        stdout = iter(["root/eal-core: up to date\n"])

        def wait(self):
            return 0

    def fake_popen(argv, **kw):
        seen["argv"] = argv
        return FakeProc()

    monkeypatch.setattr(server_mod.subprocess, "Popen", fake_popen)
    client = _admin_client(admin_cfg)

    r = client.post("/admin/index", json={"branches": [], "allow_partial": True},
                    headers={"x-argus-admin-token": "admin-secret"})
    assert r.status_code == 200 and r.json()["allow_partial"] is True

    for _ in range(200):                     # the child runs on a thread
        if "argv" in seen:
            break
        time.sleep(0.01)
    assert "--allow-partial-enumeration" in seen["argv"]


def test_allow_partial_is_absent_unless_asked_for(admin_cfg, monkeypatch):
    seen = {}

    class FakeProc:
        stdout = iter([])

        def wait(self):
            return 0

    monkeypatch.setattr(server_mod.subprocess, "Popen",
                        lambda argv, **kw: seen.setdefault("argv", argv) or FakeProc())
    client = _admin_client(admin_cfg)
    client.post("/admin/index", json={"branches": []},
                headers={"x-argus-admin-token": "admin-secret"})

    for _ in range(200):
        if "argv" in seen:
            break
        time.sleep(0.01)
    assert "--allow-partial-enumeration" not in seen["argv"]


def test_the_childs_output_is_mirrored_to_the_server_log(admin_cfg, monkeypatch, capsys):
    """Docker captures this process's stdout, Promtail ships it to Loki, and
    that is the only reason an index run has any history at all. The child's
    output used to be captured into the panel's tail and dropped, so indexing
    was the one part of Argus with no searchable log."""
    class FakeProc:
        stdout = iter(['{"ts": "2026-01-01T00:00:00Z", "event": "index_end"}\n',
                       "root/eal-core: up to date\n"])

        def wait(self):
            return 0

    monkeypatch.setattr(server_mod.subprocess, "Popen",
                        lambda argv, **kw: FakeProc())
    client = _admin_client(admin_cfg)
    client.post("/admin/index", json={"branches": []},
                headers={"x-argus-admin-token": "admin-secret"})

    out = ""
    for _ in range(200):
        out = capsys.readouterr().out
        if "index_end" in out:
            break
        time.sleep(0.01)
    assert '"event": "index_end"' in out, "the audit line never reached stdout"
    assert "root/eal-core: up to date" in out
