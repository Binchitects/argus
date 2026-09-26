# Configuration: every setting, and what it actually does

The whole deployment is `.env`. This document covers every variable in it, the
values compose derives from them, and every file under `config/`.

For the wiring those settings feed, see [architecture.md](architecture.md). For
the steps that put a deployment together, see the
[top-level README](../README.md).

---

## 1. The one rule

```
cp env-samples/<one>.env  .env      # pick a sample
<edit .env>                          # this is the whole configuration
docker compose up -d                 # or: make up
```

There is no setup script, no state outside `.env` and the Docker volumes, and
nothing to run in the right order. `env-samples/` holds complete, measured
deployments; `env-samples/README.md` explains how to add one for another model
or card.

**Most of it can also be changed in the app** (Admin → Settings): the app's own
settings apply at once or after a restart the app does itself, and `.env`
values are saved as pending and applied with `./scripts/apply-settings.sh`.
See [settings.md](settings.md).

**Nothing in `.env` is a secret that ships.** The samples ship no values in the
SECRETS block on purpose — a sample in a public repository would give every
deployment the same passwords.

---

## 2. How a value reaches a service

Three different mechanisms, and knowing which applies to a given variable
explains most surprises:

| mechanism | example | notes |
|---|---|---|
| **compose interpolation** — `docker compose` substitutes `${X}` while reading the file | `LLM_DOMAIN`, `BIND_ADDRESS`, every `${...}` inside `docker-compose.yml` | happens on the **host**, at `up` time. Changing `.env` then means `docker compose up -d` to recreate the containers that used it |
| **container environment** — a service's `environment:` block | `ARGUS_GITLAB_URL`, `GF_SERVER_ROOT_URL` | read by the process at start. `.env` → compose → container |
| **config-file rendering** — a service rewrites a file at startup | `${MODEL_NAME}` in `config/litellm/config.yaml` | LiteLLM does not expand variables in its own config, so its entrypoint renders them |

Two consequences:

* **A `.env` edit needs `docker compose up -d`.** Editing the file alone changes
  nothing that is already running.
* **Traefik's static config cannot read the environment at all.** That is why
  `traefik.yml` contains `@COMPOSE_PROJECT_NAME@` and the entrypoint replaces it
  with `sed` before starting Traefik.

---

## 3. STACK

**`COMPOSE_PROFILES`** — which parts of the stack run. Everything not named here
is not created. See §15 for the full list. Adding a profile and running
`docker compose up -d` creates only the new containers.

**`COMPOSE_PROJECT_NAME`** *(default `llmservice`)* — prefixes container names,
the network and every volume. **Changing it after the first start boots an empty
stack**: the containers come up against new volumes with none of your data. It
is also what Traefik's Docker provider constraint matches, so changing it
without recreating Traefik makes every hostname 404 while every container stays
healthy.

**`LLM_DOMAIN`** *(default `llm.localhost`)* — the parent domain. Every service
is `https://<name>.<LLM_DOMAIN>` and the certificate covers the domain and
`*.<domain>`, so this one value moves the entire stack. `tls-init` notices a
change and regenerates the certificate; the app derives its session cookie, OIDC
issuer, client redirect URIs and forwardAuth rules from it at startup.

`.localhost` is reserved (RFC 6761) and resolves to loopback without any DNS or
`/etc/hosts` entry, which is why it is the default. A name that does not resolve
publicly cannot get a public certificate, so the stack generates its own.

**`BIND_ADDRESS`** *(default `0.0.0.0`)* — the interface Traefik publishes 80
and 443 on. `127.0.0.1` means this machine only; the samples use it. This is a
real control, not decoration: it once existed in `.env` and was referenced
nowhere, so a file that said `127.0.0.1` published on `0.0.0.0` and answered the
entire LAN.

**`TRAEFIK_HTTP_PORT`** / **`TRAEFIK_HTTPS_PORT`** *(default `80` / `443`)* — the
published host ports. Change them if something else owns 80/443; the URLs in
the app's links and the OIDC redirect URIs follow `TRAEFIK_HTTPS_PORT`
automatically.

### Where everything lives

Every bind mount is a variable, so the layout is yours: config on one disk,
models on another, the samples somewhere else again. A relative value resolves
against this compose file, so the defaults keep a checkout self-contained.

| variable | default | what it points at |
|---|---|---|
| `LLM_CONFIG_DIR` | `./config` | the app (dashboards, Argus directory), Traefik, LiteLLM, Prometheus, Loki, Postgres, Argus — every committed config file |
| `LLM_SERVICES_DIR` | `./services` | what compose builds or mounts: the sandbox, the identity proxy, the engine router, the exporters |
| `LLM_SOURCE_DIR` | `..` | the repository root the app, web and Argus images build from (`src/`) |
| `LLM_MODELS_DIR` | `./models` | the bind mount at `/models`, and the default for `LLAMACPP_MODEL_DIR` |
| `LLM_ENV_SAMPLES_DIR` | `./env-samples` | read by the app's **Admin → Model** page |
| `DOCKER_SOCKET` | `/var/run/docker.sock` | Traefik's discovery and Promtail's log reading |
| `HOST_DOCKER_DIR` | `/var/lib/docker` | Promtail's container logs, cAdvisor's view |
| `HOST_ROOT` / `HOST_PROC` / `HOST_SYS` / `HOST_DEV_DISK` | `/`, `/proc`, `/sys`, `/dev/disk` | node-exporter and cAdvisor |

`LLAMACPP_MODEL_DIR` defaults to `${LLM_MODELS_DIR}` rather than an absolute
path, so a checkout that keeps its weights in `./models` needs no edit at all.

