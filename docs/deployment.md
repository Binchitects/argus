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
| `https://argus.DOMAIN/mcp` | Argus for coding agents, with a GitLab token |

`*.localhost` resolves to this machine in browsers; for other tools,
`deploy/scripts/setup-hosts.sh` adds the names to `/etc/hosts`.

## Certificates

Traefik does TLS and nothing else does.

- **Nothing set**: Traefik serves its own certificate. Browsers warn; tools need
  their check turned off (`curl -k`, `NODE_TLS_REJECT_UNAUTHORIZED=0`).
- **`ACME_EMAIL` in `.env`**: certificates from Let's Encrypt for the three
  names. They must resolve to this machine from the internet, with port 443 open.
- **A certificate of your own** (a company CA's, a wildcard): the files and a
  small `certificate.yml` beside `config/traefik/routes.yml`; Traefik picks it up
  without a restart. `config/traefik/README.md` has the file.

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
- **Models on other GPU servers**, behind the same gateway.

Who may use each model is set on its card; a person's API key carries the
models they may use.

## GitLab and Argus

Argus reads GitLab with a **read-only** token (`read_api`, `read_repository`, an
account that is at least Reporter where it should index): `GITLAB_URL` and
`GITLAB_TOKEN` in `.env`. It answers each person within their own GitLab
membership; it never needs admin. Indexing starts from **Admin → Indexing**.

To index a push or a merge at once, not at the next scheduled pass: **Admin →
Indexing → Push and merge webhook → Turn on**, then add the webhook in GitLab
(group or project → Settings → Webhooks) with the address and secret the page
shows, for push and merge request events. Adding it is a one-time step for a
GitLab Maintainer or Owner; Argus's token stays read-only. Details:
[Indexing on push and merge](argus/README.md#indexing-on-push-and-merge).

A GitLab on a private CA: the CA must be trusted inside the Argus container
(mount it and set `ARGUS_GITLAB_CA_CERT` in `docker-compose.override.yml`).

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

## Backups

`scripts/backup.sh` backs up the database (a consistent dump), every named
volume and the configuration into `backups/`; `--install-timer` runs it daily,
`--restore --from DIR` puts it back. Models are not backed up: they are in
`MODELS_DIR` and can be fetched again.

## Testing a deployment

| script | what it proves |
|---|---|
| `scripts/upgrade-test.py --zero --stop-live` | this checkout from zero on fresh volumes: it comes up, provisions its first model, signs in, answers with a file |
| `scripts/upgrade-test.py --from TAG --stop-live` | a release from zero with data in, then this checkout over it: everything kept, the old chat goes on |
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
