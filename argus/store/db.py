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
