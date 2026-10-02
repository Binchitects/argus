# Architecture: every part of the stack, and how they fit

This is the reference for what runs, why it is there, and how a request travels
through it. For how to set each value, see [configuration.md](configuration.md).
For deploying, see the [top-level README](../README.md).

Every fact here is taken from `docker-compose.yml` and `config/`; where a
service exists because something went wrong without it, the comment in the
compose file says so and this document repeats the reason rather than the
mechanism.

---

## 1. The shape

One host. One Docker network. One ingress. The only ports that reach the
machine's network interface belong to Traefik:

```
                          :80 / :443  (the only published ports)
                                │
                        ┌───────▼────────┐
                        │    traefik     │  TLS, routing, forwarding auth
                        └───────┬────────┘
                                │  llm-net (bridge)
   ┌────────────┬───────────────┼───────────────┬─────────────┐
   │            │               │               │             │
┌──▼───┐   ┌────▼────┐    ┌─────▼─────┐   ┌─────▼─────┐  ┌────▼─────┐
│ app  │   │ gateway │    │ inference │   │   tools   │  │  observ. │
│ web  │   │ litellm │    │ llamacpp  │   │ argus     │  │ prometh. │
│ (IdP)│   │ postgres│    │ vllm      │   │ sandbox   │  │ alertmgr │
│      │   │ redis   │    │ imagegen  │   │ searxng   │  │ loki ... │
└──────┘   └─────────┘    └───────────┘   └───────────┘  └──────────┘
```

Nothing but Traefik has a `ports:` entry. The inference engines, the databases
and the app's internal port are reachable **only** from inside `llm-net`, which is why
the API gateway can be the single place that authenticates machine callers and
counts their tokens.

That single-ingress design is also why a broken Traefik is a total outage and a
broken Prometheus is not — and why `tls-init` failing takes the whole stack down
with it (see §9).

---

## 2. Hostnames

`LLM_DOMAIN` in `.env` names the parent. Every service is
`https://<name>.<LLM_DOMAIN>`, and the certificate covers `<domain>` plus
`*.<domain>`, so one name is the whole configuration:

| hostname | service | auth in front of it |
|---|---|---|
| `<domain>` | the web (`web`): chat, usage and cost, admin, settings; `/api`, `/connect` and `/.well-known` go to the app (sign-in, OIDC, the API) | none (the app *is* the sign-in) |
| `gateway.<domain>` | LiteLLM | none in front — LiteLLM checks each person's key |
| `api.<domain>` | llama.cpp **or** vLLM | forwardAuth (machine token), plus a key-injecting middleware |
| `api2.<domain>` | vLLM secondary (`multi-model`) | none in front — no forwardAuth and **no key injection**, so the caller presents `VLLM_API_KEY` itself |
| `argus.<domain>` | Argus MCP (`argus` profile) | none — per-caller GitLab PAT |
| `traces.<domain>` | Langfuse | OIDC (its own session) |
| `metrics.<domain>` | Prometheus | `sso-chain` |
| `alerts.<domain>` | Alertmanager | `sso-chain` |
| `logs.<domain>` | Loki | `sso-chain` |
| `node.<domain>` | node-exporter | `sso-chain` |
| `gpu.<domain>` | nvidia-smi-exporter | `sso-chain` |
| `cadvisor.<domain>` | cAdvisor | `sso-chain` |
| `s3.<domain>` | MinIO console (`tracing`) | `default-chain` only |

`api.<domain>` is special: both `llamacpp` and `vllm` claim it, and only one of
them runs (they are in mutually exclusive profiles). The same is true of the
`gateway` route, which is what makes `ENGINE_API_BASE` in `.env` the only thing
that decides which engine serves.

---

## 3. Profiles: what is running, and why it is opt-in

`COMPOSE_PROFILES` in `.env` is the switchboard. A service with no `profiles:`
key always starts; everything else needs its profile named.

