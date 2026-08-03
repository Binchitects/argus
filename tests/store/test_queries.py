import inspect
import sqlite3

import pytest

from argus import whichrepo
from argus.resolve import Resolution
from argus.store import graph
from argus.store.db import open_db
from argus.store import writes, queries


def _public_query_functions():
    """Every public function in queries.py, found by reflection.

    New query functions (Phase 2 Task 5+: find_references, MCP-tool-backing
    queries) are picked up automatically -- nobody has to remember to add
    them to a list here.
    """
    return [
        (name, fn) for name, fn in inspect.getmembers(queries, inspect.isfunction)
        if not name.startswith("_") and fn.__module__ == queries.__name__
    ]


def _minimal_args_for(name, conn, target_repo_id):
    """The extra keyword args each query needs to return something for
    ``target_repo_id``, beyond ``allowed_repo_ids`` and ``conn``.

    Written explicitly, one branch per function that exists today. An
    unknown name raises rather than being silently skipped: a function
    added later must be given real arguments here -- deliberately -- or
    the whole test suite fails loudly, instead of quietly not testing it.
    """
    if name == "find_symbol":
        # Both repos in `two_repos` have a symbol named SharedName, so
        # switching the allowlist switches which repo's row comes back.
        return {"name": "SharedName"}
    if name == "search_code":
        # search_code has no repo_id argument to target with directly, so
        # look up target_repo_id's actual file content and search for that --
        # it will not appear in the other repo's content, so the allowlist
        # is the only thing that can make it findable.
        # Pin to src/a.c explicitly. Its content is a single plain word, unique
        # per repo -- exactly what this branch needs. The other two files added
        # in Task 5 are byte-identical across repos AND full of punctuation
        # ({ } ( )), which as a raw FTS5 MATCH query can raise. Selecting
        # without an ORDER BY once there was more than one file per repo would
        # have left which content came back to b-tree scan order.
        row = conn.execute(
            "SELECT content FROM files WHERE repo_id = ? AND path = 'src/a.c'",
            (target_repo_id,),
        ).fetchone()
        return {"query": row["content"]}
    if name == "get_file":
        return {"repo_id": target_repo_id, "path": "src/a.c"}
    if name == "index_status":
        # No arguments beyond allowed_repo_ids and conn.
        return {}
    if name == "find_references":
        # Both repos in `two_repos` get an identical src/def.c defining
        # SharedFunc and an identical src/caller.c calling it -- the text is
        # byte-for-byte the same in both repos, so switching the allowlist
        # is the *only* thing that can change which repo's occurrences come
        # back (same collision strategy as find_symbol's SharedName/a.c).
        return {"name": "SharedFunc"}
    if name == "repo_map":
        # repo_map's third positional parameter is target_repo_id itself, so
        # no cross-repo graph state is needed for this generic filtering
        # check: an allowlist that excludes target_repo_id must return {}, and
        # one that includes it must return a non-empty dict naming it.
        return {"repo_id": target_repo_id}
    if name == "which_repo":
        # Both repos in `two_repos` define a symbol named SharedName (same
        # collision strategy as find_symbol), so switching the allowlist
        # switches which repo's row comes back.
        return {"description": "SharedName"}
    raise NotImplementedError(
        f"_minimal_args_for has no branch for {name!r}. A newly added public "
        "query function must be given deliberate arguments here before this "
        "test can trust that it actually filters by allowed_repo_ids."
    )


