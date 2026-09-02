# LLMService

A self-hosted LLM stack in one `docker-compose.yml`: GPU inference, a chat UI,
an API gateway with per-user keys, single sign-on, and end-to-end observability
— hardware, containers, serving performance, logs, and prompt-level traces.

**Traefik is the only thing exposed.** No service publishes a port of its own;
every request arrives on :80/:443 and is authenticated at the edge. Optional
pieces are gated behind Compose **profiles**, so the same file runs a lean
inference-only setup or the full stack.

```
                    +===================================+
   everything ----->|  Traefik   :80 -> :443 (TLS)      |
                    |  hostname routing + Authelia SSO  |
                    +==+=========+=========+============+
                       |         |         |
              +--------v-----+   |   +-----v-------+
              | Open WebUI   |   |   | Grafana     |
              +------+-------+   |   +------+------+
                     | OpenAI    |          |
   your code --->+---v------+    |   +------v------+
   (openai SDK)  | LiteLLM  |--->|   | Prometheus  |<-- node-exporter
                 | per-user |    |   |             |<-- cAdvisor
                 |   keys   |    |   |             |<-- GPU exporter
                 +----+-----+    v   |             |<-- Traefik
                      |   +-------------+   +------+------+
                      |   |    vLLM     |          |
                      |   |  /v1  + GPU |          v
                      |   +------+------+   +--------------+
                      | traces  | /metrics  | Alertmanager |
                 +----v-----+   |           +--------------+
                 | Langfuse |<--+
                 +----------+       Loki/Promtail --> Grafana
```

---

## Guided setup

One command that asks for what it cannot safely guess, then runs everything
else in the order it has to happen:

```bash
./scripts/setup.sh          # Linux, macOS, WSL
.\scripts\setup.ps1         # Windows
```

`--dry-run` prints the whole plan without changing anything; `--defaults`
accepts every default and asks nothing. It is safe to re-run: existing secrets
are kept and every question offers the current value as its default, including
which profiles are already enabled.

Full walkthrough, and the four things it deliberately leaves to you, in
[docs/SETUP.md](docs/SETUP.md).

## Fast deploy

If you would rather run the steps yourself, on a fresh Linux server with an
NVIDIA GPU:

```bash
git clone https://github.com/aliGhadyani/hermes-argus.git && cd hermes-argus/llm-stack
```

```bash
sudo ./scripts/install-requirements.sh
```

```bash
./scripts/bootstrap.sh && ./scripts/gen-auth.sh --sync && ./scripts/up.sh
```

That is the whole path. `bootstrap` writes `.env` and generates every secret and
the TLS certificate; `gen-auth --sync` builds the user database from
`config/authelia/team.yml`; `up` pulls images, starts the stack and waits for the
model to load. Verify with:

```bash
./scripts/health.sh && ./scripts/smoke-test.sh && ./scripts/audit-auth.sh
```

### Three files are generated, never cloned

`.env`, `config/authelia/users.yml` and `config/authelia/clients.yml` hold
secrets and are gitignored. The compose file bind-mounts all three, so **running
`docker compose up` before `bootstrap` and `gen-auth` will fail** — Docker
creates directories where the files should be. Run the scripts first; they are
idempotent and safe to re-run.

### Before you expose it

| Change | Why |
|---|---|
| `LLM_DOMAIN=your.domain` in `.env` | `llm.localhost` only resolves on the local machine |
| Real certificates | `gen-certs` issues a private CA — fine internally, not for the public internet |
| Edit `config/authelia/team.yml`, re-run `gen-auth --sync` | The shipped list is an example; extra users are removed, missing ones created |
| `BIND_ADDRESS=0.0.0.0` | Defaults to loopback, so nothing is reachable from the network |

Only Traefik publishes ports (80/443). Every other service is reachable solely
through it, so the firewall surface is those two ports.

### Choosing profiles

```bash
./scripts/up.sh --profiles "smi,proxy,auth,gateway"
```