**One placeholder, deliberately.** `config/argus/config.yaml` names
`https://gitlab.example.com`, not a real instance — it is committed, and
`ARGUS_GITLAB_URL` overrides it. It stays a valid URL rather than empty so a
missing value fails as an unreachable host, which says what is wrong, instead
of as a parse error.

---

## 4. MODEL

**`MODEL_NAME`** — *required.* The one name every client sees: the gateway, Open
WebUI, Qwen Code, the OpenAI SDK. LiteLLM advertises exactly one model under
exactly this name; aliases were removed because clients cached `/model/info` and
listed every one of them as a separate model.

**`MODEL_CONTEXT`** *(sample `262144`)* — the context window in tokens,
advertised to clients and shared by all parallel slots (`--kv-unified`).
**Must match what the engine actually serves.** A stale value here makes clients
refuse prompts the engine would happily accept, because they cache it.

**`MODEL_MAX_OUTPUT`** *(sample `32768`)* — the largest completion the gateway
advertises.

**`MODEL_REASONING_EFFORT`** *(default `medium`)* — how hard the model thinks
before it answers when a chat or client does not choose: `xhigh` (the model's
own default, and slowest), `medium`, or `low`. The app's chat starts at this
level too, and each chat can pick another. This is the **engine's**
chat-template variable, not OpenAI's. The template raises on anything else, so a
typo is a 500 on every request rather than a quiet fallback to the default.

**`MODEL_ENABLE_THINKING`** *(default `true`)* — `false` turns thinking off, so
the model answers immediately.

Both are rendered into `config/litellm/config.yaml` as `chat_template_kwargs`
and applied to every request through the gateway.

### Changing the thinking level per chat

**Pick it from the model dropdown.** The stack creates one Open WebUI model per
level on every start, so the level is chosen per chat the same way the model is:

