"""Read-only access resolution for chat users, and "you have no access" notices."""
from __future__ import annotations

import asyncio

import httpx
import pytest
from starlette.applications import Starlette
from starlette.requests import Request
from starlette.responses import JSONResponse
from starlette.routing import Route
from starlette.testclient import TestClient

from argus import access, acl
from argus.config import Config, GitLabConfig, IndexConfig
from argus.mcpsrv import tools
from argus.mcpsrv.server import BearerAuthMiddleware
from argus.store import writes
from argus.store.db import open_db

CFG = GitLabConfig(url="https://gl.test", token="read-only-service-token")

ALICE = {"id": 7, "username": "alice", "name": "Alice Dev", "state": "active", "public_email": ""}
MAINTAINERS = [
    {"id": 50, "username": "owner", "name": "Olive Owner", "access_level": 50, "state": "active"},
    {"id": 40, "username": "maint", "name": "Max Maint", "access_level": 40, "state": "active"},
]


def gitlab(members_by_project: dict[int, list[dict]], users: list[dict] | None = None,
           calls: list | None = None, admin: bool = False):
    """A fake GitLab. `admin` changes what `/users?search=` is able to match.

    Measured against a real GitLab CE: an ADMIN token's search matches the
    private `email` field, while a read-only token's matches only
    `public_email` -- which is empty by default. The same lookup therefore
    succeeds on one deployment and returns nothing on another, with nothing in
    the payload to show why, so the difference is modelled rather than assumed.
    """
    users = users if users is not None else [ALICE]

    def handler(request: httpx.Request) -> httpx.Response:
        if calls is not None:
            calls.append(request)
        # A read-only token: any admin-only feature would be a bug.
        assert "sudo" not in {k.lower() for k in request.headers}
        path = request.url.path
        params = dict(request.url.params)
        if path.endswith("/users"):
            if "username" in params:
                return httpx.Response(200, json=[u for u in users if u["username"] == params["username"]])
            query = params.get("search", "")
            fields = ("public_email", "email") if admin else ("public_email",)
            return httpx.Response(200, json=[
                u for u in users
                if any(query in (u.get(f) or "") for f in fields)
            ])
        if "/members/all" in path:
            gid = int(path.split("/projects/")[1].split("/")[0])
            page = params.get("page", "1")
            return httpx.Response(200, json=members_by_project.get(gid, []) if page == "1" else [])
        return httpx.Response(404)
    return handler


def directory(handler, **kw) -> access.MemberDirectory:
    return access.MemberDirectory(CFG, client=httpx.Client(transport=httpx.MockTransport(handler)), **kw)


@pytest.fixture
def db(tmp_path):
    path = tmp_path / "argus.db"
    conn = open_db(path)
    ids = {}
    for gid, ns in ((101, "g/alpha"), (202, "g/beta")):
        rid = writes.upsert_repo(conn, gitlab_id=gid, path_with_namespace=ns,
                                 default_branch="main", http_url="x")
        fid = writes.upsert_file(conn, repo_id=rid, path="src/a.c", lang="c", size=4,
                                 blob_sha=f"s{gid}", content="secretword" if gid == 202 else "plain")
        writes.replace_symbols(conn, rid, fid, [
            {"name": "OnlyInBeta" if gid == 202 else "InAlpha", "kind": "function", "line": 1,
             "end_line": 2, "signature": "(void)", "scope": None, "is_public": 1}], f"s{gid}")
        ids[ns] = rid
    users = tmp_path / "users.yml"
    users.write_text("users:\n  alice:\n    email: 'alice@corp.example'\n    groups: [users]\n")
    yield path, conn, ids, users
    conn.close()


def test_chat_user_gets_only_projects_where_they_are_reporter(db):
    path, conn, ids, users = db
    members = {101: [dict(ALICE, access_level=30)] + MAINTAINERS,
               202: [dict(ALICE, access_level=10)] + MAINTAINERS}   # Guest cannot read code
    ident = access.resolve_person(conn, directory(gitlab(members)), "Alice@Corp.example",
                                  users_file=users)
    assert ident.username == "alice"
    assert ident.allowed_repo_ids == [ids["g/alpha"]]


def test_falls_back_to_public_email_when_username_is_unknown(db):
    path, conn, ids, _ = db
    bob = {"id": 9, "username": "bob.gl", "name": "Bob", "state": "active", "public_email": "bob@corp.example"}
    members = {202: [dict(bob, access_level=20)]}
    ident = access.resolve_person(conn, directory(gitlab(members, users=[bob])), "bob@corp.example")
    assert ident.allowed_repo_ids == [ids["g/beta"]]


