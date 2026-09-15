"""GitLab credential modes: access token and username/password.

The two are not interchangeable at the protocol level: a configured access
token goes in ``PRIVATE-TOKEN``, while password mode mints its own token and
sends that as ``Authorization: Bearer``. So the tests that matter here are
about which header carries what, and about the password never going anywhere
it should not.
"""

from __future__ import annotations

import httpx
import pytest

from argus import credentials
from argus.config import ConfigError, GitLabConfig


@pytest.fixture(autouse=True)
def _clear_cache():
    credentials._tokens.clear()
    yield
    credentials._tokens.clear()


def token_cfg(**kw) -> GitLabConfig:
    return GitLabConfig(url="https://gitlab.invalid", token="glpat-xxx", **kw)


def password_cfg(**kw) -> GitLabConfig:
    return GitLabConfig(url="https://gitlab.invalid", username="dev",
                        password="s3cret", **kw)


def stub(handler) -> httpx.Client:
    return httpx.Client(transport=httpx.MockTransport(handler))


# --- mode selection -------------------------------------------------------


def test_a_token_alone_is_token_mode():
    assert token_cfg().auth == "token"


def test_a_username_alone_means_password_mode():
    """An operator who configures a username and no `auth` plainly means
    password mode; making them state it twice would be a papercut."""
    assert password_cfg().auth == "password"


def test_password_mode_requires_both_halves():
    with pytest.raises(ConfigError, match="username"):
        GitLabConfig(url="https://gitlab.invalid", auth="password",
                     username="dev")


def test_token_mode_requires_a_token():
    with pytest.raises(ConfigError, match="token is required"):
        GitLabConfig(url="https://gitlab.invalid", auth="token")


def test_an_unknown_mode_is_refused_by_name():
    with pytest.raises(ConfigError, match="'token' or 'password'"):
        GitLabConfig(url="https://gitlab.invalid", token="t", auth="kerberos")


# --- which header carries what -------------------------------------------


def test_a_token_goes_in_the_private_token_header():
    assert credentials.headers(token_cfg()) == {"PRIVATE-TOKEN": "glpat-xxx"}


def test_a_password_becomes_a_bearer_token():
    """Password mode ends in a personal access token, but the header follows
    the MODE. Presenting it as PRIVATE-TOKEN because the value happens to look
    like a `glpat-` token is what turns a working credential into an access
    error that reads as an ACL problem."""
    handler = _signin_flow()
    assert credentials.headers(password_cfg(), client=stub(handler)) == {
        "Authorization": "Bearer glpat-minted"}


def test_the_password_is_sent_in_the_body_not_the_url():
    """A URL reaches access logs, proxies and Referer headers. This one would
    carry a reusable password."""
    handler = _signin_flow()
    credentials.headers(password_cfg(), client=stub(handler))
    assert "s3cret" in handler.seen["login_body"], "expected it in the POST body"
    assert "s3cret" not in handler.seen["login_query"]


def test_the_exchange_happens_once_and_is_reused():
    """Minting is a full sign-in, so doing it per request would hand GitLab a
    new token on every API call of an indexing run."""
    handler = _signin_flow()
    mints = []

    def counting(request):
        if request.method == "POST" and "personal_access_tokens" in request.url.path:
            mints.append(request.url.path)
        return handler(request)

    cfg = password_cfg()
    client = stub(counting)
    credentials.headers(cfg, client=client)
    credentials.headers(cfg, client=client)
    assert len(mints) == 1, "minted twice for one process"


def test_invalidate_forces_a_fresh_sign_in():
    """A minted token can be revoked or expired mid-run. Without this a long
    indexing run authenticates perfectly at startup and fails for good hours
    later, with a config that is entirely correct."""
    mints = []
    handler = _signin_flow()

    def fresh(request):
        if request.url.path == "/-/user_settings/personal_access_tokens" \
                and request.method == "POST":
            mints.append(1)
            return httpx.Response(200, json={"token": f"glpat-{len(mints)}"})
        return handler(request)

    cfg = password_cfg()
    client = stub(fresh)
    assert credentials.headers(cfg, client=client)["Authorization"] == "Bearer glpat-1"
    credentials.invalidate(cfg)
    assert credentials.headers(cfg, client=client)["Authorization"] == "Bearer glpat-2"


