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


## v5.0.0 — Parity, knowledge, governance and scale (2026-10-05)

Everything in v4.2 to v4.5 and Later above is built and released at once as
v5.0.0, each package on its own branch, then merged and tested together (511
backend, 150 Argus, 42 Arena Code and 310 web tests; the CLI's and the deploy
scripts' too). Arena Code, our own coding agent, came with it, and Argus now
takes a person's API key instead of a GitLab token.

Checked on the live stack after the merge: assistants and their gallery; a
canvas saved as a version and exported to Word; a share link refused for an
empty chat; Arena MCP (initialize, the person's 12 tools, the calculator, the
prompt library); Argus refusing an unknown API key and resolving a known one
to its person; the gateway with the key; a message queued during an answer and
answered right after it; one replica, leading; the Arena Code builds listed
for download.

Shipped on tests alone, to prove next:

- W3: the CI template reviewing a merge request in the test GitLab's CI.
- T4a: deep research under 6 minutes with the small model (not measured).
- G1: OIDC against a real identity provider (a Keycloak image is a download).
- E8: the bots against real Slack, Mattermost and Teams workspaces.
- E7: Talk with a real microphone and loudspeakers.
- G6: two app replicas live, and the Helm chart rendered by `helm` (not
  installed here; `check-chart.py` checks it without).
- N7: the video server's decode on the GPU (the 8 GB threshold).
- T6: the answer cache through Traefik's health-checked route.

## v5.1.0 — Proven, finished, and Arena Code in the browser (2026-10-05)

What v5.0.0 shipped on tests alone, proved on the live stack:

- **W3**: the CI template in the test GitLab's own pipeline (a shell runner):
  a review comment on a merge request that found both seeded bugs, and a
  failed job explained on it.
- **G1**: company sign-in with the test GitLab as the OIDC provider (as
  `http://gitlab.test:8929` on the stack's network): signed in, refused outside
  the required group, the groups claim filling a directory group.
- **E8**: a real Mattermost (10.11.3) bot answering in its thread as the asker;
  email in answered by email (an SMTP sink). Slack and Teams need their own
  workspaces: tests only.
- **E7**: Talk in Chrome with a recorded voice as the microphone: written down
  (20 s on the CPU speech server), answered, the sentence read aloud.
- **G6**: two replicas (one leads, settings everywhere, takeover in 4 s, the
  newcomer not taking the lead back); the Helm chart linted and rendered in
  five configurations (no cluster: kind's images are on Docker Hub).
- **N7**: proving it found a bug: the GPU decode never fit its 3 GB budget.
  Fixed: 63 s on the GPU, 309 s on the CPU, for a 9-frame clip.
- **T6**: the answer cache through Traefik's health-checked route.

Built: SAML (no package after all: SignedXml is in ASP.NET Core's shared
framework), Confluence and SharePoint connectors (against fakes: no instance
here), `arena-code web`, the certificate download and every Connect your tools
guide reworked (Qwen Code and Arena Code proved through the self-signed CA),
the gyro Persian voice, the live browser suite one model test at a time and
nightly on a self-hosted runner, the `qt` pack and the seven packs rebuilt
(kept local for the maintainer to upload; digests in docs/argus/overview.md).

**T4a measured, not met.** Deep research with Qwen3-4B as the model for small
steps: the four sub-agents finished 102 s after the plan (they took 255–518 s
each on the big model), but the whole answer took 1,606 s, because the big
model went on filling gaps itself: 18 page reads of its own, 679k prompt tokens
(88% from the cache) at about 160 tokens a second. The report was good (15k
characters, sources from docs.python.org). Next: send the gap-filling to
sub-agents too, and cap the big model's own tool rounds in research.

## v5.2.0 — Code Arena, Laya, and the AGPL (2026-10-06)

- **Code Arena**: Arena Code renamed, and its page an IDE (explorer, Monaco
  tabs, the agent's diffs, search, real terminals, the chat), proved in
  headless Chrome against the packaged Linux binary; packages for five systems
  from `tools/package-code-arena.sh`. The macOS and Windows terminals are
  built, not run.
- **Laya**: the module live on this host (both checkpoints load in about 2 s
  on 4 CPUs, 2.1 GiB), Decide (Laya) called by the real model in a chat;
  Code Arena's look at commands measured on 95 labelled commands (45 of 52
  risky ones ask at 0.6, 2 of 43 harmless ones).
- **Tools over a self-signed certificate or a company CA**: proved live with a
  CA-signed and a self-signed MCP server (refused with why, then trusted or
  unchecked, and called by the real model).
- **Argus with LDAP accounts**: proved against the test GitLab with an
  OpenLDAP server.
- **Deep research**: 616 s here (it was 1,606 s): the plan 155 s, the parts
  64 s, the report about 400 s at the big model's 10 tokens a second.
- **The default chat model**: the admin's default wins unless it cannot load.
- **Licensing**: AGPL-3.0-only with section 7 terms, or a commercial license.
- Before the tag, an adversarial review of everything since v5.1.0 found 13
  confirmed defects (two serious, in Code Arena: a link escape and git running
  a repository's own commands) and 36 smaller ones; all fixed with tests, each
  fix checked by a second reviewer.

## v5.3.0 — People at once, what each prompt cost, and help everywhere (2026-10-10)

- **People at once**: proved first with a live load test on v5.2.0: people on
  a small model waited 84–86 s in line and 2 of 6 of their answers failed to
  load the model, chat answers waited 84–121 s for a first token while models
  swapped. Fixed by a line per model, two models at once, room made only by an
  idle model that is not the big one, and retries of a model that failed.
- **The token cache, measured**: with four chats taking turns over the
  engine's slots, today's turns grew 14 → 31 s (the chats overwrote each
  other's cached history; llama.cpp's RAM cache cannot restore this hybrid
  model). Each chat now keeps its own slot (`id_slot`, idle slots kept, 4
  checkpoints a slot: 450 MiB of RAM instead of up to 3.6 GiB): 7–11 s a turn
  whatever the history.
- Prices for every model and medium, each prompt's cost for everyone; Argus
  repositories (one copy whatever the token, search and batch changes,
  schedules, progress and logs); LDAP test and try; voice per person; API rate
  limits; deep research as a tool; Admin → Storage; arrow-key history; Code
  Arena's connection to Argus and Arena MCP, compaction threshold and watched
  commands; the sign-in eyes; the folding chat list; dashboards named from the
  machine; a manual and help on every page.
- Every package was reviewed adversarially and repaired before merging.

## v5.4.0 — Code Arena with the web, as able as other agents, and Argus across the estate (2026-10-10)

Asked on 2026-10-06, 2026-10-09 and 2026-10-10, done one item at a time by
one agent, each committed and pushed when its tests passed:

- **The model that seemed stuck**: an admin's Load lost in an engine restart
  is sent again. **The gaps between tool calls**: lighter thinking between
  rounds, 8-9.5 s to 4.7 s a round, measured live.
- **A session cache in RAM**: measured first, llama.cpp's own RAM cache
  already brings back chats that lost their slot (six long chats over four
  slots: 57-63% of each prompt from the cache, about all there is); the
  setting sizes it, and the dashboard tells RAM used by programs from the
  page cache that holds the models.
- **Existing accounts to the directory**, keeping everything of theirs.
- **Every table sorts and filters**, in the app, in chat answers and in Argus.
- **The offline installer**: install, upgrade from v5.2.0 with a backup and
  rollback, repair, remove, status, verify; proved on Docker and rootless
  Podman with fake engines.
- **Code Arena**: its own commits under its own name; its sessions and the
  web chat kept in step both ways; the features below, from the comparison;
  reliability through a restarting gateway, a stalled stream and a dropped
  connection; a sandbox for its commands, proved on this host (the project
  writable, home read-only, `~/.ssh` and Docker's socket hidden, a private
  `/tmp`, the network on or off).
- **Argus across 200 repositories that affect each other** (asked for v5.5,
  done here): links from packages, imports, namespaces, modules, #includes,
  submodules, CI includes and images in nine ecosystems; `system_map`,
  `dependency_path`, `change_impact`; on a generated estate of 200
  repositories its 469 links found exactly, resolved in 28 ms.

### Code Arena compared (2026-10-10)

What Claude Code, Qwen Code, Hermes Agent (Nous Research) and DeepSeek
Harness (`dsh`, a developer preview: its own pages say little beyond a local
web UI, a headless profile, a plan, and plugins for everything) do, and what
Code Arena had when v5.4 began. "v5.4" marks what this release adds.

| Feature | Others | Code Arena |
|---|---|---|
| Agent loop: read, write, edit, grep, glob, shell, git | all | had |
| An IDE or web UI beside the terminal | Claude Code (IDEs, desktop), Qwen Code (IDE companion), Hermes (desktop), dsh (web) | had (the IDE in the browser) |
| One-shot, headless, JSON | Claude Code, Qwen Code, dsh | had (`-p`, `--output json`) |
| Modes: ask, edits allowed, plan, everything | Claude Code, Qwen Code | had |
| Rules kept for next time: allow or deny a tool or command pattern | Claude Code, Qwen Code | v5.4 |
| A sandbox for commands | Claude Code (bubblewrap, Seatbelt), Qwen Code (Docker, Podman, Seatbelt), Hermes (Docker, SSH, cloud) | v5.4 |
| Checkpoints: rewind the conversation and the files | Claude Code (`/rewind`), Qwen Code (`/restore`) | v5.4 |
| Memory the agent writes, and the person reads and edits | Claude Code (CLAUDE.md, `#`), Qwen Code (`save_memory`), Hermes (agent-curated memory) | v5.4 |
| Search across past sessions | Hermes (full-text, summarized), Claude Code (the resume picker) | v5.4 |
| Instruction files of other agents (AGENTS.md, CLAUDE.md) | Qwen Code, Hermes | v5.4 |
| `@file` in a prompt, `!command` straight to the shell | Claude Code, Qwen Code | v5.4 |
| Custom slash commands | Claude Code, Qwen Code | v5.4 |
| Custom sub-agents (their prompt, tools and model) | Claude Code, Qwen Code | v5.4 |
| Skills, loaded when needed | Claude Code, Hermes (agentskills.io) | v5.4 |
| MCP servers | all | had |
| Compaction of a long session | all | had (threshold and target) |
| A to-do list or plan | Claude Code, Qwen Code, dsh | had |
| Commands that run on (no time limit, watched) | Claude Code | had |
| Web search and pages | Claude Code, Qwen Code, Hermes | had (Arena MCP) |
| Sessions synced with a web chat | Claude Code (claude.ai) | v5.4 |
| Its own commits told apart from the person's | Claude Code (a co-author trailer) | v5.4 (Code Arena as the author) |
| Scheduled runs, chat platforms (Slack, Teams...) | Hermes | Arena has them (scheduled tasks, bots) |
| Hooks around tool calls | Claude Code | not in v5.4: the permission rules and the sandbox cover what hooks are mostly used for |
| A plugin marketplace, output styles, a status line | Claude Code, dsh (plugins) | not planned: MCP servers, commands and skills are the extension points |

## v5.5.0 — Argus's links sure and precise, credits per kind, a richer IDE (2026-10-11)

Asked on 2026-10-10, after v5.4.0, done one item at a time, each committed
and pushed when its tests passed:

- **"Cross-repo correlation is weak; make it stronger and more precise"**:
  exact names (no false hubs from roots, fixtures, strings or comments), each
  ecosystem's conventions, Python modules from their import roots, vendored
  copies by content, headers between projects left ambiguous, images by their
  project; then a link model: kind, layer, scope, a confidence from how the
  name matched, evidence (uses, the provider's declaration, candidates), a
  name several provide settled by what else the user has or kept as a
  candidate; the graph tools filtered by confidence, layer and tests, the
  surest paths, `change_impact` searched only where the change is used and
  `impact_of` through imports. On the generated estate of 200 repositories
  every link is found, none wrong, each strong.
- **Credits per kind and API access per person.**
- **Model states**: Unloading said while it lasts; models no longer push
  each other out.
- **Default models only from the installer.**
- **Code Arena never looks stuck**, and the next message always runs.
- **Every chat in step both ways.**
- **A richer IDE**: citations to lines, edits' lines, ask about code,
  completion, palette, Problems, Source control, replace.

## v5.6.0 — Picture, video and speech models managed like chat models (2026-10-11)

Asked on 2026-10-11, while v5.5.1 went live: "I want to be able to load and
unload and remove and config model of video/image and audio as same as LLMs
… I want setup them and set credit and other configs and dynamic loading".
Done one commit at a time, each checked on the live stack:

- The speech server leaves unloading to the app; installs that download can
  fetch speech models.
- The picture and video servers take their model and flags from a control
  file and report what they do.
- States and failures like the engine's: classes, backoff, why and since.
- Models as rows: add, edit, remove, their settings, price and idle time; one
  model per server at a time, switching by generation.
- Finding them: Hugging Face by pipeline, picked files; the speech registry.
- An API key's picture loads its model first.
- Settings name the chat's picture, video and speech to text models; the
  tools respect each model's access rule.

Left for later (its design is in the session's plan): one GPU plan for the
chat and picture models together (the chat engine's fit target leaving room
for a picture model beside it; a video model making room by unloading idle
chat models and giving it back), leases that keep a model from switching
under a running job, and a model menu in the chat's tools.

## Next (after v5.6.0)

1. **One GPU plan for chat, picture and video models**: done in v5.8 (the
   chat engine's fit target leaves room for a picture model kept beside it; a
   picture or video job short of memory makes room by unloading idle chat
   models and gives it back; jobs take turns and a model never switches under
   a running job). Left: a model menu in the chat's tools.
2. **Argus at run time**: what the build does not show. Service names to
   hosts (Kubernetes, Helm, compose, environment, Spring and .NET config, CI's
   helm and kubectl), gRPC (a .proto's services and options, server and client
   markers, generated code), topics and queues (Kafka, RabbitMQ, NATS, SQS and
   SNS, Service Bus), HTTP routes to calls and gateways, OpenAPI specs and
   generated clients, shared databases and tables, Backstage catalog files and
   Pact contracts as declared links. Each as its own layer, with its
   confidence, settled and shown like the build links.
3. **Argus's remaining precision work**: a .proto by its import root and the
   well-known ones left out; an image by its registry host; vendored copies and
   ambiguous #includes as evidence; more file types (lock files skipped,
   .mjs/.cts, CMake's FetchContent, Terraform modules); GitLab CI and deploy
   files read as YAML; co-change from the mirrors' history (never walked,
   shown as hidden coupling).
4. **Argus on the real estate**: measure the links on the company's 200
   repositories against what their owners know (`argus graph-eval`), and
   calibrate the priors.
5. **What each repository is for**, written once per repository by the model
   from its README, layout and key symbols, for `system_map` and `overview`.
6. **Code Arena**: a sandbox on Windows; a watchdog for an MCP call that never
   ends; the IDE's page showing the sandbox and the sync's state.
7. Carried over: the packs in the bucket (the maintainer), real platforms
   (Slack, Teams, SAML IdP, Confluence, SharePoint, Helm on a cluster), Code
   Arena's terminals on a real Mac and Windows, Laya's routing quality, deep
   research under six minutes.

## v5.7.0 and v5.8.0 (2026-10-16)

v5.7.0: Code Arena's IDE as VS Code (run and debug, compare, settings and
keys, formatting, language servers, VS Code extensions, a desktop window).
v5.8.0: long Code Arena sessions no longer stall, Code Arena and the web chat
in step by message ids, the GPU shared with picture and video, quoted
replies, compaction each person chooses, one dialog for every model, the
three runtimes kept alike.

## Next (after v5.8.0)

**v5.8.x, hotfixes** as the release is used: whatever the team finds, and the
bump of System.Security.Cryptography.Xml/Pkcs past today's advisory (needs a
yes: a package download).

**v5.9: code review, rooms and access control** (the person's asks of
2026-10-16) — released as v5.9.0 on 2026-10-20. Left for later: bulk access
changes with a dry run; invite links to rooms, mentions in the bell, room
export; posting nothing back stays; Code Arena's /review and Arena MCP review
tools, scheduled and automatic reviews, GitHub, Gitea and plain git.
0. Detailed access control per person and group, first, as the rest rests on
   it: admin roles in place of one admin flag (people, models, credits,
   auditor, Argus, rooms) and roles of one's own from named permissions;
   rules per resource (models, tools, MCP servers, plugins, repositories,
   rooms, reviews, knowledge, features) for people and groups, with deny and
   a stated precedence; one check every endpoint, Arena MCP, the gateway and
   Argus go through; Admin → Access with a "can X do Y on Z, and why"
   checker, bulk changes and an audit of every change; today's admins and
   group settings carried over as they are.
1. Merge request review: list a project's MRs (GitLab first, then GitHub,
   Gitea, any git repository), read the diff, commits, CI and discussions
   with Argus's read-only access, review with the model (each finding on a
   line, with its severity and fix, then checked by a second pass that tries
   to refute it), review again only what a new push added; a branch or a
   range of commits without an MR.
2. A review page in Arena: the diff, findings inline, each discussed,
   resolved or dismissed; posting back to GitLab only with the person's own
   token.
3. Rooms: a chat several people share (create, invite, roles, live, who
   wrote each, mentions), the model asked by any member at their own cost
   and under their own group's rules; a room per review or MR, and general
   ones.
4. The online code assistant: ask about a repository, branch or MR in the
   browser; explain, find, suggest a patch (a diff to download, an MR with
   the person's token, or Code Arena to apply it).
5. Code Arena: /review, a review opened locally, suggestions applied; Arena
   MCP tools to list and review MRs; notifications; scheduled reviews.
6. Code Arena's chat with every capability of the web chat (the parity
   design), as far as it is done.

**v6.0, part 1: any hardware, any engine, any runtime** (was v5.10; branch mgpu/engines, merged into main 2026-10-20)
1. Placement for any machine (CPU only to 8 GPUs, NVIDIA, AMD, Vulkan,
   Metal), vLLM beside llama.cpp, external servers (SGLang, Ollama, a cloud),
   several copies of a model, fallbacks, sleep instead of unload for media.
2. The admin's engine settings and per-GPU bars; the KV cache tiers (vLLM's
   CPU offload, then LMCache).
