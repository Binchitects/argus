"""The doc comment attached to a symbol -- what it DOES, not what it is called.

WHY THIS EXISTS

`semantic_search` embedded a symbol's `signature`, `kind`, `scope` and `path`.
That is the name and the shape, and nothing about behaviour, so a question
phrased the way a person asks it -- "what expires keys past their TTL" -- could
only match the vocabulary it had. Measured on a real corpus, that produced
`expireSlaveKeys` where `activeExpireCycle` was the answer: both in the same
file, both plausible names, and the only thing that distinguishes them is the
sentence above each one saying what it does.

That sentence was in the index the whole time. `files.content` holds the entire
file and `symbols.line` locates the symbol inside it, so the comment immediately
above a definition has been sitting in the database, unread, since the first
version. This module reads it.

WHAT IT IS NOT

Not a documentation generator, and not a parser. It walks backwards from a
definition's first line, collects an adjacent comment block, strips the markers
and stops at anything that is not a comment. No language server, no AST -- the
input is what ctags already told us plus the file text we already stored, which
means it works for all ~40 languages ctags knows rather than for the handful
anyone would write a parser for.
"""
from __future__ import annotations

import re
import textwrap
from collections.abc import Sequence

#: How far back to look for the START of a comment, and how much of it to keep.
#:
#: Two different limits on purpose, and conflating them was a bug: a single
#: bound meant a 30-line documentation block was never even FOUND, because the
#: walk gave up before reaching its opening marker. Clipping long text is right;
#: silently returning nothing for it is not.
SEARCH_LINES = 60

#: What is kept. A block longer than this is a file header or a licence far more
#: often than it is a symbol's documentation, and the banner check below catches
#: the case that actually matters.
MAX_CHARS = 1200

#: Line-comment markers, per ctags language name (lowercased).
_LINE_MARKERS: dict[str, tuple[str, ...]] = {
    "python": ("#",), "ruby": ("#",), "sh": ("#",), "bash": ("#",),
    "zsh": ("#",), "perl": ("#",), "r": ("#",), "make": ("#",),
    "cmake": ("#",), "yaml": ("#",), "toml": ("#",), "powershell": ("#",),
    "puppet": ("#",), "awk": ("#",), "tcl": ("#",),
    "sql": ("--",), "lua": ("--",), "haskell": ("--",), "ada": ("--",),
    "elm": ("--",),
    "lisp": (";",), "clojure": (";",), "scheme": (";",), "asm": (";",),
    "erlang": ("%",), "matlab": ("%",), "tex": ("%",),
    "fortran": ("!",),
    "vim": ('"',),
}

#: Default for everything else -- C, C++, Java, C#, Go, Rust, JavaScript,
#: TypeScript, Swift, Kotlin, Scala, PHP, Dart and the rest of the C family.
_DEFAULT_LINE = ("//",)

#: Languages with C-style block comments.
_BLOCK_LANGS = {
    "c", "c++", "cpp", "cxx", "cc", "h", "h++", "hpp", "java", "c#", "cs",
    "go", "rust", "javascript", "typescript", "jsx", "tsx", "swift", "kotlin",
    "scala", "php", "dart", "objective-c", "objc", "css", "scss", "less",
    "protobuf", "solidity", "verilog", "systemverilog", "v", "d", "zig",
}
_DEFAULT_BLOCK = ("/*", "*/")

#: Languages whose documentation lives in a string literal INSIDE the body
#: rather than in a comment above it. Python is the one that matters; the others
#: are here because they use the same convention.
_DOCSTRING_LANGS = {"python", "ruby"}

_BLANK = re.compile(r"^\s*$")
#: A decorator/annotation/attribute line between the comment and the symbol.
#: Python's `@property`, Java's `@Override`, C# `[Attribute]`, Rust `#[derive]`.
_SKIPPABLE = re.compile(r"^\s*(@|\[[A-Za-z]|#\[)")

#: Lines that mark the start of a file-level banner rather than a symbol doc.
#: Cheap and deliberately narrow: the cost of missing one is a licence header
#: attached to a single symbol, and the cost of being clever is dropping real
#: documentation that happens to mention "copyright" in a code sample.
_BANNER = re.compile(
    r"copyright|\(c\)\s*\d{4}|spdx-license|licensed under|all rights reserved",
    re.I)


