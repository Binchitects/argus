-- A permanently unreadable path (ACL denial, >260-char Windows path, AV
-- quarantine) is re-enqueued by every pass forever and appends a fresh
-- index_errors row each time: the queue never empties and index_errors grows
-- without bound. index_queue holds one row per repo with no path column, so
-- the attempt count needs its own table to survive across passes.
CREATE TABLE IF NOT EXISTS retry_attempts (
  repo_id  INTEGER NOT NULL REFERENCES repos(id) ON DELETE CASCADE,
  path     TEXT    NOT NULL,
  attempts INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (repo_id, path)
);
