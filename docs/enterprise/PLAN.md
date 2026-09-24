# Enterprise solution: one app at `llm.<domain>`

Branch: `enterprise-solution`. When every phase below is done and green, the repo is
reorganised around the new app, the legacy services are deleted, and the branch
becomes `main`.

## Target

```
https://llm.<domain>        Traefik: /api, /connect, /.well-known → app; the rest → web
├── web  (React 19 + TypeScript, Radix + Tailwind; static files on nginx — one container)
│     chat · dashboards · usage & cost · admin · settings · Argus
└── app  (ASP.NET Core, .NET 10 LTS — one container, the API)
    ├── Identity: ASP.NET Identity + OpenIddict (own OIDC provider)
    │     local accounts + LDAP / Active Directory · TOTP 2FA · API keys
    ├── Chat: conversations, tools (Argus, sandbox, …), assistants, knowledge
    ├── Settings: every setting typed, validated, audited, stored in the database
    ├── Dashboards: panel queries run server-side (Postgres, Prometheus, Loki)
    ├── Argus: code index, MCP server, GitLab read-only sync, packs
    └── /v1 reverse proxy (YARP) → LiteLLM, authorised by the caller's key

model server (internal only): llama.cpp + LiteLLM   — unchanged
data:        Postgres (app schema + LiteLLM's)       — shared instance
telemetry:   Prometheus + Loki + exporters           — kept as plumbing, no UI
```

The model server stays a separate container on purpose: GPU, restarts and model
swaps must never take the app down, and vice versa.

## Decisions

| Topic | Decision |
|---|---|
| Sign-in | The app is its own OIDC provider (OpenIddict). Users are local accounts or come from LDAP / Active Directory (bind + group → role mapping). Other tools (the CLI, Qwen Code, IDEs) sign in through it. |
| Dashboards | All ten Grafana dashboards are rebuilt natively in the app. Grafana is removed in Phase 5. |
| Alerts | Prometheus rules stay; the app shows firing alerts and history. Alertmanager stays as plumbing. |
| Repo | Work happens under `app/` on this branch. The final reorganisation happens once everything is green. |
| Stack | .NET 10 LTS, EF Core + Npgsql, OpenIddict, YARP, xUnit + Testcontainers; React 19 + Vite + TypeScript, TanStack Query, ECharts, Vitest, Playwright. |
| Frontend | Rewritten from zero as its own container (`web`, in `app/frontend/`): Radix primitives + Tailwind with our own components (the shadcn/ui approach), light and dark themes, self-hosted fonts and icons (air-gapped installs), accessibility checked with axe in every browser test. The first UI (`app/web`, served by the API) stays at `llm.<domain>` until the new one matches it; meanwhile the new one runs at `next.<domain>`. |
| Settings | Everything is configurable in the app. A setting applies at once when its service can take it live; otherwise the app saves it and shows the one command to apply it (still no Docker socket). Phase 6 makes those live too. |

## How "fully tested" is enforced

1. **Parity tests stay alive the whole time.** `stack/scripts/functional-test.py`,
   `acceptance.py` and `audit-auth.sh` test over HTTP from the outside. Each phase is only done
   when they pass with the legacy component **switched off**.
2. **New tests per layer:** xUnit (unit) · xUnit + Testcontainers with real Postgres,
   OpenLDAP and LiteLLM (integration) · Vitest (components) · Playwright (end-to-end in a browser).
3. **Comparison tests** wherever something is ported: the same input goes to legacy and new,
   and the outputs must match (dashboard panel values, costs, Argus answers).
4. **CI** runs all of it on every push to this branch. Every finished phase gets a tag
   (`enterprise-p0`, `enterprise-p1`, …).
5. Every phase must deploy from zero (`docker compose up` on empty volumes).

## Phases

