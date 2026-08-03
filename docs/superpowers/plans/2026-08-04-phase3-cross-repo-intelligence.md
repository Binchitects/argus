# Phase 3 — Cross-repo Intelligence Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build `which_repo` and `repo_map` — the tools that answer "which of my repos do I change for this?" — by resolving cross-repo include edges into a dependency graph.

**Architecture:** Three layers. A resolution pass turns `includes.raw` strings into concrete `resolved_file_id`/`resolved_repo_id` links; a materialization step aggregates cross-repo edges into `repo_deps`; two allowlist-gated MCP tools read the result. No embeddings anywhere — the semantic score is a slot that contributes zero.

**Tech Stack:** Python 3.11+, SQLite (FTS5), `mcp>=1.9,<2.0`, pytest.

## Global Constraints

- **`allowed_repo_ids` is the first positional parameter, no default, on every public function in `argus/store/queries.py`.** No exceptions.
- **Never edit an applied migration** (`001`–`007`). New schema goes in `008_include_resolution.sql`.
- **Nothing in `argus/packs/` may be imported by this work, and nothing here may be imported by `argus/packs/`.** The two corpora stay separate.
- **Every test must be demonstrated failing under a *targeted* revert.** Deleting a whole function only proves the test calls it.
- **Assert non-emptiness before asserting isolation or disjointness.** A test over empty sets passes vacuously.
- **`which_repo` returns empty with a reason rather than a ranked list of weak matches.** A ranked list looks like an answer and a 35B model acts on the top row.
- **The centrality penalty applies only to inferred evidence, never to direct hits.**
- **Path comparison is case-sensitive**, matching git.
- Tools register through `register_tools` in `argus/mcpsrv/tools.py`, pull identity via `_identity(ctx)`, route sqlite through `run_readonly`, and record one audit row via `_with_audit`.

---

### Task 1: Migration 008 and resolution states

**Files:**
- Create: `argus/store/migrations/008_include_resolution.sql`
- Create: `argus/resolve.py`
- Test: `tests/test_resolve.py`

**Interfaces:**
- Produces: `argus.resolve.Resolution` — string constants `RESOLVED`, `EXTERNAL`, `AMBIGUOUS`, `NOT_FOUND`.

- [ ] **Step 1: Write the failing test**

```python
# tests/test_resolve.py
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
```

- [ ] **Step 2: Run test to verify it fails**

Run: `python -m pytest tests/test_resolve.py -v`
Expected: FAIL — `ModuleNotFoundError: No module named 'argus.resolve'`

- [ ] **Step 3: Write the migration**

```sql
-- argus/store/migrations/008_include_resolution.sql
-- Distinguishes an ambiguous include from an unfindable one. Both leave
-- resolved_file_id NULL, so without this column the resolution statistics an
-- operator needs ("34% of your includes are ambiguous" means your -I layout
-- defeats suffix matching) cannot be computed.
--
-- NULL means "never resolved", which is true of every row written before this
-- migration ran.
ALTER TABLE includes ADD COLUMN resolution TEXT;

CREATE INDEX IF NOT EXISTS idx_includes_resolution ON includes(resolution);
CREATE INDEX IF NOT EXISTS idx_includes_resolved_repo ON includes(resolved_repo_id);
```

- [ ] **Step 4: Write the constants**

```python
# argus/resolve.py
"""Resolve `#include` strings to concrete files, across repos.

