# Architecture: every part of the stack, and how they fit

What runs, why it is there, and how a request travels through it. Setting
values: [configuration.md](configuration.md). Deploying: [deployment.md](deployment.md).
The commands: [scripts.md](scripts.md).

## 1. The shape

With compose: one host, one way in. Only Traefik publishes ports:
`HTTP_PORT` and `HTTPS_PORT`, 80 and 443 by default (8080 and 8443 on
rootless Podman, where ports below 1024 need root). Every other service is
reached by its name inside the stack, except the four with no network: the
app hands `sandbox`, `blender` and `lean` their jobs through a volume each,
and nothing reaches `power-limits`.

```
 browsers       API clients, agents      Code Arena on people's machines
     │                   │                             │
     └───────────────────┴─────────────┬───────────────┘
                                       │  HTTP_PORT and HTTPS_PORT, nothing else
                               ┌───────▼───────┐
                               │   traefik     │  TLS and routes
                               └──┬────┬────┬──┘
                DOMAIN            │    │    │     argus.DOMAIN
          ┌───────────────────────┘    │    └──────────────────┐
     ┌────▼────┐ DOMAIN/api, /mcp ...  │                ┌──────▼───────┐
     │   web   │ and every path of     │                │ argus        │
     └─────────┘ gateway.DOMAIN        │                │ scanners [s] │
                               ┌───────▼───────┐        │              │
                               │ app           ├───────▶│              │
                               │ postgres      │        │              │
                               │ valkey [ha]   │◀───────┤              │
                               └──┬────┬────┬──┘        └─────────┬────┘
                                  │    │    │                     │
         ┌────────────────────────┘    │    └────────┐            │ read only
┌────────▼──────────┐ ┌────────────────▼──┐ ┌────────▼──────────┐ ▼
│ litellm           │ │ sandbox [n]       │ │ prometheus        │ your GitLab
│   │               │ │ blender [n]       │ │ alertmanager      │
│   ▼               │ │ lean [n]          │ │ loki, promtail    │
│ llamacpp          │ │ browser [b]       │ │ node-exporter     │
│ vllm, sglang      │ │ searxng           │ │ gpu-exporter      │
│ imagegen          │ │ laya [l]          │ │ dcgm-exporter     │
│ videogen, audio   │ │ studio [t]        │ │ cpu-temp-exporter │
│ embed             │ │ workspaces [w]    │ │ power-limits [n]  │
│ other GPU servers │ │                   │ │                   │
└───────────────────┘ └───────────────────┘ └───────────────────┘
 the gateway and       the tools' sandboxes  monitoring
 the engines           and studio servers

 [n]  no network at all                [b]  out only through the app's proxy
 [l] [t]  a network with the app only  [s]  a network with Argus only
 [w]  the app, Argus and the internet  [ha] only with the ha feature or ha.yml
```

The app calls Argus as the chat's MCP server, and Argus asks the app whose an
API key is. Argus also embeds through `embed`. The app's chat calls, and the
chat's pictures, speech and transcripts, go through the gateway. The app
calls `embed` directly for embeddings, the picture, video and studio servers
directly for the media studio, the chat's video and the meetings' speakers,
and the engines directly only to manage them (load, unload, slots, health).

| group | services | what it does |
|---|---|---|
| the way in | `traefik` | TLS (Let's Encrypt, a certificate of its own, or yours), routes by host and path, security headers and compression |
| the web and the app | `web`, `app` | the React app; the .NET app and API: sign-in, chat, tools, admin, dashboards, and the way in for every model request |
| the stores | `postgres`, `valkey` | the app's database (`llmapp`: people, chats, settings, the ledger, the keys) and the gateway's (`litellm`); Valkey only with the `ha` feature or `ha.yml` |
| the gateway and the engines | `litellm`; `llamacpp`, `vllm`, `sglang`; `imagegen`, `videogen`, `audio`, `embed` | the models: chat models on llama.cpp, vLLM or SGLang, pictures and video on stable-diffusion.cpp, speech on speaches, embeddings on the CPU; other GPU servers the app does not run are reached the same way |
| Argus | `argus`, `scanners` | the code index of your GitLab (read only), knowledge packs and security advisories, over MCP; the scanners for its security reviews |
| the tools | `sandbox`, `blender`, `lean`, `browser`, `searxng`, `laya`, `studio`, `workspaces` | Python and commands, 3D, proofs, the browser agent, web search, typed decisions, the media studio's own models, Code Studio's workspaces |
| monitoring | `prometheus`, `alertmanager`, `loki`, `promtail`, the exporters, `power-limits` | what the app's dashboards, logs and alerts read; power caps kept applied |
| off the host | Code Arena, your GitLab, GitHub and Gitea, other GPU servers, Hugging Face, the company's knowledge sources | reached by the app or Argus, by LiteLLM (other GPU servers), by the speech server (Hugging Face) and by Code Studio's workspaces (the code hosts, package registries), or reaching in through Traefik |

A broken Traefik is a total outage. A broken Prometheus is not. Argus alone,
without the platform, is a compose of its own in `deploy/argus-standalone`
([argus/standalone/architecture.md](argus/standalone/architecture.md)): a
host runs one or the other.

### Where LiteLLM sits today

Every request to LiteLLM reaches the app first, an API key's too. Only
Argus's embeddings go past the app, straight to `embed`. Behind the app,
LiteLLM is still the model gateway (`litellm`, port 4000 inside, its own
database `litellm`, its settings in `config/litellm.yaml`). Today it:

- holds the route table: the app registers its chat, picture and speech
  models at it (not video models or the embedder), each with where it is
  answered and its price;
- checks every API key, its models and its rate limits, and an own key's
  budget;
- asks the app's guardrail before each call (safeguards, API access, credit,
  room in the engine), and refuses when the app does not answer;
- picks the least busy copy of a model, retries, and sends the call on;
- turns Anthropic's messages and OpenAI's Responses into the chat
  completions the engines speak;
- writes its own request log, which the app copies into its ledger.

The app's own gateway is replacing it step by step
([design/own-gateway.md](design/own-gateway.md)). Done so far: the app keeps
its own ledger of every model request, the usage pages and dashboards read
it, and the app makes and keeps the API keys. Next come the app's own calls,
then API keys' requests, the media, the model list and the other protocols.
LiteLLM leaves the stack last. Until then some things are kept in both
places (the keys, the request log), and this page says which part does what
today.

