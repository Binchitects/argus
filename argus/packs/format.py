"""The knowledge pack file format.

A pack is one SQLite file, self-describing (``pack_meta``) and
self-verifying (``require_compatible``). See
``docs/superpowers/specs/2026-08-02-knowledge-packs-design.md``, "The pack
format", for the schema this module implements verbatim.

Packs are a second, public corpus, entirely separate from the private index:
nothing here may import ``argus.store.queries`` or open ``index.db``, and
this module never touches ``argus/store/migrations`` -- a pack version is
tracked by its own ``pack_meta.pack_schema_version``, not a migration.
"""

from __future__ import annotations

import sqlite3

from ..embed import EMBED_DIM
from pathlib import Path

import sqlite_vec

# Bumped whenever the schema below changes in a way that makes an existing
# pack file unreadable by this code (new required table/column, changed
# vector layout, etc.). Stored in every pack's own pack_meta so a mismatch is
# caught by require_compatible rather than surfacing as a confusing SQL error
# deep in a query.
PACK_SCHEMA_VERSION = 1

# The non-vector schema. Kept as one script; CREATE VIRTUAL TABLE ... vec0
# statements are issued separately below because they require the sqlite-vec
# extension to already be loaded on the connection, which create_pack and
# open_pack both do before touching the schema.
_SCHEMA = """
CREATE TABLE pack_meta (
  key TEXT PRIMARY KEY, value TEXT NOT NULL
);

CREATE TABLE docs (
  id INTEGER PRIMARY KEY,
  path TEXT NOT NULL UNIQUE,
  title TEXT,
  url TEXT,
  lang TEXT,
  content BLOB NOT NULL,
  content_len INTEGER NOT NULL
);

CREATE TABLE chunks (
  id INTEGER PRIMARY KEY,
  doc_id INTEGER NOT NULL REFERENCES docs(id),
  heading_path TEXT,
  anchor TEXT,
  start_line INTEGER,
  text BLOB NOT NULL
);

CREATE TABLE api_symbols (
  id INTEGER PRIMARY KEY,
  name TEXT NOT NULL,
  kind TEXT,
  namespace TEXT,
  doc_id INTEGER REFERENCES docs(id),
  anchor TEXT,
  signature TEXT
);
CREATE INDEX idx_api_name ON api_symbols(name);

-- Contentless: docs.content is zstd-compressed, so FTS5 cannot proxy into
-- it the way the private index proxies into plaintext external content.
-- Terms live in the FTS index; snippets come from decompressing the row.
CREATE VIRTUAL TABLE docs_fts USING fts5(
  title, body, content='', tokenize='unicode61 remove_diacritics 2'
);
"""

# Binary-quantized: EMBED_DIM bits (96 bytes at 768). The coarse Hamming pass.
#
# The dimension comes from the configured model rather than a literal, so a
# deployment can build with a different embedder. It is baked into the pack at
# creation, so an existing pack keeps its own declaration whatever the current
# setting is -- and read_meta records the model and dimension so
# require_compatible can refuse a mismatch instead of ranking vectors from two
# different spaces against each other.
_CREATE_VEC_BIN = f"""
CREATE VIRTUAL TABLE vec_bin USING vec0(
  chunk_id INTEGER PRIMARY KEY, embedding bit[{EMBED_DIM}]
)
"""

# int8: EMBED_DIM bytes/chunk. Read only to rescore vec_bin's candidates.
_CREATE_VEC_I8 = f"""
CREATE VIRTUAL TABLE vec_i8 USING vec0(
  chunk_id INTEGER PRIMARY KEY, embedding int8[{EMBED_DIM}]
)
"""


def _load_vec_extension(conn: sqlite3.Connection) -> None:
    """Load sqlite-vec on this connection. Extension loading is per-connection.

    Verified to work on a read-only, ``immutable=1`` connection as well as a
    writable one -- loading an extension is a property of the connection
    object, not the file, so the immutable/read-only pragmas (which govern
    locking and change-counter checks on the *file*) do not interact with it.
    """
    conn.enable_load_extension(True)
    sqlite_vec.load(conn)
    conn.enable_load_extension(False)


