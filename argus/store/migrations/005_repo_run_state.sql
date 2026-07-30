-- Persist the outcome of the most recent indexing pass so index_status can
-- report partial coverage. Phase 2 exposes index_status to an agent whose
-- purpose is qualifying stale answers; these are the states worth qualifying.
ALTER TABLE repos ADD COLUMN last_run_timed_out INTEGER NOT NULL DEFAULT 0;
ALTER TABLE repos ADD COLUMN last_run_symbols_failed INTEGER NOT NULL DEFAULT 0;
ALTER TABLE repos ADD COLUMN last_run_at INTEGER;
