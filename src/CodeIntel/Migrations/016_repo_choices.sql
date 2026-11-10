-- Which repositories are indexed, and which branches besides the default.
--
-- Until now every repository the service account could see was indexed, at its
-- default branch plus the operator's global branch patterns. An admin now sees
-- what GitLab lists (refreshed by every index pass and on demand), and chooses:
-- a repository in or out, and for each, its own branches. A repository GitLab
-- lists for the first time is in or out as `index.new_repos` in argus_meta says
-- (in, when unset: what indexing did before).
CREATE TABLE IF NOT EXISTS repo_choices (
  gitlab_id           INTEGER PRIMARY KEY,
  path_with_namespace TEXT    NOT NULL,
  default_branch      TEXT    NOT NULL,
  http_url            TEXT    NOT NULL,
  included            INTEGER NOT NULL DEFAULT 1,
  -- Branches indexed besides the default one: names or globs (release/*), one per line.
  branches            TEXT    NOT NULL DEFAULT '',
  seen_at             INTEGER,
  changed_at          INTEGER
);

-- What is indexed today stays indexed: one row per repository already in the index.
INSERT OR IGNORE INTO repo_choices (gitlab_id, path_with_namespace, default_branch, http_url, included, seen_at)
  SELECT gitlab_id, path_with_namespace, default_branch, http_url, 1, MAX(last_run_at) FROM repos GROUP BY gitlab_id;

-- The commit an index row is at, in words: its subject line and when it was committed.
ALTER TABLE repos ADD COLUMN last_indexed_message   TEXT;
ALTER TABLE repos ADD COLUMN last_indexed_commit_at INTEGER;
