"""Rendered-HTML documentation sources.

Some projects publish documentation as a release archive of rendered HTML
rather than as markdown in a repository. SQLite's site is generated from a
Fossil repo and shipped as a zip; cppreference's git repo holds the build
tooling and the pages come from a release tarball. Both are reachable through
``fetch_archive`` -- what was missing was a way to read HTML.

``html_to_text`` converts headings to ATX (``#``, ``##``) rather than dropping
them. That is deliberate: the chunker builds a heading trail from markdown
headings and prepends it before embedding, which is what makes a retrieved
fragment carry its own context ("re > Regular Expression Syntax" rather than a
loose paragraph). Flattening the HTML to plain prose would discard exactly the
structure the retrieval quality rests on.
"""

from __future__ import annotations

import html as html_module
import re
from dataclasses import dataclass
from html.parser import HTMLParser
from pathlib import Path
from typing import Iterator

from .base import ApiSymbol, Doc

#: Content of these never belongs in the indexed text.
#:
#: `head` is NOT here, and that is the point: <title> lives inside it, so
#: dropping the whole element discarded every page title and the fallback
#: silently substituted the first heading -- SQLite pages came out named
#: "1.Syntax" and "1.Overview". The elements inside head that do carry text
#: (script, style) are dropped individually anyway.
_DROP = {"script", "style", "noscript", "nav", "footer"}

#: Emit a blank line before/after, so the chunker sees paragraph boundaries.
_BLOCK = {"p", "div", "section", "article", "table", "tr", "ul", "ol", "dl",
          "pre", "blockquote", "hr"}

_HEADINGS = {f"h{n}": n for n in range(1, 7)}

_BLANKS = re.compile(r"\n{3,}")
_TRAILING_WS = re.compile(r"[ \t]+\n")


class _Extractor(HTMLParser):
    """HTML to heading-preserving text.

    A parser rather than a regex: documentation HTML nests, and stripping tags
    with a pattern mangles ``<pre>`` blocks and swallows text after a stray
    ``<`` -- both common in C++ and SQL reference pages full of ``operator<``
    and ``x < y``.
    """

    def __init__(self) -> None:
        super().__init__(convert_charrefs=True)
        self.title = ""
        self._parts: list[str] = []
        self._skip = 0
        self._in_title = False
        self._heading: int | None = None

    def handle_starttag(self, tag: str, attrs) -> None:
        if tag in _DROP:
            self._skip += 1
            return
        if self._skip:
            return
        if tag == "title":
            self._in_title = True
        elif tag in _HEADINGS:
            self._heading = _HEADINGS[tag]
            self._parts.append("\n\n" + "#" * self._heading + " ")
        elif tag == "br":
            self._parts.append("\n")
        elif tag in _BLOCK:
            self._parts.append("\n\n")
        elif tag in {"td", "th", "li", "dt", "dd"}:
            self._parts.append("\n")

    def handle_endtag(self, tag: str) -> None:
        if tag in _DROP:
            self._skip = max(0, self._skip - 1)
            return
        if self._skip:
            return
        if tag == "title":
            self._in_title = False
        elif tag in _HEADINGS:
            self._heading = None
            self._parts.append("\n")
        elif tag in _BLOCK:
            self._parts.append("\n\n")

    def handle_data(self, data: str) -> None:
        if self._skip:
            return
        if self._in_title:
            self.title += data
            return
        if self._heading is not None:
            # A heading must stay on one line or it stops being a heading.
            self._parts.append(" ".join(data.split()))
            return
        self._parts.append(data)

    def text(self) -> str:
        joined = "".join(self._parts)
        joined = _TRAILING_WS.sub("\n", joined)
        return _BLANKS.sub("\n\n", joined).strip()


def html_to_text(source: str) -> tuple[str, str]:
    """Return ``(title, body)`` with headings preserved as ATX markdown."""
    parser = _Extractor()
    try:
        parser.feed(source)
        parser.close()
    except Exception:
        # A malformed page should cost that page, not the build. Falling back
        # to unescaped raw text keeps it searchable even when unparseable.
        return "", html_module.unescape(re.sub(r"<[^>]+>", " ", source)).strip()
    return " ".join(parser.title.split()), parser.text()


def _description_after_chrome(body: str, words: int = 30) -> str:
    """The first real prose on the page, skipping the site's own navigation.

    `docs_find` searches `api_symbols.signature`, so whatever lands here is
    the only text by which a symbol can be found by description. Taking the
    first words of the body put sqlite.org's page header there instead:
    every SQL statement in the pack was described as "Small. Fast. Reliable.
    Choose any three. Home Menu About Documentation Download License" -- the
    same string for all of them, matching nothing anyone would ask.

    The nav sits before the first heading and contains none, so content is
    taken from the first ATX heading onward. Heading lines themselves are
    skipped: they are section labels ("Syntax", "Overview"), not descriptions.
    Falls back to the raw head if the page has no headings at all, which is
    no worse than what it replaces.
    """
    lines = body.splitlines()
    start = next((i for i, line in enumerate(lines)
                  if line.startswith("#")), None)
    if start is None:
        return " ".join(body.split()[:words])
    prose = [line for line in lines[start:] if line.strip()
             and not line.startswith("#")]
    return " ".join(" ".join(prose).split()[:words])


def _first_heading(body: str) -> str:
    for line in body.splitlines():
        if line.startswith("#"):
            return line.lstrip("#").strip()
    return ""


def _iter_html(content: Path) -> Iterator[tuple[Path, str, str, str]]:
    """Yield ``(path, relative, title, body)`` for each readable page."""
    for path in sorted(content.rglob("*.html")):
        if not path.is_file():
            continue
        raw = path.read_text(encoding="utf-8", errors="replace")
        title, body = html_to_text(raw)
        if not body:
            continue
        relative = path.relative_to(content).as_posix()
        yield path, relative, (title or _first_heading(body) or relative), body


