"""react.dev documentation source.

Unlike CPython there is no inventory feed, so symbols are derived from the
markup. react.dev makes that tractable: reference headings pin their own
anchor with an MDX comment, and API headings are written as a bare code span::

    ### `useState(initialState)` {/*usestate*/}

while prose headings are not::

    ## Reference {/*reference*/}
    #### Parameters {/*parameters*/}
    ### Adding state to a component {/*adding-state-to-a-component*/}

That is a structural distinction, not a guess about wording, and this adapter
leans on it hard. **A heading that is not unambiguously an API name yields no
symbol.** The asymmetry is deliberate: a miss costs a lookup that falls back to
semantic search, while a wrong hit is reported to the developer with the same
confidence as a right one.

For the same reason a symbol without an explicit ``{/*anchor*/}`` is skipped
rather than linked to a slug this module guessed -- the analogue of dropping
CPython's anchorless ``std:doc`` entries.
"""

from __future__ import annotations

import re
from dataclasses import dataclass
from pathlib import Path
from typing import Iterator

from ..chunk import fenced_line_flags
from .base import ApiSymbol, Doc

BASE_URL = "https://react.dev/"
DOC_SUFFIXES = (".md", ".mdx")

_FRONT_MATTER_DELIM = "---"
_FRONT_MATTER_LINE_RE = re.compile(r"^([A-Za-z_][\w-]*)\s*:\s*(.*)$")

#: A heading, with react.dev's optional ``{/*anchor*/}`` suffix split off.
_HEADING_RE = re.compile(
    r"^(#{1,6})[ \t]+(.+?)[ \t]*(?:\{/\*(?P<anchor>.*?)\*/\})?[ \t]*$"
)

#: The heading text must be *entirely* one code span to be considered an API.
_CODE_SPAN_RE = re.compile(r"^`([^`]+)`$")

#: What counts as an API name inside that code span.
_API_RE = re.compile(
    r"""^(?:
        <(?P<component>[A-Z][A-Za-z0-9]*)\s*/?>          # <Suspense>, <Profiler>
      | (?P<name>[A-Za-z_$][\w$]*(?:\.[A-Za-z_$][\w$]*)*)  # useState, root.render
        (?P<call>\([^`]*\))?                             # optional (args)
    )$""",
    re.VERBOSE,
)

_HOOK_RE = re.compile(r"^use[A-Z$_]")

#: Lines that are nothing but a JSX element, e.g. "<Intro>", "</Intro>",
#: "<InlineToc />". Only whole-line matches are stripped: a prose sentence that
#: mentions a component inline is prose, and mangling it would lose meaning.
_JSX_LINE_RE = re.compile(r"^\s*</?[A-Za-z][\w.]*(?:\s[^>]*?)?/?>\s*$")
_MODULE_LINE_RE = re.compile(r"^\s*(?:import|export)\s")


def parse_front_matter(text: str) -> tuple[dict[str, str], str]:
    """Split leading YAML front-matter from the body.

    Deliberately minimal rather than a YAML dependency: react.dev front-matter
    is flat ``key: value`` pairs. Only a delimiter on the very first line
    counts, because ``---`` is also a horizontal rule and appears throughout
    these documents.
    """
    lines = text.splitlines()
    if not lines or lines[0].strip() != _FRONT_MATTER_DELIM:
        return {}, text

    for index in range(1, len(lines)):
        if lines[index].strip() == _FRONT_MATTER_DELIM:
            meta = {}
            for line in lines[1:index]:
                match = _FRONT_MATTER_LINE_RE.match(line)
                if match:
                    meta[match.group(1)] = match.group(2).strip().strip("'\"")
            return meta, "\n".join(lines[index + 1:])

    # An unterminated block is not front-matter; treating it as such would
    # swallow the entire document.
    return {}, text


def strip_jsx(body: str) -> str:
    """Remove whole-line JSX elements and import/export lines from prose.

    Fence-aware, and that is the whole point: ``import { useState } from
    'react';`` inside a ```` ```js ```` block is the documentation's own
    example, and a line-based strip would quietly gut every code sample on the
    page. Fence state comes from ``chunk.fenced_line_flags`` so there is only
    one implementation of the pairing rule.
    """
    lines = body.splitlines()
    flags = fenced_line_flags(lines)
    # Blanked rather than deleted, so line N of the result is still line N of
    # the source file. Dropping them would silently offset every downstream
    # Chunk.start_line from the real file it claims to point into.
    return "\n".join(
        line
        if in_fence or not (_JSX_LINE_RE.match(line) or _MODULE_LINE_RE.match(line))
        else ""
        for line, in_fence in zip(lines, flags)
    )


