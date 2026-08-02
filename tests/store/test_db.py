from argus.store.db import MIGRATIONS_DIR, open_db, migrate

EXPECTED_TABLES = {
    "repos", "files", "symbols", "includes",
    "repo_deps", "index_errors", "index_queue", "files_fts",
    "retry_attempts",
}

# Derived, not hardcoded: adding a migration should not require editing this.
LATEST_VERSION = max(
    int(p.name.split("_", 1)[0]) for p in MIGRATIONS_DIR.glob("*.sql")
)


def test_migrate_creates_tables(tmp_path):
    conn = open_db(tmp_path / "i.db")
    rows = conn.execute(
        "SELECT name FROM sqlite_master WHERE type IN ('table','view')"
    ).fetchall()
    names = {r["name"] for r in rows}
    assert EXPECTED_TABLES <= names


def test_migrate_is_idempotent(tmp_path):
    path = tmp_path / "i.db"
    v1 = migrate(open_db(path))
    v2 = migrate(open_db(path))
    assert v1 == v2 == LATEST_VERSION


def test_foreign_keys_enforced(tmp_path):
    import sqlite3
    import pytest
    conn = open_db(tmp_path / "i.db")
    with pytest.raises(sqlite3.IntegrityError):
        conn.execute(
            "INSERT INTO files (repo_id, path, size, blob_sha, content)"
            " VALUES (999, 'a.c', 1, 'deadbeef', 'x')"
        )
        conn.commit()


def test_cascade_delete_removes_files(tmp_path):
    conn = open_db(tmp_path / "i.db")
    conn.execute(
        "INSERT INTO repos (gitlab_id, path_with_namespace, default_branch, http_url)"
        " VALUES (1, 'g/a', 'main', 'https://x/g/a')"
    )
    repo_id = conn.execute("SELECT id FROM repos").fetchone()["id"]
    conn.execute(
        "INSERT INTO files (repo_id, path, size, blob_sha, content)"
        " VALUES (?, 'a.c', 1, 'deadbeef', 'x')", (repo_id,)
    )
    conn.commit()
    conn.execute("DELETE FROM repos WHERE id = ?", (repo_id,))
    conn.commit()
    assert conn.execute("SELECT COUNT(*) c FROM files").fetchone()["c"] == 0


def test_connect_readonly_rejects_writes(tmp_path):
    import pytest, sqlite3
    from argus.store.db import open_db, connect_readonly
    path = tmp_path / "i.db"
    open_db(path).close()
    ro = connect_readonly(path)
    with pytest.raises(sqlite3.OperationalError, match="readonly"):
        ro.execute("INSERT INTO repos (gitlab_id, path_with_namespace, default_branch,"
                   " http_url) VALUES (1, 'g/a', 'main', 'x')")

    # Verify that disabling PRAGMA query_only still fails.
    # This pins the guarantee to file-level mode=ro, not the bypassable pragma.
    ro.execute("PRAGMA query_only = OFF")
    with pytest.raises(sqlite3.OperationalError, match="readonly"):
        ro.execute("INSERT INTO repos (gitlab_id, path_with_namespace, default_branch,"
                   " http_url) VALUES (1, 'g/b', 'main', 'y')")


def test_connect_readonly_can_read(tmp_path):
    from argus.store.db import open_db, connect_readonly
    from argus.store import writes
    path = tmp_path / "i.db"
    conn = open_db(path)
    writes.upsert_repo(conn, gitlab_id=1, path_with_namespace="g/a",
                       default_branch="main", http_url="x")
    assert connect_readonly(path).execute("SELECT COUNT(*) c FROM repos").fetchone()["c"] == 1


def test_acl_and_audit_tables_exist(tmp_path):
    from argus.store.db import open_db
    conn = open_db(tmp_path / "i.db")
    names = {r["name"] for r in conn.execute(
        "SELECT name FROM sqlite_master WHERE type = 'table'")}
    assert {"acl_cache", "audit"} <= names
