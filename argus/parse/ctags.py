from __future__ import annotations

import json
import logging
import re
import shutil
import subprocess
from dataclasses import dataclass, field
from pathlib import Path, PurePosixPath

from .filters import HEADER_EXTENSIONS

logger = logging.getLogger(__name__)

PRIVATE_SCOPES = frozenset({"detail", "internal", "impl", "anonymous"})

# Universal Ctags has no internal timeout of its own: one pathological
# input that makes it spin would otherwise block the indexer forever, with
# no error and no way to tell which repo it died on (repo_time_budget_seconds
# is only checked in the per-file loop, never once ctags has been invoked).
# 600s is generous even for a large real batch of files in one repo.
CTAGS_TIMEOUT_SECONDS = 600

CTAGS_ARGS = [
    "--output-format=json",
    # n=line, K=long kind, S=signature, s=scope, e=end line,
    # f=file-limited visibility (i.e. `static`), surfaced as JSON key "file".
    "--fields=+nKSsef",
    # Universal Ctags disables the `prototype` kind by default for C/C++;
    # without it, header-only declarations (e.g. `int Foo(int);`) are
    # dropped entirely rather than reported with kind "prototype".
    "--kinds-c=+p",
    "--kinds-c++=+p",
    "-L", "-",   # read the file list from stdin
    "-f", "-",   # write tags to stdout
]


class CtagsUnavailable(RuntimeError):
    """universal-ctags is not installed or is the wrong implementation."""


@dataclass(frozen=True)
class SymbolBatch:
    """The result of one ctags invocation, including what it did NOT process.

    The symbol map alone cannot answer "was this path processed?". ctags
    emits no output at all for a file it parsed cleanly but that contains no
    taggable declarations (an include-only .c, a macro-only header), which is
    byte-for-byte indistinguishable from a file it never opened. Callers that
    stamp a per-path completion marker need that distinction or they will
    mark uncovered files complete forever, so it is reported explicitly:
    every path handed to extract_symbols lands in exactly one of `covered`
    and `uncovered`.
    """

    symbols: dict[str, list[dict]] = field(default_factory=dict)
    covered: frozenset[str] = frozenset()
    uncovered: dict[str, str] = field(default_factory=dict)   # path -> why


def _paths_named_in(paths: list[str], stderr: str) -> set[str]:
    """The listed paths ctags explicitly complained about.

    ctags names the offending file in its diagnostics ("cannot open input
    file \"x.c\""), which is the only attribution available: the JSON output
    format has no per-file markers.

    Matching is NOT a plain substring test: `main.c` and `sub/main.c` are
    ordinary namesakes in real C repos, and a naive `p in haystack` blames
    the root `main.c` for a diagnostic that only ever named `sub/main.c`
    (any path that is a substring of another listed path is at risk). That
    is not a safe over-match -- being blamed is destructive AND budgeted:
    the caller deletes the symbol rows ctags just extracted for the
    wrongly-blamed path, NULLs its symbols_sha, and charges it a retry
    attempt via `_cap_retries`. Three such passes permanently strand a
    perfectly healthy file as symbol-less and retry-exhausted.

    Each candidate path is therefore matched only at a path boundary: the
    character immediately before and after the match (if any) must not
    itself be a path-continuation character (word char, `.`, `/`, `-`).
    This handles both quoted diagnostics ("cannot open input file
    \"sub/main.c\"") and unquoted ones (ctags does not always quote the
    path) without needing separate quote-parsing logic, since `/` counts
    as a path-continuation character on the left: `main.c` inside
    `sub/main.c` is always preceded by `/`, which fails the boundary check.
    """
    if not stderr:
        return set()
    haystack = stderr.replace("\\", "/")
    blamed = set()
    for p in paths:
        pattern = r"(?<![\w./-])" + re.escape(p) + r"(?![\w./-])"
        if re.search(pattern, haystack):
            blamed.add(p)
    return blamed


