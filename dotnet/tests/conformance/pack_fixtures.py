"""Small documentation corpora, one per pack source adapter.

Each is shaped like the real upstream repository the adapter was written for
(front matter, directory layout, file naming), and deliberately includes the
cases each adapter special-cases: TOC files, translations, aliases, a UID with
no entity, JSX lines, fenced code containing headings, reST titles with
overlines, character references in HTML, script blocks, an ECMA XML type with
cref links, and so on. Two adapters use the repository's own test fixtures.
"""
from __future__ import annotations

import shutil
from pathlib import Path

REPO = Path(__file__).resolve().parents[3]


def w(root: Path, rel: str, text: str) -> None:
    path = root / rel
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding="utf-8")


SDK_PAGE = """---
UID: NF:winuser.MessageBox
title: MessageBox function (winuser.h)
description: Displays a modal dialog box.
req.header: winuser.h
req.lib: User32.lib
req.dll: User32.dll
req.target-min-winverclnt: Windows 2000 Professional [desktop apps only]
req.unicode-ansi: MessageBoxW (Unicode) and MessageBoxA (ANSI)
api_name: ["MessageBox","MessageBoxA","MessageBoxW"]
f1_keywords:
 - winuser/MessageBox
---

# MessageBox function

## -description

Displays a modal dialog box that contains a system icon, a set of buttons, and a brief message.

## -syntax

```cpp
int MessageBox(
  [in, optional] HWND    hWnd,
  [in, optional] LPCTSTR lpText
);
```

## -parameters

### -param hWnd [in, optional]

A handle to the owner window.
"""

METHOD_PAGE = """---
UID: NF:mfcaptureengine.IMFCaptureSource.GetMirrorState
title: IMFCaptureSource::GetMirrorState (mfcaptureengine.h)
description: Learn more about: Gets the mirror state.
req.header: mfcaptureengine.h
req.lib: Mfuuid.lib
---

Gets the mirror state of the video preview stream.
"""

WDK_PAGE = """---
UID: NF:wdm.ExAllocateFromLookasideListEx
title: ExAllocateFromLookasideListEx function (wdm.h)
description: The ExAllocateFromLookasideListEx routine removes the first entry from the specified lookaside list.
req.header: wdm.h (include Wdm.h, Ntddk.h, Ntifs.h)
req.irql: <= DISPATCH_LEVEL
req.lib: NtosKrnl.lib
req.dll: NtosKrnl.exe
req.kmdf-ver: 1.0
api_name:
 - ExAllocateFromLookasideListEx
---

## -description

The **ExAllocateFromLookasideListEx** routine removes the first entry.
"""

MODULE_PAGE = """---
UID: NA:wdm
title: wdm.h header
description: This header is used by kernel.
---

# wdm.h header
"""

CPP_PAGE = """---
title: "vector class"
description: "Reference for the C++ Standard Library container class vector."
f1_keywords: ["vector/std::vector", "vector/std::vector::push_back", "vector_class", "two words"]
---
# `vector` class

The C++ Standard Library vector class is a class template for sequence containers.

- a list item
"""

DEBUGGER_CMD = """---
title: "lm (List Loaded Modules)"
description: "The lm command displays the specified loaded modules."
topic_type:
- apiref
api_name:
- lm
- lmv
---

# lm (List Loaded Modules)

The **lm** command displays the specified loaded modules.
"""

DEBUGGER_BANG = """---
title: "!analyze, !analyze2"
description: "The !analyze extension displays information about the current exception or bug check."
topic_type: ["apiref"]
api_name: ["!analyze", "lm", "NA"]
---

The **!analyze** extension displays information.
"""

DEBUGGER_GUIDE = """---
title: Getting started with WinDbg
description: Learn how to start debugging.
---

# Getting started

Start WinDbg.
"""

PS_CMDLET = """---
external help file: Microsoft.PowerShell.Commands.Utility.dll-Help.xml
Module Name: Microsoft.PowerShell.Utility
online version: https://learn.microsoft.com/powershell/module/microsoft.powershell.utility/export-csv
title: Export-Csv
---

# Export-Csv

## SYNOPSIS
Converts objects into a series of CSV strings and saves the strings to a file.

## SYNTAX

```
Export-Csv [[-Path] <string>]
```
"""

ROBOCOPY = """---
title: robocopy
description: Reference article for the robocopy command, which copies file data.
---

# robocopy

Copies file data from one location to another.

| Option | Description |
|--|--|
| /mir | Mirrors a directory tree |
"""

TLDR = """# tar

> Archiving utility.
> [!NOTE] not a summary.

- Create an archive:

`tar cf {{target.tar}} {{file1}}`
"""

