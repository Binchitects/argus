# Deploying the platform

`deploy/` is a complete self-hosted LLM service: a GPU inference engine, a chat
UI, an API gateway with a key and a budget per person, single sign-on, an admin
console and dashboards. **The whole deployment is one `.env` file and
`docker compose up`.** There is no setup script.

**24 services** are enabled by the default profile set, with 15 profiles to
choose from — `argus` adds the code index, `embed` adds Ollama, `tracing` adds
Langfuse, and `logging` (Loki + Promtail) is **on by default**.

## Deploying — read this part

### Three steps

```bash
cd deploy
cp env-samples/qwen3.8-flash-next.rtx5090.env .env
```

Pick the sample that matches your model and card:

| sample | model | card | first-start download | status |
|---|---|---|---|---|
| `qwen3.8-flash-next.rtx5090.env` | Qwen3.8-Flash-Next, 177B MoE, UD-IQ4_XS | RTX 5090 32 GB + 64 GB+ RAM | 94 GB | measured here |
| `qwen3.8-27b.rtx5090.env` | Qwen3.8-27B, dense, NVFP4 with MTP | RTX 5090 32 GB | 17.1 GB | measured here |

Then make the secrets. No sample ships one; every empty value in the SECRETS section
has a comment saying how to make it, and this fills them all at once (the two LiteLLM
keys get the `sk-` prefix they require):

```bash
awk '/^# SECRETS/{s=1} /^# PEOPLE/{s=0} s && /^[A-Z0-9_]+=$/{c="openssl rand -hex 24"; c|getline r; close(c); if ($0 ~ /^LITELLM_/) r="sk-" r; $0=$0 r} {print}' .env > .env.new && mv .env.new .env && chmod 600 .env
```

Set `LLAMACPP_MODEL_DIR` to a directory **on NVMe** with room for the download, then:

```bash
make up
```

`make up` runs `scripts/preflight.sh` and then `docker compose up -d`. The
preflight earns its second: if this checkout has MOVED since the stack last
started, Docker keeps the containers on the **old** absolute paths and has
already created empty directories there to satisfy them, so nothing errors.
What you get instead is Traefik starting with no routes,
Alertmanager on a missing `alertmanager.yml`, the temperature exporter on a
missing `exporter.py` and Traefik exiting 127 — four unrelated-looking failures
that name files which plainly exist on disk. The preflight names the move.
`make preflight` runs it alone.

```bash
docker logs -f model-init
```

The first start downloads the model, checks every file against the SHA-256 that
Hugging Face publishes, then starts the engine. Open `https://llm.localhost`
and sign in as `admin` with `ADMIN_PASSWORD` from `.env`. The browser warns
once about the self-signed certificate; accept it.

**Adding a setup** for another model or card is one file: copy the closest sample,
edit its header and its MODEL block, and check it. Step by step, with how to choose
each value: [deploy/env-samples/README.md](../deploy/env-samples/README.md).

### Deploying without a network (airgap)

Same stack, same `.env`, but every image, model and embedding arrives on a disk
instead of from a registry. Build the bundle **on a machine that already runs
it**:

```bash
cd deploy
make airgap                                  # images + tree      (~7 GB measured)
make airgap A="--with-models --with-packs"   # + weights + docs   (~100 GB)
make airgap A="--split 4g"                   # parts for a FAT32 USB stick
```

Then, on the isolated host — no registry, no Hugging Face, no build:

```bash
unzip stack-airgap-<date>.zip
cd stack-airgap-<date>
cp deploy/.env.airgap deploy/.env
./fill-secrets.sh        # generates the ones that can be generated
./load.sh --check        # verify it all before anything starts
./load.sh --up
```

**If you used `--split`**, the parts must be **joined first**, from a directory
you can **write to**:

```bash
zip -s 0 stack-airgap-<date>.zip --out joined.zip   # writes into this directory
unzip -t joined.zip                                 # verify BEFORE trusting it
unzip joined.zip
```

Info-ZIP's `unzip` cannot read a split set at all, and the join writes into the
directory holding the parts — on a read-only mount it exits 0 having produced a
**0-byte file**, so verify with `unzip -t` rather than trusting the exit code.

Four things reach the network on a normal first start, and the bundle closes
all four rather than leaving them to be discovered on a machine that cannot fix
them:

| what | how it is handled |
|---|---|
| container images | `docker save` / `docker load`, including the three built locally, so the target never builds and never pulls |
| the GGUF weights | `--with-models`; `LLAMACPP_MODEL_DIR` is rewritten to a **relative** path, so the bundle runs from wherever it is unpacked |
| `model-init`'s Hugging Face check | `LLAMACPP_HF_FILES` is emptied. This is not tidiness: it asks the remote for the file size *before* accepting a local file, so with no network it exits 1 even when every weight is already on disk — and `llamacpp` declares it `service_completed_successfully`, so that exit 1 means the **engine never starts** |
| Ollama's embedding model | included, and restored into its volume. It is a Docker **volume**, not a bind mount, so nothing in the checkout hints that it is missing — and without it `docs_search` cannot embed a query |

