from __future__ import annotations

import json
from dataclasses import dataclass

import httpx

from .config import GitLabConfig

PER_PAGE = 100
MAX_PAGES = 1000


class GitLabError(RuntimeError):
    """GitLab returned an error or unusable response."""


@dataclass(frozen=True)
class Project:
    gitlab_id: int
    path_with_namespace: str
    default_branch: str
    http_url: str


def list_projects(cfg: GitLabConfig, *,
                  client: httpx.Client | None = None) -> list[Project]:
    owns_client = client is None
    client = client or httpx.Client(timeout=30.0)
    projects: list[Project] = []
    try:
        for page in range(1, MAX_PAGES + 1):
            response = client.get(
                f"{cfg.url}/api/v4/projects",
                params={
                    "membership": "false", "simple": "true",
                    "archived": "false", "per_page": PER_PAGE, "page": page,
                },
                headers={"PRIVATE-TOKEN": cfg.token},
            )
            if response.status_code != 200:
                raise GitLabError(
                    f"GET /projects returned {response.status_code}: "
                    f"{response.text[:200]}"
                )
            try:
                batch = response.json()
            except json.JSONDecodeError:
                raise GitLabError(
                    f"GET /projects page {page}: failed to decode JSON: "
                    f"{response.text[:200]}"
                )
            if not batch:
                break
            for item in batch:
                if not item.get("default_branch"):
                    continue  # empty repo, nothing to index
                projects.append(Project(
                    gitlab_id=int(item["id"]),
                    path_with_namespace=item["path_with_namespace"],
                    default_branch=item["default_branch"],
                    http_url=item["http_url_to_repo"],
                ))
    finally:
        if owns_client:
            client.close()
    return projects


@dataclass(frozen=True)
class EnumerationHealth:
    """Whether the service token can actually enumerate every repository.

    `list_projects` uses `membership=false`, which returns every project the
    token may *see*. For an admin that is all of them. For a non-admin it is
    only the **public** ones -- their own private memberships are excluded,
    because `membership=false` means "do not filter to my memberships", not
    "include things I could reach".

    The failure is silent and total: indexing succeeds, reports no errors, and
    covers a fraction of the estate. Every answer afterwards is confidently
    incomplete, and nothing downstream can tell.

    `member_count` is the evidence. If a token can reach projects through
    membership that `membership=false` did not return, the enumeration is
    provably missing repositories -- which is stronger than trusting the
    `is_admin` flag alone.
    """

    is_admin: bool
    visible_count: int
    member_count: int

    @property
    def ok(self) -> bool:
        return self.is_admin or self.member_count <= self.visible_count

    @property
    def problem(self) -> str | None:
        if self.ok:
            return None
        missing = self.member_count - self.visible_count
        return (
            f"The service token is not a GitLab admin, and enumeration is "
            f"missing repositories: `membership=false` returned "
            f"{self.visible_count} project(s), but the token is a member of "
            f"{self.member_count}. At least {missing} repository(ies) would be "
            f"silently absent from the index, and every answer drawn from it "
            f"would be confidently incomplete.\n"
            f"Use a token with admin rights, or add the service account to "
            f"every group you want indexed."
        )


def enumeration_health(cfg: GitLabConfig, *,
                       client: httpx.Client | None = None) -> EnumerationHealth:
    """Probe whether `list_projects` can see the whole estate."""
    owns_client = client is None
    client = client or httpx.Client(timeout=30.0)
    headers = {"PRIVATE-TOKEN": cfg.token}
    try:
        user = client.get(f"{cfg.url}/api/v4/user", headers=headers)
        if user.status_code != 200:
            raise GitLabError(
                f"GET /user returned {user.status_code}: {user.text[:200]}")
        try:
            is_admin = bool(user.json().get("is_admin", False))
        except json.JSONDecodeError as exc:
            raise GitLabError(f"GET /user: failed to decode JSON: "
                              f"{user.text[:200]}") from exc

        def count(membership: str) -> int:
            total = 0
            for page in range(1, MAX_PAGES + 1):
                response = client.get(
                    f"{cfg.url}/api/v4/projects", headers=headers,
                    params={"membership": membership, "simple": "true",
                            "archived": "false", "per_page": PER_PAGE,
                            "page": page},
                )
                if response.status_code != 200:
                    raise GitLabError(
                        f"GET /projects returned {response.status_code}: "
                        f"{response.text[:200]}")
                batch = response.json()
                if not batch:
                    break
                total += len(batch)
            return total

        return EnumerationHealth(is_admin=is_admin,
                                 visible_count=count("false"),
                                 member_count=count("true"))
    finally:
        if owns_client:
            client.close()
