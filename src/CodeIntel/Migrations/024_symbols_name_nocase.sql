-- Symbol names without case, for the wiki's search (CodeSearch.cs).
--
-- A person types "testfunc" or "TESTFUNC" for TestFunc, and the start of a
-- name for the rest of it. idx_symbols_name is in the binary collation, so
-- `name = ? COLLATE NOCASE` and a range on the start of a name scanned every
-- symbol. With this index both are a seek.
CREATE INDEX IF NOT EXISTS idx_symbols_name_nocase ON symbols(name COLLATE NOCASE);
