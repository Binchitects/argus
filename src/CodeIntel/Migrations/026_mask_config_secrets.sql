-- Configuration files (.env, .properties, appsettings*.json) are stored with their secret values taken out
-- (Util/ConfigSecrets). Those stored before that are masked once, by the indexer when it next opens the index
-- (Db.Open runs what this table holds, then empties it): SQL alone cannot tell a secret's shape.
CREATE TABLE IF NOT EXISTS pending_tasks (
  name TEXT PRIMARY KEY
);
INSERT OR IGNORE INTO pending_tasks (name) VALUES ('mask-config-secrets');
