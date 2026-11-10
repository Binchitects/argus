-- The service's own key/value table takes a neutral name (v8.0: nothing inside is named after a brand).
-- 014 made it as argus_meta: a database of any age has it, under that name, by the time this runs.
ALTER TABLE argus_meta RENAME TO code_intel_meta;
