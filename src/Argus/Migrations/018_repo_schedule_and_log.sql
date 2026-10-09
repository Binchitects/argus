-- Each repository's own schedule, and a log of what each run did to it.
--
-- schedule: '' follows the default for every repository (argus_meta
-- index.repo_schedule; with each scheduled pass, until set otherwise: what
-- indexing did before); 'pass', 'off', 'hours:N', 'daily:HH:MM' or
-- 'weekly:D:HH:MM' (D is 1 for Monday to 7 for Sunday) of its own.
-- scheduled_at: when its schedule last started a run of it, or was set; the
-- next time counts from there, so a time that went by while Argus was down
-- runs once when it is back, not once per missed time.
--
-- index_log is keyed by the GitLab project, not the index row, so a
-- repository's history outlives its index being removed and built anew. Its
-- kind keeps runs that found nothing new (only the latest is kept) and admins'
-- changes (kept apart) from pushing real runs out of it.
BEGIN;

ALTER TABLE repo_choices ADD COLUMN schedule TEXT NOT NULL DEFAULT '';
ALTER TABLE repo_choices ADD COLUMN scheduled_at INTEGER;

CREATE TABLE IF NOT EXISTS index_log (
  id        INTEGER PRIMARY KEY,
  gitlab_id INTEGER NOT NULL,
  -- The run it belongs to: when the run started (unix milliseconds); an admin's change is one of its own.
  run       INTEGER NOT NULL,
  -- run; quiet (a run that found nothing new); note (an admin's change)
  kind      TEXT    NOT NULL DEFAULT 'run',
  ts        REAL    NOT NULL,
  -- info, warning or error
  level     TEXT    NOT NULL,
  text      TEXT    NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_index_log_repo ON index_log(gitlab_id, id);

PRAGMA user_version = 18;
COMMIT;
