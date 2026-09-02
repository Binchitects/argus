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
