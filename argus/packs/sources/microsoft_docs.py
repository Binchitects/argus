"""Microsoft's documentation repositories: Win32 SDK, WDK DDI, and MSVC C++.

Three separate packs, deliberately. They answer different questions, they are
versioned by different teams, and a driver developer has no use for the STL
reference while an application developer has none for IRQL rules. Installing
one should not cost the others' disk.

Two shapes live here:

* **API reference** (`sdk-api`, `windows-driver-docs-ddi`) -- one file per API
  entity, carrying a `UID:` such as ``NF:winuser.MessageBox``. That UID is a
  real inventory, the same role `objects.inv` plays for Python: it gives an
  exact name, kind and defining header without guessing from prose. It is what
  makes ``docs_lookup("MessageBox")`` land on the definition.
* **Conceptual/reference prose** (`cpp-docs`) -- ordinary articles with no
  UID. Symbols come from `f1_keywords`, the index Microsoft's own F1 help uses,
  which is a genuine inventory rather than headings scraped from the body.

All three are CC-BY-4.0 for the documentation and MIT for code samples, so
they are redistributable inside a pack provided the attribution travels with
them -- which is what `license`/`attribution` on each source is for.
"""

from __future__ import annotations

import re
from dataclasses import dataclass
from pathlib import Path
from typing import Iterator

from .base import ApiSymbol, Doc

_FRONT_MATTER = re.compile(r"\A---\r?\n(.*?)\r?\n---\r?\n?", re.DOTALL)
_SCALAR = re.compile(r"^([A-Za-z_][\w.\-]*)\s*:\s*(.*)$")
_JSON_LIST = re.compile(r'"([^"]+)"')
#: A block-sequence item. The leading indent is OPTIONAL: YAML allows a
#: sequence to sit at its key's own indentation, and the debugger docset uses
#: exactly that -- `api_name:` followed by `- analyze` at column 0. Requiring
#: indent here parsed every indented repo correctly and silently dropped the
#: whole list on the unindented ones, which surfaces as a name that never
#: resolves rather than as an error.
_BLOCK_ITEM = re.compile(r'^[ \t]*-\s+(.*)$')
#: The fenced block under "## Syntax" -- the declaration, not prose about it.
_SYNTAX = re.compile(
    r"^##\s+Syntax\s*$.*?^```(?:\w+)?\s*$(.*?)^```", re.MULTILINE | re.DOTALL)
_ATX = re.compile(r"^(#{1,6})\s+(.+?)\s*#*\s*$", re.MULTILINE)
#: "IoCompleteRequest macro (wdm.h)" -> "IoCompleteRequest". The documented
#: name is not always the UID: nf-wdm-iocompleterequest.md carries
#: UID NF:wdm.IofCompleteRequest and lists only IofCompleteRequest in
#: api_name, so the name every driver actually calls appears in the title
#: alone. Without this the pack cannot answer docs_lookup("IoCompleteRequest").
_TITLE_NAME = re.compile(
    r"^([A-Za-z_][\w:]*)\s+(?:function|macro|structure|struct|enumeration|"
    r"interface|callback|union|method|routine|ioctl)\b")

#: UID prefix -> kind. Microsoft's own two-letter scheme; spelled out because
#: "NF" in a retrieved result tells a reader nothing.
_UID_KINDS = {
    "NF": "function", "NS": "struct", "NN": "interface", "NE": "enum",
    "NC": "callback", "NI": "ioctl", "NL": "class", "NA": "apiset",
    "NT": "typedef", "ND": "define", "NU": "union",
}


