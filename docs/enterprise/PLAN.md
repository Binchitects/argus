# Enterprise solution: one app at `llm.<domain>`

Branch: `enterprise-solution`. When every phase below is done and green, the repo is
reorganised around the new app, the legacy services are deleted, and the branch
becomes `main`.

## Target

```
https://llm.<domain>
└── app  (ASP.NET Core, .NET 10 LTS — one container)
    ├── React frontend (Vite + TypeScript), served by the backend
    │     chat · dashboards · usage & cost · admin · Argus
    ├── Identity: ASP.NET Identity + OpenIddict (own OIDC provider)
    │     local accounts + LDAP / Active Directory · TOTP 2FA · API keys
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

Status: **Phase 3 built and tested; waiting for your sign-off** ([checklist](PHASE3-CHECKLIST.md)).
Then Open WebUI goes and `enterprise-p3` is tagged. Next: Phase 4.

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

### Phase 6 — Cutover  *(S)*
- Delete the legacy services and their config; reorganise the repo around the app.
- Rewrite the README and docs; one `.env` sample per hardware setup still works.
- Full from-zero deploy plus every test suite; tag a major release; merge to `main`.
