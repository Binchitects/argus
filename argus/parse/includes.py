from __future__ import annotations

import re

# Anchored at line start (allowing leading whitespace) so that #include
# appearing inside a string literal or after code on the same line is ignored.
INCLUDE_RE = re.compile(
    r'^[ \t]*#[ \t]*include[ \t]*(?:<(?P<angle>[^>\r\n]+)>|"(?P<quote>[^"\r\n]+)")',
    re.MULTILINE,
)

LINE_COMMENT_RE = re.compile(r'^[ \t]*(?://|/\*|\*)')


def extract_includes(content: str) -> list[dict]:
    seen: set[tuple[str, int]] = set()
    out: list[dict] = []
    for line in content.splitlines():
        if LINE_COMMENT_RE.match(line):
            continue
        match = INCLUDE_RE.match(line)
        if match is None:
            continue
        angle = match.group("angle")
        raw = angle if angle is not None else match.group("quote")
        key = (raw, 1 if angle is not None else 0)
        if key in seen:
            continue
        seen.add(key)
        out.append({"raw": key[0], "is_angle": key[1]})
    return out
