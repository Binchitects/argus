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
| Frontend | Rewritten from zero as its own container (`web`, in `app/frontend/`): Radix primitives + Tailwind with our own components (the shadcn/ui approach), light and dark themes, self-hosted fonts and icons (air-gapped installs), accessibility checked with axe in every browser test. It replaced the first UI (`app/web`, served by the API) at `llm.<domain>` in 3C; the API now serves no pages. |
| Decision models | Jev and Laya ("System One" models that return typed probabilities, not text; September 2026) were evaluated and **skipped for now**. Jev is hosted only, so data would leave the network. Laya is open, but zero-shot it scores below the majority baseline on its authors' benchmark and needs labelled data and calibration per task. It does not improve the chat. Worth revisiting when a team has a classification task with labelled data. |
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

Status: **Phase 3D, part 1 done** (groups, the tool registry, image generation,
MCP servers, ask before running). Next: 3D.2 (models at runtime, pulled forward
from phase 6), then 3D part 2 (sandbox, web). The first chat UI and its
[checklist](PHASE3-CHECKLIST.md) are superseded: sign-off happens on the new web
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
Each is deployed and tested before the next starts. Until 3C the new web ran
at `next.<domain>`, which now redirects to `llm.<domain>`.

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
- **Result:** every admin page is rebuilt, along with usage (the dashboard engine's
  panels on the new charts). The Settings page edits 78 settings in 11 groups
  ([SETTINGS.md](../stack/SETTINGS.md)).
  - **How settings are stored:** the app's own settings are saved in the database
    as a configuration source that wins over the environment; secrets are
    AES-GCM encrypted under `APP_DATA_KEY`.
  - **At once:** the directory (with a connection test on unsaved values), chat
    limits, sign-in lockouts and branding.
  - **After a restart:** session lifetimes. The app restarts itself (no Docker
    socket).
  - **`.env` values:** saved as pending and applied by
    `scripts/apply-settings.sh`. The script accepts only names compose passes to
    the app, re-checks values, shows the diff (never a secret), asks, and keeps a
    backup.
  - **Model page:** switches to a shipped deployment in one click plus that one
    command.
  - **Found on the way:**
    - The engine's `--n-cpu-moe` default (48) disagreed with the memory
      planner's (0); both are now 0.
    - Chat uploads over 30 MB failed in Kestrel whatever the limit said; the
      endpoint now raises the limit to the setting.
  - **Tests:**
    - 173 backend, including the directory set up in the page against a real
      OpenLDAP and used without a restart, and compose kept in step with the
      catalog.
    - 57 UI and 98 browser (axe on every admin page in both themes, no page
      overflow on a phone, a person from added to deleted, a live setting and a
      pending one).
    - The browser suite also passes in a rehearsal of the CI job, without a
      gateway.

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
- **Result:** the chat is rebuilt ([CHAT.md](../stack/CHAT.md)), and the new web
  is the app.
  - **Backend:**
    - A conversation is a tree: edits and answering again make branches, and
      the model reads the branch on screen.
    - Models, with what each can do and its prices, come from the gateway.
    - Each chat has its own model, thinking level, instructions, temperature,
      top-p and longest answer.
    - Answering again can use another model or thinking level.
    - Images are told by their bytes (never SVG) and sent to models that can
      see.
    - Answers record how long the model thought and took; tool calls record
      their time.
    - A migration made each of the 193 existing chats on this host one branch.
  - **The page:**
    - Model and thinking pickers, and thinking shown live then folded.
    - Tool cards. Argus's answers show as places in the code, linking to the
      line range in GitLab, with highlighted code and marked matches.
    - Code blocks with language, copy, wrap, download, line numbers and
      folding; Markdown with tables and KaTeX.
    - A Files panel with attachments, files Argus read and code the model
      wrote.
    - Attachments by button, paste or drop, with progress.
    - An image viewer (fit or actual size, arrows between images).
    - Version arrows on edited questions and repeated answers; tokens and
      cost under each answer.
  - **The switchover:**
    - Traefik sends `llm.<domain>` to the web, and `/api`, `/connect` and
      `/.well-known` to the app.
    - The API image has no UI stage and answers 404 for anything else.
    - `next.<domain>` redirects to the same page.
    - `app/web` and its CI jobs are deleted.
  - **Found on the way:**
    - Argus sends a list as one text block per row, which joined is not JSON.
      The chat now takes its `structuredContent`, one compact JSON list, for
      the model and the page.
    - Argus may reach GitLab by an internal name, so links use a new live
      setting, **GitLab address for links** (79 settings now).
    - The tool card's argument names were below 4.5:1 contrast.
  - **Tests:**
    - 182 backend and 90 UI tests.
    - 117 browser tests on the live stack with the real model (4 skipped: the
      Argus fixture, and the no-gateway case). They include a chat with
      Argus's answers and images, served by the browser itself, which runs in
      CI too.
    - The stack suites still pass: functional 61/61, acceptance 33/0 (4
      skipped), auth audit, domain check, dashboards 34/34.

### Phase 3C.1 — Chat polish  *(S)*
- The layout uses wide screens: the thread, the settings and the admin pages grow
  with the window instead of staying a narrow column.
- The chat list has no sideways scrolling. Each chat can be renamed, archived
  (with an Archived view to bring it back), deleted or forked.