# --- git cloning ----------------------------------------------------------


def test_git_password_strips_the_bearer_prefix():
    """git's askpass protocol wants the secret, not a header value. `Bearer `
    left on the front would be sent as part of the password."""
    handler = _signin_flow()
    assert credentials.git_password(password_cfg(), client=stub(handler)) == "glpat-minted"


def test_git_password_in_token_mode_is_the_token():
    assert credentials.git_password(token_cfg()) == "glpat-xxx"


# --- failures say what to do ---------------------------------------------


def test_a_rejected_sign_in_does_not_echo_the_password():
    handler = _signin_flow(user_status=401)
    with pytest.raises(credentials.CredentialError) as caught:
        credentials.headers(password_cfg(), client=stub(handler))
    assert "s3cret" not in str(caught.value)


def test_a_transport_failure_does_not_echo_the_password():
    """httpx errors can carry the request, and this request's body is the
    password. Only the class name and the redacted config go out."""
    def handler(request):
        raise httpx.ConnectError("refused", request=request)

    with pytest.raises(credentials.CredentialError) as caught:
        credentials.headers(password_cfg(), client=stub(handler))
    assert "s3cret" not in str(caught.value)


def test_a_token_response_without_a_token_is_an_error_not_a_blank_credential():
    """Returning "" would authenticate as nobody and read as an ACL problem."""
    handler = _signin_flow(mint={"message": "created"})
    with pytest.raises(credentials.CredentialError, match="carried no token"):
        credentials.headers(password_cfg(), client=stub(handler))


def test_redacted_never_names_the_secret():
    assert "s3cret" not in password_cfg().redacted()
    assert "glpat-xxx" not in token_cfg().redacted()
    assert "dev" in password_cfg().redacted()


def test_a_server_that_will_not_mint_tokens_says_so():
    """Signing in and being allowed to create a token are separate
    permissions: an administrator can grant the first and withhold the
    second, and the password is not the thing to go and check."""
    handler = _signin_flow()

    def refused(request):
        if request.url.path == "/-/user_settings/personal_access_tokens" \
                and request.method == "POST":
            return httpx.Response(403)
        return handler(request)

    with pytest.raises(credentials.CredentialError, match="personal access token"):
        credentials.headers(password_cfg(), client=stub(refused))


# --- password mode: sign in, then mint a token -----------------------------
#
# GitLab removed the OAuth resource-owner password grant this used to call
# (measured: `unsupported_grant_type`). What still works is what a browser
# does -- sign in through the web form, then create a personal access token --
# and the token, not the session, is what reaches git: git-over-HTTP rejects a
# session cookie outright.

SIGN_IN_PAGE = '<form><input name="authenticity_token" value="csrf-login"></form>'
PROFILE_PAGE = '<meta name="csrf-token" content="csrf-after">'


def _signin_flow(*, user_status=200, mint=None, tokens=None, twofa=False):
    """A fake GitLab that models the sequence, and records what was sent."""
    seen = {"posted": [], "headers": []}
    mint_body = mint if mint is not None else {"token": "glpat-minted"}

    def handler(request: httpx.Request) -> httpx.Response:
        path, method = request.url.path, request.method
        seen["posted"].append((method, path))
        seen["headers"].append(dict(request.headers))
        if path == "/users/sign_in" and method == "GET":
            return httpx.Response(200, text=SIGN_IN_PAGE)
        if path == "/users/sign_in" and method == "POST":
            seen["login_body"] = request.content.decode()
            seen["login_query"] = str(request.url.query)
            if twofa:
                # A failed sign-in re-renders the form, at 200, carrying the
                # reason. That is why the status code alone cannot be trusted.
                return httpx.Response(200, text=SIGN_IN_PAGE + "two-factor")
            return httpx.Response(302, headers={"Location": "/"})
        if path == "/api/v4/user":
            return httpx.Response(user_status, json={"username": "dev"})
        if path == "/-/user_settings/profile":
            return httpx.Response(200, text=PROFILE_PAGE)
        if path == "/api/v4/personal_access_tokens" and method == "GET":
            return httpx.Response(200, json=tokens or [])
        if path.startswith("/api/v4/personal_access_tokens/") and method == "DELETE":
            return httpx.Response(204)
        if path == "/-/user_settings/personal_access_tokens" and method == "POST":
            seen["mint_body"] = request.content.decode()
            return httpx.Response(200, json=mint_body)
        return httpx.Response(404)
    handler.seen = seen
    return handler


