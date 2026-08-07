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


def test_a_vendored_copy_in_another_repo_does_not_become_a_false_edge(tmp_path):
    """Found by the first real indexing run, on real public C projects.

    zlib's own zconf.h is GENERATED at build time and absent from its source
    tree, while libjpeg-turbo vendors a whole copy of zlib at
    src/spng/zlib/zconf.h. That copy was the only candidate, so the ambiguity
    guard never fired and `#include "zconf.h"` inside zlib resolved
    confidently into libjpeg-turbo -- a false zlib -> libjpeg-turbo edge in a
    graph where zlib depends on nothing.
    """
    db = open_db(tmp_path / "index.db")
    try:
        z = _repo(db, 1, "g/zlib")
        j = _repo(db, 2, "g/libjpeg-turbo")
        src = _file(db, z, "zlib.h")
        _file(db, j, "src/spng/zlib/zconf.h")
        _include(db, z, src, "zconf.h")
        db.commit()

        counts = resolve.resolve_includes(db)
        row = db.execute("SELECT resolved_repo_id, resolution FROM includes").fetchone()
        assert row["resolved_repo_id"] is None, (
            "resolved into another repo's vendored copy")
        assert counts[resolve.Resolution.NOT_FOUND] == 1
    finally:
        db.close()


def test_a_library_namespacing_headers_under_its_own_name_still_resolves(tmp_path):
    """The false positive the first version of the vendored check introduced.

    `eal/include/eal/eal_thread.h` -- a library namespacing its headers under
    a directory matching its own name -- is the most ordinary layout in C.
    Treating that as vendored would erase legitimate edges across every
    well-organised project.
    """
    db = open_db(tmp_path / "index.db")
    try:
        app = _repo(db, 1, "g/app")
        eal = _repo(db, 2, "g/eal")
        src = _file(db, app, "src/main.c")
        hdr = _file(db, eal, "include/eal/eal_thread.h")
        _include(db, app, src, "eal/eal_thread.h")
        db.commit()

        resolve.resolve_includes(db)
        row = db.execute("SELECT resolved_file_id, resolved_repo_id FROM includes").fetchone()
        assert row["resolved_file_id"] == hdr
        assert row["resolved_repo_id"] == eal
    finally:
        db.close()


def _mkfile(conn, rid, path, sha=None):
    from argus.store import writes
    return writes.upsert_file(conn, repo_id=rid, path=path, lang="c", size=1,
                              blob_sha=sha or path, content="x")


def test_find_vendored_dirs_detects_a_bundled_copy_by_filename_cluster(tmp_path):
    """Content hashing cannot do this. Measured on real repos, freetype's
    src/gzip/inflate.c is 57,147 bytes against zlib's 53,660 -- the copy was
    modified, so the hashes differ. The filenames survive the edits."""
    db = open_db(tmp_path / "i.db")
    try:
        zlib = _repo(db, 1, "g/zlib")
        ft = _repo(db, 2, "g/freetype")
        for name in ("inflate.c", "deflate.c", "zutil.c", "crc32.c", "adler32.c"):
            _mkfile(db, zlib, name)
            _mkfile(db, ft, f"src/gzip/{name}", sha=f"modified-{name}")
        db.commit()

        rows = [(r["id"], r["repo_id"], r["path"])
                for r in db.execute("SELECT id, repo_id, path FROM files")]
        found = resolve.find_vendored_dirs(rows)
        assert (ft, "src/gzip") in found
    finally:
        db.close()


def test_the_original_is_never_mistaken_for_the_copy(tmp_path):
    """Overlap is symmetric, so a naive rule flags both. Measured on the real
    index, zlib's own root 'matches libjpeg-turbo at 74%' purely because
    libjpeg-turbo bundles zlib -- and excluding zlib's canonical files from
    resolution would be far worse than the bug being fixed.

    The asymmetry is depth: a copy is nested deeper than its original.

    The original is deliberately NOT at the repo root here. A root directory is
    skipped outright, so a root-held original is protected by that guard and
    this test would pass with the depth comparison deleted -- it has to sit in
    a subdirectory to make the depth rule the only thing standing.
    """
    db = open_db(tmp_path / "i.db")
    try:
        zlib = _repo(db, 1, "g/zlib")
        ft = _repo(db, 2, "g/freetype")
        for name in ("inflate.c", "deflate.c", "zutil.c", "crc32.c", "adler32.c"):
            _mkfile(db, zlib, f"src/{name}")                       # depth 1
            _mkfile(db, ft, f"src/gzip/{name}", sha=f"m-{name}")   # depth 2 -- copy
        db.commit()

        rows = [(r["id"], r["repo_id"], r["path"])
                for r in db.execute("SELECT id, repo_id, path FROM files")]
        found = resolve.find_vendored_dirs(rows)
        assert (ft, "src/gzip") in found, "the copy was missed, so nothing is proven"
        assert (zlib, "src") not in found, "the canonical repo was called a copy"
    finally:
        db.close()


