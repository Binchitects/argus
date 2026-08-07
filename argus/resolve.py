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

    # Bundled copies of other indexed repos, found by filename cluster rather
    # than by name or by content. See find_vendored_dirs: a copy is usually
    # modified, so hashing misses it, and the directory is often not named
    # after what it contains (freetype carries zlib under src/gzip).
    vendored_dirs = find_vendored_dirs(headers + [
        (row["id"], row["repo_id"], row["path"])
        for row in conn.execute("SELECT id, repo_id, path FROM files")
        if not row["path"].endswith(HEADER_SUFFIXES)
    ])

    counts = {Resolution.RESOLVED: 0, Resolution.EXTERNAL: 0,
              Resolution.AMBIGUOUS: 0, Resolution.NOT_FOUND: 0}
    updates = []

    includes = conn.execute(
        "SELECT i.id, i.repo_id, i.raw, i.is_angle, f.path AS from_path"
        "  FROM includes i JOIN files f ON f.id = i.file_id"
    ).fetchall()

    for inc in includes:
        match, state = _resolve_one(inc, index, by_repo_path,
                                    repo_names, repo_names_by_id, vendored_dirs)
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

    # Persist the vendored-copy verdict so query-time code can use it without
    # re-deriving it. The detection needs every path in the index at once,
    # which is far too much work to repeat per query -- so it is materialised
    # here, once per pass, the same way repo_deps is.
    conn.execute("UPDATE files SET is_vendored = 0 WHERE is_vendored != 0")
    for repo_id, directory in vendored_dirs:
        conn.execute(
            "UPDATE files SET is_vendored = 1 WHERE repo_id = ? "
            "  AND (path = ? OR path LIKE ? || '/%')",
            (repo_id, directory, directory))

    conn.commit()
    return counts



#: A directory must hold at least this many files, and this share of them must
#: be basenames the other repo also has, before it is called a vendored copy.
#: Set to catch a bundled library while ignoring the incidental overlap any two
#: C projects have (`config.h`, `util.h`). Measured on real repos: freetype's
#: src/gzip matches zlib at 88% of 16 files, libjpeg-turbo's src/spng/zlib at
#: 95% of 20 -- both far above the noise, which sat below 4 files.
VENDOR_MIN_FILES = 4
VENDOR_MIN_SHARE = 0.6


def find_vendored_dirs(rows: Iterable[FileRow],
                       repo_names_by_id: dict[int, str] | None = None
                       ) -> set[tuple[int, str]]:
    """Directories that are a bundled copy of another indexed repository.

    Returns ``(repo_id, directory)`` pairs. Detection is by *filename cluster*,
    not by content: a vendored copy is nearly always modified -- measured here,
    freetype's src/gzip/inflate.c is 57,147 bytes against zlib's 53,660 -- so
    hashing catches only the untouched ones. Names survive the edits.

    The subtle part is telling the copy from the original, because overlap is
    symmetric. zlib's own root directory matches libjpeg-turbo at 74%, purely
    because libjpeg-turbo bundles zlib; a naive rule would call the canonical
    zlib a copy and drop it from resolution entirely.

    The asymmetry is **depth**: a bundled copy is nested deeper than the
    project that owns those files holds them. zlib keeps inflate.c at its root
    (depth 0); freetype carries one at src/gzip (depth 2) and libjpeg-turbo at
    src/spng/zlib (depth 3). So a directory is a copy only when the repo it
    resembles keeps those same names *nearer its own root*.
    """
    by_repo: dict[int, list[str]] = defaultdict(list)
    for _file_id, repo_id, path in rows:
        by_repo[repo_id].append(path)

    # basename -> shallowest depth at which each repo holds it
    owned: dict[int, dict[str, int]] = {}
    for repo_id, paths in by_repo.items():
        depths: dict[str, int] = {}
        for path in paths:
            name = path.rsplit("/", 1)[-1]
            depth = path.count("/")
            if name not in depths or depth < depths[name]:
                depths[name] = depth
        owned[repo_id] = depths

    vendored: set[tuple[int, str]] = set()
    for repo_id, paths in by_repo.items():
        dirs: dict[str, set[str]] = defaultdict(set)
        for path in paths:
            directory = path.rsplit("/", 1)[0] if "/" in path else ""
            dirs[directory].add(path.rsplit("/", 1)[-1])

        for directory, names in dirs.items():
            if not directory or len(names) < VENDOR_MIN_FILES:
                continue                       # a repo's own root is never a copy
            here = directory.count("/") + 1
            for other_id, other_names in owned.items():
                if other_id == repo_id:
                    continue
                shared = names & other_names.keys()
                if len(shared) < VENDOR_MIN_FILES:
                    continue
                if len(shared) / len(names) < VENDOR_MIN_SHARE:
                    continue
                # The owner keeps these names closer to its own root.
                theirs = max(other_names[n] for n in shared)
                if theirs < here:
                    vendored.add((repo_id, directory))
                    break
    return vendored


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



def _in_vendored_dir(repo_id: int, path: str,
                     vendored_dirs: set[tuple[int, str]]) -> bool:
    """True if `path` sits inside a directory identified as a bundled copy.

    Checks every ancestor directory, not just the immediate one: a copy at
    `src/gzip` must also disqualify `src/gzip/internal/foo.h`.
    """
    if not vendored_dirs:
        return False
    parts = path.split("/")[:-1]
    for i in range(len(parts), 0, -1):
        if (repo_id, "/".join(parts[:i])) in vendored_dirs:
            return True
    return False


def _resolve_one(inc, index, by_repo_path, repo_names=frozenset(),
                 repo_names_by_id=None,
                 vendored_dirs=frozenset()) -> tuple[FileRow | None, str]:
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
        canonical = [
            c for c in outside
            if not _is_vendored_copy(c[2], repo_names_by_id.get(c[1], ""), repo_names)
            and not _in_vendored_dir(c[1], c[2], vendored_dirs)
        ]
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
