"""The Win32, WDK and MSVC C++ documentation adapters."""
from pathlib import Path

from argus.packs.sources import CppDocs, WdkDdi, Win32Api
from argus.packs.sources.microsoft_docs import parse_front_matter, parse_uid

SDK_PAGE = """---
UID: NF:winuser.MessageBox
title: MessageBox function (winuser.h)
description: Displays a modal dialog box.
req.header: winuser.h
req.lib: User32.lib
req.dll: User32.dll
req.unicode-ansi: MessageBoxW (Unicode) and MessageBoxA (ANSI)
api_name: ["MessageBox","MessageBoxA","MessageBoxW"]
---

## -description

Displays a modal dialog box.

```cpp
int DisplayResourceNAMessageBox()
{
    int msgboxID = MessageBox(NULL, L"text", L"caption", MB_OK);
}
```
"""


def _write(tmp_path: Path, rel: str, text: str) -> Path:
    path = tmp_path / rel
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding="utf-8")
    return path


def test_a_uid_gives_an_exact_name_kind_and_header():
    assert parse_uid("NF:winuser.MessageBox") == ("function", "winuser", "MessageBox")
    assert parse_uid("NS:winuser.tagMSG") == ("struct", "winuser", "tagMSG")
    assert parse_uid("NN:objidl.IStream") == ("interface", "objidl", "IStream")


def test_a_uid_with_no_entity_is_skipped():
    """Module-level pages carry a UID with no name after the dot. Inventing one
    puts a lookup key in the pack that nobody can ever type."""
    assert parse_uid("NN:combaseapi") is None
    assert parse_uid("") is None
    assert parse_uid("ZZ:winuser.Thing") is None


def test_json_style_lists_in_front_matter_are_parsed():
    meta, body = parse_front_matter(SDK_PAGE)
    assert meta["UID"] == "NF:winuser.MessageBox"
    assert meta["api_name"] == ["MessageBox", "MessageBoxA", "MessageBoxW"]
    assert body.lstrip().startswith("## -description")


def test_the_ansi_and_wide_names_are_both_looked_up(tmp_path):
    """MessageBoxA and MessageBoxW are what appear in real code and in real
    linker errors; MessageBox is the macro. Indexing only the UID name leaves
    a lookup for either of the names a developer actually pasted unanswered."""
    _write(tmp_path, "sdk-api-src/content/winuser/nf-winuser-messagebox.md", SDK_PAGE)
    names = {s.name for s in Win32Api().iter_symbols(tmp_path)}
    assert {"MessageBox", "MessageBoxA", "MessageBoxW"} <= names


def test_the_signature_is_the_requirements_not_a_code_example(tmp_path):
    """These pages carry no prototype -- the published site generates one from
    the parameter sections, and the only fenced block is a usage example.
    Using that example would report MessageBox's signature as
    `int DisplayResourceNAMessageBox()`."""
    _write(tmp_path, "sdk-api-src/content/winuser/nf-winuser-messagebox.md", SDK_PAGE)
    sym = next(s for s in Win32Api().iter_symbols(tmp_path) if s.name == "MessageBox")
    assert "DisplayResourceNAMessageBox" not in sym.signature
    assert "winuser.h" in sym.signature and "User32.lib" in sym.signature


def test_requirements_survive_into_the_indexed_body(tmp_path):
    """Which header to include and which .lib to link are the most asked-for
    facts about a Win32 entity, and they live only in front matter -- a
    body-only chunker would drop them entirely."""
    _write(tmp_path, "sdk-api-src/content/winuser/nf-winuser-messagebox.md", SDK_PAGE)
    doc = next(iter(Win32Api().iter_docs(tmp_path)))
    assert "winuser.h" in doc.body and "User32.dll" in doc.body
    assert doc.url.endswith("/winuser/nf-winuser-messagebox")


def test_the_wdk_adapter_reads_the_same_shape_from_its_own_subtree(tmp_path):
    page = SDK_PAGE.replace("NF:winuser.MessageBox", "NF:wdm.IoCreateDevice")
    _write(tmp_path, "wdk-ddi-src/content/wdm/nf-wdm-iocreatedevice.md", page)
    names = {s.name for s in WdkDdi().iter_symbols(tmp_path)}
    assert "IoCreateDevice" in names
    assert Win32Api().iter_symbols(tmp_path).__next__ is not None  # different subtree
    assert list(Win32Api().iter_symbols(tmp_path)) == [], \
        "the win32 adapter read the WDK subtree"


def test_cpp_symbols_come_from_f1_keywords_qualified_side(tmp_path):
    _write(tmp_path, "docs/standard-library/vector-class.md",
           '---\ntitle: "vector class"\n'
           'f1_keywords: ["vector/std::vector::push_back","vector"]\n---\n'
           "# `vector` class\n\n## Syntax\n\n```cpp\ntemplate <class T>\n```\n")
    syms = {s.name for s in CppDocs().iter_symbols(tmp_path)}
    assert "std::vector::push_back" in syms


