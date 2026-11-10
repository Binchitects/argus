-- A repository row's id is never given to another repository.
--
-- Without AUTOINCREMENT, SQLite gives a new row the largest id plus one, so the
-- row of a repository deleted last is given to the next one indexed. Anything
-- that kept that id (a cached allowlist, an answer, an audit line) would then
-- point at a different repository. Access is carried as GitLab project ids
-- (Acl), and from now on a row id is also never reused.
--
-- SQLite cannot add AUTOINCREMENT to a table, so the table is rebuilt with every
-- id copied as it is (as 011 did). sqlite_sequence starts at the largest id
-- there is. PRAGMA foreign_keys=OFF is load-bearing: the other tables reference
-- repos(id) ON DELETE CASCADE, and dropping repos with enforcement on would
-- empty the index.
PRAGMA foreign_keys=OFF;

CREATE TABLE repos_new (
  id                      INTEGER PRIMARY KEY AUTOINCREMENT,
  gitlab_id               INTEGER NOT NULL,
  path_with_namespace     TEXT    NOT NULL,
  default_branch          TEXT    NOT NULL,
  branch                  TEXT    NOT NULL,
  http_url                TEXT    NOT NULL,
  last_indexed_sha        TEXT,
  last_indexed_at         INTEGER,
  last_run_timed_out      INTEGER NOT NULL DEFAULT 0,
  last_run_symbols_failed INTEGER NOT NULL DEFAULT 0,
  last_run_at             INTEGER,
  last_run_error          TEXT,
  last_indexed_message    TEXT,
  last_indexed_commit_at  INTEGER,
  UNIQUE (gitlab_id, branch)
);

INSERT INTO repos_new (id, gitlab_id, path_with_namespace, default_branch, branch, http_url,
                       last_indexed_sha, last_indexed_at, last_run_timed_out, last_run_symbols_failed,
                       last_run_at, last_run_error, last_indexed_message, last_indexed_commit_at)
SELECT id, gitlab_id, path_with_namespace, default_branch, branch, http_url,
       last_indexed_sha, last_indexed_at, last_run_timed_out, last_run_symbols_failed,
       last_run_at, last_run_error, last_indexed_message, last_indexed_commit_at
FROM repos;

DROP TABLE repos;
ALTER TABLE repos_new RENAME TO repos;

CREATE INDEX IF NOT EXISTS idx_repos_gitlab ON repos(gitlab_id);

-- The next id is past every id there is now (the copy above already set it to the largest copied).
INSERT INTO sqlite_sequence (name, seq)
SELECT 'repos', COALESCE(MAX(id), 0) FROM repos
WHERE NOT EXISTS (SELECT 1 FROM sqlite_sequence WHERE name = 'repos');

-- Cached allowlists made before this held row ids: they are asked of GitLab again.
DELETE FROM acl_cache;

PRAGMA foreign_keys=ON;