def parse_front_matter(text: str) -> tuple[dict[str, object], str]:
    """Split YAML front matter from the body.

    A deliberately small parser rather than a YAML dependency: these files use
    scalars and JSON-style inline lists, and the keys that matter here (`UID`,
    `title`, `req.header`, `f1_keywords`) are all one or the other. A real YAML
    load would also happily evaluate anything else upstream adds, which is more
    surface than a documentation build needs.
    """
    match = _FRONT_MATTER.match(text)
    if not match:
        return {}, text
    meta: dict[str, object] = {}
    pending: str | None = None            # key whose block list we are inside
    for line in match.group(1).splitlines():
        item = _BLOCK_ITEM.match(line)
        if item and pending is not None:
            # The key was seeded with "" when its value was empty, because at
            # that point an empty scalar and the head of a block sequence look
            # identical. setdefault cannot replace that "", so the list has to
            # be installed explicitly -- otherwise every appended alias lands
            # on a str and is silently discarded.
            if not isinstance(meta.get(pending), list):
                meta[pending] = []
            meta[pending].append(item.group(1).strip().strip('"').strip("'"))
            continue
        found = _SCALAR.match(line)
        if not found:
            continue                      # continuation or nested block
        key, raw = found.group(1), found.group(2).strip()
        pending = None
        if raw.startswith("[") and raw.endswith("]"):
            meta[key] = _JSON_LIST.findall(raw)
        elif raw == "":
            # Either an empty scalar or the head of a block sequence:
            #
            #   api_name:
            #    - KeAcquireSpinLock
            #
            # Both sdk-api and the DDI repo use this form, and treating it as
            # an empty string silently dropped every alias on those pages.
            # Measured: KeAcquireSpinLock and IoCompleteRequest -- core driver
            # routines -- were absent from the built pack entirely.
            pending = key
            meta[key] = ""
        else:
            meta[key] = raw.strip('"').strip("'")
    return meta, text[match.end():]


def parse_uid(uid: str) -> tuple[str, str, str] | None:
    """``"NF:winuser.MessageBox"`` -> ``("function", "winuser", "MessageBox")``.

    Returns None for a UID that does not carry a name, rather than inventing
    one: an unnamed symbol in the inventory would be a lookup key nobody can
    ever type, and it would inflate the symbol count while answering nothing.
    """
    prefix, _, rest = uid.partition(":")
    if not rest:
        return None
    kind = _UID_KINDS.get(prefix.upper())
    if kind is None:
        return None
    module, _, name = rest.partition(".")
    # "NF:wdm.KeAcquireSpinLock~r1" -- the ~rN suffix disambiguates revisions
    # of the same routine upstream and is not part of the name anyone types.
    # Measured: 56 DDI pages carry one, and they include KeAcquireSpinLock.
    name = name.split("~", 1)[0]
    if not name:
        # "NN:combaseapi" and friends -- a module-level page with no entity.
        return None
    return kind, module, name


def first_heading(body: str) -> str:
    match = _ATX.search(body)
    return match.group(2).strip().strip("`") if match else ""


def syntax_signature(body: str, limit: int = 400) -> str:
    """The declaration from the page's Syntax block, flattened to one line."""
    match = _SYNTAX.search(body)
    if not match:
        return ""
    text = " ".join(match.group(1).split())
    return text[:limit]


@dataclass(frozen=True)
class _MicrosoftApiRef:
    """Common behaviour for the UID-carrying API reference repositories."""

    name: str = ""
    repo_url: str = ""
    branch: str = ""
    subtree: str = ""
    base_url: str = ""
    license: str = "CC-BY-4.0"
    license_url: str = ""
    attribution: str = ""

    def iter_docs(self, root: Path) -> Iterator[Doc]:
        content = root / self.subtree
        if not content.is_dir():
            return
        for path in sorted(content.rglob("*.md")):
            if not path.is_file() or path.name == "TOC.md":
                continue
            raw = path.read_text(encoding="utf-8", errors="replace")
            meta, body = parse_front_matter(raw)
            relative = path.relative_to(content).as_posix()
            title = str(meta.get("title") or "") or first_heading(body) or relative
            description = str(meta.get("description") or "")
            yield Doc(
                path=relative,
                title=title,
                url=self.base_url + relative[: -len(".md")],
                lang="md",
                # The requirements block is the single most asked-for fact
                # about a Win32 or DDI entity -- which header to include, which
                # .lib to link, which IRQL it may be called at -- and it lives
                # only in front matter, so it would be dropped entirely by a
                # body-only chunker.
                body=_prepend_requirements(meta, description, body),
            )

    def iter_symbols(self, root: Path) -> Iterator[ApiSymbol]:
        content = root / self.subtree
        if not content.is_dir():
            return
        for path in sorted(content.rglob("*.md")):
            if not path.is_file() or path.name == "TOC.md":
                continue
            raw = path.read_text(encoding="utf-8", errors="replace")
            meta, body = parse_front_matter(raw)
            uid = str(meta.get("UID") or "")
            parsed = parse_uid(uid)
            if parsed is None:
                continue
            kind, module, symbol = parsed
            doc_path = path.relative_to(content).as_posix()[: -len(".md")]
            # NOT a declaration. These files carry no prototype at all -- the
            # published site generates one from the "## -parameters" sections,
            # and the only fenced block on a page is a usage example. Putting
            # that example here would show `int DisplayResourceNAMessageBox()`
            # as the signature of MessageBox.
            #
            # The requirements are what a developer actually needs from a
            # lookup ("which header, which .lib, callable at what IRQL"), and
            # unlike a prototype they are really present.
            # The body is the fallback, not the primary: frontmatter carries
            # the requirements a lookup needs, and those come first. But an
            # apiset index page (windows.foundation/index.md) declares no
            # header, no library and no description, so it produced a blank
            # signature -- and docs_find skips those, leaving 14 win32 symbols
            # in the pack and unreachable. Their prose is right there under
            # "## -description".
            signature = _requirement_line(meta) or _page_lede(body)
            seen = {symbol}
            yield ApiSymbol(name=symbol, kind=kind, namespace=module,
                            doc_path=doc_path, anchor="", signature=signature)
            # MessageBoxA and MessageBoxW are what appear in real code and in
            # real compiler errors; MessageBox is the macro. A pack that
            # indexes only the UID name cannot answer a lookup for either of
            # the names a developer actually pasted.
            for alias in _aliases(meta):
                if alias in seen:
                    continue
                seen.add(alias)
                yield ApiSymbol(name=alias, kind=kind, namespace=module,
                                doc_path=doc_path, anchor="", signature=signature)


