"""The .NET API reference, from ECMAXML.

`dotnet/dotnet-api-docs` publishes one XML file per type -- 11,466 of them
across 825 namespaces -- in the ECMAXML schema docfx consumes. That covers the
base class library *and* the Microsoft-published NuGet packages that ship with
it: `System.Text.Json`, `Microsoft.Extensions.*`, and the rest.

A third parser alongside the markdown and HTML ones, and it earns its place
for a reason neither of those has: **ECMAXML carries an explicit inventory.**
Every type declares a `DocId` (``T:System.String``) and every member declares
its own signature, so `docs_lookup("String.Split")` resolves to the member
rather than to whichever page mentions it most. The React pack had to infer
symbols from heading shapes and yielded 125 from 222 documents; this needs no
inference at all.

Body text is assembled as markdown with ATX headings, for the same reason
`html_docs` does it: the chunker builds a heading trail and prepends it before
embedding, so a retrieved fragment carries "String > Split > Remarks" rather
than a loose paragraph.
"""

from __future__ import annotations

import re
import xml.etree.ElementTree as ET
from dataclasses import dataclass
from pathlib import Path
from typing import Iterator

from .base import ApiSymbol, Doc

#: The signature language to index. ECMAXML repeats every signature in C#,
#: ILAsm, VB.NET, F# and C++/CLI; storing all five would quintuple the symbol
#: table to say the same thing, and C# is what a .NET developer searching for
#: an API is overwhelmingly typing.
_SIGNATURE_LANGUAGE = "C#"

#: Inline elements whose text belongs in the prose. ECMAXML wraps API
#: references in <see cref="T:System.String" />, and the cref is the only text
#: the element has -- dropping it silently deletes the API names from summaries
#: that consist mostly of cross-references.
_CREF_RE = re.compile(r"^[A-Z]:")


def _flatten(node: ET.Element | None) -> str:
    """Element content as plain text, following cross-references.

    ``<see cref="T:System.Text.StringBuilder" />`` has no text of its own, so a
    naive ``itertext`` yields a summary with holes where every API name should
    be. The cref is unwrapped to its last path segment, which is what the
    rendered documentation shows.
    """
    if node is None:
        return ""
    parts: list[str] = []
    for element in node.iter():
        if element.tag in ("see", "seealso", "paramref", "typeparamref"):
            ref = (element.get("cref") or element.get("name")
                   or element.get("langword") or "")
            if ref:
                if _CREF_RE.match(ref):
                    ref = ref[2:]
                # Drop the parameter list BEFORE taking the last segment. A
                # method cref is `M:System.String.Concat(System.String,
                # System.String)`, and splitting on the final dot lands inside
                # the arguments -- yielding "String)" where the reader needs
                # "Concat". Measured on the real corpus's StringBuilder page.
                ref = ref.split("(", 1)[0]
                parts.append(ref.rsplit(".", 1)[-1] if "." in ref else ref)
        if element.text:
            parts.append(element.text)
        if element.tail:
            parts.append(element.tail)
    return " ".join(" ".join(parts).split())


#: How much of a file to inspect for a document type declaration. A DOCTYPE
#: is only legal in the prolog, so this is generous rather than approximate.
_PROLOG_BYTES = 4096


def _parse_guarded(path: Path) -> ET.Element | None:
    """Parse an ECMAXML file, refusing anything carrying a DTD.

    ``xml.etree`` expands internal entities, so a document declaring nested
    entities can expand to gigabytes from a few hundred bytes -- the
    billion-laughs attack. That matters here because this corpus is a cloned
    third-party repository: the files are trustworthy today because of who
    publishes them, not because of anything this code checks.

    Rejecting the declaration outright is stricter than disabling expansion
    and needs no dependency. Real ECMAXML has no DOCTYPE at all, so a file
    that has one is either corrupt or hostile, and neither belongs in a pack.
    Returning None skips the file rather than failing the build, matching how
    a malformed file is already handled.
    """
    with open(path, "rb") as handle:
        prolog = handle.read(_PROLOG_BYTES)
    lowered = prolog.lower()
    if b"<!doctype" in lowered or b"<!entity" in lowered:
        return None
    tree = ET.parse(path)
    root = tree.getroot()
    return root


def _signature(node: ET.Element, tag: str) -> str:
    for signature in node.findall(tag):
        if signature.get("Language") == _SIGNATURE_LANGUAGE:
            return signature.get("Value") or ""
    return ""


def _doc_id(node: ET.Element, tag: str) -> str:
    """The DocId signature (``T:System.String``, ``M:System.String.Split``).

    Used for the anchor because it is the identifier the rest of the .NET
    documentation ecosystem links by, so an anchor built from it matches what
    learn.microsoft.com actually serves.
    """
    for signature in node.findall(tag):
        if signature.get("Language") == "DocId":
            return signature.get("Value") or ""
    return ""


