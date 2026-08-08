"""Source-code corpora: Microsoft's sample repositories and TheAlgorithms.

Three of the requested sources are code, not prose -- Windows-driver-samples,
Windows-classic-samples, and TheAlgorithms/C-Plus-Plus (360 .cpp files against
8 READMEs). They need the same treatment, so they share one adapter.

Two things make code different from documentation here.

**Chunking.** Everything in the build pipeline goes through `chunk_markdown`,
which splits at heading boundaries and prepends the heading trail to every
chunk. A raw .cpp file has no headings, so the entire file would become one
unstructured blob and each chunk would arrive at the embedder with no idea
which sample or which file it came from. So each file is presented with a
synthetic heading trail -- the sample name, then the path -- and the code
beneath it. Every chunk then carries "which sample, which file" into the
embedding, which is exactly what a search for "how do I queue a DPC" needs to
match on.

**Symbols.** There is no inventory. Parsing C++ with a regex to find function
definitions would produce a symbol table that is wrong in ways nobody can see,
and this project has already been bitten by confident-but-wrong extraction.
What *is* reliable is the layout: TheAlgorithms puts one algorithm per file
(`sorting/quick_sort.cpp` -> "quick_sort" in "sorting"), and the Microsoft
sample repos put one sample per directory. Those are real names a person would
search for, and they cost no guessing. That is the whole inventory -- so these
packs answer `docs_search` well and `docs_lookup` only for sample names.
"""

from __future__ import annotations

import re
from dataclasses import dataclass, field
from pathlib import Path
from typing import Iterator

from .base import ApiSymbol, Doc

#: Skip anything that is not source a human would read for an example.
DEFAULT_SUFFIXES = (".c", ".cpp", ".cxx", ".cc", ".h", ".hpp", ".hxx")

#: Build output, vendored dependencies and generated artifacts. Indexing these
#: buries the samples under noise nobody searches for.
SKIP_DIRS = frozenset({
    ".git", ".github", "build", "obj", "bin", "x64", "x86", "arm64", "debug",
    "release", "packages", "node_modules", "generated", "__pycache__",
    "third_party", "external", "vendor",
})

#: A single enormous generated file can dominate a pack. Measured against the
#: Microsoft samples, real example code sits far below this.
MAX_FILE_BYTES = 200_000

_WORD = re.compile(r"[A-Za-z0-9_]+")


def _readable(path: Path) -> str | None:
    try:
        if path.stat().st_size > MAX_FILE_BYTES:
            return None
        text = path.read_text(encoding="utf-8", errors="replace")
    except OSError:
        return None
    # A file that is mostly replacement characters is binary that happened to
    # carry a source suffix; embedding it wastes a slot and returns garbage.
    if text.count("�") > len(text) / 50:
        return None
    return text


@dataclass(frozen=True)
class _CodeRepo:
    """A repository of example source code."""

    name: str = ""
    repo_url: str = ""
    branch: str = ""
    subtree: str = ""
    base_url: str = ""
    license: str = ""
    license_url: str = ""
    attribution: str = ""
    suffixes: tuple[str, ...] = DEFAULT_SUFFIXES
    #: How many leading path components name the sample. TheAlgorithms uses
    #: one (`sorting/`), the Microsoft repos nest deeper (`general/echo/...`).
    sample_depth: int = 1

    def _root(self, root: Path) -> Path:
        return root / self.subtree if self.subtree else root

    def _files(self, root: Path) -> Iterator[Path]:
        base = self._root(root)
        if not base.is_dir():
            return
        for path in sorted(base.rglob("*")):
            if path.suffix.lower() not in self.suffixes or not path.is_file():
                continue
            parts = {p.lower() for p in path.relative_to(base).parts[:-1]}
            if parts & SKIP_DIRS:
                continue
            yield path

    def sample_of(self, relative: str) -> str:
        parts = relative.split("/")
        if len(parts) <= 1:
            return self.name
        return "/".join(parts[: self.sample_depth])

    def iter_docs(self, root: Path) -> Iterator[Doc]:
        base = self._root(root)
        for path in self._files(root):
            text = _readable(path)
            if text is None:
                continue
            relative = path.relative_to(base).as_posix()
            sample = self.sample_of(relative)
            # The synthetic heading trail is the whole point: chunk_markdown
            # prepends it to every chunk, so a fragment retrieved from the
            # middle of a 900-line driver still says which sample it is from.
            body = f"# {sample}\n\n## {relative}\n\n{text}"
            yield Doc(
                path=relative,
                title=f"{sample} - {path.name}",
                url=f"{self.base_url}{relative}",
                lang="md",
                body=body,
            )

    def iter_symbols(self, root: Path) -> Iterator[ApiSymbol]:
        base = self._root(root)
        seen: set[str] = set()
        for path in self._files(root):
            # Same readability filter as iter_docs, and for the same reason
            # inverted: a symbol must point at a page that actually exists.
            # iter_docs skips oversized and binary files, so anchoring a
            # sample to one of those produced a symbol whose doc was never
            # written -- it does not error, the builder just drops it as
            # "names a page this pack does not contain". Measured: the first
            # wdk composite shipped 3 such symbols.
            if _readable(path) is None:
                continue
            relative = path.relative_to(base).as_posix()
            sample = self.sample_of(relative)
            name = sample.rsplit("/", 1)[-1]
            if not name or name in seen or not _WORD.fullmatch(name):
                continue
            seen.add(name)
            yield ApiSymbol(
                name=name,
                kind="sample",
                namespace=self.name,
                doc_path=relative,
                anchor="",
                signature=f"sample: {sample}",
            )