def _prepend_requirements(meta: dict, description: str, body: str) -> str:
    """Put header/library/DLL/IRQL facts in front of the page text."""
    wanted = (("Header", "req.header"), ("Library", "req.lib"),
              ("DLL", "req.dll"), ("IRQL", "req.irql"),
              ("Unicode/ANSI", "req.unicode-ansi"))
    lines = [f"{label}: {meta[key]}" for label, key in wanted
             if str(meta.get(key) or "").strip()]
    parts = [p for p in (description, "\n".join(lines), body) if p.strip()]
    return "\n\n".join(parts)


#: Microsoft's frontmatter descriptions overwhelmingly open with this SEO
#: boilerplate: 30,379 of cpp's 37,325 symbols (81%).
_LEARN_MORE = re.compile(r"^learn more about\s*:?\s*", re.IGNORECASE)


def _clean_description(meta: dict) -> str:
    """The page description, without the phrase four fifths of them start with.

    `docs_find` shows this field truncated, so a prefix carried by 81% of the
    corpus spends the first characters of nearly every result saying nothing.
    It is not merely redundant: a term that common has almost no inverse
    document frequency, so it costs display budget and returns no ranking
    signal for it.

    Removed only when something survives it. A description that is nothing but
    the boilerplate keeps it, because `docs_find` skips rows whose signature is
    blank -- trading a weak description for an invisible symbol would undo the
    entire reason this field is populated.
    """
    description = " ".join(str(meta.get("description") or "").split())
    return _LEARN_MORE.sub("", description).strip() or description


#: A list item, not prose. Matched with a trailing space so that `**bold**`
#: opening a real sentence is not mistaken for a bullet.
_LIST_ITEM = re.compile(r"^\s*([-*+]|\d+\.)\s")

#: Chrome a Learn page opens with: headings, code fences, moniker ranges and
#: includes, note callouts, tables, raw HTML, and horizontal rules.
_CHROME_PREFIXES = ("#", "```", ":::", ">", "|", "[!", "<", "---", "===")