**A fifth one is not a download at all: an image that is simply absent.**
The bundle carries the images for the profiles that were enabled where it was
built. Enable another profile on the target — edit `COMPOSE_PROFILES`, or drop
a different env sample over `.env` — and `docker compose up` stops on a pull
that host cannot make, *partway through*, with the other services already
running. `load.sh --check` renders the compose file against the `.env` that
will actually be used, compares the images it names with the bundle, and
refuses before anything starts:

```
  the .env asks for image(s) this bundle does not carry:
      vllm/vllm-openai:latest
error: this bundle was built for a different set of profiles.
```

`deploy/.airgap/manifest.txt` records which profiles the bundle covers. Build
with `--all-profiles` if the target may enable others.

`--with-env` puts the live `.env` in the bundle verbatim, secrets and all. Without
it the bundle ships `.env.airgap`: the same file with **every** secret emptied —
by name as well as by position, because some of them (`ARGUS_GITLAB_TOKEN`,
`ARGUS_GITLAB_PASSWORD`) live outside the `# SECRETS` block. `fill-secrets.sh` on
the target fills what can be generated and names the one that cannot.

The bundle is validated before it is written: every bind mount the compose file
asks for is checked to exist inside the staged tree, so a missing placeholder
directory cannot reach a host that has no way to fetch it. `./load.sh --check`
verifies checksums, images and mounts on arrival without changing anything.

### What `docker compose up` does before anything serves

Each of these used to be a script you had to run in the right order.

| service | does, from `.env` |
|---|---|
| `tls-init` | generates the self-signed certificate for `LLM_DOMAIN` and `*.LLM_DOMAIN`, keeps it, regenerates it if the domain changes |
| `auth-init` | builds Traefik's basic-auth file and hands the app the directory it writes for Argus |
| `app` (at start) | creates its database, the first admin from `ADMIN_PASSWORD`, and the OIDC clients from the `*_OIDC_CLIENT_SECRET` values; imports an Authelia install's people once |
| `model-init` | downloads and verifies the model; fails with the path in the message if a file is missing |
| `prometheus-secrets` | gives Prometheus the engine's scrape token |
| `power-limits` | applies `GPU_POWER_LIMIT_W` and `CPU_POWER_LIMIT_W`, and re-applies them after a reboot |

### Before you start

| requirement | why, and what happens without it |
|---|---|
| **NVMe for the model** | The engine memory-maps the GGUF and pages it in on demand. On NVMe Qwen3.8-Flash-Next serves at ~20 tok/s; on a spinning disk it is unusable. |
| **`nvidia-container-toolkit`** | Without it every GPU service fails with `could not select device driver`. `sudo ./scripts/install-requirements.sh` installs it and Docker. |
| **Your user in the `docker` group** | And log out and back in: group membership is fixed at login. |
| **A stable mount for the checkout** | See "The reboot trap" below. |
| **For Argus: a GitLab and a read-only token** | See "Connecting tools to Argus". |
| **RAM for a MoE model** | See "RAM" below. 64 GB works for Flash-Next; more is faster. |

### Addresses

| address | what | sign-in |
|---|---|---|
| `https://llm.localhost` | **the app**: sign-in for everything, **chat** (models, thinking, branches, files, Argus), usage and cost, people, API keys, credit, 2FA, **every setting**, the model, the code index, packs, audit log, **dashboards, logs and alerts** | its own sign-in; Admin needs `admins` |
| `https://chat.llm.localhost` | Open WebUI, with Argus as a tool, until the app's chat is signed off ([chat.md](chat.md)) | SSO |
| `https://gateway.llm.localhost/v1` | OpenAI-compatible API for tools | **the person's own API key** |
| `https://argus.llm.localhost/mcp` | Argus MCP server (profile `argus`) | **the person's own GitLab token** |
| `https://metrics.llm.localhost` · `alerts.` | Prometheus, Alertmanager | SSO, `admins` only |

Sign in as `admin` with the password from `.env`:

```bash
grep ADMIN_PASSWORD .env
```

Replace `llm.localhost` with your `LLM_DOMAIN`. Browsers resolve `*.localhost` by
themselves; **curl, Python, Node and every SDK on another machine do not** — run
`sudo ./scripts/setup-hosts.sh` there, or add the names to that machine's hosts file.

### Connecting tools to the API

Create the person under **Admin → People**; it shows their API key once. Every
OpenAI-compatible tool needs the same four things:

| setting | value |
|---|---|
| base URL | `https://gateway.llm.localhost/v1` |
| API key | that person's key (never `LITELLM_MASTER_KEY`: it has no budget and bills nobody) |
| model | `MODEL_NAME` from `.env`, e.g. `Qwen3.8-Flash-Next` |
| certificate | run host tools through `deploy/scripts/with-ca.sh` (below) |

The certificate is self-signed, and **TLS verification happens in the client**:
nothing the stack does on its side can make a host tool accept it. So each runtime
has to be told, and each wants a different variable.

