from __future__ import annotations

import argparse
from dataclasses import replace
import json
import os
import shutil
import sqlite3
import subprocess
import sys
import time
from pathlib import Path

import httpx

from . import auditlog, credentials
from .config import Config, ConfigError
from .embed import EmbeddingUnavailable
from .gitlab import GitLabError, enumeration_health, list_projects
from .kpi import LOWER_IS_BETTER, collect as collect_kpis
from .mcpsrv import DEFAULT_ALLOWED_HOSTS, create_app
from .mirror import (GitError, ensure_mirror, head_sha, list_branches,
                     select_branches, sync_worktree)
from .packs import format as pack_format
from .packs import registry
from .packs.build import BuildError, build_pack, fetch_source
from .packs.registry import RegistryError
from .packs.sources import SOURCES
from .resolve import resolve_includes
from .store import packs as store_packs
from .store import queries, writes
from .store.db import open_db
from .store.graph import rebuild_repo_deps
from .worker import index_repo

DEFAULT_SERVE_HOST = "127.0.0.1"
DEFAULT_SERVE_PORT = 7700

#: Exit code for pack build/install/registry failures. Distinct from the
#: existing 2 (config), 3 (gitlab) and 4 (indexing) so a script can tell a
#: pack problem from an index one.
EXIT_PACK = 5

#: `argus verify` exit codes, chosen to line up with the hook protocol every
#: agent client already speaks rather than invented here.
#:
#: A `Stop` hook in Claude Code (and the equivalent elsewhere) blocks the model
#: from finishing by exiting 2, and the stderr it printed is handed back as the
#: reason. So 2 has to mean "the draft is wrong" and nothing else.
EXIT_VERIFY_CONTRADICTED = 2
#: Could not check -- no packs installed, packs unreadable, bad config.
#:
#: DELIBERATELY NOT 2. A mandatory verifier that cannot verify must not block
#: every answer, or a deployment without documentation packs becomes an agent
#: that can never finish a sentence. "I could not check" and "I checked and you
#: are wrong" are different answers and the hook has to be able to tell them
#: apart -- which is the whole reason these are separate codes rather than a
#: boolean.
EXIT_VERIFY_UNAVAILABLE = 6


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


def _index_branch(conn, cfg: Config, project, branch: str,
                  mirror_dir) -> tuple[bool, str]:
    """Index one project at one branch. Returns (unhealthy, outcome).

    `outcome` is what the audit stream records and what a dashboard groups by;
    `unhealthy` is what the run's exit code is built from. They are not the
    same thing -- `up_to_date` is a perfectly healthy outcome that means no
    work was done, and separating it is what lets an operator tell "nothing
    changed" from "it is doing the work" without reading the log text.

    The branch appears in the printed label only when it is not the default,
    so single-branch output is unchanged and a line naming a branch always
    means something other than trunk.
    """
    label = (project.path_with_namespace if branch == project.default_branch
             else f"{project.path_with_namespace}@{branch}")
    repo_id = writes.upsert_repo(
        conn, gitlab_id=project.gitlab_id,
        path_with_namespace=project.path_with_namespace,
        default_branch=project.default_branch, branch=branch,
        http_url=project.http_url,
    )
    old = conn.execute(
        "SELECT last_indexed_sha FROM repos WHERE id = ?", (repo_id,)
    ).fetchone()["last_indexed_sha"]

    started = time.time()
    try:
        sha = head_sha(mirror_dir, branch)
        if sha == old:
            # index_repo is the only other writer of last-run state, and this
            # path never calls it. Without this, a repo polled every hour for
            # six months and correctly up to date every time reported a
            # six-month-old last_run_at -- indistinguishable from one nothing
            # has looked at since.
            writes.record_run_state(conn, repo_id, timed_out=False,
                                    symbols_failed=False, ts=int(time.time()))
            print(f"{label}: up to date")
            auditlog.index_repo(repo=project.path_with_namespace, branch=branch,
                                outcome="up_to_date",
                                duration_ms=round((time.time() - started) * 1000, 1))
            return False, "up_to_date"
        tree = sync_worktree(cfg.index, project.gitlab_id, mirror_dir, sha, branch)
        result = index_repo(conn, cfg.index, project, mirror_dir, tree, sha, old,
                            repo_id=repo_id)
    except GitError as exc:
        writes.record_error(conn, repo_id, None, "git", str(exc), int(time.time()))
        # Record the failure rather than leaving the PREVIOUS pass's flags and
        # timestamp standing: a repo whose fetch has failed every run for
        # weeks otherwise showed as clean and freshly checked.
        writes.record_run_state(conn, repo_id, timed_out=False,
                                symbols_failed=False, ts=int(time.time()),
                                error=str(exc))
        print(f"{label}: FAILED ({exc})", file=sys.stderr)
        auditlog.index_repo(repo=project.path_with_namespace, branch=branch,
                            outcome="failed", error=str(exc),
                            duration_ms=round((time.time() - started) * 1000, 1))
        return True, "failed"
    except Exception as exc:   # noqa: BLE001 - one bad repo must not end the run
        # Nothing caught a non-GitError escaping index_repo, so it aborted the
        # whole run: every repo after this one went unindexed.
        writes.record_error(conn, repo_id, None, "index", repr(exc), int(time.time()))
        writes.record_run_state(conn, repo_id, timed_out=False,
                                symbols_failed=False, ts=int(time.time()),
                                error=repr(exc))
        print(f"{label}: FAILED ({exc!r})", file=sys.stderr)
        auditlog.index_repo(repo=project.path_with_namespace, branch=branch,
                            outcome="failed", error=repr(exc),
                            duration_ms=round((time.time() - started) * 1000, 1))
        return True, "failed"

    flags = ""
    if result.timed_out:
        flags += " TIMED-OUT"
    if result.symbols_failed:
        flags += " SYMBOLS-FAILED"
    print(f"{label}: indexed={result.indexed} deleted={result.deleted} "
          f"skipped={result.skipped} errors={result.errors}{flags} "
          f"({time.time() - started:.1f}s)")
    if result.timed_out:
        outcome = "timed_out"
    elif result.symbols_failed:
        outcome = "symbols_failed"
    else:
        outcome = "ok"
    auditlog.index_repo(
        repo=project.path_with_namespace, branch=branch, outcome=outcome,
        duration_ms=round((time.time() - started) * 1000, 1),
        indexed=result.indexed, deleted=result.deleted, skipped=result.skipped,
        errors=result.errors, timed_out=bool(result.timed_out),
        symbols_failed=bool(result.symbols_failed))
    return bool(result.timed_out or result.symbols_failed), outcome


