"""Heading-aware chunking for prose/documentation packs.

Code chunks at function boundaries; prose has no such structure, so it
chunks at Markdown headings instead. The critical property is not the
splitting itself but what gets embedded: a section body on its own
("A string indicating how to handle a redirect response.") embeds to
almost nothing useful, because it could be about anything. Prepending the
heading trail ("fetch() > Parameters > options > redirect") makes the
chunk self-locating. See ``embed_text`` -- that prepending is the entire
point of this module.

Nothing here may import ``argus.store.queries`` or open ``index.db``;
packs are a second, public corpus entirely separate from the private index.
"""

from __future__ import annotations

import re
from dataclasses import dataclass

_HEADING_RE = re.compile(r'^(#{1,6})[ \t]+(.+?)[ \t]*#*[ \t]*$')
_FENCE_RE = re.compile(r'^(```+|~~~+)')


def _closes_fence(fence_match: re.Match[str], opener: tuple[str, int]) -> bool:
    """A closing fence must use the same character as the opener and be at
    least as long (CommonMark). A different character, or a shorter run of
    the same character, is not a closer -- it's just fenced content (e.g. a
    ``~~~`` line shown inside a backtick-fenced block of documentation)."""
    marker = fence_match.group(1)
    char, min_length = opener
    return marker[0] == char and len(marker) >= min_length


_SLUG_STRIP_RE = re.compile(r'[^\w\s-]')
_SLUG_WHITESPACE_RE = re.compile(r'[\s_]+')


@dataclass
class Chunk:
    """One retrievable unit of a documentation pack.

    ``heading_path`` is the full trail from the document's top-level
    heading down to whichever heading directly governs ``body`` (for
    example ``"fetch() > Parameters > options"``); it is ``""`` only for
    content that appears before any heading at all. ``anchor`` is the slug
    of the immediate (deepest) heading, for deep-linking back to the
    source. ``start_line`` is the 1-indexed source line the body begins
    at.
    """

    heading_path: str
    anchor: str | None
    start_line: int
    body: str


def _slugify(title: str) -> str:
    slug = title.strip().lower()
    slug = _SLUG_STRIP_RE.sub("", slug)
    slug = _SLUG_WHITESPACE_RE.sub("-", slug)
    return slug.strip("-")


def _dedupe_anchor(base: str, seen: dict[str, int]) -> str:
    """Make ``base`` unique within a document, the conventional way: the
    first heading to produce a given slug keeps it bare, later ones get a
    numbered suffix (``abortcontroller``, ``abortcontroller-2``, ...).

    Without this, two headings that slug identically -- an H1
    ``AbortController`` and an H3 ``AbortController()`` are a real,
    MDN-shaped example -- would deep-link to the same anchor and silently
    strand the reader at the wrong one.
    """
    count = seen.get(base, 0) + 1
    seen[base] = count
    return base if count == 1 else f"{base}-{count}"


@dataclass
class _Section:
    heading_path: str
    anchor: str | None
    start_line: int
    lines: list[str]


def _split_into_sections(text: str) -> list[_Section]:
    """Walk the document once, maintaining a heading stack.

    A heading at level *n* pops every stack entry at level >= n before
    pushing itself -- get this backwards (level > n) and same-level
    siblings nest into each other; get the whole rule wrong and deeper
    headings leak into their uncles (``options > Return value``).

    Fenced code blocks are tracked here too, purely so a ``#`` inside one
    is never mistaken for a heading.
    """
    lines = text.splitlines()
    stack: list[tuple[int, str]] = []
    sections: list[_Section] = []
    current_lines: list[str] = []
    current_start = 1
    fence_opener: tuple[str, int] | None = None
    seen_anchors: dict[str, int] = {}

    def flush() -> None:
        heading_path = " > ".join(title for _, title in stack)
        if stack:
            anchor = _dedupe_anchor(_slugify(stack[-1][1]), seen_anchors)
        else:
            anchor = None
        sections.append(_Section(heading_path, anchor, current_start,
                                  list(current_lines)))

    for lineno, line in enumerate(lines, start=1):
        stripped = line.strip()
        fence_match = _FENCE_RE.match(stripped)

        if fence_opener is not None:
            current_lines.append(line)
            if fence_match and _closes_fence(fence_match, fence_opener):
                fence_opener = None
            continue

        if fence_match:
            fence_opener = (fence_match.group(1)[0], len(fence_match.group(1)))
            current_lines.append(line)
            continue

        heading_match = _HEADING_RE.match(line)
        if heading_match:
            flush()
            level = len(heading_match.group(1))
            title = heading_match.group(2).strip()
            while stack and stack[-1][0] >= level:
                stack.pop()
            stack.append((level, title))
            current_lines = []
            current_start = lineno + 1
            continue

        current_lines.append(line)

    flush()
    return [s for s in sections if "\n".join(s.lines).strip()]