| runtime | variable | what it does with it |
|---|---|---|
| Node — DeepSeek Harness, Qwen Code | `NODE_EXTRA_CA_CERTS` | **adds** to the public roots |
| Python — `requests`, `httpx`, `urllib` | `SSL_CERT_FILE`, `REQUESTS_CA_BUNDLE` | **replaces** the trust store |
| curl | `CURL_CA_BUNDLE` | **replaces** the trust store |
| git | `GIT_SSL_CAINFO` | **replaces** the trust store |

That add/replace split is why `tls.crt` alone is not enough for the bottom three:
pointing `SSL_CERT_FILE` at it stops the tool trusting the public internet too.
`deploy/scripts/with-ca.sh` sets every one of them correctly — `tls.crt` where
the variable adds, `bundle.crt` (public roots **plus** the stack's) where it
replaces — and installs nothing anywhere:

```bash
cd deploy
./scripts/with-ca.sh curl https://gateway.llm.localhost/v1/models -H "Authorization: Bearer sk-YOURKEY"
./scripts/with-ca.sh dsh web
./scripts/with-ca.sh python3 my_client.py
```

To put them in your own shell instead of prefixing each command:

```bash
eval "$(./scripts/with-ca.sh --print)"     # same as: make ca
```

A `000`, a `certificate verify failed`, or a connection error from a host tool is
almost always this variable rather than the stack being down. Containers need
none of it: they mount the certificate and set these same variables themselves.
Go and Java tools (Traefik, most JVM CLIs) read the **operating
system's** store and honour none of these variables — `with-ca.sh` says so on
stderr rather than appearing to do nothing.

**DeepSeek Harness (`dsh`)** is a Node process and has no per-provider CA
setting; its own docs state it neither sets nor validates one. It reads
`NODE_EXTRA_CA_CERTS` **at process start**, so setting it afterwards does
nothing — it has to be in the environment that launches it:

```bash
NODE_EXTRA_CA_CERTS="/path/to/stack/config/traefik/certs/tls.crt" dsh web
```

Then add a provider with base URL `https://gateway.llm.localhost/v1` and the
person's key from **Admin → People**. (The wrapper form above does the same thing
without exporting anything permanent.)

```bash
curl --cacert deploy/config/traefik/certs/tls.crt https://gateway.llm.localhost/v1/chat/completions -H "Authorization: Bearer sk-YOURKEY" -H 'Content-Type: application/json' -d '{"model":"Qwen3.8-Flash-Next","messages":[{"role":"user","content":"hi"}],"max_tokens":300}'
```

**Qwen Code** — `~/.qwen/settings.json` (the `mcpServers` part adds Argus, below):

```json
{
  "env": {
    "LOCAL_LLM_API_KEY": "sk-YOURKEY",
    "NODE_EXTRA_CA_CERTS": "/path/to/stack/config/traefik/certs/tls.crt"
  },
  "modelProviders": {
    "openai": [
      {
        "id": "Qwen3.8-Flash-Next",
        "name": "[Local] Qwen3.8-Flash-Next",
        "baseUrl": "https://gateway.llm.localhost/v1",
        "envKey": "LOCAL_LLM_API_KEY",
        "generationConfig": { "contextWindowSize": 262144 }
      }
    ]
  },
  "mcpServers": {
    "argus": {
      "httpUrl": "https://argus.llm.localhost/mcp",
      "headers": { "Authorization": "Bearer YOUR_GITLAB_PAT" }
    }
  }
}
```

Then `qwen -m Qwen3.8-Flash-Next`.

**Hermes** — see [docs/hermes.md](hermes.md); use the model's
real name and append `tls.crt` to Hermes's own CA bundle.

**Anything else** (OpenAI SDK, IDE plugins, other agents) takes the same base URL,
key and model.

### Connecting tools to Argus

Argus is an MCP server at `https://argus.llm.localhost/mcp` (compose profile
`argus`). **Each developer authenticates with their own GitLab personal access
token** (`read_api`) as a Bearer token, and Argus shows them only the repositories
that token can read.

```bash
claude mcp add --transport http argus https://argus.llm.localhost/mcp --header "Authorization: Bearer YOUR_GITLAB_PAT"
```

Qwen Code: the `mcpServers` block above. Any other MCP client: the same URL and header.

**To make Argus work on a deployment:**