Status: **Phase 3A done** (the new web's foundation, at `next.<domain>`). Next: 3B. The first chat UI
and its [checklist](PHASE3-CHECKLIST.md) are superseded: sign-off happens on the new web
at the end of 3F, then Open WebUI goes and `enterprise-p3` is tagged.

### Phase 0 — Foundations  *(S)*
- Solution skeleton: `Llm.Api`, `Llm.Core`, test projects, `web/` (React).
- Multi-stage Dockerfile, `app` service in compose, Traefik route for the bare domain
  (the legacy subdomains keep working next to it).
- The app creates its own `llmapp` database and runs EF migrations at startup.
- Health endpoints (`/healthz` live, `/readyz` including the database), `/api/info`.
- React shell with navigation for the areas to come.
- CI workflow for this branch.
- **Done when:** a clean deploy serves the app, all new tests pass, and all legacy tests
  still pass.

### Phase 1 — Identity  *(M)*
Replaces: Authelia, `auth-init`, `identity-proxy`, the people / keys / password pages of
the admin panel.
- Local users, roles (admin, member), groups, TOTP 2FA, password change and reset.
- LDAP / AD: bind authentication, just-in-time user creation, group → role mapping,
  periodic sync that disables people who left.
- OIDC provider (authorization code + PKCE, refresh tokens, client credentials for services).
- API keys: issued as LiteLLM virtual keys through LiteLLM's admin API, so spend keeps
  being tracked per person.
- Rate limits, lockout, audit log of sign-ins and admin actions.
- **Done when:** the auth audit and the functional test's person and admin checks pass with
  Authelia off; LDAP is tested against a real OpenLDAP container.
- **Result:** Authelia and its secrets are gone; the app imports an Authelia install's people
  once, with their passwords. `identity-proxy` stays for now: it maps the chat user onto
  LiteLLM's budget field, which is a gateway concern and moves with the chat in phase 3.
  Tests: 87 backend (real Postgres and OpenLDAP), 13 UI, 20 browser; the live stack and a
  from-zero deploy both pass functional 54/54, acceptance 33/0, the auth audit and the
  domain check.

### Phase 2 — Admin, usage and cost  *(M)*
Replaces: the rest of the admin panel and the "Usage by person" dashboard.
- Model page (switch steps only — still no Docker socket), price settings,
  people and budgets.
- Usage and cost: cache hit / cache miss / output tokens, cost, per person, over time.
- The dashboard engine: panel definitions are imported from the Grafana JSON; queries run
  only on the server (the browser never sends raw SQL or PromQL).
- **Done when:** a comparison test shows every usage panel matches the Grafana panel on the
  same data, and the admin-panel container is gone.
- **Result:** the admin panel is gone (its address redirects page for page). The dashboard
  engine runs the SQL panels of the provisioned files itself; `compare-dashboards.py` shows
  34/34 panels identical to Grafana over 1, 7 and 30 days. Prices stay in `.env` (shown in
  the app, edited there): LiteLLM reads them from its config at start, and editing them in
  the app would need the same restart the Model page already describes. Everyone gets their
  own usage page. Tests: 132 backend, 25 UI, 38 browser; the live stack and a from-zero
  deploy pass functional 56/56, acceptance 33/0, the auth audit and the domain check.

### Phase 3 — Chat  *(L)*
Replaces: Open WebUI (it runs at `chat.<domain>` until this phase is accepted).
1. Streaming, markdown and code, conversation history.
2. Model and thinking-level choice, stop and regenerate.
3. Argus as a tool (first through the current Python Argus over MCP), with the
   "ask a maintainer" notices.
4. File upload.
- **Done when:** Playwright covers chat flows and permissions, you sign off the
  feature checklist, and Open WebUI is removed.
- **Result so far:** the chat is in the app ([CHAT.md](../stack/CHAT.md)). It
  has streaming, thinking levels, stop and regenerate, attachments (text and
  PDF), and searchable history. Argus is called over MCP as the person asking,
  and the no-access notice names the repository and its maintainers.
  - The chat bills through its own gateway key, aliased `chat`, to the person.
    Their credit now really binds: LiteLLM ignores `max_budget` on
    `/end_user/update`. Open WebUI had the same gap.
  - Chats are private to their owner, admins included.
  - Tests: 153 backend, 32 UI, 52 browser (desktop and phone, real model).
    The browser tests include the no-access case against the test GitLab.
  - On the live stack: functional 61/61, acceptance 33/0, auth audit, domain
    check, and dashboards 34/34.
  - Open WebUI stays at `chat.<domain>` until the checklist is signed.
  - A from-zero deploy (sample `.env`, generated secrets, empty volumes) passes the
    same suites: functional 61/61, acceptance 33/0, auth audit, domain check,
    dashboards 34/34, browser 50/50.

Phases 3A–3F rebuild the web from zero as its own container and extend the chat.
Each is deployed at `next.<domain>` and tested before the next starts.

### Phase 3A — The new web: container, design system, shell  *(M)*
- `app/frontend/`: Vite + React 19 + TypeScript, built into its own image (Alpine +
  nginx, non-root, the same security headers as the API, immutable `/assets`, SPA
  fallback, health check). Traefik serves it at `next.<domain>`, with `/api`,
  `/connect` and `/.well-known` going to the app.
- Design system: colour, type, spacing and radius tokens; light, dark and system
  themes; components on Radix (button, inputs, select, combobox, checkbox, switch,
  dialog, sheet, menus, popover, tooltip, tabs, toasts, badge, card, avatar,
  skeleton, empty state, data table on TanStack Table, command palette, forms on
  react-hook-form + zod); Lucide icons; the Inter font, bundled.
- Shell: collapsible sidebar by role, breadcrumbs, command palette (Ctrl/⌘ K), user
  menu (theme, account, sign out), phone layout, error and not-found pages, session
  expiry handled.
- Pages: sign-in (password, 2FA, directory), account (profile, password, 2FA, API key),
  home.
- **Done when:** those pages work at `next.<domain>`; Vitest, Playwright (desktop and
  phone, both themes) and axe (no serious or critical violations) pass; CI builds and
  tests the new image.
- **Result:** `app/frontend` is built into the `web` image and served at `next.<domain>`.
  - **The container:** Alpine's own nginx package (Docker Hub is unreachable here),
    non-root, read-only root, the API's CSP. Fingerprinted assets are cached for a
    year; a 404 never is.
  - **The design system:** 26 component files (most on Radix), a `/design` gallery, light, dark and
    system themes (applied before the first paint), bundled fonts and icons. Text
    colours on tints were computed to 4.5:1 or better.
  - **Pages:** the shell (sidebar, command palette ranked by title over keywords,
    breadcrumbs, a phone drawer, session expiry), sign-in with 2FA, the account page,
    and home. Pages not rebuilt yet say which phase brings them and open in the
    current UI.
  - **Load:** the first load is about 187 KB gzipped, in three cacheable parts; the form
    libraries load only on the sign-in and account pages.
  - **Found on the way:**
    - The API logged two errors whenever a browser left during a gateway 502 (the
      error handler wrote to the closed connection). It no longer does, and it is
      tested.
    - Sign-out didn't return to the sign-in page, because clearing the cache
      dropped the query the shell watched. Fixed and tested.
  - **Tests:** 29 UI and 42 browser (desktop and phone; axe on five pages in both
    themes), 156 backend. They pass on the live stack and in a rehearsal of the new
    CI job (the web and app images behind Traefik, no gateway). The stack suites
    still pass: functional 61/61, auth audit, domain check, dashboards 34/34. So
    does the current UI's browser suite (50, plus 4 skipped).

### Phase 3B — Admin, usage and settings  *(L)*
- Every admin page rebuilt: overview, people (table with search, filters, bulk
  actions, a detail panel), audit log (filters, export), sign-in and directory, model,
  indexing, packs, explore, monitoring. Usage (everyone and mine) on the chart
  components, keeping the validated palette and the table view.
- **Settings registry** in the API: every setting has a name, type, default,
  validation, description, group, whether it is secret, and how it applies (live, or
  restart of which service). Values live in the database; `.env` gives the defaults
  and the bootstrap. Secrets are write-only. Every change is audited.
- Settings page: grouped and searchable, validated forms, "changed from default",
  history, and for restart-bound settings a "pending" banner with the exact command.
  Live from the start: credit defaults, sign-in and session policy, the directory
  (LDAP), thinking presets, chat limits, tools, branding (name, logo, accent colour).
- **Done when:** the old admin and usage browser tests pass against the new pages,
  dashboards stay 34/34, and each setting is tested from save to effect.

### Phase 3C — Chat, the rich core  *(L)*
- Layout: chat list, thread, composer, and a **Files** panel like Claude's: every
  attachment and every file written in the chat, with a viewer (highlighting, copy,
  download).
- Model picker (from the gateway, with what each model can do: vision, tools,
  thinking, context), thinking level, and per-chat system prompt and parameters.
- Thinking shown live with its duration and tokens, then folded.
- Tool calls as cards: arguments, status, time, output; Argus results rendered for
  what they are (symbols, files and line ranges linking to GitLab, highlighted
  excerpts) and the no-access notice.
- Code blocks: language, copy, wrap, line numbers, collapse when long, download, open
  in the Files panel. Markdown with tables and maths (KaTeX, bundled).
- Attachments: drag and drop, paste, several at once, progress; images with
  thumbnails and a full-size preview, sent to models that can see; PDFs and text
  previewed.
- Edit a sent message (a new branch, with arrows between branches), retry with another
  model or thinking level, stop, copy, tokens and cost per answer.
- **Done when:** the chat's browser tests pass on the new web against the real model
  and the test GitLab; then `llm.<domain>` switches to the new web and `app/web` is
  deleted.

### Phase 3D — Tools  *(L)*
- A tool registry in the API (built-in tools and MCP servers); admins choose which
  exist and who may use them; a tool picker per chat; "ask before running" per tool.
- Built-in: Argus, calculator, date and time, reading attached files in parts, a
  **Python sandbox** (its own container: no network, read-only root, CPU, memory and
  time limits; files it writes appear in the Files panel), and optional web fetch and
  search (off by default; allow-list; air-gapped installs leave it off).
- **Done when:** each tool is tested end to end, and the sandbox has escape tests
  (network, filesystem, time and memory limits).

### Phase 3E — Assistants and knowledge  *(L)*
- Assistants: a name, icon, instructions, model, thinking level, tools and knowledge;
  private, shared with groups, or with everyone.
- Knowledge bases: documents chunked and embedded once (pgvector), searched as a tool,
  with citations back to the document and page; access per base.
- **Done when:** a retrieval eval on a fixture corpus meets its bar, and access rules
  are tested.

### Phase 3F — Organise and share  *(M)*
- Folders, pins, tags, archive and bulk actions; read-only share links inside the
  organisation (revocable); export as Markdown or JSON.
- The sign-off checklist rewritten for the new web.
- **Done when:** you sign it off; then Open WebUI is removed and `enterprise-p3` is
  tagged.

### Phase 4 — Argus in .NET  *(XL)*
Replaces: the Python Argus (about 47k lines including tests).
- **4a** MCP server and tools, reading the existing index. Comparison test: the same
  questions go to Python and .NET and must give the same answers.
- **4b** Indexer worker: GitLab sync with a read-only token, permission checks,
  embeddings (pgvector).
- **4c** Knowledge packs (build and verify) and the CLI as a .NET tool.
- The Python test cases are ported one by one, and the evals must not get worse.
- **Done when:** the Python Argus container is gone and the comparison and eval results
  are unchanged.

### Phase 5 — All dashboards and alerts  *(M)*
Replaces: Grafana.
- The remaining nine dashboards (LLM overview, stack performance, health and alerts,
  resources, GPU, host and containers, logs, Argus, indexing) rebuilt natively.
- Live log viewer on Loki; firing alerts and history from Alertmanager.
- **Done when:** the panel audit (every panel query run on legacy and new) matches, and
  Grafana is removed.

### Phase 6 — Every setting live  *(M)*
Makes the settings that still need "run this command" apply from the app, without
giving any web container the Docker socket.
- Prices and model routes through LiteLLM's database-backed model API.
- The engine: a small supervisor inside the engine container restarts llama.cpp with
  new arguments or a new model when the app asks over the internal network
  (authenticated, validated, audited). llama.cpp's router mode is evaluated for
  switching models without a restart.
- What is left (compose profiles, host limits) is listed with its reason.
- **Done when:** changing each setting in the app takes effect without a shell, and a
  test proves it for each.

### Phase 7 — Cutover  *(S)*
- Delete the legacy services and their config; reorganise the repo around the app.
- Rewrite the README and docs; one `.env` sample per hardware setup still works.
- Full from-zero deploy plus every test suite; tag a major release; merge to `main`.
