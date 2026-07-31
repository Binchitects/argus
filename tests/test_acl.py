import httpx, pytest, json
from argus import acl
from argus.config import GitLabConfig
from argus.store.db import open_db
from argus.store import writes

CFG = GitLabConfig(url="https://gl.test", token="service-token")


def _client(handler):
    return httpx.Client(transport=httpx.MockTransport(handler))


def _ok_handler(projects):
    def handler(request):
        if request.url.path.endswith("/user"):
            return httpx.Response(200, json={"id": 7, "username": "dev"})
        if request.url.path.endswith("/projects"):
            page = dict(request.url.params).get("page", "1")
            return httpx.Response(200, json=projects if page == "1" else [])
        return httpx.Response(404)
    return handler


@pytest.fixture
def conn(tmp_path):
    c = open_db(tmp_path / "i.db")
    writes.upsert_repo(c, gitlab_id=101, path_with_namespace="g/a",
                       default_branch="main", http_url="x")
    writes.upsert_repo(c, gitlab_id=102, path_with_namespace="g/b",
                       default_branch="main", http_url="x")
    return c


def test_resolves_and_maps_gitlab_ids_to_repo_ids(conn):
    ident = acl.resolve(conn, CFG, "dev-token",
                        client=_client(_ok_handler([{"id": 101}])))
    rid = conn.execute("SELECT id FROM repos WHERE gitlab_id = 101").fetchone()["id"]
    assert ident.allowed_repo_ids == [rid]
    assert ident.username == "dev"


def test_unknown_gitlab_project_is_dropped_not_allowed(conn):
    ident = acl.resolve(conn, CFG, "dev-token",
                        client=_client(_ok_handler([{"id": 999}])))
    assert ident.allowed_repo_ids == []


def test_token_is_never_stored_in_plaintext(conn):
    acl.resolve(conn, CFG, "super-secret", client=_client(_ok_handler([{"id": 101}])))
    blob = json.dumps([dict(r) for r in conn.execute("SELECT * FROM acl_cache")])
    assert "super-secret" not in blob


def test_cache_hit_makes_no_http_call(conn):
    acl.resolve(conn, CFG, "dev-token", client=_client(_ok_handler([{"id": 101}])))
    def explode(request):
        raise AssertionError("should not have called GitLab on a cache hit")
    acl.resolve(conn, CFG, "dev-token", client=_client(explode))


def test_expired_cache_refetches(conn):
    acl.resolve(conn, CFG, "dev-token", client=_client(_ok_handler([{"id": 101}])),
                now=lambda: 1000)
    ident = acl.resolve(conn, CFG, "dev-token",
                        client=_client(_ok_handler([{"id": 101}, {"id": 102}])),
                        now=lambda: 1000 + 601)
    assert len(ident.allowed_repo_ids) == 2


def test_gitlab_down_with_stale_cache_serves_stale(conn):
    acl.resolve(conn, CFG, "dev-token", client=_client(_ok_handler([{"id": 101}])),
                now=lambda: 1000)
    def down(request):
        raise httpx.ConnectError("unreachable")
    ident = acl.resolve(conn, CFG, "dev-token", client=_client(down),
                        now=lambda: 1000 + 900)
    assert len(ident.allowed_repo_ids) == 1


def test_gitlab_down_with_no_cache_denies(conn):
    def down(request):
        raise httpx.ConnectError("unreachable")
    with pytest.raises(acl.AclDenied):
        acl.resolve(conn, CFG, "unknown-token", client=_client(down))


def test_revoked_token_denies(conn):
    def unauthorized(request):
        return httpx.Response(401, json={"message": "401 Unauthorized"})
    with pytest.raises(acl.AclDenied, match="token"):
        acl.resolve(conn, CFG, "revoked", client=_client(unauthorized))


def test_requests_reporter_level_projects_only(conn):
    """min_access_level=20 (Reporter) must be sent to GitLab on every listing
    call -- Guest (10) can see a project but cannot read its code, so a lower
    floor would let the allowlist include repos the developer cannot read.
    """
    captured = {}
    def handler(request):
        if request.url.path.endswith("/user"):
            return httpx.Response(200, json={"id": 7, "username": "dev"})
        if request.url.path.endswith("/projects"):
            captured.update(dict(request.url.params))
            page = captured.get("page", "1")
            return httpx.Response(200, json=[{"id": 101}] if page == "1" else [])
        return httpx.Response(404)
    acl.resolve(conn, CFG, "dev-token", client=_client(handler))
    assert captured["min_access_level"] == "20"


def test_stale_beyond_grace_denies(conn):
    acl.resolve(conn, CFG, "dev-token", client=_client(_ok_handler([{"id": 101}])),
                now=lambda: 1000)
    def down(request):
        raise httpx.ConnectError("unreachable")
    with pytest.raises(acl.AclDenied):
        acl.resolve(conn, CFG, "dev-token", client=_client(down),
                    now=lambda: 1000 + 100_000)
