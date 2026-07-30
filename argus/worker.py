from __future__ import annotations

import time
from dataclasses import dataclass
from pathlib import Path

from .config import IndexConfig
from .gitlab import Project
from .mirror import Change, blob_shas, changes_since
from .parse import ctags, filters
from .parse.includes import extract_includes
from .store import writes


@dataclass
class IndexResult:
    repo_id: int
    indexed: int = 0
    deleted: int = 0
    skipped: int = 0
    errors: int = 0
    sha: str = ""
    timed_out: bool = False
    symbols_failed: bool = False


def _repo_id(conn, gitlab_id: int) -> int:
    return conn.execute(
        "SELECT id FROM repos WHERE gitlab_id = ?", (gitlab_id,)
    ).fetchone()["id"]


def index_repo(conn, index_cfg: IndexConfig, project: Project,
               mirror_path: Path, tree: Path, new_sha: str,
               old_sha: str | None, *, now=None) -> IndexResult:
    now = now or time.time
    started = now()
    repo_id = _repo_id(conn, project.gitlab_id)
    result = IndexResult(repo_id=repo_id, sha=new_sha)

    # One ancestry resolution for both answers. An old_sha that no longer
    # resolves (mirrors dir wiped, gc pruned a force-pushed history, repo
    # re-created upstream) routes to the full-reindex path and self-heals;
    # making it fatal would strand the repo forever, since last_indexed_sha
    # would stay stale and every later run would fail identically.
    full_reindex, changes = changes_since(mirror_path, old_sha, new_sha)
    shas = blob_shas(mirror_path, new_sha)

    # Deletions first: they are cheap and always safe to apply.
    for change in changes:
        if change.status == "D":
            writes.delete_file(conn, repo_id, change.path)
            result.deleted += 1

    if full_reindex:
        # changed_files' full-listing fallback (first index, or a force-push
        # that rewrote history) emits every path in the NEW tree as "A" — it
        # never inspects the old tree, so it can never emit "D". Without this,
        # rows for files that vanished from the tree (content, symbols,
        # includes, FTS entries) would linger forever even though the SHA
        # advances and search would keep returning deleted code as live.
        existing_paths = {
            row["path"] for row in conn.execute(
                "SELECT path FROM files WHERE repo_id = ?", (repo_id,)
            )
        }
        for path in existing_paths - shas.keys():
            writes.delete_file(conn, repo_id, path)
            result.deleted += 1

    changed_paths = {c.path for c in changes if c.status in ("A", "M")}
    # A path that errored on a previous pass (a transient OSError, say) keeps
    # its old content/symbols forever: the SHA still advances (blocking it
    # would violate "one bad file must never abort a repo"), so the next
    # diff starts from the new SHA and the path never reappears unless
    # edited again. Union back in whatever the previous pass queued for
    # retry, as long as it still exists in the new tree.
    # Read, but do not yet clear: the queue row is only replaced once this
    # pass has reached the point where it can write the next one (see the
    # clear_retry_queue call below).
    queued = writes.peek_retry_paths(conn, repo_id)
    retry_paths = [p for p in queued
                   if p not in changed_paths and p in shas]
    retry_set = set(retry_paths)
    # Retry paths go FIRST. They are known-stale, index_queue's row was
    # already DELETEd by the drain, and nothing re-derives them: the pass
    # that first failed on them let the SHA advance past the commit that
    # changed them, so they never reappear in a later diff. Diff paths, by
    # contrast, are safe to drop on a timeout because a timed-out pass holds
    # the SHA and the next pass recomputes the same diff. Appending retries
    # last made a repo_time_budget break the first thing to discard them,
    # on exactly the big repos most likely to time out.
    pending = [Change(status="M", path=p) for p in retry_paths]
    pending += [c for c in changes if c.status in ("A", "M")]
    to_parse: list[str] = []
    failed_paths: list[str] = []
    unreached_retry: list[str] = []

    for position, change in enumerate(pending):
        if now() - started > index_cfg.repo_time_budget_seconds:
            result.timed_out = True
            # Preserve the unreached tail's retry paths before the pass ends;
            # they have no other source to come back from.
            unreached_retry = [c.path for c in pending[position:]
                               if c.path in retry_set]
            break

        if _already_current(conn, repo_id, change.path, shas.get(change.path, "")):
            result.skipped += 1
            continue

        abs_path = tree / change.path

        try:
            st_size = abs_path.stat().st_size
        except OSError as exc:
            writes.record_error(conn, repo_id, change.path, "read",
                                str(exc), int(now()))
            result.errors += 1
            failed_paths.append(change.path)
            continue

        # Cheap pre-checks before paying for a read: a file over the size
        # cap or with an undetectable language is rejected either way by
        # should_index below, so there is no reason to bring potentially
        # gigabytes of it into memory first just to discard it.
        if (st_size > index_cfg.max_file_bytes
                or filters.detect_lang(change.path) is None):
            result.skipped += 1
            continue

        try:
            data = abs_path.read_bytes()
        except (OSError, MemoryError) as exc:
            conn.rollback()   # discard any partial FTS/files work before committing the error
            writes.record_error(conn, repo_id, change.path, "read",
                                str(exc), int(now()))
            result.errors += 1
            failed_paths.append(change.path)
            continue

        if not filters.should_index(
            change.path, len(data), data,
            max_bytes=index_cfg.max_file_bytes,
            exclude_dirs=index_cfg.exclude_dirs,
        ):
            result.skipped += 1
            continue

        try:
            content = data.decode("utf-8", errors="replace")
            file_id = writes.upsert_file(
                conn, repo_id=repo_id, path=change.path,
                lang=filters.detect_lang(change.path), size=len(data),
                blob_sha=shas.get(change.path, ""), content=content,
            )
            writes.replace_includes(conn, repo_id, file_id,
                                    extract_includes(content))
        except Exception as exc:  # one bad file must not abort the repo
            conn.rollback()   # discard any partial FTS/files work before committing the error
            writes.record_error(conn, repo_id, change.path, "store",
                                repr(exc), int(now()))
            result.errors += 1
            failed_paths.append(change.path)
            continue

        to_parse.append(change.path)
        result.indexed += 1

    # Paths whose read/store succeeded but that ctags never processed. These
    # are per-path failures, not a repo-wide outage: they must not be stamped
    # complete, and they must come back on a later pass.
    uncovered = _apply_symbols(conn, repo_id, tree, to_parse, result, now, shas)
    failed_paths.extend(uncovered)

    # Clear on success regardless of how the path entered this pass -- not
    # only paths that arrived via the retry queue -- so a fixed underlying
    # cause (an ACL, a >260-char Windows path, an AV quarantine) actually
    # recovers. A path that exhausted the cap long ago and dropped out of
    # the queue is never queued again; if it later comes back healthy
    # through an ordinary diff instead, restricting the clear to `queued`
    # would leave its stale attempts count at the cap forever. But when
    # symbols_failed, NO path in to_parse actually completed (its read/store
    # succeeded, but that alone is not "done"): clearing the counter here
    # would let bump_retry_attempts re-insert a fresh attempts=1 row every
    # such pass, and attempts could never reach MAX_RETRY_ATTEMPTS. Paths
    # gone from the tree entirely are unaffected by that guard -- they can
    # never come back through to_parse again regardless, so it is still
    # safe to forget them even on a symbols_failed pass.
    # A path ctags did not cover is not "indexed" either, no matter that its
    # read and store succeeded -- clearing its counter would let it retry
    # forever without ever accumulating attempts.
    indexed_paths = (set(to_parse) - set(uncovered)
                     if not result.symbols_failed else set())
    gone_paths = {p for p in queued if p not in shas}
    if indexed_paths or gone_paths:
        writes.clear_retry_attempts(conn, repo_id, list(indexed_paths | gone_paths))

    symbols_only_failed: set[str] = set()
    if result.symbols_failed:
        # A retry-origin path is not in any future diff, so if its symbols failed
        # it must stay queued or it is lost permanently.
        symbols_only_failed = {p for p in to_parse if p in retry_set}
        failed_paths.extend(symbols_only_failed)

    retryable = _cap_retries(conn, repo_id, failed_paths, now)

    # Only now is the previously-queued set safe to discard: everything that
    # still needs retrying has been recomputed above and is about to be
    # written back. Deleting it up front (the old drain) meant an unexpected
    # exception anywhere in this function destroyed the queue permanently --
    # nothing re-derives a retry path, so those files were simply lost.
    writes.clear_retry_queue(conn, repo_id)

    if retryable or unreached_retry:
        uncovered_set = set(uncovered)
        reasons = []
        if any(p not in symbols_only_failed and p not in uncovered_set
               for p in retryable):
            reasons.append("per-file read/store error")
        if any(p in uncovered_set for p in retryable):
            reasons.append("ctags did not process the file")
        if any(p in symbols_only_failed for p in retryable):
            reasons.append("symbol extraction error")
        if unreached_retry:
            reasons.append("not reached before the repo time budget")
        writes.enqueue_retry(conn, repo_id, retryable + unreached_retry,
                             "; ".join(reasons), int(now()))

    if not result.timed_out and not result.symbols_failed:
        writes.set_last_indexed(conn, repo_id, new_sha, int(now()))

    # Unconditional: a clean pass must clear a previously-set flag, or a
    # transient timeout/ctags outage would leave index_status reporting
    # partial coverage forever even after the repo fully recovers.
    writes.record_run_state(conn, repo_id, timed_out=result.timed_out,
                            symbols_failed=result.symbols_failed, ts=int(now()))
    return result