def test_no_matching_account_is_denied_with_what_to_do(db):
    path, conn, _, users = db
    with pytest.raises(acl.AclDenied, match="same username as your GitLab account"):
        access.resolve_person(conn, directory(gitlab({}, users=[])), "nobody@corp.example",
                              users_file=users)


def test_gitlab_down_with_nothing_cached_denies(db):
    path, conn, _, users = db
    d = directory(lambda r: httpx.Response(503))
    with pytest.raises(acl.AclDenied, match="Cannot verify"):
        access.resolve_person(conn, d, "alice@corp.example", users_file=users)


def test_member_lists_are_cached(db):
    path, conn, _, users = db
    calls: list = []
    d = directory(gitlab({101: [dict(ALICE, access_level=30)]}, calls=calls))
    access.resolve_person(conn, d, "alice@corp.example", users_file=users)
    first = len(calls)
    access.resolve_person(conn, d, "alice@corp.example", users_file=users)
    assert len(calls) == first


def test_maintainers_are_named_owner_first(db):
    d = directory(gitlab({202: MAINTAINERS + [dict(ALICE, access_level=30)]}))
    assert d.maintainers(202) == ["@owner (Olive Owner)", "@maint (Max Maint)"]


def test_search_with_no_readable_match_names_the_repo_and_maintainers(db):
    path, conn, ids, _ = db
    d = directory(gitlab({202: MAINTAINERS}))
    me = acl.Identity(7, "alice", [ids["g/alpha"]])
    with pytest.raises(tools.AccessNotice) as exc:
        asyncio.run(tools.find_symbol_impl(path, me, "OnlyInBeta", notices=d))
    message = str(exc.value)
    assert "g/beta" in message and "@owner (Olive Owner)" in message and "Reporter" in message
    assert "src/a.c" not in message          # never a path from inside the repo
    with pytest.raises(tools.AccessNotice, match="g/beta"):
        asyncio.run(tools.search_code_impl(path, me, "secretword", notices=d))


def test_readable_results_are_returned_unchanged(db):
    path, conn, ids, _ = db
    d = directory(gitlab({202: MAINTAINERS}))
    rows = asyncio.run(tools.find_symbol_impl(path, acl.Identity(7, "a", [ids["g/alpha"]]),
                                              "InAlpha", notices=d))
    assert [r["repo_id"] for r in rows] == [ids["g/alpha"]]


def test_nothing_anywhere_is_still_an_empty_result(db):
    path, conn, ids, _ = db
    d = directory(gitlab({202: MAINTAINERS}))
    assert asyncio.run(tools.find_symbol_impl(path, acl.Identity(7, "a", [ids["g/alpha"]]),
                                              "NoSuchSymbol", notices=d)) == []


def test_get_file_on_an_unreadable_repo_says_whom_to_ask(db):
    path, conn, ids, _ = db
    d = directory(gitlab({202: MAINTAINERS}))
    with pytest.raises(tools.AccessNotice, match="g/beta"):
        asyncio.run(tools.get_file_impl(path, acl.Identity(7, "a", [ids["g/alpha"]]),
                                        ids["g/beta"], "src/a.c", notices=d))


def test_notices_off_keeps_the_old_silence(db):
    path, conn, ids, _ = db
    me = acl.Identity(7, "a", [ids["g/alpha"]])
    assert asyncio.run(tools.find_symbol_impl(path, me, "OnlyInBeta")) == []


# --------------------------------------------------------------- middleware --
def _app(cfg, handler):
    async def whoami(request: Request):
        ident = request.state.identity
        return JSONResponse({"user": ident.username, "repos": ident.allowed_repo_ids})
    inner = Starlette(routes=[Route("/mcp", whoami, methods=["GET"])])
    return TestClient(BearerAuthMiddleware(inner, cfg, client=httpx.Client(
        transport=httpx.MockTransport(handler))), raise_server_exceptions=False)


@pytest.fixture
def chat_env(db, monkeypatch, tmp_path):
    path, conn, ids, users = db
    monkeypatch.setenv("ARGUS_CHAT_CLIENT_TOKEN", "chat-secret")
    monkeypatch.setenv("ARGUS_AUTHELIA_USERS_FILE", str(users))
    cfg = Config(gitlab=CFG, index=IndexConfig(data_dir=tmp_path / "d", db_path=path))
    return cfg, ids


