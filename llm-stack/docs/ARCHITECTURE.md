# Architecture: every part of the stack, and how they fit

This is the reference for what runs, why it is there, and how a request travels
through it. For how to set each value, see [CONFIGURATION.md](CONFIGURATION.md).
For deploying, see the [top-level README](../../README.md).

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
│ auth │   │ gateway │    │ inference │   │    ui     │  │  observ. │
│authel│   │ litellm │    │ llamacpp  │   │ open-webui│  │ prometh. │
│admin │   │ +pg+red │    │ vllm      │   │ admin-pnl │  │ grafana  │
│redis │   │identity-│    │ ollama    │   │ argus     │  │ loki ... │
└──────┘   │ proxy   │    └───────────┘   └───────────┘  └──────────┘
           └─────────┘
```

Nothing but Traefik has a `ports:` entry. The inference engines, the databases
and the admin panel are reachable **only** from inside `llm-net`, which is why
the API gateway can be the single place that authenticates machine callers and
counts their tokens.

That single-ingress design is also why a broken Traefik is a total outage and a
broken Grafana is not — and why `tls-init` failing takes the whole stack down
with it (see §9).

---

## 2. Hostnames

`LLM_DOMAIN` in `.env` names the parent. Every service is
`https://<name>.<LLM_DOMAIN>`, and the certificate covers `<domain>` plus
`*.<domain>`, so one name is the whole configuration:

| hostname | service | auth in front of it |
|---|---|---|
| `auth.<domain>` | Authelia portal | none (it *is* the login) |
| `chat.<domain>` | Open WebUI | OIDC (its own session) |
| `admin.<domain>` | admin panel | Authelia `sso-chain` |
| `gateway.<domain>` | LiteLLM | Authelia `one_factor` |
| `api.<domain>` | llama.cpp **or** vLLM | Authelia, plus a key-injecting middleware |
| `api2.<domain>` | vLLM secondary (`multi-model`) | none in front — no Authelia chain and **no key injection**, so the caller presents `VLLM_API_KEY` itself |
| `argus.<domain>` | Argus MCP | none — per-caller GitLab PAT |
| `grafana.<domain>` | Grafana | OIDC (its own session) |
| `traces.<domain>` | Langfuse | OIDC (its own session) |
| `metrics.<domain>` | Prometheus | `sso-chain` |
| `alerts.<domain>` | Alertmanager | `sso-chain` |
| `logs.<domain>` | Loki | `sso-chain` |
| `node.<domain>` | node-exporter | `sso-chain` |
| `gpu.<domain>` | nvidia-smi-exporter | `sso-chain` |
| `cadvisor.<domain>` | cAdvisor | `sso-chain` |
| `s3.<domain>` | MinIO console | `default-chain` only |
| `<domain>` | nothing — the SSO landing redirect | — |

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
| *(always)* | `tls-init`, `prometheus-secrets`, `prometheus`, `alertmanager`, `grafana`, `node-exporter`, `power-limits`, `open-webui` | the minimum that is useful and cheap; TLS is not optional, and an LLM stack nobody can monitor is one nobody notices breaking |
| `proxy` | `traefik` | the ingress. Separate so a machine can run the engines without 80/443 |
| `auth` | `authelia`, `auth-init`, `redis`, `admin-panel` | single sign-on. Without it, `PROTECTED_CHAIN` must be the basic-auth chain |
| `gateway` | `litellm`, `postgres`, `redis`, `identity-proxy` | the API gateway, per-person keys and budgets |
| `llamacpp` | `llamacpp`, `model-init` | one of the two engine choices |
| `vllm` | `vllm` | the other engine. Mutually exclusive with `llamacpp` for the GPU |
| `multi-model` | `vllm-secondary` | a second, small model on the same card |
| `argus` | `argus`, `ollama` | the code index and its embedding model |
| `embed` | `ollama` | embeddings alone, without Argus |
| `smi` | `nvidia-smi-exporter`, `cpu-temp-exporter` | GPU/CPU telemetry via NVML and the host's own sensors |
| `dcgm` | `dcgm-exporter` | the alternative GPU exporter; heavier, more detail |
| `cadvisor` | `cadvisor` | per-container CPU/memory |
| `logging` | `loki`, `promtail` | log aggregation |
| `tracing` | `langfuse`, `langfuse-worker`, `clickhouse`, `minio`, `postgres`, `redis` | LLM request tracing |

