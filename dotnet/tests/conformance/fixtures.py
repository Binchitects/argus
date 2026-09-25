"""Git fixtures for the conformance run: real projects plus hand-made edge cases.

Real code is what makes the comparison mean something -- zlib, libpng,
libjpeg-turbo, freetype and lz4 at pinned tags are the repository's own
measurement corpus, and they vendor copies of each other, share header
basenames and carry doxygen comments, so include resolution, vendoring and doc
extraction all get exercised on inputs nobody wrote for the test.

The hand-made repositories cover what real code does not reliably contain: a
second branch, Python docstrings, a banner comment, a binary file, a file too
large to index, a path with spaces, and a repository only one user may read.
"""
from __future__ import annotations

import os
import shutil
import subprocess
from pathlib import Path

CORPUS = [
    ("zlib", "https://github.com/madler/zlib.git", "v1.3.1"),
    ("libpng", "https://github.com/pnggroup/libpng.git", "v1.6.44"),
    ("libjpeg-turbo", "https://github.com/libjpeg-turbo/libjpeg-turbo.git", "3.0.4"),
    ("freetype", "https://github.com/freetype/freetype.git", "VER-2-13-3"),
    ("lz4", "https://github.com/lz4/lz4.git", "v1.10.0"),
]

ENV = {**os.environ, "GIT_AUTHOR_NAME": "fixture", "GIT_AUTHOR_EMAIL": "f@example.invalid",
       "GIT_COMMITTER_NAME": "fixture", "GIT_COMMITTER_EMAIL": "f@example.invalid",
       "GIT_AUTHOR_DATE": "2026-01-01T00:00:00Z", "GIT_COMMITTER_DATE": "2026-01-01T00:00:00Z"}


def git(cwd: Path, *args: str) -> str:
    return subprocess.run(["git", *args], cwd=cwd, env=ENV, check=True, capture_output=True, text=True).stdout


def fetch_corpus(cache: Path) -> dict[str, Path]:
    """Shallow clones of the corpus, reused across runs."""
    cache.mkdir(parents=True, exist_ok=True)
    out = {}
    for name, url, tag in CORPUS:
        target = cache / name
        if not (target / ".git").is_dir():
            subprocess.run(["git", "clone", "-q", "--depth", "1", "--branch", tag, url, str(target)], check=True)
        out[name] = target
    return out


def _work_tree_from(source: Path | None, work: Path) -> None:
    if work.exists():
        shutil.rmtree(work)
    work.mkdir(parents=True)
    git(work, "init", "-q", "-b", "main")
    if source is not None:
        for item in source.iterdir():
            if item.name == ".git":
                continue
            dest = work / item.name
            if item.is_dir():
                shutil.copytree(item, dest, symlinks=True)
            else:
                shutil.copy2(item, dest, follow_symlinks=False)


def _commit(work: Path, message: str) -> None:
    git(work, "add", "-A")
    git(work, "commit", "-q", "-m", message, "--allow-empty")


def _publish(work: Path, bare: Path) -> None:
    bare.parent.mkdir(parents=True, exist_ok=True)
    if not bare.exists():
        git(bare.parent, "init", "-q", "--bare", bare.name)
    git(work, "push", "-q", "--force", "--all", str(bare))
    git(bare, "update-server-info")


def write(path: Path, text: str | bytes) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    if isinstance(text, bytes):
        path.write_bytes(text)
    else:
        path.write_text(text, encoding="utf-8")


