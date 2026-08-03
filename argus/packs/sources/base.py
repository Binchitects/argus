"""The contract every documentation source implements.

A pack is built by walking one or more sources. Each source knows how to find
its own documents and API symbols in a checkout, and carries the licence and
attribution that must travel with the pack -- a redistributable artifact built
from someone else's documentation is only redistributable if it says whose it
is and under what terms.
"""

from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
from typing import Iterator, Protocol, runtime_checkable


@dataclass(frozen=True)
class Doc:
    """One documentation page, before chunking.

    ``lang`` is the *markup* of ``body`` ("rst", "md", "mdx"), not a natural
    language: it selects which chunker the builder applies. ``url`` is the
    canonical public URL, kept so a retrieved chunk can be cited back to the
    upstream site rather than to a path inside a pack nobody can browse.
    """

    path: str
    title: str
    url: str
    lang: str
    body: str


@dataclass(frozen=True)
class ApiSymbol:
    """A named API entity resolvable to an exact place in the documentation.

    This is what makes a lookup precise rather than approximate: ``anchor``
    comes from the upstream toolchain's own inventory, so
    ``docs_lookup("os.path.join")`` lands on the definition instead of on
    whichever chunk happened to mention it.

    ``namespace`` is the inventory domain ("py", "c", "std"), which is what
    distinguishes ``c:function`` from ``py:function`` -- the module path is
    already a prefix of ``name`` and would be redundant here.
    """

    name: str
    kind: str
    namespace: str
    doc_path: str
    anchor: str
    signature: str


@runtime_checkable
class Source(Protocol):
    """A documentation source that can be indexed into a pack."""

    #: Short stable identifier used as the pack namespace, e.g. "python".
    name: str
    repo_url: str
    branch: str
    #: Path within the checkout that actually holds documentation.
    subtree: str
    #: SPDX identifier where one exists, else the licence's common name.
    license: str
    license_url: str
    #: Human-readable credit line reproduced in the pack metadata.
    attribution: str

    def iter_docs(self, root: Path) -> Iterator[Doc]:
        ...

    def iter_symbols(self, root: Path) -> Iterator[ApiSymbol]:
        ...
