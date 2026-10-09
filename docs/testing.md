# Testing: what is verified, what is not, and what to add

What runs today, what it leaves out, and the tests that would close the gap.

---

## 1. What runs today

| layer | entry point | asserts | needs |
|---|---|---|---|
| **Argus** | `./tools/dn test tests/Argus.Tests`; also the image's `test` stage and CI; `npm test` in `src/argus-web` | every route of its own app has its help; config and credentials, the parser, the index store (FTS, the allowlist on every scoped query, references, impact), indexing against a git fixture, packs, the server (MCP, the admin surface, the chat-client token) and the standalone app | nothing running |
| **App** | `./tools/dn test tests/Llm.Tests` (xUnit, **345 tests**), `npm test` (Vitest, **186**) and `npm run e2e` (Playwright) in `src/web`, and CI on every push | sign-in, OIDC, LDAP against a real OpenLDAP, people and keys, the dashboards against LiteLLM's real schema and fakes of Prometheus and Loki, the admin pages, models kept loaded and loaded on request against a fake router, the picture, video and speech models' controls, sound and video attachments (what each kind of model gets, transcripts made once), read aloud, where the video server decodes (`sd-serve.sh` under `/bin/sh` with a fake `nvidia-smi`), messages queued while a chat answers, long documents' pages drawn twenty at a time, credit and alert news by email and webhook, other GPU servers, and every page in a real browser on desktop and phone | Docker (Testcontainers) |
| **Deployment** | `scripts/upgrade-test.py --zero --stop-live` (and `--from TAG`) | this checkout from zero on fresh volumes: it comes up, provisions its first model, signs in, answers with a file; an upgrade keeps people, groups, chats and files, and survives `up` and `down`/`up` | a GPU host |
| **Code Arena** | `./tools/dn test tests/CodeArena.Tests`, `npx vitest run src/code-arena` in `src/web`, and CI | the agent against a fake gateway and MCP server (tools, modes, approvals, sessions, compaction); the IDE's server: files inside the folder only (absolute paths, `..` and links that lead out refused), search, the agent's changes accepted or reverted, this run's key and the Host and Origin checks on every call and on the terminal's WebSocket; terminals on a real pseudo-terminal with `/bin/sh` (echo, `stty size` after a resize, the exit code, close, a session and controlling terminal, no signal ignored or blocked, no API key), and the macOS helper's path run on Linux; the page (Explorer, tabs, saving, the diff view, search, the terminal panel, with Monaco, xterm.js and the socket faked); CI builds the Linux package with its page and starts it | Linux or macOS |
| **arena CLI** | `python3 -m unittest discover clients/arena`, and CI | `arena ask`, `review` and `explain-failure` against a fake gateway and GitLab on a local socket: streaming, the model chosen, diffs and logs cut to the budget, the note posted, a private CA, every exit code; the GitLab CI template's jobs run by `sh` (with PyYAML) | nothing running |
| **Recovery and offline, the scripts** | `python3 -m unittest discover -s tests/deploy`; also CI | `airgap.sh`: every image once, `deploy/` without `.env`, backups, keys or models, the registered models listed or copied, checksums written and checked, a damaged bundle or an unlisted file loads nothing, Podman; `restore-test.sh` and `rollback-test.sh`: their plans, and the guards (never the live project, its volumes or its ports); `recovery-check.py`: a dump's people, chats and settings, and the API checks against a fake app; the Laya server (`services/laya/server.py`): what a request may hold, which checkpoint reads it, its HTTP, with the model stubbed | nothing running (fake `docker` and `podman` on PATH) |
| **Restore and rollback** | `scripts/restore-test.sh [--from DIR]`, `scripts/rollback-test.sh FROM_TAG TO_TAG` | a backup restores into a throwaway project beside the live one, with its people, chats, settings and spend; a release rolls back by restoring the backup taken before the upgrade | a host with the stack's images |
| **Clients** | `scripts/clients-check.py` | the API as developers use it: OpenAI and Anthropic protocols, streaming, tool calls, spend per key, Argus over MCP, Qwen Code and DeepSeek Harness through the API and MCP | a running stack |
| **Scale** | `scripts/scale-test.py` | many people at once: the chat's queue (and no answer carrying another's secret), every key at once, a burst of sandbox jobs | a running stack |
| **Functional** | `scripts/functional-test.py` | what a person does: sign-in, provisioning, key rotation, budget exhaustion and restoration, password reset, per-person billing, access per model, fair use, a key's rate limit (refused past it, seen by the person, the chat not held to it), dashboards and logs for admins only | a running stack |
| **Sandbox** | `scripts/sandbox-check.py` | the running sandbox's isolation: no network, no reading others' runs, limits bind, nothing outlives a run | a running stack |
| **Dashboard audit** | `scripts/audit-dashboards.py` | every panel's queries in the app: no errors, no values out of range | a running stack |

