"""donnemartin/system-design-primer: system design concepts and case studies.

Small by file count and unusually dense: one very large README covering
concepts (CAP, consistency patterns, load balancing, sharding, caching) plus
nine worked case studies under `solutions/system_design/`.

Two things this adapter has to get right.

**Translations are excluded.** The repo ships README-ja.md, README-zh-Hans.md
and README-zh-TW.md beside the English README, and each case study carries a
Chinese translation too -- 11 of the 23 markdown files are translations of
another file already in the pack. Indexing them puts near-duplicate content
under a different language into the same vector space, so an English query
matches a Chinese chunk it cannot use, and the duplicate crowds out a distinct
result. Language is decided from the filename suffix, which is the repo's own
convention, rather than by sniffing the text.

**Case studies get a real title.** Every case study file is literally named
README.md, so titles taken from the filename would be nine identical entries.
The owning directory is the subject ("pastebin", "web_crawler"), so it becomes
the title -- that is what someone is searching for.
"""

from __future__ import annotations

import re
from dataclasses import dataclass
from pathlib import Path
from typing import Iterator

from .base import ApiSymbol, Doc

#: README-ja.md, README-zh-Hans.md, README-pt-BR.md ... The repo appends a
#: language tag to the stem; the untagged file is the English original.
_TRANSLATED = re.compile(r"-(?:[a-z]{2})(?:-[A-Za-z]{2,4})?$")

#: Repository housekeeping, not system design content.
_SKIP = frozenset({"CONTRIBUTING.md", "TRANSLATIONS.md",
                   "PULL_REQUEST_TEMPLATE.md", "CODE_OF_CONDUCT.md"})

_ATX = re.compile(r"^#{1,6}\s+(.+?)\s*#*\s*$", re.MULTILINE)


def is_translation(name: str) -> bool:
    """True for README-zh-Hans.md and friends, False for README.md."""
    stem = name[: -len(".md")] if name.endswith(".md") else name
    return bool(_TRANSLATED.search(stem))


@dataclass(frozen=True)
class SystemDesignPrimer:
    name: str = "system-design"
    repo_url: str = "https://github.com/donnemartin/system-design-primer"
    branch: str = "master"
    subtree: str = ""
    license: str = "CC-BY-4.0"
    license_url: str = ("https://github.com/donnemartin/system-design-primer/"
                        "blob/master/LICENSE.txt")
    attribution: str = (
        "The System Design Primer. Copyright (c) Donne Martin. Used under "
        "CC BY 4.0."
    )
    base_url: str = ("https://github.com/donnemartin/system-design-primer/"
                     "blob/master/")

    def _files(self, root: Path) -> Iterator[Path]:
        base = root / self.subtree if self.subtree else root
        if not base.is_dir():
            return
        for path in sorted(base.rglob("*.md")):
            if not path.is_file() or ".git" in path.parts:
                continue
            if path.name in _SKIP or is_translation(path.name):
                continue
            yield path

    def _title(self, relative: str, body: str) -> str:
        parts = relative.split("/")
        if parts[-1].lower() == "readme.md" and len(parts) > 1:
            # Nine case studies are all named README.md; the directory is the
            # subject and the only thing that distinguishes them.
            return parts[-2].replace("_", " ")
        heading = _ATX.search(body)
        if heading:
            return heading.group(1).strip()
        return relative

    def iter_docs(self, root: Path) -> Iterator[Doc]:
        base = root / self.subtree if self.subtree else root
        for path in self._files(root):
            body = path.read_text(encoding="utf-8", errors="replace")
            relative = path.relative_to(base).as_posix()
            yield Doc(
                path=relative,
                title=self._title(relative, body),
                url=self.base_url + relative,
                lang="md",
                body=body,
            )

    def iter_symbols(self, root: Path) -> Iterator[ApiSymbol]:
        """The case studies, by subject.

        Deliberately not the concept headings from the main README. Those are
        prose section titles ("Latency numbers every programmer should know"),
        and turning them into API symbols would fill `docs_lookup` with entries
        that are really search results. The nine case studies genuinely are
        named things a person asks for by name.
        """
        base = root / self.subtree if self.subtree else root
        for path in self._files(root):
            relative = path.relative_to(base).as_posix()
            parts = relative.split("/")
            if parts[-1].lower() != "readme.md" or len(parts) < 2:
                continue
            subject = parts[-2]
            yield ApiSymbol(
                name=subject,
                kind="case-study",
                namespace=self.name,
                doc_path=relative,
                anchor="",
                signature=f"system design case study: {subject.replace('_', ' ')}",
            )