def is_public_symbol(path: str, scope: str | None, file_restricted: bool) -> bool:
    if scope:
        parts = {p.strip() for p in scope.replace("::", ".").split(".")}
        # "__anon..." is an undocumented ctags-internal naming convention for
        # anonymous-namespace scopes, not a stable public API — a future
        # ctags release changing it would silently reclassify anonymous-
        # namespace symbols as public.
        if any(p in PRIVATE_SCOPES or p.startswith("__anon") for p in parts):
            return False
    if PurePosixPath(path).suffix.lower() in HEADER_EXTENSIONS:
        return True
    return not file_restricted


def extract_symbols(root: Path, rel_paths: list[str]) -> SymbolBatch:
    """Run ctags over rel_paths (relative to root).

    Returns a SymbolBatch: the extracted symbols plus an explicit account of
    which of rel_paths were genuinely processed. Callers must not infer
    coverage from the symbol map (see SymbolBatch).

    A repo-global failure (ctags missing, wrong implementation, timed out)
    still raises CtagsUnavailable -- that is not a per-path outcome.
    """
    if not rel_paths:
        return SymbolBatch()
    exe = shutil.which("ctags")
    if exe is None:
        raise CtagsUnavailable(
            "ctags not found on PATH — install universal-ctags on the index host"
        )

    # A path git checked out that the filesystem will not hand back (a
    # Windows reserved name like aux.c, a >260-char path, a trailing dot) is
    # dropped from the argument list here. It was never processed, so it is
    # uncovered -- not "processed, found nothing".
    existing = [p for p in rel_paths if (root / p).is_file()]
    listed = set(existing)
    uncovered = {
        p: "not a readable file on disk when ctags ran"
        for p in rel_paths if p not in listed
    }
    if not existing:
        return SymbolBatch(uncovered=uncovered)

    try:
        proc = subprocess.run(
            [exe, *CTAGS_ARGS],
            input="\n".join(existing),
            cwd=root,
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            timeout=CTAGS_TIMEOUT_SECONDS,
        )
    except subprocess.TimeoutExpired as exc:
        raise CtagsUnavailable(
            f"ctags timed out after {CTAGS_TIMEOUT_SECONDS}s"
        ) from exc
    if proc.returncode != 0 and not proc.stdout:
        raise CtagsUnavailable(f"ctags failed: {proc.stderr.strip()[:500]}")

    results: dict[str, list[dict]] = {}
    for line in proc.stdout.splitlines():
        if not line.strip():
            continue
        try:
            entry = json.loads(line)
        except json.JSONDecodeError:
            continue
        if entry.get("_type") != "tag":
            continue

        path = PurePosixPath(entry["path"].replace("\\", "/")).as_posix()
        scope = entry.get("scope")
        results.setdefault(path, []).append({
            "name": entry["name"],
            "kind": entry.get("kind", "unknown"),
            "line": int(entry.get("line", 0)),
            "end_line": int(entry["end"]) if entry.get("end") else None,
            "signature": entry.get("signature"),
            "scope": scope,
            "is_public": int(is_public_symbol(path, scope, bool(entry.get("file", False)))),
        })

    if proc.returncode == 0:
        return SymbolBatch(results, frozenset(existing), uncovered)

    # Partial batch: some tags came back, but ctags also reported a non-zero
    # exit. There is no conn here to record it against a repo, so at least
    # surface it in the logs instead of silently discarding proc.stderr.
    detail = proc.stderr.strip()[:500]
    logger.warning(
        "ctags exited %s with partial output for %d file(s): %s",
        proc.returncode, len(existing), detail,
    )
    blamed = _paths_named_in(existing, proc.stderr)
    # When ctags named the files it choked on, everything else in the batch
    # really was processed -- including the symbol-free ones, which is the
    # whole reason coverage is tracked separately from the symbol map.
    # When it named nothing, no such claim can be made, so only paths that
    # actually produced tags count as covered.
    covered = listed - blamed if blamed else set(results) & listed
    for path in sorted(listed - covered):
        uncovered[path] = (
            f"ctags exited {proc.returncode} without covering this file: "
            f"{detail or 'no diagnostic output'}"
        )
    return SymbolBatch(results, frozenset(covered), uncovered)