## 2. Hostnames and paths

`DOMAIN` in `.env` names three addresses. Traefik sends each where this table
says. Nothing else is published: Prometheus, Alertmanager, Loki, the
exporters, LiteLLM and the engines are reached only inside the stack, and the
app shows their data to admins.

| address | goes to | what |
|---|---|---|
| `https://DOMAIN` | `web:8080` | the React app |
| `DOMAIN/api`, `/connect`, `/.well-known`, `/scim` | `app:8080` | the API, the OIDC issuer, discovery, SCIM |
| `DOMAIN/mcp`, `/mcp/code` | `app:8080` | [Arena MCP](mcp.md): each person's chat tools for their agent |
| `https://gateway.DOMAIN`, every path | `app:8080` | the model API for API keys (OpenAI and Anthropic protocols) |
| `https://argus.DOMAIN` | `argus:7700` | Argus's MCP (`/mcp`) for coding agents |

`gateway.DOMAIN` goes to the app alone, never straight to LiteLLM. While the
app is down, it is down too. The app answers these paths there (each also
without `/v1`):

| paths | what the app does | then |
|---|---|---|
| `/v1/chat/completions` | the key's line, the answer cache (when on), the prompt log, the model's line with the chat's answers, the session's engine slot | LiteLLM, with the caller's key |
| `/v1/messages`, `/v1/responses`, `/v1/completions` | the key's line; not cached | LiteLLM, which translates Anthropic's and the Responses protocol |
| `/v1/audio/speech`, `/v1/audio/transcriptions`, `/v1/images/generations` | the person's voice and language; the picture credit, the picture model loaded first | LiteLLM, with the caller's key |
| `/v1/models`, `/v1/models/{id}`, `/v1/model/info`, `/v1/embeddings`, `/v1/moderations`, `/v1/rerank`, `/v2/rerank`, `/v1/messages/count_tokens`, `/v1/audio/translations`, `/v1/images/edits`, `/v1/images/variations` | passed on as they came | LiteLLM, with the caller's key |
| anything else | the app's other paths answer here too: those it answers on `DOMAIN` (`/api`, `/connect`, `/.well-known`, `/scim`, `/mcp`), and `/healthz`, `/readyz` and `/internal`, which `DOMAIN` sends to the web. Every other path is a plain 404, so LiteLLM's management API and admin pages are not found | |

The app's one `/internal` path is LiteLLM's guardrail callback,
`/internal/guardrail/beta/litellm_basic_guardrail_api`: LiteLLM is given
`http://app:8080/internal/guardrail` and adds the rest (`/internal/guardrail`
itself is a plain 404). The callback answers only to the master key
(`GATEWAY_KEY`). Inside the stack it is reached at `app:8080`. From outside, `gateway.DOMAIN`
reaches it too, because it sends every path to the app (`DOMAIN` does not).

The default models have no internal path. An admin starts their download in
Admin → Models → Default models. The installer's `--default-models download`
writes `DEFAULT_MODELS=download` into `.env`, and the leader then starts the
download once, at the app's first start.