**Podman** was checked by hand (2026-10-03, Podman 5.7, rootless): every service
up with `podman.yml`, GPUs through CDI, a chat answer, a Python run, a voice
message through the transcript, a picture model loaded on the GPU. CI renders
the stack with `podman.yml` (and with `podman compose` when the runner has it),
and parses every script, the services' (the picture and video servers', the
engine's) with `/bin/sh` too, the shell they run under.

## 2. What is not tested

| gap | what it would catch |
|---|---|
| `restore-test.sh` and `rollback-test.sh` run in CI (they need the stack's images) | a backup that cannot be restored, a release that cannot be rolled back |
| An offline bundle loaded on a second host with no network, then `up` | a first start that needs the internet |
| The stack running under Podman in CI (needs a GPU runner; CI only renders `podman.yml`) | a Podman regression past the compose files |
| Video generation in CI (minutes on a GPU) | a change in stable-diffusion.cpp's job API |
| Code Arena's terminals on a Mac and on Windows (the tests run on Linux, the macOS helper's path included; ConPTY is only built) | a pseudo-terminal that does not start, or loses its size or Ctrl+C, on those systems |

## 4. Server-side tests

"Server side" = runs on the host or inside `llm-net`, asserting the stack's own
contract. None of these need a browser.

### S2 — Service contract, per service

For **every** service, not just the 22 today:

| test | asserts |
|---|---|
| S2.1 | the container reaches its own healthcheck within a bounded time |
| S2.2 | a functional probe (not just liveness) returns the expected shape |
| S2.3 | it logs no `ERROR`/`FATAL` in the first 60 s |
| S2.4 | it is absent when its profile is off, and present when on |

**Specifically missing:** `app-init` (the app's folders exist with the right owner
and modes), `prometheus-secrets` (the token is 0600 and non-empty), `cpu-temp-exporter` (a reading is
emitted), `dcgm-exporter`, `promtail` (a log line reaches Loki), `langfuse-worker`
(a trace reaches ClickHouse), `minio` (a bucket exists), `vllm-secondary`
(`api2.<domain>` serves its model).

### S3 — Identity *(good: `audit-auth.sh`, `functional-test.py`)*

| test | status |
|---|---|
| S3.1 | OIDC discovery + real code exchange per client, claims correct | exists |
| S3.2 | forwardAuth allows/bypasses/denies per hostname per the access rules | exists — `ForwardAuthTests` (every host, member vs admin, machine token, unknown and look-alike hosts) and `audit-auth.sh` steps 3-5 |
| S3.3 | the registered OIDC clients are exactly the configured ones | exists — `OidcTests.A_client_the_configuration_does_not_name_is_removed_at_start` |
| S3.4 | `config/directory/users.yml` is hash-free and Argus can read it | exists — `IdentityTests.The_directory_for_argus_lists_people_without_passwords` |
| S3.5 | a changed `APP_KEY` makes the app refuse to start instead of resetting its keys | exists — `KeyRingTests` |

### S4 — Gateway *(good)*

Covered by `functional-test.py`, rate limits included: a key held to one
request a minute is refused past it with HTTP 429, `Retry-After` and
`Limit type: requests`, the person sees the limit (theirs) on their key's card,
and the chat answers meanwhile (checks named `rate`). `RateLimitTests` covers
the rest without a stack: the company's, a group's and a person's own, the
key sync (two replicas, a change made while it runs), new keys a few an hour,
Arena MCP's pictures counted, and refusals counted apart from requests.
Missing: behaviour when the engine is down (retry, then a clear error).

### S5 — Argus *(strong unit, weak integration)*

`tests/Argus.Tests` has 138 tests (§1). Missing at the stack level: index →
MCP → per-token ACL end to end against a real GitLab **in CI** (the
`tools/test-gitlab/` fixture runs it on demand, below), and the audit JSON
stream actually reaching Loki.

One lesson from the Python Argus is kept in the .NET tests: **a test fixture
that is more permissive than production hides exactly the failures that only
production can have.** `impact_of` was once dead for every caller, because the
server opens the index read-only and the tool wrote a temporary table, while
the unit fixture's connection was writable. The allowlist now travels as one
JSON parameter, and `Impact_of_walks_reverse_includes_only_through_allowed_repos`
and `Every_scoped_query_filters_by_the_allowlist` pin the behaviour. Any query
that touches the connection should be exercised the way the server opens it.

