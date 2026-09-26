-- An indexed basename, so looking a file up by name is a seek and not a scan.
--
-- Measured on a 10,212-file index before this migration:
--
--   _files_named('psintrp.c')           15.14 ms   ->  1 row
--   _files_named('util.h')              15.17 ms   ->  3 rows
--   find_symbol('png_read_image')        0.01 ms   (indexed, for contrast)
--
-- The cost was identical whether one row came back or three, which is the
-- signature of a full scan: `path LIKE '%/' || ?` has a leading wildcard, so
-- no index on `path` can serve it and every file in the allowed repos is
-- tested one at a time. Growing the corpus 10x (1,026 -> 10,212 files) grew
-- the query 9.9x. That is linear in corpus size, and a real estate is another
-- order of magnitude larger again.
--
-- It matters more than it used to. A defect fixed in the same series made
-- bare filenames ("inflate.c") resolve as *paths* rather than falling through
-- to nothing, so this lookup now runs on the most common query shape there is.
--
-- GENERATED ALWAYS ... VIRTUAL rather than a real column plus a backfill:
-- there is no migration-time UPDATE over every row, no second writer to keep
-- in step in argus/store/writes.py, and no way for the two to drift apart.
-- The cost is that the expression is evaluated on read, which the index makes
-- irrelevant for the lookup this exists to serve. VIRTUAL is also the only
-- kind SQLite permits ALTER TABLE ADD COLUMN to add.
--
-- The expression is the standard SQLite basename idiom: REPLACE(path,'/','')
-- is the set of every non-slash character in the path, RTRIM strips those
-- from the right until it hits the last slash, leaving the directory prefix,
-- and REPLACE removes that prefix. Verified against Python's rsplit('/')[-1]
-- for paths with no slash, repeated segments ('a/b/a/b'), a prefix that also
-- occurs in the basename ('ab/abab'), spaces, mixed case and the empty string.
ALTER TABLE files ADD COLUMN basename TEXT
  GENERATED ALWAYS AS (REPLACE(path, RTRIM(path, REPLACE(path, '/', '')), ''))
  VIRTUAL;

CREATE INDEX IF NOT EXISTS idx_files_basename ON files(basename);
