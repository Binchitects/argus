-- Marks files that sit inside a bundled copy of another indexed repository.
--
-- Computed during the resolution pass by argus.resolve.find_vendored_dirs and
-- stored, rather than derived per query: the detection needs every file path
-- in the whole index at once, which is far too much work to repeat on each
-- which_repo call. Same shape as repo_deps -- materialised once per pass.
--
-- Why it cannot be inferred from the path alone: a bundled copy is usually
-- modified (freetype's src/gzip/inflate.c is 57,147 bytes against zlib's
-- 53,660, so content hashing misses it) and its directory is often not named
-- after what it contains (src/gzip, not src/zlib).
--
-- 0 means "not vendored, or never analysed", which is true of every row
-- written before this migration ran.
ALTER TABLE files ADD COLUMN is_vendored INTEGER NOT NULL DEFAULT 0;

CREATE INDEX IF NOT EXISTS idx_files_vendored ON files(is_vendored);
