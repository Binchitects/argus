# App

The app at `https://<LLM_DOMAIN>`: an ASP.NET Core (.NET 10) API and a React web,
two images behind one address. Plan and phases: [docs/enterprise/PLAN.md](../docs/enterprise/PLAN.md).

```
app/
  src/Llm.Api      the API: endpoints, health, security headers (no pages)
  src/Llm.Core     domain and data (EF Core, Postgres)
  tests/Llm.Tests  xUnit; integration tests run a real Postgres via Testcontainers
  frontend/        the web, its own image (Alpine + nginx); see frontend/README.md
  Dockerfile       the API: publish -> chiseled runtime (non-root, no shell)
  dn               runs the .NET SDK in Docker, so nothing needs installing
```

## Run with the stack

It is the `app` service in `stack/docker-compose.yml` (profile `gateway`), so
`docker compose up -d` in `stack/` builds and starts it. It creates its own `llmapp`
database on the shared Postgres and applies migrations at startup.

## Develop

```bash
./dn test                            # backend: build + all tests (needs Docker)
cd frontend && npm ci && npm test    # the web's unit tests
npm run typecheck && npm run lint && npm run build
```

Local UI development: see [frontend/README.md](frontend/README.md).

New migration:

```bash
./dn tool restore && ./dn tool run dotnet-ef migrations add <Name> -p src/Llm.Core -s src/Llm.Api -o Data/Migrations
```

## End-to-end tests

```bash
cd frontend
# against the deployed stack (E2E_PASSWORD = ADMIN_PASSWORD in .env);
# E2E_CHAT=1 adds the chat against the real model
E2E_PASSWORD=<admin password> npm run e2e
```

CI (`.github/workflows/app.yml`) runs the same suite against the two images
behind Traefik, with no model gateway (`E2E_NO_GATEWAY=1`).

`npx playwright install chromium` fetches the test browser. Where that download is
blocked, add `E2E_CHANNEL=chrome` to use the installed Google Chrome instead.

The legacy suites in `stack/scripts/` (`functional-test.py`, `acceptance.py`,
`audit-auth.sh`) must keep passing in every phase.