| profile | brings up | why it is separate |
|---|---|---|
| *(always)* | `tls-init`, `prometheus-secrets`, `prometheus`, `alertmanager`, `node-exporter`, `power-limits` | the minimum that is useful and cheap; TLS is not optional, and an LLM stack nobody can monitor is one nobody notices breaking |
| `proxy` | `traefik` | the ingress. Separate so a machine can run the engines without 80/443 |
| `gateway` | `app-init`, `app`, `web`, `litellm`, `postgres`, `redis` | the app (sign-in for everything, chat, admin), the web, the API gateway, per-person keys and budgets |
| `llamacpp` | `llamacpp`, `model-init` | one of the two engine choices |
| `vllm` | `vllm` | the other engine. Mutually exclusive with `llamacpp` for the GPU |
| `multi-model` | `vllm-secondary` | a second, small model on the same card |
| `argus` | `argus`, `llamacpp-embed`, `embed-init` | the code index and its embedding model (llama.cpp on the CPU) |
| `image` | `imagegen` | picture generation beside the chat model, on the same GPU, behind the gateway |
| `sandbox` | `sandbox` | the chat's Python: no network, a job per unprivileged user, limits; the app hands it jobs through a volume |
| `websearch` | `searxng` | a search engine for the chat's Web tool, on its own network with the app only |
| `smi` | `nvidia-smi-exporter`, `cpu-temp-exporter` | GPU/CPU telemetry via NVML and the host's own sensors |
| `dcgm` | `dcgm-exporter` | the alternative GPU exporter; heavier, more detail |
| `cadvisor` | `cadvisor` | per-container CPU/memory |
| `logging` | `loki`, `promtail` | log aggregation. **In the default `COMPOSE_PROFILES`** |
| `tracing` | `langfuse`, `langfuse-worker`, `clickhouse`, `minio`, `postgres`, `redis` | LLM request tracing |

`postgres` and `redis` appear in several profiles on purpose: they are shared,
and compose starts a service if **any** enabled profile names it.

The samples start with `gateway,proxy,smi,llamacpp,logging`. Adding a profile
is an `.env` edit and `docker compose up -d`; compose then creates only the new
containers.

---

## 4. Network and volumes

**One network.** `llm-net`, a plain bridge. Every service joins it, and every
service reaches every other by its **service name** (`http://litellm:4000`,
`http://argus:7700`). Those names are Docker's embedded DNS, not `/etc/hosts`.

Three names are *also* resolvable, deliberately:

* `<domain>` → Traefik. Traefik carries an explicit network alias for it,
  because the OIDC token exchange is server-to-server: Langfuse calls the issuer
  (the app) **directly** rather than through the browser.
  Without that alias `<domain>` does not resolve inside a container and
  every login fails at the token step — while `/healthz` on every service still
  looks perfect.
* `host.docker.internal` → the host, via `extra_hosts: host-gateway`. Two
  services need it, for the same reason — the thing they talk to lives on the
  host rather than in a container: `argus`, for a GitLab running on the same
  machine, and `cpu-temp-exporter`, for the hardware monitor it reads CPU
  temperature from. Inside a container `localhost` means the container itself.

**Sixteen named volumes.** They are prefixed with `COMPOSE_PROJECT_NAME`, so
the real names are `llmservice_postgres-data` and so on. `.env` is the only
place the project name is set, and changing it after the first start gives you
a stack that boots with none of its data.

| volume | holds | safe to delete? |
|---|---|---|
| `traefik-certs` | the TLS certificate and key, generated by `tls-init` | yes — regenerated, and browsers re-warn |
| `postgres-data` | the app's people, 2FA, audit log and chats (`llmapp`), LiteLLM's keys/spend, **and Langfuse's traces** | no — this is the accounts, billing and audit history |
| `argus-data` | the code index, symbol embeddings, audit and ACL cache | yes — rebuilt by re-indexing (minutes to hours) |
| `prometheus-data` | 30 days of metrics by default | yes — the history, not the config |
| `prometheus-secrets` | the engine scrape token, generated on every `up` | yes |
| `alertmanager-data` | silences and the notification log | yes |
| `loki-data` | aggregated container logs | yes |
| `clickhouse-data`, `clickhouse-logs` | Langfuse's trace storage | no |
| `minio-data` | Langfuse's blob store | no |
| `redis-data` | LiteLLM's response cache and auth cache | yes — caches |
| `hf-cache` | Hugging Face downloads for the vLLM path | yes, re-downloads |
| `vllm-cache` | vLLM's compilation cache | yes, recompiles |
| `llamacpp-engine` | a downloaded llama.cpp engine tarball, when `LLAMACPP_ENGINE_URL` is set | yes |
| `sandbox-jobs` | the chat's Python jobs and their results, between the app and the sandbox | yes |
| `power-limits-state` | the "original" power limits, so they can be restored | yes |

