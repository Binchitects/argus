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
    if _IDENT_RE.fullmatch(stripped):
        return Shape.SYMBOL
    return Shape.PROSE


#: Source extensions worth treating a bare token as a filename. Deliberately a
#: closed list rather than "any dotted token": prose ends sentences with a
#: period, and `decoder. it` must not become a path. The extension is the only
#: thing that distinguishes `inflate.c` from `i.e`.
_SOURCE_EXTS = (
    "c|h|cc|cpp|cxx|c\\+\\+|hpp|hxx|hh|inl|ipp|s|asm"      # C/C++ and assembly
    "|py|rs|go|java|js|ts|tsx|jsx|rb|php|cs|swift|kt|m|mm"  # other languages
    "|md|rst|txt|json|ya?ml|toml|cmake|mk"                  # docs and build
)
_FILE_TOKEN_RE = re.compile(
    rf"(?<![\w/.-])([\w+.-]+(?:/[\w+.-]+)*\.(?:{_SOURCE_EXTS}))(?![\w])",
    re.IGNORECASE,
)


def extract_paths(text: str) -> list[str]:
    """File paths named explicitly, in order, without duplicates.

    Three sources, most authoritative first. A diff names the file it changes
    in its header, so when one is present nothing else is consulted; a stack
    trace names one per frame; and failing both, a bare filename in prose is
    still a filename.

    That last source was missing, and its absence was not a small gap: a bare
    `inflate.c` is not a path by the first two rules and matches no *symbol*
    either, so `which_repo("inflate.c")` produced no evidence at all and
    returned []. An empty result reads as "that code is not indexed here",
    which is worse than a wrong repo -- it is a confident denial.
    """
    found = (_DIFF_PATH_RE.findall(text)
             or [m[0] for m in _FRAME_RE.findall(text)]
             or _FILE_TOKEN_RE.findall(text))
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
        # Qualify on *any* part, not the last one. Taking only the last part
        # is right for `std::vector` and `obj.method`, and catastrophic for a
        # filename: `inflate.c` has leaf `c`, which fails the 3-character
        # minimum, so the entire token was dropped. In a C/C++ index that
        # silently discarded every .c and .h name a developer typed -- and
        # `which_repo("inflate.c")` answered with nothing at all, which reads
        # as "no such code here" rather than as a bug.
        parts = re.split(r"::|\.", token)
        if not any(len(p) >= 3 and p.lower() not in _STOPWORDS for p in parts):
            continue
        if token not in seen:
            seen.add(token)
            out.append(token)
    return out
