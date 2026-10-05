# Configuration

Three places, each with one job:

- **`.env`**: what the stack needs to start. Short on purpose.
- **Settings** (in the app, **Admin → Settings**): everything the app does,
  saved in its database, in effect at once or after the app restarts itself.
- **Admin → Models**: the models, each with its own settings.

## `.env`

`deploy/.env.example` is the template.

| name | what |
|---|---|
| `DOMAIN` | the address: `https://DOMAIN`, `gateway.DOMAIN`, `argus.DOMAIN` |
| `ADMIN_EMAIL` | the first admin's email |
| `MODELS_DIR` | where models live (a fast disk, writable by uid 1000) |
| `MODEL` | the first chat model, fetched on the first start: `owner/repo:QUANT` on Hugging Face, or a file in `MODELS_DIR`. Empty: none |
| `ADMIN_PASSWORD` | the first admin's password; only used while nobody exists |
| `DB_PASSWORD` | Postgres (the app and the gateway) |
| `APP_KEY` | encrypts the app's key ring and saved secrets. **Never change it**: sessions, two-factor and saved secrets are lost |
| `ENGINE_KEY` | the engine's key (the gateway and Prometheus use it) |
| `ARGUS_KEY` | Argus's admin and chat credential, the app's only |
| `GATEWAY_KEY` | the gateway's master key (`sk-...`). Changing it invalidates nothing stored, but the app and the gateway must agree |
| `HF_TOKEN` | optional: gated Hugging Face models |
| `ACME_EMAIL` | optional: certificates from Let's Encrypt |
| `GITLAB_URL`, `GITLAB_TOKEN` | optional: the GitLab Argus indexes, and a read-only token |
| `GITLAB_USERNAME`, `GITLAB_PASSWORD` | optional, only where no token can be made for the account: Argus signs in once and makes its own read-only token ([details](argus/README.md#when-no-token-can-be-issued-for-the-account)). A username wins over a token |
| `GITLAB_VERIFY_TLS` | optional, testing only: `false` accepts any certificate from GitLab. For a private CA, see [deployment.md](deployment.md#gitlab-and-argus) |
| `BACKUP_DIR`, `BACKUP_COPY_DIR`, `BACKUP_KEEP`, `BACKUP_INCLUDE_LOGS`, `BACKUP_TIME` | optional, for `scripts/backup.sh`: where backups go (`./backups`), a verified second copy on another disk, how many to keep (14), whether Loki and Prometheus data go too (1), when the daily timer runs (03:30) |
| `HTTP_PORT`, `HTTPS_PORT` | optional: other ports than 80 and 443 (rootless Podman) |
| `DB_HOST`, `DB_PORT`, `DB_USER`, `DB_SSL_MODE` | optional: an external Postgres instead of the stack's (`postgres`, 5432, `arena`, `prefer`); `DB_SSL_MODE` is `disable`, `prefer` or `require` ([deployment.md](deployment.md#external-postgres)) |
| `APP_REPLICAS` | optional, with `scale.yml`: how many app replicas run behind Traefik (2) ([deployment.md](deployment.md#scale-out)) |
| `COMPOSE_PROFILES` | optional: `laya` runs Laya, the one module off by default ([deployment.md](deployment.md#laya)) |
| `GPU_POWER_LIMIT_W`, `CPU_POWER_LIMIT_W` | optional: power caps, kept applied |
| `XDG_RUNTIME_DIR` | Podman only, from your shell (not `.env`): where Promtail finds the Podman socket |

Everything else is set in the app, not here: Admin → Settings (the company
directory, mail, chat limits, the plugin catalog, the GitLab bot for tasks run
by GitLab's events, …), Admin → Models, Admin → Tools and Admin → Plugins. How
each v3 option maps: [deployment.md](deployment.md#where-the-v3-env-options-went).

## Files under `deploy/config/`

| file | what |
|---|---|
| `traefik/routes.yml` | Traefik's routes (with `APP_REPLICAS` above 1, the app's replicas and the sticky cookie); `traefik/README.md` for a certificate of your own |
| `traefik/certificate.yml` | written by `scripts/make-cert.sh`: the certificate Traefik serves, from `deploy/certs/` (not committed) |
| `litellm.yaml` | the gateway: the default credit per person and period, retries, and how a model on several servers is shared (the least busy copy). The models are registered by the app |
| `argus.yaml` | Argus's paths; the GitLab comes from `.env` |
| `prometheus/` | what is scraped, and the alert rules |
| `alertmanager.yml` | where alerts go (nowhere by default: the app shows them) |
| `loki.yml`, `promtail.yml` | the logs |
| `searxng.yml` | the web search engine |

## Volumes

| volume | holds |
|---|---|
| `postgres` | the app's database (people, chats, settings, files) and the gateway's (keys, spend) |
| `argus` | the code index, installed packs, the audit log |
| `engine` | what the app writes for the engine and the media servers: the model presets, the models kept loaded, how many at once, the picture and video servers' on/off |
| `directory` | who is who for Argus (emails and usernames, no passwords) |
| `audio` | the speech server's models |
| `sandbox` | Python jobs in flight |
| `prometheus`, `loki`, `alertmanager` | metrics, logs, alert state |
| `acme` | Let's Encrypt's certificates |
| `power-limits` | the CPU limits as the firmware set them, to put back |

Models are not in a volume: they are in `MODELS_DIR`, where Admin → Models
downloads them and every server reads them.

Folders for company knowledge are mounted read-only under `/knowledge` in the
app, in `docker-compose.override.yml` ([knowledge.md](knowledge.md#a-folder)).

## Leaving a module out, and Podman

See [deployment.md](deployment.md): `docker-compose.override.yml` with
`profiles: [off]` for a module, and `podman.yml` for rootless Podman.