Certificates: with nothing set, Traefik serves its own. `ACME_EMAIL` gets one
from Let's Encrypt for the three names. `make-cert.sh` makes a CA of the
deployment's own and a certificate for `DOMAIN`, `gateway.DOMAIN`,
`argus.DOMAIN` and `*.DOMAIN`, or installs a company's
([deployment.md](deployment.md#certificates)). TLS is 1.2 or newer with
forward-secret AEAD ciphers only. HSTS is `HSTS_SECONDS` when it is set.
Otherwise it is a year with Let's Encrypt, and none with any other
certificate.

## 3. Features

The core always runs: `traefik`, `web`, `app`, `postgres` and `litellm`.
Everything else is a feature, listed in `deploy/features` with the services it
runs, whether a new installation has it on, what the host needs for it and the
features it requires.

- **`COMPOSE_PROFILES` in `.env` is the list of the features that are on.**
  Each optional service runs under its feature's compose profile, so a feature
  that is off runs nothing and needs no image.
- **The app reads the same list** (`Features__On`). A feature that is off is
  gone from the menus, its pages say it is not installed, and its background
  work does not run. The API of the app's own features (`lab`, `decks`,
  `data`, `meetings`, `media`, `agents`, `apps`, `code-studio`) and of
  `monitoring` answers 404 too. A feature counts as on only when the
  features it requires are on too. An app given no list takes every feature
  as on.
- **To turn one on or off**: `./arenactl install`, `upgrade` or `repair` with
  `--with A,B` or `--without A,B` (the installer's own `--with` and
  `--without` are the same), and `./arenactl features` lists them. By hand:
  change `COMPOSE_PROFILES`, `docker compose up -d`, and `docker compose rm
  -sf SERVICE` for each service of a feature turned off.

| feature | on when new | runs | adds |
|---|---|---|---|
| `chat-models` | yes | `llamacpp` | chat models on this host's GPUs (llama.cpp) |
| `vllm`, `sglang` | no | `vllm`, `sglang` | the vLLM and SGLang engines, for many people at once |
| `knowledge` | yes | `embed` | company knowledge, and search by meaning |
| `argus` | yes | `argus` | the code index of your GitLab, and knowledge packs |
| `pictures`, `video` | yes | `imagegen`, `videogen` | pictures and video on the GPU |
| `speech` | yes | `audio` | speech to text and text to speech |
| `sandbox` | yes | `sandbox` | Python and commands for the chat's tools |
| `web-search` | yes | `searxng` | the chat's web search |
| `browser` | no | `browser` | the browser agent (Chromium) |
| `3d` | no | `blender` | 3D models |
| `lean` | no | `lean` | proofs checked by Lean 4 and Mathlib (requires `lab`) |
| `studio` | no | `studio` | the media studio's own models: cutouts, voices, music (requires `media`) |
| `workspaces` | no | `workspaces` | Code Studio's workspaces on this server (requires `code-studio`) |
| `laya` | no | `laya` | typed decisions on the CPU |
| `scanners` | no | `scanners` | security scanners for code reviews (requires `argus`) |
| `monitoring` | yes | `prometheus`, `alertmanager`, `loki`, `promtail`, `node-exporter`, `cpu-temp-exporter` | dashboards, logs and alerts |
| `gpu-monitoring`, `dcgm` | yes, no | `gpu-exporter`, `dcgm-exporter` | the GPUs' metrics; data-centre GPU detail on bare metal |
| `power-limits` | yes | `power-limits` | GPU and CPU power caps kept applied |
| `ha` | no | `valkey` | the store for the shared counts of LiteLLM's replicas under `ha.yml`, and for LMCache when `LMCACHE_REMOTE` names it |
| `meetings`, `lab`, `apps`, `data`, `decks`, `media`, `agents`, `code-studio` | yes | nothing: parts of the app | meetings, the science lab, apps, ask your data, decks, the media studio, Agent Studio, Code Studio |

`--cpu-only` turns the GPU features off. Argus cannot start without a GitLab.
Without `--gitlab-url` and a token, the installer leaves it out. To turn it
on later, put `GITLAB_URL` and `GITLAB_TOKEN` in `.env` and run
`./arenactl repair --with argus`.

**Compose files on top.** Each is named after `docker-compose.yml` with `-f`:

| file | what it changes |
|---|---|
| `scale.yml` | the app as `APP_REPLICAS` replicas ([section 9](#9-several-app-replicas)) |
| `ha.yml` | Valkey on, and LiteLLM as `LITELLM_REPLICAS` replicas sharing their counts in it |
| `podman.yml` | rootless Podman: GPUs through CDI, Promtail on the Podman socket, no power limits |
| `rocm.yml` | AMD GPUs (`/dev/kfd`, `/dev/dri`): llama.cpp's and SGLang's ROCm images, stable-diffusion.cpp's Vulkan image for the picture and video servers, and a ROCm vLLM of yours in `VLLM_IMAGE`; the app's backend set to `rocm`; no `nvidia-smi` exporter |
| `fake-gpu.yml` | the real vLLM and SGLang supervisor over a fake engine, with no GPU |
| `pitr.yml` | Postgres with pgBackRest's WAL archive, for point-in-time recovery |
| `vllm-build.yml`, `sglang-build.yml` | vLLM's or SGLang's image built here, for a host that cannot pull theirs |

## 4. Network and volumes

**Networks.** Most services share compose's default network and reach each
other by service name (`http://app:8080`). Five are kept apart:

| network | who is on it | way out |
|---|---|---|
| `laya` | the app, `laya` | none (internal) |
| `studio` | the app, `studio` | none (internal) |
| `browsing` | the app, `browser` | none of its own: the browser's only way out is the app's proxy on port 3128, which opens only the public sites an admin allows, on ports 80 and 443 |
| `scanning` | `argus`, `scanners` | none (internal) |
| `workspaces` | the app, `argus`, `workspaces` | the internet (a workspace clones, pushes and installs packages); no database, gateway, engine or browser is on it |

`sandbox`, `blender`, `lean` and `power-limits` have no network at all
(`network_mode: none`): the app hands the first three their jobs through
their volumes. `argus` also reaches the host (`host.docker.internal`), for a
GitLab on the same machine. `cpu-temp-exporter` reads the host's sensors from
`/sys` on Linux; on Windows it asks LibreHardwareMonitor at
`http://host.docker.internal:8085/data.json`, the exporter's `LHM_URL`
default. The compose file passes no `LHM_URL` from `.env`, so another address
goes in `docker-compose.override.yml`, in the service's `environment`
([cpu-temperature.md](cpu-temperature.md)).

**Volumes.** Each lives in a folder of `DATA_DIR` on the host (an absolute
folder, required), one per volume. The exceptions are `media-status` and
`vllm-status`, which are in memory, and `valkey`, a plain volume. The
installer makes the folders with their services' owners.

| volume | written by | holds |
|---|---|---|
| `postgres` | Postgres | both databases |
| `engine` | the app | the engines' presets and desired state, the media servers' control files, Prometheus's engine targets |
| `directory` | the app | the people Argus reads (`users.yml`: user name, display name, email) |
| `audit` | the app | the audit log's daily anchors |
| `code-arena-signature` | `sign-code-arena.sh` | the signature of the Code Arena builds the app hands out |
| `sandbox`, `blender`, `lean` | the app and those services | jobs in and results out |
| `workspaces` | `workspaces` | each workspace's clone, its engine's home, what is not pushed yet |
| `argus` | Argus | its index |
| `audio` | the speech server | its models |
| `prometheus`, `alertmanager`, `loki` | those services | metrics (30 days or 20 GB), silences, logs (90 days) |
| `acme` | Traefik | Let's Encrypt's certificates |
| `power-limits` | `power-limits` | which GPUs it capped, and the CPU's original limits, so that emptying a setting puts them back |
| `media-status`, `vllm-status` | the media servers, the vLLM and SGLang supervisor | what they do, every 2 seconds; in memory |
| `valkey` | Valkey | its counts (a plain volume) |

Folders of the host mounted as they are:

- **`MODELS_DIR`**, the model library: written by the app, read by the model
  servers (none of them fetches a model itself). The speech server is the exception:
  it keeps its models in `audio`. With Docker the library must be writable by
  uid 1000, the app's user. On rootless Podman the app runs as your own user.
- **`KV_CACHE_DIR`** (`DATA_DIR/kvcache` when empty): llama.cpp's saved
  conversation caches. vLLM's LMCache disk tier is `KVCACHE_DIR`
  (`./kvcache` by default).
- **`BACKUP_DIR`**: read only to the app, for Admin → Storage.
- **`ARGUS_PACK_LIBRARY_DIR`** (`../packs`): read only to Argus.

`backup.sh` dumps the whole Postgres cluster (both databases) and archives
the volumes, except `audio`, `acme`, `sandbox`, `blender`, `lean`,
`media-status`, `vllm-status` and `valkey`. Models are not backed up
([deployment.md](deployment.md#backups-and-restore)).

## 5. Engines and GPUs

The app drives the chat engines through one interface (`IEngineBackend`:
list, load, unload, sleep, capacity, health), and the media servers through
their control files or their own APIs. None is driven through a Docker socket
or the Kubernetes API. Each chat model goes to the engine that lists it, else
to llama.cpp's router.

| engine | service | how the app drives it | what it is for |
|---|---|---|---|
| llama.cpp | `llamacpp` | its router's HTTP API (load, unload, slots), and the presets it writes into `engine`: `models.ini`, `keep`, `max`, `kvcache`. `services/llamacpp/router.sh` restarts llama-server when the list or the count changes | GGUF files; layers or experts in RAM when the GPU is short; a few people at once; each conversation kept in its slot |
| vLLM, SGLang | `vllm`, `sglang` | files: the app writes the models each may run (`presets.json`) and the ones it wants (`models.json`) into `engine`; `services/vllm/supervisor.sh` runs one server per model on that model's GPUs, drains one taken away, sleeps and wakes them, and writes their status and a heartbeat into `vllm-status`. Load is read from each server's `/metrics` | safetensors snapshots; tensor parallel; copies of a model; a prefix cache shared by every request; many people at once |
| other GPU servers | none here | registered at the gateway and checked, never loaded or unloaded (Admin → Models → Other servers and providers; their keys kept encrypted) | another vLLM, SGLang, TGI, Ollama, or a cloud |
| pictures, video | `imagegen`, `videogen` | a control file each in `engine` (on or off, the model, the GPU); `services/sd-serve.sh` follows it and writes its status into `media-status` | stable-diffusion.cpp |
| speech | `audio` | its own API: models loaded and unloaded | speaches: Whisper, Kokoro |
| studio | `studio` | its own API; one model at a time, unloaded when idle | cutouts, voices from a sample, music, segments |
| embeddings | `embed` | nothing: it waits for its file | `nomic-embed-text`, on the CPU |

- **Keys.** The engines take `ENGINE_KEY` from their environment, never from
  their command line. The one exception is in `router.sh`: each time it
  starts llama-server, its curl calls that load the kept models and wait for
  them carry the key in a header on their command line, where `ps` in the
  `llamacpp` container can show it.
- **Placement.** `GpuPlan` is the one owner of placement: everything resident
  on every GPU (llama.cpp models spread over theirs, vLLM and SGLang groups at
  their share, the picture server and the embedder where each runs), for
  whatever machine there is: no GPU, one, or many of any vendor. Admin →
  Models shows it, and a load that does not fit is refused. Room for a model
  is made only among the models on its GPUs. A picture job borrows only its
  server's GPU, and vLLM models there sleep for it.
- **Media models** are managed like the engine's: loaded when asked for,
  unloaded after ten idle minutes by default. The leader writes the control
  files.
- **The GPUs are read** by the app from Prometheus (the `nvidia-smi` and node
  exporters). Else it reads the hardware file the vLLM and SGLang supervisor
  writes with `services/gpu-probe.py` (`nvidia-smi` or `rocm-smi`), there
  only while vLLM or SGLang runs. With `LLAMACPP_BACKEND=cpu` it reads the
  RAM alone.
- **Backends.** `LLAMACPP_BACKEND` (`cuda`, `rocm`, `vulkan`, `sycl`, `metal`
  or `cpu`) tells the app which backend llama.cpp's image runs on, so that it
  names the GPUs in the presets (`CUDA0`, `ROCm0` ...). It does not choose the
  image: that is `server-cuda` unless `rocm.yml` or the override changes it.
  vLLM and SGLang run on CUDA or ROCm.
- **Several machines**: one engine entry per vLLM or SGLang machine on storage
  both machines see, or a Helm instance per node
  ([deployment.md](deployment.md#engines-and-gpus),
  [multi-gpu-server.md](multi-gpu-server.md)).

## 6. Authentication and keys

One issuer, the app. More in [authentication.md](authentication.md).

### People

- **Sign-in** is at the app: its own accounts (10 failures lock one for 15
  minutes), LDAP or Active Directory (checked at each password sign-in; the
  leader disables people the directory no longer has), and company sign-in by
  OIDC or SAML 2.0 (made at first sign-in, or matched by email). SCIM 2.0 at
  `/scim/v2` makes, changes and deactivates people and groups.
- **Two-factor** is off, for admins, or for everyone, and groups can require
  it. A person it covers with no second factor may only set one up.
- **The session** is the cookie `llm_session` on the parent domain, so one
  sign-in covers every subdomain: HttpOnly, Secure, SameSite Lax, 1 hour idle,
  12 hours at most, or 30 days with **Keep me signed in on this device**
  (`Auth:RememberMe`). It is checked again every minute, so disabling a
  person or resetting a password ends their sessions within a minute.
- **The OIDC issuer** is the app (`/connect/authorize`, `/connect/token`,
  `/connect/userinfo`, `/connect/logout`): authorization code, refresh and
  client credentials, access tokens for an hour. The clients are exactly the
  configured ones.
- **The key ring**, which protects sessions, two-factor and the OIDC keys
  (kept in the settings), is kept in the database, encrypted under a key
  derived from `APP_KEY`.

### API keys

- **The app makes every person's key**: `sk-` and 32 random bytes, shown
  once, kept as its SHA-256 in `gateway.api_keys` in the app's database. (The
  chat's own key is made by LiteLLM and kept encrypted in the app's settings.)
  A person's main key (`app-USERNAME`) is made with them, and for someone with
  none who arrives through LDAP, company sign-in or SCIM, or whom SCIM turns
  back on. It is made again when it is rotated, and when their API access is
  turned back on with no key left. The first admin, made from
  `ADMIN_PASSWORD`, has none until they rotate it. Own keys come
  from Account → API keys, each with its own limits (requests and tokens a
  minute, at once) and, if wanted, a 30-day budget. Each Code Studio
  workspace gets a key of its own.
- **Mirrored to LiteLLM** while LiteLLM still answers: the app writes the row
  first, then registers the same secret at LiteLLM with the models the key may
  use, how many at once and its rate limits, and takes the row out if
  LiteLLM refuses. Revoking deletes it at LiteLLM, then the row. The leader
  pushes access, rate limits and blocks (disabled people, API access off) to
  LiteLLM when they change and every 10 minutes. The ledger's import brings
  over any person's key LiteLLM holds that the app does not.
- **Whose key is it?** `KeyBook` is the one answer, for every part that takes
  a key: the key's line, the answer cache, Arena MCP, Code Arena's sync,
  speech, and Argus's key check. It reads `gateway.api_keys` with the
  person's state, believes an answer for 10 seconds, and forgets it on every
  replica when a key changes.
- **On each call LiteLLM still checks** the key (unknown, blocked, expired),
  the model, the rate limits it counts, and an own key's budget. Then it asks
  the app's guardrail: a disabled person or API access off is refused, then
  the credit of the request's kind, then the safeguards (which may mask
  texts), then room in the engine. People's and groups' credit is the app's,
  not LiteLLM's.
- **Not moved yet**: the key lists on Account → API keys and Admin → People
  still read LiteLLM's, for its spend and block state.

### Argus's key check

A coding agent connects to `argus.DOMAIN/mcp` with the person's API key.
Argus takes nothing but an `sk-` key (no GitLab token in the platform) and
asks the app whose it is (`/api/authz/key`, with `ARGUS_KEY`). The app
refuses a request that came through a proxy, looks the key up in `KeyBook`,
reads the person again (disabled or API access off is refused) and answers
with their email and user name. `/api/authz/code` tells Argus which
repositories the person may read, by the app's rules. Argus keeps a good
answer 5 minutes and a refusal 30 seconds, so a revoked key can still work at
Argus for up to 5 minutes.

### Inside the stack

| credential | held by | used for |
|---|---|---|
| `GATEWAY_KEY` | the app, LiteLLM (its master key) | the app's management calls to LiteLLM (models, keys, blocks); LiteLLM's guardrail calls to the app |
| the chat key | the app (made at LiteLLM on the first model call that needs it, and again if LiteLLM loses it; kept encrypted in its settings) | the app's own model calls; the guardrail lets them pass |
| `ENGINE_KEY` | the app, LiteLLM, `llamacpp`, `vllm`, `sglang`, Prometheus | the engines' API |
| `ARGUS_KEY` | the app, Argus, Prometheus | the chat's calls to Argus (naming the person), the app's admin calls, Argus's key and repository checks, Argus's metrics |
| `WORKSPACES_KEY` | the app, `workspaces` | starting and stopping workspaces; each workspace's engine has a key of its own, kept encrypted |
| `DB_PASSWORD` | the app, LiteLLM, Postgres | the databases |
| `APP_KEY` | the app | the key ring is encrypted under it, so it never changes |
| `ADMIN_PASSWORD` | the app | the first admin, made only while there are no people |

Argus takes the chat's credential only from inside the network: a request
with forwarded headers is refused, and it must name the person.

## 7. Request flows

### A person chats

Browser → Traefik → app (session cookie). The app checks that the person may
use the model and has chat credit left. The answer then takes a place in its
model's line (`AnswerGate`). Each model has as many places as it serves at
once, so people on one model never wait for another's. A person has a limit
of their own, and the whole chat may have one. A free place goes by the
group's priority, then to whoever has the fewest answers running, then to
whoever was served longest ago.

The app then streams from LiteLLM directly (`http://litellm:4000`), not
through its own `/v1` paths. It sends the chat's own key and names the person
(`user` and `X-LLM-User-Email`), so the spend is theirs. LiteLLM's guardrail
lets the chat key's requests pass, because the app checked them, and LiteLLM
sends the call to the engine. The app streams the answer back, runs the tools,
and keeps every message.

- **The engine slot.** Each turn goes to the llama.cpp slot that holds its
  conversation's start (`SlotTable`, sent as `id_slot`), so the engine reads
  only the new turn. Side requests (titles, the safeguards' check, summaries,
  Auto's choice) go to the last slot first on a model with three or more.
- **Room.** A model that is not loaded while the engine is full gets room
  from an idle model that may make room: not kept loaded, not the one new
  chats use, not the one for small steps while it keeps a place (`EngineRoute`),
  the smallest first. With none idle, the request waits a minute, then says
  the engine is full, rather than have llama.cpp unload the big model. Idle
  means no request on its way to it either. A model the app unloads is
  unloading at once, on every replica. API keys, an admin's **Load** and the
  watcher's own loads get room the same way, so llama.cpp never chooses for
  itself which model goes.
- **vLLM and SGLang**: the chat's requests carry a higher priority than API
  keys'. A conversation on a model with copies stays on one copy (`NAME@i`,
  the same on every replica), where its prefix is cached. The model's own
  fallback, when it has one, answers if that copy cannot. Side requests go to
  the model's name, and LiteLLM picks the least busy copy.
- **When it goes wrong**: a refused chat key is made again; a model still
  loading is waited for, 5 seconds at a time, up to two minutes; a model
  LiteLLM does not know makes the app register its models again and try once
  more. A gateway that cannot be reached is tried again for 30 seconds.

**Sound and video in.** On upload the app has the sandbox's ffmpeg make an
MP3 of a sound, and a video's frames and sound track. A model that hears gets
the sound (an `input_audio` part); one that does not gets a transcript from
the gateway's speech to text, made once. Frames go as pictures to a model that
sees.

**The chat's tools.** Pictures, speech and transcripts go through LiteLLM
with the chat key. Video goes straight to `videogen`, and its cost is booked
into the ledger by the app. Python runs in `sandbox`, 3D in `blender`, proofs
in `lean`, each job handed over through its volume, each run its own user with
nothing left behind. The browser agent drives Chromium over DevTools
(`browser:9222`), one context per chat. Web search asks the SearXNG named in
Settings → Python and web → Search engine. With none named, it asks the
`web-search` feature's own (`http://searxng:8080`) while that feature is on;
with the feature off too, the Web tool only opens pages. Laya answers on
`laya:8000`. Argus answers over MCP.

### An API key's request

Client → Traefik → `gateway.DOMAIN` with the key (`Authorization: Bearer`,
or `x-api-key` from Anthropic's clients) → app. For `/v1/chat/completions`:

1. **The answer cache**, when on (Settings → API keys: off, opt-in or all;
   kept a day by default). A request of 1 MB or less is looked up by the
   key and the request. A hit is answered from the app's database
   (`x-arena-cache: hit`). A key the gateway would refuse gets nothing from it
   ([admin.md](admin.md#the-answer-cache-for-api-keys)).
2. **The prompt log** records the request from its arrival: waiting, running,
   then done, failed, stopped or refused.
3. **The key's line** (`KeyLine`): 4 at once a key by default, fewer when an
   own key says so, the rest waiting in order. After 10 minutes' wait the
   request is refused with 429 and `Retry-After`.
4. **The model's line** (`AnswerGate`), with the chat's answers, by the
   person's priority. A full line answers 503 `service_busy`.
5. **The engine slot.** The agent's session (its key, model and first
   messages) goes back to the slot that holds its start. A model that cannot
   be made ready answers 503 `model_not_ready`.
6. **On to LiteLLM** with the caller's own key, which checks it and asks the
   guardrail. For an agent's session, step 5 already chose where it goes: one
   copy of a model with several (`NAME@i`), or the model's fallback when the
   model cannot answer. LiteLLM picks the copy for any other request. The
   answer streams back piece by piece.
   A LiteLLM that cannot be reached gives 502 `gateway_unreachable`.

`/v1/messages`, `/v1/responses` and `/v1/completions` take only step 3, then
go on to LiteLLM. Claude Code, Codex and Code Arena's editor completion use
them. The other model paths are passed on as they came (section 2).

### Speech and pictures by API key

The app takes `/v1/audio/speech`, `/v1/audio/transcriptions` and
`/v1/images/generations` first. Text to speech that names no voice gets the
key's person's voice, model and speed. Speech to text that names no language
gets the language they speak ([chat.md](chat.md#your-voice)). A picture from
this stack's own model checks the person's picture credit, holds the GPU and
loads the model first. Each then goes on to LiteLLM with the same key, the
sound streamed through.

### Arena MCP

`https://DOMAIN/mcp` (and `/mcp/code`, the tools as code) is the app's MCP
server: a person's chat tools for their own agent, signed in with their API
key through `KeyBook`. `/.well-known/mcp` is its discovery. Its model tools
(pictures, speech, video, research) do not use the person's key: pictures,
speech and research call the models with the chat key, and video goes
straight to `videogen`. So the app counts each such call against the
person's requests a minute itself ([mcp.md](mcp.md)).

### Argus

- **From a coding agent**: `argus.DOMAIN/mcp` with the person's API key,
  checked with the app (section 6), answered within what that person may read.
- **From the chat**: `argus:7700/mcp` with `ARGUS_KEY` and the person's email,
  and their own GitHub and Gitea tokens when they linked them.
- **Argus itself** mirrors your GitLab with a read-only token (or a user name
  and password, LDAP too), reads the people the app writes into `directory`,
  embeds through `embed`, asks the scanners at `scanners:7800` on its own
  network, and renews its security advisories every 24 hours. Its own web,
  API and accounts are off in the platform. The app's admin calls carry
  `ARGUS_KEY`.

### Code Arena and Code Studio

- **Code Arena on a person's machine** signs in with an API key, or a user
  name and password (it then makes an own key, "Code Arena on MACHINE", and
  signs the web session out). Its model calls go to `https://gateway.DOMAIN/v1`
  (chat completions, the editor's completions, the model list and model
  info), so they take the API key's path above. With the same key it uses
  Arena MCP at `DOMAIN/mcp` and Argus at `argus.DOMAIN/mcp`.
- **Its sessions follow the web chat** both ways through `/api/code-arena`
  (chats, messages, a turn's lease of 90 seconds), signed in by the key.
- **`code-arena web --connect`** dials a WebSocket to
  `wss://DOMAIN/api/code-arena/connect`. Code Studio's IDE in the browser then
  reaches that machine through the tunnel, held by the replica it dialled.
- **Code Studio's workspaces on the server** (the `workspaces` feature): the
  app makes the workspace a key of its own and asks `workspaces` (with
  `WORKSPACES_KEY`) to start it. Each workspace runs Code Arena's engine as a
  user of its own. Its model calls go to `http://app:8080`, so they take the
  API key's path too (the key's line, the prompt log, the slot), and its tools
  to the app's MCP and `argus:7700/mcp`. The IDE reaches the workspace's
  engine through the app. No Docker socket is involved
  ([chat.md](chat.md#code-studio-workspaces), [code-arena.md](code-arena.md)).

### Company knowledge and the wiki

The app reads the sources an admin added (folders under `/knowledge`,
uploads, GitLab with its bot's token, GitHub, Confluence, Jira, Notion,
SharePoint, WebDAV, S3 buckets, feeds, websites through the web guard) every
hour. It cuts new or changed documents into passages, embeds them with
`embed`, and keeps them in its own database with each document's readers
(compared with pgvector when it is there). The chat's `search_knowledge` searches only what
the asker may read ([knowledge.md](knowledge.md)). The wiki (`/api/wiki`) is
search and browse with no model in between: knowledge by words and, when the
embedder runs, by meaning; code through Argus, as the person; and the data
tables the person may ask.

### The ledger of every model request

`gateway.requests`, in the app's database, holds the model requests LiteLLM
logged (done, failed, or refused by LiteLLM or the guardrail, with the
reason) and the media the app makes itself. Requests the app answers or
refuses before LiteLLM (answer cache hits, the key's and the model's lines, a
model not ready, a gateway not reached) are not in it yet, and no row is
stopped or cached yet. Each row names the surface (chat, app, api, studio,
media), the protocol, the kind of credit, the person, the key's hash, the
model, where it was answered (`llamacpp`, a vLLM copy, a remote server,
`imagegen`), the tokens, the time and the cost. It has a column for the
fallback that answered, which nothing fills yet.

- **Written** today by the ledger's import, on the leader: every 5 seconds it
  copies LiteLLM's new log rows (each once, by LiteLLM's id) and tells
  refusals from failures; at first it brings over all LiteLLM keeps. Media the
  app makes without the gateway (video clips, the media studio's pictures,
  the studio server's speech and music) is booked by the app into both
  LiteLLM's log and the ledger.
- **Read** by credit (the month's spend by person and kind), the usage pages
  (a person's, a group's, chargeback by month), the prompt list's API part,
  rate limits' "used this minute", the storage page, and the dashboards.
- **The dashboards** read it through the datasource `gateway-db`: the app's
  database, as a read-only role that may read only the schema `gateway`, with
  a time limit and a row cap. One panel ("Which key belongs to whom") still
  reads LiteLLM's keys through the old `litellm-db` datasource.
- **Kept** for `Retention:GatewayLogDays`, LiteLLM's log too. Erasing a person
  clears their name from both. Repricing changes both.
- `gateway.rate_windows` and the views `gateway.key_owners` and
  `gateway.month_spend` are there for the steps to come. Rate limits are still
  counted by LiteLLM.

### Metrics and logs

- **Prometheus** scrapes every 15 seconds: the engine's loaded models (the app
  writes the list into `engine`: one target per llama.cpp model, each vLLM or
  SGLang process), the exporters, Traefik, the app (the answers' line),
  Alertmanager, Loki and Argus. It reaches the engines with `ENGINE_KEY` and
  Argus with `ARGUS_KEY`, from token files it writes at start. Its rules
  (`config/prometheus/rules/`) cover Argus, the engines, the hardware, the
  models, the SLOs and the stack.
- **Promtail** is the one service with the Docker socket (read only, to find
  the containers). It ships the logs of the compose project `arena` to Loki,
  with their levels. The app and the engines never get the socket.
- **The app** reads Prometheus, Loki and Alertmanager for its dashboards, logs
  and alerts. The Logs page builds its queries from filters; nobody types
  LogQL. The leader asks Alertmanager every minute and tells admins of a new
  alert in the bell, by email and the alerts webhook. Alertmanager itself sends
  nothing until a receiver is set up.
- **Admin → Overview** probes the model gateway, Prometheus, Alertmanager,
  Loki, the engine, the picture, video and speech servers, web search and
  Argus.

## 8. Who prepares what

There are no setup containers. Each running image prepares itself, and the
installer prepares the host.

| who | prepares |
|---|---|
| the installer | `.env` with its secrets; `DATA_DIR`'s folders with their owners; the first `docker-compose.override.yml`; the certificate (`make-cert.sh`); Code Arena's signature (`sign-code-arena.sh`); the images of the features that are on, loaded into Docker or Podman. On an upgrade, a backup first, and a rollback if it fails |
| `postgres` | the gateway's database (`POSTGRES_DB`, `litellm`) |
| `litellm` | its tables: about 160 migrations on a first start, a minute or more. The app's first chat waits up to 90 seconds for it |
| `app` | its own database (`llmapp`) and its migrations, one replica at a time; the first admin from `ADMIN_EMAIL` and `ADMIN_PASSWORD`; the directory Argus reads; the OIDC clients; the chat key; the engines' presets and desired state; the media servers' on and off; Prometheus's targets; the models registered at LiteLLM; the keys at LiteLLM; the downloads, when asked (`DEFAULT_MODELS=download`, or an admin), and `MODEL` added once its files are in the library |
| `llamacpp` | nothing: `router.sh` waits for the app's model list, starts llama-server in router mode, restarts it when the list changes, and prunes old caches |
| `vllm`, `sglang` | nothing: the supervisor follows the app's desired state, and writes what it sees of the GPUs |
| `imagegen`, `videogen`, `embed` | nothing: they wait for their files (`sd-serve.sh` also follows the app's on and off) |
| `audio` | fetches its models when the app asks |
| `studio`, `laya` | nothing: the app fetches their models into `MODELS_DIR` |
| `workspaces` | a user and an engine for each workspace, when the app asks |
| `prometheus` | its scrape credentials, from its environment, at start |
| `searxng` | a new secret each start |

## 9. Several app replicas

The app can run as several replicas on one database (`scale.yml` with compose,
or the Helm chart's `app.replicas`). What must happen once runs on one of them:

- **The lead.** Each replica holds a Postgres connection of its own, outside
  any pool, and tries for an advisory lock on it every 5 seconds. The one
  holding it leads. It runs the scheduled tasks' clock, the engine watcher
  (the models kept loaded, the presets, the models at LiteLLM, Prometheus's
  targets), the media servers' on and off, the downloads, the first model,
  the key access sync to LiteLLM, the ledger's import, the LDAP sync,
  Argus's schedule, the storage watch, the alerts for the bell, the chat
  digests, meeting reminders, the memory's upkeep, idle Code Studio
  workspaces, and the answers queued before a restart. A replica that stops
  lets go of the lock. One that dies loses it with its connection, and another
  leads within seconds. The replicas are counted by their connections' names.
- **The start.** Replicas starting together go one at a time through creating
  the database, migrating it, the first admin and the OIDC keys (a lock in the
  server's `postgres` database). If that database refuses, the start goes on
  without the lock, with a warning.
- **Signals.** The same connection listens (Postgres `LISTEN`/`NOTIFY`): a yes
  or no to a tool call, a stop or **Answer now** posted to one replica reaches
  the one writing the answer; a saved setting is read again on all; a model
  loaded or a download started on one wakes the leader; a changed key is
  forgotten by every `KeyBook`. A replica that asks waits up to 3 seconds for
  the one that has the answer.
- **Scheduled tasks.** A run is claimed in the database first (the task row
  names the replica running it, renewed every 20 seconds, let go after two
  minutes without it): a task never runs twice at once. Events that come while
  it runs wait in the `task_events` table, and the replica free next takes them
  in order.
- **The answers' line.** Each replica keeps its own, with its share of the
  places the engine serves at once (rounded up), and its own table of engine
  slots. An answer runs on the replica that was asked and streams from there:
  the sticky cookie (`arena_replica`) keeps a browser on one replica. A page
  that reaches another replica sees the answer when it is saved, not live.
- **Code Arena's tunnels** are held by the replica they dialled. Another
  replica reaches a machine through it.
- **Kept in the database**: sessions and two-factor (the key ring), OIDC
  keys, settings, chats, task claims, the keys and the ledger. **Per replica**:
  the sign-in throttle, the chat's model list (a minute), `KeyBook`'s answers
  (10 seconds), and the key's line, so a key's at-once limit holds on each
  replica. API keys' rate limits are counted by LiteLLM: once for every app
  replica while one LiteLLM runs; with several (`ha.yml`), shared in Valkey.
- `Replicas:Enabled=false` is for one replica behind a pooler with no
  sessions: it always leads and hears no other. A transaction pooler breaks
  the lead and the signals.

When more than one replica runs, **Admin → Overview** says how many, and
whether the one answering leads. The replica's id is only in the API's answer
(`/api/admin/overview`).

## 10. Kubernetes

`deploy/helm/argus-arena` runs the same services on Kubernetes, with the
names the app reaches them by, one release per namespace. `./arenactl kube`
(or `installer.sh --kubernetes`) installs, upgrades and removes it
([deployment.md](deployment.md#helm)).

- **The way in** is an Ingress instead of Traefik, with the same three names:
  `gateway.DOMAIN` goes to the app, never to LiteLLM, and the sticky cookie
  keeps a browser on one app replica.
- **Features**: each service's `enabled` in `values.yaml` stands for its
  feature, and `app.features` lists the app's own.
- **Availability**: the app (2 replicas by default), the web and LiteLLM can
  spread over nodes, roll one at a time, and have a disruption budget and an
  autoscaler each. Their shared volumes (the model library, `engine`, the
  sandboxes', the status folders) must be ReadWriteMany across nodes.
- **Engines**: one Deployment per vLLM or SGLang instance, each driven by the
  same files on claims, with no Kubernetes API. The sandboxes, Laya, the
  scanners, the studio and the workspaces have NetworkPolicies.
- **Not in the chart**: the browser (point `browser.url` at one you run),
  Promtail (log shipping is the cluster's), the GPU exporters and the power
  limits. Point-in-time recovery is CloudNativePG's.

## 11. When something is broken

| what you see | what it means |
|---|---|
| every address fails | Traefik is down, or its routes file does not parse (`docker compose logs traefik`) |
| the web loads, but sign-in and `gateway.DOMAIN` fail | the app is down: nothing else answers for them |
| a request the app had already passed on is refused while the app restarts | LiteLLM's guardrail fails closed when it cannot ask the app. New requests to `gateway.DOMAIN` fail until the app is back: only the app is behind it |
| an API call answers 502 `gateway_unreachable`, or the chat says the gateway is unreachable | LiteLLM is starting (its migrations) or down |
| an API call answers 429 after a long wait | it waited too long for its turn: in the key's line (its places at once were taken for 10 minutes) or in the model's line. A 429 at once is LiteLLM's rate limit |
| 503 `service_busy` | the line of waiting requests is as long as `Chat:QueueLength` allows: try again in a while. The line holds every model's chat answers and API requests together, each replica its share; it has no limit unless an admin sets one |
| 503 `model_not_ready`, or "… is not loaded right now" | the model could not be loaded: the engine is full and no model is idle, or it failed to load and waits to try again (Admin → Models) |
| nobody can sign in on a new stack | "there are no people and ADMIN_PASSWORD is not set" in the app's log |
| "Starting without the start lock" | the server's `postgres` database refused: start one replica at a time |
| "Replicas: the connection for the lead and the signals was lost" | it reconnects; another replica leads within seconds |
| a feature's pages say it is not installed, or its API answers 404 | it is not in `COMPOSE_PROFILES`, or a feature it requires is not (`./arenactl repair --with FEATURE` brings both) |
| a picture, video or embedding server waits for its files | the app is fetching them, or the default models were never asked for (Admin → Models → Default models) |
| Argus pages say "not set up" | the app has no Argus address or key (`ARGUS_KEY`; compose does not start without it). With the `argus` feature off they say "… is not installed here" instead |
| a revoked key still answers at Argus | Argus keeps a good answer for 5 minutes |
| dashboards are empty | Prometheus is down, or the engine has no model loaded to scrape |
| the Logs page is empty | Loki or Promtail is down, or the compose project is not `arena` (Promtail keeps only that one) |
| with `scale.yml`, the app's log levels are missing, or the line's metrics show one replica | Promtail parses the container named `app`, and Prometheus scrapes the one name `app:8080` |
| alerts fire, nobody hears outside the app | the app's email (Settings → Email) and its alerts webhook are not set up, and Alertmanager's default receiver sends nothing until one is set up |
| Code Arena's builds are unsigned | no CA or code-signing certificate in `certs/` or `signing/`, or `sign-code-arena.sh` failed |
| the browser warns about the certificate | trust `certs/ca.crt`, or set `ACME_EMAIL` |
| `backup.sh` cannot write `BACKUP_DIR` | Docker made it, as root: change its owner, or `backup.sh --install-timer` |
| an upgrade exits 4, or 5 | 4: it failed and rolled back. 5: the rollback failed too: fix the cause and upgrade again |

## 12. What is generated, and what you edit

| what | who writes it | on an upgrade or repair |
|---|---|---|
| `.env` | the installer on install (mode 0600, the secrets made; only the admin's first password is shown, once, on the terminal and never in the log); you after | kept; keys new in the release are added, and `COMPOSE_PROFILES` follows `--with` and `--without` |
| `docker-compose.override.yml` | the installer, only when there is none; you after | kept, except that a repair turning a feature on takes out the `{ profiles: [off] }` line that switched its service off |
| the other files of `deploy/` (`config/`, the other overlays, `features`) | the release; you, if you change one | on an upgrade, the release's new copy, unless you changed it: then yours stays, and when the release changed it too, its copy is written beside yours as `FILE.new-VERSION`, to merge. When the copy shipped before is not known, the release's copy goes in and yours is kept as `FILE.before-VERSION`. A repair keeps what the install or upgrade left as you changed it; a file changed since then it puts back, after asking, with yours kept as `FILE.changed-DATE` |
| `docker-compose.yml`, `podman.yml`, `scale.yml`, `scripts/installer.sh` | the release | an upgrade keeps a copy you changed when the release ships the file as before. When the release changes it too, or the copy shipped before is not known, the upgrade puts the release's copy in and keeps yours as `FILE.before-VERSION`. A repair keeps what the install or upgrade left as you changed it, and puts back a file changed since then, after asking, keeping yours as `FILE.changed-DATE`. Changes to compose belong in the override |
| `certs/`, `config/traefik/certificate.yml` | `make-cert.sh` | kept |
| `signing/`, `DATA_DIR/code-arena-signature` | `sign-code-arena.sh` | signed again; the certificate made again within 30 days of its end |
| `DATA_DIR`'s folders | the installer makes them; their services write them | kept |
| the `engine` and `directory` volumes | the app | kept |
| `MODELS_DIR` | the app (and the installer, placing a bundle's models) | kept |
| `BACKUP_DIR` | `backup.sh` | kept |
| `.arena-install/`, in the installation's folder | the installer: its state and logs | kept |