The default set is monitoring + SSO + gateway. Add `argus` for the code index,
`logging` for Loki, `tracing` for Langfuse. See
[Which services run](#which-services-run).

### The one image that is not public

Every image pulls from a public registry except **Argus**, which is built from a
separate repository and referenced as `argus:${ARGUS_VERSION}`. If you enable the
`argus` profile you must load it first, or compose fails with *image not found*:

```bash
docker load < argus-v1.1.0.tar.gz
```

That tarball ships alongside a release rather than in git — it is 119 MB. Build
it yourself from the Argus repository if you would rather not trust an artefact.

### GPU-matched model

The default `.env` serves a small model so the stack comes up quickly. For a
production checkpoint pick one matched to the card:

```bash
./scripts/get-models.sh --list
```

```bash
./scripts/get-models.sh --gpu auto --model 3.8 --apply
```

`--apply` rewrites `.env` and the gateway config, then
`docker compose up -d --force-recreate vllm litellm` picks it up. See
[docs/MODELS.md](docs/MODELS.md) — the quantisation format is decided by compute
capability, and getting it wrong means vLLM refuses to start.

---

## The services

### Core — always running

| Service | What it does |
|---|---|
| **vLLM** | Inference engine. OpenAI-compatible API from your GPU, continuous batching, paged attention. Exports rich Prometheus histograms. |
| **Open WebUI** | Chat interface. Multi-user, conversation history, document upload. |
| **Prometheus** | Metrics store. Scrapes every component, evaluates 21 alert rules. |
| **Grafana** | Dashboards. Six provisioned from JSON — no clicking. |
| **Alertmanager** | Groups, deduplicates and routes firing alerts. |
| **node-exporter** | Host CPU, memory, disk, network. |
| **cAdvisor** | Per-container resource usage. |

### Optional — enabled by profile

| Profile | Service | Why |
|---|---|---|
| `proxy` | **Traefik** | TLS and hostname routing. **Required** — without it nothing is reachable. |
| `auth` | **Authelia** | Single sign-on for every service; one user database. |
| `gateway` | **LiteLLM** | Per-user API keys, budgets, rate limits, usage attribution. |
| `smi` | **nvidia-smi exporter** | GPU utilisation, VRAM, temperature, power. Works under WSL2. |
| `dcgm` | **DCGM exporter** | SM occupancy, PCIe, NVLink, ECC. Bare-metal Linux only. |
| `logging` | **Loki + Promtail** | Searchable container logs in Grafana. |
| `tracing` | **Langfuse** | Prompt/response traces, cost per call, evaluations. |
| `argus` | **Argus** | Code index and documentation server. Exposes 16 MCP tools to an agent: symbol lookup and cross-repo search over your GitLab repositories, plus offline documentation packs (Windows SDK/WDK, MSVC C++, PowerShell). |
| `multi-model` | **second vLLM** | Two models warm at once. |
| `homepage` | **Homepage** | Landing page. |

Postgres, Redis, ClickHouse and MinIO start automatically when a profile needs
them.

**Memory matters.** Measured on a 32 GB host: vLLM ~2.6 GB working set,
Open WebUI ~750 MB, the whole monitoring tier ~300 MB, LiteLLM ~700 MB (one
worker), Langfuse + ClickHouse ~2.9 GB, Argus ~75 MB idle (it memory-maps the
packs rather than loading them). Enable only what you use.

---

## Requirements

| | |
|---|---|
| **GPU** | NVIDIA. See [docs/MODELS.md](docs/MODELS.md) — the quantisation format depends on your card's compute capability |
| **Driver** | 535+; `nvidia-smi` must work on the host |
| **Docker** | Engine 24+ with the Compose v2 plugin |
| **GPU in containers** | Docker Desktop WSL2 integration, or `nvidia-container-toolkit` on Linux |
| **Disk** | ~40 GB — weights ~20 GB, images ~20 GB, metrics ~5 GB |
| **RAM** | 16 GB; 32 GB with `tracing` |
| **Ports** | **80 and 443 only** |

```bash
docker run --rm --gpus all nvidia/cuda:12.4.1-base-ubuntu22.04 nvidia-smi
```

If that fails, nothing else will work.

---

## Setup

**Windows (PowerShell):**

```powershell
.\scripts\install-requirements.ps1
```

```powershell
.\scripts\bootstrap.ps1
```

```powershell
.\scripts\gen-auth.ps1
```

```powershell
.\scripts\setup-hosts.ps1
```

```powershell
.\scripts\up.ps1
```

**Linux:**

```bash
sudo ./scripts/install-requirements.sh
```

```bash
./scripts/bootstrap.sh && ./scripts/gen-auth.sh && sudo ./scripts/setup-hosts.sh && ./scripts/up.sh
```

| Step | Does |
|---|---|
| `install-requirements` | Docker, GPU plumbing, NVIDIA Container Toolkit (Linux), windows_exporter (Windows) |
| `bootstrap` | `.env`, all secrets, TLS certificate + private CA |
| `gen-auth` | Authelia secrets, user database, OIDC clients |
| `setup-hosts` | `*.llm.localhost` entries so **CLI tools** resolve — browsers already do |
| `up` | Pull, start, wait for the model to load |

Trust the CA once so browsers stop warning:

```powershell
.\scripts\gen-certs.ps1 -Trust
```

Then verify:

```powershell
.\scripts\smoke-test.ps1
```

```bash
./scripts/health.sh && ./scripts/audit-auth.sh
```

---

## Where things are

Everything is a hostname on :443. There are **no direct ports**.

| URL | Service | Auth |
|---|---|---|
| `https://chat.llm.localhost` | Open WebUI | SSO |
| `https://grafana.llm.localhost` | Grafana | SSO |
| `https://gateway.llm.localhost/v1` | LiteLLM API | **per-user API key** |
| `https://api.llm.localhost/v1` | vLLM direct | Authelia token |
| `https://metrics.llm.localhost` | Prometheus | SSO, `admins` only |
| `https://alerts.llm.localhost` | Alertmanager | SSO, `admins` only |
| `https://logs.llm.localhost` | Loki | SSO, `admins` only |
| `https://cadvisor.llm.localhost` | cAdvisor | SSO, `admins` only |
| `https://node.llm.localhost` · `gpu.` | exporters | SSO, `admins` only |
| `https://traces.llm.localhost` | Langfuse | SSO |
| `https://argus.llm.localhost/mcp` | Argus (MCP) | **GitLab PAT** as bearer |
| `https://auth.llm.localhost` | Login portal | — |

The Traefik dashboard is **disabled** — it is a plain reverse proxy. `/ping` and
`/metrics` stay on an internal entrypoint for the healthcheck and Prometheus.

---

## Team access

Membership is declarative. Edit `config/authelia/team.yml`:

```yaml
users:
  - username: alice
    displayname: Alice Example
    email: alice@example.com
    groups:
      - users
```

```bash
./scripts/gen-auth.sh --sync-dry-run    # show the plan
./scripts/gen-auth.sh --sync            # apply
```

| Situation | Action |
|---|---|
| In `team.yml`, not in `users.yml` | **created** — random password, printed once |
| In both | password **kept**, groups/name/email **updated** |
| In `users.yml`, not in `team.yml` | **removed** |

`users` gets chat and Grafana. `admins` adds Prometheus, Alertmanager and the
other infrastructure endpoints. The sync refuses to run if it would leave nobody
in `admins`. Authelia reloads within a minute — no restart.

---

## API access and per-user usage

A coding harness or SDK client **cannot use the web UI** — it needs an endpoint
and a key. Give each developer their own:

```bash
curl -X POST https://gateway.llm.localhost/key/generate -H "Authorization: Bearer $LITELLM_MASTER_KEY" -H 'Content-Type: application/json' -d '{"key_alias":"alice","user_id":"alice","models":["local"],"max_budget":50,"budget_duration":"30d","rpm_limit":60}'
```

```
base_url: https://gateway.llm.localhost/v1
api_key:  sk-...
model:    local
```

**Why a gateway key rather than `VLLM_API_KEY`:** vLLM's metrics carry a
`model_name` label but no user dimension — it only ever sees one key, so usage
cannot be attributed to a person. LiteLLM records tokens and spend per key:

```bash
curl -H "Authorization: Bearer $LITELLM_MASTER_KEY" https://gateway.llm.localhost/spend/logs
```

Open WebUI is pointed at the gateway too, so chat traffic is attributed as well.

> Token counts are always recorded, but **spend stays 0.00 for a local model**
> until you set `input_cost_per_token` / `output_cost_per_token` in
> `config/litellm/config.yaml`. Until then `max_budget` is decorative — use
> `rpm_limit`.

Full details: **[docs/AUTHENTICATION.md](docs/AUTHENTICATION.md)**.

---

## Customising

Everything is driven by `.env`.

### Which services run

```bash
COMPOSE_PROFILES=smi,proxy,auth,gateway
```

`proxy` is effectively mandatory — it is the only ingress. Override for one run
with `./scripts/up.sh --profiles "..."`.

Add `argus` to run the code index and documentation server — see
[docs/ARGUS.md](docs/ARGUS.md). By default it uses a named volume and starts
with an empty index; to reuse an index that already exists on the host:

```bash
docker compose -f docker-compose.yml -f deploy/argus-local.yml up -d
```

### The model

Pick a build matched to your GPU; the quantisation format is decided by compute
capability, not preference. **NVFP4 needs Blackwell and will not run on a 3090
or an H200.** The reverse mistake matters too: on a 96 GB or 141 GB card there
is no reason to quantise a 27B to 4 bits at all. `get-models` knows the right
build for the 3090, 4090, 5090, RTX PRO 6000 and H200 —
`./scripts/get-models.sh --list`.

```bash
./scripts/get-models.sh --list
```

```bash
./scripts/get-models.sh --gpu auto --model 3.6 --apply
```

Sizing tables, **context-window arithmetic**, MTP notes and format
compatibility: **[docs/MODELS.md](docs/MODELS.md)**.

The served name is derived from the checkpoint, so the Open WebUI picker,
`/v1/models`, LiteLLM's spend logs and every Grafana `model_name` label all show
the real thing (`Qwen2.5-7B-Instruct`, not `default`). `switch-model` keeps the
gateway config in step and recreates both containers.

To switch manually:

```bash
./scripts/switch-model.sh /models/NAME --max-model-len 8192
```

### Tuning inference

| Variable | Effect |
|---|---|
| `VLLM_MAX_MODEL_LEN` | Context window. **Lower this first** if startup fails on KV cache size. What fits depends on the weights — see [docs/MODELS.md](docs/MODELS.md). |
| `VLLM_GPU_MEMORY_UTILIZATION` | Fraction of the card claimed at startup. |
| `VLLM_MAX_NUM_SEQS` | Concurrent sequences. Lower = less memory, less throughput. |
| `VLLM_EXTRA_ARGS` | Appended verbatim — quantisation, KV dtype, tool calling. |
| `--kv-cache-dtype fp8` | In `VLLM_EXTRA_ARGS`. Halves KV cache cost; often the difference between 32K and 16K context. |

⚠️ Keep `--enable-auto-tool-choice --tool-call-parser hermes`. Open WebUI sends
`tool_choice: "auto"` on every request and vLLM returns **400** without them.

### Alerts

Rules in `config/prometheus/rules/`. `slo.yml` is **deliberately empty** —
latency SLOs encode what your users tolerate. Measure with
`./scripts/benchmark.sh`, then set thresholds. Delivery is off by default;
receivers go in `config/alertmanager/alertmanager.yml`.

### Dashboards

Provisioned from `config/grafana/dashboards/*.json`. Edit the files, not the UI —
UI edits are overwritten within 30 seconds. Use "Save As" for experiments.

---

## Scripts

Every script has both a `.ps1` and a `.sh` form. Some `.ps1` files are thin
wrappers that call the bash implementation through Git Bash.

| Script | Does |
|---|---|
| `install-requirements` | Install/verify Docker, GPU plumbing, host exporter |
| `bootstrap` | `.env`, secrets, certificates |
| `gen-auth` | Authelia secrets, OIDC clients, `--sync` the user list |
| `setup-hosts` | Hostname entries so CLI tools resolve |
| `up` / `down` | Start (waits for the model) / stop |
| `health` | Per-component probe from inside the network |
| `smoke-test` | Models, auth, completion, streaming, metrics |
| `audit-auth` | Full authentication audit — real logins, real token exchange |
| `benchmark` | Concurrency sweep: TTFT, TPOT, throughput |
| `get-models` | Pick + download a build matched to your GPU |
| `fetch-model` | Resumable download of any HF repo into `models/` |
| `switch-model` | Change model, recreate only vLLM |
| `gen-certs` | Private CA and wildcard certificate (`-Trust` installs it) |
| `get-token` | Short-lived API token for machine clients |
| `backup` | Archive/restore stateful volumes |
| `logs` | Tail logs |
| `fix-line-endings.py` | Normalise CRLF → LF (run if you copy the tree to Linux) |

---

## Reading the dashboards

Six ship provisioned: **LLM Overview**, **Resources**, **GPU Hardware**,
**Host & Containers**, **Logs**, **Stack Health & Alerts**.

**LLM Overview** is the daily one:

- **Requests queued** — the capacity signal. Persistently above zero means
  arrival rate exceeds throughput.
- **TTFT** — perceived responsiveness. Driven by prefill *and* queue wait, so
  read it beside queue depth: high TTFT with an empty queue means long prompts;
  with a full queue it means overload.
- **TPOT** — inter-token latency. `1 / TPOT` is the tokens/sec one user sees.
- **KV cache used** — above ~95% vLLM preempts and recomputes, showing up as
  latency spikes at an unchanged request rate.

**Resources** covers the whole machine — CPU by mode and per core, memory, GPU
utilisation vs memory bandwidth, VRAM, temperature, fan, power vs limit, clocks,
and **throttle reasons**. Watch for temperature entering the throttle zone while
power draw *drops*: that is the card down-clocking.

---

## Troubleshooting

**vLLM exits immediately.** `docker logs vllm`:
- *"No available memory for the cache blocks"* — lower `VLLM_MAX_MODEL_LEN`.
- *CUDA out of memory* — the model does not fit; use a quantised build.
- *"UVA is not available"* — WSL2. Keep `VLLM_USE_V2_MODEL_RUNNER=0`.
- *401 from HuggingFace* — gated repo; set `HF_TOKEN`.

**Open WebUI shows no models / chat fails.** `VLLM_EXTRA_ARGS` must contain
`--enable-auto-tool-choice --tool-call-parser hermes`.

**Traefik returns 404 for a running service.** Traefik refuses to route
containers whose Docker healthcheck is not passing. Check `docker compose ps`.

**A config change had no effect.** Labels and environment are baked in at
container creation. `docker compose up -d` to recreate, not `restart`.

**curl cannot resolve `chat.llm.localhost`.** Run `setup-hosts`. Browsers
resolve `*.localhost` natively; CLI tools do not.

**TLS errors from curl or PowerShell.** The private CA publishes no CRL, and
Windows treats "revocation unknown" as fatal. Use
`--ssl-no-revoke --cacert config/traefik/certs/ca.crt`, or trust the CA with
`gen-certs.ps1 -Trust`.

**Login problems.** `./scripts/audit-auth.sh` tests every client end to end and
names the fix. See [docs/AUTHENTICATION.md](docs/AUTHENTICATION.md).

---

## Backups

```bash
./scripts/backup.sh
```

Archives chat history, dashboards, metrics and the databases. The model cache is
skipped by default (large, re-downloadable); add `--include-model-cache`.

`.env` holds every secret and is gitignored, as are `backups/` and `.keys/`.

---

## Layout

```
docker-compose.yml       all services, profiles, volumes, networks
.env.example             configuration template
docs/AUTHENTICATION.md   SSO design, team management, troubleshooting
docs/LINUX.md            deploying to a Linux server
docs/MODELS.md           GPU-matched model builds, quantisation formats
docs/ARGUS.md            code index + documentation server, MCP tools, auth
config/
  traefik/               proxy config, TLS certs, the only ingress
  authelia/              SSO policies, team.yml, users.yml, OIDC clients
  prometheus/            scrape config + alert rules
  alertmanager/          routing and receivers
  grafana/               datasources + 6 dashboards, provisioned
  litellm/               gateway model list and routing
  argus/                 container-side Argus config (paths inside the container)
  loki/ promtail/        log aggregation
  homepage/              landing page
scripts/                 setup, operations, benchmarking, auth tooling
models/                  local weights, bind-mounted to /models
deploy/argus-local.yml   overlay: reuse an existing Argus index instead of a volume
```