def test_cpp_symbols_carry_no_borrowed_signature(tmp_path):
    """A cpp-docs page lists many f1_keywords and has at most one Syntax
    block. Attaching it to each states that every compiler error on a page has
    the syntax of the first -- measured, A2196 and A2202 both came back
    showing A2193's example line."""
    _write(tmp_path, "docs/errors/a.md",
           '---\ntitle: "errors"\nf1_keywords: ["A2193","A2196"]\n---\n'
           "# errors\n\n## Syntax\n\n```asm\nADD r0, r8, pc ; A2193\n```\n")
    for sym in CppDocs().iter_symbols(tmp_path):
        assert sym.signature == "", (sym.name, sym.signature)


BLOCK_PAGE = """---
UID: NF:wdm.KeAcquireSpinLock~r1
title: KeAcquireSpinLock macro (wdm.h)
req.header: wdm.h
req.irql: <= DISPATCH_LEVEL
api_name:
 - KeAcquireSpinLock
---

## -description
"""

RENAMED_PAGE = """---
UID: NF:wdm.IofCompleteRequest
title: IoCompleteRequest macro (wdm.h)
req.header: wdm.h
api_name:
 - IofCompleteRequest
---

## -description
"""


def test_block_style_yaml_lists_are_parsed(tmp_path):
    """Both Microsoft repos write api_name as a block sequence, not inline
    JSON. Treating it as an empty scalar silently dropped every alias on those
    pages -- measured, KeAcquireSpinLock was absent from the built pack."""
    meta, _ = parse_front_matter(BLOCK_PAGE)
    assert meta["api_name"] == ["KeAcquireSpinLock"]


def test_a_revision_suffix_is_not_part_of_the_name():
    """56 DDI pages carry a ~rN suffix disambiguating upstream revisions.
    Keeping it stores KeAcquireSpinLock~r1, which nobody will ever type."""
    assert parse_uid("NF:wdm.KeAcquireSpinLock~r1") == (
        "function", "wdm", "KeAcquireSpinLock")


def test_the_documented_name_wins_when_the_uid_uses_an_internal_one(tmp_path):
    """nf-wdm-iocompleterequest.md carries UID NF:wdm.IofCompleteRequest and
    lists only IofCompleteRequest in api_name. The name every driver actually
    calls appears in the title alone, so without reading it the pack cannot
    answer a lookup for IoCompleteRequest at all."""
    _write(tmp_path, "wdk-ddi-src/content/wdm/nf-wdm-iocompleterequest.md", RENAMED_PAGE)
    names = {s.name for s in WdkDdi().iter_symbols(tmp_path)}
    assert "IoCompleteRequest" in names
    assert "IofCompleteRequest" in names


def test_the_spin_lock_page_resolves_end_to_end(tmp_path):
    _write(tmp_path, "wdk-ddi-src/content/wdm/nf-wdm-keacquirespinlock.md", BLOCK_PAGE)
    syms = {s.name: s for s in WdkDdi().iter_symbols(tmp_path)}
    assert "KeAcquireSpinLock" in syms
    assert "DISPATCH_LEVEL" in syms["KeAcquireSpinLock"].signature


class TestSignatureCarriesDescription:
    """`docs_find` searches api_symbols.signature and nothing else.

    With the contract alone, win32 and wdk contributed ~125,000 symbols whose
    entire searchable text was "Header: wdm.h; Library: NtosKrnl.lib; IRQL:
    <= DISPATCH_LEVEL". Asked to "allocate memory from the kernel pool",
    docs_find could not reach ExAllocatePool2 -- the word "allocate" was not
    in the searched text. Ranking cannot fix an absent word; the 25-question
    set scored 4% top-1 largely on this.
    """

    def test_the_description_is_appended_to_the_contract(self):
        from argus.packs.sources.microsoft_docs import _requirement_line

        line = _requirement_line({
            "req.header": "wdm.h",
            "req.lib": "NtosKrnl.lib",
            "req.irql": "<= DISPATCH_LEVEL",
            "description": "Allocates pool memory of the specified type.",
        })

        assert "Header: wdm.h" in line
        assert "allocates pool memory" in line.lower(), (
            "the words a description search needs are missing")

    def test_the_contract_stays_first_and_semicolon_separated(self):
        """`docs_contracts` splits this field on ';' to pull out the IRQL and
        library. Prose in front of that would change what the parse sees, so
        the description goes after a ' -- ' marker instead."""
        from argus.packs.sources.microsoft_docs import _requirement_line

        line = _requirement_line({
            "req.header": "wdm.h", "req.irql": "<= APC_LEVEL",
            "description": "Creates a device object; returns status.",
        })

        contract = line.split(" -- ", 1)[0]
        fields = [p.strip() for p in contract.split(";")]
        assert fields[0] == "Header: wdm.h"
        assert any(f.startswith("IRQL:") for f in fields)
        assert "device object" not in contract, "prose leaked into the contract"

    def test_a_page_without_a_description_is_unchanged(self):
        from argus.packs.sources.microsoft_docs import _requirement_line

        line = _requirement_line({"req.header": "wdm.h", "req.lib": "x.lib"})
        assert line == "Header: wdm.h; Library: x.lib"
        assert " -- " not in line

    def test_a_page_with_only_a_description_still_yields_one(self):
        from argus.packs.sources.microsoft_docs import _requirement_line

        assert _requirement_line({"description": "Does a thing."}) == "Does a thing."
