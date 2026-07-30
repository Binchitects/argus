from __future__ import annotations

import json
import sqlite3


def upsert_repo(conn: sqlite3.Connection, *, gitlab_id: int,
                path_with_namespace: str, default_branch: str,
                http_url: str) -> int:
    conn.execute(
        """
        INSERT INTO repos (gitlab_id, path_with_namespace, default_branch, http_url)
        VALUES (?, ?, ?, ?)
        ON CONFLICT(gitlab_id) DO UPDATE SET
            path_with_namespace = excluded.path_with_namespace,
            default_branch      = excluded.default_branch,
            http_url            = excluded.http_url
        """,
        (gitlab_id, path_with_namespace, default_branch, http_url),
    )
    conn.commit()
    return conn.execute(
        "SELECT id FROM repos WHERE gitlab_id = ?", (gitlab_id,)
    ).fetchone()["id"]


def set_last_indexed(conn: sqlite3.Connection, repo_id: int, sha: str, ts: int) -> None:
    conn.execute(
        "UPDATE repos SET last_indexed_sha = ?, last_indexed_at = ? WHERE id = ?",
        (sha, ts, repo_id),
    )
    conn.commit()


def record_run_state(conn: sqlite3.Connection, repo_id: int, *,
                     timed_out: bool, symbols_failed: bool, ts: int) -> None:
    conn.execute(
        "UPDATE repos SET last_run_timed_out = ?, last_run_symbols_failed = ?,"
        "                 last_run_at = ? WHERE id = ?",
        (int(timed_out), int(symbols_failed), ts, repo_id),
    )
    conn.commit()


def _fts_delete(conn: sqlite3.Connection, row: sqlite3.Row) -> None:
    """Remove a row from the external-content FTS index using its OLD values."""
    conn.execute(
        "INSERT INTO files_fts(files_fts, rowid, path, content) "
        "VALUES ('delete', ?, ?, ?)",
        (row["id"], row["path"], row["content"]),
    )


def upsert_file(conn: sqlite3.Connection, *, repo_id: int, path: str,
                lang: str | None, size: int, blob_sha: str, content: str) -> int:
    existing = conn.execute(
        "SELECT id, path, content FROM files WHERE repo_id = ? AND path = ?",
        (repo_id, path),
    ).fetchone()

    if existing is not None:
        _fts_delete(conn, existing)
        conn.execute(
            "UPDATE files SET lang = ?, size = ?, blob_sha = ?, content = ? WHERE id = ?",
            (lang, size, blob_sha, content, existing["id"]),
        )
        file_id = existing["id"]
    else:
        cur = conn.execute(
            "INSERT INTO files (repo_id, path, lang, size, blob_sha, content)"
            " VALUES (?, ?, ?, ?, ?, ?)",
            (repo_id, path, lang, size, blob_sha, content),
        )
        file_id = cur.lastrowid

    conn.execute(
        "INSERT INTO files_fts(rowid, path, content) VALUES (?, ?, ?)",
        (file_id, path, content),
    )
    conn.commit()
    return file_id


def delete_file(conn: sqlite3.Connection, repo_id: int, path: str) -> None:
    row = conn.execute(
        "SELECT id, path, content FROM files WHERE repo_id = ? AND path = ?",
        (repo_id, path),
    ).fetchone()
    if row is None:
        return
    _fts_delete(conn, row)
    conn.execute("DELETE FROM files WHERE id = ?", (row["id"],))
    conn.commit()


def replace_symbols(conn: sqlite3.Connection, repo_id: int, file_id: int,
                    symbols: list[dict], blob_sha: str) -> None:
    conn.execute("DELETE FROM symbols WHERE file_id = ?", (file_id,))
    conn.executemany(
        "INSERT INTO symbols"
        " (repo_id, file_id, name, kind, line, end_line, signature, scope, is_public)"
        " VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)",
        [
            (repo_id, file_id, s["name"], s["kind"], s["line"], s.get("end_line"),
             s.get("signature"), s.get("scope"), int(s.get("is_public", 0)))
            for s in symbols
        ],
    )
    # An empty symbol list is a successful extraction, not an incomplete one.
    conn.execute("UPDATE files SET symbols_sha = ? WHERE id = ?", (blob_sha, file_id))
    conn.commit()


