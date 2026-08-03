"""Work out what a developer handed `which_repo`, and pull evidence from it.

Four shapes arrive in practice: a diff under review, a stack trace, a bare
symbol name, and a prose description of a task. Three of the four need no
embeddings at all, which is why this tool ships before any vectors exist.

Detection order is load-bearing. A diff *contains* file paths and a stack
trace *contains* symbol names, so the most specific pattern must be tested
first or every diff would be read as a stack trace.
"""

from __future__ import annotations

import re


class Shape:
    DIFF = "diff"
    STACK = "stack"
    SYMBOL = "symbol"
    PROSE = "prose"


_DIFF_RE = re.compile(r"^(diff --git |@@ .* @@|\+\+\+ |--- )", re.MULTILINE)
_FRAME_RE = re.compile(r"(?:^|[\s(])([\w./\\-]+\.\w+):(\d+)", re.MULTILINE)
_AT_FRAME_RE = re.compile(r"^\s*(?:at|from|#\d+)\s+\S", re.MULTILINE)
_DIFF_PATH_RE = re.compile(r"^diff --git a/(\S+) b/\S+", re.MULTILINE)
_IDENT_RE = re.compile(r"\b[A-Za-z_][A-Za-z0-9_]*(?:(?:::|\.)[A-Za-z_][A-Za-z0-9_]*)*\b")

#: Identifier-shaped English that would otherwise swamp the real terms.
_STOPWORDS = frozenset("""
a an and add or the to for of in on with without is are be do does how what
where when why which that this it its from into support change fix update
make add new use using need want should would could can i we my our
""".split())


def detect_shape(text: str) -> str:
    if _DIFF_RE.search(text):
        return Shape.DIFF
    frames = len(_FRAME_RE.findall(text))
    if frames >= 2 or (frames >= 1 and _AT_FRAME_RE.search(text)):
        return Shape.STACK
    stripped = text.strip()
    if len(stripped.split()) <= 2 and _IDENT_RE.fullmatch(stripped):
        return Shape.SYMBOL
    return Shape.PROSE


def extract_paths(text: str) -> list[str]:
    """File paths named explicitly, in order, without duplicates."""
    found = _DIFF_PATH_RE.findall(text) or [m[0] for m in _FRAME_RE.findall(text)]
    seen, out = set(), []
    for path in found:
        normalised = path.replace("\\", "/")
        if normalised not in seen:
            seen.add(normalised)
            out.append(normalised)
    return out


def extract_symbols(text: str) -> list[str]:
    """Identifier-shaped tokens worth looking up, stopwords removed."""
    seen, out = set(), []
    for token in _IDENT_RE.findall(text):
        leaf = re.split(r"::|\.", token)[-1]
        if leaf.lower() in _STOPWORDS or len(leaf) < 3:
            continue
        if token not in seen:
            seen.add(token)
            out.append(token)
    return out