@pytest.mark.parametrize("name,fn", _public_query_functions())
def test_every_public_query_takes_allowlist_first(name, fn):
    """The allowlist must be the first positional parameter, with no default.

    This is a distinct property from `..._actually_filters` below, and neither
    subsumes the other. Filtering proves the parameter is *used*; this proves it
    cannot be *omitted* -- Python itself rejects a call that leaves it out, so a
    query added later fails at the call site rather than silently running with
    whatever a default would supply. The design calls this converting a runtime
    vulnerability into an import-time error; keep both tests.
    """
    params = list(inspect.signature(fn).parameters.values())
    assert params, f"{name} takes no parameters at all"
    assert params[0].name == "allowed_repo_ids", (
        f"{name}'s first parameter is {params[0].name!r}, not allowed_repo_ids"
    )
    assert params[0].default is inspect.Parameter.empty, (
        f"{name}'s allowed_repo_ids has a default -- a caller can omit it"
    )
    assert params[0].kind in (
        inspect.Parameter.POSITIONAL_ONLY,
        inspect.Parameter.POSITIONAL_OR_KEYWORD,
    ), f"{name}'s allowed_repo_ids is not positional"


@pytest.mark.parametrize("name,fn", _public_query_functions())
def test_every_public_query_actually_filters(name, fn, two_repos):
    """Declaring the allowlist parameter is not enough -- it must change
    the result. A function that accepts allowed_repo_ids and never
    references it would pass a signature-only check while leaking every
    repo's data to every caller; this proves that cannot happen.
    """
    conn, ids = two_repos
    a, b = ids["g/alpha"], ids["g/beta"]
    kwargs = _minimal_args_for(name, conn, b)
    allowed_none = fn([], conn, **kwargs)
    allowed_wrong = fn([a], conn, **kwargs)
    allowed_right = fn([b], conn, **kwargs)
    assert not allowed_none, f"{name} returned data for an empty allowlist"
    assert allowed_wrong != allowed_right, (
        f"{name} returns the same data regardless of the allowlist "
        "-- it is not filtering"
    )


DEF_C = (
    "int SharedFunc(void) {\n"
    "    return 1;\n"
    "}\n"
)

CALLER_C = (
    "void useIt(void) {\n"
    "    SharedFunc();\n"
    "    SharedFuncV2();\n"
    "}\n"
)


@pytest.fixture
def two_repos(tmp_path):
    conn = open_db(tmp_path / "i.db")
    ids = {}
    for gid, ns, word in ((1, "g/alpha", "alphaword"), (2, "g/beta", "betaword")):
        rid = writes.upsert_repo(conn, gitlab_id=gid, path_with_namespace=ns,
                                 default_branch="main", http_url=f"https://x/{ns}")
        fid = writes.upsert_file(conn, repo_id=rid, path="src/a.c", lang="c",
                                 size=len(word), blob_sha=f"sha{gid}", content=word)
        writes.replace_symbols(conn, rid, fid, [
            {"name": "SharedName", "kind": "function", "line": 1, "end_line": 2,
             "signature": "(void)", "scope": None, "is_public": 1},
        ], f"sha{gid}")
        writes.set_last_indexed(conn, rid, f"sha{gid}", 1000 + gid)
        ids[ns] = rid

        # Additive extension for find_references (Task 5): a definition file
        # and a caller file, with byte-identical content in both repos --
        # required so the generic allowlist-filtering test (which drives
        # find_references through _minimal_args_for) has no unique-content
        # shortcut to pass by accident, exactly as SharedName/src/a.c already
        # do for find_symbol. src/a.c itself is untouched: several existing
        # tests (e.g. test_get_file_not_truncated_reports_false) assert its
        # content equals the bare word exactly.
        def_fid = writes.upsert_file(conn, repo_id=rid, path="src/def.c", lang="c",
                                     size=len(DEF_C), blob_sha=f"def{gid}", content=DEF_C)
        writes.replace_symbols(conn, rid, def_fid, [
            {"name": "SharedFunc", "kind": "function", "line": 1, "end_line": 3,
             "signature": "(void)", "scope": None, "is_public": 1},
        ], f"def{gid}")
        writes.upsert_file(conn, repo_id=rid, path="src/caller.c", lang="c",
                           size=len(CALLER_C), blob_sha=f"caller{gid}", content=CALLER_C)
    return conn, ids


def test_find_symbol_excludes_disallowed_repo(two_repos):
    conn, ids = two_repos
    rows = queries.find_symbol([ids["g/alpha"]], conn, "SharedName")
    assert len(rows) == 1
    assert rows[0]["repo_id"] == ids["g/alpha"]


