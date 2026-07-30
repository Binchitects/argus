from __future__ import annotations

import subprocess
from dataclasses import dataclass
from pathlib import Path

from .config import IndexConfig
from .gitlab import Project


class GitError(RuntimeError):
    """A git command failed."""


@dataclass(frozen=True)
class Change:
    status: str  # "A" | "M" | "D"
    path: str


def _git(cwd: Path, *args: str) -> str:
    proc = subprocess.run(
        ["git", *args], cwd=cwd, capture_output=True,
        text=True, encoding="utf-8", errors="replace",
    )
    if proc.returncode != 0:
        raise GitError(f"git {' '.join(args)} failed: {proc.stderr.strip()[:500]}")
    return proc.stdout


def mirror_path(index_cfg: IndexConfig, gitlab_id: int) -> Path:
    return index_cfg.mirrors_dir / f"{gitlab_id}.git"


def tree_path(index_cfg: IndexConfig, gitlab_id: int) -> Path:
    return index_cfg.trees_dir / str(gitlab_id)


def ensure_mirror(index_cfg: IndexConfig, project: Project, *,
                  clone_url: str) -> Path:
    path = mirror_path(index_cfg, project.gitlab_id)
    if path.exists():
        _git(path, "fetch", "--prune", "--quiet", "origin",
             "+refs/heads/*:refs/heads/*")
        return path
    path.parent.mkdir(parents=True, exist_ok=True)
    _git(path.parent, "clone", "--mirror", "--quiet", clone_url, str(path))
    return path


def head_sha(mirror: Path, branch: str) -> str:
    return _git(mirror, "rev-parse", branch).strip()


def is_ancestor(mirror: Path, old: str, new: str) -> bool:
    proc = subprocess.run(
        ["git", "merge-base", "--is-ancestor", old, new],
        cwd=mirror, capture_output=True, text=True,
    )
    if proc.returncode == 0:
        return True
    if proc.returncode == 1:
        # Exit 1 is git's documented "no, not an ancestor" — a legitimate
        # answer, not a failure.
        return False
    # Anything else (128, etc.) means an invalid or corrupt sha, not a
    # negative answer; conflating the two would silently treat a broken
    # ref as "force-pushed, do a full reindex" instead of surfacing it.
    raise GitError(
        f"git merge-base --is-ancestor failed: {proc.stderr.strip()[:500]}"
    )


def commit_exists(mirror: Path, sha: str) -> bool:
    """True if `sha` resolves to a commit object present in this mirror.

    An absent old commit is routine, not corruption: an operator can delete
    data_dir/mirrors to reclaim disk while index.db survives (ensure_mirror
    then re-clones fresh), `git gc` prunes commits orphaned by a force-push
    (bare mirrors keep no reflog, and gc.pruneExpire defaults to 2 weeks),
    and a repo can be deleted and re-created in GitLab. Probing first keeps
    is_ancestor's exit-128 GitError meaningful for genuinely unexpected git
    failures instead of making a missing old commit a permanent repo failure.
    """
    proc = subprocess.run(
        ["git", "cat-file", "-e", f"{sha}^{{commit}}"],
        cwd=mirror, capture_output=True, text=True,
    )
    return proc.returncode == 0


def _full_listing(mirror: Path, sha: str) -> list[Change]:
    out = _git(mirror, "ls-tree", "-r", "--name-only", "-z", sha)
    return [Change(status="A", path=p) for p in out.split("\0") if p]


def changes_since(mirror: Path, old_sha: str | None,
                  new_sha: str) -> tuple[bool, list[Change]]:
    """Return (full_reindex, changes) in a single ancestry resolution.

    Callers need both answers, and resolving them separately meant running
    `merge-base --is-ancestor` twice per repo with a real risk of the two
    call sites disagreeing about what an unresolvable old sha means.
    """
    if (old_sha is None
            or not commit_exists(mirror, old_sha)
            or not is_ancestor(mirror, old_sha, new_sha)):
        # First index, history was rewritten, or the old commit is simply
        # gone: reindex the whole tree. is_ancestor is still free to raise
        # for an unexpected failure on the new_sha side.
        return True, _full_listing(mirror, new_sha)
    return False, _diff_changes(mirror, old_sha, new_sha)


def changed_files(mirror: Path, old_sha: str | None, new_sha: str) -> list[Change]:
    return changes_since(mirror, old_sha, new_sha)[1]


def _diff_changes(mirror: Path, old_sha: str, new_sha: str) -> list[Change]:
    out = _git(mirror, "diff", "--name-status", "--no-renames", "-z",
               f"{old_sha}..{new_sha}")
    # With -z and --no-renames, records are NUL-separated flat fields:
    # <status>\0<path>\0<status>\0<path>\0... (no trailing-empty rename
    # triples, since renames are disabled).
    fields = [f for f in out.split("\0") if f]
    changes: list[Change] = []
    for status, path in zip(fields[0::2], fields[1::2]):
        status = status[0]
        if status == "T":
            # Typechange (e.g. file<->symlink): real in C/C++ trees, and
            # the content still needs to be re-read and re-stored.
            status = "M"
        if status in ("A", "M", "D") and path:
            changes.append(Change(status=status, path=path))
    return changes


def sync_worktree(index_cfg: IndexConfig, gitlab_id: int,
                  mirror: Path, sha: str) -> Path:
    tree = tree_path(index_cfg, gitlab_id)
    if tree.exists():
        _git(tree, "checkout", "--force", "--detach", sha)
    else:
        tree.parent.mkdir(parents=True, exist_ok=True)
        _git(mirror, "worktree", "add", "--force", "--detach", str(tree), sha)
    return tree


def blob_shas(mirror: Path, sha: str) -> dict[str, str]:
    """Map every path in the tree to its blob sha, in one git call."""
    out = _git(mirror, "ls-tree", "-r", "-z", sha)
    result: dict[str, str] = {}
    for record in out.split("\0"):
        if not record:
            continue
        meta, _, path = record.partition("\t")
        parts = meta.split()
        if len(parts) >= 3 and path:
            result[path] = parts[2]
    return result