def _page_lede(body: str, words: int = 30) -> str:
    """The first sentence of real prose on the page.

    cpp-docs frontmatter descriptions are title echoes, unlike win32's, which
    are genuine prose. Measured over the built pack: 56% of symbols carried a
    description of two words or fewer and 42% were literally "<Name> Class".
    `_countof`'s entire searchable text was "_countof Macro", so "number of
    elements in a fixed-size array" could not reach it at any weighting --
    the symbol was visible and still unfindable.

    The page says it properly one line under the H1: "Computes the number of
    elements in a statically allocated array." This takes that.

    Page-level, so every symbol on the page shares it. That is exactly the
    claim the description already made, and a far stronger one than a title
    echo. Returns "" when a page is nothing but chrome, leaving the caller to
    fall back rather than inventing text.
    """
    fenced = False
    for line in body.splitlines():
        text = line.strip()
        # Fence state is tracked rather than prefix-matched, because a line
        # INSIDE a fence has no marker of its own. Without this, a page whose
        # only content is a Syntax block returns its first code line, and
        # every f1_keyword on the page gets it -- which is the borrowed-syntax
        # failure that left this field empty to begin with: A2196 and A2202
        # both came back showing A2193's example line.
        if text.startswith(("```", "~~~")):
            fenced = not fenced
            continue
        if fenced or not text:
            continue
        if text.startswith(_CHROME_PREFIXES) or _LIST_ITEM.match(text):
            continue
        return " ".join(text.split()[:words])
    return ""


def _requirement_line(meta: dict) -> str:
    """"Header: winuser.h; Library: User32.lib" plus what the API actually does.

    The contract comes FIRST and keeps its semicolon separators, because
    `docs_contracts` splits this field on ";" to pull out the IRQL and the
    library -- putting prose ahead of it would change what that parse sees.
    The description is appended after a " -- " marker, which the contract
    parser never treats as a field boundary.

    The description is here because `docs_find` searches this field and
    nothing else. Measured: with the contract alone, win32 and wdk contributed
    roughly 125,000 symbols whose entire searchable text was
    "Header: wdm.h; Library: NtosKrnl.lib; IRQL: <= DISPATCH_LEVEL". Asked to
    "allocate memory from the kernel pool", `docs_find` could not reach
    `ExAllocatePool2`, because the word "allocate" appeared nowhere in what
    was searched. Ranking cannot fix an absent word, and the whole 25-question
    set scored 4% top-1 largely on this.
    """
    wanted = (("Header", "req.header"), ("Library", "req.lib"),
              ("DLL", "req.dll"), ("IRQL", "req.irql"))
    contract = "; ".join(f"{label}: {meta[key]}" for label, key in wanted
                         if str(meta.get(key) or "").strip())
    description = _clean_description(meta)
    if not description:
        return contract
    return f"{contract} -- {description}" if contract else description


def _aliases(meta: dict) -> list[str]:
    """Alternate spellings of the entity, from api_name/f1_keywords.

    Filtered to bare identifiers: f1_keywords also carries header-qualified
    forms ("winuser/MessageBoxW") whose slash half is a file, not a symbol, and
    indexing those as names would make `docs_lookup` answer for things nobody
    calls.
    """
    out: list[str] = []
    title = str(meta.get("title") or "")
    named = _TITLE_NAME.match(title)
    if named:
        out.append(named.group(1))
    for key in ("api_name", "f1_keywords"):
        value = meta.get(key)
        if isinstance(value, list):
            for entry in value:
                candidate = entry.rpartition("/")[2]
                if candidate and candidate.replace("_", "").isalnum():
                    out.append(candidate)
    return out


@dataclass(frozen=True)
class Win32Api(_MicrosoftApiRef):
    """The Win32 / Windows SDK API reference (MicrosoftDocs/sdk-api)."""

    name: str = "win32"
    repo_url: str = "https://github.com/MicrosoftDocs/sdk-api"
    branch: str = "docs"
    subtree: str = "sdk-api-src/content"
    base_url: str = "https://learn.microsoft.com/en-us/windows/win32/api/"
    license: str = "CC-BY-4.0"
    license_url: str = "https://github.com/MicrosoftDocs/sdk-api/blob/docs/LICENSE"
    attribution: str = (
        "Windows SDK API reference. Copyright (c) Microsoft Corporation. "
        "Used under CC BY 4.0."
    )


@dataclass(frozen=True)
class WdkDdi(_MicrosoftApiRef):
    """The Windows Driver Kit DDI reference (MicrosoftDocs/windows-driver-docs-ddi)."""

    name: str = "wdk"
    repo_url: str = "https://github.com/MicrosoftDocs/windows-driver-docs-ddi"
    branch: str = "staging"
    subtree: str = "wdk-ddi-src/content"
    base_url: str = "https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/"
    license: str = "CC-BY-4.0"
    license_url: str = (
        "https://github.com/MicrosoftDocs/windows-driver-docs-ddi/blob/staging/LICENSE")
    attribution: str = (
        "Windows Driver Kit DDI reference. Copyright (c) Microsoft "
        "Corporation. Used under CC BY 4.0."
    )


