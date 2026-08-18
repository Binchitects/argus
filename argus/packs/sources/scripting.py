"""Shell scripting: PowerShell, the Windows command line, and Unix tools.

One pack, because the question "how do I do X from a script" does not arrive
already sorted by shell. Somebody automating a Windows box moves between
`robocopy`, `Get-ChildItem` and `grep` inside a single task, and three packs
would mean three installs and three chances to be missing the one that had the
answer.

All three parts share the property that makes a good inventory: **the filename
is the command name**. `Get-ChildItem.md`, `robocopy.md`, `pages/common/git.md`
-- no parsing, no guessing, and the name is exactly what a person types. So one
adapter serves all three; they differ only in subtree, canonical URL, and how
the human-readable title is found.

All CC-BY-4.0.
"""

from __future__ import annotations

import re
from dataclasses import dataclass, field
from pathlib import Path
from typing import Iterator

from .base import ApiSymbol, Doc

_FRONT_MATTER = re.compile(r"\A---\r?\n(.*?)\r?\n---\r?\n?", re.DOTALL)
_SCALAR = re.compile(r"^([A-Za-z_][\w.\- ]*)\s*:\s*(.*)$")
_ATX = re.compile(r"^#{1,6}\s+(.+?)\s*#*\s*$", re.MULTILINE)
#: tldr states the summary as a blockquote under the heading.
_TLDR_SUMMARY = re.compile(r"^>\s*(.+?)\s*$", re.MULTILINE)
#: PowerShell's one-line summary. Its cmdlet pages carry no `description`
#: front matter, so without this the extractor fell through to the first
#: blockquote -- which is usually an admonition, not a description.
_SYNOPSIS = re.compile(r"^##\s+SYNOPSIS\s*$\s*\n+(.+?)\s*$", re.MULTILINE)

#: Module landing pages carry no SYNOPSIS -- they document a module rather
#: than a cmdlet, and head their prose with `## Description` instead.
#: Measured: 28 of the scripting pack's symbols reached neither this nor the
#: tldr blockquote and shipped with a blank signature, which `docs_find`
#: skips. They are the pages that answer "which module has Get-Process".
_DESCRIPTION_SECTION = re.compile(
    r"^##\s+Description\s*$\s*\n+(.+?)\s*$", re.MULTILINE | re.IGNORECASE)
#: `[!NOTE]`, `[!WARNING]`, `[!CAUTION]` -- a blockquote that is markup rather
#: than prose.
_ADMONITION = re.compile(r"^\[!\w+\]")

_SKIP_NAMES = frozenset({
    "README.md", "TOC.md", "CONTRIBUTING.md", "LICENSE.md", "index.md",
    "CODE_OF_CONDUCT.md", "SECURITY.md",
})


def _front_matter(text: str) -> tuple[dict[str, str], str]:
    match = _FRONT_MATTER.match(text)
    if not match:
        return {}, text
    meta: dict[str, str] = {}
    for line in match.group(1).splitlines():
        found = _SCALAR.match(line)
        if found:
            meta[found.group(1).strip()] = found.group(2).strip().strip('"').strip("'")
    return meta, text[match.end():]