DOCS_DEMO_FILES = {
    "README.md": "# docs-demo\n\nA small service that expires cache keys and retries uploads.\n",
    "src/cache.py": '''"""Cache maintenance."""


def expire_keys(store, now):
    """Remove every key whose time to live has elapsed.

    Walks the store once and deletes expired entries in place.
    """
    for key in list(store):
        if store[key] < now:
            del store[key]


# A section banner, not documentation.
def touch(store, key, ttl):
    store[key] = ttl


class RetryPolicy:
    """Exponential backoff for uploads."""

    def delay(self, attempt: int) -> float:
        \'\'\'Seconds to wait before retry number `attempt`.\'\'\'
        return 2 ** attempt
''',
    "src/upload.c": '''/*
 * Copyright (c) 2026 Example Corp. All rights reserved.
 */
#include <stdio.h>
#include "upload.h"
#include "../include/common.h"

/**
 * Upload one chunk, retrying with backoff.
 * Returns 0 on success.
 */
int upload_chunk(const char *buf, int len)
{
    return len > 0 ? 0 : -1;
}

// Compute the backoff delay in milliseconds.
static int backoff_ms(int attempt) { return 100 << attempt; }
''',
    "src/upload.h": "#ifndef UPLOAD_H\n#define UPLOAD_H\n/** Upload one chunk. */\nint upload_chunk(const char *buf, int len);\n#endif\n",
    "include/common.h": "#pragma once\n#include <zlib.h>\n#define COMMON_VERSION 3\n",
    "docs/with space/notes.md": "# Notes\n\nPaths with spaces must survive.\n",
    "tools/gen.cpp": "namespace detail { int hidden() { return 1; } }\nnamespace { int anon() { return 2; } }\nint Visible() { return 3; }\n",
    "assets/logo.c": b"\x00\x01binary\x00data",
}


def build(work_root: Path, repos_dir: Path, corpus: dict[str, Path]) -> None:
    """Create every bare repository at its first revision."""
    for name, source in corpus.items():
        work = work_root / "oss" / name
        _work_tree_from(source, work)
        _commit(work, f"import {name}")
        _publish(work, repos_dir / "oss" / f"{name}.git")

    demo = work_root / "acme" / "docs-demo"
    _work_tree_from(None, demo)
    for rel, text in DOCS_DEMO_FILES.items():
        write(demo / rel, text)
    write(demo / "big" / "huge.c", "int x;\n" + ("/* padding */\n" * 90000))
    _commit(demo, "first")
    git(demo, "checkout", "-q", "-b", "release/v2")
    write(demo / "src" / "v2only.c", "/** Only on the release branch. */\nint release_only(void) { return 2; }\n")
    _commit(demo, "release branch")
    git(demo, "checkout", "-q", "main")
    _publish(demo, repos_dir / "acme" / "docs-demo.git")

    secret = work_root / "acme" / "secret"
    _work_tree_from(None, secret)
    write(secret / "vault.c", "/** Decrypt the vault key. */\nint vault_unlock(int pin) { return pin == 1234; }\n"
                              "int DecodeFrame(int x) { return x; }\n")
    write(secret / "README.md", "# secret\n\nKey handling.\n")
    _commit(secret, "first")
    _publish(secret, repos_dir / "acme" / "secret.git")


def mutate(work_root: Path, repos_dir: Path) -> None:
    """A second revision: edits, a deletion, an addition and a rename, per repo."""
    zlib = work_root / "oss" / "zlib"
    with open(zlib / "adler32.c", "a", encoding="utf-8") as fh:
        fh.write("\n/* Checksum a whole buffer in one call. */\n"
                 "uLong ZEXPORT adler32_whole(const Bytef *buf, uInt len) { return adler32(1L, buf, len); }\n")
    _commit(zlib, "extend adler32")
    _publish(zlib, repos_dir / "oss" / "zlib.git")

    lz4 = work_root / "oss" / "lz4"
    victims = sorted(p for p in (lz4 / "programs").glob("*.c"))[:1]
    for v in victims:
        v.unlink()
    write(lz4 / "lib" / "lz4_extra.h", "#include \"lz4.h\"\n/** Extra helper. */\nint LZ4_extra(void);\n")
    _commit(lz4, "remove a program, add a header")
    _publish(lz4, repos_dir / "oss" / "lz4.git")

    demo = work_root / "acme" / "docs-demo"
    git(demo, "checkout", "-q", "main")
    write(demo / "src" / "cache.py", DOCS_DEMO_FILES["src/cache.py"] +
          '\n\ndef flush_all(store):\n    """Drop everything."""\n    store.clear()\n')
    git(demo, "mv", "tools/gen.cpp", "tools/generate.cpp")
    _commit(demo, "second")
    _publish(demo, repos_dir / "acme" / "docs-demo.git")