@dataclass(frozen=True)
class CppDocs:
    """MSVC C++, the C runtime and the Standard Library (MicrosoftDocs/cpp-docs).

    No UID here -- these are articles, not one-entity-per-file reference. The
    symbol inventory comes from `f1_keywords`, which is what Microsoft's own F1
    help resolves against, so it is a real index rather than headings guessed
    out of the body. Entries look like ``vector/std::vector::push_back``: the
    half before the slash is the header the entry belongs to, the half after is
    the qualified name a developer would actually search for.
    """

    name: str = "cpp"
    repo_url: str = "https://github.com/MicrosoftDocs/cpp-docs"
    branch: str = "main"
    subtree: str = "docs"
    license: str = "CC-BY-4.0"
    license_url: str = "https://github.com/MicrosoftDocs/cpp-docs/blob/main/LICENSE"
    attribution: str = (
        "Microsoft C++, C, and Assembler documentation. Copyright (c) "
        "Microsoft Corporation. Used under CC BY 4.0."
    )
    base_url: str = "https://learn.microsoft.com/en-us/cpp/"

    def iter_docs(self, root: Path) -> Iterator[Doc]:
        content = root / self.subtree
        if not content.is_dir():
            return
        for path in sorted(content.rglob("*.md")):
            if not path.is_file() or path.name.startswith("TOC"):
                continue
            raw = path.read_text(encoding="utf-8", errors="replace")
            meta, body = parse_front_matter(raw)
            relative = path.relative_to(content).as_posix()
            description = str(meta.get("description") or "")
            yield Doc(
                path=relative,
                title=str(meta.get("title") or "") or first_heading(body) or relative,
                url=self.base_url + relative[: -len(".md")],
                lang="md",
                body=f"{description}\n\n{body}" if description else body,
            )

    def iter_symbols(self, root: Path) -> Iterator[ApiSymbol]:
        content = root / self.subtree
        if not content.is_dir():
            return
        for path in sorted(content.rglob("*.md")):
            if not path.is_file() or path.name.startswith("TOC"):
                continue
            raw = path.read_text(encoding="utf-8", errors="replace")
            meta, body = parse_front_matter(raw)
            keywords = meta.get("f1_keywords")
            if not isinstance(keywords, list):
                continue
            doc_path = path.relative_to(content).as_posix()[: -len(".md")]
            # No SIGNATURE, for a reason that still holds: a cpp-docs page
            # lists many f1_keywords and has at most one Syntax block, so
            # attaching it to each would state that every compiler error on an
            # ARM assembler page has the syntax of the first one -- measured,
            # A2196 and A2202 both came back showing A2193's example line.
            #
            # The page DESCRIPTION is different in kind, and leaving it out
            # cost more than the syntax block ever would have. A description
            # is a statement about the page, so it is equally true of every
            # symbol documented on it, where a syntax block is a claim about
            # one entity. Measured: with this empty, all 37,305 cpp symbols
            # were invisible to docs_find, which searches this field and
            # skips rows where it is blank -- the pack took zero result slots
            # across a 25-question set while occupying 174.7 MB.
            #
            # It is page-level rather than per-symbol, so it will not
            # distinguish two errors documented together. That is a weaker
            # claim than the name implies and a far better one than silence.
            # The lede first, the frontmatter description only as a fallback.
            # That order is the opposite of win32's for a reason measured per
            # corpus: win32 frontmatter carries real prose ("Creates or opens
            # a file"), cpp-docs frontmatter carries the title again.
            description = _page_lede(body) or _clean_description(meta)
            seen: set[str] = set()
            for entry in keywords:
                header, _, qualified = entry.partition("/")
                symbol = qualified or header
                # "vector/std::vector" and a bare "vector" both appear; only
                # the qualified form is a name anyone looks up.
                if not symbol or symbol in seen or " " in symbol:
                    continue
                seen.add(symbol)
                yield ApiSymbol(
                    name=symbol,
                    kind="class" if symbol.endswith("_class") else "cpp",
                    namespace=header if qualified else self.name,
                    doc_path=doc_path,
                    anchor="",
                    signature=description,
                )