`make backup` archives them (`scripts/backup.sh`). `make clean` deletes all of
them, and asks for the word `PURGE` first.

---

## 5. Authentication: one issuer, four mechanisms

This is the part people get wrong, because the stack deliberately runs several
schemes side by side. All of them answer to one identity provider, **the app**
at `https://<domain>` (people, roles, 2FA, the company directory). Each scheme
exists because the alternative was worse. The full reference is
[authentication.md](authentication.md).

### 5.1 forwardAuth to the app, for services with no sign-in of their own

Prometheus, Alertmanager, Loki, cAdvisor and the exporters have
no account system at all. Traefik asks the app about **every request** before
it reaches them:

```
browser → traefik → (forwardAuth) app:8080/api/authz/forward-auth
                         │
                   200 + Remote-User/Remote-Groups headers → let through
                   302 → https://<domain>/login?rd=<where they were going>   (a browser)
                   401 / 403                                                (a program / the wrong role)
```

The middleware is `app-auth@file`; the chain that wraps it with security headers
is `sso-chain@file`. The rule per hostname lives in the app
(`src/Llm.Api/Oidc/ForwardAuth.cs`): infrastructure hosts are admins only,
`api.` takes a machine token (or a signed-in person, for its docs), and a
hostname not listed there is closed.

### 5.2 OIDC, for services with their own session

Langfuse (`tracing`) has its own user database and sign-in screen. It delegates
the *sign-in* to the app and keeps its own session:

```
browser → traces.<domain> → https://<domain>/connect/authorize → back with a code
                          → Langfuse exchanges the code server-to-server
```

The app registers its clients itself on every start from the
`*_OIDC_CLIENT_SECRET` values in `.env` (Langfuse, and `api` for machines),
stored hashed in its database, and deletes any client the configuration does
not name. Its signing key is generated once and kept, encrypted with
`APP_DATA_KEY`.

The server-to-server token exchange is why `<domain>` itself must resolve
inside the network (§4), and why every such container carries the stack's
certificate as a trusted CA.

### 5.3 Bearer credentials, for machines and for Argus

The gateway, the engine and Argus authenticate the caller, not a browser session:

* **`gateway.<domain>`** takes a per-person LiteLLM key (`sk-...`), made in the
  app, and nothing in front of it: LiteLLM itself checks the key, which is what
  puts spend on the right person.
* **`api.<domain>`** takes an access token from the app's `client_credentials`
  grant (`scripts/get-token.sh`), checked by forwardAuth; Traefik then **injects
  the engine's own key** with a label-defined middleware (`llamacpp-key@docker` /
  `vllm-key@docker`). The caller never needs `LLAMACPP_API_KEY`, and the engine
  port is not reachable any other way.
* **`argus.<domain>`** has no forwardAuth at all, and that is the point: Argus
  needs the caller's **own GitLab personal access token**, untouched, so it can
  answer with exactly the repositories that person may read. Replacing it with a
  session would erase the per-caller identity the ACL is built on.

  Argus still has to map a chat user's email to a GitLab username, so the app
  publishes a list without any passwords into `config/directory/users.yml`
  (usernames, emails, display names; disabled people left out) and Argus mounts
  only that directory (`ARGUS_USERS_FILE`). The app rewrites it at start and on
  every change to a person.

---

## 6. Request flows

### 6.1 A person chats in the browser

```
browser ──https──► traefik ──► web (the page) ──► app /api/chat ──http──► litellm ──► engine
                                                    │                        │
                                   signed-in session (cookie)      spend rows in postgres
                                   X-LLM-User-Email + body `user`  per-person budget enforced
```

The app talks to LiteLLM with **its own chat key** (alias `chat`, made once,
kept encrypted), which is why it also names the signed-in person. Two
mechanisms, one per job:

* the `X-LLM-User-Email` header, which LiteLLM maps onto its internal user
  (`user_header_mappings` in `config/litellm/config.yaml`), *attributes* the
  spend: chat spend lands on the same identity as the person's API key, and
  their total is one number instead of two;
