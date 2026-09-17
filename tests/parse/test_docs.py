"""The doc-comment extractor.

This is the signal that makes a capability question answerable. The index always
had it -- `files.content` holds the whole file and `symbols.line` locates the
symbol in it -- and never read it, so `semantic_search` matched on a name and a
signature and nothing about what a function DOES. Measured on a real corpus that
returned `expireSlaveKeys` for "expire keys past their TTL" where
`activeExpireCycle` was the answer: same file, both plausible names, and only
the sentence above each one tells them apart.

Every case here is a shape that appears in real code, and most of them are here
because the first version got them wrong.
"""
from __future__ import annotations

import pytest

from argus.parse import docs


def extract(source: str, symbol_line: int, lang: str) -> str:
    return docs.for_symbol(source.splitlines(), symbol_line, lang)


# --- C and C++ -----------------------------------------------------------------

def test_a_doxygen_block_above_a_definition():
    src = """\
/**
 * Expire keys whose TTL has elapsed.
 *
 * Walks the expires dict in incremental steps so a large keyspace does not
 * block the event loop.
 */
int activeExpireCycle(int type);
"""
    assert extract(src, 7, "c") == (
        "Expire keys whose TTL has elapsed.\n"
        "\n"
        "Walks the expires dict in incremental steps so a large keyspace does not\n"
        "block the event loop.")


def test_a_run_of_line_comments():
    src = """\
// Expire keys whose TTL has elapsed.
// Incremental, so it does not block.
int activeExpireCycle(int type);
"""
    assert extract(src, 3, "c") == ("Expire keys whose TTL has elapsed.\n"
                                    "Incremental, so it does not block.")


def test_the_comment_does_not_run_past_a_paragraph_break():
    """Two blank lines is a new paragraph in the file, and continuing past it
    starts attributing whatever came before to this symbol."""
    src = """\
// Something about a different function entirely.


// Expire keys whose TTL has elapsed.
int activeExpireCycle(int type);
"""
    assert extract(src, 5, "c") == "Expire keys whose TTL has elapsed."


def test_one_blank_line_between_comment_and_definition_is_allowed():
    src = """\
/* Does a thing. */

int foo(void);
"""
    assert extract(src, 3, "c") == "Does a thing."


def test_the_code_between_stops_the_walk():
    src = """\
// Documents the previous thing.
int previous(void);

int actual(void);
"""
    assert extract(src, 4, "c") == ""


def test_a_licence_banner_at_the_top_is_not_a_symbol_doc():
    """Otherwise the first symbol in every file carries the copyright notice as
    its description, and 'licensed under Apache' becomes the text nearest its
    vector."""
    src = """\
/* Copyright 2026 Example Ltd.
 * Licensed under the Apache License, Version 2.0.
 */
int first_symbol(void);
"""
    assert extract(src, 4, "c") == ""


def test_copyright_at_line_400_is_real_documentation():
    """The banner rule only applies at the top of the file. Applying it
    everywhere drops the documentation of any function that happens to mention
    licensing, which is the more expensive mistake."""
    filler = "\n".join(f"int f{i}(void);" for i in range(400))
    src = filler + """
// Explains how the copyright header is validated on upload.
int check_header(void);
"""
    assert extract(src, 402, "c") == (
        "Explains how the copyright header is validated on upload.")


def test_an_attribute_between_the_comment_and_the_definition():
    """C++ [[nodiscard]], Rust #[inline], C# [Obsolete], Java @Override -- all
    sit between the comment and the symbol, and stopping at them loses the
    documentation of every annotated function in the file."""
    src = """\
/// Releases the frame buffer.
[[nodiscard]] int release_frame(void);
"""
    assert extract(src, 2, "c++") == "Releases the frame buffer."


# --- Go and Rust ---------------------------------------------------------------

def test_go_doc_comment():
    src = """\
// ServeHTTP writes the current index status as JSON.
// It is read-only and safe to call concurrently.
func (s *Server) ServeHTTP(w http.ResponseWriter, r *http.Request) {
"""
    assert extract(src, 3, "go").startswith("ServeHTTP writes the current")


def test_rust_doc_comment_with_inner_markers():
    src = """\
/// Returns the number of live keys.
///
/// Excludes keys that have expired but not yet been reclaimed.
pub fn live_keys(&self) -> usize {
"""
    assert extract(src, 4, "rust") == (
        "Returns the number of live keys.\n\n"
        "Excludes keys that have expired but not yet been reclaimed.")


# --- Python --------------------------------------------------------------------