`postgres` and `redis` appear in several profiles on purpose: they are shared,
and compose starts a service if **any** enabled profile names it.

The default sample is `gateway,proxy,auth,smi,llamacpp,argus`. Adding a profile
is an `.env` edit and `docker compose up -d`; compose then creates only the new
containers.

---

## 4. Network and volumes

**One network.** `llm-net`, a plain bridge. Every service joins it, and every
service reaches every other by its **service name** (`http://litellm:4000`,
`http://argus:7700`). Those names are Docker's embedded DNS, not `/etc/hosts`.

Three names are *also* resolvable, deliberately:

* `auth.<domain>` → Traefik. Traefik carries an explicit network alias for it,
  because the OIDC token exchange is server-to-server: Grafana, Open WebUI and
  Langfuse call the issuer **directly** rather than through the browser.
  Without that alias `auth.<domain>` does not resolve inside a container and
  every login fails at the token step — while `/healthz` on every service still
  looks perfect.
* `host.docker.internal` → the host, via `extra_hosts: host-gateway`. Two
  services need it, for the same reason — the thing they talk to lives on the
  host rather than in a container: `argus`, for a GitLab running on the same
  machine, and `cpu-temp-exporter`, for the hardware monitor it reads CPU
  temperature from. Inside a container `localhost` means the container itself.

**Nineteen named volumes.** They are prefixed with `COMPOSE_PROJECT_NAME`, so
the real names are `llmservice_grafana-data` and so on. `.env` is the only
place the project name is set, and changing it after the first start gives you
a stack that boots with none of its data.

| volume | holds | safe to delete? |
|---|---|---|
| `traefik-certs` | the TLS certificate and key, generated by `tls-init` | yes — regenerated, and browsers re-warn |
| `authelia-data` | Authelia's SQLite database: sessions, TOTP secrets, OIDC grants | yes, but everyone's 2FA enrolment and sessions go with it |
| `postgres-data` | LiteLLM's keys/spend, **and Langfuse's traces** | no — this is the billing and audit history |
| `argus-data` | the code index, symbol embeddings, audit and ACL cache | yes — rebuilt by re-indexing (minutes to hours) |
| `ollama-models` | `nomic-embed-text` and anything else pulled | yes — re-pulled, ~274 MB |
| `open-webui-data` | chat history, accounts, uploaded files | no |
| `grafana-data` | Grafana's own users, API keys, dashboard edits | yes if dashboards are provisioned from `config/grafana/` (they are) |
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
| `power-limits-state` | the "original" power limits, so they can be restored | yes |

`make backup` archives them (`scripts/backup.sh`). `make clean` deletes all of
them, and asks for the word `PURGE` first.

---

## 5. Authentication: three mechanisms, and why not one

This is the part people get wrong, because the stack deliberately runs three
different schemes side by side. Each exists because the alternative was worse.

### 5.1 Authelia forwardAuth — for services with no login of their own

Prometheus, Alertmanager, Loki, cAdvisor, the exporters and the admin panel have
no account system at all. Traefik asks Authelia about **every request** before
it reaches them:

```
browser → traefik → (forwardAuth) authelia:9091/api/authz/forward-auth
                         │
                   200 + Remote-User/Remote-Groups headers → let through
                   401/302 → redirect to https://auth.<domain>
```

The middleware is `authelia@file`; the chain that wraps it with security headers
is `sso-chain@file`. Authelia's `access_control` rules in
`config/authelia/configuration.template.yml` decide policy per hostname
(`bypass`, `one_factor`, `two_factor`), and `default_policy: deny` means a new
hostname is closed until it is listed.