def _classify(inner: str) -> tuple[str, str, str] | None:
    """Return (name, kind, signature) if ``inner`` names an API, else None."""
    match = _API_RE.match(inner.strip())
    if match is None:
        return None
    if match.group("component"):
        return match.group("component"), "component", ""

    name = match.group("name")
    call = match.group("call") or ""
    if _HOOK_RE.match(name):
        kind = "hook"
    elif call:
        kind = "function"
    elif "." in name:
        kind = "member"
    else:
        kind = "api"
    return name, kind, (name + call if call else "")


def iter_heading_symbols(body: str) -> Iterator[tuple[str, str, str, str]]:
    """Yield (name, kind, signature, anchor) for headings that name an API."""
    lines = body.splitlines()
    flags = fenced_line_flags(lines)
    for line, in_fence in zip(lines, flags):
        if in_fence:
            continue  # a '#' line inside a fence is code, not a heading
        heading = _HEADING_RE.match(line)
        if heading is None:
            continue
        anchor = heading.group("anchor")
        if not anchor:
            continue  # no explicit anchor: skip rather than guess a slug
        span = _CODE_SPAN_RE.match(heading.group(2).strip())
        if span is None:
            continue  # prose heading
        classified = _classify(span.group(1))
        if classified is None:
            continue
        name, kind, signature = classified
        yield name, kind, signature, anchor.strip()


def first_heading(body: str) -> str:
    lines = body.splitlines()
    flags = fenced_line_flags(lines)
    for line, in_fence in zip(lines, flags):
        if in_fence:
            continue
        heading = _HEADING_RE.match(line)
        if heading:
            text = heading.group(2).strip()
            span = _CODE_SPAN_RE.match(text)
            return span.group(1) if span else text
    return ""


@dataclass(frozen=True)
class ReactDocs:
    """react.dev's documentation, from a reactjs/react.dev checkout."""

    name: str = "react"
    repo_url: str = "https://github.com/reactjs/react.dev"
    branch: str = "main"
    subtree: str = "src/content"
    license: str = "CC-BY-4.0"
    license_url: str = (
        "https://github.com/reactjs/react.dev/blob/main/LICENSE-DOCS.md"
    )
    attribution: str = (
        "React documentation. Copyright (c) Meta Platforms, Inc. and "
        "affiliates. Used under CC BY 4.0."
    )

    def iter_docs(self, root: Path) -> Iterator[Doc]:
        content_root = root / self.subtree
        if not content_root.is_dir():
            return
        for path in sorted(content_root.rglob("*")):
            if path.suffix not in DOC_SUFFIXES or not path.is_file():
                continue
            raw = path.read_text(encoding="utf-8", errors="replace")
            meta, body = parse_front_matter(raw)
            relative = path.relative_to(content_root).as_posix()
            yield Doc(
                path=relative,
                # Front-matter wins: it is what the site renders as the page
                # title, and it is often not the first heading at all.
                title=meta.get("title") or first_heading(body) or relative,
                url=BASE_URL + _url_path(relative),
                lang="md",
                body=strip_jsx(body),
            )

    def iter_symbols(self, root: Path) -> Iterator[ApiSymbol]:
        for doc in self.iter_docs(root):
            url_path = _url_path(doc.path)
            # The section directory ("reference/react-dom/client") is what
            # distinguishes createRoot there from any same-named export
            # elsewhere; react.dev has no inventory domain to use instead.
            namespace = url_path.rpartition("/")[0] or self.name
            for name, kind, signature, anchor in iter_heading_symbols(doc.body):
                yield ApiSymbol(
                    name=name,
                    kind=kind,
                    namespace=namespace,
                    doc_path=url_path,
                    anchor=anchor,
                    signature=signature,
                )


def _url_path(relative: str) -> str:
    """'reference/react/useState.md' -> 'reference/react/useState'."""
    for suffix in DOC_SUFFIXES:
        if relative.endswith(suffix):
            relative = relative[: -len(suffix)]
            break
    if relative.endswith("/index"):
        relative = relative[: -len("/index")]
    elif relative == "index":
        relative = ""
    return relative