def test_chat_client_resolves_the_forwarded_person(chat_env):
    cfg, ids = chat_env
    client = _app(cfg, gitlab({202: [dict(ALICE, access_level=40)]}))
    resp = client.get("/mcp", headers={"Authorization": "Bearer chat-secret",
                                       "X-OpenWebUI-User-Email": "alice@corp.example"})
    assert resp.status_code == 200
    assert resp.json() == {"user": "alice", "repos": [ids["g/beta"]]}


def test_chat_client_token_through_the_proxy_is_refused(chat_env):
    cfg, _ = chat_env
    client = _app(cfg, gitlab({202: [dict(ALICE, access_level=40)]}))
    resp = client.get("/mcp", headers={"Authorization": "Bearer chat-secret",
                                       "X-OpenWebUI-User-Email": "alice@corp.example",
                                       "X-Forwarded-For": "203.0.113.9"})
    assert resp.status_code == 401
    assert "inside the stack" in resp.json()["error"]


def test_chat_client_without_a_person_is_refused(chat_env):
    cfg, _ = chat_env
    resp = _app(cfg, gitlab({})).get("/mcp", headers={"Authorization": "Bearer chat-secret"})
    assert resp.status_code == 401
    assert "did not say who is asking" in resp.json()["error"]


# --------------------------------------------------- email is the identity
#
# Open WebUI forwards the signed-in person's EMAIL, so that is the key. Keying
# on the username first made "your chat username must equal your GitLab
# username" a hard rule no user can see, and its failure reads as a permissions
# problem.

def test_email_decides_even_when_the_username_points_elsewhere(db):
    """The address wins, so the chat account need not be named after GitLab."""
    path, conn, ids, _ = db
    # `alice` is the local sign-in name; the GitLab account is `a.smith`, and
    # they share nothing but the address.
    a_smith = {"id": 7, "username": "a.smith", "name": "Alice Smith",
               "state": "active", "email": "alice@corp.example"}
    ident = access.resolve_person(conn, directory(gitlab({101: [dict(a_smith, access_level=30)]},
                                                         users=[a_smith], admin=True)),
                                  "alice@corp.example")
    assert ident.username == "a.smith"
    assert ident.allowed_repo_ids == [ids["g/alpha"]]


def test_private_email_matches_when_the_token_can_see_it(db):
    """What an ADMIN service token returns: `public_email` empty, `email` set.

    This is the field a read-only token never receives, and the reason the same
    lookup succeeds on one deployment and fails on another with nothing in the
    payload to show why.
    """
    path, conn, ids, _ = db
    private = {"id": 8, "username": "b.private", "name": "Bob",
               "state": "active", "email": "bob@corp.example", "public_email": None}
    ident = access.resolve_person(conn, directory(gitlab({202: [dict(private, access_level=20)]},
                                                         users=[private], admin=True)),
                                  "bob@corp.example")
    assert ident.username == "b.private"
    assert ident.allowed_repo_ids == [ids["g/beta"]]


def test_username_still_resolves_when_the_email_cannot_be_seen(db, tmp_path):
    """The read-only case, kept working: no email match, then username.

    A non-admin token cannot match a private address, so the Authelia username
    is the only route left. Dropping it when the email lookup fails would break
    every read-only deployment, which is the recommended one.
    """
    path, conn, ids, _ = db
    carol = {"id": 5, "username": "carol", "name": "Carol", "state": "active",
             "email": "", "public_email": ""}
    users_file = tmp_path / "users.yml"
    users_file.write_text(
        "users:\n  carol:\n    email: 'carol@corp.example'\n    groups: [users]\n")
    ident = access.resolve_person(conn, directory(gitlab({101: [dict(carol, access_level=30)]},
                                                         users=[carol])),
                                  "carol@corp.example", users_file=users_file)
    assert ident.username == "carol"


def test_a_partial_email_match_is_not_a_match(db):
    """`search=` is a substring search, so the comparison must be exact.

    Matching loosely here would hand someone another person's repositories,
    which is the one failure this module exists to prevent.
    """
    path, conn, _, _ = db
    almost = {"id": 6, "username": "eve", "name": "Eve", "state": "active",
              "public_email": "evil-alice@corp.example"}
    with pytest.raises(acl.AclDenied, match="No GitLab account matches"):
        access.resolve_person(conn, directory(gitlab({}, users=[almost])),
                              "alice@corp.example")