def test_find_symbol_kind_filter(two_repos):
    conn, ids = two_repos
    rid = ids["g/alpha"]
    fid = conn.execute(
        "SELECT id FROM files WHERE repo_id = ?", (rid,)
    ).fetchone()["id"]
    writes.replace_symbols(conn, rid, fid, [
        {"name": "SharedName", "kind": "function", "line": 1, "end_line": 2,
         "signature": "(void)", "scope": None, "is_public": 1},
        {"name": "SharedName", "kind": "variable", "line": 10, "end_line": 10,
         "signature": None, "scope": None, "is_public": 1},
    ], "sha1")

    both = queries.find_symbol([rid], conn, "SharedName")
    assert {r["kind"] for r in both} == {"function", "variable"}

    only_variable = queries.find_symbol([rid], conn, "SharedName", kind="variable")
    assert [r["kind"] for r in only_variable] == ["variable"]


def test_search_code_excludes_disallowed_repo(two_repos):
    conn, ids = two_repos
    assert queries.search_code([ids["g/alpha"]], conn, "betaword") == []
    assert len(queries.search_code([ids["g/beta"]], conn, "betaword")) == 1


def test_get_file_refuses_disallowed_repo(two_repos):
    conn, ids = two_repos
    assert queries.get_file([ids["g/alpha"]], conn, ids["g/beta"], "src/a.c") is None
    assert queries.get_file([ids["g/beta"]], conn, ids["g/beta"], "src/a.c") is not None


def test_get_file_empty_allowlist_returns_none(two_repos):
    conn, ids = two_repos
    assert queries.get_file([], conn, ids["g/alpha"], "src/a.c") is None


def test_index_status_excludes_disallowed_repo(two_repos):
    conn, ids = two_repos
    rows = queries.index_status([ids["g/beta"]], conn)
    assert [r["path_with_namespace"] for r in rows] == ["g/beta"]


def test_empty_allowlist_returns_nothing_not_everything(two_repos):
    conn, _ = two_repos
    assert queries.find_symbol([], conn, "SharedName") == []
    assert queries.search_code([], conn, "alphaword") == []
    assert queries.index_status([], conn) == []


def test_allowlist_must_be_a_sequence_of_ints(two_repos):
    conn, ids = two_repos
    with pytest.raises(TypeError):
        queries.find_symbol("all", conn, "SharedName")
    with pytest.raises(TypeError):
        queries.find_symbol([None], conn, "SharedName")


def test_index_status_reports_last_run_flags(two_repos):
    conn, ids = two_repos
    rid = ids["g/alpha"]
    writes.record_run_state(conn, rid, timed_out=True, symbols_failed=False, ts=1234)
    row = [r for r in queries.index_status([rid], conn)][0]
    assert row["last_run_timed_out"] == 1
    assert row["last_run_symbols_failed"] == 0


def test_index_status_reports_queued_retries(two_repos):
    """queued_retries must count queued PATHS, not index_queue rows.

    index_queue.repo_id is a PRIMARY KEY -- one row per repo, with the paths
    JSON-packed into `reason` -- so COUNT(*) is structurally bounded at 1 and
    a repo with 4,000 stuck paths reported "1". Phase 2 exposes this column
    to a model that will read it as a file count, so assert exact numbers:
    `>= 1` would be satisfied by a literal SELECT 1.
    """
    conn, ids = two_repos
    rid = ids["g/beta"]

    def queued():
        return [r for r in queries.index_status([rid], conn)][0]["queued_retries"]

    assert queued() == 0, "no index_queue row at all must report 0, not NULL"

    writes.enqueue_retry(conn, rid, ["a.c"], "read error", 1234)
    assert queued() == 1

    writes.enqueue_retry(conn, rid, ["a.c", "b.c", "c.c", "d.c"], "read error", 1235)
    assert queued() == 4

    # A row whose reason is not the JSON payload (a hand-written entry, or one
    # written before the payload format existed) must degrade to 0 rather than
    # abort the whole status query with a malformed-JSON error.
    conn.execute("UPDATE index_queue SET reason = 'legacy free text' WHERE repo_id = ?",
                 (rid,))
    conn.commit()
    assert queued() == 0