def test_a_python_docstring_is_preferred_over_the_comment_above():
    """The docstring is the text written for callers; a `#` above a `def` in a
    Python file is usually a section banner."""
    src = '''\
# ---- expiry ----------------------------------------------------------------

def active_expire_cycle(kind):
    """Expire keys whose TTL has elapsed, incrementally.

    Steps over a bounded number of keys per call so a large keyspace does not
    block the event loop.
    """
    return None
'''
    assert extract(src, 3, "python") == (
        "Expire keys whose TTL has elapsed, incrementally.\n\n"
        "Steps over a bounded number of keys per call so a large keyspace does not\n"
        "block the event loop.")


def test_a_python_function_with_no_docstring_falls_back_to_the_comment():
    src = """\
# Expire keys whose TTL has elapsed.
def active_expire_cycle(kind):
    return None
"""
    assert extract(src, 2, "python") == "Expire keys whose TTL has elapsed."


def test_a_single_quoted_python_docstring():
    src = """\
def f():
    'Short summary.'
    return 1
"""
    assert extract(src, 1, "python") == "Short summary."


def test_a_python_class_docstring():
    src = '''\
class Store:
    """A key-value store with a bounded keyspace."""

    def get(self, key):
        """Return the value, or None."""
        return None
'''
    assert extract(src, 1, "python") == "A key-value store with a bounded keyspace."
    assert extract(src, 4, "python") == "Return the value, or None."


def test_a_long_single_quoted_string_is_code_not_documentation():
    src = """\
def f():
    'SELECT id, name, email FROM users WHERE tenant_id = ? AND active = 1'
    return run(sql)
"""
    assert extract(src, 1, "python") == ""


def test_a_python_decorator_between_comment_and_def():
    src = """\
# Cached because the ACL lookup is expensive.
@lru_cache(maxsize=128)
def resolve(user):
    return None
"""
    assert extract(src, 3, "python") == (
        "Cached because the ACL lookup is expensive.")


# --- the shapes that must not crash or misfire ---------------------------------

def test_a_comment_at_the_very_first_line():
    src = """\
// Does a thing.
int f(void);
"""
    assert extract(src, 2, "c") == "Does a thing."


def test_a_symbol_on_the_first_line_has_no_comment():
    src = "int f(void);\n"
    assert extract(src, 1, "c") == ""


def test_an_out_of_range_line_returns_nothing():
    src = "int f(void);\n"
    assert extract(src, 99, "c") == ""
    assert extract(src, 0, "c") == ""


def test_a_shebang_is_not_documentation():
    src = """\
#!/usr/bin/env python3
import os
"""
    assert extract(src, 2, "python") == ""


def test_an_unterminated_block_does_not_run_away():
    src = """\
/* This one is never closed
int f(void);
"""
    assert extract(src, 2, "c") == ""


def test_a_long_block_is_clipped_not_dropped():
    """Clipped, not discarded. Conflating the search bound with the size limit
    was a real bug: one number meant a long block was never even FOUND, because
    the walk gave up before reaching its opening marker."""
    body = "\n".join(f" * Line {i} of a long explanation." for i in range(40))
    src = f"/**\n{body}\n */\nint f(void);\n"
    out = extract(src, 43, "c")
    assert out, "a long comment should still contribute something"
    assert len(out) <= docs.MAX_CHARS
    assert out.startswith("Line 0 of a long explanation.")


def test_a_block_larger_than_the_search_bound_is_not_found():
    """`SEARCH_LINES` is a deliberate bound, not an accident. A comment block
    hundreds of lines long above a definition is a file-level essay, and
    attributing it to whichever symbol follows is worse than storing nothing."""
    body = "\n".join(f" * Line {i}." for i in range(docs.SEARCH_LINES + 50))
    src = f"/**\n{body}\n */\nint f(void);\n"
    assert extract(src, len(src.splitlines()), "c") == ""


def test_an_unknown_language_gets_the_whole_c_family():
    """INCLUDING block comments, which is the case that actually ships.

    Universal Ctags does not emit its `language` field unless asked, so `lang`
    is None for every symbol in a real index. The first version returned line
    comments WITHOUT block support for that case, so a `/** ... */` above a
    function was never read -- and the fixture's own doxygen-commented C
    extracted nothing at all. Only running it caught that; the test here had
    covered `//` and stopped.
    """
    assert extract("// Does a thing.\nunique_thing();\n", 2, "some-new-language") \
        == "Does a thing."
    assert extract("/**\n * Does a thing.\n */\nunique_thing();\n", 4,
                   "some-new-language") == "Does a thing."
    # None is what a real index passes, so it gets the same treatment.
    assert extract("/**\n * Does a thing.\n */\nunique_thing();\n", 4,
                   None) == "Does a thing."


@pytest.mark.parametrize("lang,marker", [
    ("ruby", "#"), ("sh", "#"), ("perl", "#"), ("yaml", "#"),
    ("sql", "--"), ("lua", "--"), ("elixir", "//"),
])
def test_line_marker_per_language(lang, marker):
    src = f"{marker} Does a thing.\nsymbol_here\n"
    assert extract(src, 2, lang) == "Does a thing."
