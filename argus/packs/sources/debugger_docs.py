"""WinDbg and the Debugging Tools for Windows.

Two docsets from one repository, deliberately in one pack:
``debuggercmds`` is the command reference (``!analyze``, ``.reload``, ``dt``)
and ``debugger`` is the how-to and concept material (crash-dump workflow,
remote debugging, symbol paths). "What does ``!analyze -v`` print" and "how do
I get a kernel dump to analyse in the first place" are halves of one question,
and a developer does not know in advance which half they are asking.

**The symbol inventory comes from titles, not from ``api_name``.** That field
looks like an inventory and is not one. On the 977 command pages, 869 carry
it, and it is two different things depending on the command:

    !analyze (WinDbg)              api_name: analyze          clean, no "!"
    !pcitree (WinDbg)              api_name: pcitree          clean, no "!"
    .abandon (Abandon Process)     api_name: .abandon (Abandon Process)
    tct (Trace to Next Call...)    api_name: tct (Trace to Next Call...)

For bang-extensions it is the name minus the ``!`` a developer actually types;
for dot and plain commands it is a verbatim copy of the title, gloss included.
Building on it alone would yield an inventory half of which is prose. The
sibling ``debugger`` docset is worse still -- there ``api_name`` copies the
title on every page, so ``Activating a Debugging Client`` would be indexed as
an API name.

Titles are consistent, so the command is taken from the title and the bare
``api_name`` is kept only as an *alias* when it is a single token -- which
admits ``analyze``, ``devhandles``, ``pcitree`` and rejects the copies.
``docs_lookup`` is exact-match and never fuzzy, so a miss returns nothing at
all; carrying both spellings is what makes ``!analyze`` and ``analyze`` both
land.
"""

from __future__ import annotations

import re
from dataclasses import dataclass
from pathlib import Path
from typing import Iterator

from .base import ApiSymbol, Doc
from .microsoft_docs import first_heading, parse_front_matter

#: The docset holding the command reference. Symbols come only from here: the
#: sibling docset is articles, and an article title is not a command.
COMMANDS_DIR = "debuggercmds"

#: (directory, public base URL). Commands first so that a page reachable from
#: both resolves to the reference rather than to an article mentioning it.
DOCSETS: tuple[tuple[str, str], ...] = (
    (COMMANDS_DIR,
     "https://learn.microsoft.com/en-us/windows-hardware/drivers/debuggercmds/"),
    ("debugger",
     "https://learn.microsoft.com/en-us/windows-hardware/drivers/debugger/"),
)

#: The trailing gloss on a command title: "(WinDbg)", "(Abandon Process)".
#: What precedes it is the command.
_TITLE_GLOSS_RE = re.compile(r"\s*\(.*$")


def command_names(title: str) -> list[str]:
    """The commands a page documents, spelled the way they are typed.

    Three shapes occur across the 977 pages, and the rule has to survive all
    of them:

    - ``!analyze (WinDbg)`` -- gloss stripped, leaving ``!analyze``. 540 of
      the pages look like this.
    - ``!acpiirqarb`` -- already bare. Most of the remaining 437.
    - ``!amli debugger`` and ``* Asterisk character Comment Line Specifier``
      -- the command is the first token and the rest is prose. Taking the
      whole line here would index a sentence as a command name.

    Titles listing several spellings of one command are comma-separated
    (``$<, $><, $$<, $$><, $$ >a<``), and each is a real thing to look up, so
    they become separate symbols pointing at the same page.
    """
    head = _TITLE_GLOSS_RE.sub("", str(title or "")).strip()
    names: list[str] = []
    for part in head.split(","):
        words = part.split()
        if words:
            names.append(words[0])
    return names


def alias_names(meta: dict[str, object]) -> list[str]:
    """Bare ``api_name`` entries usable as lookup aliases.

    Single-token only. A multi-token entry is the title copied wholesale --
    ``.abandon (Abandon Process)`` -- which is not a name anyone types, and
    ``NA`` is filler that appears alongside the real value.
    """
    raw = meta.get("api_name")
    if not isinstance(raw, list):
        return []
    out: list[str] = []
    for entry in raw:
        text = str(entry).strip()
        if not text or text == "NA" or len(text.split()) != 1:
            continue
        out.append(text)
    return out


