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
_BLOCK_ITEM = re.compile(r'^\s+-\s+(.*)$')
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
            signature = _requirement_line(meta)
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


def _requirement_line(meta: dict) -> str:
    """"Header: winuser.h; Library: User32.lib; DLL: User32.dll"."""
    wanted = (("Header", "req.header"), ("Library", "req.lib"),
              ("DLL", "req.dll"), ("IRQL", "req.irql"))
    return "; ".join(f"{label}: {meta[key]}" for label, key in wanted
                     if str(meta.get(key) or "").strip())


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
            # No signature. A cpp-docs page lists many f1_keywords and has at
            # most one Syntax block, so attaching it to each would state that
            # every compiler error on an ARM assembler page has the syntax of
            # the first one -- measured, A2196 and A2202 both came back
            # showing A2193's example line.
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
                    signature="",
                )
