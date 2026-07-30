from __future__ import annotations

import argparse
import shutil
import subprocess
import sys
import time
from pathlib import Path

from .config import Config, ConfigError
from .gitlab import GitLabError, list_projects
from .mirror import GitError, ensure_mirror, head_sha, sync_worktree
from .store import queries, writes
from .store.db import open_db
from .worker import index_repo


def preflight() -> str | None:
    """Return an error message if the environment cannot index, else None."""
    exe = shutil.which("ctags")
    if exe is None:
        return (
            "ctags not found on PATH. Install Universal Ctags:\n"
            "  Linux:   sudo apt install universal-ctags\n"
            "  Windows: winget install UniversalCtags.Ctags"
        )
    try:
        out = subprocess.run(
            [exe, "--version"], capture_output=True, text=True, timeout=10
        ).stdout
    except (OSError, subprocess.SubprocessError) as exc:
        return f"could not run ctags --version: {exc}"
    if "Universal Ctags" not in out:
        return (
            f"{exe} is not Universal Ctags (reported: {out.splitlines()[0] if out else '?'}).\n"
            "Exuberant Ctags has no --output-format=json and cannot be used."
        )
    return None


def _index(cfg: Config, only: str | None, reset_retries: bool = False) -> int:
    problem = preflight()
    if problem:
        print(problem, file=sys.stderr)
        return 4

    conn = open_db(cfg.index.db_path)

    projects = list_projects(cfg.gitlab)
    if only:
        projects = [p for p in projects if p.path_with_namespace == only]

    if reset_retries:
        # Explicit operator escape hatch: an automatic clear only fires once
        # a path indexes successfully again, which requires the underlying
        # cause (ACL, path length, AV quarantine) to already be fixed. This
        # lets an operator forget the history immediately instead of waiting
        # for that to happen on its own.
        if only:
            if not projects:
                # --repo was given but matched no known repo; don't clear anything
                print(f"repo '{only}' not found in projects from GitLab")
            else:
                cursor = conn.execute(
                    "DELETE FROM retry_attempts WHERE repo_id IN"
                    " (SELECT id FROM repos WHERE path_with_namespace = ?)",
                    (only,),
                )
                conn.commit()
                rows_cleared = cursor.rowcount
                print(f"reset retry counters for '{only}' ({rows_cleared} rows)")
        else:
            cursor = conn.execute("DELETE FROM retry_attempts")
            conn.commit()
            rows_cleared = cursor.rowcount
            if rows_cleared > 0:
                print(f"reset {rows_cleared} retry counter entries")
            else:
                print("no retry counters to reset")

    if not projects:
        print("no repos matched")
        return 0

    any_repo_unhealthy = False
    for project in projects:
        repo_id = writes.upsert_repo(
            conn, gitlab_id=project.gitlab_id,
            path_with_namespace=project.path_with_namespace,
            default_branch=project.default_branch, http_url=project.http_url,
        )
        old = conn.execute(
            "SELECT last_indexed_sha FROM repos WHERE id = ?", (repo_id,)
        ).fetchone()["last_indexed_sha"]

        started = time.time()
        try:
            mirror_dir = ensure_mirror(cfg.index, project,
                                       clone_url=project.http_url)
            sha = head_sha(mirror_dir, project.default_branch)
            if sha == old:
                # index_repo is the only other writer of last-run state, and
                # this path never calls it. Without this, a repo polled every
                # hour for six months and correctly up to date every time
                # reported a six-month-old last_run_at -- indistinguishable
                # from one nothing has looked at since. Clearing the flags is
                # right here: a previous pass that timed out or lost ctags
                # held the SHA, so it could not have reached this branch.
                writes.record_run_state(conn, repo_id, timed_out=False,
                                        symbols_failed=False, ts=int(time.time()))
                print(f"{project.path_with_namespace}: up to date")
                continue
            tree = sync_worktree(cfg.index, project.gitlab_id, mirror_dir, sha)
            result = index_repo(conn, cfg.index, project, mirror_dir, tree, sha, old)
        except GitError as exc:
            any_repo_unhealthy = True
            writes.record_error(conn, repo_id, None, "git", str(exc), int(time.time()))
            # Record the failure rather than leaving the PREVIOUS pass's flags
            # and timestamp standing: a repo whose fetch has failed every run
            # for weeks otherwise showed as clean and freshly checked.
            writes.record_run_state(conn, repo_id, timed_out=False,
                                    symbols_failed=False, ts=int(time.time()),
                                    error=str(exc))
            print(f"{project.path_with_namespace}: FAILED ({exc})", file=sys.stderr)
            continue
        except Exception as exc:   # noqa: BLE001 - one bad repo must not end the run
            # Nothing caught a non-GitError escaping index_repo, so it aborted
            # the whole run: every repo after this one went unindexed, and it
            # happened after the retry queue had been read and before any run
            # state was recorded. Contain it to this repo and leave a record.
            any_repo_unhealthy = True
            writes.record_error(conn, repo_id, None, "index", repr(exc),
                                int(time.time()))
            writes.record_run_state(conn, repo_id, timed_out=False,
                                    symbols_failed=False, ts=int(time.time()),
                                    error=repr(exc))
            print(f"{project.path_with_namespace}: FAILED ({exc!r})", file=sys.stderr)
            continue

        if result.timed_out or result.symbols_failed:
            any_repo_unhealthy = True

        flags = ""
        if result.timed_out:
            flags += " TIMED-OUT"
        if result.symbols_failed:
            flags += " SYMBOLS-FAILED"
        print(
            f"{project.path_with_namespace}: indexed={result.indexed} "
            f"deleted={result.deleted} skipped={result.skipped} "
            f"errors={result.errors}{flags} "
            f"({time.time() - started:.1f}s)"
        )
    # Exit codes 2/3/4 are already claimed (config, gitlab, preflight); use a
    # distinct code so a cron job can tell "ran, but a repo is unhealthy"
    # apart from those startup failures.
    return 1 if any_repo_unhealthy else 0