A wrong edge here is invisible. It silently corrupts `repo_deps`, which feeds
the centrality term behind every `which_repo` answer, and nothing downstream
can tell a fabricated dependency from a real one. So this module guesses at
nothing: an include it cannot pin to exactly one file is recorded as
unresolved with a reason, and contributes no edge.
"""

from __future__ import annotations


class Resolution:
    """What happened to one include. Stored in `includes.resolution`."""

    #: Pinned to exactly one file in the index.
    RESOLVED = "resolved"
    #: A system or third-party header; no indexed file matches.
    EXTERNAL = "external"
    #: Several indexed files match and no tiebreak was decisive. No edge.
    AMBIGUOUS = "ambiguous"
    #: Quoted include naming a path nothing provides.
    NOT_FOUND = "not_found"
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `python -m pytest tests/test_resolve.py -v`
Expected: PASS (3 passed)

- [ ] **Step 6: Run the full suite**

Run: `python -m pytest -q`
Expected: all pass, 0 skipped

- [ ] **Step 7: Commit**

```bash
git add argus/resolve.py argus/store/migrations/008_include_resolution.sql tests/test_resolve.py
git commit -m "feat: add includes.resolution column and resolution states"
```

---

### Task 2: Boundary-aligned suffix index

**Files:**
- Modify: `argus/resolve.py`
- Test: `tests/test_resolve.py`

**Interfaces:**
- Consumes: `Resolution` from Task 1.
- Produces:
  - `build_suffix_index(rows: Iterable[tuple[int, int, str]]) -> dict[str, list[tuple[int, int, str]]]` — rows are `(file_id, repo_id, path)`; the key is each `/`-aligned suffix, the value is the matching files.
  - `path_suffixes(path: str) -> list[str]` — `"src/eal/x.h"` → `["src/eal/x.h", "eal/x.h", "x.h"]`.

This task is pure functions with no database. It exists separately because the boundary-alignment rule is the single defect most likely to corrupt the graph, and it deserves its own test cycle.

- [ ] **Step 1: Write the failing test**

```python
# tests/test_resolve.py  (append)
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
```

- [ ] **Step 2: Run test to verify it fails**

Run: `python -m pytest tests/test_resolve.py -v`
Expected: FAIL — `AttributeError: module 'argus.resolve' has no attribute 'path_suffixes'`

- [ ] **Step 3: Implement**

```python
# argus/resolve.py  (append)
from collections import defaultdict
from typing import Iterable

#: (file_id, repo_id, path)
FileRow = tuple[int, int, str]


def path_suffixes(path: str) -> list[str]:
    """Every `/`-aligned suffix of `path`, longest first.

    Alignment is the whole point. Indexing raw string suffixes would let
    `eal_thread.h` match `not_eal_thread.h`, and the resulting edge would be
    wrong, permanent, and invisible.
    """
    parts = path.split("/")
    return ["/".join(parts[i:]) for i in range(len(parts))]


def build_suffix_index(rows: Iterable[FileRow]) -> dict[str, list[FileRow]]:
    """Map each `/`-aligned suffix to the files that end with it."""
    index: dict[str, list[FileRow]] = defaultdict(list)
    for row in rows:
        for suffix in path_suffixes(row[2]):
            index[suffix].append(row)
    return dict(index)
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `python -m pytest tests/test_resolve.py -v`
Expected: PASS (8 passed)

- [ ] **Step 5: Demonstrate the boundary test discriminates**

Temporarily change `path_suffixes` to return raw string suffixes:

```python
    return [path[i:] for i in range(len(path))]
```

Run: `python -m pytest tests/test_resolve.py -v`
Expected: FAIL on `test_a_longer_name_does_not_match_a_shorter_one` and `test_directory_prefixes_do_not_match_either`. Restore the correct implementation afterwards.

- [ ] **Step 6: Commit**

```bash
git add argus/resolve.py tests/test_resolve.py
git commit -m "feat: add boundary-aligned path suffix index for include resolution"
```

---

### Task 3: The resolution pass

**Files:**
- Modify: `argus/resolve.py`
- Test: `tests/test_resolve.py`

**Interfaces:**
- Consumes: `build_suffix_index`, `path_suffixes`, `Resolution`.
- Produces: `resolve_includes(conn) -> dict[str, int]` — resolves every include in the database, writes `resolved_file_id`, `resolved_repo_id`, `is_external`, `resolution`, and returns counts keyed by resolution state.

- [ ] **Step 1: Write the failing test**

```python
# tests/test_resolve.py  (append)
import pytest
from argus.store.db import open_db


@pytest.fixture
def db(tmp_path):
    conn = open_db(tmp_path / "index.db")
    yield conn
    conn.close()


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


def test_a_unique_suffix_match_resolves_across_repos(db):
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


def test_an_ambiguous_include_emits_no_link_and_is_counted(db):
    """util.h exists in a dozen repos. Choosing the most likely one produces
    an edge that is invisible when wrong, permanent, and feeds the centrality
    behind every future answer."""
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


def test_same_repo_wins_over_a_foreign_match(db):
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


def test_a_quoted_relative_include_resolves_against_its_own_directory(db):
    a = _repo(db, 1, "g/app")
    src = _file(db, a, "src/eal/x.c")
    target = _file(db, a, "src/common/util.h")
    _include(db, a, src, "../common/util.h")
    db.commit()

    resolve.resolve_includes(db)
    row = db.execute("SELECT resolved_file_id, resolution FROM includes").fetchone()
    assert row["resolved_file_id"] == target
    assert row["resolution"] == resolve.Resolution.RESOLVED


def test_an_unmatched_angle_include_is_external(db):
    a = _repo(db, 1, "g/app")
    src = _file(db, a, "src/main.c")
    _include(db, a, src, "stdio.h", is_angle=1)
    db.commit()

    counts = resolve.resolve_includes(db)
    assert counts[resolve.Resolution.EXTERNAL] == 1
    row = db.execute("SELECT is_external, resolution FROM includes").fetchone()
    assert row["is_external"] == 1
    assert row["resolution"] == resolve.Resolution.EXTERNAL


def test_an_angle_include_that_matches_an_indexed_file_is_internal(db):
    """C projects routinely #include <eal/x.h> via -I. Treating every angle
    include as external would erase most of the graph."""
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


def test_an_unmatched_quoted_include_is_not_found(db):
    a = _repo(db, 1, "g/app")
    src = _file(db, a, "src/main.c")
    _include(db, a, src, "nowhere/missing.h")
    db.commit()

    counts = resolve.resolve_includes(db)
    assert counts[resolve.Resolution.NOT_FOUND] == 1


def test_resolution_is_independent_of_insertion_order(db):
    """An include can point into a repo indexed later in the same cycle.
    Resolving per-repo would make the graph depend on indexing order."""
    b = _repo(db, 2, "g/eal")
    a = _repo(db, 1, "g/app")
    src = _file(db, a, "src/main.c")
    hdr = _file(db, b, "include/eal/x.h")
    _include(db, a, src, "eal/x.h")
    db.commit()

    resolve.resolve_includes(db)
    row = db.execute("SELECT resolved_file_id FROM includes").fetchone()
    assert row["resolved_file_id"] == hdr


def test_rerunning_resolution_is_idempotent(db):
    a = _repo(db, 1, "g/app")
    b = _repo(db, 2, "g/eal")
    src = _file(db, a, "src/main.c")
    _file(db, b, "include/eal/x.h")
    _include(db, a, src, "eal/x.h")
    db.commit()

    first = resolve.resolve_includes(db)
    second = resolve.resolve_includes(db)
    assert first == second
```

- [ ] **Step 2: Run test to verify it fails**

Run: `python -m pytest tests/test_resolve.py -v`
Expected: FAIL — `AttributeError: module 'argus.resolve' has no attribute 'resolve_includes'`

- [ ] **Step 3: Implement**

```python
# argus/resolve.py  (append)
import posixpath
import sqlite3

#: Files eligible to satisfy an include.
HEADER_SUFFIXES = (".h", ".hpp", ".hxx", ".hh", ".inl", ".ipp")


def resolve_includes(conn: sqlite3.Connection) -> dict[str, int]:
    """Resolve every include in the database. Returns counts by state.

    Runs over the whole database rather than per repo: an include can point
    into a repo indexed later in the same cycle, and resolving repo by repo
    would make the graph depend on indexing order.
    """
    headers = [
        (row["id"], row["repo_id"], row["path"])
        for row in conn.execute("SELECT id, repo_id, path FROM files")
        if row["path"].endswith(HEADER_SUFFIXES)
    ]
    index = build_suffix_index(headers)
    by_repo_path = {(r[1], r[2]): r for r in headers}

    counts = {Resolution.RESOLVED: 0, Resolution.EXTERNAL: 0,
              Resolution.AMBIGUOUS: 0, Resolution.NOT_FOUND: 0}
    updates = []

    includes = conn.execute(
        "SELECT i.id, i.repo_id, i.raw, i.is_angle, f.path AS from_path"
        "  FROM includes i JOIN files f ON f.id = i.file_id"
    ).fetchall()

    for inc in includes:
        match, state = _resolve_one(inc, index, by_repo_path)
        counts[state] += 1
        updates.append((
            match[0] if match else None,
            match[1] if match else None,
            1 if state == Resolution.EXTERNAL else 0,
            state,
            inc["id"],
        ))

    conn.executemany(
        "UPDATE includes SET resolved_file_id = ?, resolved_repo_id = ?, "
        "is_external = ?, resolution = ? WHERE id = ?",
        updates,
    )
    conn.commit()
    return counts


def _resolve_one(inc, index, by_repo_path) -> tuple[FileRow | None, str]:
    raw = inc["raw"].strip()

    # C semantics: a quoted include is looked for beside the including file
    # before anywhere else.
    if not inc["is_angle"]:
        relative = posixpath.normpath(
            posixpath.join(posixpath.dirname(inc["from_path"]), raw))
        local = by_repo_path.get((inc["repo_id"], relative))
        if local is not None:
            return local, Resolution.RESOLVED

    candidates = index.get(raw, [])
    if not candidates:
        return None, (Resolution.EXTERNAL if inc["is_angle"] else Resolution.NOT_FOUND)
    if len(candidates) == 1:
        return candidates[0], Resolution.RESOLVED

    same_repo = [c for c in candidates if c[1] == inc["repo_id"]]
    if len(same_repo) == 1:
        return same_repo[0], Resolution.RESOLVED

    shortest = min(c[2].count("/") for c in candidates)
    fewest = [c for c in candidates if c[2].count("/") == shortest]
    if len(fewest) == 1:
        return fewest[0], Resolution.RESOLVED

    # Several plausible files and no decisive tiebreak. Guessing here produces
    # an edge that is wrong, permanent, and invisible.
    return None, Resolution.AMBIGUOUS
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `python -m pytest tests/test_resolve.py -v`
Expected: PASS (17 passed)

- [ ] **Step 5: Demonstrate the ambiguity test discriminates**

Temporarily replace the final `return None, Resolution.AMBIGUOUS` with `return candidates[0], Resolution.RESOLVED`.

Run: `python -m pytest tests/test_resolve.py -v`
Expected: FAIL on `test_an_ambiguous_include_emits_no_link_and_is_counted`. Restore afterwards.

- [ ] **Step 6: Commit**

```bash
git add argus/resolve.py tests/test_resolve.py
git commit -m "feat: resolve include strings to files without guessing at ambiguity"
```

---

### Task 4: Graph materialization

**Files:**
- Create: `argus/store/graph.py`
- Test: `tests/store/test_graph.py`

**Interfaces:**
- Consumes: resolved `includes` rows from Task 3.
- Produces: `rebuild_repo_deps(conn) -> int` — replaces `repo_deps` wholesale, returns the edge count.

- [ ] **Step 1: Write the failing test**

```python
# tests/store/test_graph.py
import pytest

from argus import resolve
from argus.store import graph
from argus.store.db import open_db


@pytest.fixture
def db(tmp_path):
    conn = open_db(tmp_path / "index.db")
    yield conn
    conn.close()


def _repo(conn, gitlab_id, name):
    return conn.execute(
        "INSERT INTO repos (gitlab_id, path_with_namespace, default_branch, "
        "http_url) VALUES (?, ?, 'main', 'u')", (gitlab_id, name)).lastrowid


def _file(conn, repo_id, path):
    return conn.execute(
        "INSERT INTO files (repo_id, path, size, blob_sha, content) "
        "VALUES (?, ?, 1, 'sha', '')", (repo_id, path)).lastrowid


def _resolved(conn, from_repo, from_file, to_repo, to_file):
    conn.execute(
        "INSERT INTO includes (repo_id, file_id, raw, is_angle, "
        "resolved_file_id, resolved_repo_id, is_external, resolution) "
        "VALUES (?, ?, 'x.h', 0, ?, ?, 0, ?)",
        (from_repo, from_file, to_file, to_repo, resolve.Resolution.RESOLVED))


def test_cross_repo_edges_are_materialised_with_weights(db):
    a, b = _repo(db, 1, "g/app"), _repo(db, 2, "g/eal")
    f1, f2 = _file(db, a, "src/one.c"), _file(db, a, "src/two.c")
    hdr = _file(db, b, "include/x.h")
    _resolved(db, a, f1, b, hdr)
    _resolved(db, a, f2, b, hdr)
    db.commit()

    assert graph.rebuild_repo_deps(db) == 1
    row = db.execute("SELECT from_repo_id, to_repo_id, weight FROM repo_deps").fetchone()
    assert (row["from_repo_id"], row["to_repo_id"]) == (a, b)
    assert row["weight"] == 2, "weight counts distinct including files"


def test_same_repo_includes_are_not_edges(db):
    a = _repo(db, 1, "g/app")
    src, hdr = _file(db, a, "src/one.c"), _file(db, a, "src/x.h")
    _resolved(db, a, src, a, hdr)
    db.commit()

    assert graph.rebuild_repo_deps(db) == 0


def test_unresolved_and_ambiguous_includes_contribute_no_edge(db):
    a, b = _repo(db, 1, "g/app"), _repo(db, 2, "g/eal")
    src = _file(db, a, "src/one.c")
    db.execute("INSERT INTO includes (repo_id, file_id, raw, is_angle, resolution) "
               "VALUES (?, ?, 'util.h', 0, ?)",
               (a, src, resolve.Resolution.AMBIGUOUS))
    db.commit()

    assert graph.rebuild_repo_deps(db) == 0


def test_rebuild_replaces_rather_than_accumulates(db):
    a, b = _repo(db, 1, "g/app"), _repo(db, 2, "g/eal")
    src, hdr = _file(db, a, "src/one.c"), _file(db, b, "include/x.h")
    _resolved(db, a, src, b, hdr)
    db.commit()

    graph.rebuild_repo_deps(db)
    graph.rebuild_repo_deps(db)
    assert db.execute("SELECT count(*) FROM repo_deps").fetchone()[0] == 1


def test_a_failed_rebuild_leaves_the_previous_graph_intact(db, monkeypatch):
    """A half-updated graph is worse than a stale one: centrality would be
    computed from a mixture of two passes."""
    a, b = _repo(db, 1, "g/app"), _repo(db, 2, "g/eal")
    src, hdr = _file(db, a, "src/one.c"), _file(db, b, "include/x.h")
    _resolved(db, a, src, b, hdr)
    db.commit()
    graph.rebuild_repo_deps(db)

    def boom(*args, **kwargs):
        raise RuntimeError("interrupted")

    monkeypatch.setattr(graph, "_edge_rows", boom)
    with pytest.raises(RuntimeError):
        graph.rebuild_repo_deps(db)

    assert db.execute("SELECT count(*) FROM repo_deps").fetchone()[0] == 1
```

- [ ] **Step 2: Run test to verify it fails**

Run: `python -m pytest tests/store/test_graph.py -v`
Expected: FAIL — `ModuleNotFoundError: No module named 'argus.store.graph'`

- [ ] **Step 3: Implement**

```python
# argus/store/graph.py
"""Materialize the cross-repo dependency graph.

