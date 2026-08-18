"""Tests for the CPython documentation source adapter.

The objects.inv fixture is not invented: its lines are copied byte-for-byte
from CPython 3.13's own published inventory, so the quirks under test are the
ones real Sphinx emits rather than the ones I expected it to.
"""

from __future__ import annotations

import zlib
from pathlib import Path

import pytest

from argus.packs.sources import python_docs
from argus.packs.sources.base import ApiSymbol, Doc, Source
from argus.packs.sources.python_docs import InventoryError, PythonDocs

FIXTURE = Path(__file__).parent / "fixtures" / "python"

# A 1,531-entry excerpt of CPython 3.13's real published inventory, copied
# byte-for-byte. Checked in rather than read from wherever this happens to run:
# the previous version pointed at a local CPython install and silently skipped
# on every machine without one -- including the container, which is the only
# place the suite is actually gated.
SAMPLE_INVENTORY = Path(__file__).parent / "fixtures" / "python_inventory_sample.inv"


@pytest.fixture
def source() -> PythonDocs:
    return PythonDocs()


@pytest.fixture
def symbols(source: PythonDocs) -> list[ApiSymbol]:
    got = list(source.iter_symbols(FIXTURE))
    assert got, "fixture produced no symbols; every assertion below would be vacuous"
    return got


@pytest.fixture
def docs(source: PythonDocs) -> list[Doc]:
    got = list(source.iter_docs(FIXTURE))
    assert got, "fixture produced no docs; every assertion below would be vacuous"
    return got


def _by_name(symbols: list[ApiSymbol], name: str) -> ApiSymbol:
    matches = [s for s in symbols if s.name == name]
    assert matches, f"{name!r} not among {sorted(s.name for s in symbols)}"
    return matches[0]


# --- the protocol -------------------------------------------------------------


def test_python_docs_satisfies_the_source_protocol(source):
    assert isinstance(source, Source)


def test_licence_metadata_is_present_and_non_empty(source):
    """A pack redistributes someone else's documentation. Shipping it without
    the licence and attribution is the one defect that is a legal problem
    rather than a quality one."""
    assert source.license.strip()
    assert source.license_url.strip().startswith("http")
    assert source.attribution.strip()
    assert "Python Software Foundation" in source.attribution


# --- documents ----------------------------------------------------------------


def test_titles_are_extracted_with_inline_markup_stripped(docs):
    titles = {d.path: d.title for d in docs}
    assert titles["library/os.path.rst"] == "os.path --- Common pathname manipulations"
    assert titles["about.rst"] == "About this documentation"


def test_canonical_urls_point_at_the_public_docs_site(docs):
    urls = {d.path: d.url for d in docs}
    assert urls["library/os.path.rst"] == "https://docs.python.org/3/library/os.path.html"
    assert urls["library/json.rst"] == "https://docs.python.org/3/library/json.html"


def test_docs_carry_their_markup_language_and_body(docs):
    doc = next(d for d in docs if d.path == "library/os.path.rst")
    assert doc.lang == "rst"
    assert ".. function:: join(path, *paths)" in doc.body


def test_iter_docs_on_a_tree_without_the_subtree_yields_nothing(source, tmp_path):
    assert list(source.iter_docs(tmp_path)) == []


# --- symbols: the plan's named requirement ------------------------------------


def test_os_path_join_resolves_to_the_right_page_and_anchor(symbols):
    """The lookup the whole adapter exists to make exact."""
    symbol = _by_name(symbols, "os.path.join")
    assert symbol.doc_path == "library/os.path.html"
    assert symbol.anchor == "os.path.join"
    assert symbol.kind == "function"
    assert symbol.namespace == "py"


def test_anchorless_entries_are_skipped_rather_than_linked_to_the_page_top(symbols):
    """std:doc entries name a whole page and have no anchor. Emitting them
    would produce links that land at the top of the page instead of at the
    symbol -- a wrong answer that looks like a right one. CPython 3.13 has
    513 of these."""
    names = {s.name for s in symbols}
    assert "library/os.path" not in names
    assert "about" not in names
    assert all(s.anchor for s in symbols)


def test_every_emitted_symbol_has_a_usable_doc_path_and_anchor(symbols):
    for symbol in symbols:
        assert symbol.doc_path.endswith(".html"), symbol
        assert "#" not in symbol.anchor, symbol


# --- symbols: the quirks real inventories actually contain ---------------------


def test_names_containing_spaces_are_parsed_correctly(symbols):
    """87 entries in CPython 3.13 have spaces in the name. Splitting the line
    on whitespace by position mis-parses every one of them, and the damage is
    silent -- the URI field lands on the priority number instead."""
    symbol = _by_name(symbols, "Python 3000")
    assert symbol.namespace == "std"
    assert symbol.kind == "term"
    assert symbol.doc_path == "glossary.html"
    assert symbol.anchor == "term-Python-3000"


