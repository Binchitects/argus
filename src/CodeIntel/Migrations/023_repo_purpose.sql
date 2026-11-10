-- What each repository is for, in a line: from its README, else its manifest's description, else what its code is
-- (written again with the graph after each pass), or as an admin or its owners put it (never written over). Kept
-- by GitLab id, so it outlives its index rows.
CREATE TABLE IF NOT EXISTS repo_purpose (
  gitlab_id  INTEGER PRIMARY KEY,
  purpose    TEXT    NOT NULL,
  source     TEXT    NOT NULL,
  updated_at INTEGER NOT NULL
);
