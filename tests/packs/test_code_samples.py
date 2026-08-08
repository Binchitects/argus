"""Adapters for source-code corpora (Microsoft samples, TheAlgorithms)."""
from pathlib import Path

from argus.packs.sources import (AlgorithmsCpp, WindowsClassicSamples,
                                 WindowsDriverSamples)
from argus.packs.sources.code_samples import MAX_FILE_BYTES


def _w(root: Path, rel: str, text: str = "int main(void) { return 0; }\n") -> Path:
    p = root / rel
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_text(text, encoding="utf-8")
    return p


def test_every_chunk_can_say_which_sample_it_came_from(tmp_path):
    """The build pipeline chunks everything with chunk_markdown, which
    prepends the heading trail to each chunk. Raw .cpp has no headings, so a
    fragment from the middle of a 900-line driver would reach the embedder
    with no indication of what it is. The synthetic trail is what makes the
    chunk searchable."""
    _w(tmp_path, "general/echo/kmdf/driver/AutoSync/queue.c")
    doc = next(iter(WindowsDriverSamples().iter_docs(tmp_path)))
    assert doc.body.startswith("# general/echo")
    assert "## general/echo/kmdf/driver/AutoSync/queue.c" in doc.body
    assert "int main" in doc.body


def test_the_driver_sample_name_is_two_components_deep(tmp_path):
    """One component would collapse every driver in the repo into a single
    bucket called "general", which is a directory, not a sample."""
    _w(tmp_path, "general/echo/kmdf/driver/x.c")
    _w(tmp_path, "general/toaster/toastDrv/y.c")
    names = {s.name for s in WindowsDriverSamples().iter_symbols(tmp_path)}
    assert names == {"echo", "toaster"}


def test_build_output_is_not_indexed(tmp_path):
    """A sample repo carries obj/, x64/ and packages/. Indexing those buries
    the examples under artifacts nobody searches for."""
    _w(tmp_path, "Samples/Foo/foo.cpp")
    _w(tmp_path, "Samples/Foo/x64/Debug/generated.cpp")
    _w(tmp_path, "Samples/Foo/packages/dep/dep.h")
    paths = {d.path for d in WindowsClassicSamples().iter_docs(tmp_path)}
    assert paths == {"Foo/foo.cpp"}


def test_an_oversized_file_is_skipped(tmp_path):
    _w(tmp_path, "Samples/Foo/ok.cpp")
    _w(tmp_path, "Samples/Foo/huge.cpp", "x" * (MAX_FILE_BYTES + 1))
    paths = {d.path for d in WindowsClassicSamples().iter_docs(tmp_path)}
    assert paths == {"Foo/ok.cpp"}


def test_binary_masquerading_as_source_is_skipped(tmp_path):
    """A .h that is really binary decodes to replacement characters. Embedding
    it wastes a slot and returns garbage."""
    _w(tmp_path, "Samples/Foo/real.cpp")
    (tmp_path / "Samples/Foo/blob.h").write_bytes(b"\xff\xfe\x00\x01" * 400)
    paths = {d.path for d in WindowsClassicSamples().iter_docs(tmp_path)}
    assert paths == {"Foo/real.cpp"}


def test_an_algorithm_is_named_by_its_file_not_by_parsing_it(tmp_path):
    """TheAlgorithms puts one algorithm per file. The filename is a real name
    someone searches for and, unlike a regex-parsed function name, it cannot
    be silently wrong."""
    _w(tmp_path, "sorting/quick_sort.cpp")
    _w(tmp_path, "graph/dijkstra.cpp")
    syms = {s.name: s for s in AlgorithmsCpp().iter_symbols(tmp_path)}
    assert set(syms) == {"quick_sort", "dijkstra"}
    assert syms["quick_sort"].namespace == "sorting"
    assert syms["quick_sort"].kind == "algorithm"


def test_the_same_algorithm_name_in_two_topics_is_kept(tmp_path):
    """binary_search exists under both search/ and dynamic_programming/.
    Deduplicating on the bare name would drop one of them."""
    _w(tmp_path, "search/binary_search.cpp")
    _w(tmp_path, "dynamic_programming/binary_search.cpp")
    ns = {s.namespace for s in AlgorithmsCpp().iter_symbols(tmp_path)
          if s.name == "binary_search"}
    assert ns == {"search", "dynamic_programming"}


def test_the_declared_licences_are_the_real_ones():
    """Windows-driver-samples is MS-PL, not MIT -- checked against the repo's
    own LICENSE. A pack redistributes someone else's work and the metadata is
    the only place that says under what terms."""
    assert WindowsDriverSamples().license == "MS-PL"
    assert WindowsClassicSamples().license == "MIT"
    assert AlgorithmsCpp().license == "MIT"


def test_a_symbol_never_points_at_a_page_that_was_skipped(tmp_path):
    """iter_docs drops oversized and binary files; iter_symbols must drop the
    same ones. Anchoring a sample to a skipped file yields a symbol whose page
    was never written, and the builder does not error -- it silently discards
    it as "names a page this pack does not contain". The first wdk composite
    shipped 3 such symbols, and nothing in a successful build said so."""
    from argus.packs.build import _doc_key

    _w(tmp_path, "Samples/Good/a.cpp")
    # This sample's only file is oversized, so it produces no page at all.
    _w(tmp_path, "Samples/OnlyHuge/huge.cpp", "x" * (MAX_FILE_BYTES + 1))
    # This one leads with a binary file and has a real one after it.
    (tmp_path / "Samples/Mixed").mkdir(parents=True)
    (tmp_path / "Samples/Mixed/aaa_blob.h").write_bytes(b"\xff\xfe\x00\x01" * 400)
    _w(tmp_path, "Samples/Mixed/zzz_real.cpp")

    src = WindowsClassicSamples()
    pages = {_doc_key(d.path) for d in src.iter_docs(tmp_path)}
    symbols = list(src.iter_symbols(tmp_path))
    assert symbols, "no symbols -- the test proves nothing"

    dangling = [s.name for s in symbols if _doc_key(s.doc_path) not in pages]
    assert dangling == [], dangling
    assert {s.name for s in symbols} == {"Good", "Mixed"}, (
        "a sample with no readable file at all still produced a symbol")


def test_an_algorithm_whose_file_is_unreadable_yields_no_symbol(tmp_path):
    from argus.packs.build import _doc_key

    _w(tmp_path, "sorting/quick_sort.cpp")
    _w(tmp_path, "sorting/huge_sort.cpp", "x" * (MAX_FILE_BYTES + 1))
    src = AlgorithmsCpp()
    pages = {_doc_key(d.path) for d in src.iter_docs(tmp_path)}
    names = {s.name for s in src.iter_symbols(tmp_path)}
    assert names == {"quick_sort"}
    assert all(_doc_key(s.doc_path) in pages for s in src.iter_symbols(tmp_path))
