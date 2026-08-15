"""Tests for the ECMAXML (.NET API) adapter.

The fixtures below are cut down from real `dotnet/dotnet-api-docs` files, and
each test pins something the real corpus actually produced -- a directory that
matched `*.xml`, a summary made entirely of cross-references, five signature
languages for one member. None of it is hypothetical.
"""

from __future__ import annotations

from pathlib import Path

import pytest

from argus.packs.sources.dotnet_docs import DotnetApiDocs, _flatten, _parse_guarded

TYPE_XML = """<?xml version="1.0" encoding="utf-8"?>
<Type Name="String" FullName="System.String">
  <TypeSignature Language="C#" Value="public sealed class String" />
  <TypeSignature Language="ILAsm" Value=".class public sealed string" />
  <TypeSignature Language="DocId" Value="T:System.String" />
  <Docs>
    <summary>Represents text as a sequence of UTF-16 code units.</summary>
    <remarks>A string is a sequential collection of characters.</remarks>
  </Docs>
  <Members>
    <Member MemberName="Split">
      <MemberSignature Language="C#" Value="public string[] Split (char separator);" />
      <MemberSignature Language="F#" Value="member this.Split : char -&gt; string[]" />
      <MemberSignature Language="DocId" Value="M:System.String.Split(System.Char)" />
      <Docs>
        <summary>Splits a string into substrings based on a delimiter.</summary>
      </Docs>
    </Member>
    <Member MemberName="Split">
      <MemberSignature Language="C#" Value="public string[] Split (string separator);" />
      <MemberSignature Language="DocId" Value="M:System.String.Split(System.String)" />
      <Docs><summary>An overload.</summary></Docs>
    </Member>
    <Member MemberName="op_Equality">
      <MemberSignature Language="C#" Value="public static bool op_Equality (string a, string b);" />
      <Docs><summary>Operator.</summary></Docs>
    </Member>
  </Members>
</Type>
"""

#: A summary whose entire content is cross-references. `itertext` yields
#: nothing useful here, because <see cref="..."/> carries its text in an
#: attribute -- so a naive flatten deletes exactly the API names a reader is
#: searching for.
CREF_XML = """<?xml version="1.0" encoding="utf-8"?>
<Type Name="StringBuilder" FullName="System.Text.StringBuilder">
  <TypeSignature Language="C#" Value="public sealed class StringBuilder" />
  <TypeSignature Language="DocId" Value="T:System.Text.StringBuilder" />
  <Docs>
    <summary>Like <see cref="T:System.String" /> but mutable. See also
    <see cref="M:System.String.Concat(System.String,System.String)" />.</summary>
  </Docs>
  <Members />
</Type>
"""

BOMB_XML = """<?xml version="1.0"?>
<!DOCTYPE lolz [
 <!ENTITY lol "lol">
 <!ENTITY lol2 "&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;">
]>
<Type Name="Boom" FullName="System.Boom">
  <TypeSignature Language="C#" Value="&lol2;" />
  <Docs><summary>&lol2;</summary></Docs>
  <Members />
</Type>
"""


def _corpus(root: Path) -> Path:
    xml = root / "xml"
    (xml / "System").mkdir(parents=True)
    (xml / "System" / "String.xml").write_text(TYPE_XML, encoding="utf-8")
    (xml / "System.Text").mkdir(parents=True)
    (xml / "System.Text" / "StringBuilder.xml").write_text(CREF_XML, encoding="utf-8")
    # A namespace overview and a build manifest: neither describes a type.
    (xml / "System" / "ns-System.xml").write_text(
        '<?xml version="1.0"?><Namespace Name="System" />', encoding="utf-8")
    (xml / "index.xml").write_text(
        '<?xml version="1.0"?><Overview />', encoding="utf-8")
    return root


class TestParsing:
    def test_a_directory_matching_the_glob_is_skipped(self, tmp_path):
        """`rglob("*.xml")` is case-insensitive on Windows, so the real
        namespace DIRECTORY `Microsoft.Extensions.Configuration.Xml` matched it
        and opening it raised PermissionError -- which reads like a filesystem
        fault rather than a pattern matching the wrong kind of thing."""
        root = _corpus(tmp_path)
        (root / "xml" / "Microsoft.Extensions.Configuration.Xml").mkdir()

        docs = list(DotnetApiDocs().iter_docs(root))

        assert {d.title for d in docs} == {"System.String", "System.Text.StringBuilder"}

    def test_namespace_and_manifest_files_produce_no_documents(self, tmp_path):
        docs = list(DotnetApiDocs().iter_docs(_corpus(tmp_path)))
        assert all(not d.path.endswith("index.xml") for d in docs)
        assert all("ns-" not in d.path for d in docs)

    def test_a_document_declaring_a_dtd_is_refused(self, tmp_path):
        """This corpus is a cloned third-party repository. stdlib XML expands
        internal entities, so a hostile commit could expand a few hundred bytes
        into gigabytes. Real ECMAXML has no DOCTYPE, so one is refused."""
        path = tmp_path / "bomb.xml"
        path.write_text(BOMB_XML, encoding="utf-8")
        assert _parse_guarded(path) is None

    def test_a_malformed_file_costs_that_file_not_the_build(self, tmp_path):
        root = _corpus(tmp_path)
        (root / "xml" / "System" / "Broken.xml").write_text(
            "<Type><unclosed>", encoding="utf-8")

        docs = list(DotnetApiDocs().iter_docs(root))

        assert len(docs) == 2, "one bad file should not abort the corpus"