def _prune_missing_branches(conn, project, keep) -> int:
    """Drop rows for branches this project no longer indexes.

    A release branch deleted upstream, or a default branch renamed, otherwise
    leaves a fully populated row behind forever -- and it keeps answering
    questions, because nothing about it looks stale: it has a real SHA and a
    real timestamp from the last run that did find it.

    The delete cascades to files, symbols and includes by foreign key.
    """
    marks = ",".join("?" for _ in keep) or "NULL"
    cur = conn.execute(
        f"DELETE FROM repos WHERE gitlab_id = ? AND branch NOT IN ({marks})",
        (project.gitlab_id, *keep),
    )
    conn.commit()
    if cur.rowcount:
        print(f"{project.path_with_namespace}: dropped {cur.rowcount} "
              f"branch(es) no longer indexed")
    return cur.rowcount


def _index(cfg: Config, only: str | None, reset_retries: bool = False,
           allow_partial: bool = False) -> int:
    started = time.time()

    def _give_up(code: int, reason: str) -> int:
        """Record the run and return its exit code.

        Every path out of this function goes through here or through the
        index_end at the bottom, so a pass that never reached a repository --
        unreachable GitLab, a refused enumeration, no ctags -- still appears in
        Loki. Those are the failures an operator most needs to chart, and
        without this they left no trace at all: exit 3, and nothing to say why.
        """
        auditlog.index_end(returncode=code,
                           duration_ms=round((time.time() - started) * 1000, 1),
                           repos=0, failed=0, up_to_date=0, reason=reason)
        return code

    # The service token must be able to see every repository, or the index is
    # silently partial and every answer drawn from it is confidently
    # incomplete. Checked before any work, because the failure produces no
    # errors of its own -- see gitlab.EnumerationHealth.
    if not allow_partial:
        try:
            health = enumeration_health(cfg.gitlab)
        except (GitLabError, httpx.HTTPError, credentials.CredentialError) as exc:
            # httpx.HTTPError and CredentialError, not just GitLabError: an
            # unreachable GitLab, a TLS rejection or a refused sign-in all
            # raise from the transport, and none of them is a GitLabError. They
            # used to escape as a raw httpx traceback with exit 1, which the
            # admin panel could only report as "exit 1" -- and which reads as
            # an Argus bug rather than "the container cannot reach GitLab",
            # the single most likely cause in a real deployment.
            print(f"could not verify GitLab enumeration: "
                  f"{type(exc).__name__}: {exc}", file=sys.stderr)
            return _give_up(3, "gitlab_unreachable")
        if not health.ok:
            print(health.problem, file=sys.stderr)
            print("\nRe-run with --allow-partial-enumeration to index anyway.",
                  file=sys.stderr)
            return _give_up(3, "enumeration_incomplete")

    problem = preflight()
    if problem:
        print(problem, file=sys.stderr)
        return _give_up(4, "preflight_failed")

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
        return _give_up(0, "no_repos_matched")

    auditlog.index_start(branches=list(cfg.index.branches),
                         allow_partial=allow_partial, repos=len(projects))
    run_started = time.time()

    any_repo_unhealthy = False
    failed_repos = 0
    up_to_date = 0
    empty_repos = 0
    for project in projects:
        # One mirror per project, however many branches come out of it: the
        # mirror already carries every ref (ensure_mirror fetches
        # "+refs/heads/*"), so indexing four branches costs one fetch.
        try:
            # Not cfg.gitlab.token: in password mode there is no token in the
            # config, and the credential is the OAuth token bought with the
            # password. `git_password` returns whichever mode is configured,
            # and git accepts both against the `oauth2` username.
            # gitlab_cfg carries the TLS policy for the clone itself. Without
            # it a self-signed GitLab enumerates projects fine and then fails
            # every clone, which reads as a credential or URL problem.
            mirror_dir = ensure_mirror(cfg.index, project,
                                       clone_url=project.http_url,
                                       token=credentials.git_password(cfg.gitlab),
                                       gitlab_cfg=cfg.gitlab)
            branches = select_branches(list_branches(mirror_dir),
                                       cfg.index.branches,
                                       project.default_branch)
        except GitError as exc:
            any_repo_unhealthy = True
            # No branch has been established yet, so the failure is recorded
            # against the default-branch row -- the one an unqualified
            # question is answered from, and so the one an operator needs to
            # see as unhealthy.
            repo_id = writes.upsert_repo(
                conn, gitlab_id=project.gitlab_id,
                path_with_namespace=project.path_with_namespace,
                default_branch=project.default_branch,
                branch=project.default_branch, http_url=project.http_url)
            writes.record_error(conn, repo_id, None, "git", str(exc),
                                int(time.time()))
            writes.record_run_state(conn, repo_id, timed_out=False,
                                    symbols_failed=False, ts=int(time.time()),
                                    error=str(exc))
            print(f"{project.path_with_namespace}: FAILED ({exc})", file=sys.stderr)
            failed_repos += 1
            auditlog.index_repo(repo=project.path_with_namespace,
                                branch=project.default_branch,
                                outcome="mirror_failed", error=str(exc))
            continue

        if not branches:
            # An enumerated project with no refs at all -- an empty repository.
            #
            # Not a failure: there is nothing wrong with it and nothing to
            # index. But not nothing, either, which is what it used to be.
            # This path emitted no row, no log line and no event, so a run
            # announced four repositories, the index held three, and nothing
            # anywhere said which one was missing or why. The overview tile
            # read "3/3 current" while the log said "repos: 4", and the only
            # way to reconcile them was to count mirrors by hand.
            #
            # Deliberately NOT given a `repos` row. A row is what
            # `metrics.snapshot` measures freshness against, and it counts
            # "never ran" as stale -- so a repository that can never be
            # indexed would raise ArgusIndexStale forever, which is how a real
            # alert becomes background noise. The event and the run summary
            # are where this belongs.
            print(f"{project.path_with_namespace}: no branches "
                  f"(empty repository) -- nothing to index")
            auditlog.index_repo(repo=project.path_with_namespace,
                                branch=project.default_branch,
                                outcome="no_branches")
            empty_repos += 1
            continue

        for branch in branches:
            unhealthy, outcome = _index_branch(conn, cfg, project, branch,
                                               mirror_dir)
            if unhealthy:
                any_repo_unhealthy = True
            if outcome == "failed":
                failed_repos += 1
            elif outcome == "up_to_date":
                up_to_date += 1

        _prune_missing_branches(conn, project, branches)

    # One pass over the whole database, after every repo. An include can point
    # into a repo indexed later in this same cycle, so resolving per repo would
    # make the graph depend on indexing order.
    try:
        counts = resolve_includes(conn)
        edges = rebuild_repo_deps(conn)
    except Exception as exc:  # noqa: BLE001 - must not escape as an uncaught traceback
        # Nothing else catches this: `main` handles only ConfigError and
        # GitLabError, so an uncaught error here -- most notably
        # sqlite3.IntegrityError from rebuild_repo_deps's FK on
        # repo_deps.to_repo_id, raised whenever an include still points at a
        # repo deleted since the last pass -- would discard the whole
        # per-repo run summary printed above and exit via a raw traceback.
        # That traceback carries no exit code of its own, so it cannot be
        # told apart from `return 1` below ("ran, but a repo is unhealthy")
        # by a caller checking $?. Report it the same way a per-repo failure
        # is reported and reuse exit code 4: this is a failure of the run
        # itself, the same category as a missing ctags binary, not a
        # per-repo health flag.
        print(f"resolve/rebuild failed: {exc!r}", file=sys.stderr)
        auditlog.index_end(returncode=4,
                           duration_ms=round((time.time() - run_started) * 1000, 1),
                           repos=len(projects), failed=failed_repos,
                           up_to_date=up_to_date)
        return 4
    print(f"includes: {counts.get('resolved', 0)} resolved, "
          f"{counts.get('external', 0)} external, "
          f"{counts.get('ambiguous', 0)} ambiguous, "
          f"{counts.get('not_found', 0)} not found")
    print(f"repo graph: {edges} cross-repo edges")
    # Only when it is non-zero, so an ordinary run does not carry arithmetic
    # nobody needs. When it IS non-zero it is the missing line: `index_start`
    # counts PROJECTS while the admin console's index tile counts REFS, so a
    # run that announced "repos: 4" next to a tile reading "3/3 current" had no
    # explanation anywhere in its own output.
    if empty_repos:
        print(f"repos: {len(projects)} seen, {empty_repos} empty (nothing to "
              f"index), {len(projects) - empty_repos} indexed")

    # Exit codes 2/3/4 are already claimed (config, gitlab, preflight/resolve);
    # use a distinct code so a cron job can tell "ran, but a repo is
    # unhealthy" apart from those startup/run failures.
    returncode = 1 if any_repo_unhealthy else 0
    auditlog.index_end(returncode=returncode,
                       duration_ms=round((time.time() - run_started) * 1000, 1),
                       repos=len(projects), failed=failed_repos,
                       up_to_date=up_to_date, empty=empty_repos)
    return returncode



