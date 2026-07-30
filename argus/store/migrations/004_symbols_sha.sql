-- Explicit marker for "symbols were successfully extracted from this blob".
-- Replaces inferring completion from the existence of symbol rows, which is
-- wrong for files that legitimately contain zero symbols and for files whose
-- fresh extraction failed while older symbol rows survived.
ALTER TABLE files ADD COLUMN symbols_sha TEXT;