def test_allowlist_larger_than_sqlite_parameter_limit(two_repos):
    """A developer in a large GitLab group must not raise OperationalError.

    Fail-closed semantics mean an exception here would be an availability bug
    wearing a security costume: the caller cannot distinguish "the store
    blew up" from "you are denied". The allowlist must simply work, and the
    one row that is genuinely allowed must be the one that comes back.

    SQLite's *documented default* SQLITE_MAX_VARIABLE_NUMBER is 999, but
    builds since 3.32.0 (Dec 2019) default to 32766 -- this repo's own
    sqlite3 measures 32766, not 999. A fixed literal like 1500 would pass
    against both the broken and the fixed code on such a build, proving
    nothing. Query the real compiled-in limit via Connection.getlimit and
    exceed *that*, so this test cannot pass by accident.
    """
    conn, ids = two_repos
    host_limit = conn.getlimit(sqlite3.SQLITE_LIMIT_VARIABLE_NUMBER)
    big = list(range(10_000_000, 10_000_000 + host_limit + 500)) + [ids["g/alpha"]]
    rows = queries.find_symbol(big, conn, "SharedName")
    assert len(rows) == 1 and rows[0]["repo_id"] == ids["g/alpha"]


def test_malformed_fts_query_returns_actionable_error(two_repos):
    conn, ids = two_repos
    with pytest.raises(queries.QueryError, match="search syntax"):
        queries.search_code([ids["g/alpha"]], conn, 'unbalanced "quote')


def test_get_file_truncates_and_says_so(two_repos):
    conn, ids = two_repos
    row = queries.get_file([ids["g/alpha"]], conn, ids["g/alpha"], "src/a.c", max_bytes=4)
    assert len(row["content"]) <= 64
    assert row["truncated"] is True


def test_get_file_not_truncated_reports_false(two_repos):
    """The truncated flag must be a real signal, not always True."""
    conn, ids = two_repos
    row = queries.get_file([ids["g/alpha"]], conn, ids["g/alpha"], "src/a.c")
    assert row["truncated"] is False
    assert row["content"] == "alphaword"


def test_get_file_refusal_paths_return_exactly_none(two_repos):
    """A denial must never be confused with a hit.

    get_file used to return sqlite3.Row | None; callers check truthiness.
    If a refused lookup returned an empty dict or an error-carrying dict
    instead of None, every one of those truthiness checks would silently
    invert and a denial would read as a successful fetch. Both refusal
    paths -- disallowed repo and empty allowlist -- must return the
    identical `None` object, not merely something falsy.
    """
    conn, ids = two_repos
    disallowed = queries.get_file([ids["g/alpha"]], conn, ids["g/beta"], "src/a.c")
    empty = queries.get_file([], conn, ids["g/alpha"], "src/a.c")
    assert disallowed is None
    assert empty is None
    assert not isinstance(disallowed, dict)
    assert not isinstance(empty, dict)


def test_index_status_queued_retries_is_per_repo(two_repos):
    """One repo's queue must not be counted against another's."""
    conn, ids = two_repos
    writes.enqueue_retry(conn, ids["g/alpha"], ["a.c", "b.c", "c.c"], "read error", 1)
    writes.enqueue_retry(conn, ids["g/beta"], ["z.c"], "read error", 1)
    rows = {r["path_with_namespace"]: r["queued_retries"]
            for r in queries.index_status(list(ids.values()), conn)}
    assert rows == {"g/alpha": 3, "g/beta": 1}


