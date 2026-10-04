# Enterprise solution: one app at `llm.<domain>`

Worked on the `enterprise-solution` branch through phase 5, then merged into `main`
with the cloud session's .NET Argus (phase 4's port), and the repository reorganised
around the app (`src/`, `tests/`, `deploy/`, `tools/`, `docs/`). The cutover
(phase 7) is done: the services the app replaced are gone from the deployment.

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
| Dashboards | All ten dashboards are drawn by the app from the same files (Grafana's JSON format). |
| Alerts | Prometheus rules stay; the app shows firing alerts and history. Alertmanager stays as plumbing. |
| Repo | `main`, organised as `src/` (the API, Argus, both web apps), `tests/`, `deploy/` (the platform and Argus alone), `tools/` and `docs/`; one .NET solution and one CI workflow. |
| Stack | .NET 10 LTS, EF Core + Npgsql, OpenIddict, YARP, xUnit + Testcontainers; React 19 + Vite + TypeScript, TanStack Query, ECharts, Vitest, Playwright. |
| Frontend | Rewritten from zero as its own container (`web`, in `src/web/`): Radix primitives + Tailwind with our own components (the shadcn/ui approach), light and dark themes, self-hosted fonts and icons (air-gapped installs), accessibility checked with axe in every browser test. It replaced the first UI (`app/web`, served by the API) at `llm.<domain>` in 3C; the API now serves no pages. |
| Decision models | Jev and Laya ("System One" models that return typed probabilities, not text; September 2026) were evaluated and **skipped for now**. Jev is hosted only, so data would leave the network. Laya is open, but zero-shot it scores below the majority baseline on its authors' benchmark and needs labelled data and calibration per task. It does not improve the chat. Worth revisiting when a team has a classification task with labelled data. |
| Settings | Everything is configurable in the app. A setting applies at once when its service can take it live; otherwise the app saves it and shows the one command to apply it (still no Docker socket). Phase 6 makes those live too. |

## How "fully tested" is enforced

1. **Parity tests stay alive the whole time.** `deploy/scripts/functional-test.py`,
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

Status: **Phase 7 done** (the cutover: only the app, the web and the model
server remain), after **Phase 5** (every dashboard, the logs and the alerts in
the app), **Phase 4** (the .NET Argus is the platform's `argus` service, with
the chat's per-person path, CPU embeddings, and packs loaded from a library) and
**Phase 3D** (the Python sandbox, the web, reading files in parts, Office
documents, models at runtime, groups, the tool registry, image generation, MCP
servers, ask before running, fair use). Next are 3E (assistants and knowledge),
3F (organise and share) and 6 (every setting live).

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
- **Result:** the admin panel is gone. The dashboard
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
- **Result so far:** the chat is in the app ([chat.md](chat.md)). It
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
  - A from-zero deploy (sample `.env`, generated secrets, empty volumes) passes the
    same suites: functional 61/61, acceptance 33/0, auth audit, domain check,
    dashboards 34/34, browser 50/50.

Phases 3A–3F rebuild the web from zero as its own container and extend the chat.
Each is deployed and tested before the next starts. Until 3C the new web ran
at a preview address beside the old one.

### Phase 3A — The new web: container, design system, shell  *(M)*
- `src/web/`: Vite + React 19 + TypeScript, built into its own image (Alpine +
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
- **Result:** `src/web` is built into the `web` image and served at `next.<domain>`.
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
  ([settings.md](settings.md)).
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
- **Result:** the chat is rebuilt ([chat.md](chat.md)), and the new web
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
    - The preview address redirected to the same page until the cutover.
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
- **Progress (part 2 of 2):**
  - **Python sandbox** (profile `sandbox`): its own container with no network
    at all, a read-only root, and only the capabilities to run each job as its
    own user; per job CPU time, memory, file size, open file and process
    limits, a wiped tmpfs directory, everything its user started killed
    afterwards, and a stop from the page that reaches the run. The app hands
    it jobs through a volume, so no Docker socket and no port. numpy, pandas,
    matplotlib, scipy, sympy, openpyxl. The chat's files are in its working
    directory (the original .xlsx, not its text); charts come back as pictures
    and other files to open or download.
  - **Escape tests** (`scripts/sandbox-check.py`, 17 checks against the
    running container): network, reading the jobs and other runs, writing the
    image, time (busy and sleeping), memory, file size, a fork bomb, processes
    that try to outlive the run, huge output, stop.
  - **Web** (off by default): search through the `websearch` profile's
    SearXNG, and reading pages, from sites an admin allows only; every
    connection, redirects included, is checked to be a public address as it
    is made (DNS rebinding included). Pages are read as text, in parts.
  - **Reading files in parts:** a long attachment goes into the question up
    to a budget, with a note; the model reads on by lines or searches it.
    Files tools made are read the same way. Text kept per attachment went
    from 200,000 to 1,000,000 characters.
  - **Office documents** as attachments: Word, Excel, PowerPoint,
    OpenDocument and RTF, read on the server from their XML.
  - **Found on the way:** a fork bomb's children, killed but never reaped
    (the runner was PID 1), filled the next job's process limit: the sandbox
    now has an init. After a stop, "Answer again" could leave the new answer
    streaming unseen (a finished run cleared the page), and a stop just
    before the new answer existed hid every answer; both fixed and tested.
    The question rail sat under the scrollbar and did not follow a new
    question; the UI got its motion.
  - **Fair use** (asked for after): a person has one answer running at a
    time and the chat as many as the engine serves at once; the rest wait in
    line, served in turn (whoever has had least goes first), and see their
    place. API keys have at most two requests at once at LiteLLM (429
    beyond), applied to every key. The model menu showed the first model
    instead of the default and went stale after a switch; fixed.
  - **Tests:** 274 backend, 111 UI, 145 browser on the live stack with the
    real model (Python on a workbook, a web search and page, Office files),
    sandbox 17/17, functional 65/65, acceptance 32/32, dashboards 34/34.


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
- **Progress:**
  - **Router mode:** `services/llamacpp/router.sh` builds the `.env` model's
    preset (the same memory logic as before; `LLAMACPP_EXTRA_ARGS` translated
    to preset keys), adds the app's `config/engine/models.ini`, and starts
    llama-server with `--models-max 1 --no-models-autoload`. A change to the
    file restarts it; at start it loads the model the app chose last.
  - **Admin → Models:** every model at the gateway; load and unload the
    engine's; add, edit and remove models from the library
    (`LLAMACPP_LIBRARY_DIR`, GGUF headers read for architecture, size and
    trained context). The app registers them at LiteLLM itself (`/model/new`,
    marked as its own and fingerprinted, so only changes are re-sent). The old
    Model page is now **Deployment**.
  - **Access per model:** everyone, admins or chosen groups. The chat lists
    only a person's models and the server refuses the others; a model that is
    not loaded is listed greyed out and refused with a reason. API keys follow:
    the app keeps each key's model list at LiteLLM (`/key/update`), on every
    change and every ten minutes, so another model answers 403.
  - **Metrics:** Prometheus scrapes `/metrics?model=<loaded>` from a target
    file the app writes, so the dashboards follow a switch.
  - **Found on the way:** this build of llama.cpp replaced `--mlock` with
    `load-mode = mmap+mlock`; router-level arguments override every preset, so
    all of a model's arguments live in its preset; an unhandled exception in a
    background service stopped the whole app (the watchers now catch
    everything but shutdown); a model that failed to load was loaded again
    every 3 seconds, 200 times in ten minutes, with nothing loaded meanwhile
    (the router marks it `failed`; the app now shows that, does not retry it,
    and loads the `.env` model in its place; the live test used a library file
    that turned out to be an incomplete download); Prometheus's config directory is a read-only
    mount, so the target file is mounted beside it; and the acceptance and
    functional suites predated the image model (they now generate a small
    picture with it rather than chat with it).
  - **Live, on the RTX 3090:** the 27B added from the library (the engine
    restarted and had the 94 GB `.env` model loaded again in 28 s); switched to
    it in 16 s, answering in 5.5 s; back in 34 s; Prometheus followed each
    switch. The library's third model, Flash-Next IQ3_XXS, is an incomplete
    download: its load failed once and the `.env` model took its place in 32 s.
    Two models switch from the page; the third needs its file downloaded again.
  - **Tests:** 231 backend, 108 UI, 139 browser on the live stack (5 skipped;
    one certificate error from the installed Chrome, passed when re-run),
    functional 65/65 (a model given to admins leaves a person's key, is
    refused, and comes back), acceptance 32/32.

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
- **Done when:** you sign it off and `enterprise-p3` is tagged.

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
- **Result:** every dashboard is drawn by the app (Observe → Dashboards): Prometheus and
  Loki panels as well as SQL, with Grafana's steps, macros, variables, legends, stats,
  gauges, tables and logs panels. `compare-dashboards.py` ran every panel in both:
  153/153 identical over (30 d, 6 h), (7 d, 24 h) and (2 d, 1 h), then Grafana was
  removed with its sign-in client (deleted at start where an older version left it),
  scrape job, probes, secrets and routes. The Logs page reads Loki by container, level
  and text, with a live tail; the page never sends LogQL. The Alerts page shows what
  fires (Alertmanager), what fired over a day, week or month (Prometheus's `ALERTS`),
  and every rule. The app now probes Alertmanager and Loki, and Promtail lifts the
  app's own log level. `audit-dashboards.py` runs every panel's queries in the app:
  207 queries, 0 errors. Tests: 290 backend, 119 UI; the live stack passes functional
  68/68, acceptance 32/0, the auth audit and the domain check. Browser: 174 passed,
  including every new page in both themes on desktop and phone (the chat tests run one
  at a time: every browser signs in as the same admin, and fair use queues one
  person's answers). All of it on a deploy from zero, after merging the cloud
  session's .NET Argus into `main`.

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
- Delete the legacy services and their config. (The repository was reorganised around the app early, with the merge into `main`.)
- Rewrite the README and docs; one `.env` sample per hardware setup still works.
- Full from-zero deploy plus every test suite; tag a major release; merge to `main`.
- **Result:** the replaced services, their settings, profiles, routes, middlewares,
  secrets and import paths are deleted, with no compatibility path: the chat is the
  app's, sign-in is the app's (forwardAuth for the internal services, OIDC for
  Langfuse), and the gateway attributes and limits chat spend from the app's own
  `X-LLM-User-Email` and `user`. People connect their tools from **Connect your
  tools** (`/setup`). The `.env` samples, the docs and the scripts describe only
  this stack.

## v4.0.0 — Simple to deploy; sound and video in and out (2026-10-03)

- **Deployment**: one 300-line compose file and a short `.env` (the domain, the
  models folder, the first model, six secrets). Every module runs; one is left
  out in `docker-compose.override.yml`. No setup containers: each running image
  prepares itself, and the app fetches the models the others read. Traefik does
  TLS alone (its own certificate, Let's Encrypt, or yours). Rootless Podman with
  `podman.yml`. The app's settings live in its database only.
- **Models**: every chat model is an Admin → Models model (no ".env model");
  the picture, video and speech models have the same controls (on or off, keep
  loaded, load, unload, who may use them).
- **Sound and video**: voice messages and sound or video files go to a model that
  hears and sees (Qwen3-Omni) or as a transcript and frames to one that does not;
  answers are read aloud (Kokoro, a Persian voice); tools make videos (Wan2.2)
  and speech.
- Removed: Langfuse, vLLM, cAdvisor, DCGM, the stack CA, apply-settings, the
  env samples, the air-gap bundle and the checks written for the old layout.

## Next (after v4.0.0): the roadmap to the one enterprise AI chat

### Where it stands against the others

What ChatGPT Enterprise, Claude for Work, Microsoft Copilot, Gemini for
Workspace, Glean and the self-hosted chats (Open WebUI, LibreChat) have, against
Argus Arena 4.0.0:

| capability | others | Argus Arena 4.0.0 |
|---|---|---|
| Chat, files, tools, sub-agents, deep research, projects | all | **yes** |
| Code that knows your codebase, within each person's GitLab rights | Copilot (GitHub only) | **yes, and stronger** (Argus) |
| Runs on your own hardware, no data leaves | Open WebUI, LibreChat | **yes**, with keys, credit, dashboards and audit they lack |
| Sound and video in and out, omni models | ChatGPT, Gemini | **yes** (voice messages, read aloud; no live voice yet) |
| Memory across chats | ChatGPT, Claude, Gemini | no |
| Shared assistants (custom GPTs, Gems, Claude projects for a team) | all four | no (projects are one person's) |
| Company knowledge: Confluence, SharePoint, Drive, Jira, wikis, with each person's rights | Glean, Copilot, Gemini, ChatGPT connectors | no (code and packs only) |
| Canvas: a document or code edited beside the chat | ChatGPT, Claude, Gemini | no (live previews only) |
| Shared chats, links within the company | all | no |
| Prompt library, slash commands | Copilot, Open WebUI, LibreChat | no |
| Feedback on answers, model comparison | all (internally) | no |
| Live voice conversation | ChatGPT, Gemini | no |
| Actions on other systems (create an issue, post to a channel), with approval | ChatGPT actions, Copilot | partly (MCP servers an admin adds) |
| A plugin catalog | ChatGPT GPT store, Copilot agents | no (MCP servers by hand) |
| CI and event triggers (a merge re-indexes, a failing pipeline is explained) | Copilot for GitHub | partly (Argus has a GitLab push webhook; v4 leaves it off) |
| Chat in Teams, Slack, Mattermost, email, the browser, the phone | Copilot, ChatGPT | no |
| Company sign-in (Entra ID, Okta, Keycloak) and SCIM | all | no (own accounts, LDAP) |
| Retention, legal hold, eDiscovery | ChatGPT Enterprise, Copilot | no |
| Budgets per team, chargeback | ChatGPT Enterprise | per person only |

What none of them has, and this platform can: **arena mode** (models answered
side by side and blind, voted on, a leaderboard of the company's own models
on the company's own questions), and **the whole stack on one GPU host,
offline**.

### The releases

Each item has its size (S days, M a week or two, L more) and the test that
says it is done. The releases are ordered by value per effort: speed and
tokens first (every person feels it daily), then automation and plugins (what
makes it a platform), then parity, then governance.

### Done in v4.1.0 (2026-10-04)

W1, T1, T2, T3, T7, P1, P2 and W2 below are done; T4–T6, W3, P3 and v4.3–v4.4
are open. Measured on the live stack:

- T1: turns 2 to 5 of a 7,000-token chat read 99.6–99.7% of their prompt from
  the cache (the gauge: tool rounds 86–98%, the misses being the hybrid model's
  checkpoint after a tool call).
- T2: with every tool on, a first question reads 927 tokens (about 6,000 with
  every tool whole; Argus alone is 18,600 characters of definitions).
- T3: deep research is **not** under four minutes on this host. The engine
  generates about 10 tokens a second and reads about 160 (Flash-Next with its
  experts on the CPU), so the sub-agents' writing dominates: four sub-agents
  took 255–518 s each (1,700–2,900 tokens written, 70–84k read, 72–83% from
  the cache) before the caps on their calls and steps. A sourced report needs
  a GPU that holds the model whole, or a smaller model for the sub-agents
  (T4).
- Found on the way and fixed: tool arguments with a key written twice failed
  the whole answer; the answer line and sub-agents ignored the engine's slots
  (never set); GitLab sign-in with a username and password could no longer be
  set in v4.

### v4.1 — Faster answers, fewer tokens

**T1 Prompt cache by design** *(S)*. The engine reuses the KV cache of a
prompt's unchanged start. Keep the start unchanged: the system prompt first and
stable (the date at day level already; tool instructions in a fixed order;
project files after the instructions), `cache-reuse` and
`slot-prompt-similarity` set in every model's preset, so a conversation's next
turn lands on the slot that holds it. Show the cache-hit share per model on the
LLM dashboard. *Done when:* the second turn of a long chat reads more than 90%
of its prompt from the cache (measured in the usage events).

**T2 Tools on demand** *(M)*. Every tool's full schema goes into every request:
with Argus, the web, Python and a few MCP servers that is 5,000 to 15,000
tokens before the question. Send the names and one line each, plus a
`load_tools` function; the full schemas join the conversation when the model
asks (as Claude Code does). *Done when:* a chat with 40 tools sends under 1,500
tokens of tool text on a turn that uses none.

**T3 Tool results condensed** *(M)*. A web page, a file or an MCP result goes in
whole up to a budget; past it, a small model (or the extraction rules per kind:
tables kept, boilerplate dropped) condenses it to what the question needs, with
a handle to read the rest (as `read_file` does). Fetches cached by URL for a
day. This is also the main lever for deep research (N4). *Done when:* the
live deep-research test finishes in under four minutes, with sources intact.

**T4 Auto model** *(M)*. "Auto" in the model menu: a fast small model (a 4B on
the GPU beside the big one, or the CPU) classifies the question (greeting,
lookup, rewrite, code, reasoning, research) and answers the easy ones itself;
the rest go to the big model, with thinking set by difficulty. Per answer the
chat says which model answered and why; one click asks the big one. *Done
when:* on a set of 200 real questions the answers rated equal or better stay
above 95% while the big model's tokens fall by a third.

**T5 Retrieval instead of stuffing** *(M)*. Project files, long attachments and
chat history beyond the context go into pgvector (the embedder already runs);
each turn takes the passages that match, with their place, instead of the
first N characters. *Done when:* a project with 200 files answers about any of
them without the files inlined.

**T6 Answer cache for the API** *(S)*. An exact-match cache per key and model
(opt-in, on Postgres), for pipelines and FAQ bots that ask the same thing;
a semantic cache only where an admin turns it on. *Done when:* a repeated
identical API call answers from the cache, costs nothing, and says so in a header.

**T7 Concise by default, per person** *(S)*. A response-length preference
(short, normal, thorough) in the account, applied as an instruction, and a
"shorter" and "longer" action on each answer. *Done when:* the preference
reaches every request and the actions re-answer at the new length.

### v4.2 — Events, CI and plugins

**W1 Index on push and merge** *(S)*. Argus already re-indexes a repository on
GitLab's push event. Make it one step to turn on: the app makes the webhook
secret, keeps it, gives it to Argus through its admin API (not `.env`), and
**Admin → Indexing** shows the URL (`https://argus.DOMAIN/webhook/gitlab`) and
the secret to paste into GitLab (group-level, so every project is covered);
merge-request events re-index the target branch; a delivery log shows the
last events. GitHub and Gitea too. *Done when:* a push to the test GitLab is
searchable in Argus within a minute, with no manual run.

**W2 Triggers: events that run an assistant** *(L)*. A trigger is: an event
(a GitLab merge request opened or updated, a pipeline failed, an issue labelled;
any CI posting JSON to `https://DOMAIN/api/hooks/<id>` with its secret; a
schedule, as tasks do today), an assistant (W5: instructions, model, tools),
and where the answer goes (a chat, the bell, email, an outbound webhook, a
comment on the merge request or issue). The answers that write to GitLab use a
**separate** bot token with only the comment scope, never Argus's read-only one;
each write is audited. Built on the scheduled-task runner. Examples shipped:
review a merge request (its diff, with Argus for context), explain a failed
pipeline from its log, draft release notes from merged requests. *Done when:*
opening a merge request in the test GitLab gets a review comment, and a failing
pipeline gets an explanation, end to end.

**W3 The API in CI** *(S)*. A GitLab CI template (`clients/gitlab-ci/`) and a
small CLI (`arena ask`, `arena review`) that call the gateway with a project
key: for teams that prefer the job in their own pipeline to a webhook. *Done
when:* the template reviews a merge request in the test GitLab's CI.

**P1 Plugins** *(L)*. A plugin is a signed folder with a manifest:

```yaml
name: jira
version: 1.2.0
title: Jira
description: Search, read and create Jira issues.
provides:
  tools: { mcp: "https://jira-mcp.internal/mcp" }      # or openapi: ./openapi.yaml
  prompts: [./prompts/*.md]                           # slash commands
  assistants: [./assistants/triage.yaml]
  knowledge: { connector: jira }                       # P3
auth:
  per_person: oauth2                                   # or api_key, or shared (admin)
  oauth2: { authorize: ..., token: ..., scopes: [read:jira-work, write:jira-work] }
writes: [create_issue, add_comment]                    # asked first, always
settings:
  - { key: base_url, type: url, required: true }
```

What it adds, the app shows and governs as it does tools today: who may use it
(groups), asking first for every tool in `writes`, each person's own credentials
(OAuth, kept encrypted with `APP_KEY`, refreshed by the app), every call in the
audit log. Installed from a catalog (a signed index, as Argus's packs are
published: the Binchitects bucket, or a company's own), an upload, or a URL.
No plugin code runs in the app: a plugin's tools are a remote MCP server, an
OpenAPI service (P2), or a container the admin adds to
`docker-compose.override.yml` (the catalog gives the snippet). *Done when:*
the Jira and GitLab-issues plugins install from the catalog, each person
connects their own account, and a write asks first and is audited.

**P2 OpenAPI actions** *(M)*. An OpenAPI document and its auth become tools (as
GPT actions do): every internal REST API is a tool without writing an MCP
server. Operations marked unsafe (POST, PUT, DELETE) ask first. *Done when:*
a petstore-like test API's operations are callable from the chat, writes asking first.

**P3 Company knowledge** *(L)*. Connectors (as plugins) that sync Confluence,
SharePoint and OneDrive, Google Drive, Jira, GitLab wikis and issues, file
shares and websites into knowledge bases: chunked, embedded (pgvector),
re-synced on change, and **with each document's readers**, so
`search_knowledge` returns only what the asker may read, with citations.
Argus stays the code path; this is the documents path. *Done when:* a person
without rights to a Confluence space gets nothing from it, and one with rights
gets the passage with its link.

### v4.3 — Parity: what people expect of a chat

**E1 Memory** *(M)*. What a person tells the chat to remember, and what it
proposes to remember (asked first), kept per person; listed, edited and
deleted in the account; given to each answer in a few hundred tokens; off per
person or for the company. *Done when:* "remember I deploy with Podman" is
used in a new chat, and deleting it removes it from the next answer.

**E2 Assistants, shared** *(M)*. Projects grow into assistants a team uses:
instructions, model, thinking, tools, knowledge bases, conversation starters,
shared with groups (use or edit), in a gallery with usage counts (N5 is part of
this). *Done when:* an assistant shared with a group is usable by its members
and invisible to others.

**E3 Shared chats** *(S)*. A read-only link for people in the company (or a
group), which they can fork into their own chat; revoked in one click. *Done
when:* a shared chat opens for a colleague and not for someone outside the group.

**E4 Canvas** *(L)*. A document or code beside the chat, edited by the person and
by the model (by changes, not rewrites), with versions, comments on a
selection ("make this paragraph shorter"), and export to Word, PDF and
Markdown. *Done when:* a model's change to one section leaves the rest
untouched, and every version can be restored.

**E5 Prompt library and slash commands** *(S)*. Prompts with variables, personal,
for a group or for the company; `/` in the composer finds them. Plugins add
their own. *Done when:* `/review` fills its variables and sends.

**E6 Feedback and arena mode** *(M)*. Thumbs on every answer with a reason;
an admin quality page per model and assistant. Arena mode: one question to two
models, side by side and blind, the person votes; a leaderboard of the
company's models on its own questions. *Done when:* votes land on the
leaderboard, and feedback is visible per model.

**E7 Live voice** *(M)*. A voice conversation: streaming speech to text with
voice activity, the answer spoken as it is written, interruptible (the speech
server's realtime API, or an omni model's). *Done when:* a spoken question gets
spoken words back in under two seconds, and speaking over it stops it.

**E8 Where people already are** *(M each)*. A Teams, Slack and Mattermost bot (an
assistant in a channel, each person as themselves); email in (forward a
thread, get an answer); a browser extension (ask about the page or a
selection); the web app installable as a PWA with push notifications. *Done
when:* a question in a channel is answered with the asker's own rights.

### v4.4 — Governance and scale

**G1 Company sign-in** *(M)*. OIDC and SAML with Entra ID, Okta, Keycloak and
Google (besides own accounts and LDAP), groups from the IdP, and SCIM for
people joining and leaving. *Done when:* a person removed in the IdP is
disabled here within minutes, keys included.

**G2 Retention and legal hold** *(M)*. Chats and files kept N days per group (then
deleted, audited), a legal hold that suspends it for named people, and an
export of a person's data (eDiscovery, and a person's own copy). *Done when:*
a chat past its retention is gone, and one under hold is not.

**G3 Budgets per team** *(S)*. Credit for a group (shared or per member), cost
centres, a monthly chargeback report; one credit across chat and keys (N1).
*Done when:* a group's spend stops at its budget across chat and API.

**G4 Safeguards everywhere** *(M)*. The chat's checks on the API path too (N3),
secret scanning on the way in (keys, passwords, private keys refused or
masked), and policies per group. *Done when:* a pasted private key is refused in
the chat and through the API.

**G5 Answer traces for admins** *(S)*. Per answer, a timeline: the prompt's size by
part, each tool call and its time, tokens, the cache share, the model's speed.
Content only where a policy allows it. *Done when:* a slow answer's trace says
where the time went.

**G6 Scale out** *(L)*. A Helm chart, several app replicas, an external Postgres,
a pool of GPU hosts with load-aware routing (the remote servers, grown up),
queue priorities per group, spend and search at scale (N2). *Done when:* two app
replicas and two GPU hosts serve the scale test with no answer lost.

**G7 Offline and recovery** *(M)*. The v4 air-gap bundle, the restore round trip,
the rollback test (N6). *Done when:* each passes from a script.

### Still open from before

- N4 Deep research faster: carried by T3.
- N7 Video faster on a shared GPU: decode on the GPU when it has room.
- N8 Smaller things: queued messages kept on the server; document previews past
  20 pages; credit and alert notifications by email and webhook; the seven
  unpublished packs published; the live e2e suite one model test at a time in
  CI; Podman and the media paths in CI; a better Persian voice.

### Order, if one thing at a time (as planned after v4.0.0; superseded below)

1. W1, T1, T7 (days each: the push webhook back on, the cache, concise answers).
2. T2, T3 (the biggest token cuts; T3 also fixes deep research).
3. P1 with P2, then W2 (the platform: plugins and triggers).
4. E1, E2, E3, E5 (what people miss first when they come from ChatGPT).
5. T4, T5, E6 (auto model, retrieval, arena mode).
6. G1, G3, G2 (what procurement asks).
7. P3, E4, E7, E8, G4 to G7.

## Next (after v4.1.0)

What v4.1.0 taught, and what follows from it:

- **The GPU is the ceiling, not the code.** Flash-Next with its experts on the
  CPU writes about 10 tokens a second and reads about 160. Every agentic
  feature (deep research, triggers, sub-agents) waits on the model's writing.
  The next speed gain is a second, small model doing the many small steps, not
  more token cuts.
- **Two things shipped on tests alone**: W2 (tasks run by GitLab events) was
  not run against the test GitLab, and the sub-agent caps were not measured.
- **Measuring needed scripts each time** (the research timings, the cache share,
  where an answer's time went): admins need that view in the app (G5).
- Still missing against the other enterprise chats: memory, shared assistants,
  shared chats, a prompt library (E1, E2, E3, E5); company knowledge beyond code
  (P3); company sign-in (G1).

### v4.2: proven, and fast where it counts (about two weeks)

1. **W2 live** *(S)*. The test GitLab: a merge request opened gets a review
   comment (with Argus for context), a failing pipeline gets an explanation on
   its merge request, an issue opened gets triage. *Done when:* all three land
   in the test GitLab from real webhooks, each audited.
2. **G5 Answer traces** *(S, moved up)*. Per answer, for admins: the prompt by
   part, the cache share, each tool call and sub-agent with its time and
   tokens, the engine's read and write speed, time in line. A sub-agent's
   trace is part of its answer's. *Done when:* the slowest step of a deep
   research answer is named on its trace, with no script.
3. **T4a A model for small steps** *(M)*. A setting, **Model for sub-agents and
   small steps** (summaries, titles, compaction, page condensing, triage), and
   the engine holding two models at once. Qwen3-4B is already in the library
   (no download): measure it on the GPU beside Flash-Next (one more expert
   layer on the CPU) against the CPU alone, and keep the faster. *Done when:*
   the live deep-research test finishes in under 6 minutes with its sources,
   and a chat's title and compaction no longer wait on the big model.
4. **W3 The API in CI** *(S)*. A GitLab CI template and a small `arena` CLI
   (`arena ask`, `arena review`) calling the gateway with a project key.
   *Done when:* the template reviews a merge request in the test GitLab's CI.
5. **Small ones** *(S)*: the certificate's expiry on Admin → Overview and an
   alert 30 days before; deep research's progress in words ("3 of 4 parts
   done"); a plugin's OAuth refresh tested against the test GitLab.

### v4.3: what people miss first (about three weeks)

6. **E1 Memory** *(M)*, **E5 Prompt library and slash commands** *(S)*, **E3
   Shared chats** *(S)*, **E2 Shared assistants** *(M)*, as described above.
   E2 builds on projects; plugins add prompts (E5) through their manifest.
7. **E6a Feedback** *(S)*: thumbs and a reason on every answer, a quality page
   per model and assistant. (Arena mode is E6b, in v4.4.)

### v4.4: knowledge and smarter routing (about four weeks)

8. **P3 Company knowledge** *(L)*. Start with what the test GitLab can prove:
   GitLab wikis and issues, then file shares and websites, then Confluence and
   SharePoint (these need instances, or their vendors' sandboxes). Each person
   gets only what they may read. *Done when:* a person without access to a
   wiki gets nothing from it, and one with access gets the passage and its link.
9. **T5 Retrieval instead of stuffing** *(M)*: project files and long
   attachments through pgvector (the embedder already runs).
10. **T4b Auto model** *(M)*: the small model of T4a answers the easy questions
    itself and hands the rest on, with the reason shown; **E6b Arena mode**
    *(M)* to check it on the company's own questions.

### v4.5: governance (about three weeks)

11. **G1 Company sign-in** *(M)*: OIDC first (Keycloak as the test IdP), then
    SAML and SCIM. **G3 Budgets per team** *(S)*. **G2 Retention and legal
    hold** *(M)*. **G4 Safeguards on the API path and secret scanning** *(M)*.

### Later

E4 Canvas, E7 Live voice, E8 Bots and the PWA, G6 Scale out, G7 Offline and
recovery, T6 Answer cache, and N7 and N8 as above.

### What needs a yes first

Downloads are asked for first, with name, source and size:

- G1: a Keycloak image (to test OIDC);
- E8: a Mattermost image (to test a bot);
- E7: a streaming speech model, if the speech server's own is not enough;
- P3 beyond GitLab: a Confluence or SharePoint sandbox.

T4a needs none: Qwen3-4B is in the library.

