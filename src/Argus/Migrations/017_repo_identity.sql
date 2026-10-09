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
--   * it is in the index when any copy is: a copy turned off to hide the
--     duplicate, or a new copy the "leave new repositories out" policy left
--     out, never takes the index of the other copy away (the next run would
--     drop what is merged into a repository left out);
--   * its branches are those of every copy that is in (of every copy, when
--     none is), the kept copy's first: a branch indexed under either copy
--     stays indexed;
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

-- In when any copy is in; the admin's latest change to any copy, when one was ever made.
CREATE TEMP TABLE merge_pick AS
  SELECT m.k, MAX(c.included) AS included, MAX(c.changed_at) AS changed_at
    FROM merge_choice m JOIN repo_choices c ON c.gitlab_id = m.gitlab_id
   GROUP BY m.k;

-- The branches of the copies that are in (of every copy, when none is), one a row: names
-- or globs, one a line (a comma separates them too), the kept copy's first, each once.
CREATE TEMP TABLE merge_branch AS
  WITH RECURSIVE part(k, rank, n, item, rest) AS (
    SELECT m.k, m.rank, 0, NULL, replace(replace(c.branches, char(13), ''), ',', char(10)) || char(10)
      FROM merge_choice m
      JOIN repo_choices c ON c.gitlab_id = m.gitlab_id
      JOIN merge_pick p ON p.k = m.k
     WHERE c.included = p.included
    UNION ALL
    SELECT k, rank, n + 1, trim(substr(rest, 1, instr(rest, char(10)) - 1), ' ' || char(9)), substr(rest, instr(rest, char(10)) + 1)
      FROM part
     WHERE rest <> '')
  SELECT k, item, MIN(rank * 100000 + n) AS ord FROM part WHERE item <> '' GROUP BY k, item;

UPDATE repo_choices
   SET included   = (SELECT p.included FROM merge_keep k JOIN merge_pick p ON p.k = k.k WHERE k.keep_id = repo_choices.gitlab_id),
       changed_at = (SELECT p.changed_at FROM merge_keep k JOIN merge_pick p ON p.k = k.k WHERE k.keep_id = repo_choices.gitlab_id),
       branches   = COALESCE((SELECT group_concat(b.item, char(10) ORDER BY b.ord) FROM merge_keep k JOIN merge_branch b ON b.k = k.k
                               WHERE k.keep_id = repo_choices.gitlab_id), '')
 WHERE gitlab_id IN (SELECT keep_id FROM merge_keep);

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
DROP TABLE merge_branch;
DROP TABLE merge_pick;
DROP TABLE merge_keep;
DROP TABLE merge_choice;

PRAGMA user_version = 17;
COMMIT;
