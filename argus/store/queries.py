from __future__ import annotations

import sqlite3
from collections.abc import Sequence


def _placeholders(allowed_repo_ids: Sequence[int]) -> tuple[str, list[int]]:
    """Validate the allowlist and render it as SQL placeholders.

    A str is explicitly rejected: it is a Sequence, so accepting it would let
    ``find_symbol("all", ...)`` silently degrade into a per-character filter.
    """
    if isinstance(allowed_repo_ids, (str, bytes)) or not isinstance(
        allowed_repo_ids, (list, tuple, set, frozenset)
    ):
        raise TypeError(
            "allowed_repo_ids must be a list/tuple/set of int repo ids, "
            f"got {type(allowed_repo_ids).__name__}"
        )
    ids = list(allowed_repo_ids)
    if any(not isinstance(i, int) or isinstance(i, bool) for i in ids):
        raise TypeError("allowed_repo_ids must contain only int repo ids")
    return ",".join("?" for _ in ids), ids


def find_symbol(allowed_repo_ids: Sequence[int], conn: sqlite3.Connection,
                name: str, kind: str | None = None,
                limit: int = 50) -> list[sqlite3.Row]:
    marks, ids = _placeholders(allowed_repo_ids)
    if not ids:
        return []
    sql = (
        "SELECT s.repo_id, r.path_with_namespace, f.path, s.name, s.kind,"
        "       s.line, s.end_line, s.signature, s.scope, s.is_public"
        "  FROM symbols s"
        "  JOIN files f ON f.id = s.file_id"
        "  JOIN repos r ON r.id = s.repo_id"
        f" WHERE s.repo_id IN ({marks}) AND s.name = ?"
    )
    params: list = [*ids, name]
    if kind is not None:
        sql += " AND s.kind = ?"
        params.append(kind)
    sql += " ORDER BY s.is_public DESC, r.path_with_namespace, f.path LIMIT ?"
    params.append(limit)
    return conn.execute(sql, params).fetchall()


def search_code(allowed_repo_ids: Sequence[int], conn: sqlite3.Connection,
                query: str, limit: int = 50) -> list[sqlite3.Row]:
    marks, ids = _placeholders(allowed_repo_ids)
    if not ids:
        return []
    return conn.execute(
        "SELECT f.repo_id, r.path_with_namespace, f.path,"
        "       snippet(files_fts, 1, '[', ']', '…', 16) AS snippet"
        "  FROM files_fts"
        "  JOIN files f ON f.id = files_fts.rowid"
        "  JOIN repos r ON r.id = f.repo_id"
        f" WHERE files_fts MATCH ? AND f.repo_id IN ({marks})"
        "  ORDER BY rank LIMIT ?",
        [query, *ids, limit],
    ).fetchall()


def get_file(allowed_repo_ids: Sequence[int], conn: sqlite3.Connection,
             repo_id: int, path: str) -> sqlite3.Row | None:
    marks, ids = _placeholders(allowed_repo_ids)
    if not ids:
        return None
    return conn.execute(
        "SELECT f.repo_id, r.path_with_namespace, f.path, f.lang, f.size, f.content"
        "  FROM files f JOIN repos r ON r.id = f.repo_id"
        f" WHERE f.repo_id = ? AND f.path = ? AND f.repo_id IN ({marks})",
        [repo_id, path, *ids],
    ).fetchone()


def index_status(allowed_repo_ids: Sequence[int],
                 conn: sqlite3.Connection) -> list[sqlite3.Row]:
    marks, ids = _placeholders(allowed_repo_ids)
    if not ids:
        return []
    return conn.execute(
        "SELECT r.id AS repo_id, r.path_with_namespace, r.last_indexed_sha,"
        "       r.last_indexed_at, r.last_run_timed_out, r.last_run_symbols_failed,"
        "       r.last_run_at, r.last_run_error,"
        "       (SELECT COUNT(*) FROM files   WHERE repo_id = r.id) AS files,"
        "       (SELECT COUNT(*) FROM symbols WHERE repo_id = r.id) AS symbols,"
        "       (SELECT COUNT(*) FROM index_errors WHERE repo_id = r.id) AS errors,"
        # index_queue.repo_id is a PRIMARY KEY: one row per repo, with the
        # queued paths JSON-packed into `reason`. COUNT(*) is therefore a 0/1
        # flag, not a count -- a repo with 4,000 stuck paths reported "1".
        # Count the packed paths instead. json_valid() guards a row whose
        # reason is not the payload (hand-written, or pre-dating the format):
        # json_extract would otherwise abort the entire status query with a
        # malformed-JSON error, and the outer COALESCE turns both "no queue
        # row" and "unreadable payload" into 0.
        "       COALESCE((SELECT CASE WHEN json_valid(reason)"
        "                             THEN json_array_length("
        "                                      json_extract(reason, '$.paths'))"
        "                        END"
        "                   FROM index_queue WHERE repo_id = r.id), 0)"
        "         AS queued_retries"
        "  FROM repos r"
        f" WHERE r.id IN ({marks})"
        "  ORDER BY r.path_with_namespace",
        ids,
    ).fetchall()