@dataclass
class _Block:
    start_line: int
    text: str
    is_fence: bool


def _split_into_blocks(lines: list[str], start_line: int) -> list[_Block]:
    """Split section lines into paragraphs, keeping each fenced code block
    (open marker to close marker, blank lines and all) as one atomic block
    that later splitting must never break open."""
    blocks: list[_Block] = []
    cur: list[str] = []
    cur_start = start_line
    fence_opener: tuple[str, int] | None = None

    for offset, line in enumerate(lines):
        lineno = start_line + offset
        stripped = line.strip()
        fence_match = _FENCE_RE.match(stripped)

        if fence_opener is not None:
            cur.append(line)
            if fence_match and _closes_fence(fence_match, fence_opener):
                blocks.append(_Block(cur_start, "\n".join(cur), True))
                cur = []
                fence_opener = None
            continue

        if fence_match:
            if cur:
                blocks.append(_Block(cur_start, "\n".join(cur), False))
                cur = []
            cur_start = lineno
            cur = [line]
            fence_opener = (fence_match.group(1)[0], len(fence_match.group(1)))
            continue

        if stripped == "":
            if cur:
                blocks.append(_Block(cur_start, "\n".join(cur), False))
                cur = []
            continue

        if not cur:
            cur_start = lineno
        cur.append(line)

    if cur:
        blocks.append(_Block(cur_start, "\n".join(cur), fence_opener is not None))

    return blocks


def _wrap_by_chars(text: str, max_chars: int) -> list[str]:
    """Split a single oversized paragraph at word boundaries. Used only for
    non-fence blocks, since a fence must never be broken regardless of size."""
    words = text.split()
    if not words:
        return []

    pieces: list[str] = []
    cur: list[str] = []
    cur_len = 0
    for word in words:
        extra = len(word) + (1 if cur else 0)
        if cur and cur_len + extra > max_chars:
            pieces.append(" ".join(cur))
            cur = [word]
            cur_len = len(word)
        else:
            cur.append(word)
            cur_len += extra
    if cur:
        pieces.append(" ".join(cur))
    return pieces


def _pack_units(units: list[tuple[int, str]], max_chars: int) -> list[tuple[int, str]]:
    """Greedily pack paragraph/fence units into chunks under max_chars.
    A single unit that is already oversized (an unsplittable fence, or a
    paragraph already at the word-wrap limit) is still emitted alone
    rather than dropped or forced together with a neighbor."""
    packed: list[tuple[int, str]] = []
    cur_texts: list[str] = []
    cur_start = 0
    cur_len = 0

    for start_line, text in units:
        extra = len(text) + (2 if cur_texts else 0)
        if cur_texts and cur_len + extra > max_chars:
            packed.append((cur_start, "\n\n".join(cur_texts)))
            cur_texts = [text]
            cur_start = start_line
            cur_len = len(text)
        else:
            if not cur_texts:
                cur_start = start_line
            cur_texts.append(text)
            cur_len += extra

    if cur_texts:
        packed.append((cur_start, "\n\n".join(cur_texts)))

    return packed


def chunk_markdown(text: str, *, max_chars: int = 1200) -> list[Chunk]:
    """Chunk Markdown prose at heading boundaries.

    Every chunk carries the full heading trail down to whichever heading
    governs it. Oversized sections are split at paragraph boundaries (and,
    if a single paragraph alone exceeds ``max_chars``, at word boundaries),
    but a fenced code block is never split -- a half-open fence would
    poison every downstream consumer of that chunk.
    """
    chunks: list[Chunk] = []

    for section in _split_into_sections(text):
        blocks = _split_into_blocks(section.lines, section.start_line)

        units: list[tuple[int, str]] = []
        for block in blocks:
            if block.is_fence or len(block.text) <= max_chars:
                units.append((block.start_line, block.text))
            else:
                units.extend(
                    (block.start_line, piece)
                    for piece in _wrap_by_chars(block.text, max_chars)
                )

        for start_line, body in _pack_units(units, max_chars):
            chunks.append(Chunk(
                heading_path=section.heading_path,
                anchor=section.anchor,
                start_line=start_line,
                body=body,
            ))

    return chunks


def embed_text(chunk: Chunk) -> str:
    """The string that actually gets embedded.

    This is the entire reason the module exists: prepend the heading
    trail so the chunk is self-locating. A pack built from
    ``chunk.body`` alone looks identical in every metric that isn't
    retrieval quality -- same chunk count, same sizes -- and simply
    returns worse answers forever.
    """
    if not chunk.heading_path:
        return chunk.body
    return f"{chunk.heading_path}\n{chunk.body}"
