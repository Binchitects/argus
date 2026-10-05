# Deploying Argus Arena

`deploy/` is the whole platform: one `docker-compose.yml`, a short `.env`, and a
few configuration files. **Every module runs by default**; a module you do not
want is left out in one line. Each running image prepares itself, and the app
fetches the models the others read: there are no setup containers and no
setup scripts.

## What runs

| service | what it is |
|---|---|
| `traefik` | the only way in, and TLS |
| `web`, `app` | the React app and the .NET API: sign-in, chat, admin, dashboards |
| `postgres` | the app's and the gateway's databases |
| `litellm` | the gateway: a key and a credit per person, every request's cost |
| `llamacpp` | the chat models (Admin → Models), loaded and unloaded live, on the GPUs |
| `embed` | embeddings for Argus, on the CPU |
| `argus` | the code index (GitLab, a read-only token) and knowledge packs, over MCP |
| `imagegen`, `videogen` | pictures (FLUX.2 klein) and video (Wan2.2 TI2V 5B), on the GPU |
| `audio` | speech to text (Whisper) and text to speech (Kokoro, a Persian voice) |
| `sandbox` | the chat's Python: no network, a read-only root, each run its own user |
| `searxng` | the chat's web search |
| `prometheus`, `alertmanager`, `loki`, `promtail`, `node-exporter`, `gpu-exporter`, `cpu-temp-exporter` | what the app's dashboards, logs and alerts read |
| `power-limits` | GPU and CPU power caps from `.env`, kept applied (privileged) |

## Deploy

On a Linux host with Docker (or Podman, below) and an NVIDIA GPU with the
container toolkit:

```bash
git clone https://github.com/Binchitects/argus && cd argus/deploy
cp .env.example .env
```

Fill in `.env`: the domain, where models live, the first model, and six
secrets, each made with `openssl rand -hex 32` (the gateway's starts with
`sk-`). Then:

```bash
docker compose up -d
```

