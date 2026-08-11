-- Phase 4, the semantic layer over PRIVATE code.
--
-- What is embedded is deliberately narrow: a symbol's name, kind, signature
-- and path -- never a function body. A C++ body embeds mostly to "generic C++
-- control flow" and floods the space with near-duplicates, while the
-- signature plus path carries the intent somebody is actually searching for.
-- That is ~70-90k vectors for a corpus whose bodies would be ~600k.
--
-- The vector tables themselves are NOT created here. `vec0` is a virtual
-- table from the sqlite-vec extension, which is loaded per-connection, so a
-- migration runner without it would fail on this file. They are created
-- alongside, at first use, exactly as the pack format does.
--
-- repo_id is denormalised from files rather than joined through them. Every
-- read of this table is ACL-filtered, and putting the filter column on the
-- row being filtered keeps that a single indexed predicate instead of a join
-- the planner might reorder.

CREATE TABLE symbol_embeddings (
  symbol_id   INTEGER PRIMARY KEY REFERENCES symbols(id) ON DELETE CASCADE,
  repo_id     INTEGER NOT NULL REFERENCES repos(id) ON DELETE CASCADE,
  embed_text  TEXT NOT NULL,
  model       TEXT NOT NULL,
  dim         INTEGER NOT NULL,
  updated_at  TEXT NOT NULL DEFAULT (datetime('now'))
);

-- The ACL predicate, and the delete path when a repo goes away.
CREATE INDEX idx_symbol_embeddings_repo ON symbol_embeddings(repo_id);

-- A model change invalidates every vector: cosine distance between two
-- different embedding spaces is meaningless, not merely inaccurate. Indexed
-- so the rebuild pass can find stale rows without a full scan.
CREATE INDEX idx_symbol_embeddings_model ON symbol_embeddings(model, dim);
