# Configuration

`deploy/code-intel-standalone/.env` is the whole deployment. Start from a sample in
`deploy/code-intel-standalone/env-samples/`; each is a measured setup for one model on one card. This
page lists every setting. Values not in `.env` take the default shown.

## Secrets

Every one must be set; `docker compose` names any that is missing. The
one-liner in [`deploy/code-intel-standalone/README.md`](../../../deploy/code-intel-standalone/README.md) generates them all.

| variable | what it is |
|---|---|
| `CODE_INTEL_ADMIN_PASSWORD` | the first administrator's password (12+ characters), used once when nobody exists yet |
| `LLAMACPP_API_KEY` | the chat engine's key; only the gateway presents it |
| `LITELLM_MASTER_KEY` | the gateway's administrative key (`sk-…`); the app uses it to manage people, keys and budgets |
| `LITELLM_SALT_KEY` | encrypts what the gateway stores (`sk-…`). **Never change it after first start**: the database becomes unreadable |
| `LLM_PG_PASSWORD` | the gateway database's password |

## App

| variable | default | what it does |
|---|---|---|
| `CODE_INTEL_ADMIN_USERNAME` | `admin` | the first administrator's username |
| `CODE_INTEL_ADMIN_EMAIL` | — (required) | their email; the code index matches it to a GitLab account |
| `BIND_ADDRESS` | `127.0.0.1` | `0.0.0.0` serves the network |
| `CODE_INTEL_HTTP_PORT` | `8080` | the app, its API and `/mcp` |
| `GATEWAY_PORT` | `4000` | the gateway's OpenAI-compatible API, for editors and scripts |
| `CODE_INTEL_HOSTNAME` | `localhost` | the name people use to reach the server. MCP refuses any other `Host` header (`421`), a guard against DNS rebinding |
| `CODE_INTEL_PUBLIC_GATEWAY_URL` | `http://localhost:4000/v1` | the gateway URL shown on each person's Settings page |
| `CODE_INTEL_TLS_CERT`, `CODE_INTEL_TLS_KEY` | empty | PEM files (paths inside the container, under `/etc/code-intel/tls/`) to serve HTTPS; empty = HTTP |
| `CODE_INTEL_CHAT_SYSTEM_PROMPT` | built in | replaces the chat's opening instruction; the tools' own instructions are always appended |
| `CODE_INTEL_ADMIN_TOKEN` | empty | lets scripts call `/admin/*` without a session (`X-Farzan Graph-Admin-Token`) |
| `CODE_INTEL_SOURCE_URL` | `https://github.com/Binchitects/argus` | the **Source** link beside the version at the foot of the app's sidebar. Farzan Graph is under the AGPL, which has everyone who uses it over a network offered the complete source of the version they use: a modified version points this at its own ([LICENSING.md](../../../LICENSING.md)) |

## Model

| variable | what it does |
|---|---|
| `MODEL_NAME` | the one name every client sees; also llama.cpp's `--alias` |
| `MODEL_CONTEXT`, `MODEL_MAX_OUTPUT` | context window and largest completion, advertised by the gateway |
| `MODEL_REASONING_EFFORT` | `xhigh`, `medium` or `low` — passed to the engine's chat template |
| `MODEL_ENABLE_THINKING` | `false` turns thinking off |
| `LLAMACPP_MODEL_DIR` | host directory for the GGUF files — **on NVMe**: weights page in on demand |
| `LLAMACPP_HF_REPO`, `LLAMACPP_HF_FILES`, `LLAMACPP_MODEL_FILE` | what `model-init` downloads, and the file to load (first shard of a split GGUF) |
| `LLAMACPP_N_GPU_LAYERS`, `LLAMACPP_N_CPU_MOE` | layers on the GPU; MoE layers whose experts stay in system RAM |
| `LLAMACPP_KV_TYPE` | KV cache precision (`q8_0` halves it against f16) |
| `LLAMACPP_PARALLEL` | people served at once |
| `LLAMACPP_MTP_DRAFT_MAX`, `LLAMACPP_MTP_HEAD`, `LLAMACPP_MTP_ARGS` | multi-token prediction |
| `LLAMACPP_ENGINE_URL`, `LLAMACPP_ENGINE_SHA256` | run a llama.cpp release tarball instead of the image's server |
| `LLAMACPP_EXTRA_ARGS` | anything else for `llama-server` |
| `LLAMACPP_THREADS`, `LLAMACPP_CPUS` | physical cores to use, and the container's CPU ceiling |
| `LLAMACPP_MLOCK`, `LLAMACPP_PRELOAD`, `LLAMACPP_RAM_RESERVE_GB` | keep the weights in RAM (`auto` decides from sizes and logs why) |
| `LLAMACPP_MEM_LIMIT`, `LLAMACPP_SHM_SIZE` | the container's memory ceiling and shared memory |
| `HF_TOKEN` | only for gated Hugging Face repositories |