def test_find_references_finds_caller_in_another_file(two_repos):
    conn, ids = two_repos
    rows = queries.find_references([ids["g/alpha"]], conn, "SharedFunc")
    paths = {r["path"] for r in rows}
    assert "src/def.c" in paths
    assert "src/caller.c" in paths, "the call site in a different file was not found"


def test_find_references_marks_definition_site(two_repos):
    conn, ids = two_repos
    rows = queries.find_references([ids["g/alpha"]], conn, "SharedFunc")
    by_path = {r["path"]: r for r in rows}

    definition = by_path["src/def.c"]
    assert definition["is_definition"] is True
    assert definition["line"] == 1
    assert definition["repo"] == "g/alpha"
    assert "SharedFunc" in definition["context"]

    call_site = by_path["src/caller.c"]
    assert call_site["is_definition"] is False


def test_find_references_excludes_disallowed_repo(two_repos):
    conn, ids = two_repos
    rows = queries.find_references([ids["g/alpha"]], conn, "SharedFunc")
    assert rows, "expected occurrences in g/alpha"
    assert all(r["repo"] == "g/alpha" for r in rows), (
        "a row from g/beta leaked through an allowlist that only names g/alpha"
    )


def test_find_references_unknown_name_returns_empty(two_repos):
    conn, ids = two_repos
    assert queries.find_references([ids["g/alpha"]], conn, "NoSuchIdentifierXYZ") == []


def test_find_references_does_not_match_substring_of_longer_identifier(two_repos):
    """`SharedFunc` must not match the `SharedFuncV2` call on caller.c's next line.

    src/caller.c (see CALLER_C) has "SharedFunc();" on line 2 and
    "SharedFuncV2();" on line 3, in the *same already-shortlisted* file --
    this is exactly the shape that catches a naive substring/`in` scan of
    the FTS-shortlisted lines instead of a real `\\b` word-boundary match.
    """
    conn, ids = two_repos
    rows = queries.find_references([ids["g/alpha"]], conn, "SharedFunc")
    caller_lines = sorted(r["line"] for r in rows if r["path"] == "src/caller.c")
    assert caller_lines == [2], (
        f"expected only line 2 (bare SharedFunc) in src/caller.c, got {caller_lines} "
        "-- line 3 is SharedFuncV2 and must not match"
    )
    assert not any("SharedFuncV2" in r["context"] for r in rows if r["path"] == "src/caller.c" and r["line"] != 3)


def test_find_references_honours_limit(two_repos):
    conn, ids = two_repos
    rid = ids["g/alpha"]
    many_content = "".join(f"SharedFunc(); // call {i}\n" for i in range(10))
    writes.upsert_file(conn, repo_id=rid, path="src/many.c", lang="c",
                       size=len(many_content), blob_sha="many1", content=many_content)

    unlimited = queries.find_references([rid], conn, "SharedFunc", limit=1000)
    assert len(unlimited) >= 12  # 1 (def.c) + 1 (caller.c) + 10 (many.c)

    limited = queries.find_references([rid], conn, "SharedFunc", limit=5)
    assert len(limited) == 5


def _cross_repo_include(conn, ids) -> None:
    """Make g/alpha depend on g/beta.

    The `two_repos` fixture seeds no includes at all, so without this every
    edge assertion below would pass over an empty graph -- proving nothing.
    """
    alpha, beta = ids["g/alpha"], ids["g/beta"]
    src = conn.execute("SELECT id FROM files WHERE repo_id = ? LIMIT 1",
                       (alpha,)).fetchone()["id"]
    hdr = conn.execute("SELECT id FROM files WHERE repo_id = ? LIMIT 1",
                       (beta,)).fetchone()["id"]
    conn.execute(
        "INSERT INTO includes (repo_id, file_id, raw, is_angle, "
        "resolved_file_id, resolved_repo_id, is_external, resolution) "
        "VALUES (?, ?, 'shared.h', 0, ?, ?, 0, ?)",
        (alpha, src, hdr, beta, Resolution.RESOLVED))
    conn.commit()


