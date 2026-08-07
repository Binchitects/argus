from __future__ import annotations

import fnmatch
import os
from collections.abc import Sequence
from urllib.parse import quote
import stat
import subprocess
import sys
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


# --------------------------------------------------------------- credentials
#
# ensure_mirror is the only place a clone/fetch touches a real remote, and
# therefore the only place a GitLab token may need to reach `git`. It must
# never end up in three places:
#
#   1. a GitError message. worker.py and cli.py persist str(exc) straight
#      into the index_errors table, which the MCP server serves queries
#      from -- so redaction happens here, at the one point every such
#      message is built, not at either call site.
#   2. .git/config. The mirror is long-lived, so a credentialed remote URL
#      would keep the token on disk for the mirror's entire life.
#   3. a command-line argument. argv is world-readable via `ps` / a Linux
#      `/proc/<pid>/cmdline` -- the deployment target for this project.
#
# GIT_ASKPASS closes all three at once: git invokes an external helper
# program for the username/password, passing only a human-readable prompt
# string ("Username for '...'"/"Password for '...'") as its one CLI
# argument. The helper answers from its OWN environment (ARGUS_TOKEN_ENV,
# set on the child process only), never from argv -- so the token never
# touches a command line, and clone_url stays the plain http(s) URL
# throughout, so there is nothing credentialed to strip out of
# remote.origin.url afterward. Residual exposure: this does not, by itself,
# hide the *username* placeholder or the fact that a clone/fetch ran with
# GIT_ASKPASS set -- only the secret value itself never reaches argv or disk.
ARGUS_TOKEN_ENV = "ARGUS_GIT_ASKPASS_TOKEN"
_ASKPASS_USERNAME = "oauth2"

_ASKPASS_SOURCE = '''#!/usr/bin/env python3
"""GIT_ASKPASS helper for Argus. Not meant to be run by hand.

git invokes this as a subprocess whenever a clone/fetch needs credentials,
passing the prompt text ("Username for '...'" or "Password for '...'") as
sys.argv[1] and reading the answer from stdout. The token itself travels
only through the {env} environment variable, set by the parent Argus
process on this helper's environment -- never as a command-line argument,
never written to a file, never printed anywhere else by this script.
"""
import os
import sys


def main() -> int:
    prompt = sys.argv[1] if len(sys.argv) > 1 else ""
    if prompt.strip().lower().startswith("username"):
        print("{username}")
    else:
        print(os.environ.get("{env}", ""))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
'''.format(env=ARGUS_TOKEN_ENV, username=_ASKPASS_USERNAME)


def _askpass_program(askpass_dir: Path) -> Path:
    """Materialize the GIT_ASKPASS helper; return the one path to hand git.

    GIT_ASKPASS is executed directly, not through a shell, so its value must
    be exactly one runnable file -- "python /path/script.py" fails with
    "cannot spawn ... No such file or directory" because git treats the
    whole string as a single (nonexistent) filename. On POSIX the script
    itself, marked executable with a shebang, IS that one file. Windows has
    no direct-exec mechanism for a bare .py file, so a tiny generated .cmd
    wrapper naming this process's own interpreter takes its place there.
    """
    askpass_dir.mkdir(parents=True, exist_ok=True)
    py_path = askpass_dir / "git_askpass.py"
    py_path.write_text(_ASKPASS_SOURCE, encoding="utf-8")
    if os.name == "nt":
        cmd_path = askpass_dir / "git_askpass.cmd"
        cmd_path.write_text(
            f'@echo off\r\n"{sys.executable}" "{py_path}" %*\r\n',
            encoding="utf-8",
        )
        return cmd_path
    py_path.chmod(py_path.stat().st_mode | stat.S_IXUSR | stat.S_IXGRP | stat.S_IXOTH)
    return py_path


def _auth_env(index_cfg: IndexConfig, token: str | None) -> dict[str, str] | None:
    """Build the subprocess environment for a credentialed git operation.

    Returns None (subprocess.run then inherits the current environment
    unchanged) when no token is given -- the existing behaviour every
    local-path test in tests/test_mirror.py depends on.
    """
    if not token:
        return None
    env = dict(os.environ)
    env["GIT_ASKPASS"] = str(_askpass_program(index_cfg.data_dir / ".askpass"))
    env[ARGUS_TOKEN_ENV] = token
    return env


def _redact(text: str, secrets: tuple[str, ...]) -> str:
    for secret in secrets:
        if secret:
            text = text.replace(secret, "***")
    return text