def _index_repeatedly(cfg: Config, only: str | None, reset_retries: bool,
                      allow_partial: bool, interval: int,
                      sleep=time.sleep, max_passes: int | None = None) -> int:
    """Run indexing passes forever, `interval` seconds apart.

    The design specifies a GitLab push webhook with a periodic poll as its
    fallback, and notes the poll alone is sufficient until the webhook exists.
    This is that poll. Without it an index only advances when somebody
    remembers to run `argus index`, and `index_status` reports freshness
    faithfully while everyone reads stale answers.

    A failing pass does not end the loop. A repo that cannot be mirrored, a
    GitLab that is briefly down, a resolution error -- none should stop the
    next attempt fifteen minutes later, which is very likely to succeed. The
    exit code of each pass is reported so an operator watching logs can see a
    repeated failure, and the last one is returned if the loop ever ends.

    Interrupts exit cleanly rather than through a traceback, so `docker stop`
    and Ctrl-C both look like a normal shutdown.
    """
    last = 0
    passes = 0
    while max_passes is None or passes < max_passes:
        started = time.monotonic()
        try:
            last = _index(cfg, only, reset_retries, allow_partial=allow_partial)
        except (GitLabError, GitError, OSError) as exc:
            # Operational, not fatal: report and try again next interval.
            print(f"indexing pass failed: {exc}", file=sys.stderr)
            last = 4
        except KeyboardInterrupt:
            print("stopped", file=sys.stderr)
            return last
        passes += 1
        if max_passes is not None and passes >= max_passes:
            break
        elapsed = time.monotonic() - started
        print(f"pass finished with exit {last} in {elapsed:.1f}s; "
              f"next in {interval}s", flush=True)
        try:
            sleep(interval)
        except KeyboardInterrupt:
            print("stopped", file=sys.stderr)
            return last
    return last



def _backup(cfg: Config, out_dir: Path, config_path: Path | None = None) -> int:
    """Write a restorable snapshot to `out_dir`.

    What is worth backing up is a much smaller set than what is on disk, and
    the distinction is the whole procedure:

    * `index.db` -- the only file here that holds anything not reconstructible
      from GitLab. Most of it *is* reconstructible (files, symbols, includes,
      repo_deps are a cache of the estate), but the `audit` table is a record
      of what the assistant showed which developer, and no rebuild recovers
      it. That single table is why this command exists.
    * `config.yaml` -- small, and losing it means reconstructing operational
      decisions from memory.
    * Knowledge packs -- rebuildable, but only with Ollama, the source
      checkouts, and an hour. Copied if present.

    Deliberately NOT copied: `mirrors/` and `trees/`. They are bare clones and
    worktrees, they dominate the disk footprint, and every byte is re-fetchable
    from GitLab. Backing them up trades a large recurring cost for a shorter
    one-off restore, which is the wrong way round.

    The index is copied with `VACUUM INTO`, never a file copy. A plain copy of
    a live SQLite database can capture a torn page mid-transaction; VACUUM INTO
    takes a consistent point-in-time snapshot, verified here against a writer
    committing thousands of rows during the copy, and compacts it on the way
    out. The indexer does not need to be stopped.
    """
    out_dir = Path(out_dir)
    out_dir.mkdir(parents=True, exist_ok=True)
    index_out = out_dir / "index.db"

    if not Path(cfg.index.db_path).exists():
        print(f"no index at {cfg.index.db_path}", file=sys.stderr)
        return 4

    conn = sqlite3.connect(cfg.index.db_path)
    try:
        index_out.unlink(missing_ok=True)
        conn.execute("VACUUM INTO ?", (str(index_out),))
    except sqlite3.Error as exc:
        print(f"backup failed: {exc}", file=sys.stderr)
        return 4
    finally:
        conn.close()

    # The audit log lives in a sidecar (see store.db.audit_db_path), so a
    # backup of the index alone would silently omit the one table a reindex
    # cannot reconstruct -- and would still pass every check below, because
    # the index carries its own now-unused `audit` table from migration 007.
    from .store.db import audit_db_path

    audit_src = audit_db_path(cfg.index.db_path)
    audit_out = out_dir / audit_src.name
    audit_rows = 0
    if audit_src.exists():
        audit_conn = sqlite3.connect(audit_src)
        try:
            audit_out.unlink(missing_ok=True)
            audit_conn.execute("VACUUM INTO ?", (str(audit_out),))
        except sqlite3.Error as exc:
            print(f"audit backup failed: {exc}", file=sys.stderr)
            return 4
        finally:
            audit_conn.close()
        verify = sqlite3.connect(audit_out)
        try:
            audit_rows = verify.execute("SELECT COUNT(*) FROM audit").fetchone()[0]
        finally:
            verify.close()

    # A backup nobody has verified is a hope, not a backup.
    check = sqlite3.connect(index_out)
    try:
        status = check.execute("PRAGMA integrity_check").fetchone()[0]
        repo_rows = check.execute("SELECT COUNT(*) FROM repos").fetchone()[0]
    finally:
        check.close()
    if status != "ok":
        print(f"backup failed integrity check: {status}", file=sys.stderr)
        return 4

    # Config.load keeps no reference to the file it came from, so the path is
    # passed in rather than guessed. An earlier version used getattr(cfg,
    # "source_path", None), which silently never copied anything.
    copied_config = False
    if config_path and Path(config_path).is_file():
        shutil.copy2(config_path, out_dir / "config.yaml")
        copied_config = True

    packs_dir = cfg.packs_dir
    copied_packs = 0
    if Path(packs_dir).is_dir():
        target = out_dir / "packs"
        target.mkdir(exist_ok=True)
        for pack in Path(packs_dir).glob("*.arguspack"):
            shutil.copy2(pack, target / pack.name)
            copied_packs += 1

    size_mb = index_out.stat().st_size / (1024 * 1024)
    print(f"index.db      {size_mb:.1f} MB  ({repo_rows} repos, integrity ok)")
    print(f"audit rows    {audit_rows}  <- the only data no rebuild recovers")
    print(f"config.yaml   {'copied' if copied_config else 'NOT FOUND'}")
    print(f"packs         {copied_packs}")
    print(f"written to {out_dir}")
    print("mirrors/ and trees/ deliberately excluded: re-fetchable from GitLab")
    return 0