def _status(cfg: Config) -> int:
    conn = open_db(cfg.index.db_path)
    # Operator tool: pass the full known set explicitly rather than bypassing
    # the allowlist parameter. The ACL module arrives in Phase 2.
    all_ids = [r["id"] for r in conn.execute("SELECT id FROM repos")]
    rows = queries.index_status(all_ids, conn)
    if not rows:
        print("no repos indexed")
        return 0
    for row in rows:
        when = (
            time.strftime("%Y-%m-%d %H:%M", time.localtime(row["last_indexed_at"]))
            if row["last_indexed_at"] else "never"
        )
        sha = (row["last_indexed_sha"] or "-")[:8]
        flags = ""
        if row["last_run_timed_out"]:
            flags += " TIMED-OUT"
        if row["last_run_symbols_failed"]:
            flags += " SYMBOLS-FAILED"
        if row["last_run_error"]:
            flags += f" RUN-FAILED({row['last_run_error'][:80]})"
        print(
            f"{row['path_with_namespace']:<40} sha={sha} at={when} "
            f"files={row['files']} symbols={row['symbols']} errors={row['errors']} "
            f"queued_retries={row['queued_retries']}{flags}"
        )
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="argus")
    sub = parser.add_subparsers(dest="command", required=True)

    p_index = sub.add_parser("index", help="Mirror and index repositories")
    p_index.add_argument("--config", required=True, type=Path)
    p_index.add_argument("--repo", help="Index only this path_with_namespace")
    p_index.add_argument("--reset-retries", action="store_true",
                         help="Clear retry counters before indexing (manual recovery only; do not use on a schedule)")

    p_status = sub.add_parser("status", help="Show per-repo index freshness")
    p_status.add_argument("--config", required=True, type=Path)

    args = parser.parse_args(argv)

    try:
        cfg = Config.load(args.config)
    except (ConfigError, OSError) as exc:
        print(f"config error: {exc}", file=sys.stderr)
        return 2

    try:
        if args.command == "index":
            return _index(cfg, args.repo, args.reset_retries)
        return _status(cfg)
    except GitLabError as exc:
        print(f"gitlab error: {exc}", file=sys.stderr)
        return 3


if __name__ == "__main__":
    raise SystemExit(main())
