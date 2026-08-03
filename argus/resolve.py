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
