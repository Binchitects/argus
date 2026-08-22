"""GitLab credential modes: access token and username/password.

The two are not interchangeable at the protocol level -- an access token goes
in ``PRIVATE-TOKEN``, an OAuth token in ``Authorization: Bearer`` -- so the
tests that matter here are about which header carries what, and about the
password never going anywhere it should not.
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
    """GitLab rejects an OAuth token presented as PRIVATE-TOKEN, so the mode
    has to change the header and not merely the value."""
    def handler(request):
        assert request.url.path == "/oauth/token"
        return httpx.Response(200, json={"access_token": "oauth-abc"})

    got = credentials.headers(password_cfg(), client=stub(handler))
    assert got == {"Authorization": "Bearer oauth-abc"}


def test_the_password_is_sent_in_the_body_not_the_url():
    """A URL reaches access logs, proxies and Referer headers. This one would
    carry a reusable password."""
    seen = {}

    def handler(request):
        seen["query"] = str(request.url.query)
        seen["body"] = request.content.decode()
        return httpx.Response(200, json={"access_token": "oauth-abc"})

    credentials.headers(password_cfg(), client=stub(handler))
    assert "s3cret" not in seen["query"]
    assert "s3cret" in seen["body"], "expected the password in the POST body"


def test_the_exchange_happens_once_and_is_reused():
    calls = []

    def handler(request):
        calls.append(request.url.path)
        return httpx.Response(200, json={"access_token": "oauth-abc"})

    cfg = password_cfg()
    client = stub(handler)
    credentials.headers(cfg, client=client)
    credentials.headers(cfg, client=client)
    assert len(calls) == 1, "signed in twice for one process"


def test_invalidate_forces_a_fresh_sign_in():
    """An OAuth token expires. Without this a long indexing run authenticates
    perfectly at startup and fails for good hours later, with a config that
    is entirely correct."""
    calls = []

    def handler(request):
        calls.append(1)
        return httpx.Response(200, json={"access_token": f"tok-{len(calls)}"})

    cfg = password_cfg()
    client = stub(handler)
    assert credentials.headers(cfg, client=client)["Authorization"] == "Bearer tok-1"
    credentials.invalidate(cfg)
    assert credentials.headers(cfg, client=client)["Authorization"] == "Bearer tok-2"


# --- git cloning ----------------------------------------------------------


def test_git_password_strips_the_bearer_prefix():
    """git's askpass protocol wants the secret, not a header value. `Bearer `
    left on the front would be sent as part of the password."""
    def handler(request):
        return httpx.Response(200, json={"access_token": "oauth-abc"})

    assert credentials.git_password(password_cfg(), client=stub(handler)) == "oauth-abc"


def test_git_password_in_token_mode_is_the_token():
    assert credentials.git_password(token_cfg()) == "glpat-xxx"


# --- failures say what to do ---------------------------------------------


def test_a_2fa_account_is_told_it_needs_a_token():
    """The password grant cannot satisfy 2FA. Reporting that as a bad
    password sends someone to reset a password that was already correct."""
    def handler(request):
        return httpx.Response(401, json={
            "error": "invalid_grant",
            "error_description": "This account requires 2FA to sign in.",
        })

    with pytest.raises(credentials.CredentialError, match="two-factor"):
        credentials.headers(password_cfg(), client=stub(handler))


def test_a_rejected_sign_in_does_not_echo_the_password():
    def handler(request):
        return httpx.Response(401, json={"error": "invalid_grant"})

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


def test_a_response_without_a_token_is_an_error_not_a_blank_credential():
    """Returning "" would authenticate as nobody and read as an ACL problem."""
    def handler(request):
        return httpx.Response(200, json={"token_type": "bearer"})

    with pytest.raises(credentials.CredentialError, match="no.*access_token"):
        credentials.headers(password_cfg(), client=stub(handler))


def test_redacted_never_names_the_secret():
    assert "s3cret" not in password_cfg().redacted()
    assert "glpat-xxx" not in token_cfg().redacted()
    assert "dev" in password_cfg().redacted()


def test_a_server_without_the_password_grant_says_to_use_a_token():
    """Verified against a real GitLab 19.2.1, which is where this was found:
    the resource-owner password grant has been removed, and no headless
    username/password path replaces it. The operator cannot fix this by
    correcting the password, so the error must not read like a bad one."""
    def handler(request):
        return httpx.Response(400, json={
            "error": "unsupported_grant_type",
            "error_description": "The authorization grant type is not "
                                 "supported by the authorization server.",
        })

    with pytest.raises(credentials.CredentialError) as caught:
        credentials.headers(password_cfg(), client=stub(handler))
    message = str(caught.value)
    assert "access token" in message, "must name the supported alternative"
    assert "ARGUS_GITLAB_TOKEN" in message, "must say where to put it"