* the body's `user` field *enforces* the ceiling: the chat key has no budget of
  its own, and the end-user budget on the person's email is what returns 429.

### 6.2 A tool or agent calls the API

```
curl/agent ──https──► traefik ──► litellm ──► engine
   Authorization: Bearer sk-<person's key>
```

The key *is* the identity. People make their own under **Connect your tools**
(`/setup`, also on the Account page), and the app mints it with `user_id` set to
the person's email, which is the identifier the whole stack agrees on. The
gateway speaks OpenAI's `/v1/chat/completions` and Anthropic's `/v1/messages`,
so Qwen Code, Claude Code and the SDKs all point at `gateway.<domain>`.

### 6.3 A developer's agent uses Argus

```
agent ──https──► traefik ──► argus ──http──► gitlab (the caller's own PAT)
   Authorization: Bearer <GitLab PAT>        └─ project membership → which repos to search
```

Argus searches the shared index but returns only what that token can read, and
when the only matches are in repositories the caller cannot read it names them
and their maintainers instead of answering "nothing found".

### 6.4 Chat uses Argus

```
app (the chat's Argus tool) ──http (inside llm-net)──► argus
   Authorization: Bearer <ARGUS_CHAT_CLIENT_TOKEN>
   X-LLM-User-Email: <the signed-in person>
```

This path deliberately does not go through Traefik. The chat-client token is
only accepted from inside the network, so a leaked one cannot claim someone
else's email from outside.

### 6.5 Metrics

```
prometheus ──scrape──► node-exporter, nvidia-smi-exporter, cpu-temp-exporter,
                       llamacpp / vllm, cadvisor, dcgm, traefik, loki
     │
     ├─ rules in config/prometheus/rules/*.yml
     ├─ firing alerts ──► alertmanager ──► (filesystem notifier)
     └─ queried by ──► the app ──► 10 dashboards, Logs (Loki), Alerts
```

The app draws every dashboard itself (Observe → Dashboards): the files in
`config/dashboards/` are Grafana's JSON format, and the app runs each panel's
query against Prometheus, Loki or the gateway's database, with Grafana's rules
for steps, macros, variables and series names. The Logs page reads Loki; the Alerts page reads Alertmanager
(what fires now), Prometheus's rules, and its `ALERTS` series (what fired before).

Per-person token usage is **not** scraped: LiteLLM's `/metrics` is an
enterprise feature on current builds, and vLLM's metrics carry token counts but
no user dimension. That is why the usage dashboards query the same spend tables
the LiteLLM admin UI reads, in Postgres.

---

## 7. The services

### 7.1 Ingress and TLS

| service | image | what it does |
|---|---|---|
| `traefik` | `traefik:v3.6.7` | the only published ports. Terminates TLS, routes by `Host(...)` label, applies middleware chains. Docker provider scoped to this compose project by label, file provider watches `config/traefik/dynamic/` |
| `tls-init` | `python:3.13-slim` (with `openssl`) | one-shot. Makes the stack's own CA once, and from it the certificate for `<domain>` and `*.<domain>` (397 days, renewed within 30 days of expiry or on a new domain) into `traefik-certs`, or takes yours from `config/tls/`; names the served one in `dynamic/certificate.yml`, so Traefik loads a new one with no restart. It reuses it, and regenerates it when `LLM_DOMAIN` changes. Also builds `/certs/bundle.crt` — the public roots, this certificate, and **every `.crt`/`.pem` dropped in `config/ca/`** — so one company CA can be trusted stack-wide, and copies the public cert and the bundle to `config/traefik/certs/` for host-side tools |

Traefik's Docker provider is constrained by
``Label(`com.docker.compose.project`, ...)``. Without that, router names are a
flat global namespace across the whole Docker daemon and an unrelated project
that happens to define a router with the same name (`app`, say) silently
replaces this stack's route — logins then break with nothing in any log.

The default certificate is generated by `tls-init` rather than Traefik's own,
because Traefik makes its default certificate **in memory and anew on every
restart**, so nothing could ever trust it.

### 7.2 Identity