`repo_deps` is rebuilt wholesale rather than maintained incrementally. It is
small -- one row per ordered repo pair that actually shares a header -- and
incremental maintenance of a derived graph is a bug farm for no gain: a missed
deletion leaves a phantom edge that nothing will ever notice.
"""

from __future__ import annotations

import sqlite3

from ..resolve import Resolution


def _edge_rows(conn: sqlite3.Connection) -> list[tuple[int, int, int]]:
    """(from_repo, to_repo, distinct including files) for cross-repo edges."""
    return [
        (row["from_repo_id"], row["to_repo_id"], row["weight"])
        for row in conn.execute(
            "SELECT repo_id AS from_repo_id, resolved_repo_id AS to_repo_id,"
            "       COUNT(DISTINCT file_id) AS weight"
            "  FROM includes"
            " WHERE resolution = ?"
            "   AND resolved_repo_id IS NOT NULL"
            "   AND resolved_repo_id != repo_id"
            " GROUP BY repo_id, resolved_repo_id",
            (Resolution.RESOLVED,),
        )
    ]


def rebuild_repo_deps(conn: sqlite3.Connection) -> int:
    """Replace `repo_deps` from the resolved includes. Returns the edge count.

    The delete and the insert share one transaction, so a failure part-way
    leaves the previous graph rather than a mixture of two passes.
    """
    rows = _edge_rows(conn)
    with conn:  # commits on success, rolls back on exception
        conn.execute("DELETE FROM repo_deps")
        conn.executemany(
            "INSERT INTO repo_deps (from_repo_id, to_repo_id, weight) "
            "VALUES (?, ?, ?)", rows,
        )
    return len(rows)
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `python -m pytest tests/store/test_graph.py -v`
Expected: PASS (5 passed)

- [ ] **Step 5: Run the full suite and commit**

Run: `python -m pytest -q`

```bash
git add argus/store/graph.py tests/store/test_graph.py
git commit -m "feat: materialize repo_deps from resolved cross-repo includes"
```

---

### Task 5: `repo_map` query and tool

**Files:**
- Modify: `argus/store/queries.py`
- Modify: `argus/mcpsrv/tools.py`
- Test: `tests/store/test_queries.py`, `tests/mcpsrv/test_tools.py`

**Interfaces:**
- Produces: `queries.repo_map(allowed_repo_ids, conn, repo_id) -> dict` with keys `repo`, `depends_on`, `depended_on_by`. Each list holds `{"repo_id", "path_with_namespace", "weight"}`.

- [ ] **Step 1: Write the failing test**

```python
# tests/store/test_queries.py  (append)
from argus.resolve import Resolution
from argus.store import graph


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
    assert ids["g/beta"] not in repr(visible)


def test_repo_map_on_a_repo_outside_the_allowlist_is_empty(two_repos):
    conn, ids = two_repos
    assert queries.repo_map([ids["g/alpha"]], conn, ids["g/beta"]) == {}


def test_repo_map_with_no_graph_built_yet_is_empty_not_an_error(two_repos):
    """Before the first resolution pass repo_deps is empty. That is a valid
    state, not a failure."""
    conn, ids = two_repos
    result = queries.repo_map([ids["g/alpha"]], conn, ids["g/alpha"])
    assert result["depends_on"] == []
```

- [ ] **Step 2: Run test to verify it fails**

Run: `python -m pytest tests/store/test_queries.py -k repo_map -v`
Expected: FAIL — `AttributeError: module 'argus.store.queries' has no attribute 'repo_map'`

- [ ] **Step 3: Implement the query**

```python
# argus/store/queries.py  (append)
def repo_map(allowed_repo_ids: Sequence[int], conn: sqlite3.Connection,
             repo_id: int) -> dict:
    """Dependencies and dependents of `repo_id`, filtered to the allowlist.

    `repo_deps` is a global graph, but a caller may only learn about repos
    they can already see. An edge to a repo outside the allowlist is dropped
    entirely rather than reported anonymously -- "depends on 1 repo you cannot
    see" is itself a disclosure.
    """
    _, ids = _placeholders(allowed_repo_ids)
    if not ids or repo_id not in set(ids):
        return {}

    row = conn.execute(
        "SELECT id, path_with_namespace FROM repos WHERE id = ?", (repo_id,)
    ).fetchone()
    if row is None:
        return {}

    def edges(sql: str) -> list[dict]:
        out: list[dict] = []
        for chunk in _chunks(list(ids), 1):
            marks = ",".join("?" for _ in chunk)
            out.extend(
                {"repo_id": r["other_id"],
                 "path_with_namespace": r["path_with_namespace"],
                 "weight": r["weight"]}
                for r in conn.execute(sql.format(marks=marks), (repo_id, *chunk))
            )
        out.sort(key=lambda e: (-e["weight"], e["path_with_namespace"]))
        return out

    return {
        "repo": {"repo_id": row["id"],
                 "path_with_namespace": row["path_with_namespace"]},
        "depends_on": edges(
            "SELECT d.to_repo_id AS other_id, r.path_with_namespace, d.weight"
            "  FROM repo_deps d JOIN repos r ON r.id = d.to_repo_id"
            " WHERE d.from_repo_id = ? AND d.to_repo_id IN ({marks})"),
        "depended_on_by": edges(
            "SELECT d.from_repo_id AS other_id, r.path_with_namespace, d.weight"
            "  FROM repo_deps d JOIN repos r ON r.id = d.from_repo_id"
            " WHERE d.to_repo_id = ? AND d.from_repo_id IN ({marks})"),
    }
```

- [ ] **Step 4: Register the tool**

```python
# argus/mcpsrv/tools.py  (append near the other impls)
async def repo_map_impl(db_path: Path | str, identity: acl.Identity,
                        repo_id: int) -> dict[str, Any]:
    return await run_readonly(
        db_path,
        lambda conn: queries.repo_map(identity.allowed_repo_ids, conn, repo_id),
    )


_REPO_MAP_DESC = (
    "Show which repos a given repo depends on, and which depend on it, based "
    "on resolved #include edges across the repos you have access to. Use it "
    "to answer 'what breaks if I change this' before editing a shared header. "
    "`weight` is how many distinct files create the dependency, so a weight of "
    "1 is a single #include and a weight of 300 is a core dependency. Repos "
    "you cannot access are omitted entirely -- an empty result may mean no "
    "dependencies, or that they are all in repos you cannot see. Returns "
    "empty if the dependency graph has not been built yet."
)
```

Inside `register_tools`, after the `index_status` registration:

```python
    @server.tool(name="repo_map", description=_REPO_MAP_DESC)
    async def repo_map(repo_id: int, *, ctx: Context) -> dict[str, Any]:
        identity = _identity(ctx)
        return await _with_audit(
            db_path, "repo_map", identity, {"repo_id": repo_id},
            lambda: repo_map_impl(db_path, identity, repo_id),
        )
```

- [ ] **Step 5: Update the exact-tool-set assertion**

In `tests/mcpsrv/test_tools.py`, the set in `test_tools_list_descriptions_are_load_bearing` becomes:

```python
    assert set(by_name) == {
        "find_symbol", "find_references", "search_code", "get_file", "index_status",
        "docs_lookup", "docs_search",
        "repo_map",
    }
```

- [ ] **Step 6: Run tests, then commit**

Run: `python -m pytest -q`
Expected: all pass

```bash
git add argus/store/queries.py argus/mcpsrv/tools.py tests/
git commit -m "feat: add repo_map, allowlist-filtered over one shared graph"
```

---

### Task 6: Input-shape detection for `which_repo`

**Files:**
- Create: `argus/whichrepo.py`
- Test: `tests/test_whichrepo.py`

**Interfaces:**
- Produces:
  - `Shape` — constants `DIFF`, `STACK`, `SYMBOL`, `PROSE`.
  - `detect_shape(text: str) -> str`
  - `extract_paths(text: str) -> list[str]` — file paths named in a diff or stack trace.
  - `extract_symbols(text: str) -> list[str]` — identifier-shaped tokens.

Pure functions, no database. Separated because the detection order is load-bearing: a diff *contains* paths and a stack trace *contains* symbols, so the most specific pattern must win.

- [ ] **Step 1: Write the failing test**

```python
# tests/test_whichrepo.py
from argus import whichrepo
from argus.whichrepo import Shape


def test_a_diff_is_detected_before_anything_else():
    text = "diff --git a/src/eal/x.c b/src/eal/x.c\n@@ -1,3 +1,4 @@\n+int x;\n"
    assert whichrepo.detect_shape(text) == Shape.DIFF


def test_a_stack_trace_is_detected():
    text = ("Traceback:\n"
            "  at decode_frame (src/codec/decode.c:412)\n"
            "  at main (src/app/main.c:88)\n")
    assert whichrepo.detect_shape(text) == Shape.STACK


def test_a_bare_identifier_is_a_symbol():
    assert whichrepo.detect_shape("decode_frame") == Shape.SYMBOL
    assert whichrepo.detect_shape("eal::Thread") == Shape.SYMBOL


def test_a_sentence_is_prose():
    assert whichrepo.detect_shape("add H.265 support to the decoder") == Shape.PROSE


def test_a_question_is_prose_even_if_it_contains_an_identifier():
    assert whichrepo.detect_shape("where do I add decode_frame support") == Shape.PROSE


def test_diff_paths_are_extracted_without_the_a_b_prefixes():
    text = "diff --git a/src/eal/x.c b/src/eal/x.c\n@@ -1 +1 @@\n"
    assert whichrepo.extract_paths(text) == ["src/eal/x.c"]


def test_stack_frame_paths_are_extracted():
    text = "  at decode_frame (src/codec/decode.c:412)\n  at main (src/app/main.c:88)\n"
    assert whichrepo.extract_paths(text) == ["src/codec/decode.c", "src/app/main.c"]


def test_symbols_are_extracted_from_a_stack_trace():
    text = "  at decode_frame (src/codec/decode.c:412)\n"
    assert "decode_frame" in whichrepo.extract_symbols(text)


def test_prose_stopwords_are_not_treated_as_symbols():
    """'the', 'add' and 'support' are identifier-shaped and would otherwise
    swamp the real terms."""
    got = whichrepo.extract_symbols("add H.265 support to the decoder")
    assert "the" not in got and "add" not in got
```

- [ ] **Step 2: Run test to verify it fails**

Run: `python -m pytest tests/test_whichrepo.py -v`
Expected: FAIL — `ModuleNotFoundError: No module named 'argus.whichrepo'`

- [ ] **Step 3: Implement**

```python
# argus/whichrepo.py
"""Work out what a developer handed `which_repo`, and pull evidence from it.