def _kpi(cfg: Config, as_json: bool) -> int:
    """Print the automatic health indicators for this index.

    `--json` exists so this can be appended to a file on a schedule and read
    as a time series. A KPI looked at once is a number; the same KPI looked at
    weekly is the only thing that catches slow decay -- an index quietly
    ageing, a ctags that stopped extracting, a header layout drifting until
    the dependency graph thins out.
    """
    conn = open_db(cfg.index.db_path)
    try:
        data = collect_kpis(conn, cfg.index.db_path)
    finally:
        conn.close()

    if as_json:
        print(json.dumps(data, sort_keys=True))
        return 0

    for key, value in data.items():
        if key == "collected_at":
            continue
        marker = "  (lower is better)" if key in LOWER_IS_BETTER else ""
        print(f"  {key:<26} {str(value):>12}{marker}")
    return 0


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


def _resolve(cfg: Config) -> int:
    conn = open_db(cfg.index.db_path)
    try:
        counts = resolve_includes(conn)
        edges = rebuild_repo_deps(conn)
    except Exception as exc:  # noqa: BLE001 - must not escape as an uncaught traceback
        # Mirrors _index's containment of this same resolve_includes /
        # rebuild_repo_deps pair (most notably sqlite3.IntegrityError from
        # rebuild_repo_deps's FK on repo_deps.to_repo_id, raised whenever an
        # include still points at a repo deleted since the last pass). Before
        # this, `argus resolve` had try/finally but no except, so the same
        # error that _index contains exited here as a raw traceback with no
        # exit code -- indistinguishable from any other crash by a caller
        # checking $?. Same exit code as _index's equivalent failure, so the
        # two paths agree.
        print(f"resolve/rebuild failed: {exc!r}", file=sys.stderr)
        return 4
    finally:
        conn.close()
    for state in ("resolved", "external", "ambiguous", "not_found"):
        print(f"{state:<12} {counts.get(state, 0)}")
    print(f"{'edges':<12} {edges}")
    return 0


def _serve(cfg: Config, host: str, port: int, allowed_hosts: list[str] | None) -> int:
    """Build the MCP app and run it, bound to ``host``/``port``.

    Binds localhost (`DEFAULT_SERVE_HOST`) unless the operator passes
    `--host` explicitly -- the server trusts the auth gate for identity, not
    the network perimeter, so it must never default to a wildcard bind.
    The stack in `stack/` puts Traefik in front for TLS; this process is
    meant to be reached only through that proxy or a loopback-only tunnel.

    `allowed_hosts` (from repeatable `--allowed-host`) is threaded into
    `create_app` so it lands in `transport_security.allowed_hosts` *at
    construction* -- FastMCP's DNS-rebinding Host-header allowlist is
    computed once, when the app object is built, and is never revisited when
    `app.settings.host` is reassigned below. A reverse proxy (Caddy) forwards
    the client's real Host header (e.g. `argus.internal`), not this
    process's own bind host, so leaving the allowlist at its loopback-only
    default behind such a proxy makes every real `/mcp` call 421. `None`
    (the flag not given) reproduces that original loopback-only default
    unchanged -- see `argus.mcpsrv.server._build_transport_security`.
    """
    app = create_app(cfg, allowed_hosts=allowed_hosts)
    app.settings.host = host
    app.settings.port = port
    app.run(transport="streamable-http")
    return 0


def _serve_stdio(cfg: Config) -> int:
    """Serve MCP over stdin/stdout, for clients that speak only stdio.

    The protocol is identical -- the same tools, the same ACL, the same audit
    rows. What differs is where identity comes from. HTTP resolves a bearer
    token per request because one server answers many developers; stdio is one
    client per process by construction, so the token arrives once in
    ``ARGUS_TOKEN`` and is resolved to an Identity before the first request.

    Resolved BEFORE serving, deliberately. A bad token fails here, on stderr,
    where the person configuring the client will see it -- rather than as a
    tool error inside an agent's transcript, which is where a missing
    credential is hardest to recognise for what it is.

    stdout belongs to the protocol: anything printed there corrupts the JSON-RPC
    stream, so every message this function emits goes to stderr.
    """
    from .acl import AclDenied, resolve
    from .mcpsrv.tools import set_stdio_identity

    token = os.environ.get("ARGUS_TOKEN", "").strip()
    if not token:
        print("ARGUS_TOKEN is not set. stdio has no request headers, so the "
              "credential must come from the environment.", file=sys.stderr)
        return 2

    conn = open_db(cfg.index.db_path)
    try:
        identity = resolve(conn, cfg.gitlab, token)
    except AclDenied as exc:
        print(f"credential rejected: {exc}", file=sys.stderr)
        return 2
    finally:
        conn.close()

    print(f"argus stdio: {identity.username} "
          f"({len(identity.allowed_repo_ids)} repos)", file=sys.stderr)
    set_stdio_identity(identity)
    create_app(cfg).run(transport="stdio")
    return 0