**`PROTECTED_CHAIN` in `.env` is the switch.** With the `auth` profile on it is
`sso-chain@file`; without it, `protected-chain@file`, which is HTTP basic auth
against a file `auth-init` writes from `PROXY_AUTH_USER`/`PROXY_AUTH_PASSWORD`.

### 5.2 OIDC — for services with their own session

Grafana, Open WebUI and Langfuse each have their own user database and their own
login screen. Rather than deleting those, they delegate the *login* to Authelia
and keep the session:

```
browser → chat.<domain>/oauth/oidc → authelia → back to the app with a code
                                             → app exchanges the code
                                               server-to-server over https://auth.<domain>
```

The secrets are not in `config/authelia/clients.yml` in plaintext: `auth-init`
writes it on every `up` with the client secrets **hashed**, which is why the
file is generated rather than committed.

Two consequences worth knowing:

* The server-to-server token exchange is why `auth.<domain>` must resolve inside
  the network (§4), and why every such container carries the stack's certificate
  as a trusted CA.
* Grafana is the exception to "containers verify the certificate": Grafana is
  Go, reads the **operating system** trust store and honours no CA environment
  variable, so its OAuth client runs with
  `GF_AUTH_GENERIC_OAUTH_TLS_SKIP_VERIFY_INSECURE=true`. That is a deliberate,
  documented downgrade on one in-network call, not an oversight.

### 5.3 Bearer tokens — for machines and for Argus

The gateway and Argus authenticate the caller, not a browser session:

* **`gateway.<domain>`** accepts a per-person LiteLLM key (`sk-...`), minted in
  the admin panel. Authelia's `one_factor` rule on that hostname allows the
  `client_credentials` grant, which is how a machine gets in without a browser.
* **`api.<domain>`** is gated by the same chain, and Traefik then **injects the
  engine's own key** with a label-defined middleware (`llamacpp-key@docker` /
  `vllm-key@docker`). The caller never needs `LLAMACPP_API_KEY`; it is added on
  the way past, and the engine port is not reachable any other way.
* **`argus.<domain>`** has no Authelia chain at all, and that is the point:
  Argus needs the caller's **own GitLab personal access token**, untouched, so
  it can answer with exactly the repositories that person may read. Replacing it
  with an SSO session would erase the per-caller identity the ACL is built on.

  Argus still has to map a chat user's email to a GitLab username, which means
  reading Authelia's account list — and `users.yml` holds password hashes at
  mode 600. So the admin panel publishes a **hash-free** copy into
  `config/authelia/directory/users.yml` (usernames, emails, display names;
  disabled accounts left out) and Argus mounts only that directory. The panel
  rewrites it on every account change and re-syncs every 30 s, so a hand edit of
  `users.yml` reaches Argus without a restart.

---

## 6. Request flows

### 6.1 A person chats in the browser

```
browser ──https──► traefik ──► open-webui ──http──► identity-proxy ──► litellm ──► llamacpp
                                   │                                      │
                    OIDC login via authelia                     spend rows in postgres
                    forwards X-OpenWebUI-User-Email            per-person budget enforced
```

Open WebUI talks to LiteLLM with **one shared key**, which is why it also
forwards the signed-in person's email. LiteLLM maps that header onto its
internal user (`user_header_mappings` in `config/litellm/config.yaml`), so chat
spend lands on the same identity as the person's API key and their total is one
number instead of two.

`identity-proxy` sits in that path for the enforcement half: the internal-user
role *attributes* spend, but the shared key has no budget attached, so an
over-budget person was still served on the web UI. The proxy plus an end-user
budget is what actually returns 429.

### 6.2 A tool or agent calls the API

```
curl/agent ──https──► traefik ──► litellm ──► engine
   Authorization: Bearer sk-<person's key>
```

