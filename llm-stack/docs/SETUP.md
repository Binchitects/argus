# Guided setup

One command, and it asks for what it cannot safely guess.

```bash
./scripts/setup.sh          # Linux, macOS, WSL
.\scripts\setup.ps1         # Windows
```

Options:

| flag | what it does |
|---|---|
| `--dry-run` | print the whole plan, change nothing |
| `--defaults` | accept every default, ask nothing |

**Safe to re-run.** Existing secrets are kept, every question offers the
current value as its default, and the profile questions default to what is
already enabled — so a second pass reviews the configuration instead of
resetting it.

It orchestrates rather than reimplements. `bootstrap.sh`, `gen-certs`,
`gen-auth`, `llm-users` and `docker compose` are each tested on their own; a
second copy of that logic inside the installer would drift from them without
anyone noticing.

## Unattended setup

Every question can be answered on the command line, which is what makes a
repeatable or scripted deploy possible:

```bash
./scripts/setup.sh --domain llm.example.com
./scripts/setup.sh --defaults --domain box.local --set VLLM_MAX_MODEL_LEN=65536

# llama.cpp instead of vLLM, fully unattended
./scripts/setup.sh --defaults   --set LLM_ENGINE=llamacpp   --set LLAMACPP_MODEL_DIR=/d/llm-models/Qwen3.8-Flash-Next-IQ4_XS   --set LLAMACPP_MODEL_FILE=Qwen3.8-Flash-Next-UD-IQ4_XS-00001-of-00003.gguf   --set LLAMACPP_SERVED_MODEL_NAME=qwen3.8-flash-next   --set LLAMACPP_CONTEXT=262144
```

| flag | |
|---|---|
| `--domain D` | set `LLM_DOMAIN` without being asked |
| `--set K=V` | set any `.env` key; repeatable |
| `--defaults` | accept every default, ask nothing |
| `--dry-run` | print the plan, change nothing |
| `--no-start` | write every config file but start no containers |

`--no-start` is for an appliance that is configured on one machine and started
on another: certificates, secrets and `.env` are all written, and
`docker compose up -d` is left for the destination.

Both are written to `.env` **before** any question runs, so they are accepted
silently under `--defaults` and pre-filled when prompting.

## Changing the domain

`LLM_DOMAIN` reaches further than the Traefik routes. Authelia names the domain
in its OIDC issuer, its session cookie, every access-control rule and every
redirect URI — and those files used to carry `llm.localhost` literally. Setting
a different domain moved the routes and left Authelia answering for a domain
nobody was asking about: **SSO broke with nothing in any log**, because nothing
had failed.

Two files are therefore **templates**, rendered by `gen-auth.sh` with the
domain substituted:

```
config/authelia/configuration.template.yml  ->  configuration.yml
config/homepage/services.template.yaml      ->  services.yaml
```

Edit the `.template.` files. The rendered ones are generated and gitignored.
`clients.yml` and `users.yml` are generated the same way and follow the domain
automatically.

**`*.localhost` does not resolve on its own.** The `llm.localhost` names work
because `scripts/setup-hosts.sh` writes them to the hosts file. A new domain
needs the same treatment, or real DNS.

## Re-running setup

`setup.sh` is safe to re-run: the CA, the OIDC signing key, `users.yml` and
every secret already in `.env` are kept, and only the templates are re-rendered.

**One thing can still bite, and it is now caught rather than suffered.**
Authelia keeps its state in SQLite at `/data/db.sqlite3` inside the
`authelia-data` volume, encrypted with `AUTHELIA_STORAGE_ENCRYPTION_KEY`. The
key lives in `.env`; the database lives in a Docker volume. Delete `.env` — or
start from a fresh checkout — while that volume survives, and `gen-auth` mints
a **new key against the old database**. Authelia then crash-loops on every
start, unable to decrypt, and the error names the encryption key rather than
the volume nobody thought to remove.