def test_password_mode_signs_in_and_mints_a_token():
    handler = _signin_flow()
    name, value = credentials.credential(password_cfg(), client=stub(handler))
    # A personal access token, sent the way a token is sent.
    assert (name, value) == ("Authorization", "Bearer glpat-minted")
    assert ("POST", "/users/sign_in") in handler.seen["posted"]
    assert ("POST", "/-/user_settings/personal_access_tokens") in handler.seen["posted"]


def test_the_minted_token_carries_the_narrow_scopes():
    handler = _signin_flow()
    credentials.credential(password_cfg(), client=stub(handler))
    body = handler.seen["mint_body"]
    assert "read_api" in body and "read_repository" in body
    assert "api=" not in body.replace("read_api", "")   # not full `api`


def test_a_previous_token_of_ours_is_revoked_before_minting():
    """Otherwise every restart leaves another token behind."""
    handler = _signin_flow(tokens=[{"id": 7, "name": credentials.TOKEN_NAME, "revoked": False},
                                   {"id": 8, "name": "someone-elses", "revoked": False}])
    credentials.credential(password_cfg(), client=stub(handler))
    deletes = [p for m, p in handler.seen["posted"] if m == "DELETE"]
    assert deletes == ["/api/v4/personal_access_tokens/7"]


def test_bad_credentials_say_so():
    handler = _signin_flow(user_status=401)
    with pytest.raises(credentials.CredentialError, match="username/password"):
        credentials.credential(password_cfg(), client=stub(handler))


def test_two_factor_is_named_rather_than_reported_as_a_bad_password():
    """The two need completely different fixes, and the password is usually
    right in the 2FA case."""
    handler = _signin_flow(user_status=401, twofa=True)
    with pytest.raises(credentials.CredentialError, match="two-factor"):
        credentials.credential(password_cfg(), client=stub(handler))


def test_a_page_without_a_sign_in_form_is_reported():
    handler = _signin_flow()
    inner = handler

    def no_form(request):
        if request.url.path == "/users/sign_in":
            return httpx.Response(200, text="<html>nothing here</html>")
        return inner(request)
    with pytest.raises(credentials.CredentialError, match="sign-in form"):
        credentials.credential(password_cfg(), client=stub(no_form))


# --- surviving a token that GitLab has thrown away -------------------------


def test_a_401_re_mints_and_retries_once():
    """A minted token carries GitLab's default expiry when `expires_at` is blank
    (measured: one year) and an administrator can revoke it at any time. Either
    way the server keeps running with a credential that is simply gone, and
    every request after that is a 401 that reads as a permissions problem --
    so the fix people reach for is the ACL, not the credential."""
    handler = _signin_flow(mint={"token": "glpat-fresh"})
    sign_ins = []

    def expiring(request):
        if request.url.path == "/api/v4/projects":
            live = dict(request.headers).get("authorization") == "Bearer glpat-fresh"
            return httpx.Response(200, json=[]) if live else httpx.Response(401, json={})
        if request.method == "POST" and request.url.path == "/users/sign_in":
            sign_ins.append(1)
        return handler(request)

    cfg = password_cfg()
    client = stub(expiring)
    credentials._tokens[(cfg.url, cfg.username)] = "glpat-expired"

    got = credentials.authorized(
        cfg, lambda auth: client.get(f"{cfg.url}/api/v4/projects", headers=auth),
        client=client)

    assert got.status_code == 200, "did not recover from a dead token"
    assert len(sign_ins) == 1, "expected exactly one re-mint"


