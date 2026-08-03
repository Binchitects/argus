"""Tests for the react.dev documentation source adapter.

The fixtures mirror the real react.dev source shape, checked against
reference/react/useState.md upstream: YAML front-matter, JSX wrapper elements,
and headings that pin their own anchor with an MDX comment.
"""

from __future__ import annotations

from pathlib import Path

import pytest

from argus.packs.sources import react_docs
from argus.packs.sources.base import ApiSymbol, Doc, Source
from argus.packs.sources.react_docs import ReactDocs

FIXTURE = Path(__file__).parent / "fixtures" / "react"


@pytest.fixture
def source() -> ReactDocs:
    return ReactDocs()


@pytest.fixture
def docs(source: ReactDocs) -> list[Doc]:
    got = list(source.iter_docs(FIXTURE))
    assert got, "fixture produced no docs; every assertion below would be vacuous"
    return got


@pytest.fixture
def symbols(source: ReactDocs) -> list[ApiSymbol]:
    got = list(source.iter_symbols(FIXTURE))
    assert got, "fixture produced no symbols; every assertion below would be vacuous"
    return got


def _doc(docs: list[Doc], path: str) -> Doc:
    matches = [d for d in docs if d.path == path]
    assert matches, f"{path!r} not among {sorted(d.path for d in docs)}"
    return matches[0]


def _names(symbols: list[ApiSymbol]) -> set[str]:
    return {s.name for s in symbols}


# --- protocol and licence -----------------------------------------------------


def test_react_docs_satisfies_the_source_protocol(source):
    assert isinstance(source, Source)


def test_licence_metadata_is_present_and_non_empty(source):
    assert source.license == "CC-BY-4.0"
    assert source.license_url.startswith("http")
    assert "Meta Platforms" in source.attribution


# --- documents ----------------------------------------------------------------


def test_front_matter_title_wins_over_the_first_heading(docs):
    """react.dev renders the front-matter title; the first heading is often
    something else entirely."""
    doc = _doc(docs, "learn/index.md")
    assert doc.title == "Quick Start"
    assert "This heading loses" in doc.body, "the heading itself must survive"


def test_title_falls_back_to_the_first_heading_when_front_matter_has_none(source, tmp_path):
    page = tmp_path / "src" / "content" / "solo.md"
    page.parent.mkdir(parents=True)
    page.write_text("## `useId` {/*useid*/}\n\nText.\n", encoding="utf-8")
    [doc] = source.iter_docs(tmp_path)
    assert doc.title == "useId"


def test_canonical_urls_are_derived_from_the_file_path(docs):
    urls = {d.path: d.url for d in docs}
    assert urls["reference/react/useState.md"] == "https://react.dev/reference/react/useState"
    assert urls["reference/react-dom/client/createRoot.md"] == (
        "https://react.dev/reference/react-dom/client/createRoot"
    )


def test_index_pages_map_to_their_directory_url(docs):
    assert _doc(docs, "learn/index.md").url == "https://react.dev/learn"


def test_jsx_wrapper_elements_are_stripped_from_prose(docs):
    body = _doc(docs, "reference/react/useState.md").body
    assert "<Intro>" not in body
    assert "</Intro>" not in body
    assert "<InlineToc />" not in body
    assert "is a React Hook" in body, "the prose inside the wrapper must survive"


def test_imports_inside_code_fences_survive_the_strip(docs):
    """The one that matters. These lines are the documentation's own examples;
    a line-based strip would gut every code sample on the page."""
    body = _doc(docs, "reference/react/useState.md").body
    assert "import { useState } from 'react';" in body

    learn = _doc(docs, "learn/adding-interactivity.md").body
    assert "import { useState } from 'react';" in learn
    assert "export default function App() {}" in learn


def test_stripping_preserves_line_numbering(source):
    """Stripped lines are blanked, not deleted. Deleting them would offset
    every downstream Chunk.start_line from the source file it points into,
    and the drift would grow with each JSX wrapper on the page."""
    raw = (FIXTURE / "src/content/reference/react/useState.md").read_text(encoding="utf-8")
    _, body = react_docs.parse_front_matter(raw)
    stripped = react_docs.strip_jsx(body)

    assert len(stripped.splitlines()) == len(body.splitlines())
    original = body.splitlines()
    for i, line in enumerate(stripped.splitlines()):
        assert line in ("", original[i]), f"line {i} was rewritten, not blanked"