def test_repo_map_reports_both_directions(two_repos):
    conn, ids = two_repos
    _cross_repo_include(conn, ids)
    graph.rebuild_repo_deps(conn)
    result = queries.repo_map([ids["g/alpha"], ids["g/beta"]], conn, ids["g/alpha"])
    assert result["repo"]["repo_id"] == ids["g/alpha"]
    assert {d["repo_id"] for d in result["depends_on"]} == {ids["g/beta"]}


def test_repo_map_hides_edges_touching_repos_outside_the_allowlist(two_repos):
    """A developer who can see alpha but not beta must not learn that beta
    exists, or that anything depends on it. Filtering happens at query time
    against one shared graph."""
    conn, ids = two_repos
    _cross_repo_include(conn, ids)
    graph.rebuild_repo_deps(conn)

    visible = queries.repo_map([ids["g/alpha"]], conn, ids["g/alpha"])
    assert visible["repo"]["repo_id"] == ids["g/alpha"], "non-empty guard"
    assert visible["depends_on"] == []
    assert visible["depended_on_by"] == []
    assert str(ids["g/beta"]) not in repr(visible)


def test_repo_map_on_a_repo_outside_the_allowlist_is_empty(two_repos):
    conn, ids = two_repos
    assert queries.repo_map([ids["g/alpha"]], conn, ids["g/beta"]) == {}


def test_repo_map_with_no_graph_built_yet_is_empty_not_an_error(two_repos):
    """Before the first resolution pass repo_deps is empty. That is a valid
    state, not a failure."""
    conn, ids = two_repos
    result = queries.repo_map([ids["g/alpha"]], conn, ids["g/alpha"])
    assert result["depends_on"] == []


def test_which_repo_finds_the_repo_defining_a_named_symbol(two_repos):
    conn, ids = two_repos
    rows = queries.which_repo([ids["g/alpha"], ids["g/beta"]], conn, "SharedName")
    assert rows, "no candidates: the assertions below would be vacuous"
    assert rows[0]["shape"] == "symbol"
    assert rows[0]["why"], "every candidate must carry its evidence"


def test_which_repo_uses_paths_named_in_a_diff(two_repos):
    conn, ids = two_repos
    path = conn.execute("SELECT path FROM files WHERE repo_id = ?",
                        (ids["g/alpha"],)).fetchone()["path"]
    diff = f"diff --git a/{path} b/{path}\n@@ -1 +1 @@\n+int x;\n"

    rows = queries.which_repo([ids["g/alpha"], ids["g/beta"]], conn, diff)
    assert rows
    assert rows[0]["repo_id"] == ids["g/alpha"]
    assert rows[0]["shape"] == "diff"


def test_which_repo_returns_empty_when_nothing_clears_the_floor(two_repos):
    """A ranked list of weak matches looks like an answer, and a 35B model
    acts on the top row."""
    conn, ids = two_repos
    assert queries.which_repo([ids["g/alpha"]], conn, "zzz_no_such_thing_anywhere") == []


def test_which_repo_never_reveals_a_repo_outside_the_allowlist(two_repos):
    conn, ids = two_repos
    rows = queries.which_repo([ids["g/alpha"]], conn, "SharedName")
    assert rows, "non-empty guard"
    assert all(r["repo_id"] == ids["g/alpha"] for r in rows)


