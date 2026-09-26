-- A one-row-per-key store for facts about the INDEX rather than about the code.
--
-- First use: which version of the symbol extractor produced the rows currently
-- in `symbols`. `files.symbols_sha` records that a file's symbols came from a
-- particular blob, which answers "have they been extracted from THIS revision"
-- and cannot answer "were they extracted by the CURRENT extractor". The stamp
-- in `worker._symbols_stamp` makes an individual file look stale once the
-- contract version changes, but nothing ever LOOKS at an unchanged file: a pass
-- computes a git diff, and a file nobody has committed to is not in it. So a
-- version bump would reach only the files edited afterwards -- the doc column
-- arriving across a real estate over months, half of it documented with no way
-- to tell which half.
--
-- Recording the version the index was built with lets a pass notice the change
-- and force one full listing. Files already carrying the new stamp are skipped
-- by the same check as always, so an interrupted or timed-out pass resumes
-- cheaply rather than starting over.
CREATE TABLE IF NOT EXISTS argus_meta (
  key   TEXT PRIMARY KEY,
  value TEXT NOT NULL
);