@dataclass(frozen=True)
class DotnetApiDocs:
    """The .NET API reference (dotnet/dotnet-api-docs)."""

    name: str = "dotnet"
    repo_url: str = "https://github.com/dotnet/dotnet-api-docs"
    branch: str = "main"
    subtree: str = "xml"
    license: str = "CC-BY-4.0"
    license_url: str = "https://github.com/dotnet/dotnet-api-docs/blob/main/LICENSE"
    attribution: str = (
        ".NET API documentation. Copyright (c) .NET Foundation and "
        "Contributors. Used under CC BY 4.0."
    )
    base_url: str = "https://learn.microsoft.com/dotnet/api/"

    def _types(self, root: Path) -> Iterator[tuple[Path, str, ET.Element]]:
        content = Path(root) / self.subtree
        if not content.is_dir():
            return
        for path in sorted(content.rglob("*.xml")):
            # is_file first: glob is case-insensitive on Windows, so the
            # namespace DIRECTORY `Microsoft.Extensions.Configuration.Xml`
            # matches `*.xml` and opening it raises PermissionError -- which
            # reads like a filesystem problem rather than a pattern that
            # matched the wrong kind of thing.
            if not path.is_file():
                continue
            # ns-*.xml files describe a namespace rather than a type, and
            # index.xml is a build manifest. Neither has members.
            if path.name.startswith("ns-") or path.name == "index.xml":
                continue
            try:
                node = _parse_guarded(path)
            except ET.ParseError:
                # One malformed file should cost that file, not the build.
                continue
            if node is None:
                continue
            if node.tag != "Type":
                continue
            yield path, path.relative_to(content).as_posix(), node

    def iter_docs(self, root: Path) -> Iterator[Doc]:
        for _path, relative, node in self._types(root):
            full_name = node.get("FullName") or node.get("Name") or relative
            docs = node.find("Docs")
            lines = [f"# {full_name}", ""]
            signature = _signature(node, "TypeSignature")
            if signature:
                lines += ["```csharp", signature, "```", ""]
            for tag, heading in (("summary", None), ("remarks", "Remarks")):
                text = _flatten(docs.find(tag)) if docs is not None else ""
                if text:
                    if heading:
                        lines += [f"## {heading}", ""]
                    lines += [text, ""]

            # Members become sections rather than separate documents: a
            # one-line overload summary is far too small to embed on its own,
            # and the chunker's heading trail keeps "String > Split" attached
            # to it either way.
            for member in node.findall("./Members/Member"):
                member_name = member.get("MemberName") or ""
                member_docs = member.find("Docs")
                summary = (_flatten(member_docs.find("summary"))
                           if member_docs is not None else "")
                member_signature = _signature(member, "MemberSignature")
                if not (summary or member_signature):
                    continue
                lines += [f"## {member_name}", ""]
                if member_signature:
                    lines += ["```csharp", member_signature, "```", ""]
                if summary:
                    lines += [summary, ""]

            body = "\n".join(lines).strip()
            if not body:
                continue
            yield Doc(
                path=relative,
                title=full_name,
                url=self.base_url + full_name.lower().replace("`", "-"),
                lang="md",
                body=body,
            )

    def iter_symbols(self, root: Path) -> Iterator[ApiSymbol]:
        """One symbol per type, plus one per member.

        Members are indexed under both their bare name and their qualified one
        (``Split`` and ``String.Split``) because both are things a developer
        types, and `docs_lookup` is exact-match -- a name it does not hold
        returns nothing at all rather than something approximate.
        """
        for _path, relative, node in self._types(root):
            full_name = node.get("FullName") or node.get("Name") or ""
            if not full_name:
                continue
            # WITH the .xml suffix, matching what iter_docs emits. The
            # builder normalises both sides through `_doc_key`, whose
            # `_DOC_SUFFIXES` covers .html/.rst/.mdx/.md and NOT .xml -- so
            # stripping it here left the symbol keyed `System/String` against
            # a document keyed `System/String.xml`, and every one of the
            # 215,269 symbols failed to resolve. The pack still built, still
            # installed, and simply had no lookup inventory.
            doc_path = relative
            docs = node.find("Docs")
            summary = _flatten(docs.find("summary")) if docs is not None else ""
            namespace = full_name.rsplit(".", 1)[0] if "." in full_name else ""

            yield ApiSymbol(
                name=full_name, kind="type", namespace=namespace,
                doc_path=doc_path, anchor=_doc_id(node, "TypeSignature"),
                signature=summary or _signature(node, "TypeSignature"),
            )
            short = full_name.rsplit(".", 1)[-1]
            if short and short != full_name:
                yield ApiSymbol(
                    name=short, kind="type", namespace=namespace,
                    doc_path=doc_path, anchor=_doc_id(node, "TypeSignature"),
                    signature=summary or _signature(node, "TypeSignature"),
                )

            seen: set[str] = set()
            for member in node.findall("./Members/Member"):
                member_name = member.get("MemberName") or ""
                if not member_name or member_name.startswith("op_"):
                    continue
                member_docs = member.find("Docs")
                member_summary = (_flatten(member_docs.find("summary"))
                                  if member_docs is not None else "")
                anchor = _doc_id(member, "MemberSignature")
                for candidate in (f"{short}.{member_name}", member_name):
                    # Overloads share a MemberName; the first wins, because a
                    # lookup returning eight identical rows for Split is worse
                    # than one returning the page that documents them all.
                    if candidate in seen:
                        continue
                    seen.add(candidate)
                    yield ApiSymbol(
                        name=candidate, kind="member", namespace=full_name,
                        doc_path=doc_path, anchor=anchor,
                        signature=(member_summary
                                   or _signature(member, "MemberSignature")),
                    )
