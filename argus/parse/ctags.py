from __future__ import annotations

import json
import logging
import shutil
import subprocess
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


def extract_symbols(root: Path, rel_paths: list[str]) -> dict[str, list[dict]]:
    """Run ctags over rel_paths (relative to root); return path -> symbols."""
    if not rel_paths:
        return {}
    exe = shutil.which("ctags")
    if exe is None:
        raise CtagsUnavailable(
            "ctags not found on PATH — install universal-ctags on the index host"
        )

    existing = [p for p in rel_paths if (root / p).is_file()]
    if not existing:
        return {}

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
    if proc.returncode != 0 and proc.stdout:
        # Partial batch: some tags came back, but ctags also reported a
        # non-zero exit. There is no conn here to record it against a repo,
        # so at least surface it in the logs instead of silently discarding
        # proc.stderr.
        logger.warning(
            "ctags exited %s with partial output for %d file(s): %s",
            proc.returncode, len(existing), proc.stderr.strip()[:500],
        )

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
    return results