`setup.sh` compares the key before and after `gen-auth` and refuses to start
Authelia against a database it cannot read:

```
!   the Authelia encryption key changed, but llmservice_authelia-data still holds a database
    Reset it with:  docker volume rm llmservice_authelia-data
X   refusing to start Authelia against an undecryptable database
```

That volume holds **sessions only** — never user accounts, which live in
`config/authelia/users.yml`. Removing it logs everyone out and costs nothing
else.

## What it asks

**Domain.** Services appear at `chat.<domain>`, `gateway.<domain>` and so on.
`llm.localhost` is fine for a single machine.

**Engine.** First *which* engine (see below), then that engine's settings.
For vLLM: the HuggingFace model id, the name clients will use for it, context
length, and how much VRAM it may claim. vLLM serves **one model at a time** and
claims that fraction of the card up front, so nothing else can load beside it —
a 24 GB card cannot hold an 8B and a 27B together.

**Profiles.** Which parts to run: the gateway is always on because it is what
gives each person a key and a budget. TLS, SSO, GPU metrics, the code index,
logging and tracing are each optional.

**Budgets.** The default ceiling per person and how often it resets. Counted
across the API and the web UI together. **Not `0`** — LiteLLM reads zero as a
budget of zero and refuses every request, which looks exactly like a broken
gateway.

**Argus**, if enabled: the GitLab URL and an access token. Argus resolves
every request's identity against GitLab, so it cannot serve without one. The
token goes to `.env`, which is gitignored.

## Choosing an inference engine

Two engines ship with the stack, and **exactly one runs**. Both claim the whole
GPU; enabling both profiles means whichever loses the race dies with a CUDA OOM
that names neither culprit.

| | vLLM | llama.cpp |
|---|---|---|
| profile | `vllm` | `llamacpp` |
| weights | safetensors (fp16, AWQ, NVFP4, …) | GGUF |
| where the weights live | VRAM, entirely | VRAM + system RAM + mmap'd file |
| batching | much better | modest |
| downloads for you | yes, from HuggingFace | no — point it at files you have |
| use it when | the model fits in VRAM | it does not |