def _flush_acl(cfg: Config, user: str | None) -> int:
    """Delete cached ACL resolutions so a GitLab revocation takes effect now.

    Without this the only way to revoke access faster than the 600s TTL
    (`argus.acl.TTL_SECONDS`) is restarting the service. `--user` scopes the
    delete to one GitLab username (acl_cache can hold more than one row per
    user -- one per distinct token they've authenticated with); omitted, it
    clears every cached identity.

    Mirrors `_index`'s `--reset-retries` distinction between "the thing you
    named doesn't exist" and "there was nothing to clear": a `--user` that
    matches no cached row is reported by name, separately from the
    zero-rows-cleared message a bare `flush-acl` prints when the cache is
    already empty -- an operator chasing a stale revocation needs to know
    which of those happened.
    """
    conn = open_db(cfg.index.db_path)
    if user:
        cursor = conn.execute("DELETE FROM acl_cache WHERE username = ?", (user,))
        conn.commit()
        rows_cleared = cursor.rowcount
        if rows_cleared == 0:
            print(f"user '{user}' not in acl cache; nothing cleared")
        else:
            print(f"cleared acl cache for '{user}' ({rows_cleared} rows)")
    else:
        cursor = conn.execute("DELETE FROM acl_cache")
        conn.commit()
        rows_cleared = cursor.rowcount
        if rows_cleared > 0:
            print(f"cleared {rows_cleared} acl cache entries")
        else:
            print("no acl cache entries to clear")
    return 0



# ---------------------------------------------------------------------------
# argus pack ...
# ---------------------------------------------------------------------------

def _packs_dir(args) -> Path:
    """Resolve where packs live, from --packs-dir or --config.

    Both are accepted, and --packs-dir is why the public tooling works on its
    own. `Config.load` requires a GitLab URL and token; demanding those before
    someone can install a public documentation pack would be absurd for a
    corpus whose whole point is that anyone can use it.
    """
    if getattr(args, "packs_dir", None):
        return Path(args.packs_dir)
    if getattr(args, "config", None):
        return Config.load(args.config).packs_dir
    raise ConfigError("pass --packs-dir or --config to say where packs live")


def _describe(pack) -> str:
    flag = "" if pack.compatible else "  [INCOMPATIBLE]"
    size_mb = pack.size_bytes / (1024 * 1024)
    return (f"{pack.name:<16} {pack.version:<10} {pack.embedding_model:<20} "
            f"{size_mb:>8.1f} MB  {pack.license}{flag}")


def _embed(cfg: Config, limit: int | None) -> int:
    """Embed public symbols that have no current vector.

    Incremental: a rerun after indexing new code only does the new work, and
    an interrupted run resumes where it stopped. Reports stale rows rather
    than silently re-embedding them -- a model change invalidates every vector
    and rebuilding a large corpus is hours of CPU, which should be a decision.
    """
    from . import embed as embed_module
    from . import semantic
    from .store.db import open_db

    conn = open_db(cfg.index.db_path)
    try:
        stale = semantic.stale_count(conn)
        if stale:
            print(f"{stale:,} vectors were built with a different embedding "
                  f"model or dimension and are NOT usable. Delete them to "
                  f"rebuild: DELETE FROM symbol_embeddings WHERE model <> "
                  f"'{embed_module.EMBED_MODEL}';")

        def progress(done: int, total: int) -> None:
            print(f"  embedded {done:,} of {total:,}", flush=True)

        written = semantic.build_symbol_embeddings(
            conn, limit=limit, progress=progress)
        print(f"embedded {written:,} symbols" if written
              else "nothing to embed; every public symbol already has a vector")
        return 0
    except EmbeddingUnavailable as exc:
        print(f"embedding unavailable: {exc}", file=sys.stderr)
        return EXIT_PACK
    finally:
        conn.close()


def _pack_build(args) -> int:
    source_cls = SOURCES.get(args.source)
    if source_cls is None:
        print(f"unknown source {args.source!r}; known: {', '.join(sorted(SOURCES))}",
              file=sys.stderr)
        return EXIT_PACK
    source = source_cls()

    work_dir = Path(args.work_dir)
    commit = None
    if args.fetch:
        archive = getattr(source, "archive_url", "")
        origin = archive or f"{source.repo_url} ({source.branch})"
        print(f"fetching {origin} into {work_dir} ...")
        commit = fetch_source(source, work_dir)

    print(f"building {source.name} pack from {work_dir} ...")
    try:
        out = build_pack(
            source, work_dir=work_dir, out_path=Path(args.out),
            version=args.version, source_commit=commit or args.commit,
        )
    except EmbeddingUnavailable as exc:
        # Separate from BuildError because the fix is elsewhere: start Ollama
        # and pull the model, then rerun.
        print(f"embedding failed: {exc}", file=sys.stderr)
        print("is ollama running, and has the model been pulled?", file=sys.stderr)
        return EXIT_PACK

    conn = pack_format.open_pack(out)
    try:
        meta = pack_format.read_meta(conn)
    finally:
        conn.close()
    print(f"wrote {out} ({out.stat().st_size / (1024 * 1024):.1f} MB)")
    print(f"  docs {meta.get('doc_count')}  chunks {meta.get('chunk_count')}  "
          f"symbols {meta.get('symbol_count')} "
          f"(unresolved {meta.get('unresolved_symbol_count')})")
    return 0


def _pack_list(args) -> int:
    packs = registry.list_installed(_packs_dir(args))
    if not packs:
        # Not an error: an empty registry is a normal state, and a non-zero
        # exit would break any script that lists before installing.
        print("no packs installed")
        return 0
    print(f"{'NAME':<16} {'VERSION':<10} {'MODEL':<20} {'SIZE':>11}  LICENSE")
    for pack in packs:
        print(_describe(pack))
    return 0


def _pack_install(args) -> int:
    dest = _packs_dir(args)
    installed = registry.install(args.source, dest_dir=dest,
                                expected_sha256=args.sha256)
    print(f"installed {installed.name} {installed.version} -> {installed.path}")
    if not installed.compatible:
        print(f"warning: {installed.incompatible_reason}", file=sys.stderr)
        print("lookup and text search still work; semantic search does not.",
              file=sys.stderr)
    return 0