Open `https://llm.localhost` (or your `DOMAIN`) and sign in as `admin` with
`ADMIN_PASSWORD`. On the first start the app fetches `MODEL` from Hugging Face
into `MODELS_DIR`, adds it under **Admin → Models** with the settings that fit
this machine, and keeps it loaded; it also fetches the picture, video and
embedding models, and tells the speech server to fetch its own. **Admin →
Models** shows the downloads as they go. `MODELS_DIR` must be writable by uid
1000 (the app's user): create it yourself before the first `up`.

The addresses, all on port 443:

| address | what |
|---|---|
| `https://DOMAIN` | the app |
| `https://gateway.DOMAIN/v1` | the API (OpenAI and Anthropic protocols), with a person's key |
| `https://argus.DOMAIN/mcp` | Argus for coding agents, with a person's API key (not a GitLab token) |

`*.localhost` resolves to this machine in browsers; for other tools,
`deploy/scripts/setup-hosts.sh` adds the names to `/etc/hosts`.

People connect their tools from **Connect your tools** in the app. A team's
GitLab pipeline uses the gateway too: a review of each merge request and an
explanation of each failed pipeline, with the `arena` CLI ([ci.md](ci.md)).

## Certificates

Traefik does TLS and nothing else does.

- **Nothing set**: Traefik serves its own certificate. Browsers warn; tools need
  their check turned off (`curl -k`, `NODE_TLS_REJECT_UNAUTHORIZED=0`).
- **`ACME_EMAIL` in `.env`**: certificates from Let's Encrypt for the three
  names. They must resolve to this machine from the internet, with port 443 open.
- **A certificate of your own**: `scripts/make-cert.sh` makes one with openssl,
  signed by a CA of the deployment's own, for `DOMAIN`, `gateway.DOMAIN`,
  `argus.DOMAIN` and `*.DOMAIN`, or installs a company's (`--cert FILE --key FILE
  [--chain FILE]`). The files go to `certs/` (mounted into Traefik at `/certs`)
  and the script writes `config/traefik/certificate.yml` naming them; Traefik
  serves them at once. Then trust `certs/ca.crt` on the machines and in the
  tools that use the stack:

  ```bash
  scripts/make-cert.sh
  sudo cp certs/ca.crt /usr/local/share/ca-certificates/argus-arena.crt && sudo update-ca-certificates
  curl --cacert certs/ca.crt https://DOMAIN/
  ```

  Run it again to renew: the same CA signs the new certificate, so nothing
  needs trusting again. `config/traefik/README.md` has the details.

  People get the CA from **Connect your tools**: when no public CA vouches for
  the site, the page offers the certificate to download (with its SHA-256 to
  compare), how to install it on Linux, macOS and Windows, and in each tool's
  steps what that tool needs besides (`NODE_EXTRA_CA_CERTS` for the Node.js
  tools, `CODEX_CA_CERTIFICATE` for Codex, a CA bundle path in Continue...).
  The app reads the certificate Traefik serves (`Certificates:Probe`,
  `traefik:443` by default) or a file (`Certificates:CaFile`); it never sees a
  private key.

Admin → Overview shows how many days the certificate has left and who issued
it (from Traefik's metrics). The alert **CertificateExpiresSoon** warns 30 days
before it expires (for a day: Traefik renews Let's Encrypt's within that time
by itself), and **CertificateExpiresVerySoon** is critical 7 days before.

## Leaving a module out

Every module runs. To leave one out, name it in `docker-compose.override.yml`
(compose reads it by itself):

```yaml
services:
  videogen: { profiles: [off] }
  argus: { profiles: [off] }
```

The app finds out by itself: a module left out has no name on the stack's
network, its tools are not offered, and **Admin → Services** says it does not
run. Keep `traefik`, `app`, `web`, `postgres` and `litellm`.

## Podman

The same files, with `podman.yml` on top, run on rootless Podman (checked with
Podman 5.7 and its Docker Compose provider):

```bash
systemctl --user enable --now podman.socket
echo 'HTTP_PORT=8080' >> .env; echo 'HTTPS_PORT=8443' >> .env
podman compose -f docker-compose.yml -f podman.yml up -d
```

A user's containers cannot take ports below 1024, hence 8080 and 8443 (the app
then links to `https://DOMAIN:8443`). GPUs come through CDI: `sudo nvidia-ctk
cdi generate --output=/etc/cdi/nvidia.yaml` once. The override runs the app as
the container's root, which under rootless Podman is you, so `MODELS_DIR` stays
yours; it gives Promtail Podman's socket, and leaves the power caps out (they
need the host's root).

## External Postgres

The stack's `postgres` holds the app's database (`llmapp`, made by the app) and
the gateway's (`litellm`). To use your own server (16 or newer) instead, set in
`.env`:

```bash
DB_HOST=db.example.com
DB_PORT=5432
DB_USER=arena
DB_SSL_MODE=require     # disable, prefer or require
```

and leave the stack's out in `docker-compose.override.yml`:

```yaml
services:
  postgres: { profiles: [off] }
```

`DB_PASSWORD` is that role's password. The role creates the app's database at
the first start (or have `llmapp` made for it, owned by it), and the gateway's
`litellm` must exist. The app connects to the server's `postgres` database at
start, to make its own and to start replicas one at a time. With a CA of your
own, the app also takes `Database__SslMode: verify-full` and
`Database__RootCertificate` (a file mounted into it) in the override. Connect
straight to Postgres, or through a pooler in session mode: the replicas' lead
and signals need a session of their own (a transaction pooler breaks them).

## Scale out

Several app replicas share the database; one leads and runs the once-only
background work (the scheduled tasks' clock, the engine's models, downloads,
the directory check), and the others take over within seconds if it stops
([architecture.md](architecture.md#9-several-app-replicas)). With compose, on one
host:

```bash
echo APP_REPLICAS=2 >> .env
docker compose -f docker-compose.yml -f scale.yml up -d
```

`scale.yml` runs the replicas (`arena-app-1`, `arena-app-2`, ...) and tells
Traefik, whose routes then list them with a **sticky cookie** (`arena_replica`):
a browser stays on one replica, where its answers run and stream. A replica that
stops answering `/healthz` is left out. **Admin → Overview** says how many
replicas run and whether the one answering leads.

What to know:

- An answer runs on the replica that was asked. A yes to a tool call, **Stop**
  and **Answer now** reach it from any replica; a page on another replica sees
  the answer when it is saved.
- Each replica keeps its own line with its share of the places (**Settings →
  Chat → Answers at once, everyone**, or the engine's slots, divided among the replicas,
  rounded up).
- A scheduled task runs once, whichever replica its event reaches.

**More GPU servers**: add them under **Admin → Models → Other GPU servers**
with the same model name. The gateway sends each request to the least busy
copy (`routing_strategy: least-busy` in `config/litellm.yaml`), never past a
copy's **At once**.

## Helm

`deploy/helm/argus-arena` runs the same services on Kubernetes: the app with
several replicas, web, the gateway, Postgres (or an external one), llama.cpp
and the media servers on GPUs, embed, Argus, the sandbox (no network: a
NetworkPolicy), SearXNG, Prometheus, Alertmanager and Loki. Each module is
turned on or off in `values.yaml`. One release per namespace: the services keep
the names the app reaches them by.

1. Build the app, web, Argus and sandbox images (`docker compose build`) and push
   them to a registry the cluster pulls from; name them under `images` (the tag
   defaults to the app's version).
2. Make a TLS Secret for `DOMAIN`, `gateway.DOMAIN` and `argus.DOMAIN` (a
   wildcard, or `scripts/make-cert.sh`'s files): `kubectl create secret tls
   arena-tls --cert=... --key=...`.
3. Install:

   ```bash
   helm install arena deploy/helm/argus-arena -n arena --create-namespace \
     --set domain=arena.example.com --set gitlab.url=https://gitlab.example.com
   ```

Secrets left empty are made at the first install and kept across upgrades
(`APP_KEY` must never change); for GitOps tools that cannot look them up, set
them or name an `existingSecret`. The model library and the app's files are
shared by several pods: on more than one node they need ReadWriteMany storage
(`persistence.sharedStorageClass`). The ingress turns on cookie affinity for
NGINX and a sticky cookie for Traefik; another controller needs its own.
`gateway.DOMAIN` goes straight to the gateway: the answer cache for API keys
needs Traefik's health-checked route (compose), so it does not answer here.
External Postgres: `postgresql.enabled=false` and `externalDatabase`. Logs reach
Loki only with a log agent of the cluster's (promtail, or another), and the
host exporters (node, GPU, CPU temperature, power caps) are left to the
cluster's own monitoring.

`python3 deploy/helm/check-chart.py` checks the chart without helm: its copies
of `config/` are the same, every template's blocks close, and every value it
reads is in `values.yaml`. `helm lint` and `helm template` render it where helm
is installed.

## Models

All of them are under **Admin → Models**, with the same controls:

- **Chat models** (llama.cpp): added from the library (`MODELS_DIR`) or found on
  Hugging Face and downloaded, each read for what it is, with settings checked
  against this machine. Kept loaded, or loaded when asked for; several at once
  (**Settings → Model → Models loaded at once**). An omni model (Qwen3-Omni)
  hears sound and sees pictures when its projector has both.
- **Picture, video and speech models**: on or off (off, they leave the gateway
  and the chat's tools), kept loaded or loaded when asked for (and unloaded
  after ten minutes unused), loaded and unloaded by hand. The picture and video
  servers run while the app's control file says so (`services/sd-serve.sh`).
  The video server decodes on the GPU when it has 13 GB free as it loads
  (`VAE_GPU_MB`, with `VAE_GPU_FLAGS`: a 12 GB budget, the weights in RAM until
  needed), and on the CPU, slower, while the chat model holds the GPU (a 9-frame
  clip: 63 s against 309 s on this host's RTX 3090);
  its log says which (`docker compose logs videogen`).
- **Models on other GPU servers**, behind the same gateway.

Who may use each model is set on its card; a person's API key carries the
models they may use.

## GitLab and Argus

Argus reads GitLab with a **read-only** token (`read_api`, `read_repository`, an
account that is at least Reporter where it should index): `GITLAB_URL` and
`GITLAB_TOKEN` in `.env`. It answers each person within their own GitLab
membership; it never needs admin. Indexing starts from **Admin → Indexing**.

Coding agents connect to Argus with the person's own API key (**Connect your
tools** has the setups); nobody hands out a GitLab token. Argus asks the app
whose a key is (`ARGUS_KEY_CHECK_URL` in `docker-compose.yml`, with `ARGUS_KEY`),
and the person's username must be their GitLab username.

To index a push or a merge at once, not at the next scheduled pass: **Admin →
Indexing → Push and merge webhook → Turn on**, then add the webhook in GitLab
(group or project → Settings → Webhooks) with the address and secret the page
shows, for push and merge request events. Adding it is a one-time step for a
GitLab Maintainer or Owner; Argus's token stays read-only. Details:
[Indexing on push and merge](argus/README.md#indexing-on-push-and-merge).

No token can be made for the account (a locked-down GitLab)? Set
`GITLAB_USERNAME` and `GITLAB_PASSWORD` instead: Argus signs in once and makes
its own read-only token ([details](argus/README.md#when-no-token-can-be-issued-for-the-account)).

A GitLab on a private CA: the CA must be trusted inside the Argus container
(mount it and set `ARGUS_GITLAB_CA_CERT` in `docker-compose.override.yml`):

```yaml
services:
  argus:
    volumes: ["./config/argus/tls:/tls:ro"]
    environment: { ARGUS_GITLAB_CA_CERT: /tls/company-ca.pem }
```

## Arena Code

The app's image carries Arena Code, our own coding agent, for people to
download from **Connect your tools**: one file per system, built with the
image. Building it needs .NET's runtime packs, which a host with no internet
cannot fetch: fill `tools/offline-nuget/` once from a machine that can (its
README has the commands, about 220 MB), then `docker compose build app`. On a
host with internet, `docker compose build app --build-arg ARENA_CODE=online`
fetches them instead. Without either, the image is built without it and the
page says so. People on a private CA sign in with
`arena-code login --url https://DOMAIN --ca ca.crt` (give them `certs/ca.crt`).
Details: [arena-code.md](arena-code.md).

## Upgrading from v3

v4 is a new layout: the project is `arena`, the volumes are named for it, and
`.env` holds new names. The data carries over:

1. Write the new `.env` from the old one: `DOMAIN` (was `LLM_DOMAIN`),
   `MODELS_DIR` (the model library), `DB_PASSWORD` (`LLM_PG_PASSWORD`),
   `APP_KEY` (`APP_DATA_KEY`), `ENGINE_KEY` (`LLAMACPP_API_KEY`), `ARGUS_KEY`
   (`ARGUS_ADMIN_TOKEN`), `GATEWAY_KEY` (`LITELLM_MASTER_KEY`), `GITLAB_URL` and
   `GITLAB_TOKEN`. Keep the values: the app's key ring and the gateway's keys
   depend on them.
2. Stop the old stack and copy each volume into its new name (`llmservice_postgres-data`
   to `arena_postgres`, likewise `argus`, `prometheus`, `alertmanager`, `loki`,
   `power-limits`), and the old `config/engine` and `config/directory` files into
   the `arena_engine` and `arena_directory` volumes.
3. In Postgres, add the `arena` role (superuser, with `DB_PASSWORD`).
4. `docker compose up -d`, then add the old `.env` model under **Admin → Models**
   with its settings. The gateway's stored models of the old stack, encrypted
   with the dropped salt key, are removed from the gateway; the app registers
   them again.

### Where the v3 `.env` options went

v3's `.env` had over a hundred options. v4 keeps in `.env` only what the stack
needs to start; the rest moved to the app (applied without a restart, audited),
became fixed, or went away:

| v3 | v4 |
|---|---|
| `LLM_DOMAIN`, `LLM_MODELS_DIR`, `LLM_PG_PASSWORD`, `APP_DATA_KEY`, `LLAMACPP_API_KEY`, `ARGUS_ADMIN_TOKEN` and `ARGUS_CHAT_CLIENT_TOKEN`, `LITELLM_MASTER_KEY`, `TRAEFIK_HTTP_PORT`, `TRAEFIK_HTTPS_PORT` | renamed: `DOMAIN`, `MODELS_DIR`, `DB_PASSWORD`, `APP_KEY`, `ENGINE_KEY`, `ARGUS_KEY` (one), `GATEWAY_KEY`, `HTTP_PORT`, `HTTPS_PORT` |
| `ARGUS_GITLAB_URL`, `ARGUS_GITLAB_TOKEN`, `ARGUS_GITLAB_USERNAME`, `ARGUS_GITLAB_PASSWORD`, `ARGUS_GITLAB_VERIFY` | `GITLAB_URL`, `GITLAB_TOKEN`, `GITLAB_USERNAME`, `GITLAB_PASSWORD`, `GITLAB_VERIFY_TLS` (`ARGUS_GITLAB_AUTH` is inferred: a username means password mode) |
| `ARGUS_GITLAB_CA_CERT` | `docker-compose.override.yml` ([above](#gitlab-and-argus)) |
| `LDAP_*` (server, bind account and password, bases, admin and required groups, StartTLS, sync interval) | Admin → Settings → Company directory (LDAP); the password is stored encrypted |
| `LLAMACPP_HF_REPO`, `LLAMACPP_HF_FILES`, `LLAMACPP_MODEL_FILE` | `MODEL` (the first model), then Admin → Models → Add, or Hugging Face search |
| `MODEL_NAME`, `MODEL_CONTEXT`, `MODEL_MAX_OUTPUT`, `LLAMACPP_PARALLEL`, `LLAMACPP_KV_TYPE`, `LLAMACPP_N_GPU_LAYERS`, `LLAMACPP_N_CPU_MOE`, `LLAMACPP_THREADS`, `LLAMACPP_MTP_*`, `LLAMACPP_EXTRA_ARGS`, `LLAMACPP_MLOCK`, `PRICE_*_PER_MTOK` | each model's own form in Admin → Models (any other llama.cpp option in its extra lines) |
| `LLAMACPP_MODELS_MAX`, `LLAMACPP_PRELOAD` | Settings → Models loaded at once; Keep loaded on each model |
| `MODEL_ENABLE_THINKING`, `MODEL_REASONING_EFFORT`, `THINKING_PRESETS` | Settings → Chat: default thinking, thinking levels offered |
| `LITELLM_DEFAULT_USER_BUDGET`, `LITELLM_BUDGET_DURATION` | `config/litellm.yaml` (`max_internal_user_budget`, `internal_user_budget_duration`); each person's credit in Admin → People |
| `BACKUP_*` | unchanged, still read by `scripts/backup.sh` from `.env` |
| `COMPOSE_PROFILES` | every module runs; leave one out in `docker-compose.override.yml` |
| `IMAGEGEN_*` | the picture model is fixed (FLUX.2 klein 4B), fetched by the app; turned on or off, loaded or kept in Admin → Models |
| `PROMETHEUS_RETENTION_TIME`, `_SIZE`, `*_CPUS`, `*_MEM_LIMIT`, `SANDBOX_*`, `LITELLM_WORKERS`, `EMBED_CPUS` | fixed in `docker-compose.yml` (30 days or 20 GB of metrics; the sandbox's limits); change one in `docker-compose.override.yml`. The longest Python run is Settings → Python and web |
| `LITELLM_SALT_KEY`, `SEARXNG_SECRET`, `ENGINE_API_BASE`, `LLAMACPP_ENGINE_URL` and `_SHA256`, `ARGUS_VERSION`, `LLM_UID`, `LLM_GID`, `LLM_CONFIG_DIR`, `LLM_SERVICES_DIR`, `LLM_ENV_SAMPLES_DIR`, `COMPOSE_PROJECT_NAME`, `BIND_ADDRESS`, `DOCKER_SOCKET`, `HOST_*`, `LLAMACPP_RAM_RESERVE_GB`, `HOST_SWAPPINESS` | gone: the stack makes or knows them itself (the search secret is new at each start; no web app gets the Docker socket) |

## Backups and restore

`scripts/backup.sh` backs up the database (a consistent dump), every named
volume and the configuration into `backups/`; `--install-timer` runs it daily.
Models are not backed up: they are in `MODELS_DIR` and can be fetched again.
`BACKUP_DIR`, `BACKUP_COPY_DIR` (a verified second copy on another disk),
`BACKUP_KEEP`, `BACKUP_INCLUDE_LOGS` and `BACKUP_TIME` in `.env` change where,
how many and when ([configuration.md](configuration.md#env)).

To put a backup back, stop the stack and restore it: the volumes and the
database, and with `--with-config` also `.env` and the files under `config/`.

```bash
docker compose down                                   # volumes are kept
scripts/backup.sh --verify backups/2026-10-04_033000
scripts/backup.sh --restore --from backups/2026-10-04_033000
docker compose up -d
```

`scripts/restore-test.sh` proves a backup restores, without touching the live
stack. It restores the newest backup (or `--from DIR`) into a throwaway project
beside it, with its own name, volumes, network and port (127.0.0.1:18443), and
only `postgres`, `app`, `web`, `traefik` and `litellm`. Its network has no way
out, so the restored copy sends no mail and calls no webhook. It checks through
the API that the backup's people, chats and saved settings are there and that
spend reads from the restored gateway, then removes the project and its
volumes.

- It signs in as `admin` with the backup's `ADMIN_PASSWORD`;
  `RESTORE_TEST_PASSWORD` and `--user` sign in as someone else.
- It pulls and builds nothing: it runs the images the live stack runs.
- `--dry-run` prints the plan; `--keep` leaves it running to look at, and
  `--down` removes it later.
- It refuses the live project's name, and every volume it removes must carry
  its own.

## Rollback

EF migrations only go forward. An older app on a database a newer one migrated
may start, but nothing undoes the newer schema. A rollback is the backup taken
before the upgrade:

1. Before upgrading, take a backup: `scripts/backup.sh`.
2. Upgrade: the new release's images, then `docker compose up -d`. The app
   migrates the database at its first start.
3. To roll back: `docker compose down`, the release you came from checked out
   and its images built, `scripts/backup.sh --restore --from <the backup from
   step 1>`, then `docker compose up -d`.

What was written after the upgrade is lost: the backup is from before it.

`scripts/rollback-test.sh FROM_TAG TO_TAG` (for example `v4.1.0 v4.0.0`) proves
it in a throwaway project, as `restore-test.sh` does, on port 18444:

1. It builds both releases' app and web images from their tags.
2. `TO_TAG` from zero; a person, a group, a chat and a setting go in; the
   backup is taken.
3. `FROM_TAG` over it: it must migrate forward and keep everything.
4. `TO_TAG`'s images straight over the newer database: the older app must come
   up.
5. The procedure above: the backup restored, and `TO_TAG` back on its own last
   migration, with what went in before the upgrade and nothing from after it.

The project, its volumes and the images it built are removed at the end.

## Offline install

`scripts/airgap.sh` carries the whole stack to a host with no network, in one
file. On a host where the stack runs:

```bash
scripts/airgap.sh pack --models /media/usb/arena.tar
```

The bundle holds:

- every image the compose files name (`docker save`);
- this `deploy/` folder without `.env`, backups, certificate keys or models;
- the speech server's models (the `arena_audio` volume);
- the models: the chat models the app registered (Admin → Models) and the
  picture, video and embedding files, copied with `--models` and listed
  without it;
- a `MANIFEST` with the version and commit, and `SHA256SUMS`, a checksum for
  every file. `arena.tar.sha256` beside the bundle checks the copy.

On the other host:

```bash
sha256sum -c arena.tar.sha256
tar -xOf arena.tar arena-airgap/deploy/scripts/airgap.sh > airgap.sh
bash airgap.sh load --into /srv/arena arena.tar
```

`load` checks every file against its checksum before it changes anything. Then
it loads the images, puts `deploy/` in `/srv/arena/deploy` (an `.env`,
`docker-compose.override.yml`, certificates or backups already there are
kept), moves the models into
`MODELS_DIR`, fills the `arena_audio` volume, and prints what to put in `.env`.

`MODEL` is printed as a file in the library, never `repo:quant`: the app adds a
library file as it is and never asks Hugging Face. The picture, video and
embedding files are there, so the app fetches none of them, and
`HF_HUB_OFFLINE=1` for `audio` in `docker-compose.override.yml` keeps the speech
server from looking. Then `docker compose up -d --pull never`.

- `--podman` packs from and loads into Podman's store, and prints Podman's
  ports and command. `--dry-run` prints the plan for either. Nothing is ever
  pulled.
- A certificate: `scripts/make-cert.sh` (openssl, no network), or Traefik's
  own.
- What needs the internet stays without it: web search, and Hugging Face search
  under Admin → Models. Knowledge packs go in `packs/` beside `deploy/`.

## Testing a deployment

| script | what it proves |
|---|---|
| `scripts/upgrade-test.py --zero --stop-live` | this checkout from zero on fresh volumes: it comes up, provisions its first model, signs in, answers with a file |
| `scripts/upgrade-test.py --from TAG --stop-live` | a release from zero with data in, then this checkout over it: everything kept, the old chat goes on |
| `scripts/restore-test.sh [--from DIR]` | a backup restores: in a throwaway project beside the live one, its people, chats, settings and spend are there |
| `scripts/rollback-test.sh FROM_TAG TO_TAG` | a release rolls back by restoring the backup taken before the upgrade, in a throwaway project |
| `scripts/airgap.sh pack --dry-run X.tar` | what an offline bundle would hold, and that every image is on this host |
| `scripts/clients-check.py` | the API (OpenAI and Anthropic, streaming, tools), Argus over MCP, Qwen Code and DeepSeek Harness |
| `scripts/scale-test.py` | many people at once: the chat's queue, every key, a burst of sandbox jobs |
| `scripts/sandbox-check.py` | the Python sandbox's limits and escapes |
| `scripts/functional-test.py` | everything a person does, done for real, as two people |

## When something is wrong

- **A module's card says its server does not run**: it is left out, or its
  container is down (`docker compose ps`).
- **A picture or video model waits for its files**: the app is fetching them;
  **Admin → Models → Downloads** shows how far. A download that failed is tried
  again within ten minutes.
- **The first chat on a new deployment waits**: the gateway sets up its
  database (160 migrations) for a minute or more after the model serves.
- **`MODELS_DIR` is not writable**: the app says so on the download; make the
  folder uid 1000's.
- **The browser warns about the certificate**: Traefik's own; set `ACME_EMAIL`
  or bring your own.
- **Logs**: **Admin → Logs** (every service, by level), or `docker compose logs SERVICE`.
- **Is the answer cache answering?** With it on (Settings → API keys), ask the
  same thing twice with a key: the second response has `x-arena-cache: hit`.

  ```bash
  for i in 1 2; do curl -sk -D - -o /dev/null https://gateway.DOMAIN/v1/chat/completions \
    -H "Authorization: Bearer $KEY" -H 'Content-Type: application/json' \
    -d '{"model":"MODEL","messages":[{"role":"user","content":"What are your opening hours?"}]}' | grep -i x-arena-cache; done
  ```

  No header at all: Traefik still sends the key straight to LiteLLM. It asks
  the app every 10 seconds; `docker compose logs traefik` shows a failing check.
