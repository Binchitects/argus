# Argus, standalone

The code index with its own small app, for a team that wants code search in a
chat and over MCP without the whole platform (`deploy/`): no single sign-on,
dashboards, logs, alerts, sandbox or image generation. It uses the same GPU and
the same container names as the platform, so a host runs one or the other.

Five services on one Docker network:

| service | what it is | reachable at |
|---|---|---|
| `argus` | the .NET backend serving the React app: sign-in, chat, administration, code index, knowledge packs, MCP | `http://localhost:8080` (`ARGUS_HTTP_PORT`) |
| `litellm` | the model gateway: one OpenAI-compatible API, a key and a budget per person | `http://localhost:4000/v1` (`GATEWAY_PORT`) |
| `postgres` | the gateway's database | internal |
| `llamacpp` | the chat model on the GPU | internal |
| `llamacpp-embed` | the embedding model (nomic-embed-text) on the CPU | internal |

Plus `model-init`, which downloads both models on first start and exits, and
the opt-in `power-limits` (GPU/CPU power caps, swappiness; privileged).

## Deploy

```bash
cp env-samples/qwen3.8-flash-next.rtx5090.env .env
awk '/^# SECRETS/{s=1} /^# APP/{s=0} s && /^[A-Z0-9_]+=$/{c="openssl rand -hex 24"; c|getline r; close(c); if ($0 ~ /^LITELLM_/) r="sk-" r; $0=$0 r} {print}' .env > .env.new && mv .env.new .env && chmod 600 .env
```

Set `ARGUS_ADMIN_EMAIL`, `LLAMACPP_MODEL_DIR` (on NVMe) and, for the code index,
`ARGUS_GITLAB_URL` and `ARGUS_GITLAB_TOKEN`. Then:

```bash
make up        # builds the app image the first time, downloads the models
make health    # every part up?
make smoke     # sign in and get a real answer from the model
```

Open `http://localhost:8080` and sign in as `admin` with `ARGUS_ADMIN_PASSWORD`
from `.env`. Add people on **People**; each gets a generated password, a
gateway account and a budget. `BIND_ADDRESS=0.0.0.0` serves the network;
`ARGUS_TLS_CERT`/`ARGUS_TLS_KEY` turn on HTTPS.

## Operate

| task | command |
|---|---|
| status | `make health`, `make ps`, `make logs S=argus` |
| back up | `make backup` — people, conversations, keys, index, packs, gateway database, `.env` |
| reset a password from the shell | `make user A="passwd admin"` |
| change the model | edit the MODEL block of `.env`, then `make restart` |
| update the app | `git pull && make build && make up` |

The full reference is [configuration](../../docs/argus/standalone/configuration.md),
[operations](../../docs/argus/standalone/operations.md) and
[architecture](../../docs/argus/standalone/architecture.md).
