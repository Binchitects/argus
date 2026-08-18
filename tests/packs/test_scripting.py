"""PowerShell, Windows commands and tldr, in one scripting pack."""
from pathlib import Path

from argus.packs.build import _doc_key
from argus.packs.sources import ScriptingDocs
from argus.packs.sources.scripting import (PowerShellDocs, TldrPages,
                                           WindowsCommands)


def _w(root: Path, rel: str, text: str) -> None:
    p = root / rel
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_text(text, encoding="utf-8")


PS = ("---\ntitle: Get-ChildItem\nModule Name: Microsoft.PowerShell.Management\n"
      "online version: https://learn.microsoft.com/x\n---\n\n# Get-ChildItem\n\nGets items.\n")
CMD = ("---\ntitle: Robocopy\ndescription: Copies file data.\n---\n\n"
       "# robocopy\n\nCopies files.\n")
TLDR = "# git\n\n> Distributed version control system.\n\n- Clone:\n\n`git clone {{url}}`\n"


def _tree(tmp_path):
    for v in ("5.1", "7.4", "7.5", "7.6", "7.7"):
        _w(tmp_path, f"PowerShell-Docs/reference/{v}/Mgmt/Get-ChildItem.md", PS)
    _w(tmp_path, "windowsserverdocs/WindowsServerDocs/administration/"
                 "windows-commands/robocopy.md", CMD)
    _w(tmp_path, "tldr/pages/common/git.md", TLDR)
    _w(tmp_path, "tldr/pages.de/common/git.md", TLDR)     # translation
    return tmp_path


def test_only_shipped_powershell_versions_are_indexed(tmp_path):
    """The repo carries 5.1, 7.4, 7.5, 7.6 and 7.7, and every cmdlet exists in
    all five. Shipping them all puts five near-identical Get-ChildItem pages in
    one corpus -- the same near-duplicate crowding measured on the wdk pack,
    where five variants of one API filled a result set and pushed the real
    answer to sixth. 7.6 and 7.7 are also unreleased."""
    _tree(tmp_path)
    versions = {s.namespace for s in
                PowerShellDocs().iter_symbols(tmp_path / "PowerShell-Docs")}
    assert versions == {"5.1", "7.5"}


def test_translations_are_outside_the_indexed_subtree(tmp_path):
    """tldr keeps translations in sibling pages.xx/ directories, so the English
    subtree excludes them structurally rather than by filtering names."""
    _tree(tmp_path)
    paths = {d.path for d in TldrPages().iter_docs(tmp_path / "tldr")}
    assert paths == {"common/git.md"}


def test_the_command_name_leads_the_indexed_text(tmp_path):
    """These pages are titled "Robocopy", and the prose often never repeats the
    name a person searched for -- a chunk from the middle of an options table
    would carry no clue which command it documents."""
    _tree(tmp_path)
    doc = next(iter(WindowsCommands().iter_docs(
        tmp_path / "windowsserverdocs")))
    assert doc.body.startswith("# robocopy")


def test_the_canonical_url_wins_over_a_repo_path(tmp_path):
    """PowerShell pages carry the published learn.microsoft.com URL in front
    matter. Citing a GitHub blob when the real page exists is worse for a
    reader following a result."""
    _tree(tmp_path)
    doc = next(d for d in PowerShellDocs().iter_docs(tmp_path / "PowerShell-Docs")
               if d.path.endswith("Get-ChildItem.md"))
    assert doc.url == "https://learn.microsoft.com/x"


def test_reference_and_worked_example_both_answer_one_name(tmp_path):
    """The point of one pack: `robocopy` should offer the flag reference AND
    what it looks like in use, without the caller choosing a pack first."""
    _tree(tmp_path)
    _w(tmp_path, "tldr/pages/windows/robocopy.md",
       "# robocopy\n\n> Copy files.\n\n- Mirror:\n\n`robocopy {{a}} {{b}} /mir`\n")
    syms = [s for s in ScriptingDocs().iter_symbols(tmp_path)
            if s.name.lower() == "robocopy"]
    assert {s.kind for s in syms} == {"command", "example"}


MODULE_PAGE = (
    "---\ntitle: Microsoft.PowerShell.Management\n"
    "Module Name: Microsoft.PowerShell.Management\n---\n\n"
    "# Microsoft.PowerShell.Management Module\n\n"
    "## Description\n\n"
    "Contains cmdlets that help you manage Windows in PowerShell.\n\n"
    "## Cmdlets\n\n### [Add-Content](Add-Content.md)\nAppends content.\n"
)


def test_a_module_landing_page_is_described_by_its_description_section(tmp_path):
    """Module pages document a module rather than a cmdlet, so they carry no
    SYNOPSIS and headed their prose with `## Description` instead. Measured:
    28 of the pack's symbols reached no fallback at all and shipped with a
    blank signature, which docs_find skips -- so "which module has
    Get-Process" could not be answered by the pages that exist to answer it."""
    _tree(tmp_path)
    _w(tmp_path, "PowerShell-Docs/reference/7.5/Microsoft.PowerShell.Management/"
                 "Microsoft.PowerShell.Management.md", MODULE_PAGE)
    module = next(s for s in ScriptingDocs().iter_symbols(tmp_path)
                  if s.name == "Microsoft.PowerShell.Management")
    assert module.signature == (
        "Contains cmdlets that help you manage Windows in PowerShell.")


def test_the_synopsis_still_wins_over_a_description_section(tmp_path):
    """Ordering matters: a cmdlet page with both must keep its SYNOPSIS, which
    is the one-line summary, not the longer prose further down."""
    _tree(tmp_path)
    _w(tmp_path, "PowerShell-Docs/reference/7.5/Mgmt/Get-Thing.md",
       "---\ntitle: Get-Thing\n---\n\n# Get-Thing\n\n"
       "## SYNOPSIS\n\nGets a thing.\n\n"
       "## Description\n\nA much longer explanation of getting things.\n")
    thing = next(s for s in ScriptingDocs().iter_symbols(tmp_path)
                 if s.name == "Get-Thing")
    assert thing.signature == "Gets a thing."


def test_every_scripting_symbol_resolves_to_a_page(tmp_path):
    _tree(tmp_path)
    src = ScriptingDocs()
    keys = {_doc_key(d.path) for d in src.iter_docs(tmp_path)}
    syms = list(src.iter_symbols(tmp_path))
    assert syms
    assert [s.name for s in syms if _doc_key(s.doc_path) not in keys] == []
