# Development

```
dotnet/            the backend (.NET 10)
  src/Argus/         Platform/ (people, chat, API), Server/ (HTTP, MCP, jobs),
                     Indexing/, Store/, Packs/, Access/, Cli/, Migrations/
  tests/Argus.Tests  xUnit
  tests/fixtures     a ctags corpus and documentation fixtures
frontend/          the React app (Vite, TypeScript)
  src/               pages/, components/, api.ts
  e2e/               Playwright, with fake GitLab, LiteLLM and embeddings
stack/             docker-compose, configuration, env samples, scripts
clients/           editor and agent configurations
Dockerfile         builds the frontend, tests and publishes the backend, one image
```

## Backend

Needs the .NET 10 SDK, git and Universal Ctags.

```bash
cd dotnet
scripts/fetch-sqlite-vec.sh linux-x64    # once: the pinned sqlite-vec, SHA-256 checked
dotnet build -c Release
dotnet test -c Release
```

Run it against a local config:

```bash
ARGUS_ADMIN_EMAIL=you@example.com ARGUS_ADMIN_PASSWORD=a-long-password \
ARGUS_GATEWAY_URL=http://localhost:4000 LITELLM_MASTER_KEY=sk-... \
ARGUS_EMBED_URL=http://localhost:8081 \
  dotnet run --project src/Argus -c Release -- serve --config config.yaml --port 7700
```

## Frontend

Needs Node 22.

```bash
cd frontend
npm ci
npm run dev        # http://localhost:5173, proxying /api, /admin and /mcp to :7700
npm run build      # typecheck and build dist/, which the backend serves
```

## Browser tests

The suite starts the real backend with fake GitLab (REST and git over HTTP),
LiteLLM (people, keys, budgets, a streaming model that calls tools) and
embeddings on real sockets, then drives the built app in Chromium: sign-in,
people, indexing, explore, packs, chat with tool calls as the signed-in person,
access limits, stop, keys used over MCP, disabled accounts, spent budgets.

```bash
(cd dotnet && dotnet build -c Release)
cd frontend && npm run build && npx playwright test
```

`ARGUS_BIN` and `ARGUS_WEB_ROOT` point it at another build. Screenshots of
every page land in `frontend/e2e/.work/screens/`.

## The image

```bash
docker build --target server -t argus .
```

The build runs the xUnit suite inside the image and fails if it fails; the
runtime image carries the receipt at `/usr/share/argus/build-verified`.

## Conventions

- Tool names, descriptions and schemas in `Server/ToolCatalog.cs` are text a
  model reads on every request; change them deliberately.
- Tool results are written byte-for-byte in a fixed shape (`Util/PyJson.cs`,
  `Util/PyStr.cs`), because a model reads them and a changed byte is a changed
  prompt.
- Changing what ctags extracts means bumping `Ctags.SymbolContractVersion`, so
  existing indexes re-extract.
