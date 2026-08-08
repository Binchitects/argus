"""A durable cache of embedder output, so an interrupted build resumes cheaply.

A pack build is dominated by embedding: measured on this hardware, ~53 chunks
per second on CPU Ollama, which puts the win32 composite past ten hours. A
build that is interrupted loses all of it, because `build_pack` deletes its
half-written pack on any exception -- correctly, since a truncated pack is
indistinguishable from a complete one until something queries it.

This cache is the partial credit that deletion throws away. It is a *sidecar*:
never the pack, never read at query time, and deliberately not removed when a
build fails. Re-running after an interruption re-chunks (cheap) and re-embeds
only what it had not reached (the expensive part).

**What is stored is the quantized pair, not the float vector.** `to_bits` and
`to_int8` are exactly what the pack inserts, they are deterministic, and they
are 864 bytes against 3,072 for float32 -- on a 700,000-chunk corpus that is
the difference between a 0.6 GB sidecar and a 2.1 GB one. The cost is that
changing the quantization invalidates the cache, which is why the key carries
a format version alongside the embedding model.

**The key is the embed text, not the chunk id.** Chunk ids are assigned per
build and shift the moment a document is added upstream; the text is what the
embedder actually saw. That also lets two packs sharing a corpus -- `wdk` the
composite and `wdk-docs` alone -- reuse each other's work.
"""

from __future__ import annotations

import hashlib
import sqlite3
from pathlib import Path

#: Bump when to_bits/to_int8 change shape. Entries keyed with an older version
#: simply never match, so a stale cache degrades to a slow build rather than a
#: wrong pack.
CACHE_FORMAT = 1

_SCHEMA = """
CREATE TABLE IF NOT EXISTS embeddings (
  key  TEXT PRIMARY KEY,
  bits BLOB NOT NULL,
  i8   BLOB NOT NULL
);
"""


def cache_key(text: str, model: str, dim: int) -> str:
    """Identity of one embedder result.

    The model and dimension are in the key because the same text embedded by a
    different model is a different vector. Without them, switching models would
    silently reuse the old one's output and produce a pack whose vectors do not
    match its own recorded `embedding_model`.
    """
    digest = hashlib.sha256(text.encode("utf-8")).hexdigest()
    return f"{CACHE_FORMAT}:{model}:{dim}:{digest}"


class EmbeddingCache:
    """Key -> (bits, int8). Absent, unreadable or corrupt means "no cache"."""

    def __init__(self, path: Path | str | None):
        self.path = Path(path) if path is not None else None
        self._conn: sqlite3.Connection | None = None
        self.hits = 0
        self.misses = 0
        if self.path is None:
            return
        try:
            self.path.parent.mkdir(parents=True, exist_ok=True)
            conn = sqlite3.connect(self.path)
            conn.execute("PRAGMA journal_mode=WAL")
            conn.executescript(_SCHEMA)
            conn.commit()
            self._conn = conn
        except (sqlite3.Error, OSError):
            # A cache is an optimisation. If it cannot be opened -- read-only
            # disk, corrupt file, another process holding it, a parent path
            # that is a file -- the build must still run, just slowly. Never
            # fail a build over a cache.
            #
            # OSError matters as much as sqlite3.Error and was missing at
            # first: mkdir() on a parent that is a regular file raises
            # FileExistsError, which is an OSError, so an unusable cache
            # location would have taken the whole build down with it.
            self._conn = None

    @property
    def enabled(self) -> bool:
        return self._conn is not None

    def get(self, key: str) -> tuple[bytes, bytes] | None:
        if self._conn is None:
            return None
        try:
            row = self._conn.execute(
                "SELECT bits, i8 FROM embeddings WHERE key = ?", (key,)
            ).fetchone()
        except sqlite3.Error:
            return None
        if row is None:
            self.misses += 1
            return None
        self.hits += 1
        return row[0], row[1]

    def put_many(self, rows: list[tuple[str, bytes, bytes]]) -> None:
        if self._conn is None or not rows:
            return
        try:
            self._conn.executemany(
                "INSERT OR REPLACE INTO embeddings (key, bits, i8) "
                "VALUES (?, ?, ?)", rows,
            )
            # Committed per flush, not at the end. The whole point is to
            # survive a kill; work held in an uncommitted transaction when the
            # process dies is work paid for and lost.
            self._conn.commit()
        except sqlite3.Error:
            pass

    def close(self) -> None:
        if self._conn is not None:
            try:
                self._conn.close()
            finally:
                self._conn = None

    def __enter__(self) -> "EmbeddingCache":
        return self

    def __exit__(self, *exc: object) -> None:
        self.close()
