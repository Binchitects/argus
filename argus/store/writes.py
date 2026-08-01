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


def get_acl_cache(conn: sqlite3.Connection, token_hash: str) -> sqlite3.Row | None:
    """Look up a cached ACL resolution by SHA-256 token hash.

    The raw token is never passed in or stored here -- only its hash, which
    is what makes acl_cache safe to keep at rest.
    """
    return conn.execute(
        "SELECT user_id, username, repo_ids_json, fetched_at FROM acl_cache"
        " WHERE token_hash = ?",
        (token_hash,),
    ).fetchone()


def upsert_acl_cache(conn: sqlite3.Connection, *, token_hash: str, user_id: int,
                     username: str, repo_ids_json: str, fetched_at: int) -> None:
    conn.execute(
        """
        INSERT INTO acl_cache (token_hash, user_id, username, repo_ids_json, fetched_at)
        VALUES (?, ?, ?, ?, ?)
        ON CONFLICT(token_hash) DO UPDATE SET
            user_id       = excluded.user_id,
            username      = excluded.username,
            repo_ids_json = excluded.repo_ids_json,
            fetched_at    = excluded.fetched_at
        """,
        (token_hash, user_id, username, repo_ids_json, fetched_at),
    )
    conn.commit()


def set_last_indexed(conn: sqlite3.Connection, repo_id: int, sha: str, ts: int) -> None:
    conn.execute(
        "UPDATE repos SET last_indexed_sha = ?, last_indexed_at = ? WHERE id = ?",
        (sha, ts, repo_id),
    )
    conn.commit()


def record_run_state(conn: sqlite3.Connection, repo_id: int, *,
                     timed_out: bool, symbols_failed: bool, ts: int,
                     error: str | None = None) -> None:
    """Record that this repo was checked, and how the check went.

    `ts` means "last checked", not "last did work": callers must record it on
    every path that reaches the repo, including one that turned out to need
    no work at all, or a current repo reads as indefinitely stale. `error` is
    the message when the run could not complete (a failed fetch, an
    unexpected exception) and None when it did -- passing None on a healthy
    run is what clears a previous failure, so it must never be skipped.
    """
    conn.execute(
        "UPDATE repos SET last_run_timed_out = ?, last_run_symbols_failed = ?,"
        "                 last_run_at = ?, last_run_error = ? WHERE id = ?",
        (int(timed_out), int(symbols_failed), ts, error, repo_id),
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


def peek_retry_paths(conn: sqlite3.Connection, repo_id: int) -> list[str]:
    """Return the paths queued for retry for this repo, leaving the queue intact.

    Reading and clearing are separate operations because nothing else
    re-derives a retry path: the pass that first failed on it let the SHA
    advance past the commit that changed it, so it never reappears in a
    later diff. A pass that reads the queue and then dies before writing the
    next one must leave the old queue standing, or those files are lost with
    no record.
    """
    row = conn.execute(
        "SELECT reason FROM index_queue WHERE repo_id = ?", (repo_id,)
    ).fetchone()
    if row is None:
        return []
    try:
        payload = json.loads(row["reason"])
        paths = payload.get("paths", [])
    except (ValueError, AttributeError):
        return []
    return [p for p in paths if isinstance(p, str)]


def clear_retry_queue(conn: sqlite3.Connection, repo_id: int) -> None:
    """Drop this repo's queued retries, committing the deletion."""
    conn.execute("DELETE FROM index_queue WHERE repo_id = ?", (repo_id,))
    conn.commit()


def drain_retry_paths(conn: sqlite3.Connection, repo_id: int) -> list[str]:
    """Return and clear the paths queued for retry for this repo, if any."""
    paths = peek_retry_paths(conn, repo_id)
    clear_retry_queue(conn, repo_id)
    return paths


def record_audit(conn: sqlite3.Connection, *, ts: int, user_id: int | None,
                 username: str | None, tool: str, args_json: str,
                 repo_ids_json: str | None) -> None:
    """Append one audit row: one call attempt, one row.

    `conn` must be a read-write connection (`connect`, never
    `connect_readonly`) -- this is the one write the MCP server's otherwise
    strictly read-only request path performs, and it needs its own
    connection separate from the one used to run the query itself (see
    `argus.mcpsrv.tools._record_audit`).

    `user_id`/`username` are None for a call denied before any identity was
    resolved (an `AclDenied` at the auth gate) -- the columns are nullable
    for exactly that reason. `tool` is NOT NULL; callers denied before a
    specific tool was identified pass a fixed sentinel rather than leaving it
    blank. Recording happens whether the call succeeded or failed: this
    table has no outcome column by design (see migration 007) -- its job is
    "what was this identity shown or did they attempt", not a full
    success/failure request log.
    """
    conn.execute(
        "INSERT INTO audit (ts, user_id, username, tool, args_json, repo_ids_json)"
        " VALUES (?, ?, ?, ?, ?, ?)",
        (ts, user_id, username, tool, args_json, repo_ids_json),
    )
    conn.commit()
