-- impact_of walks from a file to what includes it: by resolved_file_id, once per file reached.
CREATE INDEX IF NOT EXISTS idx_includes_resolved_file ON includes(resolved_file_id);
