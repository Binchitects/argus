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
    retry_paths = [p for p in writes.drain_retry_paths(conn, repo_id)
                  if p not in changed_paths and p in shas]
    pending = [c for c in changes if c.status in ("A", "M")]
    pending += [Change(status="M", path=p) for p in retry_paths]
    to_parse: list[str] = []
    failed_paths: list[str] = []

    for change in pending:
        if now() - started > index_cfg.repo_time_budget_seconds:
            result.timed_out = True
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
            writes.record_error(conn, repo_id, change.path, "store",
                                repr(exc), int(now()))
            result.errors += 1
            failed_paths.append(change.path)
            continue

        to_parse.append(change.path)
        result.indexed += 1

    _apply_symbols(conn, repo_id, tree, to_parse, result, now)

    if failed_paths:
        writes.enqueue_retry(conn, repo_id, failed_paths,
                             "per-file read/store error", int(now()))

    if not result.timed_out and not result.symbols_failed:
        writes.set_last_indexed(conn, repo_id, new_sha, int(now()))
    return result


def _already_current(conn, repo_id: int, path: str, blob_sha: str) -> bool:
    """True if this path is already stored with this exact blob and symbols.

    Guards against a livelock where a timed-out or repeated full-listing
    pass recomputes and redoes the same diff every run (each redo now an
    FTS delete+reinsert, slower than the first pass) without ever making
    progress. Only skip when the stored row exists, its blob_sha matches
    the current tree exactly, AND it already has symbol rows — otherwise a
    file that was upserted but whose symbol pass never completed (the
    symbols_failed path) would be skipped forever with no symbols.
    """
    if not blob_sha:
        return False
    row = conn.execute(
        "SELECT id FROM files WHERE repo_id = ? AND path = ? AND blob_sha = ?",
        (repo_id, path, blob_sha),
    ).fetchone()
    if row is None:
        return False
    has_symbols = conn.execute(
        "SELECT 1 FROM symbols WHERE file_id = ? LIMIT 1", (row["id"],)
    ).fetchone()
    return has_symbols is not None


def _apply_symbols(conn, repo_id: int, tree: Path, paths: list[str],
                   result: IndexResult, now) -> None:
    if not paths:
        return
    try:
        by_path = ctags.extract_symbols(tree, paths)
    except ctags.CtagsUnavailable as exc:
        writes.record_error(conn, repo_id, None, "ctags", str(exc), int(now()))
        result.errors += 1
        result.symbols_failed = True
        return

    for path in paths:
        row = conn.execute(
            "SELECT id FROM files WHERE repo_id = ? AND path = ?", (repo_id, path)
        ).fetchone()
        if row is None:
            continue
        writes.replace_symbols(conn, repo_id, row["id"], by_path.get(path, []))
