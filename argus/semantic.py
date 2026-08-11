"""Phase 4 -- selective embeddings over this organisation's own code.

**What is embedded is the point.** A symbol's name, kind, signature, scope and
path -- never a function body. A C++ body embeds mostly to "generic C++
control flow"; two unrelated functions that both loop over a vector and check
a null land next to each other, and the space fills with near-duplicates that
crowd out real answers. The signature plus the path is what carries the intent
somebody is searching for. For a corpus whose bodies would be ~600k vectors,
this is ~70-90k.

Public symbols only, for the same reason a header is more useful than a
translation unit: a file-local helper named `init` is noise in a semantic
index, and there are hundreds of them.

The vectors are stored exactly as the packs store theirs -- binary-quantised
for a coarse scan, int8 for rescoring -- because the tradeoff is identical and
the code is already measured. float32 would be 3072 bytes per symbol against
96 for the coarse pass.

Nothing here reads or enforces the ACL. Searching lives in
``store/queries.py``, where every public function is required by a reflection
test to take ``allowed_repo_ids`` first. Putting a search entry point here
would be a way to add one that the test never sees.
"""

from __future__ import annotations

import sqlite3
from typing import Callable, Iterator, Sequence

from . import embed as embed_module
from .packs.format import _load_vec_extension
from .packs.quantize import to_bits, to_int8

EmbedFn = Callable[[list[str]], list[list[float]]]

#: How many symbols to accumulate before calling the embedder. Matches the
#: pack builder: bounds peak memory without making a request per symbol.
EMBED_FLUSH = 256

_CREATE_VEC_BIN = f"""
CREATE VIRTUAL TABLE IF NOT EXISTS vec_symbols_bin USING vec0(
  symbol_id INTEGER PRIMARY KEY, embedding bit[{embed_module.EMBED_DIM}]
)
"""

_CREATE_VEC_I8 = f"""
CREATE VIRTUAL TABLE IF NOT EXISTS vec_symbols_i8 USING vec0(
  symbol_id INTEGER PRIMARY KEY, embedding int8[{embed_module.EMBED_DIM}]
)
"""

#: Symbols worth embedding. `is_public` keeps file-local helpers out, and a
#: signature is required because name alone ("Init", "Run") carries almost no
#: retrievable intent -- it is the argument and return types that distinguish
#: forty functions called Init.
_CANDIDATES = """
SELECT s.id, s.repo_id, s.name, s.kind, s.signature, s.scope, f.path
  FROM symbols s
  JOIN files f ON f.id = s.file_id
 WHERE s.is_public = 1
   AND s.signature IS NOT NULL AND s.signature <> ''
   AND NOT EXISTS (
       SELECT 1 FROM symbol_embeddings e
        WHERE e.symbol_id = s.id AND e.model = ? AND e.dim = ?
   )
"""


def ensure_vec_tables(conn: sqlite3.Connection) -> None:
    """Create the vector tables if absent. Safe to call repeatedly.

    Not done in the migration because ``vec0`` comes from an extension loaded
    per-connection: a migration runner without sqlite-vec would fail on the
    file and leave the schema half-applied.
    """
    _load_vec_extension(conn)
    conn.execute(_CREATE_VEC_BIN)
    conn.execute(_CREATE_VEC_I8)


def embed_text_for(name: str, kind: str, signature: str, scope: str,
                   path: str) -> str:
    """The text that stands in for a symbol.

    Path included because it carries domain vocabulary the signature does not:
    ``media/decode/h265.c`` tells a search for "video decoding" far more than
    ``static int Parse(Ctx*, Buf*)`` ever will.
    """
    parts = [f"{kind} {name}".strip()]
    if scope:
        parts.append(f"in {scope}")
    if signature:
        parts.append(signature)
    if path:
        parts.append(path.replace("/", " ").replace("_", " "))
    return " -- ".join(p for p in parts if p)


def _pending(conn: sqlite3.Connection, model: str, dim: int,
             limit: int | None) -> list[tuple]:
    sql = _CANDIDATES + (" LIMIT ?" if limit else "")
    args = [model, dim] + ([limit] if limit else [])
    return conn.execute(sql, args).fetchall()


def build_symbol_embeddings(
    conn: sqlite3.Connection, *, embed_fn: EmbedFn | None = None,
    limit: int | None = None, progress: Callable[[int, int], None] | None = None,
) -> int:
    """Embed public symbols that have no current vector. Returns how many.

    Incremental by construction: the candidate query excludes anything already
    embedded with this model and dimension, so an interrupted run resumes and
    a rerun after indexing new code only does the new work. A model change
    makes every existing row stale, which is correct -- distances between two
    different embedding spaces are meaningless rather than merely imprecise.
    """
    embed_fn = embed_fn or embed_module.embed_batch
    model, dim = embed_module.EMBED_MODEL, embed_module.EMBED_DIM

    ensure_vec_tables(conn)
    rows = _pending(conn, model, dim, limit)
    if not rows:
        return 0

    done = 0
    for batch in _batched(rows, EMBED_FLUSH):
        texts = [embed_text_for(r[2], r[3], r[4], r[5], r[6]) for r in batch]
        vectors = embed_fn(texts)
        if len(vectors) != len(batch):
            raise ValueError(
                f"embedder returned {len(vectors)} vectors for "
                f"{len(batch)} inputs"
            )
        for (symbol_id, repo_id, *_), text, vector in zip(batch, texts, vectors):
            conn.execute(
                "INSERT OR REPLACE INTO symbol_embeddings"
                " (symbol_id, repo_id, embed_text, model, dim)"
                " VALUES (?, ?, ?, ?, ?)",
                (symbol_id, repo_id, text, model, dim),
            )
            # DELETE first: vec0 has no upsert, and a re-embedded symbol must
            # not end up with two vectors, which would let one symbol occupy
            # two slots of a KNN result.
            conn.execute("DELETE FROM vec_symbols_bin WHERE symbol_id = ?",
                         (symbol_id,))
            conn.execute("DELETE FROM vec_symbols_i8 WHERE symbol_id = ?",
                         (symbol_id,))
            conn.execute(
                "INSERT INTO vec_symbols_bin (symbol_id, embedding)"
                " VALUES (?, vec_bit(?))", (symbol_id, to_bits(vector)))
            conn.execute(
                "INSERT INTO vec_symbols_i8 (symbol_id, embedding)"
                " VALUES (?, vec_int8(?))", (symbol_id, to_int8(vector)))
        conn.commit()
        done += len(batch)
        if progress:
            progress(done, len(rows))
    return done


def _batched(rows: Sequence[tuple], size: int) -> Iterator[Sequence[tuple]]:
    for start in range(0, len(rows), size):
        yield rows[start:start + size]


def stale_count(conn: sqlite3.Connection) -> int:
    """Symbols embedded under a different model or dimension.

    Reported rather than silently rebuilt: re-embedding a large corpus is
    hours of CPU, and it should be a decision rather than a surprise.
    """
    return conn.execute(
        "SELECT count(*) FROM symbol_embeddings WHERE model <> ? OR dim <> ?",
        (embed_module.EMBED_MODEL, embed_module.EMBED_DIM),
    ).fetchone()[0]
