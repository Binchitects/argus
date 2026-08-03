"""CPython documentation source.

Symbols come from ``objects.inv``, the Sphinx inventory the upstream build
publishes. That file is what makes a lookup exact: it maps every documented
name to the page and anchor Sphinx actually emitted, so ``os.path.join``
resolves to ``library/os.path.html#os.path.join`` rather than to whichever
chunk happens to mention it. Reproducing that mapping by parsing reST would be
guesswork; reading the inventory is not.

The format is a four-line plaintext header followed by zlib-compressed lines.
It is parsed directly rather than by depending on Sphinx, which would pull a
documentation toolchain into a search server.

Two properties of real inventories drive the parsing here, both verified
against CPython 3.13's own file (17,080 entries):

* **Names may contain spaces** -- 87 of them, e.g. ``Python 3000 std:term``.
  Splitting on whitespace by position silently mis-parses every one, so the
  fields are matched with the non-greedy pattern Sphinx itself uses, anchored
  by the numeric priority field.
* **A trailing ``$`` in the URI abbreviates the symbol name** and is used by
  13,837 of those entries (81%). Ignoring it yields broken anchors for four
  fifths of the inventory.
"""

from __future__ import annotations

import re
import zlib
from dataclasses import dataclass
from pathlib import Path
from typing import Iterator

from .base import ApiSymbol, Doc

DOCS_BASE_URL = "https://docs.python.org/3/"

#: Sphinx's own inventory line pattern. The name is non-greedy and may contain
#: spaces; it is the ``-?\d+`` priority field that makes the split unambiguous.
_LINE_RE = re.compile(r"(?x)(.+?)\s+(\S+)\s+(-?\d+)\s+?(\S*)\s+(.*)")

_INVENTORY_CANDIDATES = (
    "objects.inv",
    "Doc/build/html/objects.inv",
    "Doc/objects.inv",
)

# reST section underline: three or more of a single punctuation character.
_UNDERLINE_RE = re.compile(r"^([=\-~^\"'`#*+:.,_])\1{2,}\s*$")

# Inline markup to strip from a title: roles, literals, emphasis.
_ROLE_RE = re.compile(r":[\w:+-]+:`([^`]*)`")
_LITERAL_RE = re.compile(r"``([^`]*)``")
_INTERPRETED_RE = re.compile(r"`([^`]*)`")
_EMPHASIS_RE = re.compile(r"\*{1,2}([^*]+)\*{1,2}")

_DIRECTIVE_RE = re.compile(
    r"^(\s*)\.\.\s+(?:py:)?"
    r"(module|currentmodule|function|method|class|exception|data|attribute|"
    r"decorator|classmethod|staticmethod)::\s*(.+?)\s*$"
)
_OBJECT_DIRECTIVES = frozenset({
    "function", "method", "class", "exception", "data", "attribute",
    "decorator", "classmethod", "staticmethod",
})


class InventoryError(Exception):
    """``objects.inv`` is absent, truncated, or not a v2 Sphinx inventory."""


@dataclass(frozen=True)
class InventoryEntry:
    name: str
    domain: str
    role: str
    priority: int
    uri: str
    dispname: str


def parse_objects_inv(data: bytes) -> list[InventoryEntry]:
    """Parse a Sphinx v2 inventory, expanding the ``$`` URI abbreviation."""
    try:
        header_end = 0
        for _ in range(4):
            header_end = data.index(b"\n", header_end) + 1
    except ValueError as exc:
        raise InventoryError(
            "objects.inv is truncated: fewer than four header lines"
        ) from exc

    header = data[:header_end].decode("utf-8", errors="replace")
    if "version 2" not in header.splitlines()[0]:
        raise InventoryError(
            f"unsupported inventory header {header.splitlines()[0]!r}; "
            f"only Sphinx inventory version 2 is understood"
        )
    if "zlib" not in header:
        raise InventoryError(
            "inventory header does not declare zlib compression; refusing to "
            "guess at the payload encoding"
        )

    try:
        body = zlib.decompress(data[header_end:]).decode("utf-8")
    except zlib.error as exc:
        raise InventoryError(f"objects.inv payload is not valid zlib: {exc}") from exc

    entries = []
    for line in body.splitlines():
        if not line.strip():
            continue
        match = _LINE_RE.match(line)
        if match is None:
            # Not silently skipped: a line this parser cannot read means the
            # inventory is a shape we do not understand, and quietly dropping
            # it would produce a pack missing symbols with no indication why.
            raise InventoryError(f"unparsable inventory line: {line!r}")
        name, domain_role, priority, uri, dispname = match.groups()
        domain, _, role = domain_role.partition(":")
        if uri.endswith("$"):
            uri = uri[:-1] + name
        entries.append(InventoryEntry(
            name=name,
            domain=domain,
            role=role,
            priority=int(priority),
            uri=uri,
            dispname=name if dispname.strip() == "-" else dispname.strip(),
        ))
    return entries