@dataclass(frozen=True)
class WindowsDriverSamples(_CodeRepo):
    """Microsoft's WDK driver samples. MS-PL, not MIT -- checked, not assumed."""

    name: str = "wdk-samples"
    repo_url: str = "https://github.com/microsoft/Windows-driver-samples"
    branch: str = "main"
    subtree: str = ""
    base_url: str = ("https://github.com/microsoft/Windows-driver-samples/"
                     "blob/main/")
    license: str = "MS-PL"
    license_url: str = ("https://github.com/microsoft/Windows-driver-samples/"
                        "blob/main/LICENSE")
    attribution: str = (
        "Windows driver samples. Copyright (c) Microsoft Corporation. Used "
        "under the Microsoft Public License (MS-PL)."
    )
    #: "general/echo/kmdf/..." -- two components name the sample; one would
    #: collapse every driver class into a single bucket called "general".
    sample_depth: int = 2


@dataclass(frozen=True)
class WindowsClassicSamples(_CodeRepo):
    """Microsoft's Win32 desktop samples."""

    name: str = "win32-samples"
    repo_url: str = "https://github.com/microsoft/Windows-classic-samples"
    branch: str = "main"
    subtree: str = "Samples"
    base_url: str = ("https://github.com/microsoft/Windows-classic-samples/"
                     "blob/main/Samples/")
    license: str = "MIT"
    license_url: str = ("https://github.com/microsoft/Windows-classic-samples/"
                        "blob/main/LICENSE")
    attribution: str = (
        "Windows classic samples. Copyright (c) Microsoft Corporation. Used "
        "under the MIT License."
    )
    sample_depth: int = 1


@dataclass(frozen=True)
class AlgorithmsCpp(_CodeRepo):
    """TheAlgorithms/C-Plus-Plus: one algorithm per file, grouped by topic."""

    name: str = "algorithms"
    repo_url: str = "https://github.com/TheAlgorithms/C-Plus-Plus"
    branch: str = "master"
    subtree: str = ""
    base_url: str = "https://github.com/TheAlgorithms/C-Plus-Plus/blob/master/"
    license: str = "MIT"
    license_url: str = ("https://github.com/TheAlgorithms/C-Plus-Plus/blob/"
                        "master/LICENSE")
    attribution: str = (
        "TheAlgorithms/C-Plus-Plus. Used under the MIT License."
    )
    suffixes: tuple[str, ...] = (".cpp", ".h", ".hpp")
    sample_depth: int = 1

    def iter_symbols(self, root: Path) -> Iterator[ApiSymbol]:
        """One algorithm per file, so the *filename* is the inventory.

        `sorting/quick_sort.cpp` -> "quick_sort" in "sorting". That is the
        name someone searches for, and unlike a parsed function name it cannot
        be silently wrong.
        """
        base = self._root(root)
        seen: set[str] = set()
        for path in self._files(root):
            if _readable(path) is None:
                continue                   # see _CodeRepo.iter_symbols
            relative = path.relative_to(base).as_posix()
            name = path.stem
            topic = relative.split("/")[0] if "/" in relative else self.name
            key = f"{topic}/{name}"
            if key in seen or not _WORD.fullmatch(name):
                continue
            seen.add(key)
            yield ApiSymbol(
                name=name,
                kind="algorithm",
                namespace=topic,
                doc_path=relative,
                anchor="",
                signature=f"{topic}/{path.name}",
            )