## Embeddings

| variable | default | what it does |
|---|---|---|
| `EMBED_MODEL_DIR` | `./models` | host directory for the embedding GGUF |
| `EMBED_HF_REPO`, `EMBED_MODEL_FILE` | `nomic-ai/nomic-embed-text-v1.5-GGUF`, `nomic-embed-text-v1.5.f16.gguf` | what `model-init` downloads |
| `EMBED_THREADS`, `EMBED_CPUS` | `4`, `4` | the embedding server's threads and CPU ceiling |
| `CODE_INTEL_EMBED_MODEL`, `CODE_INTEL_EMBED_DIM` | `nomic-embed-text`, `768` | must match the packs: a different model makes semantic search refuse them |

## Gateway

| variable | default | what it does |
|---|---|---|
| `LITELLM_DEFAULT_USER_BUDGET` | `50` | budget per person per period when none is set; `0` would block everyone |
| `LITELLM_BUDGET_DURATION` | `1mo` | the budget period |
| `PRICE_INPUT_PER_MTOK`, `PRICE_CACHED_INPUT_PER_MTOK`, `PRICE_OUTPUT_PER_MTOK` | `0.20`, `0.02`, `0.80` | what a million tokens cost, which is what budgets count |
| `LITELLM_WORKERS` | `1` | gateway worker processes |

## Code index

| variable | default | what it does |
|---|---|---|
| `CODE_INTEL_GITLAB_URL` | the placeholder in `config/code-intel/config.yaml` | which GitLab |
| `CODE_INTEL_GITLAB_TOKEN` | — | read-only service token (`read_api`, `read_repository`), Reporter or above in every project to index |
| `CODE_INTEL_GITLAB_AUTH`, `CODE_INTEL_GITLAB_USERNAME`, `CODE_INTEL_GITLAB_PASSWORD` | — | password mode, only if no token can be issued; a username wins over a token unless `CODE_INTEL_GITLAB_AUTH=token` |
| `CODE_INTEL_GITLAB_CA_CERT` | — | a private CA for GitLab, as a path inside the container |
| `CODE_INTEL_GITLAB_VERIFY` | — | `false` disables certificate checks (last resort) |
| `CODE_INTEL_INDEX_INTERVAL` | `900` | seconds between index passes; `0` = only on demand. A repository with a schedule of its own (Repositories → Schedule) is left to it |
| `CODE_INTEL_INDEX_SCHEDULER` | on | `off` stops running the repositories' own schedules (every N hours, daily, weekly) |
| `CODE_INTEL_INDEX_STALE_AFTER` | `3600` | seconds without a pass before a repository is reported stale |
| `CODE_INTEL_LINK_PRIORS` | `link-priors.json` beside the index | calibrated link priors (`code-intel graph-eval --write-priors`), read by each rebuild of the links; none there: the built-in ones |
| `CODE_INTEL_GITHUB_API` | `https://api.github.com` | GitHub's API for `github:owner/repo` pull requests (a GitHub Enterprise API, or `off`); read with each person's own token, which the app passes in `x-llm-github-token` |
| `CODE_INTEL_GITEA_URL` | empty | your Gitea for `gitea:owner/repo` pull requests; read with each person's own token (`x-llm-gitea-token`) |
| `CODE_INTEL_WEBHOOK_TOKEN` | empty | GitLab push webhook secret for `/hook/gitlab`; empty = no webhook route |
| `CODE_INTEL_PACK_INDEX_URL` | empty | a published pack index, for *Update all packs* |
| `CODE_INTEL_EMBED_PER_PASS` | built in | how many symbols one index pass embeds |
| `CODE_INTEL_VECTOR_BACKEND`, `CODE_INTEL_PG_DSN` | sqlite-vec | `pgvector` and a Postgres DSN to search symbol embeddings in Postgres instead (optional, for very large estates) |

## Hardware

| variable | what it does |
|---|---|
| `GPU_POWER_LIMIT_W`, `CPU_POWER_LIMIT_W`, `HOST_SWAPPINESS` | applied by the opt-in `power-limits` service (`COMPOSE_PROFILES=power-limits`); cleared values are restored |
| `POSTGRES_CPUS` | the gateway database's CPU ceiling |

## Files under `deploy/code-intel-standalone/config/`

| file | what it is |
|---|---|
| `code-intel/config.yaml` | the code index's paths and the GitLab placeholder; no secrets |
| `code-intel/tls/` | PEM files: the HTTPS certificate and key, a GitLab CA |
| `litellm/config.yaml` | the gateway's model entry and budget policy, rendered from `.env` at start |