def test_a_few_shared_names_in_a_large_directory_is_not_vendoring(tmp_path):
    """Any two C projects share a `config.h`, a `util.h`, an `error.c`. Enough
    of those coincide in a big source directory to clear the absolute count,
    so the count alone cannot be the test -- what marks a bundled library is
    that it is *most* of what the directory contains.

    The overlap here is 5 names, above VENDOR_MIN_FILES, out of 20. Only the
    share ratio rejects it.
    """
    db = open_db(tmp_path / "i.db")
    try:
        a = _repo(db, 1, "g/a")
        b = _repo(db, 2, "g/b")
        common = ("config.h", "util.h", "error.c", "list.c", "debug.h")
        for name in common:
            _mkfile(db, a, name)                       # depth 0, so a "owns" them
            _mkfile(db, b, f"src/{name}", sha=f"b-{name}")
        for i in range(15):
            _mkfile(db, b, f"src/own{i}.c")            # b's directory is its own
        db.commit()

        rows = [(r["id"], r["repo_id"], r["path"])
                for r in db.execute("SELECT id, repo_id, path FROM files")]
        found = resolve.find_vendored_dirs(rows)
        assert (b, "src") not in found, "an ordinary source dir was called vendored"
    finally:
        db.close()


def test_resolve_persists_the_vendored_verdict_for_query_time(tmp_path):
    """Detection needs every path in the index at once, so it is materialised
    once per pass rather than re-derived on every which_repo call."""
    db = open_db(tmp_path / "i.db")
    try:
        zlib = _repo(db, 1, "g/zlib")
        ft = _repo(db, 2, "g/freetype")
        for name in ("inflate.c", "deflate.c", "zutil.c", "crc32.c", "adler32.c"):
            _mkfile(db, zlib, name)
            _mkfile(db, ft, f"src/gzip/{name}", sha=f"m-{name}")
        db.commit()

        resolve.resolve_includes(db)
        vendored = {r["path"] for r in
                    db.execute("SELECT path FROM files WHERE is_vendored = 1")}
        assert vendored == {f"src/gzip/{n}" for n in
                            ("inflate.c", "deflate.c", "zutil.c", "crc32.c", "adler32.c")}
    finally:
        db.close()


def test_a_rerun_clears_a_stale_vendored_mark(tmp_path):
    """A directory that stops looking vendored must stop being marked, or the
    flag decays into a permanent wrong answer."""
    db = open_db(tmp_path / "i.db")
    try:
        a = _repo(db, 1, "g/a")
        _mkfile(db, a, "src/vendor/x.c")
        db.execute("UPDATE files SET is_vendored = 1")
        db.commit()

        resolve.resolve_includes(db)
        assert db.execute(
            "SELECT COUNT(*) FROM files WHERE is_vendored = 1").fetchone()[0] == 0
    finally:
        db.close()


def test_a_tiny_directory_is_never_enough_evidence(tmp_path):
    """Here the share ratio is a perfect 1.0 -- every name in the directory is
    held by another repo, nearer its root. Only the absolute floor rejects it.

    Three shared names is not a bundled library, it is `config.h`, `util.h`
    and `error.c`. Share alone cannot express that, because a small directory
    reaches 100% overlap by accident; the two thresholds fail differently and
    both are load-bearing.
    """
    db = open_db(tmp_path / "i.db")
    try:
        a = _repo(db, 1, "g/a")
        b = _repo(db, 2, "g/b")
        for name in ("config.h", "util.h", "error.c"):
            _mkfile(db, a, name)
            _mkfile(db, b, f"src/compat/{name}", sha=f"b-{name}")
        db.commit()

        rows = [(r["id"], r["repo_id"], r["path"])
                for r in db.execute("SELECT id, repo_id, path FROM files")]
        assert (b, "src/compat") not in resolve.find_vendored_dirs(rows)
    finally:
        db.close()


def test_a_standard_library_header_is_external_even_if_a_repo_ships_one(tmp_path):
    """The dominant defect in the whole cross-repo graph, measured on nine
    real projects: 90.1% of cross-repo resolutions were system headers.

    postgres ships src/include/port/win32/ -- POSIX shims named sys/socket.h,
    netdb.h, dlfcn.h -- and src/include/common/string.h. EXTERNAL was only
    reached when *nothing* matched, so every `#include <string.h>` in the
    corpus landed in postgres: 503 of them from openssl alone, inventing an
    openssl -> postgres edge at weight 555.
    """
    db = open_db(tmp_path / "i.db")
    try:
        pg = _repo(db, 1, "g/postgres")
        ssl = _repo(db, 2, "g/openssl")
        _file(db, pg, "src/include/common/string.h")
        _file(db, pg, "src/include/port/win32/sys/socket.h")
        src = _file(db, ssl, "apps/asn1parse.c")
        _include(db, ssl, src, "string.h", is_angle=1)
        _include(db, ssl, src, "sys/socket.h", is_angle=1)
        db.commit()

        counts = resolve.resolve_includes(db)
        assert counts[resolve.Resolution.EXTERNAL] == 2
        rows = db.execute("SELECT resolved_repo_id, resolution FROM includes").fetchall()
        assert all(r["resolved_repo_id"] is None for r in rows), \
            "a libc header was attributed to an indexed repository"
        assert db.execute("SELECT COUNT(*) FROM repo_deps").fetchone()[0] == 0
    finally:
        db.close()


