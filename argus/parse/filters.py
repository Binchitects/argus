from __future__ import annotations

from collections.abc import Sequence
from pathlib import PurePosixPath

EXTENSION_LANG = {
    ".c": "c", ".h": "cpp", ".hpp": "cpp", ".hxx": "cpp", ".inl": "cpp",
    ".cc": "cpp", ".cpp": "cpp", ".cxx": "cpp",
    ".py": "python", ".cs": "csharp",
    ".ts": "typescript", ".tsx": "typescript",
    ".js": "javascript", ".jsx": "javascript",
    ".md": "markdown", ".txt": "text",
}

HEADER_EXTENSIONS = frozenset({".h", ".hpp", ".hxx", ".inl"})


def is_binary(data: bytes) -> bool:
    return b"\x00" in data[:8192]


def detect_lang(path: str) -> str | None:
    return EXTENSION_LANG.get(PurePosixPath(path).suffix.lower())


def should_index(path: str, size: int, data: bytes, *,
                 max_bytes: int, exclude_dirs: Sequence[str]) -> bool:
    if size > max_bytes:
        return False
    if detect_lang(path) is None:
        return False
    # Match whole path components so 'outbound' is not caught by 'out'.
    excluded = {d.lower() for d in exclude_dirs}
    parts = [p.lower() for p in PurePosixPath(path).parts[:-1]]
    if excluded.intersection(parts):
        return False
    return not is_binary(data)
