"""Packs built from more than one checkout.

The Win32 and WDK references answer "what does this API do"; the sample
repositories answer "show me one working". Those are halves of the same
question, and a developer asking either wants both -- so they belong in one
pack rather than two the user has to know to install together.

The build pipeline takes a single ``--work-dir``, so a composite is pointed at
the *parent* directory holding both checkouts and resolves each part beneath
it. That keeps the CLI unchanged: `--work-dir .packwork/docsrc` instead of
`--work-dir .packwork/docsrc/sdk-api`.

Two things this has to get right.

**Paths must not collide.** sdk-api has `winuser/nf-winuser-messagebox.md` and
the samples have `Foo/foo.cpp`; nothing stops two parts producing the same
relative path, and `docs.path` is the key symbols resolve against. Every path
is therefore prefixed with the part's name, which also makes a retrieved
result say which corpus it came from.

**Licences must not be flattened.** A pack carries one licence string, but
these parts differ -- the WDK reference is CC-BY-4.0 while the driver samples
are MS-PL. Silently recording one of them would misstate the terms under which
the other is redistributed, so the combined string names both, per part.
"""

from __future__ import annotations

from dataclasses import dataclass, replace
from pathlib import Path
from typing import Iterator

from .base import ApiSymbol, Doc, Source
from .code_samples import WindowsClassicSamples, WindowsDriverSamples
from .microsoft_docs import WdkDdi, Win32Api
from .scripting import PowerShellDocs, TldrPages, WindowsCommands


class CompositeError(RuntimeError):
    """A part's checkout is missing under the work directory."""


@dataclass(frozen=True)
class _Part:
    #: Directory beneath the work dir holding this part's checkout.
    checkout: str
    source: Source
    #: Prefix applied to every doc path from this part.
    prefix: str


@dataclass(frozen=True)
class _Composite:
    name: str = ""
    parts: tuple[_Part, ...] = ()
    repo_url: str = ""
    branch: str = ""
    subtree: str = ""
    license: str = ""
    license_url: str = ""
    attribution: str = ""

    def _resolve(self, root: Path, part: _Part) -> Path:
        path = root / part.checkout
        if not path.is_dir():
            # Loud, and naming the exact directory. A composite silently
            # skipping a missing part builds a pack that looks complete and is
            # half empty -- the failure would only surface as a lookup that
            # finds nothing, months later.
            raise CompositeError(
                f"{self.name}: no checkout for '{part.source.name}' at {path}. "
                f"Composite sources need every part cloned beneath --work-dir; "
                f"they cannot be fetched with --fetch."
            )
        return path

    def part_checkouts(self, root: Path) -> list[tuple[str, Path]]:
        """Each part's name and checkout directory, for provenance.

        A composite pack's provenance is genuinely several commits, and the
        parent directory it is built from is not a git checkout at all -- so
        the single `resolve_commit(work_dir)` the builder uses finds nothing
        and refuses to build. The builder asks for this instead and records
        one commit per part.

        Deliberately returns the directories rather than the commits: reading
        git is the builder's job, and importing it here would be a cycle
        (build imports sources).
        """
        return [(part.source.name, self._resolve(root, part))
                for part in self.parts]

    def iter_docs(self, root: Path) -> Iterator[Doc]:
        for part in self.parts:
            base = self._resolve(root, part)
            for doc in part.source.iter_docs(base):
                yield replace(doc, path=f"{part.prefix}/{doc.path}")

    def iter_symbols(self, root: Path) -> Iterator[ApiSymbol]:
        for part in self.parts:
            base = self._resolve(root, part)
            for symbol in part.source.iter_symbols(base):
                # Prefixed identically to iter_docs: the builder links a symbol
                # to its page by normalised doc path, so a mismatch here does
                # not error -- it silently drops every symbol as "names a page
                # this pack does not contain".
                yield replace(symbol,
                              doc_path=f"{part.prefix}/{symbol.doc_path}")


def _combined_licence(parts: tuple[_Part, ...]) -> str:
    return " AND ".join(dict.fromkeys(p.source.license for p in parts))


def _combined_attribution(parts: tuple[_Part, ...]) -> str:
    return " ".join(p.source.attribution for p in parts)


_WIN32_PARTS = (
    _Part("sdk-api", Win32Api(), "api"),
    _Part("Windows-classic-samples", WindowsClassicSamples(), "samples"),
)
_WDK_PARTS = (
    _Part("windows-driver-docs-ddi", WdkDdi(), "ddi"),
    _Part("Windows-driver-samples", WindowsDriverSamples(), "samples"),
)


@dataclass(frozen=True)
class Win32WithSamples(_Composite):
    """Win32 API reference plus the Windows desktop samples."""

    name: str = "win32"
    parts: tuple[_Part, ...] = _WIN32_PARTS
    repo_url: str = "https://github.com/MicrosoftDocs/sdk-api"
    branch: str = "docs"
    license: str = _combined_licence(_WIN32_PARTS)
    license_url: str = "https://github.com/MicrosoftDocs/sdk-api/blob/docs/LICENSE"
    attribution: str = _combined_attribution(_WIN32_PARTS)


@dataclass(frozen=True)
class WdkWithSamples(_Composite):
    """WDK DDI reference plus the driver samples.

    The parts are licensed differently -- CC-BY-4.0 for the reference, MS-PL
    for the samples -- which is why the combined string names both.
    """

    name: str = "wdk"
    parts: tuple[_Part, ...] = _WDK_PARTS
    repo_url: str = "https://github.com/MicrosoftDocs/windows-driver-docs-ddi"
    branch: str = "staging"
    license: str = _combined_licence(_WDK_PARTS)
    license_url: str = (
        "https://github.com/MicrosoftDocs/windows-driver-docs-ddi/blob/staging/LICENSE")
    attribution: str = _combined_attribution(_WDK_PARTS)


_SCRIPTING_PARTS = (
    _Part("PowerShell-Docs", PowerShellDocs(), "powershell"),
    _Part("windowsserverdocs", WindowsCommands(), "cmd"),
    _Part("tldr", TldrPages(), "tldr"),
)


@dataclass(frozen=True)
class ScriptingDocs(_Composite):
    """PowerShell, the Windows command line, and Unix tools in one pack.

    "How do I do X from a script" does not arrive already sorted by shell.
    Someone automating a Windows machine moves between robocopy, Get-ChildItem
    and grep inside one task, and three packs would mean three installs and
    three chances to be missing the one that had the answer.

    The reference halves (PowerShell, Windows commands) say what every flag
    does; tldr says what the command looks like in use. Most scripting
    questions want the second and then the first.
    """

    name: str = "scripting"
    parts: tuple[_Part, ...] = _SCRIPTING_PARTS
    repo_url: str = "https://github.com/MicrosoftDocs/PowerShell-Docs"
    branch: str = "main"
    license: str = _combined_licence(_SCRIPTING_PARTS)
    license_url: str = ("https://github.com/MicrosoftDocs/PowerShell-Docs/blob/"
                        "main/LICENSE.md")
    attribution: str = _combined_attribution(_SCRIPTING_PARTS)
