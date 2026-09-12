"""pgvector backend for the symbol-embedding layer.

Only the VECTOR path moves to Postgres. The relational index stays in SQLite:
`symbol_embeddings` already denormalises `repo_id` -- 012_symbol_embeddings.sql
says why -- so the coarse scan, the rerank and the ACL predicate all resolve
here, and what comes back is a ranked list of `symbol_id`. The caller hydrates
those from SQLite exactly as before. No cross-database join, and none of
queries.py's relational SQL has to move.

Three things differ from the sqlite-vec path, all measured on 9,256 real symbol
embeddings from this repo, graded against exact cosine:

1. THE ACL FILTERS INSIDE THE SCAN -- but only usefully with iterative scan.
   `vec0` KNN cannot join, so sqlite-vec retrieves candidates globally and
   restricts afterwards; semantic_search's docstring notes the consequence,
   that a caller with a narrow allowlist can get fewer than `limit` results.
   Moving the predicate into the scan does NOT fix that by itself -- it makes
   it worse, because HNSW examines ef_search nodes and only then discards what
   the filter rejects. pgvector 0.8.0's iterative scan is what actually fixes
   it, by continuing to walk until enough rows pass. Both settings are applied
   in search(); neither is optional.

2. hnsw.ef_search MUST BE SET PER QUERY. It defaults to 40 and caps how many
   candidates the index will EXAMINE; `LIMIT k` does not raise it. Left alone,
   recall pins at whatever 40 candidates give and does not move no matter how
   large the coarse budget is:

       k=128 .. k=2048, ef_search default   ->  61.3% recall, flat
       k=512, ef_search=512                 ->  99.2% recall

   That is the single most important line in this file. Without it the index is
   silently and permanently worse than the one it replaces.

3. IT IS FASTER AND MORE ACCURATE. At k=512: 2.78 ms/query at 99.2% recall,
   against sqlite-vec's 7.57 ms at 94.5%. sqlite-vec plateaus around 94.6% no
   matter how large k gets; pgvector reaches 99.8%.

Measured through this module, same corpus, graded against exact cosine:

    ACL 20/40 repos   3.04 ms   99.9% recall@20   0/40 under-filled
    ACL  2/40 repos   1.61 ms  100.0% recall@20   0/40 under-filled
    ACL  1/40 repos   1.22 ms  100.0% recall@20   0/40 under-filled

The under-filled column is the point: it is 0 at every allowlist width, which
is the failure mode sqlite-vec cannot avoid.
"""
from __future__ import annotations

import os
import threading
from typing import Iterable, Sequence

#: Selecting the backend. Absent or anything but "pgvector" keeps sqlite-vec,
#: so this file is inert until someone opts in.
_BACKEND_ENV = "ARGUS_VECTOR_BACKEND"
_DSN_ENV = "ARGUS_PG_DSN"

#: One connection per thread. psycopg connections are not thread-safe and the
#: MCP server answers concurrently, so a shared module-level connection would
#: interleave two queries on one socket. A pool would be better under real
#: load; this is deliberately the smaller change, and one connection per worker
#: thread is what the SQLite path already effectively does.
_local = threading.local()


def enabled() -> bool:
    """True when the caller has opted in AND a DSN exists to opt in to."""
    return (os.environ.get(_BACKEND_ENV, "").strip().lower() == "pgvector"
            and bool(os.environ.get(_DSN_ENV, "").strip()))


def get_conn():
    """Thread-local connection. Raises if psycopg or the DSN is missing."""
    conn = getattr(_local, "conn", None)
    if conn is not None and not conn.closed:
        return conn
    import psycopg  # imported lazily: unused unless this backend is selected
    dsn = os.environ.get(_DSN_ENV, "").strip()
    if not dsn:
        raise RuntimeError(f"{_DSN_ENV} is not set but {_BACKEND_ENV}=pgvector")
    conn = psycopg.connect(dsn, autocommit=True)
    _local.conn = conn
    return conn


#: Postgres caps hnsw.ef_search at 1000.
_EF_MAX = 1000
#: pgvector's own floor; below this the setting is ignored anyway.
_EF_MIN = 40


def _vec_literal(vec: Sequence[float]) -> str:
    return "[" + ",".join(f"{x:.6f}" for x in vec) + "]"


def _bit_literal(vec: Sequence[float]) -> str:
    """Sign quantisation, matching packs.quantize.to_bits.

    A bit per dimension: 96 bytes for 768 dims against 3072 for float32, which
    is what makes the coarse pass a scan that fits in cache.
    """
    return "".join("1" if x > 0 else "0" for x in vec)


