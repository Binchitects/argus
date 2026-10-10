-- The cross-repository graph beyond #include: what each file declares it
-- provides (a package, a module, a namespace) and what it uses (a package
-- reference, an import, a submodule, a CI include, an image), and the links
-- between repositories that resolving one against the other gives.
--
-- file_decls is written with the file, like includes, so a run reads only what
-- changed; repo_links is rebuilt from it after every run. A name more than one
-- repository provides links to none of them: a wrong edge silently misleads
-- every answer built on the graph, and the run's statistics say how many.
CREATE TABLE IF NOT EXISTS file_decls (
  id      INTEGER PRIMARY KEY,
  repo_id INTEGER NOT NULL REFERENCES repos(id) ON DELETE CASCADE,
  file_id INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
  role    TEXT    NOT NULL,
  kind    TEXT    NOT NULL,
  name    TEXT    NOT NULL,
  line    INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_file_decls_file ON file_decls(file_id);
CREATE INDEX IF NOT EXISTS idx_file_decls_kind ON file_decls(role, kind, name);

CREATE TABLE IF NOT EXISTS repo_links (
  from_repo_id INTEGER NOT NULL REFERENCES repos(id) ON DELETE CASCADE,
  to_repo_id   INTEGER NOT NULL REFERENCES repos(id) ON DELETE CASCADE,
  kind         TEXT    NOT NULL,
  name         TEXT    NOT NULL,
  files        INTEGER NOT NULL,
  file_id      INTEGER,
  line         INTEGER,
  PRIMARY KEY (from_repo_id, to_repo_id, kind, name)
);
CREATE INDEX IF NOT EXISTS idx_repo_links_to ON repo_links(to_repo_id);