| service | image | what it does |
|---|---|---|
| `app` | built from `src/Llm.Api/Dockerfile` | the identity provider (sign-in, local and LDAP, 2FA, OIDC, forwardAuth), the chat (its tools: Argus, Python, the web, pictures), people, API keys and credit through LiteLLM, the audit log, usage and cost (the SQL dashboards, drawn by the app), and the admin area (model, Argus index and packs, services, settings). Its own `llmapp` database; runs as `LLM_UID`, read-only root |
| `app-init` | `python:3.13-slim` | one-shot. Hands the app the folders it writes (`config/directory`, `config/app`, `config/engine`) as `LLM_UID:LLM_GID` |
| `redis` | `redis:7-alpine` | LiteLLM's response and auth caches |

### 7.3 Inference

| service | image | what it does |
|---|---|---|
| `llamacpp` | `ghcr.io/ggml-org/llama.cpp:server-cuda` | GGUF inference. The default for a single 24 GB card, because MoE experts can live in system RAM (`LLAMACPP_N_CPU_MOE`) and be paged from NVMe. Runs in **router mode**: several models, up to `LLAMACPP_MODELS_MAX` loaded at once (those kept loaded, and others loaded on request), each on the GPUs chosen for it, managed from Admin → Models through the engine's API (`/models/load`), with no restart and no Docker socket |
| `model-init` | same | one-shot. Downloads the GGUF shards from Hugging Face, verifies SHA-256, optionally unpacks a custom engine tarball, then exits. **`llamacpp` waits for it to exit 0** (`service_completed_successfully`), so a failure here stops the engine rather than producing one that cannot find its weights |
| `vllm` | `vllm/vllm-openai:latest` | the alternative engine, for safetensors/AWQ models |
| `vllm-secondary` | same | a second, small model on the same GPU (`multi-model` profile), reached at `api2.<domain>` |
| `llamacpp-embed` | `ghcr.io/ggml-org/llama.cpp:server-cuda`, run on the CPU | Argus's embeddings (`nomic-embed-text-v1.5`, OpenAI protocol): every `semantic_search`, `which_repo` and `docs_find` query, ~15 ms each on 4 cores. `embed-init` fetches the model once into `EMBED_MODEL_DIR`, checked against Hugging Face's SHA-256 |

Exactly one of `llamacpp`/`vllm` should hold the GPU; `ENGINE_API_BASE`,
`ENGINE_API_KEY` and `ENGINE_MODEL` in `.env` are what point the gateway at the
one you chose.

### 7.3a Chat tools

| service | image | what it does |
|---|---|---|
| `sandbox` | built from `services/sandbox` (Python 3.13 and data packages) | runs the chat's Python. `network_mode: none`, a read-only root, all capabilities dropped but those needed to run each job as its own user (20000 + slot), `no-new-privileges`, an init that reaps orphans, pid, memory and CPU limits; per job: CPU time, address space, file size, open files and process limits, a wiped tmpfs directory, and every process of its user killed afterwards. The app writes jobs to the `sandbox-jobs` volume and reads results beside them: no port, no Docker socket |
| `searxng` | `ghcr.io/searxng/searxng` (pinned) | search for the Web tool, answering the app in JSON. On `search-net` with the app only, read-only, as its own user, no port or route |

The Web tool itself runs in the app: a page is opened only when its site is
allowed and every address it connects to (redirects too, checked as each
connection is made) is on the public internet.

### 7.4 Gateway

| service | image | what it does |
|---|---|---|
| `litellm` | `ghcr.io/berriai/litellm:main-stable` | `/v1` OpenAI-compatible endpoint (and Anthropic's `/v1/messages`, OpenAI's `/v1/responses`). Per-person keys, spend, budgets, retries, optional Redis cache. Renders `${MODEL_NAME}`-style tokens in its config from the environment at startup. Serves the engine's models and those chosen from other GPU servers (Admin → Models), which the app registers through its model API; it reads the stack's CA bundle (`config/traefik/certs/bundle.crt`, never the key) for servers behind https |
| `imagegen` | `ghcr.io/leejet/stable-diffusion.cpp` (pinned by digest) | pictures: FLUX.2 [klein] 4B by default, OpenAI-style `/v1/images/generations`. Weights in RAM, streamed to the GPU within `IMAGEGEN_MAX_VRAM`. Non-root, read-only root, on an internal network only LiteLLM joins |
| `postgres` | `pgvector/pgvector:0.8.0-pg16` | one instance, three databases: `litellm`, `langfuse`, `argus` (created by `config/postgres/init/01-create-databases.sql`, with the `vector` extension). Argus uses it only when `ARGUS_VECTOR_BACKEND=pgvector` |

