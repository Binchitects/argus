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
```

| flag | |
|---|---|
| `--domain D` | set `LLM_DOMAIN` without being asked |
| `--set K=V` | set any `.env` key; repeatable |

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

**Engine.** The HuggingFace model id, the name clients will use for it,
context length, and how much VRAM vLLM may claim. vLLM serves **one model at a
time** and claims that fraction of the card up front, so nothing else can load
beside it — a 24 GB card cannot hold an 8B and a 27B together.

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
model as a Q4_K_M GGUF is ~2 GB smaller and fits a 64K window on the same
card. That path runs through Ollama on the host, with `num_ctx` requested by
the gateway. See [HERMES.md](HERMES.md) for the measured numbers.

Ollama and vLLM both claim the whole GPU, so **only one runs at a time**:

```bash
docker compose stop vllm          # before starting Ollama
./scripts/start-ollama.sh         # NOT plain `ollama serve` -- see below
```

```powershell
.\scripts\start-ollama.ps1        # Windows
```

Use the script rather than `ollama serve`. It sets `OLLAMA_KV_CACHE_TYPE`,
which is server-level environment and cannot be requested per call — and the
window the gateway asks for (131,072) only fits with a `q4_0` cache. Started
any other way, Ollama gets an `f16` cache, accepts the same request, spills a
quarter of the model to system RAM and crawls. It does not error.

`scripts/e2e-check.py` probes vLLM first and Ollama second, and names which
one answered — so it passes either way and tells you which engine you are on.

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