def _git(cwd: Path, *args: str, env: dict[str, str] | None = None,
        secrets: tuple[str, ...] = ()) -> str:
    proc = subprocess.run(
        ["git", *args], cwd=cwd, capture_output=True,
        text=True, encoding="utf-8", errors="replace", env=env,
    )
    if proc.returncode != 0:
        # Scrub before the GitError message is built -- this is the only
        # place one is constructed, and both worker.py and cli.py persist
        # str(exc) straight into index_errors. Applied to both the command
        # echo and stderr: defense in depth, since nothing about this
        # design puts the token in either, but a leak anywhere upstream
        # (a proxy, a future call site) must not survive past this line.
        cmd = _redact(" ".join(args), secrets)
        stderr = _redact(proc.stderr.strip()[:500], secrets)
        raise GitError(f"git {cmd} failed: {stderr}")
    return proc.stdout


def mirror_path(index_cfg: IndexConfig, gitlab_id: int) -> Path:
    return index_cfg.mirrors_dir / f"{gitlab_id}.git"


def tree_path(index_cfg: IndexConfig, gitlab_id: int,
              branch: str | None = None) -> Path:
    """Worktree for one project at one branch.

    Keyed by project alone before a repo could be indexed at several refs --
    which meant two branches of the same project would check out over each
    other, and whichever ran last would silently define the contents of both.

    Branch names may contain "/" (`release/v1`) and on Windows also ":" and
    other characters a path cannot hold, so the branch component is encoded
    rather than used raw. `branch=None` keeps the old bare path so existing
    single-branch trees are reused instead of re-created.
    """
    if branch is None:
        return index_cfg.trees_dir / str(gitlab_id)
    return index_cfg.trees_dir / str(gitlab_id) / _branch_dir(branch)


def _branch_dir(branch: str) -> str:
    """A filesystem-safe, collision-free directory name for a branch.

    Percent-encoding rather than replacing separators with "-": `release/v1`
    and `release-v1` are different branches and must not land in the same
    directory, and a lossy mapping would make one silently shadow the other.
    """
    return quote(branch, safe="")


def ensure_mirror(index_cfg: IndexConfig, project: Project, *,
                  clone_url: str, token: str | None = None) -> Path:
    env = _auth_env(index_cfg, token)
    secrets = (token,) if token else ()
    # credential.helper is cleared for this invocation only -- a `-c`, never
    # written to any config file -- so a configured system/global helper
    # (Git Credential Manager on Windows, libsecret on Linux) cannot
    # intercept the prompt ahead of GIT_ASKPASS. Without this, GCM in
    # particular answers (or refuses) on git's behalf before our helper is
    # ever consulted, and refuses an HTTP GitLab remote outright.
    auth_args = ("-c", "credential.helper=") if token else ()
    path = mirror_path(index_cfg, project.gitlab_id)
    if path.exists():
        _git(path, *auth_args, "fetch", "--prune", "--quiet", "origin",
             "+refs/heads/*:refs/heads/*", env=env, secrets=secrets)
        return path
    path.parent.mkdir(parents=True, exist_ok=True)
    _git(path.parent, *auth_args, "clone", "--mirror", "--quiet", clone_url,
        str(path), env=env, secrets=secrets)
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
                  mirror: Path, sha: str, branch: str | None = None) -> Path:
    tree = tree_path(index_cfg, gitlab_id, branch)
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


def list_branches(mirror: Path) -> list[str]:
    """Every branch in the mirror, in git's own order.

    The mirror already carries them all: ensure_mirror clones with --mirror
    and fetches "+refs/heads/*:refs/heads/*", so selecting a branch to index
    needs no extra network round trip.
    """
    out = _git(mirror, "for-each-ref", "--format=%(refname:short)", "refs/heads/")
    return [line.strip() for line in out.splitlines() if line.strip()]


def select_branches(available: Sequence[str], patterns: Sequence[str],
                    default_branch: str) -> list[str]:
    """Branches to index: the default, plus anything matching `patterns`.

    The default branch is included unconditionally. It is what an unqualified
    question is answered from, so a pattern list that happens not to match it
    -- `["v*"]` against a default of `main` -- would leave the default empty
    while every release branch was indexed, and no error would be raised.

    Order is deterministic (default first, then the rest as git listed them)
    so that an indexing run is reproducible.
    """
    chosen = [default_branch] if default_branch in available else []
    for branch in available:
        if branch in chosen:
            continue
        if any(fnmatch.fnmatchcase(branch, pattern) for pattern in patterns):
            chosen.append(branch)
    return chosen
