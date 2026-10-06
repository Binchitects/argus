-- One repository, one row: a repository is its project in one GitLab, never
-- the token or the account that listed it.
--
-- repo_choices and repos were keyed by GitLab's project id alone, and that id
-- means something only inside one GitLab. Listing the same repositories again
-- from a GitLab set up anew (a test instance brought up again with a new
-- token, a server moved by export and import) gave every one of them a new
-- id, and each came back as a second row beside the first. The first was
-- never listed again: never updated, stale for good, an alert that never
-- clears, and still holding its whole index.
--
-- From now on each listing is matched to what is known (Choices.Record): by
-- id when it is the same project (GitLab's creation time for it, or the same
-- GitLab when that is not known), else by path, so a repository GitLab lists
-- under a new id moves to that id with its choices and its index. Here, once,
-- the copies that already exist are merged:
--
--   * per path (GitLab compares paths without case), the copy GitLab listed
--     last is the repository;
--   * the admin's latest choice among the copies (in or out, branches) is its
--     choice;
--   * per branch, the newest good index stays: indexed at a commit, most
--     recently; the other copies go, with their files, symbols, vectors and
--     text search;
--   * permissions cached per token are dropped, so the next request reads
--     them again for the rows as they are now.
--
-- Where a row came from is recorded too: the origin of its clone URL, which
-- the indexer always rewrote to the configured GitLab.
--
-- One transaction, with the version inside it: a stop part-way leaves the
-- database as it was, and the next start runs this again from the top.
BEGIN;

ALTER TABLE repo_choices ADD COLUMN instance TEXT;
ALTER TABLE repo_choices ADD COLUMN created_at TEXT;

UPDATE repo_choices
   SET instance = lower(substr(http_url, 1, instr(http_url, '://') + 2 +
         CASE WHEN instr(substr(http_url, instr(http_url, '://') + 3), '/') > 0
              THEN instr(substr(http_url, instr(http_url, '://') + 3), '/') - 1
              ELSE length(http_url) END))
 WHERE instr(http_url, '://') > 0;

-- The copies: every path held by more than one row, ranked by when GitLab last listed each.
CREATE TEMP TABLE merge_choice AS
  SELECT gitlab_id, lower(path_with_namespace) AS k,
         ROW_NUMBER() OVER (PARTITION BY lower(path_with_namespace) ORDER BY COALESCE(seen_at, 0) DESC, gitlab_id DESC) AS rank,
         COUNT(*) OVER (PARTITION BY lower(path_with_namespace)) AS copies
    FROM repo_choices;
DELETE FROM merge_choice WHERE copies < 2;

CREATE TEMP TABLE merge_keep AS SELECT k, gitlab_id AS keep_id FROM merge_choice WHERE rank = 1;

-- The admin's latest choice among the copies, when one was ever made.
CREATE TEMP TABLE merge_pick AS
  SELECT k, from_id FROM (
    SELECT m.k, c.gitlab_id AS from_id, ROW_NUMBER() OVER (PARTITION BY m.k ORDER BY c.changed_at DESC, m.rank) AS n
      FROM merge_choice m JOIN repo_choices c ON c.gitlab_id = m.gitlab_id
     WHERE c.changed_at IS NOT NULL)
   WHERE n = 1;

UPDATE repo_choices
   SET included   = (SELECT c.included FROM merge_keep k JOIN merge_pick p ON p.k = k.k JOIN repo_choices c ON c.gitlab_id = p.from_id
                      WHERE k.keep_id = repo_choices.gitlab_id),
       branches   = (SELECT c.branches FROM merge_keep k JOIN merge_pick p ON p.k = k.k JOIN repo_choices c ON c.gitlab_id = p.from_id
                      WHERE k.keep_id = repo_choices.gitlab_id),
       changed_at = (SELECT c.changed_at FROM merge_keep k JOIN merge_pick p ON p.k = k.k JOIN repo_choices c ON c.gitlab_id = p.from_id
                      WHERE k.keep_id = repo_choices.gitlab_id)
 WHERE gitlab_id IN (SELECT k.keep_id FROM merge_keep k JOIN merge_pick p ON p.k = k.k);

-- Per branch, the newest good index of all the copies.
CREATE TEMP TABLE merge_rows AS
  SELECT r.id, k.keep_id,
         ROW_NUMBER() OVER (PARTITION BY m.k, r.branch
           ORDER BY (r.last_indexed_sha IS NOT NULL) DESC, COALESCE(r.last_indexed_at, 0) DESC, COALESCE(r.last_run_at, 0) DESC,
                    (r.gitlab_id = k.keep_id) DESC, r.id DESC) AS rank
    FROM repos r
    JOIN merge_choice m ON m.gitlab_id = r.gitlab_id
    JOIN merge_keep k ON k.k = m.k;

-- The text search keeps its own copy of the text (external content): told first, or it would go on finding what is gone.
INSERT INTO files_fts (files_fts, rowid, path, content)
  SELECT 'delete', id, path, content FROM files WHERE repo_id IN (SELECT id FROM merge_rows WHERE rank > 1);
-- Files, symbols, includes, vectors, errors and retries follow by ON DELETE CASCADE.
DELETE FROM repos WHERE id IN (SELECT id FROM merge_rows WHERE rank > 1);

UPDATE repos
   SET gitlab_id           = (SELECT m.keep_id FROM merge_rows m WHERE m.id = repos.id),
       path_with_namespace = (SELECT c.path_with_namespace FROM merge_rows m JOIN repo_choices c ON c.gitlab_id = m.keep_id WHERE m.id = repos.id),
       default_branch      = (SELECT c.default_branch FROM merge_rows m JOIN repo_choices c ON c.gitlab_id = m.keep_id WHERE m.id = repos.id),
       http_url            = (SELECT c.http_url FROM merge_rows m JOIN repo_choices c ON c.gitlab_id = m.keep_id WHERE m.id = repos.id)
 WHERE id IN (SELECT id FROM merge_rows WHERE rank = 1);

DELETE FROM repo_choices WHERE gitlab_id IN (SELECT gitlab_id FROM merge_choice WHERE rank > 1);

-- Which extractor built a row's symbols: nothing to say for rows that are gone.
DELETE FROM argus_meta
 WHERE key LIKE 'symbol_contract:%' AND CAST(substr(key, 17) AS INTEGER) NOT IN (SELECT id FROM repos);

DELETE FROM acl_cache;

DROP TABLE merge_rows;
DROP TABLE merge_pick;
DROP TABLE merge_keep;
DROP TABLE merge_choice;

PRAGMA user_version = 17;
COMMIT;