| picker entry | what it sends |
|---|---|
| `Qwen3.8-Flash-Next · Deep think` | `reasoning_effort: xhigh` (the model's own default) |
| `Qwen3.8-Flash-Next · Balanced` | `reasoning_effort: medium` |
| `Qwen3.8-Flash-Next · Quick` | `reasoning_effort: low` |
| `Qwen3.8-Flash-Next · No thinking` | `enable_thinking: false` |

Configured by `THINKING_PRESETS` (`level:Label`, comma-separated); empty turns
the presets off. They are created by `deploy/services/seed-presets.py`, which the
open-webui entrypoint starts in the background — it waits for the first admin to
sign in, because Open WebUI will not list a model that has no owner, and on a
fresh deployment no user exists until someone signs in through SSO.

Measured end to end through Open WebUI, same prompt, three picker entries:

```
Deep think    reasoning=2654 chars   completion_tokens=1319
Quick         reasoning=1251 chars   completion_tokens= 667
No thinking   reasoning=   0 chars   completion_tokens= 910
```

#### Why the field Open WebUI already has does not work

Open WebUI has a `reasoning_effort` parameter, offered in the chat controls'
Advanced Params and in the model editor, and it looks like exactly the right
control. **It does nothing here.** It goes out as a top-level field, and LiteLLM
drops a top-level `reasoning_effort` when the backend is a custom `openai/`
api_base — which this stack is. Measured, same prompt:

| request | reasoning | honoured? |
|---|---|---|
| no override | 2654 chars | — (the engine default) |
| `reasoning_effort: low` at the top level | 2654 chars | **no** |
| `chat_template_kwargs: {reasoning_effort: low}` | 1251 chars | yes |
| `chat_template_kwargs: {enable_thinking: false}` | 0 chars | yes |

`--jinja` means the model's own template decides what thinking means, and it
reads `reasoning_effort` and `enable_thinking` as Jinja variables. Only
`chat_template_kwargs` reaches it. A typo there is loud — the template raises
`Unexpected reasoning effort` — which is the one mercy in this arrangement.

This is why the presets carry the value in `custom_params`, which Open WebUI
deep-merges into the outgoing body (see `apply_model_params_to_body_openai`).

#### Doing it by hand

Any request whose params carry `chat_template_kwargs` works, so the API and a
model preset both do:

```json
{ "custom_params": { "chat_template_kwargs": { "reasoning_effort": "low" } } }
```

The value must be a real object, not a string. A JSON string is passed through
as a string and the engine never sees a variable it recognises.

| want | value |
|---|---|
| deepest reasoning (the model's own default) | `{"reasoning_effort": "xhigh"}` |
| balanced | `{"reasoning_effort": "medium"}` |
| quick answers | `{"reasoning_effort": "low"}` |
| no thinking at all | `{"enable_thinking": false}` |

Verified end-to-end through Open WebUI's own `/api/chat/completions`, not only
at the gateway: a bogus `reasoning_effort` sent this way reaches the template and
raises, while the same value sent as `reasoning_effort` is dropped.

---

## 5. The llama.cpp engine (`llamacpp` profile)

| variable | default | what it does |
|---|---|---|
| `LLAMACPP_MODEL_DIR` | *required* | host directory holding the GGUFs. **Put it on NVMe** — the weights are memory-mapped and paged in on demand |
| `LLAMACPP_MODEL_FILE` | *required* | the model file; for a split GGUF, the **first** shard |
| `LLAMACPP_LIBRARY_DIR` | `LLAMACPP_MODEL_DIR` | the **model library**: the host folder Admin → Models adds more models from, searched three levels deep. Usually the folder that holds `LLAMACPP_MODEL_DIR` |
| `LLAMACPP_MODELS_MAX` | `1` | models loaded at once. Loading one more unloads the least recently used. One GPU usually holds one large model |
| `LLAMACPP_HF_REPO` | empty | Hugging Face repo to download from on first start. Empty means the files must already be present |
| `LLAMACPP_HF_FILES` | empty | space-separated paths inside that repo. They land flat in `LLAMACPP_MODEL_DIR` and are SHA-256 checked |
| `LLAMACPP_ENGINE_URL` | empty | a llama.cpp release tarball to run instead of the stock server. Exists for MTP: mainline has no MTP graph for some architectures, so the stock image accepts `--spec-type draft-mtp` and silently does nothing |
| `LLAMACPP_ENGINE_SHA256` | empty | checksum of that tarball |
| `LLAMACPP_MTP_DRAFT_MAX` | `0` | multi-token prediction: tokens drafted per step. `0` is off |
| `LLAMACPP_MTP_HEAD` | empty | a separate MTP draft head file in `LLAMACPP_MODEL_DIR`. Empty means the model's own MTP layer |
| `LLAMACPP_MTP_ARGS` | empty | extra flags for the draft |
| `LLAMACPP_N_GPU_LAYERS` | `99` | layers on the GPU. `99` is all |
| `LLAMACPP_N_CPU_MOE` | `42` | MoE only: how many layers keep their **experts** in system RAM. Raise it if loading hits CUDA out of memory; `0` for a dense model |
| `LLAMACPP_KV_TYPE` | `q8_0` | KV cache precision. `q8_0` halves it against `f16` for very little quality cost |
| `LLAMACPP_PARALLEL` | `2` | people served at once. More slots means more queue, not more speed |
| `LLAMACPP_EXTRA_ARGS` | see sample | anything else for `llama-server` |
| `LLAMACPP_IMAGE_TAG` | `server-cuda` | the image tag to run |
| `LLAMACPP_API_KEY` | secret | the engine's own key. **Not something clients need** — Traefik injects it (see below) |

The engine runs llama.cpp in **router mode** (`services/llamacpp/router.sh`):
the variables above become the `.env` model's preset (`LLAMACPP_EXTRA_ARGS`
translated to preset keys), the models added in Admin → Models come from
`config/engine/models.ini`, and a change to that file restarts llama-server so
it reads it. See [admin.md](admin.md#models).

`LLAMACPP_MODEL_DIR` and `LLAMACPP_MODEL_FILE` are required **by compose itself**
(`:?`), so a missing one fails at `docker compose up` with the variable named,
rather than at 3 a.m. as a container that cannot find its weights.

`LLAMACPP_API_KEY` is unusual: the engine listens on `api.<domain>` behind
the app's forwardAuth, and Traefik adds `Authorization: Bearer <key>` with the
label-defined `llamacpp-key@docker` middleware. Callers authenticate as
themselves; the engine key never leaves the stack.

---

## 6. The vLLM engine (`vllm` profile)

Same job as §5, different engine. `ENGINE_API_BASE` decides which one the
gateway talks to.

| variable | sample | what it does |
|---|---|---|
| `ENGINE_API_BASE` | `http://llamacpp:8080/v1` | the gateway's backend. Use `http://vllm:8000/v1` for vLLM |
| `VLLM_MODEL` | `/models/Qwen3.8-9B-AWQ` | the model path or Hugging Face id |
| `VLLM_SERVED_MODEL_NAME` | `qwen3.8-9b` | the name vLLM serves it under |
| `VLLM_MAX_MODEL_LEN` | `262144` | context window the engine allocates for |
| `VLLM_GPU_MEMORY_UTILIZATION` | `0.94` | fraction of VRAM vLLM may claim |
| `VLLM_DTYPE` | `auto` | weight dtype |
| `VLLM_TENSOR_PARALLEL_SIZE` | `1` | GPUs to shard across |
| `VLLM_MAX_NUM_SEQS` | `256` | concurrent sequences |
| `VLLM_API_KEY` | secret | the engine's key, injected by `vllm-key@docker` exactly as in §5 |
| `VLLM_EXTRA_ARGS` | see sample | anything else for `vllm serve` |
| `VLLM_IMAGE_TAG` | `latest` | image tag |
| `VLLM_USE_V2_MODEL_RUNNER` | `0` | the v2 runner; off by default |

**`ENGINE_MODEL`** is derived, not written by you: it defaults to
`openai/${MODEL_NAME}`, which is what LiteLLM wants for an OpenAI-compatible
backend.

---

## 7. The second model (`multi-model` profile)

A small model alongside the main one, reached at `api2.<domain>`. Useful for
fast classification or as a fallback while the big model is busy.

`VLLM2_MODEL`, `VLLM2_SERVED_MODEL_NAME`, `VLLM2_MAX_MODEL_LEN`,
`VLLM2_GPU_MEMORY_UTILIZATION`, `VLLM2_DTYPE`, `VLLM2_MAX_NUM_SEQS`,
`VLLM2_EXTRA_ARGS` — the same meanings as §6, for `vllm-secondary`.

The two shares of `GPU_MEMORY_UTILIZATION` must add up to less than 1.

### Image generation (`image` profile)

A picture model beside the chat model, on the same GPU. The chat's **Image
generation** tool and anyone's API key use it through the gateway, as the
model `IMAGEGEN_MODEL_NAME` (`POST /v1/images/generations`). The spend is
counted against the person, like chat.

`imagegen` runs [stable-diffusion.cpp](https://github.com/leejet/stable-diffusion.cpp)'s
server with **FLUX.2 [klein] 4B** (Apache 2.0) by default. Its three files
(about 5.3 GB) go in `IMAGEGEN_MODEL_DIR`; the `imagegen` service in
`docker-compose.yml` names where to get each one.

| Variable | Default | Meaning |
|---|---|---|
| `IMAGEGEN_MODEL_NAME` | `FLUX.2-klein-4B` | the name at the gateway and in the chat |
| `IMAGEGEN_MODEL_DIR` | — | the host folder with the files |
| `IMAGEGEN_DIFFUSION_MODEL`, `IMAGEGEN_TEXT_ENCODER`, `IMAGEGEN_VAE` | klein's files | file names in that folder |
| `IMAGEGEN_STEPS`, `IMAGEGEN_CFG_SCALE` | `4`, `1.0` | right for a distilled model like klein |
| `IMAGEGEN_MAX_VRAM` | `2` | GiB of GPU memory it may use at most (negative: leave that much free, which races the chat model at start) |

**Sharing the GPU.** The weights stay in RAM, and each step streams them to
the GPU within `IMAGEGEN_MAX_VRAM`, so the chat model's memory is never taken.
Measured on a 24 GB RTX 3090 beside a 21 GB chat model: 1024x1024 in about
27 s, 768x768 in 20 s; on an RTX 5090 beside a 20 GB one, 1024x1024 in 20 s.
Keep the budget a fixed number of GiB. With `-1` (leave 1 GiB free) a
from-zero start raced: this server filled the empty GPU first, the chat model
took the rest, and every picture failed for want of room to stream one block.

**Isolation.** The server has no key of its own. It sits on an internal network
(`image-net`) that only LiteLLM joins, with no way out.

---

## 8. Hardware limits

These describe **this machine**, not the model, which is why they sit in their
own section. They are also what keeps a 24 GB card from cooking itself.

| variable | default | what it does |
|---|---|---|
| `LLAMACPP_THREADS` | sample `16` | physical core count for the engine. Hyperthreads measured *slower*, not faster |
| `LLAMACPP_CPUS` | `16` | CPU ceiling for the engine container. Keep it equal to `LLAMACPP_THREADS` |
| `OLLAMA_CPUS` | sample `2` | CPU ceiling for the embedding model. Far less load-bearing since the model moved to the GPU — tokenisation and the HTTP layer are all that remain on the CPU |
| `OLLAMA_GPU_LAYERS` | empty | how many layers of the embedding model to offload. Empty lets Ollama decide, which is "all of it" when it fits. Set an integer to force a partial offload, or `0` to keep it on the CPU |
| `OLLAMA_GPU_DEVICE` | `all` | which GPU the embedder is pinned to (`NVIDIA_VISIBLE_DEVICES`). Set a device id on a multi-GPU host to keep the embedder off the card the engine is using |
| `POSTGRES_CPUS` | sample `3` | CPU ceiling for Postgres |
| `LLAMACPP_MEM_LIMIT` | `0` | engine RAM ceiling, e.g. `56g`. `0` is no limit |
| `GPU_POWER_LIMIT_W` | sample `150` | GPU power cap in watts (`nvidia-smi -pl`). Empty leaves the driver default |
| `CPU_POWER_LIMIT_W` | sample `125` | CPU package power cap (Intel RAPL). Many boards ship with **no limit** and cook the CPU; `125` is Intel's spec for a 13700K. Empty leaves the firmware setting |

`power-limits` applies both, records the originals in `power-limits-state`, and
re-applies them every 60 seconds — a reboot does not silently undo them.

---

## 9. SECRETS

Every value here must be set; compose names the missing one and refuses to
start. The rest of this section is what each is *for* and what you lose by
rotating it.

| variable | generate with | notes |
|---|---|---|
| `ADMIN_PASSWORD` | a few unrelated words, or `openssl rand -hex 24` | the first admin's password, used on the very first start only; change it in the app afterwards. (An install from before the app may still have `AUTHELIA_ADMIN_PASSWORD`; it is read as a fallback.) |
| `APP_DATA_KEY` | `openssl rand -hex 32` | **never change after first start**: encrypts the app's session and OIDC signing keys in its database |
| `PROXY_AUTH_USER` | — (a username) | basic-auth user for the internal services when the `auth` profile is **off** |
| `PROXY_AUTH_PASSWORD` | `openssl rand -hex 32` | the matching password |
| `LLAMACPP_API_KEY` | `openssl rand -hex 32` | the engine's own key, injected by Traefik |
| `LITELLM_MASTER_KEY` | `echo sk-$(openssl rand -hex 24)` | mints per-person keys. **Must start with `sk-`** |
| `SEARXNG_SECRET` | `openssl rand -hex 32` | only with the `websearch` profile (SearXNG refuses to start without it); free to rotate |
| `LITELLM_SALT_KEY` | `echo sk-$(openssl rand -hex 24)` | **never change after first start** |
| `LLM_PG_PASSWORD` | `openssl rand -hex 32` | the Postgres password; also the default for ClickHouse and MinIO |
| `WEBUI_SECRET_KEY` | `openssl rand -hex 32` | Open WebUI's session signing key |
| `OPENWEBUI_OIDC_CLIENT_SECRET` | `openssl rand -hex 32` | Open WebUI's OIDC client |
| `API_OIDC_CLIENT_SECRET` | `openssl rand -hex 32` | the `client_credentials` client machine callers use |
| `ARGUS_ADMIN_TOKEN` | `openssl rand -hex 32` | enables Argus's `/admin/index` route; unset means the surface does not exist |
| `ARGUS_CHAT_CLIENT_TOKEN` | `openssl rand -hex 32` | lets Open WebUI use Argus per person |

**Rotating a `*_OIDC_CLIENT_SECRET` invalidates that app's existing sessions**
until both sides have the new value: `docker compose up -d` recreates the app
(which re-registers the client) and the service together.

### The three that must not change

`LITELLM_SALT_KEY`, `APP_DATA_KEY` — and by extension anything already written
into `postgres-data`. Both encrypt data at rest:

* `LITELLM_SALT_KEY` encrypts the per-person keys stored in Postgres. Change it
  and every existing key stops being readable.
* `APP_DATA_KEY` encrypts the app's key ring: the keys that protect sessions,
  two-factor secrets and the OIDC signing key. The app **detects** a mismatch and
  refuses to start, naming the setting, rather than making new keys (which would
  sign everyone out and break single sign-on).

### Secrets outside the SECRETS block

Eight more exist further down `.env`, because they belong to optional profiles
and are documented next to them: `ARGUS_GITLAB_TOKEN`, `VLLM_API_KEY`,
`CLICKHOUSE_PASSWORD`, `MINIO_ROOT_PASSWORD`, `LANGFUSE_ENCRYPTION_KEY`,
`LANGFUSE_NEXTAUTH_SECRET`, `LANGFUSE_OIDC_CLIENT_SECRET` and
`WEBUI_BACKEND_KEY`.

Worth knowing if you ever script over `.env`: the airgap bundler's first version
emptied only the SECRETS block and shipped all eight of those. It now matches by
name as well as position.

---

## 10. People, gateway and dashboards

| variable | default | what it does |
|---|---|---|
| `LITELLM_DEFAULT_USER_BUDGET` | `50` | default monthly credit per person, in USD. `0` would **block** every request rather than mean unlimited, which is why the default is a real number |
| `LITELLM_BUDGET_DURATION` | `1mo` | the window that ceiling applies to |
| `LITELLM_WORKERS` | `1` | LiteLLM worker processes |
| `WEBUI_DEFAULT_ROLE` | `user` | what a newly signed-in person gets. Never `admin`: behind SSO anyone who can authenticate would otherwise self-promote |
| `WEBUI_BACKEND_URL` | `http://identity-proxy:8080/v1` | where Open WebUI sends completions |
| `WEBUI_BACKEND_KEY` | `${LITELLM_MASTER_KEY}` | the **single shared key** Open WebUI uses. Because it is shared, per-person attribution depends on the forwarded email header, and enforcement depends on `identity-proxy` |
| `PROTECTED_CHAIN` | `sso-chain@file` | the middleware chain on the unauthenticated internals. Use `protected-chain@file` when the `auth` profile is off |
| `PROMETHEUS_RETENTION_TIME` | `30d` | how long metrics are kept |
| `PROMETHEUS_RETENTION_SIZE` | `20GB` | and how much disk they may take. Whichever hits first |
| `ADMIN_GROUP` | `admins` | the group name the app gives admins in OIDC and forwardAuth; Open WebUI maps it to its admin role |
| `HF_TOKEN` | empty | only needed for gated Hugging Face repositories |

---

## 11. Argus

| variable | default | what it does |
|---|---|---|
| `ARGUS_VERSION` | `latest` | the image tag |
| `ARGUS_GITLAB_URL` | `config/argus/config.yaml`'s value | **which GitLab.** Overrides `url:` in the committed config file, so this is the line to change |
| `ARGUS_GITLAB_TOKEN` | — | read-only service token: `read_api` + `read_repository`, Reporter or above in every project to index. No admin, no sudo |
| `ARGUS_GITLAB_AUTH` | inferred | `token` or `password`. Inferred when empty: a token alone means token mode, a username alone means password mode. **A username wins over a token**, so set this explicitly if a username is left behind from an earlier experiment |
| `ARGUS_GITLAB_USERNAME` | — | password mode only: the account Argus signs in as |
| `ARGUS_GITLAB_PASSWORD` | — | password mode only. Never read from `config/argus/config.yaml` — a `password` key there is refused outright rather than ignored |
| `ARGUS_GITLAB_CA_CERT` | empty | path **inside the container** to the CA that signed GitLab's certificate. Drop the PEM in `config/argus/tls/` and use `/etc/argus/tls/<name>` |
| `ARGUS_GITLAB_VERIFY` | empty | `false` disables certificate verification. Last resort for a self-signed GitLab with no CA file anywhere. Setting it **and** `ARGUS_GITLAB_CA_CERT` is refused at startup |
| `ARGUS_EMBED_MODEL` | `nomic-embed-text` | Ollama model for query embeddings |
| `ARGUS_EMBED_DIM` | `768` | its output dimension. Must match the model, or the pack vectors are unusable |
| `ARGUS_OLLAMA_URL` | `http://ollama:11434` | without this Argus falls back to `localhost:11434`, which inside a container is the container itself |
| `ARGUS_INDEX_INTERVAL` | `900` | seconds between automatic index passes. **`0` turns automatic reindexing off**, and then the index only advances when somebody presses **Index now** under **Admin → Indexing** |
| `ARGUS_INDEX_STALE_AFTER` | `3600` | seconds without a successful pass before a repository counts as stale. Feeds `argus_index_stale`, the `ArgusIndexStale` alert and the number on **Admin → Overview**. Default is 4 × the interval above |
| `ARGUS_WEBHOOK_TOKEN` | empty | the GitLab push webhook's secret. **Empty means the webhook route does not exist at all.** Set it here and put the same value in GitLab's webhook configuration; see [ARGUS.md](argus/README.md#indexing-on-push). Deliberately not the admin token — this one is stored in GitLab, so it is the lower-privilege credential |

`ARGUS_INDEX_INTERVAL` is the setting that makes the rest of the freshness
story work. Before it existed the stack had alert rules, a stale metric and a
button, and nothing that ran a pass on a timer: `argus_index_stale` was `1` on
every healthy deployment, and an alert that is always firing is an alert
nobody reads. The timer runs **inside the `argus` serve process**, not as a
second container, because two processes writing one SQLite index would fail
each other with `database is locked`. Raise
`ARGUS_INDEX_STALE_AFTER` on an estate where a full pass legitimately takes
longer than the interval.

Both TLS settings apply to the API **and** to every `git clone`, because Argus
reaches GitLab over two transports that share no TLS configuration. Configuring
one and not the other is the failure that looks like a bad credential:
enumeration succeeds, and the first clone dies with `server certificate
verification failed`.

---

## 11a. Chat tools: Python and the web (`sandbox`, `websearch` profiles)

| variable | default | what it does |
|---|---|---|
| `SANDBOX_SLOTS` | `2` | Python runs at once; more wait for a free slot |
| `SANDBOX_JOB_MEMORY_MB` | `1536` | memory one run may use (its address space); more is a `MemoryError` |
| `SANDBOX_MEMORY` | `3g` | the sandbox container's memory, all runs together |
| `SANDBOX_CPUS` | `2` | its CPUs; a run uses one |
| `SEARXNG_SECRET` | *(secret)* | the search engine's key (§9) |

What the tools may do is set live in the app, not here (Settings → Python and
web): the longest Python run (60 s), the sites the Web tool may open (none
until set; `*` for any public site), and another SearXNG to search with.
The Web tool is off until an admin turns it on in Admin → Tools.

The sandbox image is built on the first `up` (Python packages from PyPI,
about 100 MB); `scripts/sandbox-check.py` tests its isolation against the
running container. Air-gapped, leave `websearch` off; `sandbox` works offline
once its image is in the bundle.

---

## 12. Tracing (`tracing` profile)

Langfuse keeps request traces, backed by ClickHouse (the traces) and MinIO (the
blobs), on the same Postgres as LiteLLM.

| variable | default | what it does |
|---|---|---|
| `LANGFUSE_SALT` | — | hashes user identifiers in traces |
| `LANGFUSE_ENCRYPTION_KEY` | — | encrypts stored API keys. Needs 32 bytes (64 hex characters) |
| `LANGFUSE_NEXTAUTH_SECRET` | — | Langfuse's session secret |
| `LANGFUSE_OIDC_CLIENT_SECRET` | — | its OIDC client |
| `CLICKHOUSE_PASSWORD` | defaults to `LLM_PG_PASSWORD` | ClickHouse's password |
| `MINIO_ROOT_PASSWORD` | — | MinIO's password |
| `LLM_PG_USER` | `llmservice` | the Postgres user, shared by LiteLLM, Langfuse and Argus |

---

## 13. Backup

`scripts/backup.sh` writes everything a restore needs into one dated directory.
Every setting lives in `.env`:

| variable | default | what it does |
|---|---|---|
| `BACKUP_DIR` | `./backups` | where backups go. **Put it on a different disk from the data it protects** |
| `BACKUP_COPY_DIR` | empty | a second, verified copy of every good backup, on **another physical disk**. Empty means none — and then one failed disk takes the data and every backup of it |
| `BACKUP_KEEP` | `14` | how many to keep. Older ones are removed *after* a good backup, never before |
| `BACKUP_INCLUDE_LOGS` | `1` | also archive Loki's logs and Prometheus's metrics. Off is much smaller and loses the history |
| `BACKUP_TIME` | `03:30` | when the systemd timer runs it (`HH:MM`, or any `OnCalendar` value). Installed by `sudo ./scripts/backup.sh --install-timer` |

One backup is one directory, `BACKUP_DIR/<date>_<time>/`:

* `postgres.sql.gz` — a `pg_dumpall` of the gateway database (people, API keys,
  budgets, spend), taken **from the running server** so it is consistent.
* `volumes/<name>.tar.gz` — every other named volume. SQLite databases inside
  them (chat history, the Argus index and audit) are
  copied through SQLite's own online backup, so a write in progress cannot tear
  them.
* `config/` — `.env`, every compose file in `COMPOSE_FILE`, and `config/`.

`--list` shows what is present, `--verify [DIR]` checks one against its
checksums, and `--restore --from DIR` puts the volumes and the database back
(with the stack stopped); `--with-config` also restores `.env` and `config/`.

---

## 14. Values compose derives — not in `.env`

Do not add these to `.env`; they are composed from the ones above and would be
overwritten in meaning if you set them independently.

| derived | from |
|---|---|
| `DATABASE_URL` | `LLM_PG_USER`, `LLM_PG_PASSWORD` → `postgres:5432/litellm` (and `/langfuse`) |
| `REDIS_HOST` / `REDIS_PORT` | the `redis` service |
| `ENGINE_MODEL` | `openai/${MODEL_NAME}` unless overridden |
| `ENGINE_API_KEY` | `LLAMACPP_API_KEY` unless overridden |
| `STORE_MODEL_IN_DB` | `"True"` |
| `OLLAMA_KEEP_ALIVE` | `-1` — keep the embedding model resident |
| `LHM_URL` | `http://host.docker.internal:8085/data.json` — one of three sources `cpu-temp-exporter` tries, in order: Linux `/sys/class/hwmon`, then LibreHardwareMonitor, then ACPI thermal zones. It exists because node-exporter's `node_hwmon_temp_celsius` has **zero series** on a Windows/WSL2 host — the kernel exposes no thermal sensors — and `windows_exporter` has no core-temperature collector at all |
| `COMPOSE_PROJECT_NAME` (in Traefik) | `@COMPOSE_PROJECT_NAME@` in `traefik.yml`, replaced by `sed` at startup |
| `POSTGRES_USER` / `POSTGRES_DB` | `LLM_PG_USER` / `llmservice` |
| `OAUTH_ADMIN_ROLES` | `ADMIN_GROUP` (default `admins`) — the app's group that becomes an Open WebUI admin |
| `OAUTH_ALLOWED_ROLES` | `*` — anyone the app signs in may use chat; authorisation comes from the group claim |
| `OFFLINE_MODE`, `CHECKPOINT_DISABLE`, `LITELLM_LOCAL_MODEL_COST_MAP` | LiteLLM does not phone home and uses its bundled model cost map instead of fetching one |

---

## 15. Profiles

`COMPOSE_PROFILES` is a comma-separated list. See
[ARCHITECTURE.md §3](architecture.md#3-profiles-what-is-running-and-why-it-is-opt-in)
for what each brings up; this is the short reference:

| profile | brings up |
|---|---|
| *(none needed)* | `tls-init`, `prometheus-secrets`, `prometheus`, `alertmanager`, `node-exporter`, `power-limits`, `open-webui` |
| `proxy` | `traefik` |
| `auth` | `auth-init`, `redis` |
| `gateway` | `app`, `litellm`, `postgres`, `redis`, `identity-proxy` |
| `llamacpp` | `llamacpp`, `model-init` |
| `vllm` | `vllm` |
| `multi-model` | `vllm-secondary` |
| `argus` | `argus`, `ollama` |
| `embed` | `ollama` |
| `image` | `imagegen` (picture generation; see §7) |
| `sandbox` | `sandbox` (the chat's Python; see §11a) |
| `websearch` | `searxng` (search for the chat's Web tool; see §11a) |
| `smi` | `nvidia-smi-exporter`, `cpu-temp-exporter` |
| `dcgm` | `dcgm-exporter` |
| `cadvisor` | `cadvisor` |
| `logging` | `loki`, `promtail` — **on by default**, because it is what makes logs searchable at all |
| `tracing` | `langfuse`, `langfuse-worker`, `clickhouse`, `minio`, `postgres`, `redis` |

`llamacpp` and `vllm` are mutually exclusive in practice: both want the GPU.

`logging` ships in the default `COMPOSE_PROFILES`, so a fresh deployment
collects logs without opting in. Remove it from that list to turn collection
off; nothing else depends on it. Two things make it work, and both are worth
knowing before something looks broken:

- **Promtail reads `HOST_DOCKER_DIR`**, which defaults to `/var/lib/docker`. A
  rootless daemon or a custom `data-root` collects nothing until that path is
  corrected. Preflight checks the mount, so a wrong path is caught before `up`
  rather than showing up as an empty Logs page later.
- **Promtail collects only this compose project's containers.** A second stack
  on the same daemon — the bundled test GitLab, for instance — does not end up
  in this deployment's Loki.

Running **without** `proxy` is supported but means reaching services by their
internal names; without `auth`, `PROTECTED_CHAIN` must be set to
`protected-chain@file`.

---

## 16. Files under `config/`

Everything here is committed and travels with the repository. **Nothing needs
editing for a normal deployment** — the `.env` covers it — but each file is the
place to go for behaviour the `.env` does not expose.

### Edited by hand, only if you want to

| file | what it configures |
|---|---|
| `traefik/traefik.yml` | static config: entrypoints, the Docker and file providers, the project-scoping constraint, Prometheus metrics, access-log filters. **Cannot read the environment** — `@COMPOSE_PROJECT_NAME@` is substituted at startup |
| `traefik/dynamic/middlewares.yml` | the middleware chains: `internal-auth` (basic auth), `security-headers`, `compress` (which never compresses SSE), `default-chain`, `protected-chain`, `app-auth` (forwardAuth to the app), `sso-chain` |
| `traefik/dynamic/tls.yml` | the certificate store and TLS options: minimum version TLS 1.2, and a restricted cipher list |
| `litellm/config.yaml` | the model list and its advertised window, router retries and timeout, the default per-person budget, the Redis cache policy (`mode: default_off`), and `user_header_mappings` — the mapping that makes chat spend and API spend one number |
| `prometheus/prometheus.yml` | the 14 scrape jobs and their intervals |
| `prometheus/rules/*.yml` | alerting rules: `hardware.yml` (9), `llm.yml` (7), `stack.yml` (5), `argus.yml` (4). `slo.yml` holds two more written out but **commented off** — an SLO alert needs a target somebody agreed to, and shipping guesses produces alarms nobody owns |
| `alertmanager/alertmanager.yml` | routing and receivers. **Out of the box everything routes to the `null` receiver**: alerts are visible on the app's Alerts page (and in Prometheus and Alertmanager) and notified nowhere. Slack, SMTP and generic-webhook receivers are present but commented out, and read their secrets from files (`slack_api_url_file`, `auth_password_file`) so a real URL never lands in version control |
| `loki/loki-config.yml` | storage and retention for log aggregation — retention is **enabled**, at 336 h (14 days), with a 2 h delete delay |
| `promtail/promtail-config.yml` | which logs to collect; reads the Docker socket and container log files |
| `dashboards/*.json` | ten dashboards, in Grafana's JSON format, that the app draws (Observe → Dashboards): **LLM Overview**, **Usage by person**, **GPU Hardware**, **Resources (CPU, Memory, GPU)**, **Stack Health & Alerts**, **Stack Performance**, **Host & Containers**, **Logs**, **Argus** (audit events, query latency) and **Indexing** (index passes, per-repo outcomes and failures). A panel names its datasource by UID: `prometheus`, `loki` or `litellm-db` (the gateway's Postgres: the spend tables, because LiteLLM's `/metrics` is enterprise-only and vLLM's metrics have no user dimension). Edit a file and the next request uses it |
| `postgres/init/01-create-databases.sql` | creates the `litellm`, `langfuse` and `argus` databases and the `vector` extension. Runs **once**, only when `postgres-data` is empty |
| `argus/config.yaml` | container-side Argus config: the GitLab URL **as a default**, and where the index and packs live |
| `argus/tls/` | empty. Drop a private CA here and point `ARGUS_GITLAB_CA_CERT` at it |
| `ca/` | **stack-wide** extra CAs. `tls-init` appends every `.crt`/`.pem` here to `/certs/bundle.crt`, which the `argus` container verifies against (`SSL_CERT_FILE`, `GIT_SSL_CAINFO`) and every container that mounts `traefik-certs` can use. Only public certificates belong here, never a private key. The two mechanisms compose: a CA in `ca/` covers the whole stack, `ARGUS_GITLAB_CA_CERT` narrows it to one GitLab and wins when both are set |

### Generated — do not edit

| file | written by | when |
|---|---|---|
| `traefik/auth/users.htpasswd` | `auth-init` | every `up`, from `PROXY_AUTH_USER`/`PROXY_AUTH_PASSWORD` |
| `traefik/certs/tls.crt`, `bundle.crt` | `tls-init` | every `up`. Copies for host-side tools; the private key never leaves the volume |
| `prometheus/secrets/llamacpp.token` | `prometheus-secrets` | every `up` |
| `authelia/directory/users.yml` | the app | at start and on every change to a person. What Argus mounts (`config/authelia/directory` → `/authelia`): usernames, emails and display names only, never passwords. The directory keeps its old name so Argus needs no change; it is kept in the tree with a `.gitkeep` and only its contents are ignored |

### The `deploy/services/` directory

| path | what it is |
|---|---|
| `src/` (repository root) | the app (`Llm.Api`, `Llm.Core`), the web and Argus, built by compose from `LLM_SOURCE_DIR`. See [development.md](development.md) |
| `deploy/services/identity-proxy/` | turns Open WebUI's forwarded identity header into the `user` field LiteLLM enforces budgets against |
| `deploy/services/cpu-temp-exporter/` | a small exporter for CPU package temperature, which NVML does not report |
| `deploy/services/argus-local.yml` | an **override**: reuse an existing Argus index and pack estate instead of the named volume. Requires `ARGUS_HOME` |
| `tools/test-gitlab/` | a throwaway GitLab CE for verifying Argus end to end. Not for production |

---

## 17. Scripts and entry points

All paths are relative to `deploy/`.

| entry point | what it does |
|---|---|
| `make up` | `scripts/preflight.sh`, then `docker compose up -d` |
| `make preflight` | checks that every bind mount resolves to real content — catches a moved checkout before it becomes four unrelated crash loops |
| `make ca` | prints the environment exports that make **host** tools (`dsh`, `curl`, python, git) trust the stack certificate, installing nothing |
| `make airgap` | builds an offline bundle into `dist/` (§18) |
| `make down` / `make clean` | stop, keeping volumes / stop and delete every volume |
| `make health` | `scripts/health.sh` — every enabled component |
| `make smoke` | `scripts/smoke-test.sh` — end-to-end API verification |
| `make bench` | `scripts/benchmark.sh` — concurrency sweep |
| `make backup` | `scripts/backup.sh` — one complete, verified backup (§13). `--list`, `--verify`, `--restore`, `--install-timer` |
| `make logs S=<name>` | follow one service |
| `scripts/with-ca.sh <cmd>` | run one command with the stack's certificate trusted |
| `scripts/domain-check.sh` | verify every hostname routes and redirects correctly, without `/etc/hosts` entries |
| `scripts/install-requirements.sh` | check/install Docker, the NVIDIA container toolkit, and the rest |
| `scripts/setup-hosts.sh` | add the hostnames to `/etc/hosts` (only needed for a non-`.localhost` domain) |

The `.ps1` equivalents are for Windows hosts.

---

## 18. Airgap bundles

`scripts/airgap-bundle.sh` produces a self-contained zip; `load.sh` inside it
loads every image, restores Ollama's model volume, runs the preflight and
starts. The generated `AIRGAP-README.md` and `.env.airgap` in the bundle are the
authoritative instructions for a given build.

Only four things reach the network on a normal first start — container images,
the GGUF weights, `model-init`'s Hugging Face size check, and Ollama's embedding
model — and the bundle closes all four. That third one matters more than
it looks: `llamacpp` declares `model-init` as `service_completed_successfully`,
so a size check that cannot reach Hugging Face stops **the engine itself**
from starting, even with every weight already on disk.

`--with-env` includes the live `.env`
verbatim (secrets and all); without it the shipped `.env.airgap` has every
secret emptied, matched by name as well as by position.

---

## 19. Env samples

`env-samples/` holds complete deployments for specific model/card combinations.
Each file's header records the hardware, the first-start download size, and what
was measured on it.

| sample | model | card | first-start download |
|---|---|---|---|
| `qwen3.8-flash-next.rtx5090.env` | Qwen3.8-Flash-Next, 177B MoE, UD-IQ4_XS | RTX 5090 32 GB, 64 GB+ RAM | 94.1 GB |
| `qwen3.8-27b.rtx5090.env` | Qwen3.8-27B, dense, NVFP4 with MTP | RTX 5090 32 GB | 17.1 GB |

Both are **measured**, on an i7-14700K with 123 GB RAM and driver 595, and each
file's header carries the numbers. The samples are RTX 5090 only: the 3090
configurations were dropped, and the measurements taken on that machine are
kept in the top-level README under a heading that says so.

Adding one for another model or card is a single file: copy the closest sample,
edit its header and its MODEL block, and check it.
[env-samples/README.md](../deploy/env-samples/README.md) has the step-by-step.
