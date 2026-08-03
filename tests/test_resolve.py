from argus.resolve import Resolution
from argus.store.db import open_db
from argus import resolve


def test_path_suffixes_are_component_aligned():
    assert resolve.path_suffixes("src/eal/eal_thread.h") == [
        "src/eal/eal_thread.h", "eal/eal_thread.h", "eal_thread.h",
    ]


def test_a_single_component_path_yields_itself_only():
    assert resolve.path_suffixes("stdio.h") == ["stdio.h"]


def test_suffix_index_groups_files_under_every_suffix():
    index = resolve.build_suffix_index([
        (1, 10, "src/eal/eal_thread.h"),
        (2, 20, "include/eal_thread.h"),
    ])
    assert {f[0] for f in index["eal_thread.h"]} == {1, 2}
    assert {f[0] for f in index["eal/eal_thread.h"]} == {1}


def test_a_longer_name_does_not_match_a_shorter_one():
    """The defect that matters. A naive endswith makes 'eal_thread.h' match
    'not_eal_thread.h' -- the same class of bug as the substring-blame defect
    in Phase 1, which deleted healthy symbols."""
    index = resolve.build_suffix_index([(1, 10, "src/not_eal_thread.h")])
    assert "eal_thread.h" not in index
    assert "not_eal_thread.h" in index


def test_directory_prefixes_do_not_match_either():
    index = resolve.build_suffix_index([(1, 10, "src/myeal/x.h")])
    assert "eal/x.h" not in index


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