class TestFlatten:
    def test_cross_references_survive(self, tmp_path):
        """A summary built entirely of <see cref> would otherwise come out
        empty, deleting precisely the API names someone is searching for."""
        docs = {d.title: d for d in DotnetApiDocs().iter_docs(_corpus(tmp_path))}
        body = docs["System.Text.StringBuilder"].body
        assert "String" in body
        assert "Concat" in body

    def test_a_cref_is_reduced_to_its_last_segment(self):
        import xml.etree.ElementTree as ET

        node = ET.fromstring(
            '<summary>see <see cref="T:System.Text.StringBuilder" /></summary>')
        assert _flatten(node) == "see StringBuilder"


class TestDocuments:
    def test_the_body_carries_atx_headings_for_the_chunker(self, tmp_path):
        """The chunker builds its heading trail from markdown headings and
        prepends it before embedding; without them a member summary embeds as
        a loose sentence with no idea which type it belongs to."""
        docs = {d.title: d for d in DotnetApiDocs().iter_docs(_corpus(tmp_path))}
        body = docs["System.String"].body
        assert body.startswith("# System.String")
        assert "## Split" in body
        assert "## Remarks" in body

    def test_the_url_points_at_the_published_page(self, tmp_path):
        docs = {d.title: d for d in DotnetApiDocs().iter_docs(_corpus(tmp_path))}
        assert docs["System.String"].url.endswith("/dotnet/api/system.string")

    def test_the_csharp_signature_is_the_one_stored(self, tmp_path):
        """ECMAXML repeats every signature in five languages. Storing them all
        would multiply the pack to say the same thing five ways."""
        docs = {d.title: d for d in DotnetApiDocs().iter_docs(_corpus(tmp_path))}
        body = docs["System.String"].body
        assert "public sealed class String" in body
        assert ".class public sealed" not in body, "ILAsm leaked into the body"


class TestSymbols:
    def test_a_type_is_indexed_under_both_its_names(self, tmp_path):
        """docs_lookup is exact-match and never fuzzy, so a name it does not
        hold returns nothing at all -- and developers type both."""
        names = {s.name for s in DotnetApiDocs().iter_symbols(_corpus(tmp_path))}
        assert "System.String" in names
        assert "String" in names

    def test_members_are_indexed_qualified_and_bare(self, tmp_path):
        names = {s.name for s in DotnetApiDocs().iter_symbols(_corpus(tmp_path))}
        assert "String.Split" in names
        assert "Split" in names

    def test_overloads_collapse_to_one_symbol(self, tmp_path):
        """Split appears twice in the fixture, as it does upstream. Eight rows
        for one name is worse than one row pointing at the page documenting
        them all."""
        symbols = [s for s in DotnetApiDocs().iter_symbols(_corpus(tmp_path))
                   if s.name == "String.Split"]
        assert len(symbols) == 1

    def test_operator_members_are_not_indexed(self, tmp_path):
        """`op_Equality` is a compiler-generated name nobody looks up."""
        names = {s.name for s in DotnetApiDocs().iter_symbols(_corpus(tmp_path))}
        assert not any(n.endswith("op_Equality") for n in names), names

    def test_the_anchor_is_the_docid(self, tmp_path):
        """The identifier the rest of the .NET ecosystem links by, so an anchor
        built from it matches what learn.microsoft.com actually serves."""
        by_name = {s.name: s for s in DotnetApiDocs().iter_symbols(_corpus(tmp_path))}
        assert by_name["System.String"].anchor == "T:System.String"
        assert by_name["String.Split"].anchor.startswith("M:System.String.Split")

    def test_the_signature_field_carries_the_summary_for_docs_find(self, tmp_path):
        """`docs_find` searches api_symbols.signature, so a symbol is only
        reachable by what it DOES if the summary lands there."""
        by_name = {s.name: s for s in DotnetApiDocs().iter_symbols(_corpus(tmp_path))}
        assert "sequence of UTF-16" in by_name["System.String"].signature
        assert "delimiter" in by_name["String.Split"].signature

    def test_a_missing_checkout_yields_nothing_rather_than_raising(self, tmp_path):
        assert list(DotnetApiDocs().iter_docs(tmp_path / "absent")) == []
        assert list(DotnetApiDocs().iter_symbols(tmp_path / "absent")) == []


class TestSymbolDocumentLinkage:
    """The join the builder performs, which no isolated adapter test sees.

    `_insert_symbols` resolves `doc_ids[_doc_key(symbol.doc_path)]`, and
    `_doc_key` strips only .html/.rst/.mdx/.md -- not .xml. Emitting a symbol
    path with the suffix stripped therefore keyed it `System/String` against a
    document keyed `System/String.xml`, and ALL 215,269 symbols came back
    unresolved. The pack built, installed and listed normally; it simply had
    no lookup inventory, which nothing but a lookup would reveal.
    """

    def test_every_symbol_resolves_to_a_document(self, tmp_path):
        from argus.packs.build import _doc_key

        root = _corpus(tmp_path)
        source = DotnetApiDocs()
        doc_keys = {_doc_key(d.path) for d in source.iter_docs(root)}

        unresolved = [s.name for s in source.iter_symbols(root)
                      if _doc_key(s.doc_path) not in doc_keys]

        assert not unresolved, f"{len(unresolved)} symbols resolve to nothing"

    def test_the_suffix_is_not_stripped_from_symbol_paths(self, tmp_path):
        """Pinned explicitly: `.xml` is absent from _DOC_SUFFIXES, so both
        sides must carry it for the keys to agree."""
        symbols = list(DotnetApiDocs().iter_symbols(_corpus(tmp_path)))
        assert symbols
        assert all(s.doc_path.endswith(".xml") for s in symbols)
