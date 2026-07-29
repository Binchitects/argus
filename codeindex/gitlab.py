from __future__ import annotations

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
            batch = response.json()
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