Only one model is advertised, under one name. Aliases were removed on purpose:
clients cached `/model/info` and showed every alias as a separate model.

### 7.5 User interfaces

| service | image | what it does |
|---|---|---|
| `web` | built from `src/web` (Alpine + nginx) | the web: static files only, non-root, read-only root, the same security headers as the API. It also serves the chat's preview runner (`/preview.html`), sandboxed into an origin of its own with no network ([chat.md](chat.md)). The app itself serves only the API |
| `argus` | built from `src/Argus/Dockerfile` (`target: server`) | MCP code-search server over the private GitLab index, and the knowledge packs loaded from the pack library (`ARGUS_PACK_LIBRARY_DIR`, the repository's `packs/` by default). Every answer is also written as a JSON audit line — who asked, which repositories were consulted, what was returned — which is what the Argus dashboard reads |

### 7.6 Observability

| service | image | profile | what it does |
|---|---|---|---|
| `prometheus` | `prom/prometheus:v3.1.0` | always | scrapes 14 jobs; rules in `config/prometheus/rules/` |
| `prometheus-secrets` | `prom/prometheus:v3.1.0` | always | one-shot. Puts the engine's scrape token into a volume Prometheus mounts read-only |
| `alertmanager` | `prom/alertmanager:v0.28.0` | always | receives firing alerts. The default receiver is `null`, so alerts are visible on the app's Alerts page (and Alertmanager's UI) and sent nowhere until you configure one |
| `node-exporter` | `prom/node-exporter:v1.9.0` | always | host CPU, memory, disk, network |
| `nvidia-smi-exporter` | `utkuozdemir/nvidia_gpu_exporter:1.3.2` | `smi` | GPU via NVML |
| `cpu-temp-exporter` | `python:3.13-slim` + `deploy/services/cpu-temp-exporter/exporter.py` | `smi` | CPU package temperature, which NVML does not report |
| `dcgm-exporter` | `nvcr.io/nvidia/k8s/dcgm-exporter` | `dcgm` | the heavier alternative to `nvidia-smi-exporter` |
| `cadvisor` | `gcr.io/cadvisor/cadvisor:v0.52.1` | `cadvisor` | per-container CPU and memory |
| `loki` + `promtail` | `grafana/loki:3.4.1`, `grafana/promtail:3.4.1` | `logging` (**default on**) | log aggregation. Promtail reads the Docker socket and container log files under `HOST_DOCKER_DIR`, and keeps only this compose project's containers |
| `langfuse` + `langfuse-worker` | `langfuse/langfuse:3` | `tracing` | LLM request tracing, backed by `clickhouse` and `minio` |

### 7.7 Host control

| service | image | what it does |
|---|---|---|
| `power-limits` | `utkuozdemir/nvidia_gpu_exporter:1.3.2` | applies `GPU_POWER_LIMIT_W` and `CPU_POWER_LIMIT_W`, saves the originals into `power-limits-state`, and re-applies every 60 s so a reboot does not silently undo them |

---

## 8. Ports and exposure, honestly

| port | who | reachable from |
|---|---|---|
| 80 | Traefik | everywhere `BIND_ADDRESS` allows; redirects to 443 |
| 443 | Traefik | same |
| everything else | nobody | only other containers on `llm-net` |

`BIND_ADDRESS` defaults to `0.0.0.0`. Set it to `127.0.0.1` (as the samples do)
for a single-machine install. It is a real control: it used to exist in `.env`
and be referenced nowhere, so a file that said `127.0.0.1` published on
`0.0.0.0` and answered the whole LAN.

---

## 9. Startup order

Compose starts what it can in parallel; the **one-shot** services exist to
prepare state. Their ordering is the only ordering the stack depends on:

