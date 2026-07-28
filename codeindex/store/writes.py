from __future__ import annotations

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
                    symbols: list[dict]) -> None:
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