def _pack_info(args) -> int:
    dest = _packs_dir(args)
    matches = [p for p in registry.list_installed(dest) if p.name == args.name]
    if not matches:
        print(f"no installed pack named {args.name!r} in {dest}", file=sys.stderr)
        return EXIT_PACK
    pack = matches[0]
    meta = pack_format.read_meta(pack_format.open_pack(pack.path))

    print(f"name          {pack.name}")
    print(f"version       {pack.version}")
    print(f"path          {pack.path}")
    print(f"size          {pack.size_bytes / (1024 * 1024):.1f} MB")
    print(f"model         {pack.embedding_model} ({pack.embedding_dim}d)")
    print(f"compatible    {'yes' if pack.compatible else 'no'}")
    if not pack.compatible:
        print(f"              {pack.incompatible_reason}")
    print(f"source        {meta.get('source_repo', '')}")
    print(f"branch        {meta.get('source_branch', '')}")
    print(f"commit        {pack.source_commit}")
    print(f"docs          {meta.get('doc_count', '?')}")
    print(f"chunks        {meta.get('chunk_count', '?')}")
    print(f"symbols       {meta.get('symbol_count', '?')}")
    # This output is how a user meets the redistribution obligation, so the
    # licence and attribution are printed in full and never truncated.
    print()
    print(f"license       {pack.license}")
    print(f"license url   {meta.get('license_url', '')}")
    print("attribution")
    print(f"  {pack.attribution}")
    return 0


def _pack_remove(args) -> int:
    dest = _packs_dir(args)
    if registry.remove(args.name, dest):
        print(f"removed {args.name}")
        return 0
    print(f"no installed pack named {args.name!r} in {dest}", file=sys.stderr)
    return EXIT_PACK


def _pack_update(args) -> int:
    dest = _packs_dir(args)
    available = {entry.name: entry for entry in registry.fetch_index(args.index_url)}
    installed = registry.list_installed(dest)
    if args.name:
        installed = [p for p in installed if p.name == args.name]
        if not installed:
            print(f"no installed pack named {args.name!r} in {dest}", file=sys.stderr)
            return EXIT_PACK

    updated = 0
    for pack in installed:
        entry = available.get(pack.name)
        if entry is None:
            print(f"{pack.name}: not in the index, leaving alone")
            continue
        if entry.version == pack.version:
            print(f"{pack.name}: {pack.version} is current")
            continue
        print(f"{pack.name}: {pack.version} -> {entry.version}, downloading ...")
        registry.install(entry.url, dest_dir=dest, expected_sha256=entry.sha256)
        updated += 1
    print(f"{updated} pack(s) updated")
    return 0


def _pack_index(args) -> int:
    """Write the index that `pack update` consumes.

    Without this the update path had no producer: `--index-url` pointed at a
    file an operator had to hand-write, checksum and all, from a format
    documented nowhere. This is the other half.
    """
    dest = _packs_dir(args)
    out = Path(args.out)
    try:
        index = registry.write_index(dest, base_url=args.base_url, name=args.name)
    except OSError as exc:
        print(f"could not read {dest}: {exc}", file=sys.stderr)
        return EXIT_PACK

    if not index["packs"]:
        # Not an error exit: "there is nothing to publish yet" is a legitimate
        # answer to a build script, and a failure here would break a pipeline
        # on the run before the first pack exists. It does say so loudly,
        # because an index published empty silently un-publishes every pack.
        print(f"no packs found in {dest}"
              + (f" named {args.name!r}" if args.name else ""), file=sys.stderr)
    # `skipped` is not part of the format `fetch_index` reads, and it is kept in
    # the file anyway: a pack that fails to parse is silently absent from the
    # published index otherwise, and "the update did not offer it" is a much
    # harder thing to diagnose than reading the reason here.
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(json.dumps(index, indent=2) + "\n", encoding="utf-8")

    print(f"wrote {out}: {len(index['packs'])} pack(s) from {dest}")
    for entry in index["packs"]:
        print(f"  {entry['name']} {entry['version']}  "
              f"{entry['size_bytes'] / (1024 * 1024):.1f} MB  {entry['url']}")
    for skipped in index["skipped"]:
        print(f"  SKIPPED {skipped['file']}: {skipped['reason']}", file=sys.stderr)
    return 0


def _verify(args) -> int:
    """Check a draft answer against the installed packs. Exit 2 if contradicted.

    WHY THIS IS A CLI COMMAND AND NOT ONLY AN MCP TOOL

    `docs_verify` has existed as an MCP tool for a while, and the problem it was
    built for -- a model answering from memory with zero tool calls, wrong 100%
    of the time on contract claims -- is still open, because nothing makes the
    model call it. Every client has a way to run a SHELL COMMAND when the model
    finishes and to block on its exit code; almost none of them can be made to
    call a *tool* at that moment. So the enforcement point that exists everywhere
    is a command, and until this command existed, verify-after could not be
    forced in any client at all.

    Exit codes are the interface, and they carry the whole contract:

      0  nothing contradicted -- the draft stands. Also when the packs are
         silent about every identifier in it: "no authority here" is not an
         error, and a verifier that fails a draft for mentioning something it
         does not know would be worse than none.
      2  the documentation contradicts the draft. Block, and hand back what it
         said.
      6  could not check. NOT 2, deliberately: a deployment with no packs
         installed must not become an agent that can never finish a sentence.

    Only `contradicted` blocks. `confirmed` and `unstated` never do -- the tool
    exists to correct, not to replace, and an answer is allowed to be about
    something other than the fact it happens to mention.
    """
    try:
        cfg = Config.load(args.config)
    except (ConfigError, OSError) as exc:
        # OSError, not just ConfigError: a missing file raises
        # FileNotFoundError, and `main` catches the pair for exactly this
        # reason. Catching only ConfigError here meant a wrong --config path
        # escaped as a traceback -- and an unhandled exception exits 1, which a
        # hook reads as neither "clean" nor "blocked" but as a crash.
        print(f"config error: {exc}", file=sys.stderr)
        return EXIT_VERIFY_UNAVAILABLE

    text = args.text or ""
    if args.text_file:
        try:
            # `-` is stdin, which is how a hook pipes a transcript in without a
            # temporary file -- and a temporary file holding a model's entire
            # answer is one more thing to leak and to clean up.
            if str(args.text_file) == "-":
                text = sys.stdin.read()
            else:
                text = Path(args.text_file).read_text(encoding="utf-8")
        except OSError as exc:
            print(f"could not read {args.text_file}: {exc}", file=sys.stderr)
            return EXIT_VERIFY_UNAVAILABLE
    if not text.strip():
        # An empty draft is not a contradiction. A hook firing on a turn that
        # produced nothing (a tool-only turn, an interrupted one) must not block.
        print("nothing to verify", file=sys.stderr)
        return 0

    packs_dir = cfg.packs_dir
    paths = sorted(packs_dir.glob(f"*{registry.PACK_SUFFIX}")) \
        if packs_dir.is_dir() else []
    if not paths:
        print(f"no documentation packs installed in {packs_dir}; "
              f"cannot check this draft", file=sys.stderr)
        return EXIT_VERIFY_UNAVAILABLE

    try:
        opened = store_packs.open_packs(paths)
    except Exception as exc:                      # noqa: BLE001
        print(f"could not open the installed packs: {exc}", file=sys.stderr)
        return EXIT_VERIFY_UNAVAILABLE
    try:
        findings = store_packs.verify_text(opened, text, limit=args.limit)
    except Exception as exc:                      # noqa: BLE001
        print(f"could not check the draft: {exc}", file=sys.stderr)
        return EXIT_VERIFY_UNAVAILABLE
    finally:
        store_packs.close_packs(opened)

    contradicted = [f for f in findings
                    if str(f.get("status", "")).lower() == "contradicted"]

    if args.json:
        print(json.dumps({"contradicted": contradicted,
                          "findings": findings}, default=str))

    if not contradicted:
        # Silent on success unless asked. A hook that prints something on every
        # turn trains the reader to ignore it.
        if not args.quiet:
            print(f"verified against {len(paths)} pack(s): no contradictions",
                  file=sys.stderr)
        return 0

    # Stderr, because that is what a blocking hook hands back to the model --
    # and phrased as an instruction, since the reader is a model that has just
    # finished an answer and has to decide what to do about it.
    lines = [f"The documentation contradicts {len(contradicted)} claim(s) in "
             f"your draft. Correct these, or say the requirement is not "
             f"documented -- do not restate them from memory:"]
    for f in contradicted[:10]:
        subject = f.get("symbol") or f.get("name") or "?"
        field = f.get("field") or "?"
        said = f.get("stated") or f.get("claim") or "?"
        documented = f.get("documented") or f.get("value") or "?"
        source = f.get("source") or f.get("doc_path") or ""
        lines.append(f"  - {subject} {field}: you said {said!r}; the "
                     f"documentation says {documented!r}"
                     + (f" [{source}]" if source else ""))
    print("\n".join(lines), file=sys.stderr)
    return EXIT_VERIFY_CONTRADICTED