def test_a_direct_hit_is_not_penalised_for_being_a_popular_repo(two_repos):
    """Down-weighting high in-degree repos is right for prose and wrong when
    evidence points directly into the shared library, where that library
    genuinely is the answer.

    The shared `two_repos` fixture cannot express this on its own: it seeds
    the *same* relative paths in both alpha and beta, so a path-based direct
    hit ties 1-for-1 between them and there is no signal left to break the
    tie toward the popular one. It's also worth noting `_WEIGHTS` gives
    Shape.STACK/DIFF/SYMBOL a central weight of 0.0, so the `if not hits:`
    guard in `which_repo` is only ever load-bearing for Shape.PROSE (central
    weight 0.3) -- this test therefore has to use prose-shaped input that
    still contains a directly-named file, rather than a pure stack trace.

    This test builds the extra state the property needs, on top of the
    fixture: a file that exists ONLY in beta (so a direct hit on it can only
    ever mean beta), plus a cross-repo include from alpha -> beta so beta has
    non-zero in-degree, i.e. is the "popular" one.
    """
    conn, ids = two_repos
    alpha, beta = ids["g/alpha"], ids["g/beta"]

    # beta-only file: a direct hit here names beta and only beta.
    writes.upsert_file(conn, repo_id=beta, path="src/unique_beta.py", lang="python",
                       size=6, blob_sha="uniqueb", content="unique")

    # alpha depends on beta -- gives beta non-zero in-degree (it's "popular").
    _cross_repo_include(conn, ids)
    graph.rebuild_repo_deps(conn)

    # Prose (not a stack trace/diff/bare symbol): mentions the shared symbol
    # SharedName (a direct hit in both repos) and the beta-only path (a direct
    # hit in beta alone), so beta collects strictly more direct evidence than
    # alpha while both have at least one hit each.
    description = (
        "The team suspects SharedName is misbehaving, possibly tied to "
        "src/unique_beta.py:42, though nobody has filed a report yet."
    )
    assert whichrepo.detect_shape(description) == whichrepo.Shape.PROSE, (
        "test assumes prose shape -- central weight is 0.0 for every other "
        "shape, so the guard this test protects would not be exercised"
    )

    rows = queries.which_repo([alpha, beta], conn, description)
    assert rows
    assert rows[0]["repo_id"] == beta, (
        "beta is directly named here (src/unique_beta.py) and must not be "
        "down-ranked just because it also has incoming deps from alpha"
    )


def test_which_repo_does_not_treat_underscore_as_a_wildcard(two_repos):
    """SQLite's LIKE treats a bare `_` in the *bound value* as a wildcard
    (matching any one character), not just in literal pattern text. Two
    files in different repos whose paths differ only where a `_` sits must
    not both come back as evidence for a query naming one of them.

    This is not just an extra row: which_repo treats a _files_named result
    as a *direct* hit, and direct hits are exempt from the centrality
    penalty (`if not hits:`). A false match here both invents evidence for
    the wrong repo and disables the correction that would otherwise damp
    it, so a change can be attributed to a repo that was never actually
    named.
    """
    conn, ids = two_repos
    alpha, beta = ids["g/alpha"], ids["g/beta"]

    writes.upsert_file(conn, repo_id=alpha, path="src/unique_beta.c", lang="c",
                       size=6, blob_sha="ubeta-a", content="alpha!")
    # Differs from the file above only where a `_` sits (`X` instead of `_`).
    # Under an unescaped LIKE, that `_` in the bound value would match any
    # single character, so this unrelated file would falsely satisfy a query
    # for unique_beta.c.
    writes.upsert_file(conn, repo_id=beta, path="src/uniqueXbeta.c", lang="c",
                       size=5, blob_sha="ubeta-b", content="beta!")

    description = "Traceback (most recent call last):\n  at unique_beta.c:42\n"
    assert whichrepo.detect_shape(description) == whichrepo.Shape.STACK, (
        "test assumes stack shape, where a direct hit is unpenalised evidence"
    )

    rows = queries.which_repo([alpha, beta], conn, description)
    repo_ids_hit = {r["repo_id"] for r in rows}
    assert repo_ids_hit == {alpha}, (
        f"expected evidence attributed to only alpha, got {repo_ids_hit} -- "
        "an unescaped `_` in the LIKE pattern let src/uniqueXbeta.c in beta "
        "match a query for src/unique_beta.c"
    )


def test_confidence_is_between_zero_and_one(two_repos):
    conn, ids = two_repos
    rows = queries.which_repo([ids["g/alpha"], ids["g/beta"]], conn, "SharedName")
    assert rows
    assert all(0.0 <= r["confidence"] <= 1.0 for r in rows)