1. Add `argus` to `COMPOSE_PROFILES` in `.env`.
2. Set `ARGUS_GITLAB_URL` and `ARGUS_GITLAB_TOKEN`. The token is **read-only**:
   `read_api` and `read_repository`, for an account that is at least Reporter in every
   project you want indexed. Argus never needs admin or sudo.
   If no token can be issued for the account, set `ARGUS_GITLAB_USERNAME` and
   `ARGUS_GITLAB_PASSWORD` instead: Argus signs in through GitLab's web form and mints
   its own read-only token. See
   [When no token can be issued](argus/README.md#when-no-token-can-be-issued-for-the-account).
3. If your GitLab's certificate is not from a public CA, set **one** of these — see
   [GitLab on a private CA](#gitlab-on-a-private-ca):
   `ARGUS_GITLAB_CA_CERT=/etc/argus/tls/gitlab-ca.pem` (drop the PEM in
   `config/argus/tls/` first), or `ARGUS_GITLAB_VERIFY=false` when no CA file
   exists anywhere.
4. `make up`, then start an index run from **Admin → Indexing**.
   After that it keeps itself current — see [Keeping the index
   current](argus/overview.md#keeping-the-index-current).

### GitLab on a private CA

Argus reaches GitLab over **two transports that cannot see each other's TLS
configuration**: `httpx` for the API, and the `git` binary for clones. Configuring
one and not the other is the failure that costs the most time, because it reads as a
bad credential — the API enumerates every project, and the first clone then dies with
`server certificate verification failed`.

Both settings in `.env` therefore apply to both transports:

| setting | when | what it does |
|---|---|---|
| `ARGUS_GITLAB_CA_CERT` | you have the CA that signed GitLab's certificate | verifies against the public roots **plus** that CA |
| `ARGUS_GITLAB_VERIFY=false` | self-signed, and **no** CA file is available anywhere | disables verification for every request and clone to that GitLab |

Drop the PEM in `deploy/config/argus/tls/` and give `ARGUS_GITLAB_CA_CERT` the path
**as the container sees it** (`/etc/argus/tls/<name>`). For a standalone deployment
outside compose, point it at any path the Argus process can read.

`ARGUS_GITLAB_VERIFY=false` means anything able to answer on that hostname can read
the service token — the most privileged string in the deployment. Use the CA whenever
one exists. Setting **both** is refused at startup rather than silently resolved,
because a CA bundle is exactly what makes verification possible.

### Argus in Open WebUI

With the `argus` profile on and `ARGUS_CHAT_CLIENT_TOKEN` set (the samples list it
under SECRETS), Open WebUI registers Argus as a tool. In a chat, enable **Argus**
from the tools button beside the message box.

It answers **per person**, like the developer path. Open WebUI can send only one
shared credential, so it proves it is the chat client with `ARGUS_CHAT_CLIENT_TOKEN`
and forwards the signed-in person's email; Argus reads that person's GitLab project
memberships with the read-only service token. Two rules follow:

- **Argus matches the person by their email address.** That address is what
  Open WebUI forwards and the one identifier the whole stack agrees on, so the
  chat account and the GitLab account do **not** have to share a username. What
  the lookup can see depends on your GitLab token, and nothing in the API
  response says which case you are in:

  | service token | what `search=` can match |
  |---|---|
  | an **administrator** | the private email — any account resolves |
  | read-only *(recommended)* | only the **public** email, which is empty by default |

  So with a read-only token, either set the address as the profile's public
  email, or name the chat account after the GitLab account — that path is still
  tried, second. With an admin token, nothing needs matching.
- **The chat-client token only works from inside the stack.** Open WebUI calls
  `http://argus:7700` on the compose network; the same token arriving through
  Traefik is refused, so a leaked token cannot claim someone else's email.

### When someone has no access

Argus does not answer "nothing found" when the only matches are in repositories
the person cannot read. It says which repositories hold them and who maintains
each, and tells them to ask a maintainer for Reporter access — for example:

```
Nothing you have access to matches this, but it does exist in 1 repository you cannot read:
- platform/billing (3 matches) -- maintainers: @olive (Olive Owner), @max (Max Maint)
Tell the person asking that they do not have access, and that they can ask a maintainer
listed above to add them in GitLab with at least Reporter access. Argus picks the change
up within 10 minutes.
```

Only repository names and maintainers are disclosed, never a path, symbol or line.
Reading a file or repository map in an unreadable repository gives the same message.
`ARGUS_ACCESS_NOTICES=0` on the argus service restores a plain "nothing found".

### "Failed to connect to Argus" in Open WebUI

**Usually it is not a connection problem.** Argus answers `401`, and Open WebUI
renders any 401 as a connection failure, so the obvious next step — checking DNS,
the network, whether Argus is up — finds everything healthy and explains nothing.

The usual cause is that **the person has no GitLab account**. Argus identifies a
chat user by their email, falling back to their username, because the chat token
is one shared credential and cannot say who is asking. If neither matches a GitLab
account, Argus cannot know which repositories that person may read, so it refuses
rather than guess.

```bash
docker compose logs argus | grep denied
```

```
{"event":"denied","reason":"token_rejected","path":"/mcp",
 "detail":"No GitLab account matches admin@llm.localhost (looked up by email,
           then by username 'admin')."}
```

`detail` is the sentence the person was actually shown. Two fixes, depending on
which is true:

- **They should have GitLab access** — create the account, or make its email or
  username match their SSO identity. Add them to the projects they need at
  Reporter or above.
- **They should not** — that is the ACL working. An operator who only runs the
  stack and should not read the estate's code should not be given a GitLab
  account for Argus's sake.

To check a mapping without going through the UI, use the same path Open WebUI
does — the chat token plus the email header:

```bash
docker compose exec identity-proxy python - <<'PY'
import json, urllib.request
tok = "<ARGUS_CHAT_CLIENT_TOKEN from .env>"
req = urllib.request.Request("http://argus:7700/mcp",
    data=json.dumps({"jsonrpc":"2.0","id":1,"method":"initialize","params":{
        "protocolVersion":"2025-06-18","capabilities":{},
        "clientInfo":{"name":"probe","version":"1"}}}).encode(),
    headers={"Authorization": "Bearer " + tok,
             "x-openwebui-user-email": "someone@example.com",
             "Content-Type": "application/json",
             "Accept": "application/json, text/event-stream"}, method="POST")
print(urllib.request.urlopen(req, timeout=30).status,
      urllib.request.urlopen(req, timeout=30).headers.get("mcp-session-id"))
PY
```

A `401` names the reason. Anything else means the identity resolved and the
problem is elsewhere.

### Admin, usage and cost

Everything an operator does is in the app at `https://llm.localhost`: **Admin**
(admins only) has Overview, People, Model, Indexing, Packs, Explore, Monitoring,
Settings, the audit log and sign-in settings; **Usage & cost** shows everyone's
usage to admins and each person their own. The old `https://admin.llm.localhost`
redirects there, page for page.

The Model page **shows** the steps for switching a model rather than performing
them: performing them would need the Docker socket, and a socket in a web app is
root on the host for anyone who reaches it.

Details: [docs/admin.md](admin.md).

### Tokens and cost

Every request is priced by the gateway from three values in `.env`, per 1M tokens,
the way DeepSeek bills: prompt tokens the engine had to process
(`PRICE_INPUT_PER_MTOK`), prompt tokens served from its prefix cache — a resent
conversation or system prompt — (`PRICE_CACHED_INPUT_PER_MTOK`), and generated tokens
(`PRICE_OUTPUT_PER_MTOK`). Checked against the spend log: an identical request
resent, with 84 of its 88 prompt tokens cached, cost 40% less, to the digit.

The **Usage by person** dashboard has a *Tokens and cost* section: cache-miss input,
cache-hit input, hit rate, output and cost in total, per person, and over time.
**LLM Overview** splits token throughput the same way. Credit limits in the admin
panel are in the same currency.

### Customizing

Everything is a value in `.env`. Change it, then run `docker compose up -d`: compose
recreates exactly the containers the change affects.

| to change | set in `.env` | notes |
|---|---|---|
| **another model** (same family, other quant, other card) | the MODEL block | copy the block from the closest `env-samples/` file; how to choose each value is in [deploy/env-samples/README.md](../deploy/env-samples/README.md) |
| **a model with no sample** | a new file in `env-samples/` | same guide, "Adding a new setup"; any GGUF on Hugging Face works via `LLAMACPP_HF_REPO` + `LLAMACPP_HF_FILES` |
| context window | `MODEL_CONTEXT` | on a CUDA out-of-memory at start, lower `-ub` in `LLAMACPP_EXTRA_ARGS` first |
| people served at once | `LLAMACPP_PARALLEL` | keep `--kv-unified` so they share one context pool |
| MoE layers kept in RAM | `LLAMACPP_N_CPU_MOE` | raise on out-of-memory, lower for speed |
| multi-token prediction | `LLAMACPP_MTP_DRAFT_MAX` | `2` for dense models with an MTP layer, `0` for MoE on CPU |
| domain | `LLM_DOMAIN` | certificate and single sign-on follow; check with `./scripts/domain-check.sh --old <previous>` |
| reachable from the network | `BIND_ADDRESS=0.0.0.0` | default `127.0.0.1` is this machine only |
| ports | `TRAEFIK_HTTP_PORT`, `TRAEFIK_HTTPS_PORT` | |
| which services run | `COMPOSE_PROFILES` | `argus` code index, `embed` Ollama, `tracing` Langfuse, `cadvisor`, `dcgm`. `logging` (Loki + Promtail) is **on by default** — remove it to stop collecting logs |
| GPU / CPU power cap | `GPU_POWER_LIMIT_W`, `CPU_POWER_LIMIT_W` | empty restores the hardware default; see [Measured](#measured) for what these do and do not buy |
| CPU threads and ceilings | `LLAMACPP_THREADS`, `LLAMACPP_CPUS`, `OLLAMA_CPUS`, `POSTGRES_CPUS` | threads = physical cores; ceilings must sum under the core count |
| engine RAM ceiling | `LLAMACPP_MEM_LIMIT` | e.g. `56g`; `0` = none |
| pin the model in RAM | `LLAMACPP_MLOCK`, `LLAMACPP_PRELOAD`, `LLAMACPP_RAM_RESERVE_GB` | `auto` pins when the weights fit; see RAM |
| host swappiness | `HOST_SWAPPINESS` | empty = system default |
| prices | `PRICE_INPUT_PER_MTOK`, `PRICE_CACHED_INPUT_PER_MTOK`, `PRICE_OUTPUT_PER_MTOK` | per 1M tokens; cache hits priced separately (DeepSeek-style) |
| the admin's email | `ADMIN_EMAIL` | the app's first admin and Open WebUI's administrator |
| company directory | `LDAP_URL`, `LDAP_BIND_DN`, `LDAP_BIND_PASSWORD`, `LDAP_USER_BASE_DN`, `LDAP_ADMIN_GROUP`, `LDAP_REQUIRED_GROUP` | LDAP or Active Directory sign-in next to local accounts; see [AUTHENTICATION](authentication.md) |
| default credit per person | `LITELLM_DEFAULT_USER_BUDGET`, `LITELLM_BUDGET_DURATION` | per person under **Admin → People** |
| a different llama.cpp build | `LLAMACPP_ENGINE_URL`, `LLAMACPP_ENGINE_SHA256` | a release tarball; empty = the image's own server |
| vLLM instead of llama.cpp | `COMPOSE_PROFILES` (`vllm` instead of `llamacpp`), the `VLLM_*` values with `VLLM_SERVED_MODEL_NAME` equal to `MODEL_NAME`, `ENGINE_API_BASE=http://vllm:8000/v1` | exactly one engine profile at a time; **not re-tested since the compose-only change** — the shipped samples are llama.cpp |
| gated Hugging Face repos | `HF_TOKEN` | |
| updating knowledge packs | `ARGUS_PACK_INDEX_URL` | the published index JSON the **Update** button on **Admin → Packs** reads. Unset = install and remove still work and Update is absent, not broken |
| backups | `BACKUP_DIR`, `BACKUP_COPY_DIR`, `BACKUP_KEEP`, `BACKUP_INCLUDE_LOGS`, `BACKUP_TIME` | `./scripts/backup.sh` takes a complete, verified backup (pg_dumpall, SQLite online copies, config with secrets); `sudo ./scripts/backup.sh --install-timer` runs it daily; `--restore --from <dir>` puts it back; `BACKUP_COPY_DIR` keeps a verified second copy on another disk |

Config files, for what `.env` does not cover: alert rules in
`deploy/config/prometheus/rules/`, dashboards in `deploy/config/dashboards/`
(Grafana's JSON format; the app draws them and uses an edited file at once). Who may reach which service is
decided by the app; see [AUTHENTICATION](authentication.md).

### Switching the model

**Admin → Model** shows what is running and, for each
sample, the exact `.env` block to paste and the command. Every model setting sits
between `# >>> MODEL` and `# <<< MODEL`; replace that block, keep your own
`LLAMACPP_MODEL_DIR`, and run `docker compose up -d`. A model not yet on disk is
downloaded first. The engine, gateway and Open WebUI all take the name from
`MODEL_NAME`, so they cannot disagree.

### Measured

**RTX 5090, the shipped samples.** i7-14700K (20 physical cores), 123 GB RAM, RTX 5090
32 GB, NVMe, no power caps (the CPU peaked at 80 °C under two people). `multiuser-bench.py`,
400-token answers through the gateway, medians of 3 rounds, q8_0 KV cache, 256K context:

| | one person | two people, each | 28,500-token prompt | VRAM |
|---|---|---|---|---|
| Qwen3.8-Flash-Next UD-IQ4_XS, `N_CPU_MOE=37`, `-ub 2048` (sample) | **26.9 tok/s** | 16.1 tok/s | 48 s | 31.0 GB |
| Qwen3.8-Flash-Next UD-IQ4_XS, `N_CPU_MOE=36`, `-ub 1024` | 28.2 tok/s | 16.6 tok/s | 75 s | 28.2 GB |
| Qwen3.8-Flash-Next NVFP4 W4A16 (180 GB), `N_CPU_MOE=39` | 23.9 tok/s | 13.9 tok/s | 90 s | 29.6 GB |
| Qwen3.8-27B NVFP4, MTP 2 drafts (sample) | **112.8 tok/s** | 83.2 tok/s | 7.8 s | 28.2 GB |
| Qwen3.8-27B NVFP4, MTP 3 drafts | 107.0 tok/s | 89.3 tok/s | | |
| Qwen3.8-27B NVFP4, no MTP | 77.7 tok/s | 66.4 tok/s | | |

The NVFP4 build of Flash-Next keeps attention, shared experts and per-layer embeddings
in BF16, which makes it larger than RAM plus VRAM, so part of it is always paged from
NVMe; UD-IQ4_XS fits in memory and is faster at everything measured. For the 27B, NVFP4
runs entirely on the card and MTP with 2 drafts is the fastest for one person (~69% of
drafts accepted); 3 drafts helps two people a little and one person less.

The rest of this section is from the earlier machine, which no sample targets any more:
i7-13700K (16 physical cores), 61 GB RAM, RTX 3090, NVMe, with the
power limits below applied. Every number comes from a script in `deploy/scripts/`.

**Two people at once** — Qwen3.8-Flash-Next, 400-token answers through the gateway
(`multiuser-bench.py`, medians of 3 rounds, repeated across 4 engine restarts):

| | decode | time to first token |
|---|---|---|
| one person | **20.2 tok/s** | 0.3 s |
| two people, each | **12.1 tok/s** | 2.0 s |
| two people, combined | 24.4 tok/s (1.2× one) | |

Other scenarios from the same script: a third request on two slots queued 18 s and then
ran at full speed; secrets in two concurrent prompts never crossed (0 leaks in 3 rounds);
a repeated 14,000-token prompt answered in 0.3 s from the prefix cache, for the other
person too.

**Qwen3.8-27B on the same 3090** — 131K context in 23.8 of 24 GB VRAM. **10.3 tok/s
with MTP** (2 drafted tokens, ~70% accepted) against 6.8 without: for a dense model on
the GPU, MTP is a 50% win. Measured at the 150 W GPU cap, which it hits; raise
`GPU_POWER_LIMIT_W` for a 27B deployment.

**Qwen Code on a large codebase** — Qt Creator 4.11.2 (12,091 files), headless, scored
against answers fixed beforehand with grep (`qwen-code-realworld.py`). It found the
text editor's duplicate-selection implementation at the right line in an 8,708-line
file (247 s), and answered a question about the 222,876-line `sqlite3.c` correctly with
grep and ranged reads instead of reading it whole (255 s; 63,345 of 74,100 prompt
tokens served from the prefix cache).

**Power limits.** This board shipped with Intel's CPU power limits removed (4095 W).
Under two users the CPU held 93–97 °C, and fewer threads did not help — measured at
16, 12, 10 and 8 — because package temperature follows the hottest core, and each busy
core runs flat out. Capping power did:

| two people, 16 threads | no caps | CPU 125 W, GPU 150 W |
|---|---|---|
| peak CPU temperature | 95 °C | **86 °C** |
| samples at or above 90 °C | 17% | **0%** |
| decode, one person / each of two | 20.4 / 12.2 tok/s | 20.2 / 11.8 tok/s |

**The caps are not what limits Flash-Next on the 3090, and raising them is not a
speed-up.** Measured directly: `GPU_POWER_LIMIT_W` 150 → 350 changed decode by
nothing (21.25 → 21.5 tok/s, n=4 each) — the card never drew more than 169 W peak
or 134 W average at 22–38% SM utilisation, so it is not power-limited.
`CPU_POWER_LIMIT_W` 125 → 253 bought +2% (inside the run-to-run spread) for
**+43 °C** of package temperature. Both are best left at the values in `.env`.
The same measurement rules out the other obvious levers: moving expert layers to
the GPU (`N_CPU_MOE` 42 → 40) changed nothing and spent 2.2 GB of VRAM, and
raising `-ub` to 2048 lifted prompt processing only 8.5% while costing 7% of
decode. On this hardware the engine is at its documented number — **20.9 tok/s
warm against 21.7 in the table below** — and the limit is that a 94 GB model is
running on 61 GB of RAM.

**MTP does not help Qwen3.8-Flash-Next here.** Four alternating runs, same build:
off 20.3 / 12.2 tok/s (one / each of two), on 19.9 / 11.6. Each drafted token routes to
different experts in system RAM, so verification multiplies the slow part. Mainline
llama.cpp has no MTP graph for this architecture anyway (ggml-org/llama.cpp#28243);
`LLAMACPP_ENGINE_URL` can run Unsloth's build that has one, and the stock image and
that build measured the same within noise with MTP off.

### RAM

**Pinning.** `LLAMACPP_MLOCK=auto` (the default in every sample) pins the weights in
RAM with `--mlock` whenever they live in system RAM and fit beside
`LLAMACPP_RAM_RESERVE_GB`. Pinned, nothing is ever read from disk again. The engine
log says what it decided and why, with the numbers, for example:

```
memory: pinning 52 GB of weights in RAM (--mlock); 76 GB left for the rest
memory: weights run on the GPU; nothing in RAM to pin
memory: NOT pinning -- model 87 GB exceeds RAM 61 GB minus the 8 GB reserve; ...
```

Pinning is not forced when the model is larger than RAM, because it cannot work:
the kernel would thrash or kill something to honour it. For Qwen3.8-Flash-Next that
means **96 GB+ of RAM to pin it**; the 27B runs entirely on the GPU and has nothing to
pin. `LLAMACPP_PRELOAD=auto` reads a model that fits into the page cache before
serving, at full sequential disk speed, rather than during the first requests.

**When the model is larger than RAM** (Flash-Next on 64 GB), the weights page in from
NVMe on demand. RAM is still fully used — as page cache, which `free` reports under
`buff/cache`, not `used`. Measured on a 61 GB machine under two users, the paging
fades as the cache settles, and decode speed never suffered:

| after start | NVMe reads | major page faults | decode, one / each of two |
|---|---|---|---|
| ~5 min | 24 MB/s | 524/s | 19.9 / 11.9 tok/s |
| ~12 min | 10 MB/s | 251/s | 19.9 / 11.9 tok/s |
| ~20 min | 6 MB/s | 198/s | 21.2 / 12.6 tok/s |

**Swap.** `HOST_SWAPPINESS` sets the host's `vm.swappiness` (the power-limits service
applies it and restores the original when cleared). Measured: 60 and 150 made no
difference here — the improvement above is the cache warming — so the samples leave
the system default. Idle services' memory goes to swap on its own (5.4 GB after 20
minutes), which is what gives the model the room.

### The traps

**`*.localhost` resolves in browsers but not in curl, Python or any SDK.** Run
`sudo ./scripts/setup-hosts.sh`, or every command-line call fails with a DNS error that
looks like the stack is down.

**The reboot trap.** If the checkout lives on a removable or automounted volume,
Docker's `restart: unless-stopped` starts the stack before the volume mounts and binds
empty directories. Mount it from `/etc/fstab` with `x-systemd.before=docker.service`:

```
UUID=<uuid>  /mnt/data  ntfs3  defaults,nofail,x-systemd.before=docker.service,uid=1000,gid=1000  0 0
```

**Never change `LITELLM_SALT_KEY` or `APP_DATA_KEY` after the first start.** Both
encrypt stored data. `APP_DATA_KEY` encrypts the app's session and OIDC signing
keys, and the app refuses to start with a different one rather than sign everyone
out; LiteLLM's key encrypts credentials it stores in its database, which become
unreadable.

**A long paste stalls the other person.** A 28,500-token paste took 97 s to process,
and a short question sent 2 s later waited 95 s behind it: the engine processes one
prompt at a time. Chat-sized prompts are unaffected, and agents re-sending a
conversation hit the prefix cache (a repeated 14,000-token prompt answered in 0.3 s).

**`docker compose down -v` deletes vLLM's model cache.** llama.cpp models live in
`LLAMACPP_MODEL_DIR` on the host and are never deleted.

**Open WebUI settings come from `.env` only.** `ENABLE_PERSISTENT_CONFIG=false`, so
changes made in its admin UI do not survive a restart.

**Set `BIND_ADDRESS`.** The samples use `127.0.0.1`. `0.0.0.0` serves everything to
your network.

**`-t` must be the physical core count, never logical.** On the 13700K (16 physical,
24 logical): 24 threads 10.2 tok/s, **16 threads 21.7**, 8 threads 17.9. That is
`LLAMACPP_THREADS`.

**On a startup CUDA OOM, lower `-ub` before the context.** The compute buffer scales
with context × ubatch and appears in no weights-plus-KV calculation; at 256K context
`-ub 4096` asked for 15 GB in one allocation, `-ub 1024` needed 3.7 GB.

**Empty replies.** These models return their reasoning in a separate
`reasoning_content` field. With a small `max_tokens` the whole budget goes to
reasoning and the reply is empty. Give it 300+ tokens.

**Thinking level.** Chosen per chat from the model dropdown, because the stack
creates one Open WebUI model per level:

| picker entry | what it does |
|---|---|
| `… · Deep think` | `reasoning_effort: xhigh` — the engine's own default |
| `… · Balanced` | `reasoning_effort: medium` |
| `… · Quick` | `reasoning_effort: low` |
| `… · No thinking` | `enable_thinking: false` — answers immediately |

Measured on the same prompt: 2654 / 1251 / 0 characters of reasoning. Set
`THINKING_PRESETS` in `.env` to change the list, or leave it empty for none.

**The `Reasoning Effort` field in Open WebUI's own params panel does nothing
here** — it goes out as a top-level field, and LiteLLM drops that for a custom
`openai/` api_base. Measured identical to setting nothing at all. Use the
picker. See
[docs/configuration.md](configuration.md#changing-the-thinking-level-per-chat).

**Low GPU utilisation with Flash-Next.** Expected: the experts run on the CPU, and the
card idles between attention layers.

**Traefik returns 404 for a running service.** It does not route containers whose
healthcheck is failing; `docker compose ps` shows which.

### Checking it

```bash
python3 scripts/acceptance.py
```

```bash
python3 scripts/functional-test.py
```

```bash
./scripts/domain-check.sh
```

`acceptance.py` checks wiring, and that `.env` and every sample resolve completely.
`functional-test.py` does what people do, for real: creates a person in the app,
signs them in, uses their key, proves a credit limit binds and a rotated key dies,
signs into Open WebUI with the right role, reads the dashboards, logs and alerts as the
admin (and is refused as the person), and confirms a chat is billed to whoever typed
it — 68 checks. `domain-check.sh` proves the running stack answers
for `LLM_DOMAIN`. `audit-auth.sh`, `multiuser-bench.py` and `qwen-code-realworld.py`
go deeper on login, concurrency and agent work.

### If you enable pgvector

Three settings, none optional, each of which silently degrades the index:

- **`hnsw.ef_search`** defaults to 40 and `LIMIT k` does not raise it. Left alone,
  recall pins at 61% and does not move from k=128 to k=2048.
- **`hnsw.iterative_scan`** — without it a filtered search examines `ef_search`
  nodes and only *then* discards what the ACL rejects.
- **`SET`, not `SET LOCAL`** — autocommit gives every statement its own
  transaction, so `LOCAL` expires before the query that needed it.

Argus's semantic layer also needs an embedding provider. Ollama is in the compose
file under the `embed` profile; without it there is nowhere for vectors to come
from, whichever database stores them. The embedder is the latency users feel, so
it is worth a GPU: measured warm, GPU embedding is **94 ms → 5 ms** median per
embed, 18×, for 849 MB of VRAM.

---