def test_dollar_abbreviation_expands_to_the_symbol_name(symbols):
    """81% of CPython's URIs end in '$'."""
    assert _by_name(symbols, "os.path.split").anchor == "os.path.split"
    assert _by_name(symbols, "PyList_Append").anchor == "c.PyList_Append"


def test_dollar_expansion_keeps_any_prefix_before_it(symbols):
    """'#module-$' must become '#module-os.path', not '#os.path'."""
    assert _by_name(symbols, "os.path").anchor == "module-os.path"


def test_c_and_python_domains_are_distinguished(symbols):
    assert _by_name(symbols, "PyList_Append").namespace == "c"
    assert _by_name(symbols, "os.path.join").namespace == "py"


def test_non_ascii_in_the_inventory_decodes_as_utf8(source):
    """The fixture's std:doc line carries an em dash. A latin-1 fallback would
    mangle it into mojibake rather than fail loudly."""
    entries = python_docs.parse_objects_inv((FIXTURE / "objects.inv").read_bytes())
    doc_entries = [e for e in entries if e.name == "library/os.path"]
    assert doc_entries, "fixture lost its std:doc entry"
    assert "\u2014" in doc_entries[0].dispname


# --- signatures ---------------------------------------------------------------


def test_signatures_come_from_the_rest_directives(symbols):
    # The call signature leads; the summary after " -- " is asserted by
    # TestSummaries. Split so this test keeps testing what it is named for.
    signature = _by_name(symbols, "os.path.join").signature
    assert signature.split(" -- ")[0] == "join(path, *paths)"


def test_method_signatures_resolve_through_their_class(symbols):
    """py:method is the largest domain in the inventory. A method nested under
    a class directive must resolve to json.JSONDecoder.decode, not
    json.decode -- which is what tracking only the module would give."""
    signature = _by_name(symbols, "json.JSONDecoder.decode").signature
    assert signature.split(" -- ")[0] == "decode(s)"


class TestSummaries:
    """The call signature is not something a description search can match.

    Measured over the built pack: 11,633 of 18,778 symbols (62%) carried two
    words or fewer -- a bare signature like "join(path, /, *paths)", or a page
    title for the half of the inventory the reST declares no directive for.
    The prose under the directive is the description, and it is per-symbol.
    """

    def test_the_prose_under_the_directive_is_captured(self):
        body = (".. module:: os.path\n\n"
                ".. function:: join(path, *paths)\n\n"
                "   Join one or more path segments intelligently.\n")
        summaries = python_docs.extract_summaries(body)
        assert summaries["os.path.join"] == (
            "Join one or more path segments intelligently.")

    def test_a_stacked_signature_is_not_mistaken_for_prose(self):
        """reST stacks alternate signatures directly under the first with no
        blank line. Taking the next non-empty line would return another
        signature for every multi-signature object in the corpus."""
        body = (".. module:: m\n\n"
                ".. function:: open(file)\n"
                "              open(file, mode)\n\n"
                "   Open a file and return a stream.\n")
        assert python_docs.extract_summaries(body)["m.open"] == (
            "Open a file and return a stream.")

    def test_field_lists_and_nested_directives_are_skipped(self):
        body = (".. module:: m\n\n"
                ".. function:: f(x)\n\n"
                "   :param x: the input\n"
                "   .. versionadded:: 3.9\n\n"
                "   Does the thing.\n")
        assert python_docs.extract_summaries(body)["m.f"] == "Does the thing."

    def test_an_undocumented_object_borrows_nothing(self):
        """A directive with no body must yield "", not the next object's
        sentence -- that would attribute one symbol's behaviour to another."""
        body = (".. module:: m\n\n"
                ".. function:: undocumented(x)\n\n"
                ".. function:: documented(y)\n\n"
                "   Only this one has prose.\n")
        summaries = python_docs.extract_summaries(body)
        assert "m.undocumented" not in summaries
        assert summaries["m.documented"] == "Only this one has prose."

    def test_both_halves_are_kept_when_both_exist(self):
        from argus.packs.sources.python_docs import _describe

        assert _describe("join(path)", "Join segments.", "os.path") == (
            "join(path) -- Join segments.")

    def test_the_title_is_the_last_resort_not_the_first(self):
        from argus.packs.sources.python_docs import _describe

        assert _describe(None, "Join segments.", "os.path") == "Join segments."
        assert _describe(None, None, "os.path") == "os.path"
        assert _describe("join(path)", None, "os.path") == "join(path)"