```
tls-init ──────────────► traefik, argus (service_completed_successfully)
app-init ──────────────► app            (service_completed_successfully)
postgres ──────────────► app, litellm   (service_healthy)
embed-init ────────────► llamacpp-embed (service_completed_successfully)
prometheus-secrets ────► prometheus     (service_completed_successfully)
model-init ────────────► llamacpp       (service_completed_successfully)
power-limits ──────────► (no dependents)
```

`service_completed_successfully` is not decoration. **`llamacpp` does not start
at all until `model-init` exits 0** — so a download that fails, or a model file
that is missing, stops the engine rather than producing an engine that cannot
find its weights. On a machine with no network that also means `model-init`'s
Hugging Face size check has to pass even when every file is already on disk,
which is why the airgap bundle empties `LLAMACPP_HF_FILES`.

`tls-init` runs on **every** `up`, not just the first: it is what regenerates
the certificate when `LLM_DOMAIN` changes.

Everything long-running has `restart: unless-stopped`, so a crash loop is
visible as a container that keeps returning to `Restarting` rather than as a
service that is quietly absent.

---

## 10. When something is broken

The stack's own failure modes are specific, and most of them were discovered
rather than designed. This table is the short path from symptom to cause.

| symptom | almost always |
|---|---|
| `certificate verify failed` from a host tool (`dsh`, `curl`, an SDK) | the client does not trust the stack's CA (`config/traefik/certs/ca.crt`). `deploy/scripts/with-ca.sh` |
| `certificate verify failed` from **inside** a container | that container is missing the cert mount and its CA variable |
| `421 Invalid Host Header` from Argus | `--allowed-host` does not match the Host header Traefik forwards |
| Every hostname 404s, every container healthy | Traefik's `constraints:` no longer matches `COMPOSE_PROJECT_NAME` |
| Sign-in fails at the token step, no error in any log | `<domain>` does not resolve inside the network (the Traefik alias) |
| Several unrelated services crash-loop naming files that exist on disk | the checkout **moved**, and the containers still bind the old path. `make preflight` |
| The chat has no Argus tool with the `argus` profile on | `ARGUS_CHAT_CLIENT_TOKEN` is empty, or differs between the app and Argus (recreate both with `up -d`) |
| A model is served but `/model/info` shows a stale window | `MODEL_CONTEXT`/`MODEL_MAX_OUTPUT` changed without `up -d`, so LiteLLM did not re-render its config |
| The engine never starts, and `model-init` exited 1 | it could not fetch or verify the weights. **On a machine with no network this happens even when every file is already in `LLAMACPP_MODEL_DIR`** — `model-init` asks Hugging Face for the size first. Clear `LLAMACPP_HF_FILES` |
| Loading the model hits CUDA out of memory | raise `LLAMACPP_N_CPU_MOE` to keep more expert layers in system RAM |
| An over-budget person is refused on the API and still served in chat | the person's end-user budget is missing at the gateway (Admin → People re-provisions it) |
| `argus index` enumerates projects then every clone fails | the GitLab certificate is not trusted by **git** — a separate transport from the API |
| Nobody can log in after a reboot on a removable disk | the volume mounted after dockerd, so containers started against empty directories |

---

## 11. What is generated, and what you edit

| path | generated? | edit it? |
|---|---|---|
| `.env` | no | **yes — this is the configuration** |
| `docker-compose.yml` | no | rarely; it is the wiring |
| `config/**/*.yml` | no | yes, for behaviour the `.env` does not cover (alert rules, dashboards, scrape jobs) |
| `config/traefik/certs/*.crt` | **yes**, by `tls-init` | no |
| `config/prometheus/secrets/llamacpp.token` | **yes** | no |
| `config/engine/` (`models.ini`, `keep`, `targets.json`) | **yes**, by the app (Admin → Models) | no; the engine and Prometheus read it |
| `config/searxng/settings.yml` | no | rarely: the search engine's settings (engines, safe search); its key comes from `SEARXNG_SECRET` |
| `config/directory/` | **yes** — the list of people (no passwords) the app publishes for Argus | no; the directory itself is kept with a `.gitkeep` |
| `config/app/` | **yes** — the Settings page's pending `.env` changes | no |
| `config/argus/tls/` | empty; you drop a CA here | yes, in the airgap/private-CA case |
| `env-samples/*.env` | no | they are templates; copy one to `.env` |

See [configuration.md](configuration.md) for every variable and every file.
