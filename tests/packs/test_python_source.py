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

# The real inventory on this machine, used for an external-validation test that
# skips where it is absent. 17,080 entries of ground truth beats any fixture.
REAL_INVENTORY = Path("C:/Python313/Doc/html/objects.inv")


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
    assert _by_name(symbols, "os.path.join").signature == "join(path, *paths)"


def test_method_signatures_resolve_through_their_class(symbols):
    """py:method is the largest domain in the inventory. A method nested under
    a class directive must resolve to json.JSONDecoder.decode, not
    json.decode -- which is what tracking only the module would give."""
    assert _by_name(symbols, "json.JSONDecoder.decode").signature == "decode(s)"


def test_symbols_absent_from_the_rest_have_an_empty_signature(symbols):
    """objects.inv covers more than the fixture's reST does. A missing
    signature is empty, not fabricated."""
    assert _by_name(symbols, "os.PathLike").signature == ""


def test_exception_directive_at_module_level_is_not_captured_by_the_class(symbols):
    assert _by_name(symbols, "json.JSONDecodeError").signature == (
        "JSONDecodeError(msg, doc, pos)"
    )


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


@pytest.mark.skipif(
    not REAL_INVENTORY.is_file(),
    reason="CPython's built HTML docs are not installed here",
)
def test_parses_the_real_cpython_inventory_end_to_end():
    """Validation against an artifact this project did not author.

    The fixture is a 14-line excerpt; this is all 17,080 entries. If the
    parser has a shape assumption the excerpt happens not to exercise, it
    fails here.
    """
    entries = python_docs.parse_objects_inv(REAL_INVENTORY.read_bytes())
    assert len(entries) > 10_000, f"only {len(entries)} entries -- excerpt, not the real file?"

    names = {e.name for e in entries}
    assert "os.path.join" in names
    assert any(" " in n for n in names), "expected names containing spaces"

    join = next(e for e in entries if e.name == "os.path.join")
    assert join.uri == "library/os.path.html#os.path.join"
    assert not any(e.uri.endswith("$") for e in entries), "unexpanded '$' remains"