The GitLab **credential modes** are covered in `ConfigTests` (a username implies
password mode and needs the password; a password in the config file is
refused; the environment wins over the file), and the facts they encode (the
sign-in form, the minted token's `read_api` + `read_repository` scopes, the
removed OAuth password grant) were each read off a live GitLab
(`tools/test-gitlab/`) before being written down.

### S6 — Data and disaster recovery

| test | asserts | status |
|---|---|---|
| S6.1 | `backup.sh` produces a complete directory and `--verify` passes | **missing** |
| S6.2 | **restore round trip**: back up, destroy the volumes, restore, and compare row counts and file counts | **scripted**: `scripts/restore-test.sh` restores into fresh volumes of a throwaway project and compares people, chats and settings with the backup's dump, through the API |
| S6.3 | `--restore --with-config` restores `.env` and `config/` | **missing** |
| S6.4 | `--install-timer` produces a working systemd unit | **missing** |
| S6.5 | restore onto a **clean host** (no prior volumes) | **scripted** on the same host: `restore-test.sh` starts from no volumes |

### S7 — Observability

Every scrape target `up == 1` **for the profiles that are on**; rule files load;
one alert is driven from a synthetic metric to Alertmanager and observed. Today
acceptance checks that the rules load and that Alertmanager and Loki answer, and
the functional test reads a Prometheus panel, Loki's lines and every rule through
the app; no alert is driven end to end yet.

Log *ingestion* is the newest gap to close, and the first live start showed why
it needs a test. Promtail's `keep` filter was documented as "only ingest this
stack's containers" and its regex was `".+"` — every container on the daemon
carrying any compose project label. The bundled test GitLab's whole log was
being shipped into this deployment's Loki, which is both volume and another
project's data appearing in this project's Logs page. It now matches
`COMPOSE_PROJECT_NAME`, and Loki's per-stream rate limit was raised from its
3MB/s default: Promtail reads each container's entire existing log file on first
start, and Loki answered `429 — entry ignored` for most of the backlog, which is
exactly the history an operator enables collection to get.

| test | asserts | status |
|---|---|---|
| S7.1 | only this compose project's containers appear in Loki | verified by hand; **no test** |
| S7.2 | `{event="index_start"}` … `{event="index_end"}` reach Loki with their labels | verified by hand; **no test** |
| S7.3 | an index run that never reaches a repository still emits `index_end` | unit-tested (`test_a_refused_run_still_emits_index_end_with_the_reason`) |

### S8 — Ingress and TLS

Covered well by `domain-check.sh` and `acceptance.py` for routes. Missing:
`api2`, `s3` and the `metrics` sub-routes per engine; and that a **denied**
hostname really is denied.

### S9 — Supply and offline

| test | status |
|---|---|
| S9.1 | airgap bundle builds, its checksums verify, and a damaged one loads nothing | `scripts/airgap.sh pack` and `load`; the logic in CI (`tests/deploy`) |
| S9.2 | full round trip: build → extract → load → `up` → the stack serves | **missing**: needs a second host |
| S9.3 | the stack starts with `--network none` on the compose network, i.e. genuinely offline | **missing** — this is what the offline commits claim |
| S9.4 | images build from a clean cache (all three local ones) | **missing** |

### S11 — Many people at once (`scripts/scale-test.py`), 2026-10-03

One RTX-class GPU serving Qwen3.8-Flash-Next (IQ4_XS), the sandbox on 2 slots:

| scenario | result |
|---|---|
| 12 people send a chat message at the same moment | 12/12 answered; 10 waited their turn (up to 10 ahead); first word p50 12 s, p95 18 s; no answer carried another person's secret |
| 30 people at once | 30/30; 28 waited (up to 28 ahead, 38 s); first word p50 20 s, p95 38 s; no leak |
| 12 and 30 API keys at once (streaming) | all 200; first token p50 9 s / 20 s, p95 14 s / 39 s |
| 24 and 60 one-second Python jobs in a burst | all done and right, in 15 s and 34 s on 2 slots; none lost |

Nothing failed or was refused: the fair-use queue serves everyone in turn
instead. What grows with people is the wait, about linearly with the engine's
slots; more slots (`LLAMACPP_PARALLEL`) or a second engine shorten it.