def _cap_retries(conn, repo_id: int, failed_paths: list[str], now) -> list[str]:
    """Return the failed paths still worth retrying, giving up past the cap.

    A path that can never be read (ACL denial, >260-char Windows path, AV
    quarantine) would otherwise be re-enqueued every pass forever: the queue
    would never empty and index_errors would grow without bound. Give up after
    MAX_RETRY_ATTEMPTS, recording one distinguishable final row so the decision
    is not silent. result.errors is deliberately not bumped again -- the
    failure that triggered this already counted once.
    """
    if not failed_paths:
        return []
    attempts = writes.bump_retry_attempts(conn, repo_id, failed_paths)
    retryable: list[str] = []
    for path in failed_paths:
        if attempts.get(path, 0) >= writes.MAX_RETRY_ATTEMPTS:
            writes.record_error(
                conn, repo_id, path, "retry-exhausted",
                f"giving up after {writes.MAX_RETRY_ATTEMPTS} failed attempts;"
                " no longer queued for retry",
                int(now()),
            )
        else:
            retryable.append(path)
    return retryable


def _already_current(conn, repo_id: int, path: str, blob_sha: str) -> bool:
    """True when this exact blob is stored AND its symbols were extracted from it.

    Guards against a livelock where a timed-out or repeated full-listing
    pass recomputes and redoes the same diff every run (each redo now an
    FTS delete+reinsert, slower than the first pass) without ever making
    progress. Completion is tracked explicitly via files.symbols_sha rather
    than inferred from "does this file have any symbol rows" — that proxy
    was wrong in both directions: a file with zero symbols (include-only
    .c, macro-only header) never satisfied it and was redone every pass,
    while a file whose fresh extraction failed could still satisfy it using
    symbol rows left over from an older revision.

    Keep the existing parameter name — this replaces the body only, so every
    existing call site stays valid.
    """
    if not blob_sha:
        return False
    row = conn.execute(
        "SELECT blob_sha, symbols_sha FROM files WHERE repo_id = ? AND path = ?",
        (repo_id, path),
    ).fetchone()
    if row is None:
        return False
    return row["blob_sha"] == blob_sha and row["symbols_sha"] == blob_sha


