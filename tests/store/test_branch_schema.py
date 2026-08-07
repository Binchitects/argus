"""Migration 011: one repo row per (project, branch)."""
import sqlite3

import pytest

from argus.store.db import open_db
from argus.store import writes


def test_the_rebuild_does_not_cascade_away_the_whole_index(tmp_path):
    """The migration drops and recreates `repos`. Every other table references
    repos(id) ON DELETE CASCADE, so dropping it with foreign keys enforced
    would delete every file, symbol and include -- the upgrade would silently
    empty the database it was meant to upgrade.

    Built at migration 010 and then migrated forward, because a database
    created fresh at 011 never exercises the copy at all.
    """
    db_path = tmp_path / "i.db"
    conn = sqlite3.connect(db_path)
    conn.row_factory = sqlite3.Row
    conn.executescript("PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;")
    from argus.store import db as dbmod
    for sql in sorted(dbmod.MIGRATIONS_DIR.glob("*.sql")):
        version = int(sql.name.split("_", 1)[0])
        if version > 10:
            break
        conn.executescript(sql.read_text(encoding="utf-8"))
        conn.execute(f"PRAGMA user_version = {version}")
    conn.commit()

    conn.execute("INSERT INTO repos (gitlab_id, path_with_namespace, "
                 "default_branch, http_url) VALUES (7, 'g/r', 'main', 'u')")
    rid = conn.execute("SELECT id FROM repos").fetchone()["id"]
    conn.execute("INSERT INTO files (repo_id, path, size, blob_sha, content) "
                 "VALUES (?, 'a.c', 1, 's', 'x')", (rid,))
    fid = conn.execute("SELECT id FROM files").fetchone()["id"]
    conn.execute("INSERT INTO symbols (repo_id, file_id, name, kind, line) "
                 "VALUES (?, ?, 'f', 'function', 1)", (rid, fid))
    conn.commit()
    conn.close()

    conn = open_db(db_path)          # applies 011
    try:
        assert conn.execute("SELECT COUNT(*) FROM files").fetchone()[0] == 1, \
            "the rebuild cascaded and deleted the index"
        assert conn.execute("SELECT COUNT(*) FROM symbols").fetchone()[0] == 1
        row = conn.execute("SELECT id, branch, default_branch FROM repos").fetchone()
        assert row["id"] == rid, "repo ids must survive; every table references them"
        assert row["branch"] == "main", "an existing row was indexed at its default"
    finally:
        conn.close()


def test_a_project_can_now_exist_at_several_branches(tmp_path):
    conn = open_db(tmp_path / "i.db")
    try:
        ids = {b: writes.upsert_repo(conn, gitlab_id=7, path_with_namespace="g/r",
                                     default_branch="main", branch=b,
                                     http_url="u")
               for b in ("main", "v2", "v1")}
        assert len(set(ids.values())) == 3, "branches collapsed onto one row"
        assert conn.execute("SELECT COUNT(*) FROM repos").fetchone()[0] == 3
    finally:
        conn.close()


def test_the_same_branch_twice_is_still_one_row(tmp_path):
    """Re-indexing must update, not accumulate. The old ON CONFLICT keyed on
    gitlab_id alone; keyed too narrowly it would insert a duplicate every
    pass, and keyed too broadly it would collapse the branches."""
    conn = open_db(tmp_path / "i.db")
    try:
        first = writes.upsert_repo(conn, gitlab_id=7, path_with_namespace="g/r",
                                   default_branch="main", branch="v2", http_url="u")
        again = writes.upsert_repo(conn, gitlab_id=7, path_with_namespace="g/renamed",
                                   default_branch="main", branch="v2", http_url="u2")
        assert first == again
        row = conn.execute("SELECT path_with_namespace, http_url FROM repos").fetchone()
        assert row["path_with_namespace"] == "g/renamed"
        assert row["http_url"] == "u2"
    finally:
        conn.close()


def test_acl_grants_every_branch_of_a_permitted_project(tmp_path):
    """A user permitted on a GitLab project is permitted on all of its
    branches -- permission is a property of the project, and GitLab has no
    per-branch read ACL to mirror. Verified rather than assumed, because it
    is the seam where multi-branch could silently widen access."""
    from argus.acl import _map_to_repo_ids
    conn = open_db(tmp_path / "i.db")
    try:
        allowed = {writes.upsert_repo(conn, gitlab_id=7, path_with_namespace="g/ok",
                                      default_branch="main", branch=b, http_url="u")
                   for b in ("main", "v1")}
        secret = writes.upsert_repo(conn, gitlab_id=9, path_with_namespace="g/secret",
                                    default_branch="main", branch="main", http_url="u")
        got = set(_map_to_repo_ids(conn, [7]))
        assert got == allowed
        assert secret not in got
    finally:
        conn.close()
