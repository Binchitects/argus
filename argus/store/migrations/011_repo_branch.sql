-- One row per (project, branch), so a repo can be indexed at several refs.
--
-- Real estates ship long-lived release branches alongside the trunk -- main
-- plus v1, v2, v3 -- and "which branch is this answer from?" is not a
-- question the index could previously even represent. `repos` held a single
-- `default_branch` and `gitlab_id` was UNIQUE, so a project could not appear
-- twice at all. A developer working on v2 got trunk answers with nothing
-- saying so, which is worse than no answer.
--
-- SQLite cannot drop the implicit index behind a UNIQUE column, so the table
-- is rebuilt. `id` values are copied verbatim: every other table references
-- repos(id), and preserving it keeps files, symbols, includes, repo_deps,
-- index_errors, index_queue and retry_attempts pointing at the same rows.
--
-- PRAGMA foreign_keys=OFF is load-bearing, not caution. Those references are
-- ON DELETE CASCADE, so `DROP TABLE repos` with enforcement on would delete
-- every file, symbol and include in the index -- the migration would silently
-- empty the database it was meant to upgrade.
PRAGMA foreign_keys=OFF;

CREATE TABLE repos_new (
  id                  INTEGER PRIMARY KEY,
  gitlab_id           INTEGER NOT NULL,
  path_with_namespace TEXT    NOT NULL,
  default_branch      TEXT    NOT NULL,
  -- The ref this row is indexed at. Existing rows were indexed at whatever
  -- GitLab called default, which is exactly what default_branch holds.
  branch              TEXT    NOT NULL,
  http_url            TEXT    NOT NULL,
  last_indexed_sha    TEXT,
  last_indexed_at     INTEGER,
  last_run_timed_out      INTEGER NOT NULL DEFAULT 0,
  last_run_symbols_failed INTEGER NOT NULL DEFAULT 0,
  last_run_at         INTEGER,
  last_run_error      TEXT,
  UNIQUE (gitlab_id, branch)
);

INSERT INTO repos_new (id, gitlab_id, path_with_namespace, default_branch,
                       branch, http_url, last_indexed_sha, last_indexed_at,
                       last_run_timed_out, last_run_symbols_failed,
                       last_run_at, last_run_error)
SELECT id, gitlab_id, path_with_namespace, default_branch,
       default_branch, http_url, last_indexed_sha, last_indexed_at,
       last_run_timed_out, last_run_symbols_failed,
       last_run_at, last_run_error
FROM repos;

DROP TABLE repos;
ALTER TABLE repos_new RENAME TO repos;

-- ACL maps a GitLab project to every row it owns, so this is looked up on
-- every authenticated request once the cache misses.
CREATE INDEX IF NOT EXISTS idx_repos_gitlab ON repos(gitlab_id);

PRAGMA foreign_keys=ON;
