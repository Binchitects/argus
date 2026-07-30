-- index_status full-scans index_errors once per repo to count errors;
-- give it an index on the column it filters by.
CREATE INDEX IF NOT EXISTS idx_errors_repo ON index_errors(repo_id);