def _pack(args) -> int:
    return {
        "build": _pack_build, "list": _pack_list, "install": _pack_install,
        "info": _pack_info, "remove": _pack_remove, "update": _pack_update,
        "index": _pack_index,
    }[args.pack_command](args)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="argus")
    sub = parser.add_subparsers(dest="command", required=True)

    p_embed = sub.add_parser(
        "embed",
        help="Build semantic vectors over your own public symbols")
    p_embed.add_argument("--config", required=True, type=Path)
    p_embed.add_argument(
        "--limit", type=int, default=None,
        help="Embed at most this many symbols, then stop. For a first "
             "run on a large corpus, to see the rate before committing "
             "to hours.")

    p_index = sub.add_parser("index", help="Mirror and index repositories")
    p_index.add_argument("--config", required=True, type=Path)
    p_index.add_argument("--repo", help="Index only this path_with_namespace")
    p_index.add_argument(
        "--allow-partial-enumeration", action="store_true",
        help=("Index even when the service token cannot see every repository. "
              "Without an admin token GitLab's membership=false returns only "
              "PUBLIC projects, so the index would silently cover a fraction "
              "of the estate and every answer would be confidently incomplete."))
    p_index.add_argument(
        "--interval", type=int, default=0, metavar="SECONDS",
        help=("Keep running, starting a new pass every SECONDS. Without it a "
              "single pass runs and the command exits. This is the periodic "
              "poll the design specifies as the webhook's fallback: without "
              "it the index only advances when someone runs this by hand, and "
              "everyone reads stale answers while index_status reports the "
              "staleness faithfully. 900 (15 minutes) is the documented "
              "default cadence. A failing pass does not stop the loop."))
    p_index.add_argument(
        "--branch", action="append", metavar="GLOB", default=None,
        help=("Index this branch in every repo, overriding index.branches from "
              "the config for this run. Repeatable, and a glob: --branch main "
              "--branch 'release/*'. Each project's DEFAULT branch is always "
              "indexed as well -- it is what an unqualified question is "
              "answered from -- so this ADDS refs rather than replacing them. "
              "A repo that has no branch matching the pattern is indexed at its "
              "default alone rather than failing the run, which is what makes "
              "one branch name usable across an estate that does not share it."))
    p_index.add_argument("--reset-retries", action="store_true",
                         help="Clear retry counters before indexing (manual recovery only; do not use on a schedule)")

    p_backup = sub.add_parser(
        "backup", help="Write a restorable snapshot of the index and packs")
    p_backup.add_argument("--config", required=True, type=Path)
    p_backup.add_argument("--out", required=True, type=Path,
                          help="Directory to write the snapshot into")

    p_kpi = sub.add_parser(
        "kpi", help="Print health indicators computed from the index")
    p_kpi.add_argument("--config", required=True, type=Path)
    p_kpi.add_argument("--json", action="store_true",
                       help="Emit one JSON object, for appending to a time series")

    p_status = sub.add_parser("status", help="Show per-repo index freshness")
    p_status.add_argument("--config", required=True, type=Path)

    p_verify = sub.add_parser(
        "verify",
        help="Check a draft answer against the packs; exit 2 if contradicted",
        description=(
            "The enforcement point for verify-after. Every agent client can run "
            "a shell command when the model finishes and block on its exit code; "
            "almost none can be made to call an MCP tool at that moment. Exit 0 "
            "means the draft stands, 2 means the documentation contradicts it "
            "(with the contradictions on stderr, which a blocking hook hands "
            "back to the model), and 6 means it could not be checked -- no "
            "packs, or unreadable ones -- which deliberately does NOT block."))
    p_verify.add_argument("--config", required=True, type=Path)
    p_verify.add_argument(
        "--text-file", type=Path,
        help="File holding the draft. Use - to read stdin (what a hook pipes).")
    p_verify.add_argument("--text", help="The draft inline, for a quick check")
    p_verify.add_argument("--limit", type=int, default=40,
                          help="How many identifiers to resolve (default 40)")
    p_verify.add_argument("--json", action="store_true",
                          help="Emit the findings as JSON on stdout")
    p_verify.add_argument("--quiet", action="store_true",
                          help="Say nothing when the draft is clean")

    p_resolve = sub.add_parser(
        "resolve", help="Re-resolve includes and rebuild the dependency graph")
    p_resolve.add_argument("--config", required=True, type=Path)

    p_serve = sub.add_parser("serve", help="Run the MCP retrieval server")
    p_serve.add_argument("--config", required=True, type=Path)
    p_serve.add_argument(
        "--stdio", action="store_true",
        help="Serve MCP over stdin/stdout instead of HTTP, for clients "
             "that speak only stdio. Credential comes from ARGUS_TOKEN.")
    p_serve.add_argument("--host", default=DEFAULT_SERVE_HOST,
                         help=f"Bind address (default: {DEFAULT_SERVE_HOST})")
    p_serve.add_argument("--port", type=int, default=DEFAULT_SERVE_PORT,
                         help=f"Bind port (default: {DEFAULT_SERVE_PORT})")
    p_serve.add_argument(
        "--allowed-host", action="append", dest="allowed_hosts", metavar="HOST",
        help=(
            "Host header value the DNS-rebinding check will accept on /mcp "
            "(repeatable). Default: the loopback set "
            f"({', '.join(DEFAULT_ALLOWED_HOSTS)}) -- unchanged from a bare "
            "`argus serve`. A reverse-proxied deployment MUST pass the "
            "proxy-facing hostname the proxy forwards, e.g. "
            "--allowed-host argus.internal, or every /mcp call is rejected "
            "with 421 Invalid Host Header. Passing --allowed-host replaces "
            "the default set entirely rather than adding to it; pass it more "
            "than once to allow more than one hostname."
        ),
    )

    p_flush_acl = sub.add_parser(
        "flush-acl", help="Clear cached ACL resolutions ahead of their TTL"
    )
    p_flush_acl.add_argument("--config", required=True, type=Path)
    p_flush_acl.add_argument("--user", help="Only clear this GitLab username's cache entries")

    p_pack = sub.add_parser("pack", help="Build, install and inspect knowledge packs")
    pack_sub = p_pack.add_subparsers(dest="pack_command", required=True)

    def _where(parser: argparse.ArgumentParser) -> None:
        # Either is enough. --packs-dir keeps the public tooling usable without
        # a GitLab URL and token, which Config.load would otherwise demand.
        parser.add_argument("--config", type=Path, help="Read packs.dir from this config")
        parser.add_argument("--packs-dir", type=Path, help="Directory holding installed packs")

    p_build = pack_sub.add_parser("build", help="Build a pack from a documentation source")
    p_build.add_argument("--source", required=True,
                         help=f"One of: {', '.join(sorted(SOURCES))}")
    p_build.add_argument("--work-dir", required=True, type=Path,
                         help="Checkout of the source repository")
    p_build.add_argument("--out", required=True, type=Path, help="Pack file to write")
    p_build.add_argument("--version", required=True, help="Version to record in the pack")
    p_build.add_argument("--commit", help="Source commit, if work-dir is not a git checkout")
    p_build.add_argument("--fetch", action="store_true",
                         help="Clone or update the source into --work-dir first")

    p_plist = pack_sub.add_parser("list", help="List installed packs")
    _where(p_plist)

    p_pinstall = pack_sub.add_parser("install", help="Install a pack from a path or URL")
    p_pinstall.add_argument("source", help="Pack file path or https URL")
    p_pinstall.add_argument("--sha256", help="Expected SHA-256; install is refused on mismatch")
    _where(p_pinstall)

    p_pinfo = pack_sub.add_parser(
        "info", help="Show provenance, licence and attribution for an installed pack")
    p_pinfo.add_argument("name")
    _where(p_pinfo)

    p_premove = pack_sub.add_parser("remove", help="Remove an installed pack")
    p_premove.add_argument("name")
    _where(p_premove)

    p_pupdate = pack_sub.add_parser("update", help="Update installed packs from an index")
    p_pupdate.add_argument("name", nargs="?", help="Only this pack (default: all)")
    p_pupdate.add_argument("--index-url", required=True, help="Published pack index JSON")
    _where(p_pupdate)

    p_pindex = pack_sub.add_parser(
        "index", help="Write the published index that `pack update` reads")
    p_pindex.add_argument(
        "--out", required=True, type=Path, help="Index JSON file to write")
    p_pindex.add_argument(
        "--base-url", required=True,
        help=("Where the packs will be SERVED from -- the URL a consumer will "
              "fetch them at, not the path they sit at here. Each entry's url "
              "is this joined with the pack's filename."))
    p_pindex.add_argument("name", nargs="?", help="Only this pack (default: all)")
    _where(p_pindex)

    args = parser.parse_args(argv)

    # Handled before Config.load: pack commands may run with only --packs-dir,
    # and loading a full config would demand GitLab credentials they never use.
    if args.command == "pack":
        try:
            return _pack(args)
        except ConfigError as exc:
            print(f"config error: {exc}", file=sys.stderr)
            return 2
        except (BuildError, RegistryError, GitError) as exc:
            print(f"pack error: {exc}", file=sys.stderr)
            return EXIT_PACK

    # Handled BEFORE the shared Config.load below, and that is not tidiness:
    # that load returns 2 on a bad config, and 2 is this command's "the
    # documentation contradicts you". A typo in a path would block every answer
    # the agent tried to give, for ever, with a message about documentation.
    if args.command == "verify":
        return _verify(args)

    try:
        cfg = Config.load(args.config)
    except (ConfigError, OSError) as exc:
        print(f"config error: {exc}", file=sys.stderr)
        return 2

    try:
        if args.command == "index":
            # dataclasses.replace, not mutation: IndexConfig is frozen, and the
            # override is per-run rather than a change to what is on disk.
            if args.branch:
                cfg = replace(cfg, index=replace(cfg.index,
                                                 branches=tuple(args.branch)))
            if args.interval > 0:
                return _index_repeatedly(
                    cfg, args.repo, args.reset_retries,
                    args.allow_partial_enumeration, args.interval)
            return _index(cfg, args.repo, args.reset_retries,
                          allow_partial=args.allow_partial_enumeration)
        if args.command == "embed":
            return _embed(cfg, args.limit)
        if args.command == "serve":
            if getattr(args, "stdio", False):
                return _serve_stdio(cfg)
            return _serve(cfg, args.host, args.port, args.allowed_hosts)
        if args.command == "flush-acl":
            return _flush_acl(cfg, args.user)
        if args.command == "kpi":
            return _kpi(cfg, args.json)
        if args.command == "backup":
            return _backup(cfg, args.out, args.config)
        if args.command == "resolve":
            return _resolve(cfg)
        return _status(cfg)
    except GitLabError as exc:
        print(f"gitlab error: {exc}", file=sys.stderr)
        return 3
    except (httpx.HTTPError, credentials.CredentialError) as exc:
        # Anything else that could not reach GitLab. Named separately from the
        # GitLabError handler above only to keep the wording honest: this is a
        # transport or credential failure, not something GitLab said. Without
        # it every command that touches the API could still exit on a raw
        # traceback, which is unreadable in a log and indistinguishable from a
        # crash in Argus itself.
        print(f"could not reach GitLab: {type(exc).__name__}: {exc}",
              file=sys.stderr)
        return 3


if __name__ == "__main__":
    raise SystemExit(main())