### S10 — Migrations and upgrades

`argus index` twice, compose `up` twice, `down`/`up`, and a version rollback.
The repo has been bitten by idempotency before. `upgrade-test.py --from TAG`
runs `up` twice and `down`/`up`; `rollback-test.sh FROM_TAG TO_TAG` the
rollback: the older images over the newer database, then the backup from
before the upgrade restored under them.

---

## 5. Client-side tests

"Client side" = a consumer **outside** the stack: the failures every
server-side check calls healthy, such as a harness that cannot reach the API
because it does not trust the certificate.

### C1 — HTTP API clients

| test | asserts |
|---|---|
| C1.1 | `curl` with the CA gets `/v1/models`, and lists exactly one model by its real name |
| C1.2 | an OpenAI SDK (`openai` python) completes a chat through `gateway.<domain>` |
| C1.3 | streaming: tokens arrive incrementally, not as one buffered body |
| C1.4 | no key → 401; bad key → 401; over-budget → 429 with a usable message |
| C1.5 | without the CA → a certificate error, i.e. the stack is **not** accidentally plaintext |

### C3 — Agent integrations

| test | asserts |
|---|---|
| C3.1 | DeepSeek Harness reaches the API with `NODE_EXTRA_CA_CERTS` set before launch. **Verified, and re-verified on v2.1.2** — `dsh --profile headless` with `NODE_EXTRA_CA_CERTS` and `--patch` called `mcp__argus__find_symbol`, got `root/eal-core` back, and Argus logged `tool=find_symbol user=dev_alpha outcome=ok`. The variable is read at process start, so it cannot be set afterwards. Config: `clients/deepseek-harness/` |
| C3.2 | Qwen Code connects to Argus, calls a tool and completes. **Now verified** on v2.1.2 with qwen 0.23.3, against the stack's own gateway (`--auth-type openai --openai-base-url https://gateway.<domain>/v1`) rather than a cloud key: it called `find_symbol`, distinguished the definition in `src/decoder.c` from the declaration in `include/eal/decoder.h`, and Argus logged `user=dev_alpha outcome=ok`. Two things had to be learned: `--trust` is required in a headless run or every call waits for confirmation, and the gateway host is `gateway.<domain>` — `api.<domain>` routes to the sign-in and answers with a login redirect that reads as a 401. Config: `clients/qwen-code/` |
| C3.3 | Hermes connects, lists tools and completes (see [hermes.md](hermes.md)) |
| C3.4 | a generic MCP client connects to `argus.<domain>/mcp` with a GitLab PAT and lists tools. **Verified** — the harness MCP client (`@deepseek-ai/dsh-mcp-client`, streamable-http) handshakes through Traefik and lists 16 tools |
| C3.5 | **per-person ACL**: developer A's PAT does not return developer B's private repository — the question `tools/test-gitlab/` exists to answer. **Verified and now automated** against a real GitLab CE by `./tools/test-gitlab/run.sh`, which is one command from a cold start: `DecodeFrame` (eal-core) is visible to `dev_alpha` and denied to `dev_beta`; `RunPipeline` (etl-decoder) the reverse; `ShimEntry` (driver-shim, which has no members) is denied to both, with the "does exist in 1 repository you cannot read" notice. `verify_tools.py` extends it to **all sixteen MCP tools over the wire**, checks each result's declared shape, and asserts that no structured field names a repository the caller cannot read |

### C4 — Browser *(G8, now covered by the app's Playwright suite)*

The app's pages, chat included, run in Playwright on desktop and phone. The
original minimum set, and where each item stands:

| test | asserts |
|---|---|
| C4.1 | `https://<domain>/admin` sends a signed-out browser to sign in and back |
| C4.2 | before the CA is trusted, the warning can be clicked through (no HSTS); after, no warning |
| C4.3 | a chat renders a streamed answer incrementally: **done in the app's chat** (`chat.spec.ts`) |
| C4.4 | Argus is offered to a non-admin: **done in the app's chat**, with the no-access notice for someone without access |
| C4.5 | every dashboard draws every panel without an error: **done in the app** (`admin.spec.ts`) |
| C4.6 | sign-out ends the session at the app and every service that trusts it |

### C5 — Recovery from the client's point of view

After a restore (S6.2), an existing per-person API key still works and the
person's spend history is still there. A restore that silently invalidates every
key is a failure that S6.2 alone would not catch.

### The harness, 2026-09-15

Argus as an MCP tool server for DeepSeek Harness, end to end:

