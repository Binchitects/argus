-- Links with how sure they are, their layer, their scope and their evidence.
--
-- A declaration says its form (a C# alias, a CI include, a Dockerfile's base image), its scope (a test's, or the
-- product's) and its origin (source, a manifest, generated code); file_decls is read anew for every file (the reader's
-- contract 5). repo_links keeps one row per way one repository uses another: its layer (build, deploy, runtime,
-- declared), its scope, how its name matched, how many repositories provide it, a confidence and its tier (strong,
-- likely, weak; candidate for a name several repositories provide that nothing settled), the files that make it and
-- where. repo_edges sums a pair's ways per layer; file_links says which files make each link (change_impact keeps a
-- symbol's references to them).
ALTER TABLE file_decls ADD COLUMN form TEXT NOT NULL DEFAULT '';
ALTER TABLE file_decls ADD COLUMN scope TEXT NOT NULL DEFAULT 'main';
ALTER TABLE file_decls ADD COLUMN origin TEXT NOT NULL DEFAULT 'source';
CREATE INDEX IF NOT EXISTS idx_file_decls_repo ON file_decls(repo_id);

DROP TABLE IF EXISTS repo_links;
CREATE TABLE repo_links (
  from_repo_id INTEGER NOT NULL REFERENCES repos(id) ON DELETE CASCADE,
  to_repo_id   INTEGER NOT NULL REFERENCES repos(id) ON DELETE CASCADE,
  kind         TEXT    NOT NULL,
  name         TEXT    NOT NULL,
  layer        TEXT    NOT NULL,
  scope        TEXT    NOT NULL,
  how          TEXT    NOT NULL,
  providers    INTEGER NOT NULL,
  confidence   REAL    NOT NULL,
  tier         TEXT    NOT NULL,
  files        INTEGER NOT NULL,
  evidence     TEXT    NOT NULL,
  PRIMARY KEY (from_repo_id, to_repo_id, kind, name)
);
CREATE INDEX IF NOT EXISTS idx_repo_links_to ON repo_links(to_repo_id);

CREATE TABLE IF NOT EXISTS repo_edges (
  from_repo_id INTEGER NOT NULL REFERENCES repos(id) ON DELETE CASCADE,
  to_repo_id   INTEGER NOT NULL REFERENCES repos(id) ON DELETE CASCADE,
  layer        TEXT    NOT NULL,
  scope        TEXT    NOT NULL,
  confidence   REAL    NOT NULL,
  tier         TEXT    NOT NULL,
  files        INTEGER NOT NULL,
  kinds        TEXT    NOT NULL,
  PRIMARY KEY (from_repo_id, to_repo_id, layer)
);
CREATE INDEX IF NOT EXISTS idx_repo_edges_to ON repo_edges(to_repo_id);

CREATE TABLE IF NOT EXISTS file_links (
  file_id    INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
  to_repo_id INTEGER NOT NULL REFERENCES repos(id) ON DELETE CASCADE,
  kind       TEXT    NOT NULL,
  name       TEXT    NOT NULL,
  confidence REAL    NOT NULL,
  PRIMARY KEY (file_id, to_repo_id, kind, name)
);
CREATE INDEX IF NOT EXISTS idx_file_links_to ON file_links(to_repo_id);

DELETE FROM argus_meta WHERE key = 'graph_stats';