**vLLM is the default and usually the right answer.** Reach for llama.cpp when
a model will not fit — in particular a large mixture-of-experts model, where
`--n-cpu-moe` keeps the routed experts in system RAM and leaves only attention
and the shared expert on the GPU. That is what lets a 177B MoE serve from a
24 GB card; see [MODELS.md](MODELS.md#qwen38-flash-next-177b-moe--can-this-stack-run-it).

### What the choice changes

Only the engine. Everything above it is untouched: LiteLLM still issues
per-person keys and budgets, usage is still attributed per user, the dashboards
and Open WebUI are unchanged.

That works because `config/litellm/config.yaml` names no engine. It reads
`ENGINE_API_BASE`, `ENGINE_MODEL` and `ENGINE_API_KEY` through LiteLLM's
`os.environ/` indirection, and `setup.sh` writes those three from whichever
engine you picked:

| | `ENGINE_API_BASE` | `ENGINE_MODEL` |
|---|---|---|
| vllm | `http://vllm:8000/v1` | `openai/$VLLM_SERVED_MODEL_NAME` |
| llamacpp | `http://llamacpp:8080/v1` | `openai/$LLAMACPP_SERVED_MODEL_NAME` |

The `openai/` prefix is the **protocol**, not the vendor — it means "speak the
OpenAI API to that base URL". Nothing is sent to api.openai.com.

Leave `ENGINE_*` unset and the stack falls back to the vLLM wiring, which is
what lets an `.env` written before llama.cpp existed keep working.

### llama.cpp settings worth understanding

| key | |
|---|---|
| `LLAMACPP_MODEL_DIR` | host directory holding the GGUFs, mounted read-only at `/gguf`. **Put it on NVMe.** |
| `LLAMACPP_MODEL_FILE` | file name only. For a split GGUF name the **first** shard; the rest are found for you. |
| `LLAMACPP_N_CPU_MOE` | layers whose routed experts stay in system RAM. **The flag that decides whether a large MoE fits.** Raise it if the server dies with a CUDA OOM while loading; lower it for speed once it loads. |
| `LLAMACPP_CONTEXT` | KV cache is allocated up front from this — lower it first if startup fails. |
| `LLAMACPP_KV_TYPE` | `q8_0` roughly halves the KV cache against `f16` for little quality cost. |
| `LLAMACPP_PARALLEL` | request slots. Each gets `CONTEXT/PARALLEL` tokens, so raising it **shrinks** the per-request window. |
| `LLAMACPP_EXTRA_ARGS` | extra flags. **Do not put `-t <logical cpus>` here** — llama.cpp already defaults to the physical core count, and forcing all logical CPUs measured 2.5x *slower*. |

### Keeping RAM free for the system

`LLM_MEM_RESERVE_PCT` (default 10) becomes a hard `mem_limit` on the engine
container. Whether that costs throughput depends on one thing: **does the model
fit underneath the cap?**

Measured on a 24 GB GPU with a 50 GB VM and a 93.7 GB model -- which cannot be
fully cached at any setting:

| reserve | engine cap | warm decode |
|---|---|---|
| none | unlimited | **8.02 tok/s** |
| 10% | 39,025 MB | 5.46 tok/s |

A **32% loss**, because every GB taken from the page cache becomes disk I/O.
On a machine where the model *does* fit, the cap sits above the working set and
costs nothing measurable. `setup.sh` compares the two and says which case you
are in rather than applying a number quietly.

**On Windows this is usually redundant.** The WSL ceiling in `.wslconfig`
already reserves RAM for the host: 50 GB of a 63.7 GB machine leaves Windows
21%, comfortably past a 10% target. A second reservation inside the VM only
takes cache from the model. Tune `.wslconfig` there and leave
`LLAMACPP_MEM_LIMIT=0`.

**On Linux there is no VM layer**, so the container limit is the only lever and
the right one to use.

### What it costs, measured

A 177B MoE (`Qwen3.8-Flash-Next`, `UD-IQ4_XS`, 93.7 GB) on a 24 GB RTX 3090 with
48 GB of RAM: **185–202 s to load, 3.4–4.3 tok/s warm**, correct output. During
generation the CPU runs at ~800% and the GPU at 25–35% — the expert matmuls on
the CPU set the pace, so raising `--n-cpu-moe`'s GPU share (48 -> 38, VRAM
8.4 -> 20.5 GB) bought nothing measurable on this box. Tune it to make the model
*fit*, then stop.

llama.cpp downloads nothing. Fetch the GGUF yourself first — the multi-part
files are large enough that a resumable transfer matters — then point
`LLAMACPP_MODEL_DIR` at where it landed.

## What it decides for you

**vLLM's model runner.** On WSL2 or Windows it forces the V1 runner without
asking. The V2 runner allocates Unified Virtual Addressing buffers and WSL2's
GPU driver does not expose UVA, so the engine dies with `RuntimeError: UVA is
not available` — *after* the container has reported healthy once, which reads
like a hardware limitation rather than a setting. On bare-metal Linux it asks,
because there V2 is the faster path.

**The HuggingFace read timeout**, defaulted to 120s rather than the library's
10s. A first pull of a multi-GB checkpoint over a slow link spends most of its
time hitting that timeout and retrying, and each retry restarts the HTTP
request rather than the transfer, so throughput *decays*: measured here,
4.6 MB/s falling to 1.0 MB/s, and back to 2.4 MB/s once the timeout was
raised.

## After it finishes

Four things it deliberately leaves to you, because each needs a decision or a
credential:

**1. Trust the local CA**, or browsers warn on every page. This writes to the
system certificate store, which is not something a setup script should do
behind your back:

```powershell
.\scripts\gen-certs.ps1 -Trust      # Windows
sudo ./scripts/gen-certs.sh --trust # Linux/macOS
```

**2. Resolve the hostnames.** `*.localhost` resolves in browsers but **not**
in curl or SDK clients:

```bash
./scripts/setup-hosts.sh
```

**3. Wait for the model.** The first vLLM start downloads it, which can take
an hour on a slow link. **Do not recreate the container while it runs** —
HuggingFace writes to a fresh temp file per attempt, so a restart abandons the
partial download rather than resuming it. That mistake cost about 8 GB here.

**4. Check Open WebUI's connection** if the model list is empty. Open WebUI
stores its backend connection in its own database after first boot and ignores
the environment variable from then on, so an existing install keeps pointing
wherever it was first configured. Fix it under **Admin → Settings →
Connections**.

## Things that look broken and are not

Each of these cost real time here, and each presents as a fault somewhere other
than its cause.

**A cached failure that looks like a broken model.** LiteLLM caches responses in
Redis for an hour. Retry an agent task with the *same* prompt and you get the
same reply in **3 milliseconds** — including if that reply was a bad one. Three
identical "the model returned empty content" results here were one failure
served three times; varying the prompt produced working answers immediately.
Check `request_duration_ms` in `LiteLLM_SpendLogs`: single-digit values are
cache hits, not inference.

**Another project's Traefik stealing your routes.** Traefik's Docker provider
sees *every* container on the daemon, and router names are a global namespace.
An unrelated stack that names a router `authelia` — a very likely name —
silently replaces yours, and logins start redirecting to that project's domain.
Nothing errors, because nothing failed. `config/traefik/traefik.yml` now
constrains discovery to this compose project:

```yaml
constraints: "Label(`com.docker.compose.project`,`llmservice`)"
```

`exposedByDefault: false` does **not** protect against this: the other
container opted in for its own Traefik and yours cannot tell the difference.

**A port conflict with no culprit.** `docker compose up` reports only
`Bind for 0.0.0.0:80 failed: port is already allocated`. `setup.sh` now
preflights and names the container holding the port, and
`TRAEFIK_HTTP_PORT` / `TRAEFIK_HTTPS_PORT` move this stack out of the way.
URLs then carry the port: `https://chat.<domain>:8443`.

**Provisioning that cannot reach the gateway.** LiteLLM's port 4000 is exposed
to the compose network but never published to the host, so `llm-users.sh` used
to fail with "cannot reach the gateway at http://localhost:4000" — on the very
first thing anyone does after setup. It now derives the public URL from
`LLM_DOMAIN` and `TRAEFIK_HTTPS_PORT` and trusts the stack's own CA. Override
with `LITELLM_URL` for unusual topologies.


Each of these cost real time here, and each presents as a fault somewhere
other than its cause.

**A config edit that does nothing.** `config/litellm/config.yaml` is a mount,
not a build input, so the gateway keeps serving the old model list until
`docker compose restart litellm`. The symptom is an empty or stale
`/v1/models` with no error anywhere.

**`docker compose restart` on Open WebUI.** It came back `Exited (127)` here.
`docker compose up -d open-webui` recreates it cleanly; prefer that.

**A model list that is empty while the backend is reachable.** Open WebUI
stores its connection in its own database after first boot and ignores the
environment variable from then on. Fix it under **Admin → Settings →
Connections**, not in `.env`.

**Attribution checks failing on a working stack.** Responses are cached for an
hour, and a cached reply records no spend. Vary the prompt — see
[USAGE-LIMITS.md](USAGE-LIMITS.md).

**A download that gets slower.** Recreating the vLLM container mid-download
abandons the partial files rather than resuming them: HuggingFace writes to a
fresh temp name per attempt. That cost 8.6 GB here. Leave it alone and watch
`docker compose logs -f vllm`.

