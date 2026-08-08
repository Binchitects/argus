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