@dataclass(frozen=True)
class _CommandDocs:
    """A corpus of one-command-per-file reference pages."""

    name: str = ""
    repo_url: str = ""
    branch: str = "main"
    subtree: str = ""
    base_url: str = ""
    license: str = "CC-BY-4.0"
    license_url: str = ""
    attribution: str = ""
    kind: str = "command"
    #: Only these top-level directories under the subtree, if given. Used to
    #: keep PowerShell to the versions worth shipping.
    only_dirs: tuple[str, ...] = ()

    def _root(self, root: Path) -> Path:
        return root / self.subtree if self.subtree else root

    def _files(self, root: Path) -> Iterator[Path]:
        base = self._root(root)
        if not base.is_dir():
            return
        for path in sorted(base.rglob("*.md")):
            if not path.is_file() or path.name in _SKIP_NAMES:
                continue
            if ".git" in path.parts:
                continue
            relative = path.relative_to(base)
            if self.only_dirs and relative.parts[0] not in self.only_dirs:
                continue
            yield path

    def _title_and_body(self, path: Path) -> tuple[str, str, str]:
        raw = path.read_text(encoding="utf-8", errors="replace")
        meta, body = _front_matter(raw)
        title = meta.get("title", "")
        summary = meta.get("description", "")
        if not title:
            heading = _ATX.search(body)
            title = heading.group(1).strip().strip("`") if heading else path.stem
        if not summary:
            # PowerShell cmdlet pages carry no `description` front matter --
            # the one-line summary is the SYNOPSIS section. Reaching straight
            # for the first blockquote instead found `> [!NOTE]`, because
            # admonitions are blockquotes too: measured, 244 of 9,302 symbol
            # descriptions in the built pack were a bare admonition marker and
            # 675 were under 15 characters. Those descriptions are what
            # docs_find searches, so a marker is a symbol nobody can find.
            synopsis = _SYNOPSIS.search(body)
            if synopsis:
                summary = synopsis.group(1).strip()
        if not summary:
            section = _DESCRIPTION_SECTION.search(body)
            if section:
                summary = section.group(1).strip()
        if not summary:
            for quoted in _TLDR_SUMMARY.finditer(body):
                candidate = quoted.group(1).strip()
                if not _ADMONITION.match(candidate):
                    summary = candidate
                    break
        return title, summary, body

    def _url(self, relative: str, meta_url: str = "") -> str:
        return meta_url or (self.base_url + relative)

    def iter_docs(self, root: Path) -> Iterator[Doc]:
        base = self._root(root)
        for path in self._files(root):
            raw = path.read_text(encoding="utf-8", errors="replace")
            meta, body = _front_matter(raw)
            title, summary, body = self._title_and_body(path)
            relative = path.relative_to(base).as_posix()
            command = path.stem
            # The command name leads the indexed text. These pages are titled
            # things like "Robocopy" or "Get-ChildItem", but the surrounding
            # prose often never repeats the name a person searched for, and a
            # chunk from the middle of an options table would otherwise carry
            # no clue which command it documents.
            header = f"# {command}\n\n{title}"
            if summary:
                header += f"\n\n{summary}"
            yield Doc(
                path=relative,
                title=title or command,
                url=self._url(relative, meta.get("online version", "")),
                lang="md",
                body=f"{header}\n\n{body}",
            )

    def iter_symbols(self, root: Path) -> Iterator[ApiSymbol]:
        base = self._root(root)
        for path in self._files(root):
            relative = path.relative_to(base).as_posix()
            command = path.stem
            if not command:
                continue
            parts = relative.split("/")
            # PowerShell: "7.5/Microsoft.PowerShell.Management/X.md" -> the
            # version, so a lookup can tell 5.1's cmdlet from 7.5's. tldr:
            # "common/git.md" -> the platform. Windows commands are flat.
            namespace = parts[0] if len(parts) > 1 else self.name
            title, summary, _ = self._title_and_body(path)
            yield ApiSymbol(
                name=command,
                kind=self.kind,
                namespace=namespace,
                doc_path=relative,
                anchor="",
                signature=summary[:200],
            )


@dataclass(frozen=True)
class PowerShellDocs(_CommandDocs):
    """PowerShell cmdlet reference (MicrosoftDocs/PowerShell-Docs).

    Only two of the five version trees are shipped. The repo carries 5.1, 7.4,
    7.5, 7.6 and 7.7, and every cmdlet exists in all of them -- five
    near-identical pages for `Get-ChildItem` alone. That is precisely the
    near-duplicate crowding measured against the wdk pack, where five variants
    of one API filled a result set and pushed the real answer to sixth.

    5.1 is Windows PowerShell, which ships in the box and is what most existing
    scripts target. 7.5 is the current cross-platform release. 7.6 and 7.7 are
    in development, and shipping unreleased behaviour as reference is worse
    than omitting it.
    """

    name: str = "powershell"
    repo_url: str = "https://github.com/MicrosoftDocs/PowerShell-Docs"
    branch: str = "main"
    subtree: str = "reference"
    base_url: str = ("https://github.com/MicrosoftDocs/PowerShell-Docs/blob/"
                     "main/reference/")
    license_url: str = ("https://github.com/MicrosoftDocs/PowerShell-Docs/blob/"
                        "main/LICENSE.md")
    attribution: str = ("PowerShell documentation. Copyright (c) Microsoft "
                        "Corporation. Used under CC BY 4.0.")
    kind: str = "cmdlet"
    only_dirs: tuple[str, ...] = ("5.1", "7.5")


@dataclass(frozen=True)
class WindowsCommands(_CommandDocs):
    """The Windows command-line reference: cmd built-ins and shipped tools."""

    name: str = "windows-commands"
    repo_url: str = "https://github.com/MicrosoftDocs/windowsserverdocs"
    branch: str = "main"
    subtree: str = "WindowsServerDocs/administration/windows-commands"
    base_url: str = ("https://learn.microsoft.com/en-us/windows-server/"
                     "administration/windows-commands/")
    license_url: str = ("https://github.com/MicrosoftDocs/windowsserverdocs/"
                        "blob/main/LICENSE")
    attribution: str = ("Windows Commands reference. Copyright (c) Microsoft "
                        "Corporation. Used under CC BY 4.0.")
    kind: str = "command"


@dataclass(frozen=True)
class TldrPages(_CommandDocs):
    """tldr-pages: worked examples for common commands, every platform.

    The practical half of this pack. Reference pages say what every flag does;
    tldr says what the command looks like when you actually use it, which is
    the shape most scripting questions take.

    English only -- `pages/` is the English tree and translations live in
    sibling `pages.xx/` directories, which are excluded by the subtree rather
    than by filtering.
    """

    name: str = "tldr"
    repo_url: str = "https://github.com/tldr-pages/tldr"
    branch: str = "main"
    subtree: str = "pages"
    base_url: str = "https://github.com/tldr-pages/tldr/blob/main/pages/"
    license_url: str = "https://github.com/tldr-pages/tldr/blob/main/LICENSE.md"
    attribution: str = "tldr-pages. Used under CC BY 4.0."
    kind: str = "example"