**Grafana rejecting the password that is in `.env`.** Grafana writes the admin
password into its own database on **first boot** and ignores
`GF_SECURITY_ADMIN_PASSWORD` from then on, exactly like Open WebUI and its
connection settings. Editing `.env` afterwards changes nothing and the login
keeps failing. Reset it to whatever `.env` says:

```bash
grep -E '^GRAFANA_ADMIN_PASSWORD=' .env | cut -d= -f2-   | docker compose exec -T grafana grafana-cli admin reset-admin-password --password-from-stdin
```

Pipe it rather than passing it as an argument -- argv is visible in the
container's process list. The login name is `GRAFANA_ADMIN_USER`, not `admin`.

**`.env` losing to your shell.** Docker Compose gives **shell environment
variables precedence over `.env`**. Measured here: `.env` said
`POSTGRES_USER=llmservice` while the stack ran as `myuser`, because `myuser`
was exported in the user's machine environment. Postgres had been initialised
under that name and only honours `POSTGRES_USER` on first init, so everything
worked -- until the day that variable is cleared, when Compose falls back to a
role the database does not have and LiteLLM and Grafana lose the database
together. Check what is really in force before trusting `.env`:

```bash
docker compose config | grep POSTGRES_USER
```

**Certificates that will not generate on Windows.** Git Bash rewrites
`-subj "/CN=..."` into a path. `scripts/setup.sh` exports `MSYS_NO_PATHCONV`,
but if you call `openssl` by hand, do the same or use the `.ps1`.

## When vLLM cannot give you the window you need

vLLM is the default engine and the better one under load. But the context it
can offer is bounded by what the weights leave over, and on a 24 GB card the
27B AWQ build leaves room for about 27K tokens — short of the 64K
[Hermes](HERMES.md) requires.

The lever is the **quantisation format, not the engine settings**: the same
model as a Q4_K_M GGUF is ~2 GB smaller and fits a 64K window on the same card.
GGUF is what the **llamacpp** engine reads. See [HERMES.md](HERMES.md) for the
measured numbers.

Switch engines by re-running setup and answering `llamacpp`, or unattended:

```bash
./scripts/setup.sh --defaults --set LLM_ENGINE=llamacpp   --set LLAMACPP_MODEL_DIR=/path/to/gguf   --set LLAMACPP_MODEL_FILE=your-model.gguf   --set LLAMACPP_CONTEXT=65536
```

Both engines claim the whole GPU, so **only one runs at a time** — the engine
profile is what enforces that, and `setup.sh` sets exactly one.

Two llama.cpp settings do the work here:

- `LLAMACPP_KV_TYPE=q8_0` quantises the **KV cache itself**, roughly halving it
  against `f16`. That is the other half of how a large window fits on a small
  card, and vLLM has no equivalent this cheap.
- `LLAMACPP_N_CPU_MOE` keeps a mixture-of-experts model's routed experts in
  system RAM. Irrelevant for a dense model; decisive for an MoE.

`scripts/e2e-check.py` and `scripts/health.sh` probe vLLM first and llama.cpp
second, and name which one answered — so they pass either way and tell you
which engine you are on.

## Adding people

The installer offers to provision everyone in
`config/authelia/team.yml` at the end, and you can re-run it any time:

```bash
./scripts/llm-users.sh            # show the plan
./scripts/llm-users.sh --apply    # create users, keys and ceilings
```

Each person gets an API key printed **once**, an internal-user record for
attribution, and an end-user record so their ceiling binds on the chat path
too. See [USAGE-LIMITS.md](USAGE-LIMITS.md) for why both are needed.

## Verifying

```bash
./scripts/health.sh
```

Then reach the stack at `https://chat.<domain>`. Usage per person is in
Grafana under **Usage by person**; GPU and host metrics are in the `gpu` and
`resources` dashboards.