Four shapes arrive in practice: a diff under review, a stack trace, a bare
symbol name, and a prose description of a task. Three of the four need no
embeddings at all, which is why this tool ships before any vectors exist.

Detection order is load-bearing. A diff *contains* file paths and a stack
trace *contains* symbol names, so the most specific pattern must be tested
first or every diff would be read as a stack trace.
"""

from __future__ import annotations

import re


class Shape:
    DIFF = "diff"
    STACK = "stack"
    SYMBOL = "symbol"
    PROSE = "prose"


_DIFF_RE = re.compile(r"^(diff --git |@@ .* @@|\+\+\+ |--- )", re.MULTILINE)
_FRAME_RE = re.compile(r"(?:^|[\s(])([\w./\\-]+\.\w+):(\d+)", re.MULTILINE)
_AT_FRAME_RE = re.compile(r"^\s*(?:at|from|#\d+)\s+\S", re.MULTILINE)
_DIFF_PATH_RE = re.compile(r"^diff --git a/(\S+) b/\S+", re.MULTILINE)
_IDENT_RE = re.compile(r"\b[A-Za-z_][A-Za-z0-9_]*(?:(?:::|\.)[A-Za-z_][A-Za-z0-9_]*)*\b")

#: Identifier-shaped English that would otherwise swamp the real terms.
_STOPWORDS = frozenset("""
a an and add or the to for of in on with without is are be do does how what
where when why which that this it its from into support change fix update
make add new use using need want should would could can i we my our
""".split())


def detect_shape(text: str) -> str:
    if _DIFF_RE.search(text):
        return Shape.DIFF
    frames = len(_FRAME_RE.findall(text))
    if frames >= 2 or (frames >= 1 and _AT_FRAME_RE.search(text)):
        return Shape.STACK
    stripped = text.strip()
    if len(stripped.split()) <= 2 and _IDENT_RE.fullmatch(stripped):
        return Shape.SYMBOL
    return Shape.PROSE


def extract_paths(text: str) -> list[str]:
    """File paths named explicitly, in order, without duplicates."""
    found = _DIFF_PATH_RE.findall(text) or [m[0] for m in _FRAME_RE.findall(text)]
    seen, out = set(), []
    for path in found:
        normalised = path.replace("\\", "/")
        if normalised not in seen:
            seen.add(normalised)
            out.append(normalised)
    return out


def extract_symbols(text: str) -> list[str]:
    """Identifier-shaped tokens worth looking up, stopwords removed."""
    seen, out = set(), []
    for token in _IDENT_RE.findall(text):
        leaf = re.split(r"::|\.", token)[-1]
        if leaf.lower() in _STOPWORDS or len(leaf) < 3:
            continue
        if token not in seen:
            seen.add(token)
            out.append(token)
    return out
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `python -m pytest tests/test_whichrepo.py -v`
Expected: PASS (9 passed)

- [ ] **Step 5: Demonstrate the ordering test discriminates**

Temporarily move the `_DIFF_RE` check below the stack-frame check in `detect_shape`.

Run: `python -m pytest tests/test_whichrepo.py -v`
Expected: FAIL on `test_a_diff_is_detected_before_anything_else` — a diff's `@@` hunk headers contain `path:line`-shaped text. Restore afterwards.

- [ ] **Step 6: Commit**

```bash
git add argus/whichrepo.py tests/test_whichrepo.py
git commit -m "feat: detect which_repo input shape and extract its evidence"
```

---

### Task 7: `which_repo` scoring

**Files:**
- Modify: `argus/store/queries.py`
- Test: `tests/store/test_queries.py`

**Interfaces:**
- Consumes: `whichrepo.detect_shape`, `extract_paths`, `extract_symbols`; `repo_deps` from Task 4.
- Produces: `queries.which_repo(allowed_repo_ids, conn, description, limit=5) -> list[dict]` with keys `repo_id`, `path_with_namespace`, `confidence`, `shape`, `why`.

- [ ] **Step 1: Write the failing test**

```python
# tests/store/test_queries.py  (append)
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
    """Down-weighting high in-degree repos is right for prose and wrong when a
    stack frame points into the shared library, where that library genuinely
    is the answer."""
    conn, ids = two_repos
    _cross_repo_include(conn, ids)
    graph.rebuild_repo_deps(conn)
    path = conn.execute("SELECT path FROM files WHERE repo_id = ?",
                        (ids["g/beta"],)).fetchone()["path"]
    trace = f"  at f ({path}:10)\n  at g ({path}:20)\n"

    rows = queries.which_repo([ids["g/alpha"], ids["g/beta"]], conn, trace)
    assert rows
    assert rows[0]["repo_id"] == ids["g/beta"]


def test_confidence_is_between_zero_and_one(two_repos):
    conn, ids = two_repos
    rows = queries.which_repo([ids["g/alpha"], ids["g/beta"]], conn, "SharedName")
    assert rows
    assert all(0.0 <= r["confidence"] <= 1.0 for r in rows)
```

- [ ] **Step 2: Run test to verify it fails**

Run: `python -m pytest tests/store/test_queries.py -k which_repo -v`
Expected: FAIL — `AttributeError: module 'argus.store.queries' has no attribute 'which_repo'`

- [ ] **Step 3: Implement**

```python
# argus/store/queries.py  (append)
from .. import whichrepo
from ..whichrepo import Shape

#: Per-shape weights. Not magic numbers: each is defended by a test above that
#: fails if it changes materially. A diff or stack trace names files outright,
#: so lexical overlap would only add noise; prose inverts that.
_WEIGHTS: dict[str, dict[str, float]] = {
    Shape.DIFF:   {"direct": 1.0, "lexical": 0.0, "central": 0.0},
    Shape.STACK:  {"direct": 1.0, "lexical": 0.1, "central": 0.0},
    Shape.SYMBOL: {"direct": 1.0, "lexical": 0.2, "central": 0.0},
    Shape.PROSE:  {"direct": 0.5, "lexical": 1.0, "central": 0.3},
}

#: A repo qualifies with any direct hit, or a lexical score at least this
#: fraction of the best repo's. Below it, the answer is "nothing matched".
_FLOOR_RATIO = 0.35


def which_repo(allowed_repo_ids: Sequence[int], conn: sqlite3.Connection,
               description: str, limit: int = 5) -> list[dict]:
    """Rank repos a change probably belongs in, with the evidence for each.

    Returns [] rather than a ranked list of weak matches when nothing clears
    the evidence floor: a list looks like an answer, and the caller acts on
    the top row.

    The semantic term is absent, not zero-weighted by accident -- Phase 4 adds
    it. Diffs, stack traces and symbols do not depend on it at all.
    """
    _, ids = _placeholders(allowed_repo_ids)
    if not ids or not description.strip():
        return []

    allowed = set(ids)
    shape = whichrepo.detect_shape(description)
    weights = _WEIGHTS[shape]

    direct: dict[int, list[str]] = {}
    lexical: dict[int, float] = {}

    for path in whichrepo.extract_paths(description):
        for row in _files_named(conn, allowed, path):
            direct.setdefault(row["repo_id"], []).append(
                f"file {row['path']}")

    for name in whichrepo.extract_symbols(description)[:10]:
        for row in find_symbol(list(allowed), conn, name, limit=20):
            direct.setdefault(row["repo_id"], []).append(
                f"{row['kind']} {row['name']} at {row['path']}:{row['line']}")

    if weights["lexical"]:
        for row in search_code(list(allowed), conn, description, limit=50):
            lexical[row["repo_id"]] = lexical.get(row["repo_id"], 0.0) + 1.0

    if not direct and not lexical:
        return []

    best_lex = max(lexical.values(), default=0.0) or 1.0
    centrality = _in_degree(conn, allowed) if weights["central"] else {}
    max_central = max(centrality.values(), default=0) or 1

    scored: list[dict] = []
    for repo_id in allowed:
        hits = direct.get(repo_id, [])
        lex = lexical.get(repo_id, 0.0) / best_lex
        if not hits and lex < _FLOOR_RATIO:
            continue

        score = weights["direct"] * min(len(hits), 5) / 5.0 + weights["lexical"] * lex
        # Only inferred evidence is penalised. A repo named outright in a diff
        # or a stack frame is never punished for being widely depended upon.
        if not hits:
            score -= weights["central"] * (centrality.get(repo_id, 0) / max_central)

        if score <= 0:
            continue
        why = hits[:5] or [f"lexical match on {lexical.get(repo_id, 0):.0f} file(s)"]
        scored.append({
            "repo_id": repo_id,
            "path_with_namespace": _repo_name(conn, repo_id),
            "confidence": round(min(score, 1.0), 3),
            "shape": shape,
            "why": why,
        })

    scored.sort(key=lambda r: (-r["confidence"], r["path_with_namespace"]))
    return scored[:limit]


def _files_named(conn, allowed: set[int], path: str) -> list[sqlite3.Row]:
    rows = []
    for chunk in _chunks(list(allowed), 1):
        marks = ",".join("?" for _ in chunk)
        rows.extend(conn.execute(
            f"SELECT repo_id, path FROM files WHERE repo_id IN ({marks})"
            "  AND (path = ? OR path LIKE '%/' || ?)",
            (*chunk, path, path)).fetchall())
    return rows


def _in_degree(conn, allowed: set[int]) -> dict[int, int]:
    return {
        row["to_repo_id"]: row["n"]
        for row in conn.execute(
            "SELECT to_repo_id, COUNT(*) AS n FROM repo_deps GROUP BY to_repo_id")
        if row["to_repo_id"] in allowed
    }


def _repo_name(conn, repo_id: int) -> str:
    row = conn.execute("SELECT path_with_namespace FROM repos WHERE id = ?",
                       (repo_id,)).fetchone()
    return row["path_with_namespace"] if row else ""
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `python -m pytest tests/store/test_queries.py -k which_repo -v`
Expected: PASS (6 passed)

- [ ] **Step 5: Demonstrate the floor test discriminates**

Temporarily change `_FLOOR_RATIO = 0.35` to `_FLOOR_RATIO = 0.0` and remove the `if not direct and not lexical: return []` guard.

Run: `python -m pytest tests/store/test_queries.py -k which_repo -v`
Expected: FAIL on `test_which_repo_returns_empty_when_nothing_clears_the_floor`. Restore afterwards.

- [ ] **Step 6: Commit**

```bash
git add argus/store/queries.py tests/store/test_queries.py
git commit -m "feat: add which_repo scoring with evidence and an explicit refusal case"
```

---

### Task 8: The `which_repo` MCP tool

**Files:**
- Modify: `argus/mcpsrv/tools.py`
- Test: `tests/mcpsrv/test_tools.py`

**Interfaces:**
- Consumes: `queries.which_repo` from Task 7.
- Produces: `which_repo_impl(db_path, identity, description, limit=5) -> list[dict]`, and a registered `which_repo` tool.

- [ ] **Step 1: Write the failing test**

```python
# tests/mcpsrv/test_tools.py  (append)
def test_which_repo_description_names_all_four_input_shapes():
    """The description is what a 35B model reads to decide it may paste a
    stack trace in. If it only mentions descriptions, it will only ever get
    descriptions."""
    desc = tools._WHICH_REPO_DESC.lower()
    for word in ("stack trace", "diff", "symbol", "describ"):
        assert word in desc, word


def test_which_repo_description_states_the_empty_result_means_no_match():
    assert "empty" in tools._WHICH_REPO_DESC.lower()


@pytest.mark.anyio
async def test_which_repo_returns_evidence_for_each_candidate(two_repo_cfg):
    cfg, ids = two_repo_cfg
    identity = acl.Identity(user_id=1, username="dev",
                            allowed_repo_ids=[ids["g/alpha"], ids["g/beta"]])
    # acl.Identity is a dataclass of exactly (user_id, username,
    # allowed_repo_ids) -- see argus/acl.py.
    rows = await tools.which_repo_impl(cfg.index.db_path, identity, "SharedName")
    assert rows, "non-empty guard"
    assert all(r["why"] for r in rows)
```

- [ ] **Step 2: Run test to verify it fails**

Run: `python -m pytest tests/mcpsrv/test_tools.py -k which_repo -v`
Expected: FAIL — `AttributeError: module 'argus.mcpsrv.tools' has no attribute '_WHICH_REPO_DESC'`

- [ ] **Step 3: Implement**

```python
# argus/mcpsrv/tools.py  (append near the other impls)
async def which_repo_impl(db_path: Path | str, identity: acl.Identity,
                          description: str, limit: int = 5) -> list[dict]:
    return await run_readonly(
        db_path,
        lambda conn: queries.which_repo(
            identity.allowed_repo_ids, conn, description, limit=limit),
    )


_WHICH_REPO_DESC = (
    "Work out WHICH REPOSITORY a change belongs in, across the repos you have "
    "access to. This is the tool for 'where do I add this?' when you do not "
    "already know the repo. Accepts any of: a description of the task ('add "
    "H.265 support to the decoder'), a symbol or function name, a stack trace "
    "or error log pasted verbatim, or a diff you are reviewing -- paste "
    "whichever you actually have, including multi-line text. Each candidate "
    "comes back with `confidence` and a `why` list naming the specific files "
    "and symbols that drove the match, so you can judge the answer instead of "
    "trusting it. An EMPTY result means nothing matched well enough to be "
    "worth reporting, not that the code does not exist -- fall back to "
    "search_code with a distinctive term. Use repo_map afterwards to see what "
    "else depends on the repo you pick."
)
```

Inside `register_tools`, after the `repo_map` registration:

```python
    @server.tool(name="which_repo", description=_WHICH_REPO_DESC)
    async def which_repo(description: str, *, ctx: Context) -> list[dict]:
        identity = _identity(ctx)
        return await _with_audit(
            db_path, "which_repo", identity, {"description": description[:200]},
            lambda: which_repo_impl(db_path, identity, description),
        )
```

Note the `[:200]` truncation: a pasted stack trace or diff can be thousands of
lines, and the audit row records what was asked, not the whole payload.

- [ ] **Step 4: Update the exact-tool-set assertion**

```python
    assert set(by_name) == {
        "find_symbol", "find_references", "search_code", "get_file", "index_status",
        "docs_lookup", "docs_search",
        "repo_map", "which_repo",
    }
```

- [ ] **Step 5: Run tests, then commit**

Run: `python -m pytest -q`

```bash
git add argus/mcpsrv/tools.py tests/mcpsrv/test_tools.py
git commit -m "feat: expose which_repo as an MCP tool"
```

---

### Task 9: Pipeline wiring, operator statistics, and docs

**Files:**
- Modify: `argus/cli.py`
- Modify: `argus/store/queries.py` (`index_status`)
- Modify: `README.md`, `docs/deployment.md`
- Test: `tests/test_cli.py`, `tests/store/test_queries.py`

**Interfaces:**
- Consumes: `resolve.resolve_includes`, `graph.rebuild_repo_deps`.
- Produces: `argus resolve` subcommand; `index_status` rows gain `includes_resolved`, `includes_ambiguous`, `includes_external`.

- [ ] **Step 1: Write the failing test**

```python
# tests/test_cli.py  (append)
def _config_file(tmp_path):
    """A minimal on-disk config. tests/test_cli.py already writes one this way
    (see its `config.yaml` helper); there is no shared fixture to reuse."""
    path = tmp_path / "config.yaml"
    path.write_text(
        "gitlab:\n  url: https://gl.test\n  token: t\n"
        f"index:\n  data_dir: {(tmp_path / 'data').as_posix()}\n"
        f"  db_path: {(tmp_path / 'index.db').as_posix()}\n",
        encoding="utf-8")
    return path


def test_resolve_subcommand_runs_both_passes_and_reports_counts(tmp_path, capsys, monkeypatch):
    """Resolution runs once over the whole database, then the graph rebuilds
    from it. Order matters: rebuilding first would materialise the previous
    pass's edges."""
    calls = []
    monkeypatch.setattr("argus.cli.resolve_includes",
                        lambda conn: (calls.append("resolve"), {"resolved": 3,
                                                                "ambiguous": 1})[1])
    monkeypatch.setattr("argus.cli.rebuild_repo_deps",
                        lambda conn: (calls.append("graph"), 2)[1])

    assert main(["resolve", "--config", str(_config_file(tmp_path))]) == 0
    assert calls == ["resolve", "graph"]

    out = capsys.readouterr().out
    assert "resolved" in out and "ambiguous" in out
    assert "3" in out and "1" in out
