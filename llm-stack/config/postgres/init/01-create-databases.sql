-- Runs once, only when the postgres-data volume is empty.
-- LiteLLM and Langfuse each want their own database on the shared instance.

CREATE DATABASE litellm;
CREATE DATABASE langfuse;