def create_pack(path: Path | str) -> sqlite3.Connection:
    """Create a new, empty pack file at ``path`` with the full schema applied.

    Returns a writable connection (the caller populates it via write_meta and
    ordinary INSERTs, then closes it). Overwrites any existing file at path,
    since a half-built pack is not something to append to.
    """
    path = Path(path)
    if path.exists():
        path.unlink()
    path.parent.mkdir(parents=True, exist_ok=True)

    conn = sqlite3.connect(path)
    conn.row_factory = sqlite3.Row
    _load_vec_extension(conn)
    conn.executescript(_SCHEMA)
    conn.execute(_CREATE_VEC_BIN)
    conn.execute(_CREATE_VEC_I8)
    conn.execute(
        "INSERT INTO pack_meta (key, value) VALUES (?, ?)",
        ("pack_schema_version", str(PACK_SCHEMA_VERSION)),
    )
    conn.commit()
    return conn


def open_pack(path: Path | str) -> sqlite3.Connection:
    """Open an existing pack read-only and immutable.

    ``immutable=1`` tells SQLite the file will not change under it, which
    removes locking and change-counter checks -- a pack is frozen by
    definition, so this is a correctness statement (a pack must not appear to
    mutate under a reader) as much as a speed one. Do not drop it.
    """
    path = Path(path)
    uri = f"file:{path.as_posix()}?mode=ro&immutable=1"
    conn = sqlite3.connect(uri, uri=True)
    conn.row_factory = sqlite3.Row
    _load_vec_extension(conn)
    return conn


def write_meta(conn: sqlite3.Connection, **kv: object) -> None:
    """Write or overwrite pack_meta key/value pairs. Values are stringified."""
    conn.executemany(
        "INSERT INTO pack_meta (key, value) VALUES (?, ?) "
        "ON CONFLICT(key) DO UPDATE SET value = excluded.value",
        [(key, str(value)) for key, value in kv.items()],
    )
    conn.commit()


def read_meta(conn: sqlite3.Connection) -> dict[str, str]:
    """Read the whole pack_meta table as a dict. The first thing a consumer
    should read before trusting anything else in the pack."""
    rows = conn.execute("SELECT key, value FROM pack_meta").fetchall()
    return {row["key"]: row["value"] for row in rows}


class PackMismatch(Exception):
    """A pack cannot be served against this instance's embedding space.

    Vectors from a different embedding model or dimension produce
    plausible-looking, subtly wrong semantic results -- the failure mode that
    looks like it works. This is raised before any query touches vec_bin or
    vec_i8, and the message always names the offending value (the pack's
    actual model/dimension/schema version) rather than just saying
    "incompatible pack", because a mismatch a human cannot diagnose from the
    error is a support ticket.
    """


def require_compatible(meta: dict, *, model: str, dim: int) -> None:
    """Raise PackMismatch unless ``meta`` matches this instance's embedding
    model, embedding dimension, and pack schema version.

    Lexical (docs_fts) and API-symbol (api_symbols) search do not depend on
    the embedding space, so callers should catch PackMismatch and still serve
    those -- a mismatched pack degrades rather than dies. This function only
    decides whether semantic (vec_bin/vec_i8) search is safe to run.
    """
    pack_model = meta.get("embedding_model")
    if pack_model != model:
        raise PackMismatch(
            f"pack was built with embedding model {pack_model!r}, but this "
            f"instance serves {model!r} -- semantic search is disabled for "
            f"this pack"
        )

    pack_dim = meta.get("embedding_dim")
    if pack_dim is None or int(pack_dim) != dim:
        raise PackMismatch(
            f"pack embeddings are {pack_dim!r}-dimensional, but this "
            f"instance expects {dim} -- semantic search is disabled for "
            f"this pack"
        )

    pack_schema = meta.get("pack_schema_version")
    if pack_schema is None or int(pack_schema) != PACK_SCHEMA_VERSION:
        raise PackMismatch(
            f"pack schema version {pack_schema!r} is not compatible with "
            f"this instance's pack format version {PACK_SCHEMA_VERSION} -- "
            f"the pack must be rebuilt"
        )
