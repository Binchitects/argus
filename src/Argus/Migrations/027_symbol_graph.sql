-- The symbol graph inside each repository (Symbols/SymbolGraph.cs, with Code Arena's scanners and resolution): who calls
-- or names what, and which type extends or implements which. One row per use (or base), with how it was tied to what it
-- names (evidence: S scope or import, T the receiver's declared type, N the only definition of the name, G a guess among
-- two or three, one row each) and how sure that is. Rebuilt for a repository when it is indexed; every read puts the
-- reader's allowlist on repo_id, as every other read does.
CREATE TABLE IF NOT EXISTS sym_edges (
  repo_id     INTEGER NOT NULL REFERENCES repos(id) ON DELETE CASCADE,
  kind        TEXT    NOT NULL,   -- calls, references, inherits
  from_path   TEXT    NOT NULL,
  from_symbol TEXT    NOT NULL,   -- qualified; '' for a file's top level
  from_name   TEXT    NOT NULL,
  from_line   INTEGER NOT NULL,
  to_path     TEXT    NOT NULL,
  to_symbol   TEXT    NOT NULL,   -- qualified
  to_name     TEXT    NOT NULL,
  to_line     INTEGER NOT NULL,
  evidence    TEXT    NOT NULL,   -- S, T, N, G
  confidence  REAL    NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_sym_edges_to ON sym_edges(repo_id, to_symbol, kind);
CREATE INDEX IF NOT EXISTS idx_sym_edges_from ON sym_edges(repo_id, from_symbol, kind);
CREATE INDEX IF NOT EXISTS idx_sym_edges_name ON sym_edges(to_name);
CREATE INDEX IF NOT EXISTS idx_sym_edges_from_name ON sym_edges(from_name);
