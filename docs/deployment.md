# Deploying Argus Arena

`deploy/` is the whole platform: one `docker-compose.yml`, a short `.env`, and a
few configuration files. The core always runs; **everything else is a
[feature](#features)**, on or off as `COMPOSE_PROFILES` in `.env` says, chosen when
the installer installs, upgrades or repairs. Each running image prepares itself,
and the app fetches the models the others read: there are no setup containers
and no setup scripts.

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
| `laya` | typed decisions on the CPU (the chat's Decide tool, Code Arena's look at commands); **off** until its feature is on ([below](#laya)) |
| `searxng` | the chat's web search |
| `prometheus`, `alertmanager`, `loki`, `promtail`, `node-exporter`, `gpu-exporter`, `cpu-temp-exporter` | what the app's dashboards, logs and alerts read |
| `dcgm-exporter` (profile `dcgm`, off by default) | NVIDIA DCGM's data-centre GPU detail for the GPU Hardware dashboard: PCIe and NVLink throughput, SM and tensor activity, XID errors, ECC and retired pages. Bare-metal Linux only, from NVIDIA's registry (`nvcr.io`); the `dcgm` feature turns it on. Without it the dashboard's GPU detail section (clocks, throttling, PCIe link, fans, ECC) still comes from `gpu-exporter` |
| `power-limits` | GPU and CPU power caps from `.env`, kept applied (privileged) |

## Deploy

The short way, on a Linux host, from a clone of the repository:

```bash
git clone https://github.com/Binchitects/argus && cd argus
./arenactl doctor --for install    # what the host lacks, each with what to do
./arenactl install                 # the images built, then the installer: .env, certificate, start
```

It checks the host and offers to install what is missing (Docker and its
compose plugin, NVIDIA's driver and toolkit), asks the domain and the rest,
and waits until the stack is healthy. For a host with no network,
`./arenactl bundle --with-deps` makes one file with everything, the host's
dependencies included ([scripts.md](scripts.md)).

By hand, on a Linux host with Docker (or Podman, below) and an NVIDIA GPU with the
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
`ADMIN_PASSWORD`. The app downloads no model on its own: a site may be offline
or metered, or want to choose. The **default models** (`MODEL`, the picture,
video and embedding models, and the speech server's) come one of three ways:

- `DEFAULT_MODELS=download` in `.env`: the app downloads them once, at its first
  start (the installer's **download** choice writes it). `MODEL` is a Hugging
  Face `repo:quant` then.
- Their files copied into `MODELS_DIR` as it lays them out (the installer's
  folder choice, or by hand).
- Later, by an admin: **Admin → Models → Default models** lists what is missing
  and downloads it when told.

Once `MODEL`'s files are in `MODELS_DIR` (however they came), the app adds it
under **Admin → Models** with the settings that fit this machine, and keeps it
loaded. **Admin → Models** shows the downloads as they go. `MODELS_DIR` must be
writable by uid 1000 (the app's user): create it yourself before the first `up`.
A folder that holds models already is used as it is: nothing is moved.

The stack's data (the database, Argus's indexes, the speech server's models,
metrics and logs, the engine's presets) lives in folders of **`DATA_DIR`** on the
host, one per volume (`DATA_DIR/postgres`, `DATA_DIR/argus`, ...), not in the
engine's own storage: `DATA_DIR` in `.env` is an absolute folder, and each
folder must be there before `up` (the installer asks where, and makes them with
their services' owners). Compose still names them as volumes, so
`scripts/backup.sh` backs them up and restores them the same way. An upgrade
from a release that kept them in the engine's storage moves them into
`DATA_DIR` (copied, checked, then the engine's copies removed; the upgrade's
backup holds them too), and a rollback restores them from that backup. To move
`DATA_DIR` later: stop the stack (`docker compose down`), move the folder,
remove the volumes (`docker volume rm arena_postgres ...`: the folders stay),
set `DATA_DIR` in `.env`, and `up -d`.

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
- **A certificate of your own** (what the installer makes by default):
  `scripts/make-cert.sh` makes one with openssl,
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

  The same CA signs the Code Arena builds the stack serves: on install, upgrade
  and repair the installer runs `scripts/sign-code-arena.sh`, which makes a
  code-signing certificate from it (`signing/code-signing.crt` and `.key`, 3
  years, kept out of every container and of airgap bundles) and signs the
  builds' list into `DATA_DIR/code-arena-signature/`, which the app serves.
  Run it by hand after changing the builds; `--check` says whether what is
  served is signed ([code-arena.md](code-arena.md#getting-it)).

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

## Features

`traefik`, `web`, `app`, `postgres` and `litellm` always run. Everything else is
a feature, listed in [`deploy/features`](../deploy/features) with the services it
runs, whether a new installation has it on, what the host needs for it and the
features it requires. Each optional service runs under its feature's compose
profile, and **`COMPOSE_PROFILES` in `.env` is the list of the features that
are on**: a feature that is not named runs nothing and needs no image. The app
reads the same list. A feature that is off is gone from the menus, the command
palette and the tour; its pages say it is not installed and how it is turned
on; its API answers 404; and its background work does not run.

| feature | on in a new installation | runs | needs, requires |
|---|---|---|---|
| `chat-models` | yes | `llamacpp` | a GPU |
| `vllm` | no | `vllm` (with `VLLM_IMAGE`) | a GPU |
| `sglang` | no | `sglang` (with `SGLANG_IMAGE`) | a GPU |
| `knowledge` | yes | `embed` | |
| `argus` | yes | `argus` | a GitLab |
| `pictures` | yes | `imagegen` | a GPU |
| `video` | yes | `videogen` | a GPU |
| `speech` | yes | `audio` | |
| `sandbox` | yes | `sandbox` | |
| `web-search` | yes | `searxng` | |
| `browser` | no | `browser` | |
| `3d` | no | `blender` | |
| `lean` | no | `lean` (an image of about 13 GB) | the lab |
| `studio` | no | `studio` | a GPU; the media studio |
| `workspaces` | no | `workspaces` | Code Studio |
| `laya` | no | `laya` | |
| `scanners` | no | `scanners` (an image of about 700 MB) | Argus |
| `monitoring` | yes | `prometheus`, `alertmanager`, `loki`, `promtail`, `node-exporter`, `cpu-temp-exporter` | |
| `gpu-monitoring` | yes | `gpu-exporter` | a GPU; monitoring |
| `dcgm` | no | `dcgm-exporter` | a GPU; monitoring |
| `power-limits` | yes | `power-limits` | a GPU |
| `ha` | no | `valkey` | |
| `meetings` | yes | (the app) | speech |
| `lab` | yes | (the app) | the sandbox |
| `apps`, `data`, `decks`, `agents`, `code-studio` | yes | (the app) | |
| `media` | yes | (the app) | pictures |

The installer chooses them ([below](#the-offline-installer)): `--with A,B` and
`--without A,B` on `install`, `upgrade` and `repair`, and on a terminal the list
to change, each with the disk its images take. A feature turned on brings those
it requires; one that others on need is not turned off without them. `repair
--with` loads a feature's images from the bundle and starts it; `repair
--without` removes its containers and its images (`--keep-images` keeps them),
and its data stays until `remove --purge`. `installer.sh features` lists them,
on or off, with their sizes.

By hand, in `deploy/`: change `COMPOSE_PROFILES` in `.env`, then `docker compose
up -d` (which starts what is on) and `docker compose rm -sf SERVICE` for each
service of a feature turned off (compose leaves those containers as they
were).

## Laya

[Laya](https://github.com/NandhaKishorM/laya) (Convai Innovations, Apache-2.0)
answers typed questions about a text with a probability for every option, in
one forward pass, on the CPU: the chat's **Decide (Laya)** tool
([chat.md](chat.md#decide-laya)), which Arena MCP serves to agents, and Code
Arena's look at commands ([code-arena.md](code-arena.md#laya-looks-at-commands)).
It is off by default. To turn it on, with the installer:

```bash
installer.sh repair --with laya
```

or by hand: add `laya` to `COMPOSE_PROFILES` in `.env`, then `docker compose up -d --build laya`.

- **Its weights** are not in the image. The app fetches its two checkpoints
  from Hugging Face (`convaiinnovations/laya`) into the model library, as it
  does the picture models: the English one (843 MB, calibrated) into
  `MODELS_DIR/laya/english`, the multilingual one (678 MB, 100+ languages
  including Persian) into `MODELS_DIR/laya/multilingual`. **Admin → Models**
  shows the downloads. The server loads each as soon as its files are all
  there (a couple of seconds), and the tool is offered once one is loaded.
- **The image** is `python:3.13-slim` with PyTorch's CPU build (no CUDA: the GPU
  is the chat model's) and `laya` from PyPI, about 1.7 GB. Its server is
  `services/laya/server.py`: Python's own HTTP server, `POST /v1/decide` and
  `GET /health` on port 8000, one forward pass at a time. Calls wait their
  turn, at most 16 of them and 8 seconds each; past that it answers busy
  (503) at once, and a call whose caller has gone is skipped, so a burst
  cannot keep it busy for callers that gave up.
- **Locked down.** A read-only root, no capabilities, the library mounted
  read-only, `HF_HUB_OFFLINE` (it fetches nothing), and a network of its own
  that is `internal` and holds only the app: it can reach nothing. 4 CPUs and
  6 GB; both checkpoints loaded take about 2.1 GB (up to 4.8 GB while they
  load).
- **How fast**, on 4 threads of an i7-13700K: four questions about an English
  incident in about 0.6 s; four about a Persian complaint, on the smaller
  multilingual checkpoint, in about 0.2 s; three yes/no questions about a
  command in about 0.35 s. Each checkpoint loads in 2 to 3 seconds and answers
  once at start, so the first call is no slower. More threads (`LAYA_THREADS`
  and `cpus` in `docker-compose.override.yml`) make one call faster.
- **Offline**: `scripts/airgap.sh pack --models` carries the two checkpoints
  when they are in the library.
- **Helm**: `laya.enabled=true`, with the `arena-laya` image built and pushed
  like the app's; a NetworkPolicy lets only the app's pods in, and nothing out.
  The app is told whether it runs (`Modules__laya`), as for the media
  servers, rather than looking the name up.

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
need the host's root). The offline installer takes `--podman`
([below](#the-offline-installer)), with `podman.yml` on top for you, and so does
`scripts/backup.sh --podman` when `COMPOSE_FILE` names no files: a restore makes
the volumes as Podman runs them, and the backup keeps `podman.yml`.
[Runtimes](#runtimes) says what else differs.

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

`scale.yml` runs the replicas (`arena-app-1`, `arena-app-2`, ..., named after
the project) and tells
Traefik, whose routes then list them with a **sticky cookie** (`arena_replica`):
a browser stays on one replica, where its answers run and stream. A replica that
stops answering `/healthz` is left out. **Admin → Overview** says how many
replicas run and whether the one answering leads.

What to know:

- An answer runs on the replica that was asked. A yes to a tool call, **Stop**
  and **Answer now** reach it from any replica; a page on another replica sees
  the answer when it is saved.
- Each replica keeps its own line for each model, with its share of the
  model's places (its slots, divided among the replicas, rounded up; and of
  **Settings → Chat → Answers at once, everyone** when set).
- Each replica keeps its own table of which conversation holds which engine
  slot. It goes by what the engine said of its slots within the last second
  (or asks it), so it never sends a turn to a slot another replica (or an API
  key) is using; a conversation whose turns reach both replicas may lose its
  slot now and then (its turn is read whole once).
- A scheduled task runs once, whichever replica its event reaches.

**More GPU servers**: add them under **Admin → Models → Other GPU servers**
with the same model name. The gateway sends each request to the least busy
copy (`routing_strategy: least-busy` in `config/litellm.yaml`), never past a
copy's **At once**.

## Helm

`deploy/helm/argus-arena` runs the same services on Kubernetes: the app with
several replicas, web, the gateway, Postgres (or an external one), llama.cpp
and the media servers on GPUs, embed, Argus, the sandbox (no network: a
NetworkPolicy), SearXNG, Prometheus, Alertmanager, Loki, and Laya (off unless
`laya.enabled`). Each module is turned on or off in `values.yaml`. One release
per namespace: the services keep the names the app reaches them by.

### With the installer

`installer.sh COMMAND --kubernetes` (the offline bundle's, or
`deploy/scripts/kube-install.sh` from the repository) installs the chart for
you. `requirements` checks the cluster before anything is changed and says
what each lack needs: kubectl 1.27 and helm 3.14 or newer, the cluster
answering and your rights in the namespace, a default storage class (or
`--storage-class`), one that shares volumes between nodes on more than one
node (`--shared-storage-class`: NFS, CephFS, EFS, Azure Files, Filestore; the
app, the sandbox and the tools share the model library and their jobs'
volumes; on one node they are ReadWriteOnce), an ingress controller, the TLS (`--tls-secret`, or cert-manager's
`--cluster-issuer`), GPUs (NVIDIA's GPU operator or device plugin; without
them the chat engine, pictures and video are left out), room on the nodes (4
CPUs and 12 GiB at least; 8 and 24 with the GPU servers), and the metrics
server for `--autoscale`.

```bash
installer.sh install --kubernetes --domain arena.example.com \
  --cluster-issuer letsencrypt --shared-storage-class nfs --replicas 3 --autoscale
```

`install` then makes the namespace and the stack's secrets in the cluster (a
Secret, `RELEASE-secrets`, written from stdin: never on a command line, in a
file or the log; the admin's password is read from it with the command it
prints), writes the values (`~/.arena-kube/RELEASE-values.yaml`, no secret in
it), runs `helm upgrade --install --atomic --wait` and checks that the app
answers through its Service. `--database external --db-host HOST
--db-password-file FILE` uses the company's Postgres (16 or newer;
`--db-ca-secret` for its CA). Air-gapped, `--registry HOST/PATH` (with
`--pull-secret`) loads the bundle's images, pushes them there and points the
chart at them. `upgrade` keeps the release's values and secrets (helm
`--reset-then-reuse-values`) and rolls back by itself when the new release
does not come up; `status` says the release, its pods and the app's version;
`remove` uninstalls and keeps the data, `remove --purge --confirm PURGE` also
takes its volumes and secrets. `--dry-run` shows the plan and changes nothing.
`--with A,B` and `--without A,B` choose the [features](#features) on `install`
and `upgrade`, as on one host: each becomes its service's `enabled` in the
values (a part of the app, a name in `app.features`); an upgrade sets only them
on top of the release's values. The browser agent, DCGM, the GPU metrics and
the power limits run with compose only.

### By hand

1. Build the app, web, Argus and sandbox images, and Laya's if you turn it on
   (`docker compose build`, with `laya` in `COMPOSE_PROFILES` for Laya), and push
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
`gateway.DOMAIN` goes to the app alone, as in compose: the app answers the
requests that ask a model (each key's line, the fair line, the answer cache,
the prompt log, the person's voice and language) and passes on only the model
API's own paths (models, model info, embeddings, moderations, rerank, token
counts) with the caller's key; LiteLLM's management API and pages are not
reachable from outside. While no app pod is ready, the API is down too (502 or
503): nothing goes around the app.
External Postgres: `postgresql.enabled=false` and `externalDatabase`. Logs reach
Loki only with a log agent of the cluster's (promtail, or another). The host's
figures come from the `host-exporters` DaemonSet (node-exporter and the CPU's
temperature, as compose's), which runs with the host's processes and its `/`,
`/proc` and `/sys` read only: a namespace at the `baseline` or `restricted` Pod
Security level refuses it. Turned off there (`monitoring.hostExporters.enabled=false`),
`TargetDown` fires for the `node` and `cpu-temp` jobs and the host's disks,
memory and temperature go unwatched by the stack. On several
nodes, give it a `nodeSelector` for the GPU node. GPU telemetry and power caps
are the cluster's. Argus checks coding agents' API keys with the app, as in
compose; built knowledge packs (**Admin → Packs**) come from a claim of yours
(`argus.packLibraryClaim`), mounted read only.

Availability: the app, the web and the gateway roll one new pod at a time and
take none away before it is ready (`maxUnavailable: 0`), spread their replicas
over nodes (preferred; `availability.strictSpread=true` requires a node each),
and have a PodDisruptionBudget each when they run more than one replica, so a
node's drain keeps them serving. `availability.autoscale.enabled=true` adds a
HorizontalPodAutoscaler each, from their replicas up to
`availability.autoscale.maxReplicas` at `cpuPercent` of their CPU request (the
cluster's metrics server reads it); the Deployments then leave their replica
counts to it. The gateway's replicas share their rate-limit counts only with
`valkey.enabled`.

`python3 deploy/helm/check-chart.py` checks the chart without helm: its copies
of `config/` are the same, every template's blocks close, and every value it
reads is in `values.yaml`. `helm lint` and `helm template` render it where helm
is installed.

## Runtimes

One stack, three ways to run it: **Docker** (`docker-compose.yml`, and
`scale.yml` for replicas), **rootless Podman** (`podman.yml` on top,
`podman compose`, GPUs through CDI) and **Kubernetes** (the Helm chart). A
feature added to one is added to all three, or is listed here with the reason.
`tests/deploy/test_runtime_parity.py` holds them to it: it reads what `docker
compose config` (or `podman compose config`) resolves with and without
`podman.yml` and `scale.yml`, and the chart's templates (`helm template` where
helm is installed), and fails when a service, a variable given to one, a path
it mounts, a file or script from `deploy/` it reads, a secret or one of the
app's routes is in one runtime and not the others, unless its allow-list names
it with one of the reasons below. `deploy/helm/check-chart.py` keeps the
chart's copies of `config/` and `services/` byte for byte the same.

**yes**: present and the same. **differs**: present, run another way (said
how). **no**: missing, with the reason.

| | Docker | Podman | Helm |
|---|---|---|---|
| **Services** | | | |
| `traefik` | yes | yes (ports 8080 and 8443) | differs: the cluster's ingress controller and the chart's Ingress |
| `web`, `app` | yes | differs: the app runs as the container's root, which is you | yes, two replicas by default |
| `postgres` (or external) | yes | yes | yes (a StatefulSet, or `externalDatabase`) |
| `litellm` | yes | yes | yes |
| `llamacpp` | yes, every GPU | yes, CDI; no locked-memory limit | differs: `llamacpp.gpus` whole GPUs (`gpuResource`: `nvidia.com/gpu` or `amd.com/gpu`); no `ENGINE_RAM_PROTECT` |
| `vllm` | off (the `vllm` feature, `VLLM_IMAGE`), every GPU; `fake-gpu.yml`: the fake vLLM, no GPU | the same, CDI; no locked-memory limit | off (`vllm.enabled`); a Deployment per `vllm.instances` entry with its GPUs and node; `vllm.fake` |
| `sglang` | off (the `sglang` feature, `SGLANG_IMAGE`), every GPU; `fake-gpu.yml`: the fake SGLang, no GPU | the same, CDI; no locked-memory limit | off (`sglang.enabled`); a Deployment per `sglang.instances` entry with its GPUs and node; `sglang.fake` |
| `embed`, `audio`, `searxng` | yes | yes | yes |
| `argus` | yes | yes | yes |
| `imagegen`, `videogen` | yes, every GPU | yes, CDI | differs: whole GPUs each; `videogen` off by default |
| `sandbox` | yes | yes | differs: no network by a NetworkPolicy (the CNI must enforce it); no `pids_limit` (the node's pod PID limit) |
| `laya` | off (the `laya` feature) | the same | off (`laya.enabled`); a NetworkPolicy for compose's internal network |
| `prometheus`, `alertmanager`, `loki` | yes | yes | yes |
| `valkey` (the gateway's shared counts, LMCache's shared store) | off (the `ha` feature; `ha.yml` runs `LITELLM_REPLICAS` gateway replicas on it) | the same | off (`valkey.enabled`, with `litellm.replicas`) |
| `dcgm-exporter` | off (the `dcgm` feature) | the same, CDI | no: the cluster's (GPU Operator) |
| `node-exporter`, `cpu-temp-exporter` | yes | yes | yes: the `host-exporters` DaemonSet (`monitoring.hostExporters`) |
| `promtail` | yes | differs: Podman's socket for Docker's | no: the cluster's log agent ships the pods' logs to Loki |
| `gpu-exporter` | yes | yes, CDI | no: the cluster's (NVIDIA's DCGM exporter); until Prometheus scrapes one, `GpuExporterDown` fires |
| `dcgm-exporter` | profile `dcgm` | profile `dcgm`, CDI | no: the cluster's (NVIDIA's GPU Operator runs it) |
| `power-limits` | yes | no: it writes the host's `/sys` | no: privileged; the cluster's |
| **Volumes** | | | |
| model library | `MODELS_DIR` | the same | the `arena-models` claim (ReadWriteMany across nodes) or `persistence.models.existingClaim` |
| `engine`, `directory`, `sandbox` | volumes | the same | claims, ReadWriteMany across nodes |
| `media-status`, `vllm-status` | in memory (tmpfs) | the same | differs: small claims, kept on disk |
| `argus`, `audio`, `prometheus`, `alertmanager`, `loki`, `postgres` | volumes | the same | claims, kept when the release goes |
| `code-arena-signature` (the Code Arena builds' signature) | a folder of `DATA_DIR`, written by `scripts/sign-code-arena.sh` on the host | the same | no: there is no host with the CA's key; the builds are served unsigned, checked by their SHA-256 |
| `acme`, the certificates | Traefik's | the same | differs: the TLS Secret (`ingress.tlsSecret`) |
| the pack library | `packs/` or `ARGUS_PACK_LIBRARY_DIR` | the same | `argus.packLibraryClaim` (empty: none) |
| `/backups` in the app | `BACKUP_DIR`, read only | the same | no: backups are the cluster's |
| **Configuration and scripts** | | | |
| `litellm.yaml` (its 960 s timeouts), `argus.yaml`, `searxng.yml`, `alertmanager.yml`, `loki.yml`, `prometheus/` and its rules | mounted from `config/` | the same | the same files, from the chart's copies |
| `services/llamacpp/router.sh` (the engine key by `LLAMA_API_KEY`), `services/sd-serve.sh`, `services/cpu-temp-exporter`, `services/vllm/` (the key by `VLLM_API_KEY`), `services/sglang/serve.py` (the key by `SGLANG_API_KEY`, handed to SGLang inside its process), `services/gpu-probe.py`, `tools/fake-vllm` | mounted | the same | the same files, from the chart's copies |
| `promtail.yml`, `traefik/routes.yml` | mounted | the same | no: the cluster's log agent; the Ingress |
| `services/power-limits.sh` | mounted | no | no |
| **Secrets** | `.env` | `.env` | the `arena-secrets` Secret, made at install and kept (or `existingSecret`): the same keys |
| **Health checks** | the images' (app, web, Argus, Laya), Traefik's and Postgres's | the same (compose builds through Podman's Docker API keep them; a `podman build` by hand drops them) | probes: app, web, Postgres, LiteLLM, Laya, Argus |
| **Backup and restore** | `scripts/backup.sh` | `scripts/backup.sh --podman` (with `podman.yml`) | no: the cluster's volume snapshots and a `pg_dumpall` |
| **Install and upgrade** | `installer.sh`, `scripts/upgrade-test.py` | `installer.sh --podman` | `helm install` / `helm upgrade`; the app and Argus migrate as they start; no upgrade test |
| **Features** | | | |
| media borrowing the GPU from idle chat models | yes: every server sees every GPU | yes | differs: each pod has its own GPUs, so a picture never needs to borrow; one GPU shared needs the device plugin's time-slicing (a vLLM model put to sleep frees its memory only for pods that share its GPU) |
| AMD GPUs (ROCm) | `rocm.yml`: `/dev/kfd` and `/dev/dri`, ROCm images | `rocm.yml` after `podman.yml` | `gpuResource: amd.com/gpu`, ROCm images, `engines.backend: rocm` |
| Argus checks agents' API keys with the app | yes | yes | yes |
| answer cache for API keys, speech and pictures by the app | yes, with fallback to LiteLLM while the app is down | yes | differs: no answer cache; speech and pictures fail while no app pod is ready ([Helm](#helm)) |
| TLS | Let's Encrypt (`ACME_EMAIL`) or a certificate of your own | the same | a TLS Secret (cert-manager through `ingress.annotations`); no certificate alerts (they read Traefik's metrics) |
| app replicas | `scale.yml`, `APP_REPLICAS` | the same | `app.replicas` |

## Engines and GPUs

- **llama.cpp** runs on whatever GPUs the machine has (`LLAMACPP_BACKEND`:
  `cuda`, `rocm`, `vulkan`, `sycl`, `metal`, or `cpu`); the presets name the
  GPUs for its backend (`CUDA0`, `ROCm0`...).
- **vLLM** is off until asked for: the `vllm` feature with `VLLM_IMAGE`
  set to a vLLM image you have (none is pulled while the profile is off; Docker
  Hub's is blocked here, see [multi-gpu-server.md](multi-gpu-server.md) §6),
  `podman.yml` on Podman (CDI), `vllm.enabled` on Kubernetes (one Deployment per
  `vllm.instances` entry, each with its GPUs and node). Its supervisor
  (`services/vllm/supervisor.sh`) runs one `vllm serve` per model the app
  wants, on that model's GPUs, from the app's desired state in the `engine`
  volume; it says what each does in `vllm-status`. The key goes by
  `VLLM_API_KEY`, never a command line. Models come from the library's Hugging
  Face snapshots (Admin → Models → Hugging Face, *snapshot*).
- **SGLang** is off until asked for: the `sglang` feature, `--with sglang` on
  install, upgrade or repair. `SGLANG_IMAGE` is SGLang's own image
  (`lmsysorg/sglang:v0.5.21-cu130-runtime` by default, about 14 GB;
  `SGLANG_ROCM_IMAGE` with `rocm.yml`), or arena-sglang, built from PyPI by
  `sglang-build.yml` where Docker Hub cannot be reached. The same supervisor
  runs it (`ENGINE_SERVER=sglang`): one SGLang server per model the app wants,
  on that model's GPUs, from the app's desired state in the `engine` volume, its
  status in `vllm-status`. The key goes by the environment to
  `services/sglang/serve.py`, which hands it to SGLang inside its process, never
  a command line. Kubernetes: `sglang.enabled` (one Deployment per
  `sglang.instances` entry). Its models are Hugging Face snapshots too; choose
  the engine as you add one (Admin → Models).
- **Which engine**: llama.cpp for GGUF files, one machine, layers or experts in
  RAM when the GPU is short, and a few people at once. vLLM or SGLang for
  safetensors snapshots, tensor parallel over GPUs, many people at once, copies
  of a model, and a prefix cache shared by every request. SGLang's radix cache
  suits many conversations that share long prefixes (agents, the same system
  prompt); vLLM adds LMCache's tiers on disk and in a shared store. The RAM
  llama.cpp keeps for conversations that lost their slot (8 GB a loaded model
  by default) is its prompt cache filling up as designed, not a leak: Admin →
  Settings → *RAM for chats that lost their slot* sizes it.
- **Without a big GPU**, `fake-gpu.yml` (compose and Podman) or `vllm.fake` and
  `sglang.fake` (Helm) runs the real supervisor over the fake vLLM and SGLang
  (`tools/fake-vllm`), on the python image the stack already has, with no GPU.
- **AMD GPUs**: `rocm.yml` after the compose files (and after `podman.yml` on
  Podman) gives the engines `/dev/kfd` and `/dev/dri` and ROCm images
  (`LLAMACPP_ROCM_IMAGE`, `VLLM_IMAGE`, `SDCPP_GPU_IMAGE`: check each exists
  for your ROCm first); Helm: `gpuResource: amd.com/gpu`, ROCm images,
  `engines.backend: rocm`. GPUs are read by `services/gpu-probe.py`
  (`rocm-smi`) into the hardware file the app reads.
- **The picture server and the embedder** go on any GPU (`MEDIA_GPU`,
  `EMBED_GPU`; a picture model's own GPU first) or the CPU; the app charges
  their memory to that GPU only, and lends only that GPU to a job short of
  room (a vLLM model there sleeps, then wakes).
- **Several machines**: one `Engine__Vllm__N__Name`/`Kind`/`Host`/`ConfigDir`/`StatusDir`
  per vLLM or SGLang machine (`Kind`: `vllm` or `sglang`; its folders on storage
  both machines see; listing them replaces this stack's `vllm` and `sglang`, so
  list those too), or a Helm `vllm.instances` or `sglang.instances` entry per
  node; servers the app does not run are added under Admin → Models → Other GPU
  servers.

### Multi-GPU server: bring-up

In this order, on the new server (each step's details in
[multi-gpu-server.md](multi-gpu-server.md)):

1. **Hardware and firmware.** All GPUs on one CPU socket's PCIe root complex or
   behind one Gen5 switch; `iommu=pt`, ACS off on the switch; redundant power,
   208–240 V. Pin the NVIDIA driver (R580 or newer) so the distribution does not
   upgrade it; install nvidia-container-toolkit with CDI
   (`nvidia-ctk cdi generate`; AMD: `amd-ctk`, or `rocm.yml`).
2. **`scripts/gpu-check.sh`** (read only): driver, GPUs, ECC and MIG, peer to
   peer between every pair, no pair across sockets, power caps, locked memory and
   shared memory. With nccl-tests built: `--nccl /path/to/all_reduce_perf`
   (about 40–50 GB/s bus bandwidth over PCIe Gen5 with P2P). Fix every FAIL
   before going on.
3. **The engine image** (approve the download): NGC's vLLM image as `VLLM_IMAGE`,
   or build arena-vllm (`vllm-build.yml`); the `vllm` feature on. Without the
   image, `fake-gpu.yml` runs the whole path on the fake vLLM first.
4. **The models** (approve each snapshot's size, shown before it downloads):
   Admin → Models → Hugging Face → *snapshot*. A realistic first set is about
   450–500 GB.
5. **Add each as a vLLM model**, its GPUs left to the advisor's proposal unless
   you choose; FP8 checkpoints first, NVFP4 only once one passes its smoke test
   on the pinned vLLM. Load it, ask it a question in the chat, and read its log
   under Admin → Models if it fails.
6. **Measure**: `scripts/multiuser-bench.py --scenarios ramp,prefixshare,returnafter
   --ramp 1..64 --json run.json`, then again with another setting (TP=2×2 against
   TP=4, a CPU or LMCache tier) and `--compare run.json`. Keep the JSON of each.
7. **Durability**: the `ha` feature with `ha.yml` for gateway replicas,
   `pitr.yml` for Postgres with point-in-time recovery, the `dcgm` feature for
   per-GPU errors and PCIe throughput.

### Supported configurations

| Configuration | Status |
|---|---|
| One NVIDIA GPU (this host's RTX 3090), llama.cpp | Live and unchanged |
| CPU only; several NVIDIA GPUs of mixed sizes; 4 × 96 GB; 8 × 80 GB; AMD 2 × 192 GB | Placement and advice tested with fake machines; needs real hardware to verify |
| vLLM (CUDA, ROCm), tensor parallel, copies, sleep for media | Tested with the fake vLLM and the real supervisor in a container; needs the hardware and a vLLM image |
| Vulkan, SYCL, Metal (llama.cpp) | Presets name them; untested on hardware |
| Docker, Podman, Kubernetes | The same engine services, checked by `tests/deploy/test_runtime_parity.py`, `check-chart.py` and `helm template` |

The full list, with what each needs: [multi-gpu-server.md](multi-gpu-server.md), "Supported configurations".

## Models

All of them are under **Admin → Models**, with the same controls:

- **Chat models** (llama.cpp): added from the library (`MODELS_DIR`) or found on
  Hugging Face and downloaded, each read for what it is, with settings checked
  against this machine. Kept loaded, or loaded when asked for; several at once
  (**Settings → Model → Models loaded at once**). An omni model (Qwen3-Omni)
  hears sound and sees pictures when its projector has both.
- **Picture, video and speech models**: added, edited and removed like chat
  models, each with its files (or the speech server's id), its settings, GPU,
  price and idle time; kept loaded, or loaded when asked for (by the chat, Talk
  or an API key) and unloaded when unused ([admin.md](admin.md#picture-video-and-speech-models)).
  The picture and video servers (`services/sd-serve.sh`) run the model the app
  names in their control file, `<server>.conf` in the `engine` volume: its
  files, its arguments, the GPU, and a generation the app raises to start it
  again. The script reads it line by line, never as commands, every 2 seconds;
  waits for missing files; never starts a server that exited again on its own;
  and writes what it does every 2 seconds to `<server>.json` (with the
  server's output in `<server>.log`) in the `media-status` volume, a small
  in-memory one the app reads. Their image is pinned by digest. The speech
  server's own unloading is off (`STT_MODEL_TTL`, `TTS_MODEL_TTL` -1): the app
  unloads its models.
  A video model set to decode where there is room decodes on the GPU when it
  has that much free as it loads (Wan: 13 GB, with a 12 GB budget and the
  weights in RAM until needed), and on the CPU, slower, while the chat model
  holds the GPU (a 9-frame clip: 63 s against 309 s on this host's RTX 3090);
  its status says which.
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
An account GitLab checks against LDAP works too: Argus uses GitLab's LDAP
sign-in when its own refuses the account (`GITLAB_LDAP` names one server, or
`off`).

A GitLab on a private CA: the CA must be trusted inside the Argus container
(mount it and set `ARGUS_GITLAB_CA_CERT` in `docker-compose.override.yml`):

```yaml
services:
  argus:
    volumes: ["./config/argus/tls:/tls:ro"]
    environment: { ARGUS_GITLAB_CA_CERT: /tls/company-ca.pem }
```

## Code Arena

The app's image carries Code Arena, our own coding agent, for people to
download from **Connect your tools** (a one-line installer for each
system, the file checked against its SHA-256) and to update itself from
(`code-arena update`): one file per system, built with the image. Building
it needs .NET's runtime packs: run `tools/fetch-code-arena-packs.sh` before
`docker compose build app` (it fetches them from nuget.org at the version the
SDK publishes with, about 220 MB each .NET patch), or carry
`tools/offline-nuget/` to a host with no internet. When the packs there are
of another version, the image publishes with nuget.org's; only with neither
is it built without Code Arena, the page saying so and the build log listing
the packs to fetch. People on a private CA sign in with
`code-arena login --url https://DOMAIN --ca ca.crt` (give them `certs/ca.crt`).
To hand it out another way, `./arenactl code-arena` packs each system with
its README and licence into `dist/` (with `SHA256SUMS`), on Linux, macOS or
Windows. Details: [code-arena.md](code-arena.md).

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
| `ARGUS_GITLAB_URL`, `ARGUS_GITLAB_TOKEN`, `ARGUS_GITLAB_USERNAME`, `ARGUS_GITLAB_PASSWORD`, `ARGUS_GITLAB_LDAP`, `ARGUS_GITLAB_VERIFY` | `GITLAB_URL`, `GITLAB_TOKEN`, `GITLAB_USERNAME`, `GITLAB_PASSWORD`, `GITLAB_LDAP`, `GITLAB_VERIFY_TLS` (`ARGUS_GITLAB_AUTH` is inferred: a username means password mode) |
| `ARGUS_GITLAB_CA_CERT` | `docker-compose.override.yml` ([above](#gitlab-and-argus)) |
| `LDAP_*` (server, bind account and password, bases, admin and required groups, StartTLS, sync interval) | Admin → Settings → Company directory (LDAP); the password is stored encrypted |
| `LLAMACPP_HF_REPO`, `LLAMACPP_HF_FILES`, `LLAMACPP_MODEL_FILE` | `MODEL` (the first model), then Admin → Models → Add, or Hugging Face search |
| `MODEL_NAME`, `MODEL_CONTEXT`, `MODEL_MAX_OUTPUT`, `LLAMACPP_PARALLEL`, `LLAMACPP_KV_TYPE`, `LLAMACPP_N_GPU_LAYERS`, `LLAMACPP_N_CPU_MOE`, `LLAMACPP_THREADS`, `LLAMACPP_MTP_*`, `LLAMACPP_EXTRA_ARGS`, `LLAMACPP_MLOCK`, `PRICE_*_PER_MTOK` | each model's own form in Admin → Models (any other llama.cpp option in its extra lines) |
| `LLAMACPP_MODELS_MAX`, `LLAMACPP_PRELOAD` | Settings → Models loaded at once; Keep loaded on each model |
| `MODEL_ENABLE_THINKING`, `MODEL_REASONING_EFFORT`, `THINKING_PRESETS` | Settings → Chat: default thinking, thinking levels offered |
| `LITELLM_DEFAULT_USER_BUDGET`, `LITELLM_BUDGET_DURATION` | gone: each person's and group's credits, one per kind, are the app's (Admin → People, Groups) |
| `BACKUP_*` | unchanged, still read by `scripts/backup.sh` from `.env` |
| `COMPOSE_PROFILES` | the list of the features that are on ([Features](#features)) |
| `IMAGEGEN_*` | the picture model is fixed (FLUX.2 klein 4B), fetched by the app; turned on or off, loaded or kept in Admin → Models |
| `PROMETHEUS_RETENTION_TIME`, `_SIZE`, `*_CPUS`, `*_MEM_LIMIT`, `SANDBOX_*`, `LITELLM_WORKERS`, `EMBED_CPUS` | fixed in `docker-compose.yml` (30 days or 20 GB of metrics; the sandbox's limits); change one in `docker-compose.override.yml`. The longest Python run is Settings → Python and web |
| `LITELLM_SALT_KEY`, `SEARXNG_SECRET`, `ENGINE_API_BASE`, `LLAMACPP_ENGINE_URL` and `_SHA256`, `ARGUS_VERSION`, `LLM_UID`, `LLM_GID`, `LLM_CONFIG_DIR`, `LLM_SERVICES_DIR`, `LLM_ENV_SAMPLES_DIR`, `COMPOSE_PROJECT_NAME`, `BIND_ADDRESS`, `DOCKER_SOCKET`, `HOST_*`, `LLAMACPP_RAM_RESERVE_GB`, `HOST_SWAPPINESS` | gone: the stack makes or knows them itself (the search secret is new at each start; no web app gets the Docker socket) |

## Backups and restore

`scripts/backup.sh` backs up the database (a consistent dump), every named
volume and the configuration into `backups/`; `--install-timer` runs it daily.
Models are not backed up: they are in `MODELS_DIR` and can be fetched again.
`BACKUP_DIR`, `BACKUP_COPY_DIR` (a verified second copy on another disk),
`BACKUP_KEEP`, `BACKUP_INCLUDE_LOGS` and `BACKUP_TIME` in `.env` change where,
how many and when ([configuration.md](configuration.md#env)). After each good
backup it keeps the newest `BACKUP_KEEP`; `scripts/backup.sh --prune [--keep N]`
removes older ones now, never the latest nor the newest that ended well.

The app mounts `BACKUP_DIR` at `/backups` **read only**, to show the backups
under Admin → Storage: their sizes, how each ended, and which old ones `--prune`
would remove ([admin.md](admin.md#storage)). It never changes or deletes a
backup: a backup holds `.env` and the compose files `--restore --with-config`
puts back, so an app that could write there could change what a restore runs.
It can read them, though, `.env` included, when it runs as the user who took
them (uid 1000, as most operators are); it already has most of the stack's keys
in its own environment. To keep the backups out of its sight as well, take them
as another user (the folder is `0700`), or list the app's volumes without
`/backups` in `docker-compose.override.yml` (`volumes: !override`): the page
then says the backups are not seen. Backups taken by a user the app is not, it
cannot read, and says so.

**Encrypted backups.** With `BACKUP_KEY_FILE` in `.env`, each file of a backup
(the database dump, every volume's archive, and `config/`, `.env` included, as
`config.tar.gz.enc`) is encrypted with AES-256, its key stretched from the key
file by PBKDF2; `MANIFEST` says so and names the key by its SHA-256, never the
key. Make the key once, and keep a copy where the backups are not (the
company's vault): without it no backup opens.

```bash
sudo sh -c 'openssl rand -base64 48 > /root/arena-backup.key && chmod 600 /root/arena-backup.key'
```

A key inside `deploy/` or the backups' folders is refused (it would travel with
the backups it opens). `--verify` opens each file with the key, or checks the
checksums alone where the key is not; `--restore` opens the backup into a
private folder beside it and removes it after; `scripts/backup.sh --open DIR
--to OUT` makes a plain copy to look into (remove it when done: it holds every
secret of the stack); `scripts/restore-test.sh` opens an encrypted backup by
itself. Admin → Compliance shows whether the newest backup is encrypted.
pgBackRest (below) encrypts its repository with its own `repo1-cipher-type` and
`repo1-cipher-pass` (`PGBACKREST_REPO1_CIPHER_TYPE` and
`PGBACKREST_REPO1_CIPHER_PASS` in the postgres service's environment), set
before its first backup.

Docker makes a folder it mounts root's when the folder is not there yet, as
when the stack starts (an upgrade, say) before the first backup into a new
`BACKUP_DIR`. `scripts/backup.sh`, run as you, takes such a folder back while
it is empty, through Docker as its other containers run; a folder of root's
with something in it, it names with the fix. `sudo scripts/backup.sh
--install-timer` makes the folder (or takes it back from root) for the user the
timer runs as. The default `./backups` is in the repository, so it is always
yours.

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

### Point-in-time recovery (PITR)

`pitr.yml` runs the stack's Postgres with pgBackRest (`arena-postgres`, built
once from the pgvector image already on the host with Debian's pgbackrest
package: approve the build first): every WAL segment is archived to
`BACKUP_DIR/pgbackrest` as it is written, so the database can be put back to
any second. `scripts/backup.sh --pitr full` (weekly) and `--pitr diff`
(daily) take the backups (four full ones are kept, with their WAL);
`scripts/restore-drill.sh [--to "YYYY-MM-DD HH:MM:SS+00"]` restores the
repository to a moment into a scratch container and volume of its own,
starts it and checks the databases and their people, and never touches the
stack's Postgres (`--dry-run` says what it would do). The logical dump above
stays the second line. On Kubernetes, use CloudNativePG with barman-cloud
backups (an `externalDatabase`), not this file.

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

What was written after the upgrade is lost: the backup is from before it. The
offline installer's upgrade takes the backup and rolls back by itself
([Upgrade from 5.2.0](#upgrade-from-520)).

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

Two ways to a host with no network:

- **The offline installer**: one file per release that installs, upgrades from
  5.2.0, repairs and removes the stack ([below](#the-offline-installer)).
- **`scripts/airgap.sh`**: a running stack's images, files and models in one
  tar, loaded by hand on the other host
  ([below](#carrying-a-running-stack-airgapsh)).

Neither pulls or builds anything there.

### The offline installer

`argus-arena-VERSION-offline.run` is a shell script with the release after it:
every image (gzipped), `deploy/` without secrets, the docs, the licences, Code
Arena's packages, the embedding model, and when asked the other models and the
knowledge packs. The host needs bash, tar, gzip and sha256sum, and Docker or
rootless Podman with Docker's compose plugin; nothing is ever pulled, the helper
containers included. Without curl, the installer asks the app and Argus their
version from a helper container on the stack's network instead of through
Traefik.

#### Making it

On the release's host, with its images built (`docker compose build`, and
`docker compose --profile laya build laya` in `deploy/`):

```bash
deploy/scripts/make-installer.sh                    # dist/argus-arena-VERSION-offline.run and .sha256
deploy/scripts/make-installer.sh --models --packs   # with the models and the knowledge packs
deploy/scripts/make-installer.sh --dry-run          # what it would hold
```

| option | what |
|---|---|
| `--models`, `--models-dir DIR` | the models too, as `airgap.sh --models`; without it they are listed, and only the embedding model goes in |
| `--packs` | the knowledge packs in `packs/` |
| `--code-arena DIR` | Code Arena's packages (default `dist/`, which `./arenactl code-arena` fills, and this when it is empty) |
| `--without A,B` | features whose images stay out: a smaller file for hosts without them (`chat-models,pictures,video,gpu-monitoring,power-limits` for hosts with no GPU); the installer then cannot turn them on |
| `--podman`, `--project NAME` | the images in Podman's store; the compose project they were built under (default `arena`) |
| `--version V`, `--out DIR` | the release (default `VERSION`); where the file goes (default `dist/`) |

The release's own images go in as `arena-SERVICE:VERSION`, the others as the
compose file names them. `MANIFEST` says the version, the commit and the oldest
release it upgrades (5.2.0). `SHA256SUMS` holds a checksum for every file, and
`known/` the checksums of each earlier release's `deploy/` files, so that an
upgrade tells a file the host changed from one it was shipped with. They come
from the releases' git tags: without 5.2.0's (a shallow clone), no file is made
(`git fetch --tags` first). The speech server's models come from the
`arena_audio` volume, as with `airgap.sh`. The embedding model
(`embed/nomic-embed-text-v1.5.f16.gguf`, which the embedding server waits for)
goes in from `MODELS_DIR` even without `--models`; when it is not there, the
bundle maker says so loudly.

#### Install

Copy the `.run` and its `.sha256` to the host, then:

```bash
sha256sum -c argus-arena-5.6.1-offline.run.sha256
sudo sh argus-arena-5.6.1-offline.run install --dir /srv/arena
```

It checks the bundle and the host first: the system (Ubuntu, Debian, Fedora,
RHEL and its kin, openSUSE, Arch), 4 CPU cores and 16 GB of memory, the tools
it uses, disk for the images and the files, Docker or Podman with compose, the
engine's daemon (and Podman's socket) running, an NVIDIA GPU with its driver and
NVIDIA's container toolkit wired into the engine (or `--cpu-only`), the two ports
free and open in the firewall (ufw, firewalld), the clock synchronised and the
kernel's file-watching limits. What is not met is named with what to do; on a
terminal it offers to put it right (unattended: `--install-requirements`):
the system's packages, Docker's or NVIDIA's own repositories where they are in
reach (else the system's packages), the daemon started at every boot, the
toolkit wired into Docker (its runtime) or Podman (a CDI spec, kept current
as the driver changes), the ports opened and the limits raised, each step
checked again after. A missing NVIDIA driver is installed too, and the
installer stops for the reboot it needs (exit 7: run it again after).
`installer.sh requirements` checks a host before anything else (`--fix` puts it
right; `./arenactl requirements` is the same from a checkout). After the images load, a
container runs `nvidia-smi` to prove the GPU reaches it. Then it asks the domain, the admin's e-mail, where models live, the
**default models** (download them from Hugging Face once, at the first start;
copy them from a folder, the model library's layout, with `audio.tar.gz` for the
speech server's; or skip, and an admin downloads them later under Admin →
Models), the first model, the ports and the GitLab Argus indexes; loads the images; writes
`/srv/arena/deploy` and its `.env`, with every secret generated; starts the
stack, waits until each service is healthy, checks the version the app and
Argus report, and prints the address. The admin's first password is shown once,
on the terminal and never in the log: it is `ADMIN_PASSWORD` in `.env` (0600).

- Unattended, every answer is an option: `--yes --domain llm.example.com
  --admin-email a@example.com --gitlab-url URL --gitlab-token-file FILE`
  (`--models-dir`, `--default-models download|skip|DIR`, `--model`, `--http-port`,
  `--https-port`, `--acme-email`).
  The token is read from the file, never from the command line.
- Without a GitLab, the `argus` feature is off until `GITLAB_URL` and
  `GITLAB_TOKEN` are in `.env` and `repair --with argus` turns it on.
- The [features](#features): the release's defaults, and on a terminal the list to
  change; `--with A,B` and `--without A,B` unattended. `--cpu-only` turns off those
  that need a GPU (unless `--with` names one). They go in `.env`
  (`COMPOSE_PROFILES`), and only their images are loaded.
- `docker-compose.override.yml` is yours: upgrades and repairs keep it. When the default models are copied from a folder
  (`--default-models DIR`), it also keeps the speech server from looking for
  models online (`HF_HUB_OFFLINE=1`); otherwise an admin can download speech
  models later.
- `MODEL` is a `.gguf` file in `MODELS_DIR` (the bundle's with `--models`): a
  Hugging Face name (`repo:quant`) needs the network and is refused.
- Install, upgrade and repair make a certificate from a CA of the deployment's
  own (`scripts/make-cert.sh`), and sign Code Arena's builds with it, unless
  `--acme-email` (Let's Encrypt), a certificate or key is there already (never
  made over), or `--no-cert`; `--hosts` adds the names to `/etc/hosts`
  (`scripts/setup-hosts.sh`, as root).
- The folder defaults to `/srv/arena` as root, else `~/arena`. Beside
  `deploy/` the installer writes the docs, the licences, `VERSION` and Code
  Arena's packages, but never over a file or folder it did not write there (a
  git checkout keeps its own).
- Rootless Podman makes some new volumes root's (Alertmanager's then cannot
  write): once the stack runs, install and upgrade give each volume to its
  service's user and start that service again.
- A bundle without the embedding model: the embedding server waits for its file
  (on Docker its image's health check fails meanwhile). The installer counts it
  as waiting for models, not as a failure: it ends with "healthy, waiting for
  models", names the file and where it goes, and the server starts by itself
  once it is there. An upgrade does the same.

#### Upgrade from 5.2.0

```bash
sh argus-arena-5.6.1-offline.run upgrade --dir /srv/arena --dry-run   # the plan: each file, each new .env key
sh argus-arena-5.6.1-offline.run upgrade --dir /srv/arena
```

It upgrades an installation of 5.2.0 or newer, whether this installer made it
or not: a checkout's `deploy/`, or one `airgap.sh` loaded (`--dir` is the folder
that holds `deploy/`, or `deploy/` itself). The version comes from the
installer's record, else `VERSION` beside `deploy/`, else the running app's
`/api/info`. An older version, or one it cannot tell, is refused with why.

The [features](#features) stay as they were. From a release before the list of
features, those whose services ran are on (and the app's own, as a new
installation has them). A feature new in the release is asked about on a
terminal, and left off unattended, named with the `repair --with` that turns it
on. `--with` and `--without` change them in the same run, and a rollback puts
back the list there was.

1. **A rollback point.** The images the stack runs are tagged
   `arena-rollback:5.2.0-...` and `deploy/` is copied.
2. **The new images load** while the old release still serves: the longest
   step, and it changes nothing the old release runs on.
3. **A backup** with `scripts/backup.sh`: the database, the volumes, `.env` and
   the config. Every service but postgres stops first, so the backup is the
   data the new release starts from; people cannot use the stack from here
   until the new release runs. Nothing goes on unless the backup verifies.
4. **The new files.** `.env`, `docker-compose.override.yml`, the certificates,
   the backups and the models are never touched. Each shipped file is compared
   three ways: as the old release shipped it, as the new one does, and as it is
   on the host. A file the host changed (an Alertmanager receiver, the budgets
   in `config/litellm.yaml`) stays as the host has it when the release ships it
   unchanged. When the release changed it too, the host's still stays in
   effect, and the release's is written beside it as `FILE.new-VERSION`; the
   summary at the end lists each one under TO MERGE. Only the compose files
   (`docker-compose.yml`, `podman.yml`, `scale.yml`) and the installer are the
   release's whatever the host did: the host's copy is kept beside as
   `FILE.before-VERSION`, listed under TO APPLY AGAIN (a host's change to a
   compose file belongs in `docker-compose.override.yml`). A file the release
   dropped is removed only when it is the old release's own, unchanged; every
   change is listed. `.env` gets each new key with its default (a new secret is
   generated), listed; nothing is removed, and the keys the release no longer
   reads are named.
5. **The new release starts.** The app and Argus migrate their data as they
   start. Every service must be up and healthy (one that was down before the
   upgrade is named and not waited for, and the embedding server without its
   model is waiting for models), and the app, Argus and every container must
   run the new release.
6. **On a failure it rolls back by itself**: the old images, the files the
   upgrade changed, and, once the new release had started, the data from the
   backup. The data as it is, with the files and `.env` that open it, goes into
   a backup of its own first; if that backup fails (a full disk), nothing is put
   back or restored over the data and it stops with exit 5 and the steps to
   take. It says what failed and exits 4 when the old release runs again.

An upgrade cut off half way (a crash, a power cut) carries on where it was when
it is run again, and a rollback cut off half way is finished, never taken for
the upgrade. `upgrade --rollback` goes back to the release before the last
upgrade later on: what was written since is lost (a backup of it is kept). It
puts back only what the upgrade changed: the old release's copy of each file
it replaced or removed, what it added taken out, and `.env` without the keys it
added. What was changed here since the upgrade is asked about first, while the
stack still runs (`--yes` takes the defaults): a file the upgrade had changed
goes back to the old release's, one it had left as it was stays as it is now;
in `.env` a key changed since stays, but for `DB_PASSWORD`, `APP_KEY` and
`GATEWAY_KEY`, which the restored data opens with, and which go back. The
certificates and the override stay as they are. The `.env` there was, and each
file changed since the upgrade that is replaced or taken out, are kept in
`.arena-install/rollback/VERSION/files-before-rollback/`; `--dry-run` lists all
of it. The old release's images stay for that until you remove them; the
upgrade prints the command.

#### Repair, status, verify

`repair` checks the installation against the bundle and puts right what it
finds, each finding named with what was done: a missing file restored, a
changed one put back after asking (the changed copy kept beside it; never
`.env` or an override, nor a file the install or upgrade kept as you changed
it), a missing image loaded, a volume owned by the wrong
user given back to its service's (rootless Podman leaves Alertmanager's to
root), a stopped or unhealthy container recreated, and each service that
reads a file it put back started again on it. Then it waits for health and
checks the versions. Without the bundle, the installation's own copy
(`/srv/arena/deploy/scripts/installer.sh repair`) checks, and recreates
containers. An embedding server waiting for its model is named, not recreated.

`repair --with A,B` and `--without A,B` turn [features](#features) on and off:
the images of one turned on are loaded from the bundle (without the bundle,
only when they are on the host already), and a line an earlier installer wrote
in `docker-compose.override.yml` to leave its service out is taken out; the
containers of one turned off are removed, then its images (unless
`--keep-images`), and its data stays until `remove --purge`.

`status` says what runs: the version each service reports, its health, the
features on and off, the disk, the GPU and the last backup. `features` lists
every feature, on or off, with the disk its images take and what it needs. `verify` checks the `.run` against its
`.sha256`, the bundle against its `SHA256SUMS`, and the installed files against
what was written (or kept as you changed them).

#### Remove

`remove` stops and removes the stack's containers, its network and its images;
an image that was on the host before the install, or that another project's
container uses, stays. The volumes, `.env`, the backups and the models stay
too: `install` brings the stack back on the same data. So does
`python:3.13-slim`, which `scripts/backup.sh` reads the volumes with.

`remove --purge` also deletes the data: the volumes, `deploy/` with `.env` and
the certificates, what the installer wrote beside it (the docs, the licences,
`VERSION`, Code Arena's packages), the backups, and the models when they are
inside the folder. The knowledge packs in `packs/` stay.
It asks for the word PURGE (unattended: `--confirm PURGE`) and offers a last
backup first (`--final-backup DIR`, outside the folder), taken with the
bundle's `scripts/backup.sh` (5.2.0's backs up only with Docker). A project
started from another folder is refused, and nothing outside the project is
touched.

#### Every command

`--dry-run` prints the plan and changes nothing. `--yes` takes the defaults
without asking; with no terminal and no `--yes`, a question is refused rather
than guessed. `--docker` or `--podman` picks the engine (default: the one the
installation runs on); `--project` the compose project (default:
`COMPOSE_PROJECT_NAME` in `.env`, else `arena`). Each run logs to
`DIR/.arena-install/logs/` (`--log FILE` elsewhere), and secrets never reach a
log. Each command can run twice: the second finds nothing left to do. On
Podman ([above](#podman)) the ports default to 8080 and 8443.

| exit code | means |
|---|---|
| 0 | done, or nothing to do |
| 1 | failed: the log says what |
| 2 | a wrong command or option |
| 3 | refused: a requirement not met, a version it does not upgrade from, no confirmation |
| 4 | the upgrade failed and was rolled back: the release before it runs again |
| 5 | the upgrade failed and so did the rollback: once the cause is put right, `upgrade` again finishes the rollback; the steps by hand are printed |
| 6 | checksums do not match: the bundle or the installed files are damaged |

### Carrying a running stack (airgap.sh)

`scripts/airgap.sh` carries a running stack, as it is, to a host with no network, in
one file. On a host where the stack runs:

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
| `scripts/make-installer.sh --dry-run` | what the release's offline installer would hold, and that its images are built |
| `sh argus-arena-VERSION-offline.run upgrade --dir DIR --dry-run` | what an upgrade would change on that host: the files, the `.env` keys, the images |
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
- **Decide (Laya) says it is still fetching or loading**: the same, for
  `MODELS_DIR/laya`; `docker compose logs laya` says when each checkpoint
  loaded, or why it could not.
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

  No header at all: the answer cache is off (Settings → API keys), or the
  request is not one it keeps (over 1 MB, the Anthropic protocol, no-store).
