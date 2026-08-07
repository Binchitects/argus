"""Resolve `#include` strings to concrete files, across repos.

A wrong edge here is invisible. It silently corrupts `repo_deps`, which feeds
the centrality term behind every `which_repo` answer, and nothing downstream
can tell a fabricated dependency from a real one. So this module guesses at
nothing: an include it cannot pin to exactly one file is recorded as
unresolved with a reason, and contributes no edge.
"""

from __future__ import annotations

import posixpath
import sqlite3
from collections import defaultdict
from typing import Iterable


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

    # Basenames of every indexed repository, used to spot vendored copies.
    # See _is_vendored_copy.
    repo_names_by_id = {
        row["id"]: row["path_with_namespace"].rsplit("/", 1)[-1]
        for row in conn.execute("SELECT id, path_with_namespace FROM repos")
    }
    repo_names = set(repo_names_by_id.values())

    counts = {Resolution.RESOLVED: 0, Resolution.EXTERNAL: 0,
              Resolution.AMBIGUOUS: 0, Resolution.NOT_FOUND: 0}
    updates = []

    includes = conn.execute(
        "SELECT i.id, i.repo_id, i.raw, i.is_angle, f.path AS from_path"
        "  FROM includes i JOIN files f ON f.id = i.file_id"
    ).fetchall()

    for inc in includes:
        match, state = _resolve_one(inc, index, by_repo_path,
                                    repo_names, repo_names_by_id)
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


def _is_vendored_copy(path: str, own_repo: str, repo_names: set[str]) -> bool:
    """True if `path` sits under a directory named after an indexed repository.

    C projects routinely vendor copies of their dependencies, and a vendored
    header is not the canonical home of that name. Measured on real repos:
    libjpeg-turbo carries a copy of zlib at `src/spng/zlib/zconf.h`, and
    freetype carries one at `src/gzip/`.

    That matters because zlib's own `zconf.h` is *generated at build time* and
    so is absent from its source tree. The vendored copy was therefore the
    only candidate, the ambiguity guard never fired, and `#include "zconf.h"`
    inside zlib resolved confidently into libjpeg-turbo -- producing a false
    `zlib -> libjpeg-turbo` edge in a graph where zlib depends on nothing.

    A unique match is not evidence of correctness when the canonical file is
    missing. This is the one signal available that does not need a hand-written
    list of vendor directory names: a directory named after *another*
    repository this instance already indexes is almost certainly a copy of it.

    `own_repo` is what makes that safe. Namespacing a library's headers under
    a directory matching its own name -- `eal/include/eal/eal_thread.h` -- is
    the most ordinary layout in C, and an earlier version of this check that
    ignored the owning repo flagged every such file as vendored. Three tests
    caught it. Only a directory naming a *different* indexed repo counts.
    """
    segments = set(path.split("/")[:-1])
    return bool(segments & (repo_names - {own_repo}))


def _resolve_one(inc, index, by_repo_path, repo_names=frozenset(),
                 repo_names_by_id=None) -> tuple[FileRow | None, str]:
    repo_names_by_id = repo_names_by_id or {}
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

    # Drop vendored copies before any tiebreak. Keeping them lets a bundled
    # duplicate become the sole candidate and resolve with false confidence;
    # dropping them means an include whose canonical target is missing is
    # honestly recorded as unfound rather than attributed to the wrong repo.
    # A vendored copy inside the *including* repo is still fine -- that is a
    # local file, not a cross-repo claim.
    outside = [c for c in candidates if c[1] != inc["repo_id"]]
    if outside:
        canonical = [c for c in outside
                     if not _is_vendored_copy(c[2], repo_names_by_id.get(c[1], ""), repo_names)]
        candidates = [c for c in candidates if c[1] == inc["repo_id"]] + canonical

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