@dataclass(frozen=True)
class SqliteDocs:
    """The SQLite documentation set (sqlite.org).

    Published only as a versioned zip -- there is no docs repository on GitHub,
    which is why this needed the archive fetch path rather than an adapter
    alone. The zip unpacks to a version-stamped directory
    (``sqlite-doc-3530400``), so the subtree is discovered rather than named:
    hardcoding it would break on every release.
    """

    name: str = "sqlite"
    repo_url: str = "https://www.sqlite.org/"
    branch: str = ""
    subtree: str = ""
    archive_url: str = "https://www.sqlite.org/2026/sqlite-doc-3530400.zip"
    archive_sha256: str = ""
    license: str = "public-domain"
    license_url: str = "https://www.sqlite.org/copyright.html"
    attribution: str = (
        "SQLite documentation. The SQLite source code and documentation are "
        "dedicated to the public domain."
    )
    base_url: str = "https://www.sqlite.org/"

    def _content(self, root: Path) -> Path | None:
        root = Path(root)
        if (root / "index.html").is_file():
            return root
        for child in sorted(root.iterdir()) if root.is_dir() else []:
            if child.is_dir() and (child / "index.html").is_file():
                return child
        return None

    def iter_docs(self, root: Path) -> Iterator[Doc]:
        content = self._content(root)
        if content is None:
            return
        for _path, relative, title, body in _iter_html(content):
            yield Doc(path=relative, title=title,
                      url=self.base_url + relative, lang="md", body=body)

    def iter_symbols(self, root: Path) -> Iterator[ApiSymbol]:
        """SQL statements and pragmas, from the pages that define them.

        `lang_select.html` documents SELECT, `pragma.html` documents them all
        on one page. Only the per-statement pages give a symbol a page of its
        own, so only those are indexed -- a symbol whose anchor lands on a
        1,000-line combined page is worse than no symbol at all.
        """
        content = self._content(root)
        if content is None:
            return
        for _path, relative, title, body in _iter_html(content):
            if not relative.startswith("lang_") or relative == "lang.html":
                continue
            # "SELECT", "CREATE TABLE" -- the title is the statement itself.
            statement = title.split("(")[0].strip().rstrip(".")
            if not statement or len(statement) > 40:
                continue
            yield ApiSymbol(
                name=statement, kind="statement", namespace="sql",
                doc_path=relative, anchor="",
                signature=_description_after_chrome(body),
            )


@dataclass(frozen=True)
class CppReference:
    """cppreference.com, from the community offline HTML release.

    The upstream git repo holds the build tooling, not the rendered pages, so
    this reads the ``html-book`` release archive.

    Symbols come from PATHS, which for this corpus is a real inventory rather
    than a guess: the site is generated one entity per page, so
    ``reference/en/cpp/container/vector/push_back.html`` is exactly
    ``std::vector::push_back``. Deriving them from page titles would be worse
    -- those read "std::vector<T,Allocator>::push_back" with template
    parameters nobody types into a lookup.
    """

    name: str = "cppreference"
    repo_url: str = "https://github.com/PeterFeicht/cppreference-doc"
    branch: str = "master"
    subtree: str = "reference/en"
    archive_url: str = (
        "https://github.com/PeterFeicht/cppreference-doc/releases/download/"
        "v20250209/html-book-20250209.tar.xz"
    )
    archive_sha256: str = ""
    license: str = "CC-BY-SA-3.0"
    license_url: str = "https://en.cppreference.com/w/Cppreference:Copyright/CC-BY-SA"
    attribution: str = (
        "cppreference.com, by the cppreference contributors. Used under "
        "CC BY-SA 3.0."
    )
    base_url: str = "https://en.cppreference.com/w/"

    def iter_docs(self, root: Path) -> Iterator[Doc]:
        content = Path(root) / self.subtree
        if not content.is_dir():
            return
        for _path, relative, title, body in _iter_html(content):
            yield Doc(path=relative, title=title,
                      url=self.base_url + relative[: -len(".html")],
                      lang="md", body=body)

    def iter_symbols(self, root: Path) -> Iterator[ApiSymbol]:
        content = Path(root) / self.subtree
        if not content.is_dir():
            return
        for _path, relative, _title, body in _iter_html(content):
            name = symbol_from_path(relative)
            if not name:
                continue
            yield ApiSymbol(
                name=name, kind="entity",
                namespace=relative.split("/", 1)[0],
                doc_path=relative[: -len(".html")], anchor="",
                signature=" ".join(body.split()[:30]),
            )


#: Path segments that describe the site, not an entity.
_NOT_ENTITIES = frozenset({
    "index", "language", "keyword", "concept", "header", "meta", "experimental",
    "symbol_index", "links", "types", "utility", "io", "numeric", "algorithm",
    "container", "iterator", "memory", "string", "thread", "regex", "chrono",
    "locale", "error", "preprocessor",
})


def symbol_from_path(relative: str) -> str:
    """``cpp/container/vector/push_back.html`` -> ``std::vector::push_back``.

    Only C++ library pages become qualified names. Language pages (``cpp/
    language/if.html``) document syntax rather than an entity, and C pages have
    no namespace, so both are left to search rather than given a lookup name
    that would collide with the C++ one.
    """
    if not relative.endswith(".html"):
        return ""
    parts = relative[: -len(".html")].split("/")
    if len(parts) < 3 or parts[0] != "cpp" or parts[1] == "language":
        return ""
    tail = [p for p in parts[1:] if p not in _NOT_ENTITIES]
    if not tail or any(not p.replace("_", "").isalnum() for p in tail):
        return ""
    return "std::" + "::".join(tail)