def _markers(lang: str | None) -> tuple[tuple[str, ...], tuple[str, ...] | None]:
    """Line markers and, where the language has them, C-style block markers.

    THE UNKNOWN CASE IS THE COMMON CASE, not an edge case. Universal Ctags does
    not emit its `language` field unless asked (`--fields=+l`, which this index
    does not pass), so `lang` is None for every symbol in practice -- and the
    first version returned line comments WITHOUT block-comment support for that
    case, which silently meant a `/** ... */` above a function was never read.
    The fixture's own corpus is doxygen-commented C, so it extracted nothing at
    all and the only thing that would have caught it was running it.

    Defaulting to the whole C family is right: it is what the overwhelming
    majority of INDEXED languages use, and a wrong guess costs one symbol some
    odd text rather than losing every block comment in the estate.
    """
    key = (lang or "").strip().lower()
    if key in _LINE_MARKERS:
        return (_LINE_MARKERS[key],
                _DEFAULT_BLOCK if key in _BLOCK_LANGS else None)
    return _DEFAULT_LINE, _DEFAULT_BLOCK


def _strip_line(text: str, marker: str) -> str | None:
    """`text` with `marker` removed, or None if it is not that kind of comment."""
    stripped = text.lstrip()
    if not stripped.startswith(marker):
        return None
    # `#!` is a shebang, not documentation.
    if marker == "#" and stripped.startswith("#!"):
        return None
    body = stripped[len(marker):]
    # `///` and `//!` and `##` are the same comment with decoration. Only the
    # FIRST character is dropped: `////` is a divider, and eating all of them
    # turns it into an empty line.
    body = body[1:] if body.startswith(marker[0]) else body
    # The space after the marker is convention, not content -- `// Foo` and
    # `//Foo` say the same thing, and leaving it makes every continuation line
    # start with a stray space in the stored text and in the embedding.
    return body.lstrip().rstrip()


def leading_comment(lines: Sequence[str], line: int,
                    lang: str | None = None) -> str:
    """The doc comment ending immediately above `line` (1-based), or "".

    At most one blank line is tolerated between the comment and the definition,
    because plenty of real code has one and a comment separated by a paragraph
    break is still the comment for what follows. Two blank lines is a new
    paragraph in the file, and continuing past it starts attributing whatever
    came before.
    """
    if line < 2 or line > len(lines):
        return ""
    line_markers, block = _markers(lang)

    i = line - 2                     # 0-based index of the line above
    # Decorators, annotations and attributes sit between the comment and the
    # definition. Skipping them is what makes `@property\ndef f():` keep its
    # doc, and it is the difference between working on Python and not.
    while i >= 0 and _SKIPPABLE.match(lines[i] or ""):
        i -= 1
    if i < 0:
        return ""

    # The blank line, if there is one, comes BEFORE deciding whether this is a
    # block comment. Checking for `*/` first and only then skipping blanks meant
    # `/* Does a thing. */\n\nint foo(void);` found nothing: the walk was
    # already on the blank line and gave up without looking one further up.
    if _BLANK.match(lines[i] or ""):
        i -= 1
        if i < 0 or _BLANK.match(lines[i] or ""):
            return ""

    # ---- block comment: /* ... */ or /** ... */ ----------------------------
    if block and block[1] in (lines[i] or ""):
        open_marker, close_marker = block
        # The closing marker is on the line just above the symbol. Walk up to
        # its opener, then read the block's body out of that span.
        k = i
        parts: list[str] = []
        while k >= 0 and k > i - SEARCH_LINES:
            candidate = lines[k]
            body = candidate
            if close_marker in body:
                body = body[:body.rindex(close_marker)]
            if open_marker in body:
                body = body[body.index(open_marker) + len(open_marker):]
                parts.append(body)
                block_text = _clean_block(reversed(parts))
                if block_text and not _is_banner(block_text, k + 1):
                    return _clip(block_text)
                return ""
            parts.append(body)
            k -= 1

    # ---- line comments: // ... or # ... ------------------------------------
    collected: list[str] = []
    j = i
    while j >= 0 and j > i - SEARCH_LINES:
        text = lines[j]
        if _BLANK.match(text or ""):
            break                # the one allowed blank was already consumed
        body = None
        for marker in line_markers:
            body = _strip_line(text, marker)
            if body is not None:
                break
        if body is None:
            break
        collected.append(body)
        j -= 1

    if not collected:
        return ""
    collected.reverse()
    # Drop trailing blank comment lines (`//` alone), which are paragraph
    # breaks inside the block rather than content.
    while collected and not collected[-1].strip():
        collected.pop()
    text = "\n".join(collected).strip()
    if not text:
        return ""
    if _is_banner(text, j + 1):
        return ""
    return _clip(text)