def _apply_symbols(conn, repo_id: int, tree: Path, paths: list[str],
                   result: IndexResult, now, shas: dict[str, str]) -> list[str]:
    """Stamp symbols for the paths ctags covered; report the ones it did not.

    Returns the paths ctags did NOT process. Those are genuine per-path
    failures (a file it could not open, a name the filesystem refuses to hand
    back) and the caller must treat them as such: never stamped complete,
    surfaced in index_errors, and queued for retry. Stamping them -- which is
    what looping blindly over `paths` did -- marks them complete forever,
    because symbols_sha = blob_sha makes the next pass skip them as
    already-current and the SHA has already advanced past the commit that
    would otherwise bring them back.
    """
    if not paths:
        return []
    try:
        batch = ctags.extract_symbols(tree, paths)
    except ctags.CtagsUnavailable as exc:
        writes.record_error(conn, repo_id, None, "ctags", str(exc), int(now()))
        result.errors += 1
        result.symbols_failed = True
        # These files were already upserted with their new content and new
        # blob_sha, so their surviving symbol rows still describe the
        # previous revision -- symbols_sha was never touched this pass, so
        # _already_current (which now compares symbols_sha to the new
        # blob_sha) already reports these files incomplete on their own,
        # without needing the rows cleared. Clearing them anyway is still
        # required for two other reasons: (1) search must not keep serving
        # symbols from a revision the file no longer has, and (2) it NULLs
        # symbols_sha so a later edit that reverts the content to be
        # byte-identical to an *older* successfully-extracted blob cannot
        # make a stale symbols_sha coincidentally equal the recomputed
        # blob_sha and be mistaken for complete.
        writes.clear_symbols_for_paths(conn, repo_id, paths)
        return []

    uncovered: list[str] = []
    for path in paths:
        row = conn.execute(
            "SELECT id FROM files WHERE repo_id = ? AND path = ?", (repo_id, path)
        ).fetchone()
        if row is None:
            continue
        if path not in batch.covered:
            writes.record_error(
                conn, repo_id, path, "symbols",
                batch.uncovered.get(path, "ctags did not process this file"),
                int(now()),
            )
            result.errors += 1
            uncovered.append(path)
            continue
        # An empty list here is a real answer -- ctags opened the file and
        # found nothing taggable -- so it is stamped complete, which is the
        # whole point of tracking coverage separately from the symbol map.
        writes.replace_symbols(conn, repo_id, row["id"], batch.symbols.get(path, []),
                               shas.get(path, ""))

    # Drop whatever these still carry from an older revision: their content
    # row was already updated in place, so surviving symbol rows describe a
    # revision the file no longer has, and a NULL symbols_sha cannot later be
    # mistaken for a match if the content is reverted to that older blob.
    writes.clear_symbols_for_paths(conn, repo_id, uncovered)
    return uncovered
