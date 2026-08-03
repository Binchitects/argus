from argus.resolve import Resolution
from argus.store.db import open_db


def test_resolution_states_are_the_four_the_spec_names():
    assert Resolution.RESOLVED == "resolved"
    assert Resolution.EXTERNAL == "external"
    assert Resolution.AMBIGUOUS == "ambiguous"
    assert Resolution.NOT_FOUND == "not_found"


def test_migration_adds_the_resolution_column(tmp_path):
    """Ambiguous and unfindable includes both leave resolved_file_id NULL.
    Without a column that distinguishes them, the operator statistic in
    index_status cannot be computed at all."""
    conn = open_db(tmp_path / "index.db")
    try:
        cols = {r[1] for r in conn.execute("PRAGMA table_info(includes)")}
    finally:
        conn.close()
    assert "resolution" in cols


def test_existing_include_rows_default_to_null_resolution(tmp_path):
    """NULL means 'never resolved', which is exactly true of every row
    written before this migration."""
    conn = open_db(tmp_path / "index.db")
    try:
        conn.execute("INSERT INTO repos (gitlab_id, path_with_namespace, "
                     "default_branch, http_url) VALUES (1, 'g/r', 'main', 'u')")
        conn.execute("INSERT INTO files (repo_id, path, size, blob_sha, content) "
                     "VALUES (1, 'a.c', 1, 'sha', '')")
        conn.execute("INSERT INTO includes (repo_id, file_id, raw, is_angle) "
                     "VALUES (1, 1, 'x.h', 0)")
        conn.commit()
        row = conn.execute("SELECT resolution FROM includes").fetchone()
    finally:
        conn.close()
    assert row[0] is None