def _clean_block(parts) -> str:
    """Strip a block comment's leading `*` margin and trailing whitespace."""
    out = []
    for raw in parts:
        body = raw.strip()
        if body.startswith("*"):
            body = body[1:]
        out.append(body.strip())
    while out and not out[0]:
        out.pop(0)
    while out and not out[-1]:
        out.pop()
    return "\n".join(out).strip()


def _is_banner(text: str, first_line_index: int) -> bool:
    """Does this look like a file banner rather than a symbol's documentation?

    Only when it also reaches the top of the file: a comment at line 1 that
    says "Copyright" is a licence, whereas the same words at line 400 are
    somebody documenting a function that happens to mention licensing. Without
    that second condition this drops real documentation, which is the more
    expensive mistake.
    """
    if first_line_index > 1:
        return False
    return bool(_BANNER.search(text))


def _clip(text: str) -> str:
    if len(text) > MAX_CHARS:
        # Cut at a line boundary so the stored text does not end mid-word.
        text = text[:MAX_CHARS].rsplit("\n", 1)[0].rstrip()
    return text


#: The first string literal at the start of a body, which is where these
#: languages put their documentation.
_DOCSTRING = re.compile(
    r'^[rubf]{0,2}("""|\'\'\'|"|\')(.*?)(?:\1)', re.S)

#: How many lines a docstring may span before this gives up. A docstring is
#: bounded in practice; joining an entire file to find a closing `"""` that was
#: never written is how a parser turns one bad file into a slow pass.
_DOCSTRING_LINES = 60


def docstring(lines: Sequence[str], line: int, lang: str | None = None) -> str:
    """The docstring at the start of a body, for languages that use one.

    Reads the definition line's remainder and the lines after it as ONE string,
    because the whole shape being looked for is a multi-line `\"\"\"...\"\"\"`.
    The first version matched line by line, so it could only ever find
    single-line docstrings -- which is the minority of them, and none of the
    ones that actually explain a function.

    Bounded to the first few lines and to `_DOCSTRING_LINES`: a string literal
    further into a body is data, not documentation, and a body that never closes
    its quotes must not make this walk the file.
    """
    key = (lang or "").strip().lower()
    if key not in _DOCSTRING_LANGS:
        return ""
    if line < 1 or line > len(lines):
        return ""

    # The body starts on the definition's own line if something follows the
    # colon (`def f(): "Doc."`), and on a later line otherwise.
    head = lines[line - 1]
    after = head.split(":", 1)[1] if ":" in head else ""
    pending = [after] if after.strip() else []
    start = line if after.strip() else line

    # At most one blank line before the body, then the literal must be the very
    # first thing -- anything else and this is a statement, not a docstring.
    for _ in range(3):
        if start >= len(lines):
            return ""
        if lines[start].strip():
            break
        start += 1
    if start >= len(lines):
        return ""

    window = "\n".join(pending + list(lines[start:start + _DOCSTRING_LINES]))
    match = _DOCSTRING.match(window.lstrip())
    if not match:
        return ""
    quote, content = match.group(1), match.group(2)
    # A single-quoted one-liner that is not a sentence is far more likely to be
    # a SQL statement or a format string than documentation.
    if len(quote) == 1 and len(content) > 60:
        return ""
    return _clip(_dedent_body(content))


def _dedent_body(text: str) -> str:
    """A docstring with its source indentation removed.

    The first line sits right after the opening quotes and carries no indent,
    while every line under it is indented to the body. Running `textwrap.dedent`
    over the whole thing therefore removes nothing -- the first line's empty
    prefix is the common one -- and the stored text keeps four spaces on every
    continuation line, which then travels into the embedding.
    """
    lines = text.splitlines()
    while lines and not lines[0].strip():
        lines.pop(0)
    if not lines:
        return ""
    head = lines[0].strip()
    # `rest` is NOT stripped before joining: the blank line between a summary
    # and the body is the convention in every Python docstring, and stripping it
    # here silently welded the two sentences together.
    rest = textwrap.dedent("\n".join(lines[1:]))
    return f"{head}\n{rest}".strip()


def for_symbol(lines: Sequence[str], line: int, lang: str | None = None) -> str:
    """Documentation for one symbol, from whichever convention the language uses.

    Python is checked for a docstring FIRST and falls back to the comment above,
    because that is the order of authority there: a `#` above a `def` in a
    Python file is usually a section banner, and the docstring is the text
    written for callers. Every other language has only the comment, so this is
    one branch rather than a rule about precedence.
    """
    key = (lang or "").strip().lower()
    if key in _DOCSTRING_LANGS:
        inside = docstring(lines, line, lang)
        if inside:
            return inside
    return leading_comment(lines, line, lang)