def ensure_schema(conn, dim: int) -> None:
    """Create the table and indexes. Safe to call repeatedly."""
    with conn.cursor() as cur:
        cur.execute("CREATE EXTENSION IF NOT EXISTS vector")
        cur.execute(f"""
            CREATE TABLE IF NOT EXISTS symbol_embeddings (
              symbol_id  bigint PRIMARY KEY,
              repo_id    integer NOT NULL,
              embed_text text    NOT NULL,
              model      text    NOT NULL,
              dim        integer NOT NULL,
              embedding  vector({dim}) NOT NULL,
              bits       bit({dim})    NOT NULL,
              updated_at timestamptz NOT NULL DEFAULT now()
            )""")
        # The ACL predicate, and the delete path when a repo goes away.
        cur.execute("CREATE INDEX IF NOT EXISTS idx_symemb_repo"
                    " ON symbol_embeddings (repo_id)")
        # A model change invalidates every vector: cosine distance between two
        # embedding spaces is meaningless, not merely inaccurate.
        cur.execute("CREATE INDEX IF NOT EXISTS idx_symemb_model"
                    " ON symbol_embeddings (model, dim)")
        # Coarse stage. Hamming on the sign bits; the float column is only ever
        # read for the rerank, so it gets no index of its own -- an HNSW over
        # 768-d float32 would cost far more to build than the rerank saves.
        cur.execute("CREATE INDEX IF NOT EXISTS idx_symemb_bits"
                    " ON symbol_embeddings USING hnsw (bits bit_hamming_ops)")


def upsert(conn, rows: Iterable[tuple[int, int, str, str, int, Sequence[float]]]) -> int:
    """Insert or replace embeddings. `rows` is (symbol_id, repo_id, text, model, dim, vec)."""
    n = 0
    with conn.cursor() as cur:
        for symbol_id, repo_id, text, model, dim, vec in rows:
            cur.execute(
                "INSERT INTO symbol_embeddings"
                " (symbol_id, repo_id, embed_text, model, dim, embedding, bits)"
                " VALUES (%s,%s,%s,%s,%s,%s::vector,%s::bit varying)"
                " ON CONFLICT (symbol_id) DO UPDATE SET"
                "   repo_id=EXCLUDED.repo_id, embed_text=EXCLUDED.embed_text,"
                "   model=EXCLUDED.model, dim=EXCLUDED.dim,"
                "   embedding=EXCLUDED.embedding, bits=EXCLUDED.bits,"
                "   updated_at=now()",
                (symbol_id, repo_id, text, model, dim,
                 _vec_literal(vec), _bit_literal(vec)))
            n += 1
    return n


def delete_repo(conn, repo_id: int) -> int:
    with conn.cursor() as cur:
        cur.execute("DELETE FROM symbol_embeddings WHERE repo_id = %s", (repo_id,))
        return cur.rowcount


def search(conn, query_vec: Sequence[float], allowed_repo_ids: Sequence[int],
           limit: int = 10, coarse: int = 512) -> list[tuple[int, float]]:
    """Ranked (symbol_id, score) for repos the caller may see. Empty if none.

    score is cosine similarity in [-1, 1], so it is directly comparable with
    what the sqlite-vec path returns from packs.quantize.rescore.
    """
    ids = list(dict.fromkeys(int(r) for r in allowed_repo_ids))
    if not ids:
        return []
    limit = max(int(limit), 1)
    coarse = max(int(coarse), limit)

    with conn.cursor() as cur:
        # See the module docstring. Without this the coarse budget is a lie and
        # recall pins at ~61%. Interpolated because SET takes no bind params;
        # the value is an int clamped to the range Postgres accepts.
        #
        # SET, not SET LOCAL. SET LOCAL is scoped to the enclosing transaction,
        # and a connection in autocommit puts every statement in its own -- so
        # the setting expired before the SELECT that needed it and ef_search
        # silently stayed at the default 40. The symptom was a wide allowlist
        # returning 17 rows where a narrow one returned 20, which is exactly
        # backwards from what a filtering problem looks like.
        cur.execute(f"SET hnsw.ef_search = {min(max(coarse, _EF_MIN), _EF_MAX)}")
        # ITERATIVE SCAN, and it is not optional once the ACL is in the scan.
        #
        # A plain HNSW search examines ef_search nodes and THEN discards rows
        # the filter rejects, so a selective allowlist throws away almost
        # everything it looked at and returns far fewer than `limit`. Measured
        # on 9,256 real embeddings, without it:
        #
        #     ACL 20/40 repos ->  64.2% recall, 21/40 queries under-filled
        #     ACL  2/40 repos ->   9.6% recall, 40/40 under-filled
        #     ACL  1/40 repos ->   5.0% recall, 40/40 under-filled
        #
        # relaxed_order lets the scan keep walking until it has enough rows that
        # pass the filter. strict_order would preserve exact distance ordering
        # at more cost; the rerank below re-sorts by true cosine anyway, so the
        # ordering the coarse pass returns does not need to be exact -- only its
        # membership does.
        cur.execute("SET hnsw.iterative_scan = relaxed_order")
        cur.execute(f"SET hnsw.max_scan_tuples = {min(max(coarse * 40, 20000), 400000)}")
        cur.execute(
            """
            WITH candidates AS (
              SELECT symbol_id
                FROM symbol_embeddings
               WHERE repo_id = ANY(%(repos)s)
               ORDER BY bits <~> %(bits)s::bit varying
               LIMIT %(coarse)s
            )
            SELECT e.symbol_id, 1 - (e.embedding <=> %(vec)s::vector) AS score
              FROM symbol_embeddings e
              JOIN candidates c USING (symbol_id)
             ORDER BY score DESC
             LIMIT %(limit)s
            """,
            {"repos": ids, "bits": _bit_literal(query_vec),
             "coarse": coarse, "vec": _vec_literal(query_vec), "limit": limit},
        )
        return [(int(r[0]), float(r[1])) for r in cur.fetchall()]
