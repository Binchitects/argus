from __future__ import annotations

import hashlib
import json
import logging
import sqlite3
import time
from dataclasses import dataclass

import httpx

from .config import GitLabConfig
from .store import writes

log = logging.getLogger(__name__)

TTL_SECONDS = 600
STALE_GRACE_SECONDS = 3600
MIN_ACCESS_LEVEL = 20  # Reporter. Guest (10) cannot read repository code.
PER_PAGE = 100
MAX_PAGES = 1000


class AclDenied(Exception):
    """Access could not be established. The message is read by an agent."""


class _GitLabUnwell(Exception):
    """Internal signal: GitLab responded but with a 5xx.

    Deliberately not AclDenied. A 5xx behind a load balancer is the normal
    presentation of an outage -- more common than a raw transport failure --
    and must reach the same stale-cache grace window as
    ``httpx.HTTPError``, rather than denying immediately. This type never
    escapes ``resolve``.
    """


@dataclass(frozen=True)
class Identity:
    user_id: int
    username: str
    allowed_repo_ids: list[int]


def _hash(token: str) -> str:
    return hashlib.sha256(token.encode("utf-8")).hexdigest()


def _map_to_repo_ids(conn: sqlite3.Connection, gitlab_ids: list[int]) -> list[int]:
    """Map GitLab project ids to local repo ids, dropping unknown projects.

    Dropping is the fail-closed behaviour: a project the index has never seen
    simply is not in the allowlist. An empty result stays empty -- every query
    treats [] as 'return nothing', never as 'skip the filter'.
    """
    if not gitlab_ids:
        return []
    marks = ",".join("?" for _ in gitlab_ids)
    rows = conn.execute(
        f"SELECT id FROM repos WHERE gitlab_id IN ({marks})", gitlab_ids
    ).fetchall()
    return sorted(r["id"] for r in rows)


def _fetch(cfg: GitLabConfig, token: str, client: httpx.Client) -> tuple[int, str, list[int]]:
    me = client.get(f"{cfg.url}/api/v4/user", headers={"PRIVATE-TOKEN": token})
    if me.status_code in (401, 403):
        raise AclDenied(
            "Your GitLab token was rejected. Refresh it and re-run "
            "`hermes mcp add argus --url <url> --auth header`."
        )
    if me.status_code >= 500:
        raise _GitLabUnwell(f"GitLab returned {me.status_code} for /user.")
    if me.status_code != 200:
        raise AclDenied(f"GitLab returned {me.status_code} for /user.")
    user = me.json()

    gitlab_ids: list[int] = []
    for page in range(1, MAX_PAGES + 1):
        resp = client.get(
            f"{cfg.url}/api/v4/projects",
            params={"membership": "true", "min_access_level": MIN_ACCESS_LEVEL,
                    "simple": "true", "per_page": PER_PAGE, "page": page},
            headers={"PRIVATE-TOKEN": token},
        )
        if resp.status_code >= 500:
            raise _GitLabUnwell(f"GitLab returned {resp.status_code} listing your projects.")
        if resp.status_code != 200:
            raise AclDenied(f"GitLab returned {resp.status_code} listing your projects.")
        batch = resp.json()
        if not batch:
            break
        gitlab_ids.extend(int(p["id"]) for p in batch)
    return int(user["id"]), user["username"], gitlab_ids


def resolve(conn: sqlite3.Connection, cfg: GitLabConfig, token: str, *,
            client: httpx.Client | None = None, now=None) -> Identity:
    now = now or time.time
    if not token:
        raise AclDenied("No credential was sent. Configure the server with --auth header.")

    key = _hash(token)
    cached = writes.get_acl_cache(conn, key)
    age = (now() - cached["fetched_at"]) if cached else None

    if cached is not None and age < TTL_SECONDS:
        return Identity(cached["user_id"], cached["username"],
                        json.loads(cached["repo_ids_json"]))

    owns_client = client is None
    client = client or httpx.Client(timeout=15.0)
    try:
        user_id, username, gitlab_ids = _fetch(cfg, token, client)
    except AclDenied:
        raise
    except (httpx.HTTPError, ValueError, _GitLabUnwell) as exc:
        # "GitLab is unwell": connection/timeout/transport failure, a 5xx
        # (_GitLabUnwell), or a 200 with an unparseable body (json.JSONDecodeError,
        # a ValueError) -- none of these are a bug in this module, and none of
        # them are GitLab telling us the token is bad. Serve stale inside the
        # grace window; deny otherwise. Narrowing to exactly these types,
        # rather than a bare except, matters: a bare except would silently
        # reclassify an actual programming error in _fetch (a KeyError, an
        # AssertionError from a test double, ...) as "GitLab is down" and
        # paper over it with a stale-cache response or a generic deny,
        # instead of surfacing it. AclDenied (401/403 -- "GitLab says no") is
        # re-raised above and never reaches this branch, so a revoked token
        # cannot keep working off a cached entry.
        if cached is not None and age < STALE_GRACE_SECONDS:
            log.warning("GitLab is unwell (%s); serving ACL cached %.0fs ago", exc, age)
            return Identity(cached["user_id"], cached["username"],
                            json.loads(cached["repo_ids_json"]))
        raise AclDenied(
            "Cannot verify your GitLab access right now and no recent cached "
            "permission exists, so access is denied. Retry shortly."
        ) from exc
    finally:
        if owns_client:
            client.close()

    repo_ids = _map_to_repo_ids(conn, gitlab_ids)
    writes.upsert_acl_cache(
        conn, token_hash=key, user_id=user_id, username=username,
        repo_ids_json=json.dumps(repo_ids), fetched_at=int(now()),
    )
    return Identity(user_id, username, repo_ids)