```bash
cat > /tmp/argus-mcp.patch.yml <<'YML'
- insert:                       # `insert:` is the append form; a bare entry
  - name: '@deepseek-ai/dsh-mcp-client'   # is read as an override and rejected
    config:
      serverName: argus
      transport: streamable-http
      url: https://argus.llm.localhost/mcp
      headers:
        Authorization: !!js '`Bearer ${process.env.ARGUS_TOKEN}`'
YML

ARGUS_TOKEN=<gitlab PAT> NODE_EXTRA_CA_CERTS=config/traefik/certs/ca.crt \
  dsh --profile headless --patch /tmp/argus-mcp.patch.yml \
  "Use the mcp__argus__find_symbol tool, with name=DecodeFrame."
# -> root/eal-core, and Argus logs the call: outcome=ok, user=dev_alpha
```

Two things this settled. The harness has **no per-server TLS option**, so
`NODE_EXTRA_CA_CERTS` before launch is the only way to reach a self-signed
endpoint. And `--profile headless "task"` is the way to exercise an MCP server
from a script, with no browser and no server left running.

### Verified by hand, 2026-09-15

The per-person path was exercised end to end against a real GitLab CE for the
first time, which had been the largest untested claim in the project:

```bash
./tools/test-gitlab/run.sh          # up, seed, verify, down
```

That one command replaces the sequence below (up and seed; the per-tool
verifier is not yet ported to the .NET Argus), and takes the fixture down again
when it is finished — including when seeding fails. The fixture is
`restart: "no"` now; it used to be `unless-stopped`, which meant it survived
reboots and sat at 2.63 GiB of RAM and 2.17% CPU indefinitely on a host whose
whole job is to keep the GPU busy with something else.

By hand, which is what `run.sh` wraps:

```bash
docker compose -f tools/test-gitlab/docker-compose.yml up -d   # first boot: minutes
# Wait for `curl -fsS http://localhost:8929/-/readiness`. The container reports
# `healthy` several minutes before the API can answer, and seeding against a
# GitLab that is still reconfiguring fails in ways that look like a bad seed.
# seed.py shells out to `docker exec`, so it needs the CLI and the socket
docker run --rm --user root --network host -e HOME=/tmp \
  -v /var/run/docker.sock:/var/run/docker.sock \
  -v "$(command -v docker)":/usr/local/bin/docker \
  -v "$PWD/tools/test-gitlab:/t" -w /t python:3.13-slim python /t/seed.py
docker exec argus argus index --config /etc/argus/config.yaml
docker compose -f tools/test-gitlab/docker-compose.yml down -v   # do not skip this
```

An index outside a container needs its work directory off an NTFS checkout:
SQLite in WAL mode cannot open its shared-memory file over that filesystem and
`argus index` dies with `disk I/O error`.

Two things that had to be right first, all now recorded in the code:

* Argus could not reach GitLab at all until the fixture was running, and every
  chat's Argus call came back **401** — Argus rejects a chat user it cannot
  resolve access for.
* The clone URL had to be rebased onto the configured GitLab: GitLab advertises
  `http://localhost:8929`, which inside a container is the container itself.

Note that a deployment pointed at this fixture (`ARGUS_GITLAB_URL` in `.env`)
has nothing to index while the fixture is down, so `ArgusIndexStale` fires and
the app's Overview says the repositories are out of date. That is the
system being right, not broken: the index genuinely cannot refresh.

---

## 6. Priority

Ordered by (risk × likelihood), not by effort:

| # | test | why first |
|---|---|---|
| 1 | **S6.2 restore round trip** — **scripted** (`restore-test.sh`); next, run it after every backup | the only operation that can destroy data |
| 3 | **S2 for the ten unreferenced services** | closes the largest named hole |
| 4 | **C2 TLS trust per runtime** | this is the failure users actually hit; four small tests |
| 5 | **S9.3 genuinely offline start** | the offline commits claim it; nothing checks it |
| ~~6~~ | ~~**S3.4 hash-free account list**~~ — **done** (`IdentityTests`) | |
| 7 | **S9.1/S9.2 airgap round trip** — S9.1 **scripted** (`airgap.sh`); S9.2 needs a second host | easy to regress |
| ~~8~~ | ~~**Automate C3.5**~~ — **done.** `./tools/test-gitlab/run.sh` runs the whole lifecycle, and `ARGUS_TEST_WORK` moves the index off the NTFS volume that SQLite's WAL mode cannot use. All sixteen tools are contract-tested over the wire; the six `docs_*` tools are reported as NOT COVERED because the fixture has no documentation pack installed |
| 9 | **S10 idempotency** | two `up`s, two indexes, one `down`/`up` |
| 10 | **C4 browser** | highest effort, and the only way to test the UI layer at all |