def test_a_real_library_header_still_resolves(tmp_path):
    """The system list must not swallow project headers. zlib.h and png.h are
    exactly the includes the graph exists to capture, and both sit one
    character away in shape from the names being excluded."""
    db = open_db(tmp_path / "i.db")
    try:
        zlib = _repo(db, 1, "g/zlib")
        png = _repo(db, 2, "g/libpng")
        hdr = _file(db, zlib, "zlib.h")
        src = _file(db, png, "png.c")
        _include(db, png, src, "zlib.h", is_angle=1)
        db.commit()

        resolve.resolve_includes(db)
        row = db.execute("SELECT resolved_file_id, resolved_repo_id, resolution "
                         "FROM includes").fetchone()
        assert row["resolved_file_id"] == hdr
        assert row["resolved_repo_id"] == zlib
        assert row["resolution"] == resolve.Resolution.RESOLVED
    finally:
        db.close()


def test_more_local_candidates_must_not_push_the_answer_into_another_repo(tmp_path):
    """Measured: postgres's vendored snowball code does #include "header.h",
    and postgres ships two of them. Because there were *two*, the same-repo
    branch (which required exactly one) was skipped and a global shortest-path
    tiebreak ran -- handing the include to curl's include/curl/header.h at
    depth 2. Fifty includes, one fabricated postgres -> curl dependency.

    Having more local evidence made the answer worse.
    """
    db = open_db(tmp_path / "i.db")
    try:
        pg = _repo(db, 1, "g/postgres")
        curl = _repo(db, 2, "g/curl")
        _file(db, pg, "src/include/snowball/header.h")
        _file(db, pg, "src/include/snowball/libstemmer/header.h")
        _file(db, curl, "include/curl/header.h")          # shallower!
        src = _file(db, pg, "src/backend/snowball/libstemmer/api.c")
        _include(db, pg, src, "header.h")
        db.commit()

        resolve.resolve_includes(db)
        row = db.execute("SELECT resolved_repo_id FROM includes").fetchone()
        assert row["resolved_repo_id"] != curl, "resolved into an unrelated repo"
    finally:
        db.close()


def test_a_repo_that_owns_a_platform_shaped_name_still_resolves_it(tmp_path):
    """`param.h`, `stat.h` and `types.h` are platform headers *and* plausible
    project headers. Excluding them outright would delete real intra-repo
    edges, so the platform list is consulted only when the including repo has
    no candidate of its own -- which is what makes it safe to be generous."""
    db = open_db(tmp_path / "i.db")
    try:
        mine = _repo(db, 1, "g/mine")
        other = _repo(db, 2, "g/other")
        hdr = _file(db, mine, "include/param.h")
        # Deliberately SHALLOWER than the local one, so the depth tiebreak
        # would hand the include to the other repo. Only the same-repo
        # restriction prevents that -- with the local file at the shallower
        # depth the test passes either way and proves nothing.
        _file(db, other, "param.h")
        src = _file(db, mine, "src/main.c")
        _include(db, mine, src, "param.h", is_angle=1)
        db.commit()

        resolve.resolve_includes(db)
        row = db.execute("SELECT resolved_file_id, resolution FROM includes").fetchone()
        assert row["resolved_file_id"] == hdr
        assert row["resolution"] == resolve.Resolution.RESOLVED
    finally:
        db.close()


def test_a_bare_platform_name_does_not_reach_into_another_repo(tmp_path):
    """openssl's include/internal/sockets.h includes <socket.h> and <in.h>
    for VMS, where the platform puts them at the top of the include path.
    Both matched postgres's src/include/port/win32/ shims by basename."""
    db = open_db(tmp_path / "i.db")
    try:
        pg = _repo(db, 1, "g/postgres")
        ssl = _repo(db, 2, "g/openssl")
        _file(db, pg, "src/include/port/win32/sys/socket.h")
        _file(db, pg, "src/include/port/win32/netinet/in.h")
        src = _file(db, ssl, "include/internal/sockets.h")
        _include(db, ssl, src, "socket.h", is_angle=1)
        _include(db, ssl, src, "in.h", is_angle=1)
        db.commit()

        counts = resolve.resolve_includes(db)
        assert counts[resolve.Resolution.EXTERNAL] == 2, counts
        rows = db.execute("SELECT resolved_repo_id, resolution FROM includes").fetchall()
        assert all(r["resolved_repo_id"] is None for r in rows),             "a platform header reached into another repository"
    finally:
        db.close()