HTML_INDEX = "<html><head><title>SQLite Home Page</title></head><body><h1>SQLite</h1><p>Small. Fast.</p></body></html>"

HTML_SELECT = """<!DOCTYPE html>
<html><head><title>SELECT</title><style>p { color: red }</style>
<script>var x = "<p>not text</p>";</script></head>
<body><nav>Home | About</nav>
<h1>SELECT</h1>
<p>The SELECT statement is used to query the database &amp; return rows &lt;like this&gt;.</p>
<!-- a comment <h2>ignored</h2> -->
<h2>Simple   Select
Processing</h2>
<table><tr><td>a</td><td>b</td></tr></table>
<ul><li>one</li><li>two<br>lines</li></ul>
<pre>SELECT * FROM t;</pre>
<footer>copyright</footer>
</body></html>"""

HTML_CREATE = """<html><head><title>CREATE TABLE (with a very long title that goes past forty characters)</title></head>
<body><h1>CREATE TABLE</h1><p>Creates a table.</p></body></html>"""

HTML_INSERT = """<html><head><title>INSERT.</title></head><body><h1>INSERT</h1><p>The INSERT statement comes in three forms &mdash; values, select, default.</p></body></html>"""

CPPREF_VECTOR = """<html><head><title>std::vector - cppreference.com</title></head>
<body><h1 id="firstHeading">std::vector</h1><p>Defined in header &lt;vector&gt;</p>
<p>std::vector is a sequence container that encapsulates dynamic size arrays.</p></body></html>"""

DOTNET_TYPE = """<Type Name="String" FullName="System.String">
  <TypeSignature Language="C#" Value="public sealed class String : IComparable" />
  <TypeSignature Language="DocId" Value="T:System.String" />
  <Docs>
    <summary>Represents text as a sequence of <see cref="T:System.Char" /> values. See <see langword="null" />.</summary>
    <remarks>
      <para>A string is a <paramref name="value" /> collection.</para>
    </remarks>
  </Docs>
  <Members>
    <Member MemberName="Concat">
      <MemberSignature Language="C#" Value="public static string Concat (string str0, string str1);" />
      <MemberSignature Language="DocId" Value="M:System.String.Concat(System.String,System.String)" />
      <Docs><summary>Concatenates two specified instances of <see cref="T:System.String" />.</summary></Docs>
    </Member>
    <Member MemberName="op_Equality">
      <MemberSignature Language="C#" Value="public static bool operator == (string a, string b);" />
      <Docs><summary>Equality.</summary></Docs>
    </Member>
    <Member MemberName="Length">
      <MemberSignature Language="C#" Value="public int Length { get; }" />
      <MemberSignature Language="DocId" Value="P:System.String.Length" />
      <Docs><summary>Gets the number of characters.</summary></Docs>
    </Member>
  </Members>
</Type>
"""

DOTNET_GENERIC = """<Type Name="List&lt;T&gt;" FullName="System.Collections.Generic.List&lt;T&gt;">
  <TypeSignature Language="C#" Value="public class List&lt;T&gt;" />
  <Docs><summary>Represents a strongly typed list of objects.</summary></Docs>
</Type>
"""