def test_a_401_that_survives_the_re_mint_is_returned_as_is():
    """Otherwise a genuinely unauthorised account loops forever re-signing in."""
    handler = _signin_flow(mint={"token": "glpat-still-refused"})

    def always_401(request):
        if request.url.path == "/api/v4/projects":
            return httpx.Response(401, json={})
        return handler(request)

    cfg = password_cfg()
    client = stub(always_401)
    got = credentials.authorized(
        cfg, lambda auth: client.get(f"{cfg.url}/api/v4/projects", headers=auth),
        client=client)
    assert got.status_code == 401


def test_other_failures_are_not_retried():
    """A 404 or a 500 is not a credential problem, and signing in again cannot
    fix it -- it would only double every failing request."""
    handler = _signin_flow()
    calls = []

    def failing(request):
        if request.url.path == "/api/v4/projects":
            calls.append(request.url.path)
            return httpx.Response(500, text="boom")
        return handler(request)

    cfg = password_cfg()
    client = stub(failing)
    got = credentials.authorized(
        cfg, lambda auth: client.get(f"{cfg.url}/api/v4/projects", headers=auth),
        client=client)
    assert got.status_code == 500
    assert len(calls) == 1


def test_token_mode_never_re_mints():
    """A static token that GitLab rejects is wrong, and no amount of retrying
    produces a different one -- these are not ours to refresh."""
    calls = []

    def handler(request):
        calls.append(request.url.path)
        return httpx.Response(401, json={})

    got = credentials.authorized(
        token_cfg(), lambda auth: httpx.Response(401, json={}),
        client=stub(handler))
    assert got.status_code == 401
    assert calls == [], "token mode must not sign in"


def test_a_second_mint_on_the_same_client_still_finds_the_form():
    """Live-found, against a real GitLab: the client that minted the first
    token is still holding GitLab's session cookie, and GitLab redirects an
    already-signed-in browser away from /users/sign_in. Following that redirect
    serves the dashboard, which has no sign-in form, so the re-mint failed with
    "did not serve a sign-in form" -- pointing the operator at the URL instead
    of at a session that needed dropping. Unit tests missed it because a fake
    that always returns the form cannot redirect.

    This asserts the session is dropped between mints, which is the whole
    reason a re-mint works at all.
    """
    signed_in: list[str] = []
    minted: list[int] = []

    def handler(request: httpx.Request) -> httpx.Response:
        path, method = request.url.path, request.method
        if path == "/users/sign_in" and method == "GET":
            if "_gitlab_session" in request.headers.get("cookie", ""):
                # Exactly what GitLab does to a signed-in browser.
                signed_in.append(path)
                return httpx.Response(302, headers={"Location": "/dashboard"})
            return httpx.Response(200, text=SIGN_IN_PAGE)
        if path == "/dashboard":
            return httpx.Response(200, text="<html>dashboard</html>")
        if path == "/users/sign_in" and method == "POST":
            return httpx.Response(302, headers={"Location": "/",
                                                "Set-Cookie":
                                                "_gitlab_session=sess-1; Path=/"})
        if path == "/api/v4/user":
            return httpx.Response(200, json={"username": "dev"})
        if path == "/-/user_settings/profile":
            return httpx.Response(200, text=PROFILE_PAGE)
        if path == "/api/v4/personal_access_tokens":
            return httpx.Response(200, json=[])
        if path == "/-/user_settings/personal_access_tokens":
            minted.append(1)
            return httpx.Response(200, json={"token": f"glpat-{len(minted)}"})
        return httpx.Response(404)

    cfg = password_cfg()
    client = httpx.Client(transport=httpx.MockTransport(handler),
                          follow_redirects=True)

    first = credentials.credential(cfg, client=client)[1]
    credentials.invalidate(cfg)
    second = credentials.credential(cfg, client=client)[1]

    assert first == "Bearer glpat-1"
    assert second == "Bearer glpat-2", "the second mint did not get a form"
    assert signed_in == [], "the old session was still on the client"