def test_symbols_absent_from_the_rest_have_an_empty_signature(symbols):
    """objects.inv covers more than the fixture's reST does. A missing
    signature is empty, not fabricated."""
    assert _by_name(symbols, "os.PathLike").signature == ""


def test_exception_directive_at_module_level_is_not_captured_by_the_class(symbols):
    signature = _by_name(symbols, "json.JSONDecodeError").signature
    assert signature.split(" -- ")[0] == "JSONDecodeError(msg, doc, pos)"


# --- malformed inventories fail loudly ----------------------------------------


def _inventory(body: str, header: str | None = None) -> bytes:
    header = header or (
        "# Sphinx inventory version 2\n# Project: Test\n# Version: 1\n"
        "# The remainder of this file is compressed using zlib.\n"
    )
    return header.encode() + zlib.compress(body.encode("utf-8"))


def test_truncated_header_raises():
    with pytest.raises(InventoryError, match="truncated"):
        python_docs.parse_objects_inv(b"# Sphinx inventory version 2\n")


def test_unsupported_inventory_version_raises():
    data = _inventory("x py:function 1 a.html#$ -\n",
                      header="# Sphinx inventory version 1\n#\n#\n#\n")
    with pytest.raises(InventoryError, match="version 2"):
        python_docs.parse_objects_inv(data)


def test_corrupt_zlib_payload_raises():
    header = (
        "# Sphinx inventory version 2\n# Project: Test\n# Version: 1\n"
        "# The remainder of this file is compressed using zlib.\n"
    )
    with pytest.raises(InventoryError, match="zlib"):
        python_docs.parse_objects_inv(header.encode() + b"not-compressed")


def test_unparsable_line_raises_rather_than_being_skipped():
    """Silently dropping a line would yield a pack missing symbols with no
    indication why."""
    with pytest.raises(InventoryError, match="unparsable"):
        python_docs.parse_objects_inv(_inventory("this-line-has-no-fields\n"))


def test_missing_inventory_names_where_it_looked(source, tmp_path):
    with pytest.raises(InventoryError, match="objects.inv"):
        list(source.iter_symbols(tmp_path))


# --- external validation ------------------------------------------------------


def test_parses_a_broad_sample_of_the_real_cpython_inventory():
    """Validation against data this project did not author.

    The small fixture is 14 entries; this is 1,531 real ones spanning 20
    domains, 97 names containing spaces, 150 anchorless documents and 1,077
    uses of the "$" abbreviation. If the parser has a shape assumption the
    small fixture happens not to exercise, it fails here.
    """
    entries = python_docs.parse_objects_inv(SAMPLE_INVENTORY.read_bytes())
    assert len(entries) > 1_000, f"only {len(entries)} entries -- wrong fixture?"

    names = {e.name for e in entries}
    assert "os.path.join" in names
    assert len([n for n in names if " " in n]) > 50, "expected names containing spaces"
    # py/c/std are the domains; the variety is in the roles (function,
    # method, label, term, macro, ...), which is what must survive the split.
    assert {e.domain for e in entries} >= {"py", "c", "std"}
    assert len({e.role for e in entries}) >= 10, "expected many roles"
    assert not any(e.uri.endswith("$") for e in entries), "unexpanded '$' remains"

    join = next(e for e in entries if e.name == "os.path.join")
    assert join.uri == "library/os.path.html#os.path.join"


def test_the_sample_inventory_exercises_the_anchorless_skip():
    """The 150 anchorless entries must actually reach iter_symbols' skip, not
    merely sit in the fixture."""
    entries = python_docs.parse_objects_inv(SAMPLE_INVENTORY.read_bytes())
    anchorless = [e for e in entries if "#" not in e.uri]
    assert len(anchorless) > 100, f"only {len(anchorless)} anchorless entries"


def test_the_branch_is_a_release_branch_not_main():
    """The checkout and objects.inv must describe the same tree.

    The inventory is a build artifact fetched from docs.python.org rather than
    cloned, so nothing makes the two agree automatically. Measured against the
    published 3.14 inventory: a 3.14 checkout resolves all 18,778 anchored
    entries, main resolves 18,764. A symbol whose page is missing gets no
    title fallback, so it ships with a blank signature and docs_find skips it
    -- the pack would build clean and report a healthy symbol count either way.

    Pinned because `--fetch` clones whatever this field names, so a well-meant
    bump to main would silently reintroduce that.
    """
    assert python_docs.PythonDocs().branch != "main", (
        "main is a development branch; the published inventory describes a "
        "release, and a mismatch costs symbols rather than failing the build")
    assert python_docs.PythonDocs().branch[0].isdigit(), (
        "expected a version branch such as 3.14")
