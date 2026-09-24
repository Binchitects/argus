# App

The all-in-one app at `https://<LLM_DOMAIN>`: ASP.NET Core (.NET 10) backend serving a
React frontend. Plan and phases: [docs/enterprise/PLAN.md](../docs/enterprise/PLAN.md).

```
app/
  src/Llm.Api      web host: endpoints, health, security headers, serves the UI
  src/Llm.Core     domain and data (EF Core, Postgres)
  tests/Llm.Tests  xUnit; integration tests run a real Postgres via Testcontainers
  web/             the first UI, served by the API; replaced by frontend/ (plan 3C)
  frontend/        the new web, its own image (Alpine + nginx); see frontend/README.md
  Dockerfile       UI build -> API publish -> chiseled runtime (non-root, no shell)
  dn               runs the .NET SDK in Docker, so nothing needs installing
```

## Run with the stack

It is the `app` service in `stack/docker-compose.yml` (profile `gateway`), so
`docker compose up -d` in `stack/` builds and starts it. It creates its own `llmapp`
database on the shared Postgres and applies migrations at startup.

## Develop

```bash
./dn test                       # backend: build + all tests (needs Docker)
cd web && npm ci && npm test    # frontend unit tests
npm run typecheck && npm run lint && npm run build
```

Local UI development: run the API (`./dn run --project src/Llm.Api` with
`ConnectionStrings__App` set) and `npm run dev` in `web/`; Vite forwards `/api` to it.

New migration:

```bash
./dn tool restore && ./dn tool run dotnet-ef migrations add <Name> -p src/Llm.Core -s src/Llm.Api -o Data/Migrations
```

## End-to-end tests

```bash
cd web
# against the deployed stack (E2E_PASSWORD = ADMIN_PASSWORD in .env):
E2E_BASE_URL=https://llm.localhost E2E_PASSWORD=<admin password> npm run e2e
# against a bare app container, as CI does (.github/workflows/app.yml): the app
# serves HTTPS itself with a throwaway certificate, because sessions use Secure cookies
E2E_BASE_URL=https://llm.localhost:8443 E2E_PASSWORD=<its Auth__AdminPassword> npm run e2e
```

`npx playwright install chromium` fetches the test browser. Where that download is
blocked, add `E2E_CHANNEL=chrome` to use the installed Google Chrome instead.

The legacy suites in `stack/scripts/` (`functional-test.py`, `acceptance.py`,
`audit-auth.sh`) must keep passing in every phase.
