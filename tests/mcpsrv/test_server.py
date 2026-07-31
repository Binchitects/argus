from __future__ import annotations

import threading
import time

import httpx
import pytest
from starlette.requests import Request
from starlette.responses import JSONResponse
from starlette.testclient import TestClient

from argus.config import Config, GitLabConfig, IndexConfig
from argus.mcpsrv.server import create_app
from argus.store import writes
from argus.store.db import connect_readonly, open_db

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


def test_malformed_non_bearer_header_is_rejected(cfg):
    _app, client = _client_for(cfg, _gitlab_ok([{"id": 101}]))
    resp = client.get(MCP_PATH, headers={"Authorization": "Basic dXNlcjpwYXNz"})
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