No Open WebUI, no identity proxy: the key *is* the identity. The admin panel
mints it with `user_id` set to the person's email, which is the identifier the
whole stack agrees on.

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
open-webui ──http (inside llm-net)──► argus
   Authorization: Bearer <ARGUS_CHAT_CLIENT_TOKEN>
   X-OpenWebUI-User-Email: <the signed-in person>
```

This path deliberately does not go through Traefik. The chat-client token is
only accepted from inside the network, so a leaked one cannot claim someone
else's email from outside.

### 6.5 Metrics

```
prometheus ──scrape──► node-exporter, nvidia-smi-exporter, cpu-temp-exporter,
                       llamacpp / vllm, cadvisor, dcgm, traefik, loki, grafana
     │
     ├─ rules in config/prometheus/rules/*.yml
     ├─ firing alerts ──► alertmanager ──► (filesystem notifier)
     └─ datasource ──► grafana ──► 9 provisioned dashboards
```

Per-person token usage is **not** scraped: LiteLLM's `/metrics` is an
enterprise feature on current builds, and vLLM's metrics carry token counts but
no user dimension. That is why Grafana has a *Postgres* datasource reading the
same spend tables the LiteLLM admin UI reads.

---

## 7. The services

### 7.1 Ingress and TLS

| service | image | what it does |
|---|---|---|
| `traefik` | `traefik:v3.6.7` | the only published ports. Terminates TLS, routes by `Host(...)` label, applies middleware chains. Docker provider scoped to this compose project by label, file provider watches `config/traefik/dynamic/` |
| `tls-init` | `authelia/authelia:4.39` | one-shot. Generates the self-signed certificate for `<domain>` and `*.<domain>` into `traefik-certs`, reuses it, and regenerates it when `LLM_DOMAIN` changes. Also builds `/certs/bundle.crt` — the public roots, this certificate, and **every `.crt`/`.pem` dropped in `config/ca/`** — so one company CA can be trusted stack-wide, and copies the public cert and the bundle to `config/traefik/certs/` for host-side tools |

Traefik's Docker provider is constrained by
``Label(`com.docker.compose.project`, ...)``. Without that, router names are a
flat global namespace across the whole Docker daemon and an unrelated project
that happens to define `traefik.http.routers.authelia` silently replaces this
stack's auth route — logins then break with nothing in any log.

The default certificate is generated by `tls-init` rather than Traefik's own,
because Traefik makes its default certificate **in memory and anew on every
restart**, so nothing could ever trust it.

### 7.2 Identity

| service | image | what it does |
|---|---|---|
| `authelia` | `authelia/authelia:4.39` | forwardAuth gate and OIDC provider. Reads `configuration.template.yml` with the `template` filter, so `{{ env "LLM_DOMAIN" }}` is expanded at startup and the domain lives in `.env` only |
| `auth-init` | `authelia/authelia:4.39` | one-shot. Writes `clients.yml` (OIDC clients, secrets **hashed**), the admin account into `users.yml`, and Traefik's `users.htpasswd`. Refuses to start Authelia against a database encrypted with a different key |
| `redis` | `redis:7-alpine` | Authelia sessions, LiteLLM's response and auth caches |
| `admin-panel` | built from `deploy/admin-panel/` | per-person provisioning: creates the Authelia account, mints the LiteLLM key, sets credit, rotates keys. Its console is gated on Authelia's `Remote-Groups` header, not on the route |
| `identity-proxy` | built from `deploy/identity-proxy/` | turns Open WebUI's forwarded identity header into the `user` field LiteLLM enforces budgets against |

### 7.3 Inference

| service | image | what it does |
|---|---|---|
| `llamacpp` | `ghcr.io/ggml-org/llama.cpp:server-cuda` | GGUF inference. The default for a single 24 GB card, because MoE experts can live in system RAM (`LLAMACPP_N_CPU_MOE`) and be paged from NVMe |
| `model-init` | same | one-shot. Downloads the GGUF shards from Hugging Face, verifies SHA-256, optionally unpacks a custom engine tarball, then exits. **`llamacpp` waits for it to exit 0** (`service_completed_successfully`), so a failure here stops the engine rather than producing one that cannot find its weights |
| `vllm` | `vllm/vllm-openai:latest` | the alternative engine, for safetensors/AWQ models |
| `vllm-secondary` | same | a second, small model on the same GPU (`multi-model` profile), reached at `api2.<domain>` |
| `ollama` | `ollama/ollama:0.5.7` | embeddings only (`nomic-embed-text`). Argus calls it for every `docs_search` query |

Exactly one of `llamacpp`/`vllm` should hold the GPU; `ENGINE_API_BASE`,
`ENGINE_API_KEY` and `ENGINE_MODEL` in `.env` are what point the gateway at the
one you chose.

### 7.4 Gateway

| service | image | what it does |
|---|---|---|
| `litellm` | `ghcr.io/berriai/litellm:main-stable` | `/v1` OpenAI-compatible endpoint. Per-person keys, spend, budgets, retries, optional Redis cache. Renders `${MODEL_NAME}`-style tokens in its config from the environment at startup |
| `postgres` | `pgvector/pgvector:0.8.0-pg16` | one instance, three databases: `litellm`, `langfuse`, `argus` (created by `config/postgres/init/01-create-databases.sql`, with the `vector` extension). Argus uses it only when `ARGUS_VECTOR_BACKEND=pgvector` |

Only one model is advertised, under one name. Aliases were removed on purpose:
clients cached `/model/info` and showed every alias as a separate model.

### 7.5 User interfaces

| service | image | what it does |
|---|---|---|
| `open-webui` | `ghcr.io/open-webui/open-webui:main` | chat. OIDC login, forwards the person's identity to the gateway, and registers Argus as an MCP tool when `ARGUS_CHAT_CLIENT_TOKEN` is set |
| `argus` | built from this repository (`target: server`) | MCP code-search server over the private GitLab index. Every answer is also written as a JSON audit line — who asked, which repositories were consulted, what was returned — which is what the Argus dashboard reads |

### 7.6 Observability

| service | image | profile | what it does |
|---|---|---|---|
| `prometheus` | `prom/prometheus:v3.1.0` | always | scrapes 13 jobs; rules in `config/prometheus/rules/` |
| `prometheus-secrets` | `prom/prometheus:v3.1.0` | always | one-shot. Puts the engine's scrape token into a volume Prometheus mounts read-only |
| `alertmanager` | `prom/alertmanager:v0.28.0` | always | receives firing alerts. The default receiver is `null`, so alerts are visible in the UI and sent nowhere until you configure one |
| `grafana` | `grafana/grafana:11.5.1` | always | 9 provisioned dashboards (Prometheus, Loki, Alertmanager and Postgres datasources), including **Argus**: index size, query latency and audit events |
| `node-exporter` | `prom/node-exporter:v1.9.0` | always | host CPU, memory, disk, network |
| `nvidia-smi-exporter` | `utkuozdemir/nvidia_gpu_exporter:1.3.2` | `smi` | GPU via NVML |
| `cpu-temp-exporter` | `python:3.13-slim` + `deploy/cpu-temp-exporter/exporter.py` | `smi` | CPU package temperature, which NVML does not report |
| `dcgm-exporter` | `nvcr.io/nvidia/k8s/dcgm-exporter` | `dcgm` | the heavier alternative to `nvidia-smi-exporter` |
| `cadvisor` | `gcr.io/cadvisor/cadvisor:v0.52.1` | `cadvisor` | per-container CPU and memory |
| `loki` + `promtail` | `grafana/loki:3.4.1`, `grafana/promtail:3.4.1` | `logging` | log aggregation. Promtail reads the Docker socket and container log files |
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

Compose starts what it can in parallel; five services are **one-shot** and exist
to prepare state. Their ordering is the only ordering the stack depends on:

```
tls-init ──────────────► traefik        (service_completed_successfully)
auth-init ─┬───────────► authelia       (service_completed_successfully)
           └───────────► (writes clients.yml, users.yml, users.htpasswd)
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

`open-webui` declares `llamacpp` and `vllm` as dependencies but only
`service_started`, and compose enforces a dependency only when that service is
in an active profile — so a gateway-only deployment without an engine profile
still brings the chat UI up.

Everything long-running has `restart: unless-stopped`, so a crash loop is
visible as a container that keeps returning to `Restarting` rather than as a
service that is quietly absent.

---

## 10. When something is broken

The stack's own failure modes are specific, and most of them were discovered
rather than designed. This table is the short path from symptom to cause.

| symptom | almost always |
|---|---|
| `certificate verify failed` from a host tool (`dsh`, `curl`, an SDK) | the client was not told about the self-signed certificate. `llm-stack/scripts/with-ca.sh` |
| `certificate verify failed` from **inside** a container | that container is missing the cert mount and its CA variable |
| `421 Invalid Host Header` from Argus | `--allowed-host` does not match the Host header Traefik forwards |
| Every hostname 404s, every container healthy | Traefik's `constraints:` no longer matches `COMPOSE_PROJECT_NAME` |
| Login fails at the token step, no error in any log | `auth.<domain>` does not resolve inside the network (the Traefik alias) |
| Authelia crash-loops on `configuration.template.yml: read-only file system` | `/config` is empty — see the next row |
| Several unrelated services crash-loop naming files that exist on disk | the checkout **moved**, and the containers still bind the old path. `make preflight` |
| Open WebUI says `Initialized 0 tool server(s)` with the token set | a persisted `tool_server.connections` row in `webui.db` shadows `.env` |
| A model is served but `/model/info` shows a stale window | `MODEL_CONTEXT`/`MODEL_MAX_OUTPUT` changed without `up -d`, so LiteLLM did not re-render its config |
| The engine never starts, and `model-init` exited 1 | it could not fetch or verify the weights. **On a machine with no network this happens even when every file is already in `LLAMACPP_MODEL_DIR`** — `model-init` asks Hugging Face for the size first. Clear `LLAMACPP_HF_FILES` |
| Loading the model hits CUDA out of memory | raise `LLAMACPP_N_CPU_MOE` to keep more expert layers in system RAM |
| An over-budget person is refused on the API and still served in chat | `identity-proxy` is not in the path, or the end-user budget is missing |
| `argus index` enumerates projects then every clone fails | the GitLab certificate is not trusted by **git** — a separate transport from the API |
| Nobody can log in after a reboot on a removable disk | the volume mounted after dockerd, so containers started against empty directories |

---

## 11. What is generated, and what you edit

| path | generated? | edit it? |
|---|---|---|
| `.env` | no | **yes — this is the configuration** |
| `docker-compose.yml` | no | rarely; it is the wiring |
| `config/**/*.yml` | no | yes, for behaviour the `.env` does not cover (alert rules, dashboards, scrape jobs) |
| `config/authelia/clients.yml` | **yes**, every `up` | no — `auth-init` rewrites it |
| `config/authelia/users.yml` | created by `auth-init` if absent | via the admin panel |
| `config/traefik/auth/users.htpasswd` | **yes**, every `up` | no |
| `config/traefik/certs/*.crt` | **yes**, by `tls-init` | no |
| `config/prometheus/secrets/llamacpp.token` | **yes** | no |
| `config/authelia/directory/` | **yes** — the hash-free account list the admin panel publishes for Argus | no; the directory itself is kept with a `.gitkeep` |
| `config/argus/tls/` | empty; you drop a CA here | yes, in the airgap/private-CA case |
| `env-samples/*.env` | no | they are templates; copy one to `.env` |

See [CONFIGURATION.md](CONFIGURATION.md) for every variable and every file.