def clear_symbols_for_paths(conn: sqlite3.Connection, repo_id: int,
                            paths: list[str]) -> None:
    """Drop the symbol rows of these paths so they cannot look up to date.

    upsert_file UPDATEs in place: after it runs, the row already carries the
    new content and the new blob_sha while the symbol rows still describe the
    previous revision. If symbol extraction then fails, clearing them is what
    makes _already_current return False on the next pass -- otherwise the
    surviving stale rows would let the file be reported complete and the SHA
    would advance over symbols from an older revision.

    symbols_sha is cleared alongside the rows, not left holding the old
    blob_sha. Leaving it would usually be harmless -- it is not the new
    blob_sha, so _already_current's equality check fails regardless -- but it
    becomes a real bug if the path's content is later edited back to be
    byte-identical to that older blob: the recomputed blob_sha would then
    equal the stale symbols_sha again, and _already_current would report the
    file complete despite having zero symbol rows. NULL-ing it here closes
    that gap and keeps the marker's meaning exact: "this blob's symbols are
    on record," not "some past blob's were."
    """
    if not paths:
        return
    conn.executemany(
        "DELETE FROM symbols WHERE file_id IN"
        " (SELECT id FROM files WHERE repo_id = ? AND path = ?)",
        [(repo_id, path) for path in paths],
    )
    conn.executemany(
        "UPDATE files SET symbols_sha = NULL WHERE repo_id = ? AND path = ?",
        [(repo_id, path) for path in paths],
    )
    conn.commit()


def replace_includes(conn: sqlite3.Connection, repo_id: int, file_id: int,
                     includes: list[dict]) -> None:
    conn.execute("DELETE FROM includes WHERE file_id = ?", (file_id,))
    conn.executemany(
        "INSERT INTO includes (repo_id, file_id, raw, is_angle) VALUES (?, ?, ?, ?)",
        [(repo_id, file_id, i["raw"], int(i.get("is_angle", 0))) for i in includes],
    )
    conn.commit()


def record_error(conn: sqlite3.Connection, repo_id: int, path: str | None,
                 stage: str, message: str, ts: int) -> None:
    conn.execute(
        "INSERT INTO index_errors (repo_id, path, stage, message, ts)"
        " VALUES (?, ?, ?, ?, ?)",
        (repo_id, path, stage, message[:2000], ts),
    )
    conn.commit()


def enqueue_retry(conn: sqlite3.Connection, repo_id: int, paths: list[str],
                  reason: str, ts: int) -> None:
    """Persist paths that failed this pass so the next pass retries them.

    index_queue is keyed one row per repo (repo_id PRIMARY KEY) with no
    path column, so the failed paths are serialized as JSON into the
    existing free-text `reason` field. An upsert replaces any previous
    entry outright: retries are the current pass's failures, not an
    ever-growing accumulation across passes.
    """
    if not paths:
        return
    payload = json.dumps({"reason": reason, "paths": sorted(set(paths))})
    conn.execute(
        """
        INSERT INTO index_queue (repo_id, enqueued_at, reason)
        VALUES (?, ?, ?)
        ON CONFLICT(repo_id) DO UPDATE SET
            enqueued_at = excluded.enqueued_at,
            reason      = excluded.reason
        """,
        (repo_id, ts, payload),
    )
    conn.commit()


MAX_RETRY_ATTEMPTS = 3


def bump_retry_attempts(conn: sqlite3.Connection, repo_id: int,
                        paths: list[str]) -> dict[str, int]:
    """Count one more failure per path; return the cumulative counts."""
    if not paths:
        return {}
    conn.executemany(
        "INSERT INTO retry_attempts (repo_id, path, attempts) VALUES (?, ?, 1)"
        " ON CONFLICT(repo_id, path) DO UPDATE SET attempts = attempts + 1",
        [(repo_id, path) for path in paths],
    )
    conn.commit()
    wanted = set(paths)
    return {
        row["path"]: row["attempts"]
        for row in conn.execute(
            "SELECT path, attempts FROM retry_attempts WHERE repo_id = ?",
            (repo_id,),
        )
        if row["path"] in wanted
    }


def clear_retry_attempts(conn: sqlite3.Connection, repo_id: int,
                         paths: list[str]) -> None:
    """Forget the failure history of paths that are healthy again.

    Without this a path that failed twice long ago, recovered, and failed once
    more years later would be given up on immediately.
    """
    if not paths:
        return
    conn.executemany(
        "DELETE FROM retry_attempts WHERE repo_id = ? AND path = ?",
        [(repo_id, path) for path in paths],
    )
    conn.commit()


def drain_retry_paths(conn: sqlite3.Connection, repo_id: int) -> list[str]:
    """Return and clear the paths queued for retry for this repo, if any."""
    row = conn.execute(
        "SELECT reason FROM index_queue WHERE repo_id = ?", (repo_id,)
    ).fetchone()
    if row is None:
        return []
    conn.execute("DELETE FROM index_queue WHERE repo_id = ?", (repo_id,))
    conn.commit()
    try:
        payload = json.loads(row["reason"])
        paths = payload.get("paths", [])
    except (ValueError, AttributeError):
        return []
    return [p for p in paths if isinstance(p, str)]