```

```python
# tests/store/test_queries.py  (append)
def test_index_status_reports_resolution_quality(two_repos):
    """'34% of your includes are ambiguous' tells an operator their -I layout
    defeats suffix matching, which no tool tuning will fix."""
    from argus.resolve import resolve_includes

    conn, ids = two_repos
    _cross_repo_include(conn, ids)
    resolve_includes(conn)
    rows = queries.index_status([ids["g/alpha"]], conn)
    assert rows, "non-empty guard"
    # `in` on a sqlite3.Row tests its VALUES, not its column names.
    assert "includes_ambiguous" in rows[0].keys()
    assert rows[0]["includes_resolved"] >= 1
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `python -m pytest tests/test_cli.py -k resolve tests/store/test_queries.py -k resolution -v`
Expected: FAIL — resolution is never invoked and the key is absent.

- [ ] **Step 3: Wire the pipeline**

In `argus/cli.py`, add the imports:

```python
from .resolve import resolve_includes
from .store.graph import rebuild_repo_deps
```

At the end of `_index`, after every repo has been processed and before returning:

```python
    # One pass over the whole database, after every repo. An include can point
    # into a repo indexed later in this same cycle, so resolving per repo would
    # make the graph depend on indexing order.
    counts = resolve_includes(conn)
    edges = rebuild_repo_deps(conn)
    print(f"includes: {counts.get('resolved', 0)} resolved, "
          f"{counts.get('external', 0)} external, "
          f"{counts.get('ambiguous', 0)} ambiguous, "
          f"{counts.get('not_found', 0)} not found")
    print(f"repo graph: {edges} cross-repo edges")
```

