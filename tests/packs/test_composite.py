"""Packs assembled from more than one checkout."""
from pathlib import Path

import pytest

from argus.packs.build import _doc_key
from argus.packs.sources import WdkWithSamples, Win32WithSamples
from argus.packs.sources.composite import CompositeError

SDK_PAGE = """---
UID: NF:wdm.IoCreateDevice
title: IoCreateDevice function (wdm.h)
req.header: wdm.h
---

## -description

Creates a device object.
"""


def _w(root: Path, rel: str, text: str) -> None:
    p = root / rel
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_text(text, encoding="utf-8")


@pytest.fixture
def wdk_tree(tmp_path):
    _w(tmp_path, "windows-driver-docs-ddi/wdk-ddi-src/content/wdm/"
                 "nf-wdm-iocreatedevice.md", SDK_PAGE)
    _w(tmp_path, "Windows-driver-samples/general/echo/kmdf/driver/queue.c",
       "int main(void){return 0;}\n")
    return tmp_path


def test_both_corpora_land_in_one_pack(wdk_tree):
    paths = {d.path for d in WdkWithSamples().iter_docs(wdk_tree)}
    assert paths == {
        "ddi/wdm/nf-wdm-iocreatedevice.md",
        "samples/general/echo/kmdf/driver/queue.c",
    }


def test_every_path_says_which_corpus_it_came_from(wdk_tree):
    """Nothing stops two parts producing the same relative path, and
    docs.path is the key symbols resolve against. The prefix also makes a
    retrieved result say whether it is reference or sample code."""
    for doc in WdkWithSamples().iter_docs(wdk_tree):
        assert doc.path.split("/")[0] in {"ddi", "samples"}


def test_symbols_still_resolve_to_their_page(wdk_tree):
    """The builder links a symbol to its page by normalised doc path. If the
    two prefixes disagree nothing errors -- every symbol is silently dropped
    as "names a page this pack does not contain", and the pack ships with an
    empty inventory that looks fine."""
    src = WdkWithSamples()
    keys = {_doc_key(d.path) for d in src.iter_docs(wdk_tree)}
    symbols = list(src.iter_symbols(wdk_tree))
    assert symbols, "no symbols -- the test proves nothing"
    unresolved = [s.name for s in symbols if _doc_key(s.doc_path) not in keys]
    assert unresolved == [], unresolved


def test_a_missing_part_is_loud(tmp_path):
    """A composite that quietly skips a missing checkout builds a pack that
    looks complete and is half empty; the failure would surface months later
    as a lookup that finds nothing."""
    _w(tmp_path, "windows-driver-docs-ddi/wdk-ddi-src/content/wdm/x.md", SDK_PAGE)
    with pytest.raises(CompositeError) as excinfo:
        list(WdkWithSamples().iter_docs(tmp_path))
    assert "Windows-driver-samples" in str(excinfo.value)


def test_differing_licences_are_both_recorded():
    """The WDK reference is CC-BY-4.0 and the driver samples are MS-PL.
    Recording only one misstates the terms the other is redistributed under,
    and a pack's metadata is the only place those terms appear."""
    wdk = WdkWithSamples()
    assert "CC-BY-4.0" in wdk.license and "MS-PL" in wdk.license
    assert "CC BY 4.0" in wdk.attribution
    assert "Microsoft Public License" in wdk.attribution
    win32 = Win32WithSamples()
    assert "CC-BY-4.0" in win32.license and "MIT" in win32.license
