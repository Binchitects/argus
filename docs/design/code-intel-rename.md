# Names people see, neutral names inside (v8.0)

The product and its code-intelligence service were both called "Argus". From v8.0 the names people see are
set at build time (`branding.env`: `PRODUCT_NAME`, `CODE_INTEL_NAME`; Farzan Arena and Farzan Graph by
default) and appear only in text people read. Nothing inside (code, deployment, data) carries a brand.

## The rule

- Text people read (UI, messages, docs, help, notifications, MCP titles): `Brand.Product` / `Brand.CodeIntel`
  in .NET, `productName` / `codeIntelName` (`src/web/src/lib/brand.ts`) in the web; the run-time product name
  (Settings → Branding, `/api/info`) where it is known. Never a name spelled out.
- Inside: the product is `arena` (it already is: the compose project, the images, `arenactl`); the service is
  `code-intel` (lowercase identifiers, files, services, URLs, MCP name, features), `CodeIntel` (C#), `CODE_INTEL_`
  (environment), "code intelligence" in prose where no name is wanted.

## Mapping

| Was | Is |
|---|---|
| "Argus Arena" (text) | the product's name (build time, or Settings → Branding) |
| "Argus" (text, the service) | the code-intelligence service's name (build time) |
| `src/Argus`, namespace `Argus.*`, `Argus.csproj` | `src/CodeIntel`, `CodeIntel.*`, `CodeIntel.csproj` |
| `tests/Argus.Tests`, `tests/fixtures/argus` | `tests/CodeIntel.Tests`, `tests/fixtures/code-intel` |
| `src/argus-web` | `src/code-intel-web` |
| `deploy/argus-standalone`, `docs/argus` | `deploy/code-intel-standalone`, `docs/code-intel` |
| `deploy/helm/argus-arena`, `argus-arena-V-offline.run` | `deploy/helm/arena`, `arena-V-offline.run` |
| the service's binary `argus` | `code-intel` |
| compose service and container `argus`, image `arena-argus` | `code-intel`, `arena-code-intel` |
| feature and profile `argus` | `code-intel` |
| `$DATA_DIR/argus`, `/var/lib/argus` | `$DATA_DIR/code-intel`, `/var/lib/code-intel` |
| `ARGUS_*` (service), `ARGUS_KEY`, `ARGUS_PACK_LIBRARY_DIR` | `CODE_INTEL_*`, `CODE_INTEL_KEY`, `CODE_INTEL_PACK_LIBRARY_DIR` |
| app config section `Argus` (`Argus__*`) | `CodeIntel` (`CodeIntel__*`) |
| `argus.DOMAIN` | `CODE_INTEL_HOST.DOMAIN`, `code-intel` by default |
| MCP server `argus` (people's tools) | `code-intel` |
| chat tool id `argus`, `useArgus` | `code-intel`, `useCodeIntel` |
| permissions `argus.read`, `argus.manage`, `argus.explore` | `code-intel.read`, `code-intel.manage`, `code-intel.explore` |
| audit actions `argus.*` | `code-intel.*` (old rows keep what they were called) |
| metrics `argus_*`, dashboard `argus.json` | `code_intel_*`, `code-intel.json` |

## What an upgrade from 7.x carries over

The installer's upgrade, and its rollback the other way:

- `.env`: `ARGUS_KEY` → `CODE_INTEL_KEY`, `ARGUS_PACK_LIBRARY_DIR` → `CODE_INTEL_PACK_LIBRARY_DIR`;
  `COMPOSE_PROFILES` and the installer's features: `argus` → `code-intel`; `CODE_INTEL_HOST=argus`, so the
  address people's DNS, certificates and tools know stays.
- `$DATA_DIR/argus` → `$DATA_DIR/code-intel` (a rename on the same disk); the old container and image removed.
- The database (an EF migration): the tool id `argus` in conversations, assistants, scheduled tasks, Agent
  Studio workflows and tool settings; the permissions in roles and access rules; storage history's key.

People's own tool configs (Claude Code, Qwen Code, Cursor…) are not carried: they add the server again from
Connect your tools (no aliases, by design).

## Phases (branch v8.0)

1. Names from the build: `branding.env`, `Brand`, `brand.ts`. Done.
2. The service's own project: folders, namespaces, binary, its environment, its data folder, its web.
3. The deployment: compose, features, installer (with the upgrade above), chart, bundle, scripts, tools.
4. The app: code, the database migration, the API's fields, permissions, tool id, texts by `Brand`.
5. The web: texts by `brand.ts`, fields, the sidebar's group, Connect your tools.
6. Docs, clients, fixtures; a search for any name left; the full tests and the offline upgrade from 7.10.1.
