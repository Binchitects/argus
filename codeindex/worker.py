from __future__ import annotations

import time
from dataclasses import dataclass
from pathlib import Path

from .config import IndexConfig
from .gitlab import Project
from .mirror import blob_shas, changed_files, is_ancestor
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

    full_reindex = old_sha is None or not is_ancestor(mirror_path, old_sha, new_sha)
    changes = changed_files(mirror_path, old_sha, new_sha)
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

    pending = [c for c in changes if c.status in ("A", "M")]
    to_parse: list[str] = []

    for change in pending:
        if now() - started > index_cfg.repo_time_budget_seconds:
            result.timed_out = True
            break

        abs_path = tree / change.path
        try:
            data = abs_path.read_bytes()
        except OSError as exc:
            writes.record_error(conn, repo_id, change.path, "read",
                                str(exc), int(now()))
            result.errors += 1
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
            continue

        to_parse.append(change.path)
        result.indexed += 1

    _apply_symbols(conn, repo_id, tree, to_parse, result, now)

    if not result.timed_out and not result.symbols_failed:
        writes.set_last_indexed(conn, repo_id, new_sha, int(now()))
    return result


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
