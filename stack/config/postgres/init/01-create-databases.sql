-- Runs once, only when the postgres-data volume is empty.
-- LiteLLM and Langfuse each want their own database on the shared instance.

CREATE DATABASE litellm;
CREATE DATABASE langfuse;

-- Argus keeps its code index and symbol embeddings here. The `vector`
-- extension is created inside this database rather than globally: extensions
-- are per-database in Postgres, and Argus is the only consumer.
CREATE DATABASE argus;
\connect argus
CREATE EXTENSION IF NOT EXISTS vector;