Add the subcommand parser next to `p_status`:

```python
    p_resolve = sub.add_parser(
        "resolve", help="Re-resolve includes and rebuild the dependency graph")
    p_resolve.add_argument("--config", required=True, type=Path)
```

Add a handler and dispatch it alongside `status`:

```python
def _resolve(cfg: Config) -> int:
    conn = open_db(cfg.index.db_path)
    try:
        counts = resolve_includes(conn)
        edges = rebuild_repo_deps(conn)
    finally:
        conn.close()
    for state in ("resolved", "external", "ambiguous", "not_found"):
        print(f"{state:<12} {counts.get(state, 0)}")
    print(f"{'edges':<12} {edges}")
    return 0
```

```python
        if args.command == "resolve":
            return _resolve(cfg)
```

- [ ] **Step 4: Add the statistics to `index_status`**

`index_status` returns `list[sqlite3.Row]`, and a `Row` is **immutable** — it
cannot be assigned into. The counts therefore go in as correlated subqueries
in the existing SELECT, which is also how `files`, `symbols` and `errors` are
already computed there.

In `argus/store/queries.py`, inside `index_status`, add these three lines to
the SELECT list immediately after the `errors` subquery and before the
`queued_retries` comment block:

```python
            "       (SELECT COUNT(*) FROM includes WHERE repo_id = r.id"
            "         AND resolution = 'resolved')  AS includes_resolved,"
            "       (SELECT COUNT(*) FROM includes WHERE repo_id = r.id"
            "         AND resolution = 'external')  AS includes_external,"
            "       (SELECT COUNT(*) FROM includes WHERE repo_id = r.id"
            "         AND resolution = 'ambiguous') AS includes_ambiguous,"
```