def test_docs_declare_markdown_so_the_builder_picks_the_right_chunker(docs):
    assert {d.lang for d in docs} == {"md"}


def test_iter_docs_on_a_tree_without_the_subtree_yields_nothing(source, tmp_path):
    assert list(source.iter_docs(tmp_path)) == []


# --- symbols: what must be emitted --------------------------------------------


def test_useState_yields_a_symbol_with_its_pinned_anchor(symbols):
    # The typing guide also has a `useState` heading, exactly as react.dev
    # does; the adapter emits both and ranking decides which wins downstream.
    [symbol] = [s for s in symbols
                if s.name == "useState" and s.namespace == "reference/react"]
    assert symbol.kind == "hook"
    assert symbol.anchor == "usestate"
    assert symbol.doc_path == "reference/react/useState"
    assert symbol.signature == "useState(initialState)"


def test_an_api_heading_without_a_call_signature_still_yields_a_symbol(symbols):
    [symbol] = [s for s in symbols if s.name == "useReducer"]
    assert symbol.kind == "hook"
    assert symbol.signature == ""


def test_component_headings_are_classified_as_components(symbols):
    [symbol] = [s for s in symbols if s.name == "StrictMode"]
    assert symbol.kind == "component"
    assert symbol.anchor == "strictmode"


def test_dotted_call_headings_are_captured(symbols):
    [symbol] = [s for s in symbols if s.name == "root.render"]
    assert symbol.signature == "root.render(reactNode)"


def test_namespace_distinguishes_react_from_react_dom(symbols):
    assert "reference/react" in {s.namespace for s in symbols if s.name == "useState"}
    assert {s.namespace for s in symbols if s.name == "createRoot"} == {
        "reference/react-dom/client"
    }


# --- symbols: what must NOT be emitted ----------------------------------------


def test_prose_headings_yield_no_symbols(symbols):
    """The plan's named case, plus the section headings that surround every
    reference page."""
    names = _names(symbols)
    for prose in ("Adding state to a component", "Reference", "Parameters",
                  "Returns", "Usage", "In this chapter"):
        assert prose not in names


def test_a_prose_only_page_contributes_no_symbols(source):
    docs = [d for d in source.iter_docs(FIXTURE) if d.path == "learn/adding-interactivity.md"]
    assert docs, "fixture page missing"
    assert list(react_docs.iter_heading_symbols(docs[0].body)) == []


def test_a_partially_code_heading_is_treated_as_prose(symbols):
    """'`set` functions, like `setSomething(nextState)`' is a real react.dev
    heading. It mentions APIs but does not name one, so it must not produce a
    symbol called 'set'."""
    assert "set" not in _names(symbols)
    assert "setSomething" not in _names(symbols)


def test_a_code_span_that_is_not_an_api_name_yields_nothing(symbols):
    """'`npm install react`' is a code span but not an identifier."""
    assert "npm" not in _names(symbols)


def test_an_api_heading_without_an_explicit_anchor_is_skipped(symbols):
    """Deriving the slug ourselves risks a link that lands somewhere else. A
    miss degrades to semantic search; a wrong hit is reported confidently."""
    assert "useDeferredValue" not in _names(symbols)


def test_every_emitted_symbol_has_a_non_empty_anchor(symbols):
    assert all(s.anchor for s in symbols)


def test_headings_inside_code_fences_are_not_symbols(source, tmp_path):
    page = tmp_path / "src" / "content" / "fenced.md"
    page.parent.mkdir(parents=True)
    page.write_text(
        "---\ntitle: Fenced\n---\n\n"
        "```md\n### `useFake(x)` {/*usefake*/}\n```\n",
        encoding="utf-8",
    )
    assert list(source.iter_symbols(tmp_path)) == []


# --- front-matter edge cases --------------------------------------------------


def test_unterminated_front_matter_does_not_swallow_the_document():
    meta, body = react_docs.parse_front_matter("---\ntitle: Broken\n\nReal content.\n")
    assert meta == {}
    assert "Real content." in body


def test_a_horizontal_rule_mid_document_is_not_front_matter():
    text = "# Title\n\nIntro.\n\n---\n\nMore.\n"
    meta, body = react_docs.parse_front_matter(text)
    assert meta == {}
    assert body == text


def test_quoted_front_matter_values_are_unquoted():
    meta, _ = react_docs.parse_front_matter('---\ntitle: "useState"\ncanary: true\n---\nBody\n')
    assert meta["title"] == "useState"
    assert meta["canary"] == "true"