def extract_title(body: str) -> str:
    """Return the first reST section title, with inline markup stripped."""
    lines = body.splitlines()
    for i, line in enumerate(lines):
        text = line.strip()
        # Overline form: punctuation, title, punctuation.
        if _UNDERLINE_RE.match(text) and i + 2 < len(lines):
            candidate = lines[i + 1].strip()
            if candidate and _UNDERLINE_RE.match(lines[i + 2].strip()):
                return _strip_markup(candidate)
        if not text or _UNDERLINE_RE.match(text):
            continue
        if i + 1 < len(lines):
            under = lines[i + 1].strip()
            if _UNDERLINE_RE.match(under) and len(under) >= len(text):
                return _strip_markup(text)
    return ""


def _strip_markup(text: str) -> str:
    text = _ROLE_RE.sub(r"\1", text)
    text = _LITERAL_RE.sub(r"\1", text)
    text = _INTERPRETED_RE.sub(r"\1", text)
    text = _EMPHASIS_RE.sub(r"\1", text)
    return text.strip()


def extract_signatures(body: str) -> dict[str, str]:
    """Map fully-qualified name -> signature from reST object directives.

    The inventory has no signatures, so they come from the markup. Names are
    resolved against the enclosing ``module``/``currentmodule`` directive the
    way Sphinx resolves them, which is what lets ``.. function:: join(path)``
    under ``.. module:: os.path`` become ``os.path.join``.

    Class context is tracked by indentation because CPython nests methods
    inside their class directive. Without it every ``.. method:: decode(s)``
    under ``.. class:: JSONDecoder`` would resolve to ``json.decode`` -- wrong
    for the largest domain in the inventory (3,043 ``py:method`` entries).
    """
    signatures: dict[str, str] = {}
    module = ""
    class_name = ""
    class_indent = -1

    for line in body.splitlines():
        match = _DIRECTIVE_RE.match(line)
        if match is None:
            continue
        indent, directive, argument = match.groups()
        depth = len(indent)

        if directive in ("module", "currentmodule"):
            module, class_name, class_indent = argument.strip(), "", -1
            continue
        if directive not in _OBJECT_DIRECTIVES:
            continue

        local = argument.split("(", 1)[0].strip()
        if not local:
            continue

        if directive == "class":
            class_name, class_indent = local, depth
            full = _qualify(module, local)
        elif class_name and depth > class_indent:
            nested = local if local.startswith(f"{class_name}.") else f"{class_name}.{local}"
            full = _qualify(module, nested)
        else:
            class_name, class_indent = "", -1
            full = _qualify(module, local)

        signatures[full] = argument.strip()
    return signatures


def _qualify(module: str, local: str) -> str:
    if module and not local.startswith(f"{module}."):
        return f"{module}.{local}"
    return local


@dataclass(frozen=True)
class PythonDocs:
    """CPython's documentation, from a cpython checkout plus its inventory."""

    name: str = "python"
    repo_url: str = "https://github.com/python/cpython"
    branch: str = "main"
    subtree: str = "Doc"
    license: str = "PSF-2.0"
    license_url: str = "https://docs.python.org/3/license.html"
    attribution: str = (
        "Python documentation. Copyright (c) 2001-2026 Python Software "
        "Foundation; All Rights Reserved. Used under the PSF License "
        "Agreement."
    )

    def iter_docs(self, root: Path) -> Iterator[Doc]:
        doc_root = root / self.subtree
        if not doc_root.is_dir():
            return
        for path in sorted(doc_root.rglob("*.rst")):
            body = path.read_text(encoding="utf-8", errors="replace")
            relative = path.relative_to(doc_root).as_posix()
            html = relative[: -len(".rst")] + ".html"
            yield Doc(
                path=relative,
                title=extract_title(body) or relative,
                url=DOCS_BASE_URL + html,
                lang="rst",
                body=body,
            )

    def iter_symbols(self, root: Path) -> Iterator[ApiSymbol]:
        entries = parse_objects_inv(self._find_inventory(root).read_bytes())

        signatures: dict[str, str] = {}
        for doc in self.iter_docs(root):
            signatures.update(extract_signatures(doc.body))

        for entry in entries:
            doc_path, sep, anchor = entry.uri.partition("#")
            if not sep or not anchor:
                # A whole-page entry (std:doc) has no anchor. Emitting it would
                # produce a link that silently lands at the top of the page
                # instead of at the symbol, so it is dropped rather than
                # shipped broken. CPython 3.13 has 513 of these.
                continue
            yield ApiSymbol(
                name=entry.name,
                kind=entry.role,
                namespace=entry.domain,
                doc_path=doc_path,
                anchor=anchor,
                signature=signatures.get(entry.name, ""),
            )

    def _find_inventory(self, root: Path) -> Path:
        for candidate in _INVENTORY_CANDIDATES:
            path = root / candidate
            if path.is_file():
                return path
        raise InventoryError(
            f"no objects.inv under {root}; looked in "
            f"{', '.join(_INVENTORY_CANDIDATES)}. It is a build artifact, so "
            f"a bare source checkout will not have one -- fetch the published "
            f"inventory from {DOCS_BASE_URL}objects.inv"
        )