- Fork a chat from any message: a new chat with the branch up to that message.
- A rail of the questions in the chat, to jump to any of them (like ChatGPT and
  DeepSeek).
- Medium thinking by default.
- **Done when:** each is covered by UI and browser tests, on desktop and phone.
- **Result:**
  - **Width:** Comfortable, Wide (the default) or Full width, in the account
    menu and on Your account. At 2560 px the thread is about 1,200 px wide
    instead of 768, and admin pages use the screen.
  - **The chat list:** a grid item's minimum width had made long titles push
    the list sideways; titles now end in an ellipsis. Rename, fork, archive and
    delete are in each chat's menu and the chat's header. **Archived chats**
    has its own view, and writing in an archived chat brings it back.
  - **Forks:** from the list or header (the branch on screen) or from any
    answer (**Fork from here**). A fork shares the original's files, and
    notes where it came from.
  - **Question rail:** jump to any question; the one being read is marked.
  - **Medium thinking** is the default in compose, both env samples and this
    host's `.env`.
  - **Found on the way:**
    - Deleting a chat never deleted its files, though the dialog said so.
      Images are stored in the database, so they piled up. Now they go,
      except files a fork still uses.
    - Opening two-factor setup and cancelling signed the person out on every
      other device, because it rotated the security stamp. It no longer does;
      turning two-factor on still does. In CI this signed out the browser
      suite's shared session.
    - A page's code or styles that fail to arrive are fetched once more, and
      otherwise the page says it did not load. Before, a dropped CSS file
      showed "Something went wrong".
    - The browser tests' `ask()` could report an answer finished before it
      started: in a new chat, Send shows while the chat is being made.
  - **Tests:** 186 backend, 95 UI, and 121 browser tests on the live stack
    with the real model (5 skipped), with no retries.

### Phase 3D — Tools  *(L)*
- A tool registry in the API (built-in tools and MCP servers, Argus among them);
  admins turn each on or off and choose who may use it; a tool picker per chat;
  "ask before running" per tool.
- **Image generation**, local: FLUX.2 [klein] 4B (Apache 2.0) on
  stable-diffusion.cpp's OpenAI-style server, in the GPU memory the chat model
  leaves free. The model calls it as a tool, and the picture shows in the answer
  and the Files panel.
- Built-in: Argus, calculator, date and time, reading attached files in parts, a
  **Python sandbox** (its own container: no network, read-only root, CPU, memory and
  time limits; files it writes appear in the Files panel), and optional web fetch and
  search (off by default; allow-list; air-gapped installs leave it off).
- **Done when:** each tool is tested end to end, and the sandbox has escape tests
  (network, filesystem, time and memory limits).
- **Progress (part 1 of 2):**
  - **Groups** (Admin → Groups), the base for "who may use it": app groups,
    and directory groups whose members follow LDAP. Each person's directory
    groups are now kept at sign-in and every sync.
  - **The registry:** built-in tools and MCP servers behind one interface.
    Admin → Tools turns each on or off and sets who may use it, on in new
    chats, and ask before each call. A chat keeps its own list (the old Argus
    switch became a list; chats with Argus off kept no tools). The server
    checks access on every answer.
  - **Built in:** Argus; **image generation** (FLUX.2 [klein] 4B beside the chat
    model, about 20–30 s a picture); an **exact calculator** (decimal, 28
    digits; the first, double-based version got the product of two nine-digit
    numbers wrong, which the live test caught); date and time.
  - **MCP servers:** added with a test, an encrypted key, and optionally the
    person's email; their functions are named `{server}__{function}`.
  - **Ask before running:** the answer waits for Allow or Don't allow (ten
    minutes at most), and only the chat's owner can answer.
  - **Found on the way:** tool JSON was escaped for HTML (`+` as `\u002B`,
    `<` as `\u003C`), and the model read the escapes. It is now written plain.
    The chiseled app image had no time zones; it now carries 3.9 MB of them.
  - **Tests:** 226 backend, 103 UI, 133 browser on the live stack with the
    real model (5 skipped), no retries.
  - **Left for part 2:** the Python sandbox, web fetch and search, reading
    attachments in parts.

### Phase 3D.2 — Models at runtime  *(M)*
Pulled forward from phase 6.
- llama.cpp's router mode serves every local model, each with its own preset
  (context, offload, speculative decoding). LiteLLM routes them all to it, so a
  model is added or switched with no restart.
- **Models** page: admins load, unload and switch models with one click. One
  GPU holds one large model, so a switch is an admin's decision; people pick
  from the loaded models they may use.
- **Access per model:** everyone, admins, or chosen groups. It is enforced in
  the chat and on API keys (LiteLLM's per-key model list).
- **Done when:** the three local models switch from the page with no shell, a
  person sees and can call only the models they may use, and both are tested.

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
- The engine: model switching moves to 3D.2 (router mode). What remains is
  engine-wide arguments, applied by a small supervisor inside the engine
  container when the app asks over the internal network (authenticated,
  validated, audited).
- What is left (compose profiles, host limits) is listed with its reason.
- **Done when:** changing each setting in the app takes effect without a shell, and a
  test proves it for each.

### Phase 7 — Cutover  *(S)*
- Delete the legacy services and their config; reorganise the repo around the app.
- Rewrite the README and docs; one `.env` sample per hardware setup still works.
- Full from-zero deploy plus every test suite; tag a major release; merge to `main`.