def build(root: Path, corpus: dict[str, Path]) -> dict[str, tuple[str, Path]]:
    """Create every corpus under `root`. Returns source name -> (label, work dir)."""
    if root.exists():
        shutil.rmtree(root)
    out: dict[str, tuple[str, Path]] = {}

    # The repository's own fixtures.
    shutil.copytree(REPO / "tests/packs/fixtures/python", root / "python")
    out["python"] = ("python", root / "python")
    shutil.copytree(REPO / "tests/packs/fixtures/react", root / "react")
    out["react"] = ("react", root / "react")

    sdk = root / "win32-docs"
    w(sdk, "sdk-api-src/content/winuser/nf-winuser-messagebox.md", SDK_PAGE)
    w(sdk, "sdk-api-src/content/mfcaptureengine/nf-mfcaptureengine-imfcapturesource-getmirrorstate.md", METHOD_PAGE)
    w(sdk, "sdk-api-src/content/winuser/TOC.md", "# TOC\n")
    out["win32-docs"] = ("win32", sdk)

    wdk = root / "wdk-docs"
    w(wdk, "wdk-ddi-src/content/wdm/nf-wdm-exallocatefromlookasidelistex.md", WDK_PAGE)
    w(wdk, "wdk-ddi-src/content/wdm/index.md", MODULE_PAGE)
    out["wdk-docs"] = ("wdk", wdk)

    cpp = root / "cpp"
    w(cpp, "docs/standard-library/vector-class.md", CPP_PAGE)
    w(cpp, "docs/standard-library/TOC.md", "# toc\n")
    out["cpp"] = ("cpp", cpp)

    dbg = root / "debugger"
    w(dbg, "windows-driver-docs-pr/debuggercmds/lm--list-loaded-modules-.md", DEBUGGER_CMD)
    w(dbg, "windows-driver-docs-pr/debuggercmds/-analyze.md", DEBUGGER_BANG)
    w(dbg, "windows-driver-docs-pr/debugger/getting-started.md", DEBUGGER_GUIDE)
    out["debugger"] = ("debugger", dbg)

    sql = root / "sqlite"
    w(sql, "sqlite-doc-3530400/index.html", HTML_INDEX)
    w(sql, "sqlite-doc-3530400/lang_select.html", HTML_SELECT)
    w(sql, "sqlite-doc-3530400/lang_createtable.html", HTML_CREATE)
    w(sql, "sqlite-doc-3530400/lang_insert.html", HTML_INSERT)
    w(sql, "sqlite-doc-3530400/lang.html", "<html><head><title>SQL</title></head><body><p>Index.</p></body></html>")
    out["sqlite"] = ("sqlite", sql)

    cppref = root / "cppreference"
    w(cppref, "reference/en/cpp/container/vector.html", CPPREF_VECTOR)
    w(cppref, "reference/en/cpp/language/lambda.html", "<html><body><h1>Lambda</h1><p>Closures.</p></body></html>")
    w(cppref, "reference/en/cpp/algorithm/sort.html", "<html><head><title>std::sort</title></head><body><p>Sorts.</p></body></html>")
    out["cppreference"] = ("cppreference", cppref)

    dotnet = root / "dotnet"
    w(dotnet, "xml/System/String.xml", DOTNET_TYPE)
    w(dotnet, "xml/System.Collections.Generic/List`1.xml", DOTNET_GENERIC)
    w(dotnet, "xml/ns-System.xml", "<Namespace Name=\"System\"/>")
    w(dotnet, "xml/System/Evil.xml", "<!DOCTYPE x [<!ENTITY a \"b\">]><Type Name=\"Evil\" FullName=\"System.Evil\"/>")
    w(dotnet, "xml/System/Broken.xml", "<Type Name=\"Broken\"")
    out["dotnet"] = ("dotnet", dotnet)

    sd = root / "system-design"
    w(sd, "README.md", "# The System Design Primer\n\nLearn how to design large-scale systems.\n")
    w(sd, "README-zh-Hans.md", "# 系统设计入门\n")
    w(sd, "solutions/system_design/pastebin/README.md", "# Design Pastebin.com\n\nA paste service.\n")
    w(sd, "solutions/system_design/web_crawler/README.md", "# Design a web crawler\n")
    w(sd, "CONTRIBUTING.md", "# Contributing\n")
    out["system-design"] = ("system-design", sd)

    alg = root / "algorithms"
    w(alg, "sorting/quick_sort.cpp", "/** Quick sort. */\nvoid quick_sort(int *a, int n) {}\n")
    w(alg, "sorting/bubble-sort.cpp", "void bubble() {}\n")
    w(alg, "math/gcd.h", "int gcd(int a, int b);\n")
    w(alg, "build/generated.cpp", "int ignored;\n")
    out["algorithms"] = ("algorithms", alg)

    # Composites: every part is a checkout beneath the work dir.
    scripting = root / "scripting"
    w(scripting, "PowerShell-Docs/reference/7.5/Microsoft.PowerShell.Utility/Export-Csv.md", PS_CMDLET)
    w(scripting, "PowerShell-Docs/reference/docs-conceptual/overview.md", "# Overview\n")
    w(scripting, "windowsserverdocs/WindowsServerDocs/administration/windows-commands/robocopy.md", ROBOCOPY)
    w(scripting, "tldr/pages/common/tar.md", TLDR)
    w(scripting, "tldr/pages/README.md", "# skip me\n")
    out["scripting"] = ("scripting", scripting)

    win32 = root / "win32"
    w(win32, "sdk-api/sdk-api-src/content/winuser/nf-winuser-messagebox.md", SDK_PAGE)
    samples = win32 / "Windows-classic-samples" / "Samples"
    for rel in ("MessageBoxDemo/cpp/main.cpp", "MessageBoxDemo/cpp/main.h"):
        w(samples, rel, "#include <windows.h>\nint main() { return MessageBoxW(NULL, L\"hi\", L\"t\", MB_OK); }\n")
    w(samples, "Weird Name!/x.c", "int x;\n")
    out["win32"] = ("win32", win32)

    # A real code tree as a samples pack.
    lz4 = root / "wdk-samples"
    shutil.copytree(corpus["lz4"], lz4, ignore=shutil.ignore_patterns(".git"))
    out["wdk-samples"] = ("wdk-samples", lz4)
    return out
