"""Who may read which indexed repository -- answered with a READ-ONLY service token.

Two jobs, both built on GitLab's project member lists:

* **Chat users.** Open WebUI calls Argus on behalf of whoever is signed in, but
  it can only send one shared credential, never that person's own GitLab
  token. So the person is identified by the email Open WebUI forwards, mapped
  to a GitLab account, and their access is read from membership.
* **Explaining a refusal.** When a question matches only repositories the
  person cannot read, say which ones and who maintains them, so they can ask
  for access instead of concluding the code does not exist.

No admin or sudo scope is involved, deliberately: operators will not hand an
indexer admin rights over their GitLab. A project's member list
(`/projects/:id/members/all`, inherited members included) is readable by any
account that can see the project -- which the service account already can,
since it indexes it. Membership at Reporter or above is exactly the rule the
personal-token path applies (`acl.MIN_ACCESS_LEVEL`), so both paths grant the
same repositories.

Mapping an email to a GitLab account without admin rights: a read-only token
cannot see private emails. The chat user's sign-in USERNAME (from Authelia's
users.yml) is looked up as a GitLab username first; GitLab's public-email
search is the fallback. In practice: give people the same username in the admin
panel as in GitLab.
"""
from __future__ import annotations

import logging
import threading
import time
from pathlib import Path
from typing import Callable

import httpx
import yaml

from . import credentials
from .acl import MIN_ACCESS_LEVEL, PER_PAGE, STALE_GRACE_SECONDS, TTL_SECONDS, AclDenied, Identity
from .config import GitLabConfig

log = logging.getLogger(__name__)

MAINTAINER_LEVEL = 40
_MAX_MEMBER_PAGES = 50


class GitLabUnavailable(Exception):
    """GitLab could not be asked, and nothing recent enough is cached."""


class MemberDirectory:
    """Project member lists and user lookups, cached, via the service credential.

    Shared across requests and threads: every cache access takes the lock.
    A failed refresh serves the previous answer for up to STALE_GRACE_SECONDS,
    the same outage tolerance the personal-token path has.
    """

    def __init__(self, cfg: GitLabConfig, *, client: httpx.Client | None = None,
                 ttl: float = TTL_SECONDS, now: Callable[[], float] = time.time):
        self.cfg = cfg
        self._client = client
        self.ttl = ttl
        self.now = now
        self._lock = threading.Lock()
        self._members: dict[int, tuple[float, list[dict]]] = {}
        self._users: dict[str, tuple[float, dict | None]] = {}

    def _get(self, path: str, params: dict) -> httpx.Response:
        client = self._client or httpx.Client(timeout=15.0)
        try:
            return client.get(f"{self.cfg.url}/api/v4{path}", params=params,
                              headers=credentials.headers(self.cfg, client=client))
        finally:
            if self._client is None:
                client.close()

    def _cached(self, cache: dict, key, fetch):
        with self._lock:
            hit = cache.get(key)
        age = None if hit is None else self.now() - hit[0]
        if hit is not None and 0 <= age < self.ttl:
            return hit[1]
        try:
            value = fetch()
        except (httpx.HTTPError, ValueError, GitLabUnavailable) as exc:
            if hit is not None and age < STALE_GRACE_SECONDS:
                log.warning("GitLab is unwell (%s); serving a cached answer %.0fs old", exc, age)
                return hit[1]
            raise GitLabUnavailable(str(exc)) from exc
        with self._lock:
            cache[key] = (self.now(), value)
        return value

    def members(self, gitlab_id: int) -> list[dict]:
        """Everyone with access to a project, inherited memberships included."""
        def fetch() -> list[dict]:
            out: list[dict] = []
            for page in range(1, _MAX_MEMBER_PAGES + 1):
                resp = self._get(f"/projects/{gitlab_id}/members/all",
                                 {"per_page": PER_PAGE, "page": page})
                if resp.status_code in (403, 404):
                    return []          # the service account cannot see it: grant nothing
                if resp.status_code >= 500:
                    raise GitLabUnavailable(f"GitLab returned {resp.status_code} for project {gitlab_id} members")
                if resp.status_code != 200:
                    return []
                batch = resp.json()
                if not batch:
                    break
                out.extend({"id": int(m["id"]), "username": m.get("username", ""),
                            "name": m.get("name", ""), "access_level": int(m.get("access_level", 0)),
                            "state": m.get("state", "active")} for m in batch)
                if len(batch) < PER_PAGE:
                    break
            return out
        return self._cached(self._members, int(gitlab_id), fetch)

    def user(self, *, username: str | None = None, email: str | None = None) -> dict | None:
        """A GitLab account by exact username, else by exact PUBLIC email."""
        def lookup(params: dict, match: Callable[[dict], bool]) -> dict | None:
            resp = self._get("/users", params)
            if resp.status_code >= 500:
                raise GitLabUnavailable(f"GitLab returned {resp.status_code} looking up a user")
            if resp.status_code != 200:
                return None
            hits = [u for u in resp.json() if match(u)]
            return hits[0] if len(hits) == 1 else None

        if username:
            name = username.strip().lower()
            found = self._cached(self._users, f"u:{name}", lambda: lookup(
                {"username": name}, lambda u: str(u.get("username", "")).lower() == name))
            if found:
                return found
        if email:
            addr = email.strip().lower()
            return self._cached(self._users, f"e:{addr}", lambda: lookup(
                {"search": addr}, lambda u: str(u.get("public_email") or "").lower() == addr))
        return None

    def maintainers(self, gitlab_id: int, limit: int = 5) -> list[str]:
        """"@username (Name)" for active members at Maintainer or Owner, best first."""
        people = [m for m in self.members(gitlab_id)
                  if m["access_level"] >= MAINTAINER_LEVEL and m["state"] == "active"]
        people.sort(key=lambda m: (-m["access_level"], m["name"].lower()))
        return [f"@{m['username']} ({m['name']})" if m["name"] else f"@{m['username']}"
                for m in people[:limit]]