---

## 7. Running what exists

```bash
cd deploy

# unit (no stack needed), from the repository root
../tools/dn test tests/Llm.Tests -c Release
../tools/dn test tests/Argus.Tests -c Release
(cd .. && python3 -m unittest discover -s tests/deploy)   # the recovery and offline scripts

# against a real Laya (a throwaway container of services/laya on 127.0.0.1:18000); skipped without LAYA_URL
../tools/dn test tests/Llm.Tests --filter RealLaya -e LAYA_URL=http://127.0.0.1:18000
../tools/dn test tests/CodeArena.Tests --filter RealLaya -e LAYA_URL=http://127.0.0.1:18000

# with the stack up
make health          # container state + in-network probes
make smoke           # API surface
./scripts/domain-check.sh
./scripts/audit-auth.sh
./scripts/acceptance.py        # note: SKIP is not PASS
./scripts/functional-test.py   # the person-facing flows
./scripts/audit-dashboards.py 6h  # every panel's queries, run in the app

# the web in a real browser (in src/web): desktop and phone, both themes,
# axe accessibility (WCAG 2.2 AA) on every page and what opens on it, focus
# back where it was when a dialog closes, the skip link, every page at 320 px
# (a11y.spec.ts); E2E_CHAT=1 adds the chat against the real model
E2E_PASSWORD=<admin password> E2E_CHAT=1 npm run e2e
# (with E2E_CHAT=1 one test at a time: the engine answers in turn. In CI it is
# .github/workflows/e2e-live.yml, nightly and by hand, on a self-hosted runner
# labelled argus-arena on the stack's host, with E2E_PASSWORD and E2E_BASE_URL)
# ... and Argus's per-person access, with the test GitLab up
./tools/test-gitlab/run.sh --keep   # from the repo root
E2E_ARGUS_USER=dev_beta E2E_ARGUS_PASSWORD=<theirs> E2E_PASSWORD=... E2E_CHAT=1 npm run e2e

# clients as developers point them: the API (OpenAI and Anthropic), streaming,
# tool calls, spend attribution; Argus over MCP; Qwen Code and DeepSeek Harness
# through the API and calling Argus (each with a throwaway home; the test GitLab up)
./scripts/clients-check.py --dsh /path/to/node_modules/.bin/dsh

# many people at once: the chat's fair-use queue (and no answer carrying another
# person's secret), every person's API key at once, a burst of sandbox jobs
./scripts/scale-test.py --users 12 --sandbox-jobs 24

# upgrade: an old release from zero (fresh volumes), data in, this checkout over
# it; and this checkout from zero. The live stack goes down meanwhile (volumes kept)
./scripts/upgrade-test.py --from v3.0.0 --stop-live
./scripts/upgrade-test.py --zero --stop-live

# recovery, beside the live stack (never touching it): the newest backup restored
# into a throwaway project and checked; a release rolled back by its backup
./scripts/restore-test.sh
./scripts/rollback-test.sh v4.1.0 v4.0.0
./scripts/airgap.sh pack --dry-run /tmp/arena.tar   # what an offline bundle would hold

# from inside the network
docker run --rm --network llm-net -e MK=<master-key> \
  -v "$PWD/scripts:/s:ro" python:3.13-slim python /s/e2e-check.py
```

For the Argus case the stack's Argus must index the test GitLab, and the
fixture's `dev_beta` must be a person in the app. Keep a copy of `.env` first,
because all of this is temporary:

1. In `.env`, set `GITLAB_URL=http://host.docker.internal:8929` and
   `GITLAB_TOKEN` to the fixture's token
   from `tools/test-gitlab/seeded.json` (not in git; it is a throwaway
   instance). Then run `docker compose up -d` and wait for the first index.
2. In the app, go to Admin → People and add `dev_beta` with the email
   `dev_beta@argus.test`. Argus matches the chat user by that email.
3. Afterwards:
   - put the old `.env` back and run `docker compose up -d --remove-orphans`
   - run `docker compose --profile argus rm -sf argus llamacpp-embed embed-init`
   - delete `dev_beta` from the app
   - run `./tools/test-gitlab/run.sh --down`

**Read the SKIP lines.** A green `acceptance.py` on the default profiles has not
exercised tracing, logging, cadvisor, dcgm or the second model.