@dataclass(frozen=True)
class DebuggerDocs:
    """Debugging Tools for Windows (MicrosoftDocs/windows-driver-docs)."""

    name: str = "debugger"
    repo_url: str = "https://github.com/MicrosoftDocs/windows-driver-docs"
    #: This repository publishes from ``staging`` and has no ``main`` branch
    #: at all; a build pointed at one fails at clone time with "Remote branch
    #: main not found", which reads like a network fault.
    branch: str = "staging"
    #: Both docsets live under this directory rather than at the repo root.
    subtree: str = "windows-driver-docs-pr"
    license: str = "CC-BY-4.0"
    license_url: str = (
        "https://github.com/MicrosoftDocs/windows-driver-docs/blob/staging/LICENSE"
    )
    attribution: str = (
        "Debugging Tools for Windows documentation. Copyright (c) Microsoft "
        "Corporation. Used under CC BY 4.0."
    )

    def iter_docs(self, root: Path) -> Iterator[Doc]:
        for docset, base_url in DOCSETS:
            content = Path(root) / self.subtree / docset
            if not content.is_dir():
                continue
            for path in sorted(content.rglob("*.md")):
                if not path.is_file() or path.name.startswith("TOC"):
                    continue
                raw = path.read_text(encoding="utf-8", errors="replace")
                meta, body = parse_front_matter(raw)
                relative = path.relative_to(content).as_posix()
                description = str(meta.get("description") or "")
                yield Doc(
                    # Prefixed: both docsets have their own `index.md`, and
                    # without this the second would overwrite the first.
                    path=f"{docset}/{relative}",
                    title=(str(meta.get("title") or "")
                           or first_heading(body) or relative),
                    url=base_url + relative[: -len(".md")],
                    lang="md",
                    body=f"{description}\n\n{body}" if description else body,
                )

    def _reference_pages(self, content: Path):
        """Command reference pages, with their titles already resolved.

        Only pages Microsoft marks `topic_type: [apiref]`. The docset also
        holds index and landing pages whose titles are ordinary prose, and
        taking the first token of "Debugger Commands" would index `Debugger`
        as a command. Using the upstream marker beats guessing from titles.
        """
        for path in sorted(content.rglob("*.md")):
            if not path.is_file() or path.name.startswith("TOC"):
                continue
            meta, _ = parse_front_matter(
                path.read_text(encoding="utf-8", errors="replace"))
            topics = meta.get("topic_type")
            if not isinstance(topics, list) or "apiref" not in topics:
                continue
            yield path, meta, command_names(meta.get("title", ""))

    def iter_symbols(self, root: Path) -> Iterator[ApiSymbol]:
        content = Path(root) / self.subtree / COMMANDS_DIR
        if not content.is_dir():
            return
        pages = list(self._reference_pages(content))
        # Every command the docset documents. An alias is only safe if it is
        # not some OTHER page's real command: `!dt` carries the bare alias
        # `dt`, which is a different, very common command, and `dt` cannot
        # invoke `!dt`. Left in, the alias makes docs_lookup("dt") ambiguous
        # between the command you asked for and one you cannot type that way.
        # Measured on the corpus: 3 such aliases -- dt, dpa, version -- out
        # of 586, so this drops almost nothing and removes real ambiguity.
        commands = {name for _, _, names in pages for name in names}
        for path, meta, names in pages:
            relative = path.relative_to(content).as_posix()
            # Without the suffix, matching how the builder normalises doc
            # paths when it links a symbol to its page.
            doc_path = f"{COMMANDS_DIR}/{relative[: -len('.md')]}"
            # The one-line description is what `docs_find` searches, so a
            # command is reachable by what it does and not only by its name.
            description = str(meta.get("description") or "")
            own = set(names)
            aliases = [a for a in alias_names(meta)
                       if a in own or a not in commands]
            seen: set[str] = set()
            for symbol in (*names, *aliases):
                if symbol in seen:
                    continue
                seen.add(symbol)
                yield ApiSymbol(
                    name=symbol,
                    kind="command",
                    namespace="debugger",
                    doc_path=doc_path,
                    anchor="",
                    signature=description,
                )
