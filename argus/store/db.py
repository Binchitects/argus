from __future__ import annotations

import sqlite3
from pathlib import Path

MIGRATIONS_DIR = Path(__file__).parent / "migrations"


def connect(db_path: Path | str) -> sqlite3.Connection:
    path = Path(db_path)
    path.parent.mkdir(parents=True, exist_ok=True)
    conn = sqlite3.connect(path)
    conn.row_factory = sqlite3.Row
    conn.execute("PRAGMA journal_mode = WAL")
    conn.execute("PRAGMA foreign_keys = ON")
    conn.execute("PRAGMA synchronous = NORMAL")
    return conn


def migrate(conn: sqlite3.Connection) -> int:
    current = conn.execute("PRAGMA user_version").fetchone()[0]
    for sql_file in sorted(MIGRATIONS_DIR.glob("*.sql")):
        version = int(sql_file.name.split("_", 1)[0])
        if version <= current:
            continue
        conn.executescript(sql_file.read_text(encoding="utf-8"))
        # PRAGMA does not accept bound parameters; version is a validated int.
        conn.execute(f"PRAGMA user_version = {version}")
        conn.commit()
        current = version
    return current


def open_db(db_path: Path | str) -> sqlite3.Connection:
    conn = connect(db_path)
    migrate(conn)
    return conn


def connect_readonly(db_path: Path | str) -> sqlite3.Connection:
    """Open the index read-only. The server must never write index data.

    check_same_thread=False because an HTTP server is concurrent; the caller
    is responsible for using one connection per request or per thread.
    """
    uri = f"file:{Path(db_path).as_posix()}?mode=ro"
    conn = sqlite3.connect(uri, uri=True, check_same_thread=False)
    conn.row_factory = sqlite3.Row
    conn.execute("PRAGMA query_only = ON")
    return conn


#: The audit log lives in a sidecar database rather than in the index.
#:
#: Measured: every tool call appends an audit row, which is a WRITE. In WAL a
#: reader never blocks on a writer, but a writer does -- so with an indexing
#: run in progress each query had to serialise behind it just to record that
#: it happened. Read throughput collapsed 76.1 -> 7.2 req/s at 4 concurrent
#: readers and p95 went 305 ms -> 4,410 ms.
#:
#: Splitting the writes onto their own file removes that coupling entirely:
#: the query reads the index, the audit row goes somewhere nothing else is
#: writing, and neither waits for the indexer.
#:
#: `argus backup` copies this alongside the index. It is the one table a
#: reindex cannot reconstruct, so losing it to a refactor would be worse than
#: the contention it fixes.
AUDIT_SUFFIX = "-audit.db"

_AUDIT_SCHEMA = """
CREATE TABLE IF NOT EXISTS audit (
  id            INTEGER PRIMARY KEY,
  ts            INTEGER NOT NULL,
  user_id       INTEGER,
  username      TEXT,
  tool          TEXT    NOT NULL,
  args_json     TEXT    NOT NULL,
  repo_ids_json TEXT
);
CREATE INDEX IF NOT EXISTS idx_audit_ts ON audit(ts);
"""


def audit_db_path(db_path: Path | str) -> Path:
    """Sidecar path for ``db_path``: ``index.db`` -> ``index-audit.db``."""
    path = Path(db_path)
    return path.with_name(path.stem + AUDIT_SUFFIX)


def connect_audit(db_path: Path | str) -> sqlite3.Connection:
    """Open (creating if absent) the audit sidecar for ``db_path``.

    Schema is created here rather than by a migration: the file is derived
    from the index path at runtime and has exactly one table, so a migration
    runner pointed at the index would never see it.
    """
    conn = connect(audit_db_path(db_path))
    conn.executescript(_AUDIT_SCHEMA)
    conn.commit()
    return conn