def username_for_email(users_file: str | Path | None, email: str) -> str | None:
    """The sign-in username Authelia has for this email, if the file is readable."""
    if not users_file:
        return None
    try:
        data = yaml.safe_load(Path(users_file).read_text(encoding="utf-8")) or {}
    except (OSError, yaml.YAMLError):
        return None
    addr = email.strip().lower()
    for username, entry in (data.get("users") or {}).items():
        if isinstance(entry, dict) and str(entry.get("email", "")).strip().lower() == addr:
            return str(username)
    return None


def resolve_person(conn, directory: MemberDirectory, email: str, *,
                   users_file: str | Path | None = None) -> Identity:
    """The Identity of a chat user, from membership read with the service credential."""
    email = (email or "").strip()
    if not email:
        raise AclDenied("The chat client did not say who is asking, so access is denied.")
    username = username_for_email(users_file, email)
    try:
        user = directory.user(username=username, email=email)
    except GitLabUnavailable as exc:
        raise AclDenied("Cannot verify your GitLab access right now and no recent cached "
                        "permission exists, so access is denied. Retry shortly.") from exc
    if user is None:
        tried = f"GitLab username {username!r}" if username else "a GitLab username"
        raise AclDenied(
            f"No GitLab account matches {email} (looked for {tried}, then a public email). "
            "Ask an administrator to give your chat account the same username as your "
            "GitLab account, or set that email as public on your GitLab profile.")
    if user.get("state", "active") != "active":
        raise AclDenied(f"The GitLab account {user.get('username')} is not active.")

    uid = int(user["id"])
    rows = conn.execute("SELECT id, gitlab_id FROM repos").fetchall()
    by_project: dict[int, list[int]] = {}
    for r in rows:
        by_project.setdefault(int(r["gitlab_id"]), []).append(int(r["id"]))
    allowed: list[int] = []
    try:
        for gitlab_id, repo_ids in by_project.items():
            if any(m["id"] == uid and m["access_level"] >= MIN_ACCESS_LEVEL and m["state"] == "active"
                   for m in directory.members(gitlab_id)):
                allowed.extend(repo_ids)
    except GitLabUnavailable as exc:
        raise AclDenied("Cannot verify your GitLab access right now and no recent cached "
                        "permission exists, so access is denied. Retry shortly.") from exc
    return Identity(uid, str(user.get("username", "")), sorted(allowed))


def no_access_message(repos: list[tuple[str, int, int]], directory: MemberDirectory | None) -> str:
    """Explain matches the person cannot read: which repositories, and whom to ask.

    `repos` is (path_with_namespace, gitlab_id, match_count). Names repositories
    and maintainers only -- never a path, symbol or line from inside them.
    """
    lines = []
    for path, gitlab_id, count in repos:
        who = ""
        if directory is not None:
            try:
                people = directory.maintainers(gitlab_id)
                who = ("maintainers: " + ", ".join(people)) if people else "no maintainer listed"
            except GitLabUnavailable:
                who = "maintainers unavailable right now"
        noun = "match" if count == 1 else "matches"
        lines.append(f"- {path} ({count} {noun}){' -- ' + who if who else ''}")
    many = len(repos) != 1
    return ("Nothing you have access to matches this, but it does exist in "
            f"{len(repos)} {'repositories' if many else 'repository'} you cannot read:\n"
            + "\n".join(lines)
            + "\nTell the person asking that they do not have access, and that they can ask a "
              "maintainer listed above to add them in GitLab with at least Reporter access. "
              "Argus picks the change up within 10 minutes.")