3. Docker Compose, Podman and Kubernetes alike for all of it.
4. Needs a yes: the vLLM image (NGC or built), AMD images. Needs the real
   server: the bring-up check, a smoke test per model, the numbers.

**v6.0, part 2: durable at scale** (was v5.11)
1. A fair queue for API keys (429 with Retry-After) and the engine's
   priorities; LiteLLM as several replicas with Valkey; answers that survive
   an app replica's restart.
2. Postgres point-in-time recovery (pgBackRest; CloudNativePG on
   Kubernetes) with a monthly restore drill.
3. DCGM and vLLM metrics, alerts; load tests at 50, 100 and 300 people.
4. Needs a yes: Valkey, pgBackRest.

**v6.0, part 3: Argus at run time and on the real estate** (was v5.12, carried
from after v5.6.0): service names to hosts, gRPC, topics and queues, HTTP routes,
OpenAPI, shared databases, Backstage and Pact as declared links; the
remaining precision work; `argus graph-eval` on the company's repositories;
what each repository is for.

**Later**: Code Arena's sandbox on Windows; real platforms (Slack, Teams,
SAML, Confluence, SharePoint, Helm on a cluster); Laya's routing quality;
deep research under six minutes; a model menu in the chat's tools.

**v6.0, part 4** (the person's asks of 2026-10-19; on 2026-10-20 they asked
that the next release be v6.0 with the rest of the plan in it: parts 1-3 above,
these, and v5.9's leftovers)
1. Operational roles: team manager (a group's manager: its people, their
   credit and limits, its models and tools, its reports), supervisor
   (monitoring: dashboards, alerts, logs, the stack's state, read-only), and
   others the Access page makes from permissions; operational duties handed
   to them (taking alerts, loading models, running backups, answering
   requests for access) with an audit of what each did. Builds on v5.9's
   access control.
2. A 3D model generator tool: Blender run headless in a sandbox of its own
   (no network, limits on CPU, memory and time), driven by the model with
   Blender's Python (and text- or image-to-3D models where one is loaded);
   the result (.glb, .blend, renders) shown in the chat with a 3D viewer.
   Needs a yes: Blender's image or package.
3. A planning tool: plans with steps, owners, dates and dependencies that the
   chat makes and keeps up to date, shown as a board and a timeline, shared in
   rooms.
4. Other providers in Code Arena: providers' APIs by key (Anthropic, OpenAI,
   Gemini, OpenRouter...) through the gateway; and a person's own
   subscription (Claude Pro/Max, ChatGPT Plus/Pro) only through the
   providers' own agents (Claude Code, Codex CLI, Gemini CLI) run inside Code
   Arena under the person's own sign-in (Agent Client Protocol), since the
   providers' terms do not let other apps use a subscription's credentials.

**v6.0, part 5** (the person's asks of 2026-10-20)
1. Rooms: reply to a message and @mention people and the model; the model
   answers only a message that mentions it or replies to one of its answers
   (the reply is the prompt, with the answer it replies to); people
   mentioned or replied to hear it in their bell.
2. Code Arena compacts before the answer's output would reach the model's
   window, not only when the prompt is full.
3. Code Arena takes a screenshot (the screen, a window, or the page it
   previews) and passes it to the model.
4. Access in far more detail: permissions per page and per action (each admin
   area's read, change and delete apart), per model, tool and knowledge
   source actions, rooms, reviews, plans and API keys.


## v6.0.0 — Any engine, durable, operational roles, access in detail (2026-10-21)

Released with parts 1-5 above. Still to do from it, carried below: the
vLLM image checked on a real GPU server (bring-up, smoke test per model,
numbers), the load tests at 50, 100 and 300 people there, DCGM's image
(unreachable from here); room invite links and export; bulk access changes
with a dry run; scheduled and automatic reviews; GitHub, Gitea and plain git
for reviews; `argus graph-eval` and what each repository is for; a 3D viewer
in the chat (three.js needs a yes).

## Next (after v6.0.0): v6.1 — Safeguards, plugins and tools, in one release

The person's asks of 2026-10-21: "much more effort on safeguards and plugin
system", "more customization on tools", all in one version.

**Safeguards**
1. Rules, not only switches: an ordered list of safeguard rules, each a
   detector (secrets, personal data by kind, blocked words and patterns, a
   denied topic the model judges, prompt injection and jailbreaks, file types
   and sizes, a document's classification label, links and domains), where it
   applies (questions, answers, files, tool calls and their results, the API;
   chat, rooms, Code Arena), to whom (everyone, groups, people) and with
   which models (a cloud provider stricter than the company's own), and an
   action (block, mask, warn and let the person go on, ask for a reason, log
   only, send to a safer model).
2. Answers checked too: secrets, personal data, blocked words and denied
   topics in what the model says, as it streams.
3. A monitor mode for every rule (it records what it would have done) and a
   test bench: paste a text, see which rules fire and why.
4. Events and review: every rule that fired, by whom, where and what it did
   (never the secret), with charts; a person may ask for a refused message to
   be reviewed, and an admin allows it, confirms it, or turns the rule into an
   exception.
5. Tools held: web domains allowed or denied, tool results read for
   injection, rate limits per tool and per person.

**Plugins**
1. A plugin brings more than tools: prompts, assistants, skills (Code Arena
   and the chat), safeguard rules and detectors, scheduled tasks, and its own
   settings page; its manifest says which permissions it asks for, and the
   admin grants them at install.
2. Versions and updates: a plugin's updates from its catalog, shown with
   what changed and what new permissions they ask for; roll back to the
   version before; pin a version.
3. Per group: which groups have a plugin, and its settings per group.
4. Health and logs: each plugin's calls, errors and time, its server's state,
   a check now; a plugin turned off when it keeps failing.
5. A plugin's code run safely: an MCP server shipped in the plugin runs in
   the sandbox (no network but its own hosts, limits), so a plugin need not
   be hosted elsewhere.
6. A starter template and a check for authors (`arena plugin check`).

**Tools**
1. Each tool's functions apart: on or off, asking first or not, per
   function.
2. A tool's name, description and instructions to the model overridden by
   the admin; its arguments' defaults and fixed values (the web's domains,
   Python's time, a picture's size, which model draws).
3. Settings per group: a tool on, off, on by default or asking first for one
   group and not another; quotas per tool.
4. Toolsets: named sets of tools a chat, an assistant or a group starts
   with.
5. Tools made without code: an HTTP request tool from a form, and a prompt
   tool (a prompt run on a model of the admin's choice), tested from the
   page.

## v6.1.0 — Safeguards, plugins and tools (2026-10-21)

Released with the plan above, except: a plugin's own MCP server run in the
sandbox (a plugin still names a server hosted elsewhere), plugin settings per
group, and plugins that bring skills or scheduled tasks. These, and v6.0's
leftovers, are what comes next.


## v6.2.0 — Automatic reviews, GitHub and Gitea, plugins that run their own server (2026-10-21)

Released with every item of its plan: room invite links and export; access
for many people with a dry run; automatic and scheduled reviews; GitHub and
Gitea pull requests through Argus with each person's own token; `argus
graph-eval` and what each repository is for; plugin settings per group,
plugin tasks and Code Arena skills, a plugin's own MCP server in the
sandbox; people load tests at 50, 100 and 300 (testing.md, S12), which found
the sign-in flood guard too tight for an office behind one address (now 600 a
minute). Left: vLLM on a real GPU server (none here), and what is below.


## Next: v6.3 to v6.8 — a headline in every release

Asked 2026-10-21: the features had become plumbing; each release should have
something worth showing. So each release below has a theme, two headline
features that can be shown in a minute, and smaller ones that go with them.
Groundwork (the list at the end) goes in between, not instead. One item at
a time, each tested, committed, pushed and deployed. A download (an image, a
model) waits for a yes, with its name, source and size.

Released: **v6.3.0** (2026-10-21), all three items below.

### v6.3 — "Show me": the model sees what you see

1. **Show your screen.** In Talk or in a chat, **Share screen** (a window, a
   tab or the whole screen, as the browser offers): each question takes what
   is on it now and goes to a model that sees. "What is wrong with this
   chart?", "Which button do I press?", a spoken walk through a dashboard.
   Nothing is recorded but the frames sent, kept with the question; a frame
   the safeguards' file and PII rules refuse is not sent.
2. **Napkin to app.** A whiteboard in the chat: draw with the mouse, a pen
   or a finger, or photograph a paper sketch, and ask: the model turns it
   into a running preview (a page or a React component), a diagram
   (Mermaid), a 3D scene, or a table. Draw over the result to change it.
3. **The browser agent.** A chat tool that drives a real browser in its own
   container (Chromium, no access to the stack's network, sites only through
   an allow-list proxy): it opens pages, reads, clicks and types to do a
   task, while the chat shows its screen live; the person can take over at
   any moment. It asks first before anything that sends a form, buys, posts
   or deletes, and never enters a password or a card (the person does).
   Needs a yes: the Playwright image (mcr.microsoft.com/playwright, about
   2 GB).

Released: **v6.4.0** (2026-10-22), all three items below, except: an app's
`arena.ask` has no tools, and an app or a board is shared with groups, not
named people; Ask your data connects PostgreSQL only (MySQL and SQL Server
need their drivers, a download); a deck's PDF is printed by the browser.

### v6.4 — Apps: what the chat builds, people use

1. **Arena Apps.** A preview published as an app at its own link in the
   company: a gallery, versions, **Remix** to make one's own. An app may ask
   the model (`arena.ask`, as the person using it: their credit, their
   safeguards, their tools) and keep its data (`arena.store`, per app,
   shared or per person). Who may open it: everyone, groups, people.
2. **Ask your data.** Read-only databases an admin connects (PostgreSQL,
   MySQL, SQL Server; who may ask which tables): a question becomes SQL,
   shown and explained, run as a read-only role, and answered with a table
   and a chart. **Pin** a chart to a dashboard that refreshes on its
   schedule and can be shared to groups or posted to a room.
3. **Decks.** "Make a deck about…": slides with the company's look (an admin
   uploads a template), charts from the data and pictures from the picture
   model, shown in the browser, changed by asking ("a slide on costs after
   the third"), downloaded as .pptx and PDF.

Released: **v6.5.0** (2026-10-22), all three items below, except: Agent
Studio's canvas is a flow from the top down (branches and loops), not
free-form nodes, and shares with groups, not named people; Fix it is GitLab
only (GitHub needs the host's write API and a token of that kind), and runs
tests only in the Python sandbox (other languages need a code sandbox image,
a download).

### v6.5 — Agents that do the work

1. **Agent Studio.** Workflows on a canvas: a trigger (a schedule, a
   webhook, a GitLab event, a room message, a form), steps (ask a model, a
   tool, a condition, a loop over items, a person's approval), and outputs
   (a room, an email, a merge request comment, a file). Every run kept with
   a timeline to replay it step by step. Templates from the tasks and
   reviews people have today.
2. **Fix it.** On a review finding, an issue or a failed pipeline: an agent
   takes the code in the sandbox, makes the change, runs the tests, and shows
   the diff; with the person's own linked GitLab or GitHub token (never
   Argus's), it opens the merge request as them.
3. **Panel.** Several assistants in a room discuss a question from their
   roles (the expert, the critic, the one who says no), the moderator writes
   the verdict with who said what.

Released: **v6.6.0** (2026-10-22), all three items below, except: figures
that move (a walk, a wave) are not made yet; a voice from a sample speaks
English only (Chatterbox's English model; its multilingual one is a further
2.1 GB); a painted change redraws the painted part with what is around it
and blends it back (FLUX.2-klein's masked inpainting ignored the mask).

### v6.6 — Studio: pictures, video and 3D, finished

1. **Video studio.** From a script or a question: a storyboard, each shot
   from the video model, a narration in the person's voice, captions and a
   music bed, cut into one MP4; any shot made again on its own.
2. **Image studio.** Edit a picture by painting over the part to change
   (FLUX.2's editing with references), make variations, enlarge, take the
   background away, and make a picture move (a short video from it).
3. **3D, made real.** STL and 3MF for printing, a turntable video of any
   model, and figures that move (a walk, a wave) exported with their
   animation.

Released: **v6.7.0** (2026-10-22), all three items below, except: SVG is not
yet optimized beyond what making it safe does (decimals to three places,
nothing that does not draw); frames come as a sprite sheet (one PNG), not a
folder of files; a drawing the model writes itself is only as good as the
chat model (an illustration, traced, is the high-quality route).

### v6.7 — Vector and motion

The person asked for this release to be about high-quality SVG and animated
SVG, and nothing else.

1. **SVG you would ship.** Drawn by the model from words (icons, diagrams,
   scenes with named parts), or from a picture: the picture model draws it
   flat, and it is traced into clean vector shapes (vtracer), layered by
   colour. Every SVG is made safe on the way in (no script, no outside
   references) and checked: the model sees its own drawing rendered and
   fixes what is wrong. Changed by asking; its code editable with a live
   preview; optimized.
2. **Animated SVG.** Motions that always work (fade, slide, pop, spin,
   float, bounce, sway, pulse, wiggle, draw the lines on, colour shifts),
   and keyframes of one's own, compiled to CSS inside the SVG. Animate by
   words ("the wheels turn, the cloud drifts"), or pick a part on the
   preview and give it a motion, with a timeline: play, pause, scrub, loop.
3. **Out of the studio.** An animated SVG as a GIF, an MP4, a WebM or
   frames, rendered by the stack's own Chromium; SVG and PNG at any size;
   and the chat's tools draw and animate SVG too.

Released: **v6.8.0** (2026-10-23), all four items below, except: the IDE's
Review view reviews local changes and branches (a merge request's review,
the automatic reviews and Fix it stay on the Code page); a business map is
made on request, not again as Argus indexes; a server workspace's network
reaches the internet at large, not only the code host and the package
registries.

### v6.8 — Code Studio, and Argus the business's technical mind

The person asked (2026-10-22) for the Code section to become Code Studio, an
IDE like VS Code in the browser, with the code arena's whole feature set,
review inside it, and Argus as the business's technical mind and senior
expert that explains the business, not only the code. Chosen: workspaces on
the server and on one's own machine; Argus learns from the code and the
repositories' documents, GitLab's issues, merge requests and wikis, and the
company's knowledge.

1. **Code Studio.** Workspace → Code Studio (`/code`): open a GitLab project
   and branch in a workspace on the server, or connect one's own machine
   (Code Arena's CLI), and work in the IDE Code Arena already is — editor,
   explorer, search, source control, terminals, tasks, run and debug,
   language servers, extensions, settings and keys, the agent's chat. A
   server workspace runs Code Arena's own engine per workspace in an
   isolated service (each its own user, limits, no Docker socket; network
   only to the code host and package registries), cloned and pushed with the
   person's own token; idle ones sleep and wake. Its home lists one's
   workspaces, recent reviews and Argus.
2. **Review in the studio.** Review one's changes, a branch or a merge
   request from the IDE: findings as comments in the editor and in
   Problems, each with its fix to apply; the automatic reviews and fixes
   there too.
3. **Argus, the business's technical mind.** Argus learns what the code is
   for: each system's domain, capabilities, entities, rules, processes and
   integrations, a glossary, from the code and its documents, GitLab's
   issues, merge requests and wikis (read only), and the company's
   knowledge; kept fresh as it indexes. It answers as a senior expert who
   knows the business — "what happens when a customer cancels an order",
   "why does billing round this way", "who uses this service" — traced
   through the code with its sources, in the studio and in the chat.
4. **Skills** (asked 2026-10-22: "usage like skills in Claude Code"). The
   prompt library becomes skills: a slash name, when to use it, instructions
   with declared parameters (`{{name}}`, `$ARGUMENTS`). `/name` puts the
   skill above the box with a field for each parameter and sends it as a
   skill, not as pasted text; the model also loads one on its own when a
   question fits (`use_skill`). A plugin's skill folders (`SKILL.md`) are the
   chat's slash commands and Code Arena's skills alike.

Released: **v6.9.0** (2026-10-25), all three items below, except: a call's
sound goes page to page (WebRTC) and is not recorded, so a call is not made
a meeting yet; push notifications came ahead, in v5.x.

### v6.9 — Everyone, in every language

1. **Rooms in every language** (reading a room in one's language came
   ahead, on main after v6.6.0). Each member reads the room in their own
   language and writes in theirs; the others read it in theirs, the original
   a click away. Voice in rooms with live captions, translated too.
2. **Meetings.** Record one in the browser or upload it: who spoke when, the
   transcript, the decisions and the action items, each made a task with its
   owner and a reminder; then ask the meeting. Needs a yes: a speaker
   diarization model.
3. **Arena on the phone.** Installable (PWA): share a file or a page from
   any app to a chat, push notifications, Talk on the lock screen's
   shortcut, works with a poor connection (what is sent waits).

### v7.0 — Memory: what the models remember, and what is in their window

Released: **v7.0.0** (2026-10-25), every item below, and beyond them, as the
person asked along the way: very long chats and sessions (their own earlier
messages read back by number, a long chat opened at its end), a project's
long memory in Code Arena's `.remember/`, skills that learn from the work
(learned, made better with Undo, lessons from answers marked bad, Code
Arena's `skill_save`), and Code Arena delivered by the Arena (one-line
install, built by every deploy, self-update). Still to do from it: Code
Arena's pin and leave out per message (the web chat's are done), and the
garden for the company's memories.

The person asked (2026-10-25) for a pivot: a large effort on memory
management and planning in both Code Arena and Argus Arena — memory meaning
the model's context and its memory — and for the most useful and popular
skills to come with both. v7.0 is memory and the window; v7.1 is planning.

What is there to build on: the chat keeps up to 100 one-line memories a
person (the newest few hundred tokens go into every prompt; the model offers
new ones, kept when the person accepts) and compacts a long chat into a
summary; Code Arena keeps a person's and a project's memory as Markdown
files on the machine (all of it in the prompt at a session's start),
`search_sessions` finds words in earlier sessions, and it compacts at a
share of the window (summaries, then a turn's own steps, then long results
cut).

1. **One memory, everywhere.** Memories in Arena's database for the chat and
   Code Arena alike, each with its scope — the person (every chat and
   project), a project (a repository: Code Arena's sessions in it, and the
   people who may read it in GitLab when its owner shares it), or the company
   (kept by admins, read by everyone) — its kind (preference, fact,
   decision, how-to, lesson), where it came from (a chat, a session, a
   meeting, and the message), when it was last used and how often, pinned or
   not. Hundreds a person, not 100: the prompt takes only what matters.
   Code Arena reads and writes the same memories through Arena (its
   `memory.md` files imported once, then a cache for when Arena is out of
   reach, written back when it answers).
2. **Recall by relevance.** Before each answer the memories that matter to
   it — nearest the question and the last turns by meaning (the knowledge
   embedder; words and recency without it), the pinned ones always — go to
   the model within a budget, after the cached start of the prompt; `recall`
   searches all of memory when the model needs more. Under each answer:
   which memories it used, each a click away.
3. **Memories that form themselves.** When a chat goes quiet, a Code Arena
   session ends or a compaction happens, the model for small steps reads
   what is new and proposes memories: preferences, decisions, how the
   project builds and tests, and lessons (what the person corrected).
   A proposal like one kept is merged into it; one that contradicts an older
   one replaces it (the older kept as replaced). In the chat the person
   accepts them (or turns on "remember on its own"); in Code Arena a
   project's are kept and said.
4. **The memory garden.** Now and then, per person and per project:
   near-duplicates merged, what was replaced or unused for months let go,
   long ones tightened; every change listed under Memory → History, each
   with Undo.
5. **The Memory page.** Workspace → Memory: search, by scope, kind and
   project; pin, edit, forget; where each came from and when it was last
   used; History; Markdown in and out. Code Arena's `/memory` lists and
   edits the same, and the IDE has a Memory panel.
6. **The window, managed.** The same in the chat and Code Arena:
   - **What fills it**: the instructions, the tools, the memory, the plan,
     the conversation and the tools' results, each with its tokens, the
     biggest items listed, each with Pin, Leave out and Summarize. The
     chat's gauge opens it; Code Arena's `/context` draws it; the IDE shows
     it.
   - **Big results kept aside**: a tool's result past a few thousand tokens
     stays out of the window; the model reads its start, its size and a
     handle, and `read_result` reads any part of it again.
   - **Clearing before compacting**: as the window fills, old results
     (before the last few turns) go first, each call kept with a line of
     what it returned (readable again); compaction only when that is not
     enough.
   - **Compaction that keeps the thread**: the summary in sections (goal,
     constraints, decisions, files, state, next steps, open questions); the
     plan, the pinned messages, the memories in use and the files being
     changed come back after it on their own; the person sees what it kept.
   - **Pin and leave out**: a message pinned is kept word for word through
     compactions; one left out is not read by the model, and not deleted.
7. **Past work, findable.** Each chat and Code Arena session gets a digest
   when it goes quiet (what was asked, done and decided, the files, how it
   ended), searched by meaning: `search_history` finds past chats and
   sessions, the web's and Code Arena's together ("what did we decide about
   retries last month?"), and opens them.
8. **Skills that come with Arena.** The skills people use most, built in for
   the chat and Code Arena alike (with parameters, used by / or by the model
   on its own): review code, security review, write tests, debug
   systematically, refactor safely, explain code, commit message, merge
   request description, release notes, document a project, plan a change,
   brainstorm, summarize a document, meeting notes, write an email or a
   memo, analyse data, make a deck, translate keeping the terms, explain and
   speed up a SQL query, review an API, incident postmortem. Our own words;
   an admin turns any off, and a person's or a project's skill of the same
   name wins.

### v7.1 — Compliance: ISO 27001, SOC 2, HIPAA and GDPR, built in and kept

The person asked (2026-10-26) to cover ISO/IEC 27001:2022, SOC 2 (the Trust
Services Criteria), HIPAA (the Security and Privacy Rules) and the GDPR in
this release, documented, and kept from breaking in every release after it.
Planning and code mode move to v7.2.

A framework is met by an organisation, not a program: Arena gives the
technical controls, the evidence and the documents an auditor asks for, and
says plainly what is the operator's part (policies, training, physical
security, contracts such as a HIPAA BAA, the data protection officer). What is
there to build on: sign-in hardened (password strength, lockouts, 1-hour idle
and 12-hour absolute sessions), two-factor, OIDC, SAML and SCIM, groups and
permissions, the audit log, retention and legal hold, a person's data
exported (by them or an admin), safeguards on answers, Python and commands in
sandboxes, backups with restore drills, alerts, and `audit-auth.sh`. The gaps:

1. **The Compliance center** (Admin → Compliance): each framework's controls
   (Annex A, the TSC, 45 CFR 164.308–316, the GDPR's articles) with what
   meets each in Arena, its live state from automated checks (red, amber,
   green), the operator's part to attest (with who and when), and the
   evidence an auditor asks for as one download (settings, the checks'
   results, the audit log's proof, access reviews, policies' versions).
2. **Two-factor required**: for everyone, for admins, or for chosen groups;
   who has none enrols at their next sign-in; recovery codes; the
   directory's and SSO's own MFA counted when the provider says so.
3. **An audit log nobody can change unseen**: each entry chained to the one
   before (SHA-256), a daily anchor kept apart, a check that walks the chain
   (and alerts when it breaks); sent on as it happens to a SIEM (syslog,
   or a webhook); kept at least six years (HIPAA's documentation rule);
   reads of another person's data (an admin's export, a shared chat opened)
   audited as changes are.
4. **Access reviews**: every quarter (or on demand), each privileged person
   and group membership listed for a reviewer to confirm or revoke, with the
   record kept: who reviewed, what changed, when. Accounts unused for 90
   days flagged, and disabled when the company says so.
5. **Data subject requests** (GDPR 15–22): a request taken in (by the person,
   or an admin for them) with its 30-day clock; access and portability (the
   existing export, machine readable), rectification, restriction (no memory,
   digests or learning from their work), objection, and erasure that reaches
   everything of theirs (chats, files, memories, digests, skills, the
   gateway's logs, traces) with a certificate of what was erased and what
   ages out of backups by when. Each step in the audit log.
6. **Retention for everything**: logs (Loki, now kept forever), traces,
   the gateway's request logs, the audit log, backups, metrics: each with
   its period in Settings, defaults that meet the frameworks, and the jobs
   that enforce them shown in the Compliance center.
7. **Encryption, said and checked**: TLS 1.2 or later outside (and the
   ciphers checked), backups encrypted (pgBackRest's cipher, the key kept
   apart), files at rest encrypted with `APP_KEY`, and the host's disk
   encryption attested; HSTS and the security headers checked live.
8. **Personal and health data kept in bounds**: detectors for identifiers
   (national IDs, medical record numbers, cards, IBANs, emails, phones, dates
   of birth) in the safeguards, to warn, mask or block before a question goes
   to a model, stricter for models outside the company; no personal data in
   the app's logs and traces (scrubbed, and tested).
9. **Notice and acknowledgement**: a sign-in notice (acceptable use, the
   privacy notice) whose version each person acknowledges, kept.
10. **Security monitoring and incidents**: alerts for repeated failed
    sign-ins, an admin granted, a mass export, the audit chain broken, a
    backup failed, a certificate near its end; an incident register with the
    GDPR's 72-hour clock and HIPAA's breach steps, from alert to closure.
11. **Supply chain**: every release with its SBOM (CycloneDX), its
    dependencies scanned for known vulnerabilities (.NET, npm, Python, the
    images), and checksums; a release with a critical finding does not ship.
12. **The documents**: `docs/compliance/` with the shared-responsibility
    model, a mapping for each framework (each control: what meets it, where
    it is documented, the tests and the live check that prove it), the data
    flows, a record of processing (GDPR 30) and a DPIA (35) to fill in, an
    incident-response runbook, and policy templates (access control,
    backup, retention, encryption, change management).
13. **Kept in every release**: `docs/compliance/controls.yaml` names, for
    each control, the feature, the documentation and the tests and live
    checks that prove it. A test fails when a control names a test that does
    not exist or a document section that is gone; the tests that prove the
    controls carry the control's id and run in every release;
    `deploy/scripts/compliance-check.py` checks the live stack (TLS, headers,
    cookies, two-factor policy, the audit chain, backups' age and
    encryption, retention in force, logs' retention); the release checklist
    and the release tests require it, and a release whose checks fail does
    not ship. A change that weakens a control says so under its own heading
    in the changelog.

### v7.2 — Planning and code mode: plans the agents make and prove, and tools called from code

Released as v7.2.0 (2026-10-26), every item below built; CHANGELOG.md has what
it does. Left for later: the graph questions in the tool-modes measurement
(they need a measured person with GitLab rights), and tools on demand that a
model in direct mode passes over (it answered from memory instead of loading
them).

1. **The plan as the agent's working state.** `plan` (make and change)
   instead of Code Arena's to-do list: a goal, and steps each with what
   "done" means (its check), the files it touches, what it waits for, its
   status and notes. Shown as it changes (the terminal, the IDE's Plan
   panel, beside the chat), kept through compactions (it is always in the
   window), stored with its chat or session and on the Plans board.
2. **Plan mode, finished.** In plan mode the agent investigates, then
   proposes a plan the person reviews step by step: approve, change a step,
   ask for another way, or reject. Approved, it carries out that plan.
3. **Grounded in the code.** Argus's impact of a change — the symbols and
   files it touches, their callers in every repository, the tests that cover
   them — goes into the plan, each step citing the code it changes.
4. **Critique before acting.** A second reading of the plan with fresh eyes
   (another model, or the same in a new context): what is missing, what is
   risky, what has no check. Its notes are shown and folded in.
5. **Carry out and prove.** Step by step: a checkpoint before each, its
   check run after it (build, tests, a command), a failed check fixed or the
   plan changed with the reason; nothing is "done" that was not checked.
6. **Long tasks across sessions.** A plan outlives its session: the next
   session (or `code-arena --plan`) picks it up with its progress notes;
   plans in progress are listed in the web; a scheduled task can carry one
   on.
7. **Steps at once.** Steps that wait for nothing run together: research by
   read-only sub-agents in parallel, changes in their own git worktrees,
   merged back with their checks.
8. **Plans in the chat.** For multi-step work that is not code (research, a
   document, an analysis) the chat proposes a plan, the person approves it,
   and it is carried out with the tools, each step shown.

**Code mode.** The person asked (2026-10-25) to weigh code mode for both
products: rather than calling tools one by one, each call and its whole
result passing through the model's window, the model writes a short program
that calls the tools as functions, and only what the program prints comes
back. The case for it, from those who measured it:

- Anthropic's [code execution with MCP](https://www.anthropic.com/engineering/code-execution-with-mcp)
  presents MCP servers as code to explore (a file of functions per tool, read
  when needed): one workflow went from about 150,000 tokens to 2,000.
  Cloudflare's Code Mode reaches a similar finding (models write code better
  than they make long chains of calls; 2,500 API endpoints behind two tools).
  Anthropic's API has it as programmatic tool calling (tools callable from its
  code execution, results filtered before they reach the window).
- Measured by others on mixed tasks: input tokens down 78% (15,417 to 3,310)
  with the same success, output tokens up (the code), latency a little up
  ([AIMultiple](https://research.aimultiple.com/code-execution-with-mcp));
  37% fewer tokens on complex research in Anthropic's managed form. The gain
  is large with many tools or long chains of calls carrying big payloads, and
  small for one or two simple calls.

Why it fits here: Argus alone is about 5,000 tokens of definitions, a person
may add plugins and MCP servers, and the chains Argus answers with (search,
then read files, then follow references) carry results the model only needs
to filter. What there is to build on: the chat's Python sandbox (no network),
tools on demand (definitions sent whole only once loaded), results kept aside
and read again (v7.0), sub-agents, and Code Arena's sandbox and shell. The
risks: a weaker model writes wrong code (so direct calls stay), code in a
sandbox that reaches tools needs a broker that keeps every right and every
"ask first", and more output tokens. The plan:

9. **Tools as code, one bridge for both.** Every tool an answer may use (the
   built-in ones, Argus, plugins, MCP servers, Arena's own) as a Python
   module generated from its definition: a function per tool with typed
   parameters and its description as the docstring, grouped by source
   (`arena.argus.semantic_search(...)`), found with `arena.tools.search("…")`
   and `help(...)` rather than all of them read up front. A call from code
   goes to a broker, as the person: their rights, a tool that asks first
   asks (once for the program, with what it will call), a budget of calls per
   program, each call shown nested under the program and audited as a direct
   one is.
10. **`run_code` in the chat.** Python in the existing sandbox (no network
    but the broker's socket): the program calls tools, loops, filters and
    joins, and only what it prints (and the files it writes) comes back, cut
    and kept aside past a size. For research over the code (every caller of
    a symbol across repositories, then what each does) and data work that
    reads many results.
11. **Code mode in Code Arena.** The same module in its sandbox (a socket to
    a bridge in Code Arena with the session's own key): its local tools
    (files, search, git), Arena's and Argus's MCP tools and the person's MCP
    servers, callable from one `run_code` in Python, beside the shell it has.
12. **Arena MCP in code mode.** For agents that connect to Arena MCP (Claude
    Code, Qwen Code, Codex...): two tools, `search_tools` and `run_code`, in
    place of every definition, chosen per key on Connect your tools.
    *As built:* at `/mcp/code`, chosen per agent by its address (a person has one
    key, and may want two agents each its own way): a switch on Connect your
    tools puts it in the setups ([mcp.md](mcp.md#tools-as-code)).
13. **When to use which.** Per model (Models: tools direct, as code, or both):
    both for the strong models, direct for the small ones; the system
    prompt teaches the pattern when code is on. Direct calls stay for one or
    two simple calls.
    *As built:* Admin → Models → Tools; automatic is direct for the model for
    small steps and names that say 8B or fewer, as code for the rest (item 14's
    numbers); the chat and Code Arena (its chat config, `toolMode` in
    config.json last) follow it.
14. **Measured before it is the default.** The Argus task families and the
    graph eval in both modes on our own models: tokens in and out, success,
    time. The release says the numbers, and the defaults follow them.
    *As built:* `evals/run_tool_modes.py`, 8 task families and two six-lookup
    tables through the chat ([tool-modes.md](measurements/tool-modes.md)):
    Qwen3.8-27B 10/10 as code, 6/10 both, 4/10 direct; Qwen3-4B 1/10 either
    way. The graph questions wait for a measured person with GitLab rights.

### v7.3 — A model of your own

1. **Model lab.** Teach a small model the company's words on the local GPU
   (a LoRA from chosen knowledge sources, prompts and good answers), then
   compare it with the model it came from in blind battles, and give it to
   people with one click. Needs a yes: the training libraries.
2. **Evals.** Test sets for assistants, prompts and models, run on every
   change (a new model, a new prompt), scored by rules or a judge model,
   with what got worse shown first.

### v7.4 — Security: vulnerabilities known, found and fixed

The person asked (2026-10-26) for a knowledge pack about vulnerabilities, and
for recognising them and fixing them. Three parts: what the models know (two
packs), what finds the vulnerabilities (scanners and the model's review, each
grounded in the packs), and what fixes them (a change that is proved, then
checked again). What is there to build on: Argus's packs (`docs_lookup` by
exact name, `docs_search` by meaning), the code index with references, change
impact and the graph, the `security-review` skill, code review and automatic
reviews, Fix it, Code Studio, the sandbox with no network, and v7.1's scan of
Arena's own dependencies.

1. **The `security` pack**, one composite, each source's licence recorded
   (`argus pack info` prints the attribution):
   - **CWE** (MITRE's XML, free to use with its notice): each weakness with
     its description, consequences, how it is detected, mitigations, the
     demonstrative examples (the weak code and the fixed) and the CVEs seen;
     `CWE-89` a symbol; the Top 25 and the views by language as facets.
   - **CAPEC**: the attack patterns, linked to the weaknesses they use.
   - **OWASP** (CC BY-SA 4.0): the Cheat Sheet Series (how to fix, by topic
     and framework), ASVS 5.0 (each requirement a symbol, `V5.3.4`), the
     Top 10s (web, API, LLM applications) and the Web Security Testing Guide.
   - **CodeQL's query help** (MIT): per language, what each query
     recognises, the recommendation, and the bad and good examples, with
     their CWE tags. The help only: the CodeQL engine's licence does not
     cover scanning private code.
   - **The languages' own rules**: .NET's security analyzers (CA2100,
     CA3001…), Bandit's checks, gosec's, ESLint's security plugin; each rule
     a symbol, linked to its CWE.
   Not taken: Semgrep's registry rules and the SEI CERT wiki (their terms do
   not allow redistributing them), and the CIS benchmarks.
2. **The `advisories` pack, renewed every day**: known vulnerabilities in
   packages, from the GitHub Advisory Database (CC BY 4.0: npm, NuGet, PyPI,
   Maven, Go, crates, RubyGems, Composer), OSV, NVD's CVE records and CISA's
   Known Exploited Vulnerabilities. Each advisory a document (`GHSA-…` and
   `CVE-…` as symbols), and a table of the packages and version ranges it
   affects, the versions that fix it, its CVSS score, its CWE and whether it
   is exploited, so matching needs no model. Built on a connected machine
   each day, published in a pack index with its SHA-256, taken by `argus pack
   update` (and carried to an offline host by `airgap.sh`); Arena shows its
   age and warns past a week.
3. **Recognising vulnerable dependencies.** Argus reads each indexed branch's
   manifests and lock files (npm, pnpm and yarn locks, NuGet's
   `packages.lock.json` and `PackageReference`, pip requirements, Poetry and
   uv locks, `go.sum`, `Cargo.lock`, Maven, Gradle, `Gemfile.lock`,
   `composer.lock`) and matches them against the advisories: a tool,
   `deps_check`, answers as the person, within their GitLab rights. Each
   finding says how the package comes in (directly, or through which), the
   version that fixes it and the smallest upgrade. A CycloneDX SBOM per
   repository and branch, to download. Checked again whenever the advisories
   are renewed, so a new CVE in old code is found without a push.
4. **Recognising weaknesses in code**, two passes that check each other:
   - **Scanners**, in the sandbox with no network, on a checkout of the
     indexed commit: opengrep (LGPL-2.1) with Arena's own rules (written
     here, under the app's licence, each tagged with its CWE and tested on
     its bad and good examples), Bandit, gosec, the .NET analyzers, ESLint's
     security plugin, gitleaks for secrets (the history too), and Trivy for
     images and infrastructure as code (Dockerfiles, compose, Helm,
     Terraform; its database carried as the advisories are). Their SARIF
     merged, duplicates folded.
   - **The model's review**: `security-review` grown into a skill that walks
     the code with Argus, from the entry points (routes, handlers, commands,
     queue consumers, found through the graph) to the sinks (SQL, commands,
     paths, deserialisers, templates, outbound requests) through
     `find_references`. Each suspicion is checked against the pack's CWE page
     and the scanners' rules, and dropped when the line where untrusted data
     reaches the sink cannot be cited. A finding gives the CWE, severity,
     `file:line`, the data's path, how it could be exploited, the fix and the
     pack's pages it rests on.
   - **Triage**: whether both passes found it, one, or neither; a finding
     people mark as wrong is remembered for that repository, and the
     reflection learns from it (v7.0's skills that learn).
5. **Fixing.** **Fix it** on a finding, in Code Studio or in Code Arena on
   the person's machine: the agent writes a test that shows the weakness
   where it can (a request that injects, a path that escapes), makes the
   change the cheat sheet and the CWE's mitigations describe (a parameterised
   query, not an escaped string), runs the tests, runs the scanners and the
   review again on the change, and offers the patch, a branch and a merge
   request opened as the person with their own GitLab sign-in (Argus's token
   stays read only). For a dependency: the smallest upgrade that fixes it,
   the lock file updated, the build and the tests run. Many findings at once:
   one branch each, for people to take or leave.
6. **Where people see it**:
   - **Admin → Security**, and each repository's page for those who may read
     it: findings by severity with the known-exploited first, what is new
     this week, what was fixed, risks accepted (by whom, why, and until
     when), and the SBOMs.
   - **The chat and Code Arena**: "is this repository affected by
     CVE-2026-…?", `/security-review`, and findings in Code Arena's Problems
     panel with Fix it beside each.
   - **Automatic reviews**: a merge request that adds a finding or a
     vulnerable package says so in its review.
   - **Alerts**: a new critical or known-exploited finding reaches the
     repository's owners and the security team.
7. **Measured, as Argus was.** Recognising: on the Juliet test suite (NIST
   SARD, public domain: C, C++, Java, C#) and the OWASP Benchmark (Java),
   precision and recall per CWE for the scanners alone, the model alone, the
   model with the pack, and both passes together. Fixing: on real CVE fixes
   (CVEfixes, its licence checked first), the share fixed with the tests
   passing and the scanners clean. Published in `docs/measurements/`; a
   release that makes either worse says so.
8. **Compliance**: the controls it adds to `controls.yaml`: vulnerability
   management (ISO 27001 A.8.8, SOC 2 CC7.1, HIPAA 164.308(a)(1)(ii)(A) and
   (B), GDPR 32(1)(d)), secure coding (A.8.28) and security testing (A.8.29),
   each with its tests and live checks (the advisories' age, findings past
   the deadline their severity gives).

In this order: the `security` pack and the grounded review first (a headline
on its own), then the advisories and dependencies, the scanners, Fix it, and
the measurements. Needs a yes before downloading: the CWE and CAPEC files, the
OWASP and CodeQL repositories, the advisory databases, opengrep, Bandit,
gosec, gitleaks and Trivy for the sandbox image, the Juliet suite and the
OWASP Benchmark; each one's size said when asked.

### Groundwork, in between

1. The vLLM image on a real GPU server: bring-up, a smoke test per model,
   numbers, and the people load tests there; DCGM's image.
2. `argus graph-eval` with the owners' links of the company's repositories,
   and the priors calibrated from it.
3. Reviews: plain git repositories, comparing two refs on Gitea, and
   GitHub's and Gitea's webhooks for automatic reviews.
4. The chat's default model with more answers at once, or a second copy,
   where people wait for it.
5. Carried: Code Arena's sandbox on Windows; real platforms (Slack, Teams,
   SAML, Confluence, SharePoint, Helm on a cluster); Laya's routing quality;
   deep research under six minutes.
