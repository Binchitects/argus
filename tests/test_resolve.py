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


def _repo(conn, gitlab_id, name):
    cur = conn.execute(
        "INSERT INTO repos (gitlab_id, path_with_namespace, default_branch, "
        "http_url) VALUES (?, ?, 'main', 'u')", (gitlab_id, name))
    return cur.lastrowid


def _file(conn, repo_id, path):
    cur = conn.execute(
        "INSERT INTO files (repo_id, path, size, blob_sha, content) "
        "VALUES (?, ?, 1, 'sha', '')", (repo_id, path))
    return cur.lastrowid


def _include(conn, repo_id, file_id, raw, is_angle=0):
    conn.execute("INSERT INTO includes (repo_id, file_id, raw, is_angle) "
                 "VALUES (?, ?, ?, ?)", (repo_id, file_id, raw, is_angle))


def test_a_unique_suffix_match_resolves_across_repos(tmp_path):
    db = open_db(tmp_path / "index.db")
    try:
        a = _repo(db, 1, "g/app")
        b = _repo(db, 2, "g/eal")
        src = _file(db, a, "src/main.c")
        hdr = _file(db, b, "include/eal/eal_thread.h")
        _include(db, a, src, "eal/eal_thread.h")
        db.commit()

        counts = resolve.resolve_includes(db)
        assert counts[resolve.Resolution.RESOLVED] == 1

        row = db.execute("SELECT resolved_file_id, resolved_repo_id, resolution, "
                         "is_external FROM includes").fetchone()
        assert row["resolved_file_id"] == hdr
        assert row["resolved_repo_id"] == b
        assert row["resolution"] == resolve.Resolution.RESOLVED
        assert row["is_external"] == 0
    finally:
        db.close()


def test_an_ambiguous_include_emits_no_link_and_is_counted(tmp_path):
    """util.h exists in a dozen repos. Choosing the most likely one produces
    an edge that is invisible when wrong, permanent, and feeds the centrality
    behind every future answer."""
    db = open_db(tmp_path / "index.db")
    try:
        a = _repo(db, 1, "g/app")
        b = _repo(db, 2, "g/one")
        c = _repo(db, 3, "g/two")
        src = _file(db, a, "src/main.c")
        _file(db, b, "include/util.h")
        _file(db, c, "lib/util.h")
        _include(db, a, src, "util.h")
        db.commit()

        counts = resolve.resolve_includes(db)
        assert counts[resolve.Resolution.AMBIGUOUS] == 1

        row = db.execute("SELECT resolved_repo_id, resolution FROM includes").fetchone()
        assert row["resolved_repo_id"] is None, "an ambiguous include must link nothing"
        assert row["resolution"] == resolve.Resolution.AMBIGUOUS
    finally:
        db.close()


def test_same_repo_wins_over_a_foreign_match(tmp_path):
    db = open_db(tmp_path / "index.db")
    try:
        a = _repo(db, 1, "g/app")
        b = _repo(db, 2, "g/other")
        src = _file(db, a, "src/main.c")
        mine = _file(db, a, "src/util.h")
        _file(db, b, "lib/util.h")
        _include(db, a, src, "util.h")
        db.commit()

        resolve.resolve_includes(db)
        row = db.execute("SELECT resolved_file_id, resolved_repo_id FROM includes").fetchone()
        assert row["resolved_file_id"] == mine
        assert row["resolved_repo_id"] == a
    finally:
        db.close()


def test_a_quoted_relative_include_resolves_against_its_own_directory(tmp_path):
    db = open_db(tmp_path / "index.db")
    try:
        a = _repo(db, 1, "g/app")
        src = _file(db, a, "src/eal/x.c")
        target = _file(db, a, "src/common/util.h")
        _include(db, a, src, "../common/util.h")
        db.commit()

        resolve.resolve_includes(db)
        row = db.execute("SELECT resolved_file_id, resolution FROM includes").fetchone()
        assert row["resolved_file_id"] == target
        assert row["resolution"] == resolve.Resolution.RESOLVED
    finally:
        db.close()


def test_an_unmatched_angle_include_is_external(tmp_path):
    db = open_db(tmp_path / "index.db")
    try:
        a = _repo(db, 1, "g/app")
        src = _file(db, a, "src/main.c")
        _include(db, a, src, "stdio.h", is_angle=1)
        db.commit()

        counts = resolve.resolve_includes(db)
        assert counts[resolve.Resolution.EXTERNAL] == 1
        row = db.execute("SELECT is_external, resolution FROM includes").fetchone()
        assert row["is_external"] == 1
        assert row["resolution"] == resolve.Resolution.EXTERNAL
    finally:
        db.close()


def test_an_angle_include_that_matches_an_indexed_file_is_internal(tmp_path):
    """C projects routinely #include <eal/x.h> via -I. Treating every angle
    include as external would erase most of the graph."""
    db = open_db(tmp_path / "index.db")
    try:
        a = _repo(db, 1, "g/app")
        b = _repo(db, 2, "g/eal")
        src = _file(db, a, "src/main.c")
        hdr = _file(db, b, "include/eal/x.h")
        _include(db, a, src, "eal/x.h", is_angle=1)
        db.commit()

        resolve.resolve_includes(db)
        row = db.execute("SELECT resolved_file_id, is_external FROM includes").fetchone()
        assert row["resolved_file_id"] == hdr
        assert row["is_external"] == 0
    finally:
        db.close()


def test_an_unmatched_quoted_include_is_not_found(tmp_path):
    db = open_db(tmp_path / "index.db")
    try:
        a = _repo(db, 1, "g/app")
        src = _file(db, a, "src/main.c")
        _include(db, a, src, "nowhere/missing.h")
        db.commit()

        counts = resolve.resolve_includes(db)
        assert counts[resolve.Resolution.NOT_FOUND] == 1
    finally:
        db.close()


def test_resolution_is_independent_of_insertion_order(tmp_path):
    """An include can point into a repo indexed later in the same cycle.
    Resolving per-repo would make the graph depend on indexing order."""
    db = open_db(tmp_path / "index.db")
    try:
        b = _repo(db, 2, "g/eal")
        a = _repo(db, 1, "g/app")
        src = _file(db, a, "src/main.c")
        hdr = _file(db, b, "include/eal/x.h")
        _include(db, a, src, "eal/x.h")
        db.commit()

        resolve.resolve_includes(db)
        row = db.execute("SELECT resolved_file_id FROM includes").fetchone()
        assert row["resolved_file_id"] == hdr
    finally:
        db.close()


def test_rerunning_resolution_is_idempotent(tmp_path):
    db = open_db(tmp_path / "index.db")
    try:
        a = _repo(db, 1, "g/app")
        b = _repo(db, 2, "g/eal")
        src = _file(db, a, "src/main.c")
        _file(db, b, "include/eal/x.h")
        _include(db, a, src, "eal/x.h")
        db.commit()

        first = resolve.resolve_includes(db)
        second = resolve.resolve_includes(db)
        assert first == second
    finally:
        db.close()
