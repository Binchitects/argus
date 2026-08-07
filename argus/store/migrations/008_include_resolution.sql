-- Distinguishes an ambiguous include from an unfindable one. Both leave
-- resolved_file_id NULL, so without this column the resolution statistics an
-- operator needs ("34% of your includes are ambiguous" means your -I layout
-- defeats suffix matching) cannot be computed.
--
-- NULL means "never resolved", which is true of every row written before this
-- migration ran.
ALTER TABLE includes ADD COLUMN resolution TEXT;

CREATE INDEX IF NOT EXISTS idx_includes_resolution ON includes(resolution);
CREATE INDEX IF NOT EXISTS idx_includes_resolved_repo ON includes(resolved_repo_id);
