"""Tests for the rendered-HTML adapters (SQLite, cppreference)."""

from __future__ import annotations

from pathlib import Path

from argus.packs.sources.html_docs import (
    CppReference, SqliteDocs, html_to_text, symbol_from_path,
)


class TestHtmlToText:
    def test_the_title_comes_from_the_title_element(self):
        """<title> lives inside <head>, so dropping head loses every title.

        That bug was silent: the caller falls back to the first heading, and
        SQLite pages came out named "1.Syntax" and "1.Overview" -- plausible
        enough to survive a glance at the output.
        """
        title, _ = html_to_text(
            "<html><head><title>ALTER TABLE</title></head>"
            "<body><h1>1.Overview</h1><p>x</p></body></html>")
        assert title == "ALTER TABLE"

    def test_headings_become_atx_so_the_chunker_can_build_a_trail(self):
        """The heading trail is prepended before embedding; flattening loses it."""
        _, body = html_to_text(
            "<h1>Top</h1><p>intro</p><h2>Nested</h2><p>detail</p>")
        lines = [ln for ln in body.splitlines() if ln.startswith("#")]
        assert lines == ["# Top", "## Nested"]

    def test_script_and_style_content_is_dropped(self):
        _, body = html_to_text(
            "<style>a{color:red}</style><script>evil()</script><p>real</p>")
        assert "color" not in body and "evil" not in body
        assert "real" in body

    def test_entities_are_unescaped(self):
        _, body = html_to_text("<p>a &lt; b &amp;&amp; c &gt; d</p>")
        assert "a < b && c > d" in body

    def test_a_stray_angle_bracket_does_not_swallow_the_page(self):
        """Regex tag-stripping eats everything after `operator<`.

        C++ and SQL reference pages are full of these, which is why this uses
        a parser.
        """
        _, body = html_to_text("<p>operator&lt; compares</p><p>KEEP ME</p>")
        assert "KEEP ME" in body

    def test_a_heading_stays_on_one_line(self):
        _, body = html_to_text("<h2>two\n   words</h2>")
        assert "## two words" in body


class TestSymbolFromPath:
    def test_a_library_entity_becomes_a_qualified_name(self):
        assert symbol_from_path(
            "cpp/container/vector/push_back.html") == "std::vector::push_back"
        assert symbol_from_path("cpp/algorithm/accumulate.html") == "std::accumulate"

    def test_language_pages_are_not_entities(self):
        """`cpp/language/if.html` documents syntax, not a named entity."""
        assert symbol_from_path("cpp/language/if.html") == ""

    def test_c_pages_are_left_to_search(self):
        """C has no namespace; naming them std:: would be wrong, bare would collide."""
        assert symbol_from_path("c/string/byte/strlen.html") == ""

    def test_non_html_and_index_pages_yield_nothing(self):
        assert symbol_from_path("cpp/container/vector.txt") == ""
        assert symbol_from_path("cpp/index.html") == ""


def _sqlite_tree(root: Path, dirname: str = "sqlite-doc-3530400") -> Path:
    content = root / dirname
    content.mkdir(parents=True)
    (content / "index.html").write_text(
        "<html><head><title>SQLite Home</title></head><body><p>home</p></body></html>",
        encoding="utf-8")
    (content / "lang_altertable.html").write_text(
        "<html><head><title>ALTER TABLE</title></head>"
        "<body><h1>1.Overview</h1><p>Renames a table.</p></body></html>",
        encoding="utf-8")
    (content / "pragma.html").write_text(
        "<html><head><title>PRAGMA statements</title></head>"
        "<body><p>many</p></body></html>", encoding="utf-8")
    return root


class TestSqliteDocs:
    def test_the_versioned_subtree_is_discovered_not_hardcoded(self, tmp_path):
        """The zip unpacks to sqlite-doc-<version>; naming it breaks per release."""
        _sqlite_tree(tmp_path, "sqlite-doc-9999999")
        docs = list(SqliteDocs().iter_docs(tmp_path))
        assert {d.path for d in docs} == {"index.html", "lang_altertable.html",
                                          "pragma.html"}

    def test_symbols_come_only_from_per_statement_pages(self, tmp_path):
        """pragma.html documents them all at once; an anchorless hit there is
        worse than no symbol."""
        _sqlite_tree(tmp_path)
        names = {s.name for s in SqliteDocs().iter_symbols(tmp_path)}
        assert names == {"ALTER TABLE"}

    def test_an_empty_tree_yields_nothing_rather_than_raising(self, tmp_path):
        assert list(SqliteDocs().iter_docs(tmp_path)) == []
        assert list(SqliteDocs().iter_symbols(tmp_path)) == []


class TestCppReference:
    def _tree(self, root: Path) -> Path:
        content = root / "reference" / "en" / "cpp" / "algorithm"
        content.mkdir(parents=True)
        (content / "accumulate.html").write_text(
            "<html><head><title>std::accumulate</title></head>"
            "<body><p>Sums a range.</p></body></html>", encoding="utf-8")
        return root

    def test_docs_carry_an_upstream_url(self, tmp_path):
        [doc] = list(CppReference().iter_docs(self._tree(tmp_path)))
        assert doc.url == "https://en.cppreference.com/w/cpp/algorithm/accumulate"
        assert doc.path == "cpp/algorithm/accumulate.html"

    def test_symbol_doc_path_matches_its_document(self, tmp_path):
        root = self._tree(tmp_path)
        docs = {d.path for d in CppReference().iter_docs(root)}
        for symbol in CppReference().iter_symbols(root):
            assert f"{symbol.doc_path}.html" in docs

    def test_missing_subtree_yields_nothing(self, tmp_path):
        assert list(CppReference().iter_docs(tmp_path)) == []
        assert list(CppReference().iter_symbols(tmp_path)) == []