The literals match `Resolution.RESOLVED` / `EXTERNAL` / `AMBIGUOUS` from Task
1. They are inlined rather than parameterised because these subqueries sit
inside a statement whose host parameters are the repo-id chunk, and adding
three per-row parameters would eat into the ~999 host-parameter budget that
`_chunks` exists to manage.

- [ ] **Step 5: Run the full suite**

Run: `python -m pytest -q`
Expected: all pass, 0 skipped

- [ ] **Step 6: Verify in the container**

Run: `docker build --target test --no-cache -t argus-test:phase3 .`
Expected: the test stage passes. A `CACHED` test layer proves nothing — `--no-cache` is required.

- [ ] **Step 7: Update the docs**

In `README.md`, move `which_repo` and `repo_map` out of **Planned** into the shipped private-code table, and update the Status table to mark Phase 3 complete.

In `docs/deployment.md`, add under the indexing section:

> `argus index` now ends with a resolution pass and a graph rebuild. Watch the
> `ambiguous` count: a high proportion means many repos ship headers with the
> same basename, and `which_repo` will be correspondingly weaker. `argus
> resolve` re-runs both without re-indexing.

- [ ] **Step 8: Commit**

```bash
git add argus/cli.py argus/store/queries.py README.md docs/deployment.md tests/
git commit -m "feat: run resolution and graph rebuild in the indexing pipeline"
```

---

## Completion criteria

- [ ] `pytest -q` passes locally and in the container, 0 skipped, 0 warnings
- [ ] `which_repo` answers all four input shapes with evidence, with no vectors present
- [ ] `which_repo` returns empty — not a ranked list — when nothing clears the floor
- [ ] An ambiguous include emits no edge and is counted
- [ ] `not_eal_thread.h` does not resolve to `eal_thread.h`
- [ ] Neither new tool reveals a repo outside the caller's allowlist, including by inference from an edge
- [ ] Resolution statistics visible through `index_status` and `argus resolve`
- [ ] Every new test demonstrated failing under a targeted revert

## Deliberately not in this phase

Embeddings, `semantic_search`, and any vector table. When Phase 4 comes it
extends the knowledge-pack pipeline (`packs/quantize.py`, `argus/embed.py`,
the measured two-stage search) rather than forking a second embedding stack.
