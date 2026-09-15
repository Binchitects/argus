"""The one place that turns a `GitLabConfig` into an API credential.

Argus authenticates to GitLab two ways, and they differ in the header, not
just the value: an access token goes in ``PRIVATE-TOKEN``, an OAuth token in
``Authorization: Bearer``. Presenting either in the other's header is a 401,
so "swap the string" is not a valid way to add password support. Every API
caller asks this module for headers instead of building them.

Password mode exchanges the username and password for an OAuth token once,
then uses that token. The password is never sent anywhere but
``POST /oauth/token``, never placed in a URL, and never logged.

The exchange result is cached in memory for the life of the process. OAuth
tokens expire, so `invalidate` lets a caller that has just been told 401 drop
the cached token and force one re-exchange -- otherwise a long-running
indexer would authenticate perfectly at startup and fail for good some hours
later, with a config that is entirely correct.
"""

from __future__ import annotations

import re
import threading

import httpx

from . import tls
from .config import GitLabConfig


class CredentialError(Exception):
    """The configured GitLab credential was rejected or could not be obtained.

    Carries no credential material: the message is built from the config's
    own `redacted()` description, so it is safe to log and safe to return in
    an error.
    """


#: Cached OAuth tokens, keyed by (url, username). Not keyed by password: a
#: changed password should not accumulate a second entry, and the key would
#: then hold the secret.
_tokens: dict[tuple[str, str], str] = {}
_lock = threading.Lock()


#: Name Argus gives the token it mints for itself in password mode. Fixed, so
#: a restart REPLACES it rather than accumulating one token per start -- a
#: deployment that restarts daily would otherwise leave hundreds behind.
TOKEN_NAME = "argus"

#: The least privilege that works: enumerate projects and clone them. Not
#: `api`, which would also allow writes.
TOKEN_SCOPES = ("read_api", "read_repository")


def _mint_token(cfg: GitLabConfig, client: httpx.Client) -> str:
    """Trade a username and password for a personal access token.

    Two steps, because GitLab removed the OAuth resource-owner password grant
    this used to call -- measured against a real GitLab: it answers
    `unsupported_grant_type`, and the comment on this function used to conclude
    that no headless username/password path replaces it. That conclusion was
    wrong. What still works is exactly what a browser does: sign in through the
    web form, then create a token.

    The TOKEN is what reaches everything else. git's askpass protocol has no way
    to present a session cookie, and git-over-HTTP rejects one outright
    (measured: 401 on `info/refs`), so the session is used once to mint a
    credential that both transports accept, and then discarded.

    The password is sent only to `/users/sign_in`, in a body, never in a URL --
    a URL reaches access logs, proxies and `Referer` headers. Nothing here is
    logged, and every raised message is built from `cfg.redacted()`.
    """
    def _fail(what: str, exc: Exception) -> CredentialError:
        return CredentialError(
            f"could not reach GitLab at {cfg.redacted()} to {what}: "
            f"{type(exc).__name__}")

    # Drop any session left on this client by an earlier mint. The caller
    # reuses one client for a whole request, so the common case for a re-mint
    # is a client that is ALREADY signed in -- and GitLab redirects a signed-in
    # browser away from /users/sign_in to the dashboard. Following that
    # redirect serves a page with no sign-in form, and the re-mint dies with
    # "did not serve a sign-in form", which is the opposite of what was asked
    # for and points the operator at the URL.
    client.cookies.clear()

    try:
        page = client.get(f"{cfg.url}/users/sign_in")
    except httpx.HTTPError as exc:
        raise _fail("sign in", exc) from None

    found = re.search(r'name="authenticity_token" value="([^"]+)"', page.text)
    if not found:
        raise CredentialError(
            f"{cfg.url} did not serve a sign-in form, so username/password "
            f"sign-in cannot proceed. Use gitlab.auth=token with "
            f"ARGUS_GITLAB_TOKEN, or check the URL.")

    try:
        # follow_redirects matters: a successful sign-in is a 302, and without
        # following it the session cookie on the redirect is never stored.
        login = client.post(f"{cfg.url}/users/sign_in",
                            data={"user[login]": cfg.username,
                                  "user[password]": cfg.password,
                                  "authenticity_token": found.group(1)})
    except httpx.HTTPError as exc:
        raise _fail("sign in", exc) from None

    # Ask the API who we are: the only unambiguous check. A failed sign-in
    # re-renders the form with a flash message and still returns 200, so the
    # status code alone cannot tell the two apart.
    try:
        me = client.get(f"{cfg.url}/api/v4/user")
    except httpx.HTTPError as exc:
        raise _fail("verify the sign-in", exc) from None
    if me.status_code != 200:
        if "two-factor" in login.text.lower() or "otp" in login.text.lower():
            raise CredentialError(
                f"GitLab refused the sign-in for {cfg.redacted()} because the "
                f"account uses two-factor authentication, which a scripted "
                f"sign-in cannot satisfy. Use gitlab.auth=token with a "
                f"personal access token instead.")
        raise CredentialError(
            f"GitLab rejected the username/password sign-in for "
            f"{cfg.redacted()}. Check gitlab.username and "
            f"ARGUS_GITLAB_PASSWORD.")

    # A token minted on the pre-login page is rejected, so take the CSRF token
    # from a page fetched AFTER signing in.
    try:
        after = client.get(f"{cfg.url}/-/user_settings/profile")
        csrf = re.search(r'name="csrf-token" content="([^"]+)"', after.text)
        csrf_token = csrf.group(1) if csrf else found.group(1)

        # Replace any token we minted before, so restarts do not accumulate.
        listing = client.get(f"{cfg.url}/api/v4/personal_access_tokens")
        if listing.status_code == 200:
            for existing in listing.json():
                if existing.get("name") == TOKEN_NAME and not existing.get("revoked"):
                    client.delete(
                        f"{cfg.url}/api/v4/personal_access_tokens/{existing['id']}",
                        headers={"X-CSRF-Token": csrf_token})

        minted = client.post(
            f"{cfg.url}/-/user_settings/personal_access_tokens",
            headers={"X-CSRF-Token": csrf_token},
            # FLAT parameter names, and `scopes[]` repeated. Measured: the
            # nested `personal_access_token[...]` shape the API endpoint uses
            # is rejected here with "Name can't be blank".
            data={"name": TOKEN_NAME, "scopes[]": list(TOKEN_SCOPES),
                  "expires_at": ""})
    except httpx.HTTPError as exc:
        raise _fail("create a personal access token", exc) from None

    if minted.status_code not in (200, 201):
        raise CredentialError(
            f"GitLab signed {cfg.redacted()} in but would not create a personal "
            f"access token (HTTP {minted.status_code}). An administrator may "
            f"have disabled token creation for this account.")

    try:
        token = str(minted.json()["token"])
    except (ValueError, KeyError, TypeError):
        raise CredentialError(
            f"GitLab's token response for {cfg.redacted()} carried no token"
        ) from None
    if not token:
        raise CredentialError(
            f"GitLab returned an empty token for {cfg.redacted()}")
    return token


