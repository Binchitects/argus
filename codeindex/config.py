from __future__ import annotations

import os
from dataclasses import dataclass
from pathlib import Path

import yaml

DEFAULT_EXCLUDE_DIRS = (
    "third_party", "vendor", "node_modules",
    "build", "out", "x64", "Debug", "Release",
)


class ConfigError(Exception):
    """Raised when configuration is missing or malformed."""


@dataclass(frozen=True)
class GitLabConfig:
    url: str
    token: str


@dataclass(frozen=True)
class IndexConfig:
    data_dir: Path
    db_path: Path
    max_file_bytes: int = 1048576
    exclude_dirs: tuple[str, ...] = DEFAULT_EXCLUDE_DIRS
    repo_time_budget_seconds: int = 600

    @property
    def mirrors_dir(self) -> Path:
        return self.data_dir / "mirrors"

    @property
    def trees_dir(self) -> Path:
        return self.data_dir / "trees"


@dataclass(frozen=True)
class Config:
    gitlab: GitLabConfig
    index: IndexConfig

    @staticmethod
    def load(path: Path) -> "Config":
        raw = yaml.safe_load(Path(path).read_text()) or {}
        gl = raw.get("gitlab") or {}
        ix = raw.get("index") or {}

        url = gl.get("url")
        if not url:
            raise ConfigError("gitlab.url is required")

        token = os.environ.get("CODEINDEX_GITLAB_TOKEN") or gl.get("token")
        if not token:
            raise ConfigError(
                "gitlab.token is required (set it in config or CODEINDEX_GITLAB_TOKEN)"
            )

        for key in ("data_dir", "db_path"):
            if not ix.get(key):
                raise ConfigError(f"index.{key} is required")

        return Config(
            gitlab=GitLabConfig(url=url.rstrip("/"), token=token),
            index=IndexConfig(
                data_dir=Path(ix["data_dir"]),
                db_path=Path(ix["db_path"]),
                max_file_bytes=int(ix.get("max_file_bytes", 1048576)),
                exclude_dirs=tuple(ix.get("exclude_dirs", DEFAULT_EXCLUDE_DIRS)),
                repo_time_budget_seconds=int(ix.get("repo_time_budget_seconds", 600)),
            ),
        )
