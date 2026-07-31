-- Developer token -> GitLab project membership, cached. The token itself is
-- never stored: token_hash is SHA-256 of the token.
CREATE TABLE IF NOT EXISTS acl_cache (
  token_hash    TEXT PRIMARY KEY,
  user_id       INTEGER NOT NULL,
  username      TEXT    NOT NULL,
  repo_ids_json TEXT    NOT NULL,
  fetched_at    INTEGER NOT NULL
);

-- One row per tool call. At 2-5 developers this costs nothing and answers
-- "what did the assistant show them" after the fact.
CREATE TABLE IF NOT EXISTS audit (
  id            INTEGER PRIMARY KEY,
  ts            INTEGER NOT NULL,
  user_id       INTEGER,
  username      TEXT,
  tool          TEXT    NOT NULL,
  args_json     TEXT    NOT NULL,
  repo_ids_json TEXT
);
CREATE INDEX IF NOT EXISTS idx_audit_ts ON audit(ts);
