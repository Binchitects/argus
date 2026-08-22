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

import threading

import httpx

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


def _exchange(cfg: GitLabConfig, client: httpx.Client) -> str:
    """Trade username and password for an OAuth access token.

    Uses the resource-owner password grant. GitLab refuses this for accounts
    with 2FA enabled, and the error says so rather than reporting a bad
    password, because the two need completely different fixes and the
    password is usually right in the 2FA case.
    """
    try:
        resp = client.post(
            f"{cfg.url}/oauth/token",
            # In the BODY, never the query string: a URL reaches access logs,
            # proxies and `Referer` headers, and this one would carry a
            # reusable password.
            json={
                "grant_type": "password",
                "username": cfg.username,
                "password": cfg.password,
            },
        )
    except httpx.HTTPError as exc:
        # str(exc) on an httpx error names the URL, which is the endpoint and
        # not the credential -- but the request object it may carry has the
        # body. Only the class name and the redacted config go out.
        raise CredentialError(
            f"could not reach GitLab at {cfg.redacted()} to sign in: "
            f"{type(exc).__name__}"
        ) from None

    if resp.status_code in (400, 401):
        detail = ""
        code = ""
        try:
            body = resp.json()
            code = str(body.get("error") or "")
            detail = str(body.get("error_description") or code or "")
        except ValueError:
            detail = ""
        if code == "unsupported_grant_type" or "grant type" in detail.lower():
            # Measured against a real GitLab 19.2.1: the resource-owner
            # password grant is gone, and there is no headless replacement.
            # Deploy tokens are a username/password pair but cannot enumerate
            # projects, and the authorization-code flow needs a browser. So
            # this is not a misconfiguration the operator can correct by
            # adjusting the password -- the server will never accept one.
            raise CredentialError(
                f"{cfg.url} does not support password sign-in for the API: it "
                f"answered 'unsupported_grant_type'. Recent GitLab removed the "
                f"password grant, and no headless username/password path "
                f"replaces it -- deploy tokens cannot enumerate projects and "
                f"the authorization-code flow needs a browser. Create a "
                f"personal, group or project access token with read_api and "
                f"read_repository, then set gitlab.auth=token with "
                f"ARGUS_GITLAB_TOKEN."
            )
        if "2fa" in detail.lower() or "two-factor" in detail.lower():
            raise CredentialError(
                f"GitLab refused the password sign-in for {cfg.redacted()} "
                f"because the account uses two-factor authentication. The "
                f"password grant cannot satisfy 2FA -- use gitlab.auth=token "
                f"with a personal access token instead."
            )
        raise CredentialError(
            f"GitLab rejected the sign-in for {cfg.redacted()}"
            + (f": {detail}" if detail else "")
        )
    if resp.status_code >= 500:
        raise CredentialError(
            f"GitLab returned {resp.status_code} signing in as {cfg.redacted()}")
    if resp.status_code != 200:
        raise CredentialError(
            f"GitLab returned {resp.status_code} signing in as {cfg.redacted()}")

    try:
        token = str(resp.json()["access_token"])
    except (ValueError, KeyError, TypeError):
        raise CredentialError(
            f"GitLab's sign-in response for {cfg.redacted()} carried no "
            f"access_token"
        ) from None
    if not token:
        raise CredentialError(
            f"GitLab returned an empty access_token for {cfg.redacted()}")
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
    client = client or httpx.Client(timeout=15.0)
    try:
        token = _exchange(cfg, client)
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
    prompt with ``oauth2``, and GitLab accepts an access token or an OAuth
    token as the password against that username either way.
    """
    _name, value = credential(cfg, client=client)
    return value[len("Bearer "):] if value.startswith("Bearer ") else value


def invalidate(cfg: GitLabConfig) -> None:
    """Forget any cached OAuth token, so the next call signs in again.

    For a caller that has just received a 401 with a credential that worked
    before -- the signature of an expired OAuth token.
    """
    with _lock:
        _tokens.pop((cfg.url, cfg.username), None)
