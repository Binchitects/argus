# The pgvector backend for symbol embeddings

Argus stores symbol vectors in SQLite via `sqlite-vec`. This is an optional
Postgres backend for the same data, selected per deployment. It is **off by
default** and the SQLite path is unchanged.

## Why

Three reasons, in the order they matter.

**A documented correctness limitation goes away.** `semantic_search` notes that
`vec0` KNN cannot join, so candidates are retrieved globally and the ACL is
applied afterwards — and a caller whose allowlist is a small fraction of the
corpus can therefore receive fewer than `limit` results. Postgres puts the
predicate in the scan. Measured across allowlist widths, `under-filled` is 0 at
every one.

**It is faster and more accurate.** Same 9,256 real embeddings, both backends
graded against exact cosine in numpy:

| backend | k=512 | recall@20 |
|---|---|---|
| pgvector | 2.78 ms | **99.2%** |
| sqlite-vec | 7.57 ms | 94.5% |

sqlite-vec plateaus near 94.6% however large `k` grows; pgvector reaches 99.8%.

**It removes the reason the audit log needs a sidecar database.** `db.py`
records that every tool call appends an audit row, that in SQLite a writer
blocks other writers, and that with an indexing run in progress read throughput
collapsed **76.1 → 7.2 req/s** with p95 going **305 ms → 4,410 ms**. Postgres
MVCC has no such interaction. That workaround is not removed here, but this is
the change that makes removing it possible.

## Enabling it

```bash
ARGUS_VECTOR_BACKEND=pgvector
ARGUS_PG_DSN=postgresql://user:pass@postgres:5432/argus
```

Both are required; either missing leaves the SQLite path in force. The stack's
Postgres already ships the extension (`pgvector/pgvector:0.8.0-pg16`) and the
init script creates the `argus` database with `CREATE EXTENSION vector`.

Install the optional dependency if you are not using the shipped image:

```bash
pip install ".[pgvector]"
```

`argus/store/pgvector.py` imports `psycopg` lazily, so its absence costs nothing
on the default path.

## Three settings, none optional

Each of these cost a debugging cycle. They are applied inside `search()`; this
section exists so nobody removes them as noise.

**`hnsw.ef_search`** caps how many candidates the index will EXAMINE and
defaults to **40**. `LIMIT k` does not raise it. Left alone, recall pins at
61.3% and does not move from k=128 to k=2048 — a flat line across a 16x range,
which is what a silently capped index looks like rather than a slow one.

**`hnsw.iterative_scan`** lets the walk continue until enough rows pass the
filter. Without it a plain HNSW search examines `ef_search` nodes and only then
discards what the ACL rejects, so a selective allowlist throws away nearly
everything it looked at.

**`SET`, not `SET LOCAL`.** `SET LOCAL` is scoped to the enclosing transaction
and a connection in autocommit puts every statement in its own, so the settings
expired before the SELECT that needed them. The symptom was a **wide** allowlist
returning 17 rows where a **narrow** one returned 20 — exactly backwards from
what a filtering problem looks like.

## Measured: multi-repo, real embeddings

The full symbol corpus of this repository -- **75,616 real embeddings**, every
one produced through argus/embed.py's own path -- across 200 repos with
realistically uneven sizes (largest 5,508 symbols, median 218, smallest 115).
Graded against exact cosine in numpy.

| ACL width | rows visible | ms/query | recall@20 | under-filled |
|---|---|---|---|---|
| 1 repo | 181 | 1.27 | 100.0% | 0/40 |
| 5 repos | 1,116 | 4.66 | 99.9% | 0/40 |
| 25 repos | 14,906 | 11.70 | 98.1% | 0/40 |
| 100 repos | 46,340 | 6.67 | 98.1% | 0/40 |
| 200 repos | 75,616 | 7.72 | 97.8% | 0/40 |

Recall falls with corpus size and the honest number is the bottom row: **97.8%
at full scale**, not the 99.8% an earlier run on 30,463 of the same vectors
reported. Still well above sqlite-vec's ~94.6% ceiling, and sqlite-vec cannot
serve the narrow-ACL rows at all without under-filling.

Worst-case latency is at MODERATE selectivity, not at scale -- 25 of 200 repos
is the slowest at 11.70 ms while the whole corpus is 7.72 ms. The 500k run below
shows the same shape, so this is a property of filtered HNSW rather than an
artefact of one corpus. Size capacity for a mid-selectivity allowlist.

**ACL leakage on a 3-repo allowlist across 40 queries: 0 rows** from repos the
caller could not see.

## Measured: 500,000 symbols across 500 repos

Corpus expanded from the real embeddings (sigma 0.01 perturbation, renormalised)
so neighbourhood structure stays realistic. Repo sizes follow a long tail, as
real ones do. Load 127 s (3,935 rows/s), HNSW build 71 s, 2,277 MB on disk.

| ACL width | rows visible | ms/query | under-filled | leaked |
|---|---|---|---|---|
| 1 repo | 520 | 2.19 | 0/30 | 0 |
| 10 repos | 12,761 | **43.73** | 0/30 | 0 |
| 50 repos | 79,330 | 24.36 | 0/30 | 0 |
| 250 repos | 261,058 | 11.83 | 0/30 | 0 |
| 500 repos | 500,000 | 7.99 | 0/30 | 0 |

**Worst-case latency is at moderate selectivity, not at scale.** 10 of 500 repos
is the slowest case at 43.7 ms, while the whole corpus is 8.0 ms. That is the
iterative scan doing its job: when only ~2.5% of rows pass the ACL, the walk has
to go a long way to find `coarse` of them, whereas an unrestricted query is
satisfied immediately. Plan capacity around a mid-selectivity allowlist rather
than the largest one — the intuition that "more repos is slower" is backwards
here.

Completeness and isolation hold throughout: **0 under-filled** results and **0
rows leaked** from repos outside the allowlist, at every width.

## Scaling

Synthetic scale test, 768-d, HNSW over the bit column:

| | 80k | 500k |
|---|---|---|
| load (COPY) | 43.6 s | 271.8 s |
| HNSW build | 2.9 s | 80.5 s |
| storage | 362 MB | 2,259 MB |
| exact cosine + ACL | 89.4 ms | 254.3 ms |
| **two-stage** | **0.8 ms** | **1.5 ms** |

Two things to plan around:

**HNSW build is superlinear** — 6.25x the rows cost 27x the build time. At a few
million vectors an index rebuild is a maintenance window, so prefer incremental
maintenance over dropping and recreating.

**Storage is ~4.5 KB/vector**, dominated by the fp32 `vector(768)` column.
`halfvec(768)` would roughly halve it at negligible cost for a rerank stage,
and is the first thing to try if the table gets large.

Exact search is not an alternative at this size: 254 ms/query at 500k on a
corpus 6x the one this index is built for.

## What this does NOT change

Only the vector path moves. `symbol_embeddings` already denormalises `repo_id`
— `012_symbol_embeddings.sql` explains why — so the coarse pass, the rerank and
the ACL all resolve in Postgres and the result is a ranked list of `symbol_id`
that the caller hydrates from SQLite exactly as before. There is no
cross-database join, and none of `queries.py`'s relational SQL moved.

A failure in the Postgres path is logged and falls back to `sqlite-vec` rather
than propagating: the semantic index is an enhancement over the lexical one, and
an unreachable database should degrade search, not break the tool call.
