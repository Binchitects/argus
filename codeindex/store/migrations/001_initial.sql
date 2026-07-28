CREATE TABLE IF NOT EXISTS repos (
  id                  INTEGER PRIMARY KEY,
  gitlab_id           INTEGER NOT NULL UNIQUE,
  path_with_namespace TEXT    NOT NULL,
  default_branch      TEXT    NOT NULL,
  http_url            TEXT    NOT NULL,
  last_indexed_sha    TEXT,
  last_indexed_at     INTEGER
);

CREATE TABLE IF NOT EXISTS files (
  id       INTEGER PRIMARY KEY,
  repo_id  INTEGER NOT NULL REFERENCES repos(id) ON DELETE CASCADE,
  path     TEXT    NOT NULL,
  lang     TEXT,
  size     INTEGER NOT NULL,
  blob_sha TEXT    NOT NULL,
  content  TEXT    NOT NULL,
  UNIQUE (repo_id, path)
);
CREATE INDEX IF NOT EXISTS idx_files_repo ON files(repo_id);

CREATE TABLE IF NOT EXISTS symbols (
  id        INTEGER PRIMARY KEY,
  repo_id   INTEGER NOT NULL REFERENCES repos(id) ON DELETE CASCADE,
  file_id   INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
  name      TEXT    NOT NULL,
  kind      TEXT    NOT NULL,
  line      INTEGER NOT NULL,
  end_line  INTEGER,
  signature TEXT,
  scope     TEXT,
  is_public INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS idx_symbols_name ON symbols(name);
CREATE INDEX IF NOT EXISTS idx_symbols_repo ON symbols(repo_id);
CREATE INDEX IF NOT EXISTS idx_symbols_file ON symbols(file_id);

CREATE TABLE IF NOT EXISTS includes (
  id               INTEGER PRIMARY KEY,
  repo_id          INTEGER NOT NULL REFERENCES repos(id) ON DELETE CASCADE,
  file_id          INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
  raw              TEXT    NOT NULL,
  is_angle         INTEGER NOT NULL DEFAULT 0,
  resolved_file_id INTEGER,
  resolved_repo_id INTEGER,
  is_external      INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS idx_includes_repo ON includes(repo_id);
CREATE INDEX IF NOT EXISTS idx_includes_file ON includes(file_id);

CREATE TABLE IF NOT EXISTS repo_deps (
  from_repo_id INTEGER NOT NULL REFERENCES repos(id) ON DELETE CASCADE,
  to_repo_id   INTEGER NOT NULL REFERENCES repos(id) ON DELETE CASCADE,
  weight       INTEGER NOT NULL,
  PRIMARY KEY (from_repo_id, to_repo_id)
);

CREATE TABLE IF NOT EXISTS index_errors (
  id      INTEGER PRIMARY KEY,
  repo_id INTEGER NOT NULL REFERENCES repos(id) ON DELETE CASCADE,
  path    TEXT,
  stage   TEXT    NOT NULL,
  message TEXT    NOT NULL,
  ts      INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS index_queue (
  repo_id     INTEGER PRIMARY KEY REFERENCES repos(id) ON DELETE CASCADE,
  enqueued_at INTEGER NOT NULL,
  reason      TEXT    NOT NULL
);

CREATE VIRTUAL TABLE IF NOT EXISTS files_fts USING fts5(
  path,
  content,
  content='files',
  content_rowid='id',
  tokenize='unicode61'
);
