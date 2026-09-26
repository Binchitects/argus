# Development

How the repository is laid out, how to build and test each part, and the
conventions that keep it working. Operating a deployment is in the
[README](../README.md) and [admin.md](admin.md); what each test layer proves is
in [testing.md](testing.md).

## Layout

```
LlmService.slnx         every .NET project; Directory.Build.props and global.json beside it
src/
  Llm.Api/              the platform's API: sign-in, OIDC, chat, tools, admin, dashboards (Dockerfile)
  Llm.Core/             its domain and data (EF Core, Postgres)
  Argus/                the code index service: indexer, knowledge packs, MCP, CLI (Dockerfile)
  web/                  the platform's web: React, its own image (nginx)
  argus-web/            Argus's own web, for Argus alone; built into the Argus image
tests/
  Llm.Tests/            xUnit; real Postgres, OpenLDAP and LiteLLM's schema via Testcontainers
  Argus.Tests/          xUnit
  fixtures/argus/       a ctags corpus and documentation fixtures for Argus's tests
  deploy/               pytest, for the deployment's tooling (preflight, presets, acceptance)
deploy/                 the platform: docker-compose, config, env samples, scripts
  services/             what compose builds or mounts: sandbox, identity proxy, engine router...
  argus-standalone/     Argus alone: its own compose, config and scripts
tools/                  dn, fetch-sqlite-vec.sh, build-packs.sh, the test GitLab, Hermes add-ons
clients/                editor and agent configurations (MCP)
evals/                  evaluation question sets and results
docs/                   this documentation; docs/plan.md is the plan and its phases
```

## .NET

The API and Argus are one solution, `LlmService.slnx`, on .NET 10. Without the
SDK installed, `tools/dn` runs it in Docker with the repository mounted, and
Testcontainers can reach the databases it starts:

```bash
tools/dn build LlmService.slnx -c Release
tools/dn test tests/Llm.Tests -c Release       # needs Docker (Testcontainers)
tools/dn test tests/Argus.Tests -c Release
```

Argus's tests need Universal Ctags (it decides which symbols exist) and the
pinned sqlite-vec, fetched once and checked against its SHA-256:

```bash
tools/fetch-sqlite-vec.sh linux-x64            # into native/, which the Argus project copies
```

Warnings are errors everywhere. The platform's projects add the recommended
analyzers (`AnalysisLevel`); Argus runs with invariant globalization.

A new migration for the platform:

```bash
tools/dn tool restore
tools/dn tool run dotnet-ef migrations add <Name> -p src/Llm.Core -s src/Llm.Api -o Data/Migrations
```

Argus against a local config:

```bash
ARGUS_ADMIN_EMAIL=you@example.com ARGUS_ADMIN_PASSWORD=a-long-password \
ARGUS_GATEWAY_URL=http://localhost:4000 LITELLM_MASTER_KEY=sk-... \
ARGUS_EMBED_URL=http://localhost:8081 \
  dotnet run --project src/Argus -c Release -- serve --config config.yaml --port 7700
```

## The web apps

Both need Node 22.

```bash
cd src/web
npm ci
npm run typecheck && npm run lint && npm test && npm run build
```

Local UI work on the platform's web is described in
[src/web/README.md](../src/web/README.md). Argus's web proxies `/api`, `/admin`
and `/mcp` to a backend on `:7700`:

```bash
cd src/argus-web && npm ci && npm run dev      # http://localhost:5173
```

## Browser tests

**The platform** (`src/web/e2e`), against a running deployment, on desktop and
phone, in both themes, with axe accessibility checks on every page:

```bash
cd src/web
E2E_PASSWORD=<ADMIN_PASSWORD from deploy/.env> npm run e2e
E2E_CHAT=1 E2E_PASSWORD=... npm run e2e        # adds the chat against the real model
```

`npx playwright install chromium` fetches the test browser; where that is
blocked, `E2E_CHANNEL=chrome` uses the installed Google Chrome. The chat tests
run one after another: every browser signs in as the same admin, and fair use
gives one person one answer at a time.

**Argus** (`src/argus-web/e2e`): the real backend with fake GitLab (REST and git
over HTTP), LiteLLM and embeddings on real sockets, driving the built app in
Chromium:

```bash
dotnet build src/Argus -c Release
cd src/argus-web && npm run build && npx playwright test
```

`ARGUS_BIN` and `ARGUS_WEB_ROOT` point it at another build; screenshots land in
`src/argus-web/e2e/.work/screens/`.

## The deployment's own tests

Against the running platform, from `deploy/` (each described in
[testing.md](testing.md)):

```bash
python3 scripts/functional-test.py      # what a person does, as two people
python3 scripts/acceptance.py           # the wiring
./scripts/audit-auth.sh                 # every OIDC client, for real
./scripts/domain-check.sh               # routes and certificates
python3 scripts/sandbox-check.py        # the sandbox's isolation
python3 scripts/audit-dashboards.py 6h  # every dashboard panel's queries
```

`pytest tests/deploy` checks the tooling itself.

## Images

Both .NET images build from the repository root:

```bash
docker build -f src/Llm.Api/Dockerfile -t llmservice-app .
docker build -f src/Argus/Dockerfile --target server -t argus .
docker build -t llmservice-web src/web
```

The Argus image runs its xUnit suite during the build and fails with it; the
runtime image carries the receipt at `/usr/share/argus/build-verified`.
`docker compose build` in `deploy/` builds everything the platform runs.

## CI

`.github/workflows/ci.yml` runs on every push: the API and Argus tests, both web
apps, each in a browser (the platform's images behind Traefik with no model,
Argus with its fakes), the deployment tooling's tests, and every env sample
resolving into a complete compose file. `release.yml` publishes the Argus image
on a `v*` tag.

## Conventions

- **Commits** say what changed and why, in plain words; one concern per commit.
- **Secrets never enter the repository.** `.env` holds them, and the env samples
  only say how to make each one (`openssl rand -hex 32`). Nothing prints them.
- **No Docker socket in a web container.** Changes that recreate containers stay
  one host command (`deploy/scripts/apply-settings.sh`).
- **Argus's tool names, descriptions and schemas** (`src/Argus/Server/ToolCatalog.cs`)
  are text a model reads on every request; change them deliberately.
- **Argus's tool results** are written byte for byte in a fixed shape
  (`Util/PyJson.cs`, `Util/PyStr.cs`): a model reads them, and a changed byte is
  a changed prompt.
- **Changing what ctags extracts** means bumping `Ctags.SymbolContractVersion`,
  so existing indexes re-extract.
- The outside-in suites in `deploy/scripts/` keep passing in every change: they
  are how a deployment is judged, not the unit tests.