def credential(cfg: GitLabConfig, *,
               client: httpx.Client | None = None) -> tuple[str, str]:
    """Return ``(header_name, value)`` for authenticating to GitLab's API."""
    if cfg.auth == "token":
        return "PRIVATE-TOKEN", cfg.token

    key = (cfg.url, cfg.username)
    with _lock:
        cached = _tokens.get(key)
    if cached:
        return "Authorization", f"Bearer {cached}"

    owns = client is None
    client = client or tls.client_for(cfg, timeout=15.0)
    try:
        token = _mint_token(cfg, client)
    finally:
        if owns:
            client.close()

    with _lock:
        _tokens[key] = token
    return "Authorization", f"Bearer {token}"


def headers(cfg: GitLabConfig, *,
            client: httpx.Client | None = None) -> dict[str, str]:
    """The auth header for a GitLab API request, whichever mode is configured."""
    name, value = credential(cfg, client=client)
    return {name: value}


def git_password(cfg: GitLabConfig, *,
                 client: httpx.Client | None = None) -> str:
    """The secret to hand git as the password for an HTTPS clone.

    One value rather than a header, because git's askpass protocol has no
    notion of which header a token belongs in: `mirror` answers the username
    prompt with ``oauth2``, and GitLab accepts a personal access token as the
    password against that username either way.
    """
    _name, value = credential(cfg, client=client)
    return value.removeprefix("Bearer ")


def invalidate(cfg: GitLabConfig) -> None:
    """Forget any cached token, so the next call signs in again.

    For a caller that has just received a 401 with a credential that worked
    before -- the signature of an expired or revoked token.
    """
    with _lock:
        _tokens.pop((cfg.url, cfg.username), None)


def authorized(cfg: GitLabConfig, request, *,
               client: httpx.Client | None = None) -> httpx.Response:
    """Send a GET, and re-mint the token once if GitLab rejects it.

    ``request`` takes the auth headers and returns a response; it must be safe
    to call twice, so this is for reads only.

    A token minted from a password is not ours to keep: GitLab applies its
    default expiry when `expires_at` is blank (measured: one year) and an
    administrator can revoke it at any moment. Without this, a server that runs
    for months authenticates perfectly and then answers every request with a
    401 that is indistinguishable from a permissions problem -- and the fix
    applied is usually to go and re-check the ACL.

    Token mode never retries. A static token that GitLab rejects is wrong, and
    retrying it would only double the failed requests before the same error.
    """
    response = request(headers(cfg, client=client))
    if response.status_code != 401 or cfg.auth != "password":
        return response
    invalidate(cfg)
    return request(headers(cfg, client=client))
