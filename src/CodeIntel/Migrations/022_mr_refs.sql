-- Merge requests read from GitLab, and code at any commit.
--
-- A merge request's head is fetched into the project's mirror only when a
-- tool needs its files (file_at, grep_at): one ref,
-- refs/merge-requests/<iid>/head, kept as refs/arena/mr/<iid>. Each one
-- fetched is remembered here, with when it was last read, so it is dropped
-- again when its merge request is merged or closed, or after 30 days unused.
CREATE TABLE IF NOT EXISTS mr_refs (
  gitlab_id  INTEGER NOT NULL,
  iid        INTEGER NOT NULL,
  sha        TEXT    NOT NULL,
  fetched_at INTEGER NOT NULL,
  used_at    INTEGER NOT NULL,
  PRIMARY KEY (gitlab_id, iid)
);

-- A GitLab token's access, kept by the token's hash: besides the index rows it
-- may read, every GitLab project it may read, indexed or not, so a project left
-- out of the index can still be reviewed. Null in rows kept before; those are
-- read again within ten minutes.
ALTER TABLE acl_cache ADD COLUMN gitlab_ids_json TEXT;
