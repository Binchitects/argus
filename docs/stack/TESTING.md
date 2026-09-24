# Testing: what is verified, what is not, and what to add

Short answer to "is every part of the stack tested": **no.** Roughly two thirds
of the services are referenced by a test; a third are not, and "referenced" is
not the same as "verified" — being named in a health check means a container
answered, not that its behaviour is correct.

This document says exactly what runs today, what it leaves out, and defines the
server-side and client-side tests that would close the gap.

Every number here was measured against the tree, not estimated.

---

## 1. What runs today

| layer | entry point | asserts | needs |
|---|---|---|---|
| **Unit** | `pytest tests/` — **928 tests** | the Argus Python package: config, credentials, gitlab, mirror, tls, acl, access, resolve, worker, cli, packs, parse, store, mcpsrv, auditlog — plus `check_mounts.py`, the stack's preflight guard | nothing running; no Docker |
| **App** | `./dn test` (xUnit), `npm test` (Vitest) and `npm run e2e` (Playwright) in `app/`, and CI on every push | sign-in, OIDC, forwardAuth, LDAP against a real OpenLDAP, people and keys, the dashboard engine running every SQL panel against LiteLLM's real schema, the admin pages including Indexing's exit-code meanings and the partial-enumeration opt-in, and every page in a real browser on desktop and phone | Docker (Testcontainers) |
| **Dashboard parity** | `scripts/compare-dashboards.py` | every SQL panel queried in the app and in Grafana over the same range and interval gives the same rows | a running stack |
| **Acceptance** | `scripts/acceptance.py` | 7 groups — `config`, `routes`, `identity`, `infra`, `obs`, `ops`, `e2e`. Routes answer, OIDC discovery documents exist, scraping works, datasources are healthy | a running stack |
| **E2E** | `scripts/e2e-check.py` | from **inside** the network: the engine serves the model the gateway advertises, an API call is attributed to the key that made it, a chat is attributed to the same person, an over-budget person is refused | a running stack |
| **Functional** | `scripts/functional-test.py` | 36 checks of what a *person* does: SSO sign-in, provisioning, key rotation, budget exhaustion and restoration, password reset, self-signup refusal, per-person billing on both surfaces | `auth`,`gateway` profiles |
| **Auth audit** | `scripts/audit-auth.sh` | a **real** OAuth2 authorization-code exchange per OIDC client, then the claims actually delivered | `auth` profile |
| **Domain** | `scripts/domain-check.sh` | every hostname routes, TLS serves the right certificate, and the *old* domain is gone | `proxy` |
| **Smoke** | `scripts/smoke-test.sh` | model listing, auth enforcement, a completion, a streaming completion, Prometheus saw the traffic | `gateway` |
| **Health** | `scripts/health.sh` | Docker healthchecks plus an in-network probe, per enabled service | any |
| **Benchmarks** | `benchmark.py`, `multiuser-bench.py` | throughput and latency under concurrency — **measurement, not assertion** | `vllm`/`llamacpp` |

That is a genuinely good layer for the paths a person takes. It is not
uniform coverage of the stack.

---

## 2. Coverage per service, measured

Each of the 32 services against the nine test entry points, by name:

| covered | service | by |
|---|---|---|
| ✅ | argus | acceptance, e2e-check, health, domain-check |
| ✅ | app (sign-in, OIDC, forwardAuth, people) | app tests (xUnit, Vitest, Playwright), acceptance, functional-test, health, domain-check, audit-auth |
| ✅ | open-webui | acceptance, e2e-check, functional-test, health, domain-check, audit-auth |
| ✅ | litellm | acceptance, e2e-check, functional-test, health |
| ✅ | grafana | acceptance, functional-test, health, domain-check, audit-auth |
| ✅ | traefik | acceptance, functional-test, health, domain-check, audit-auth |
| ✅ | langfuse | acceptance, health, audit-auth |
| ✅ | prometheus, alertmanager, node-exporter, nvidia-smi-exporter, cadvisor, loki, redis, postgres, power-limits, clickhouse | acceptance/health only |
| ✅ | llamacpp, vllm | e2e-check, health, smoke/bench |
| ✅ | app | app tests, CI, acceptance, functional-test, audit-auth, compare-dashboards |
| ⚠️ | model-init, tls-init | one script each |
| ❌ | **auth-init** | nothing |
| ❌ | **identity-proxy** | nothing |
| ❌ | **prometheus-secrets** | nothing |
| ❌ | **ollama** | nothing |
| ❌ | **cpu-temp-exporter** | nothing |
| ❌ | **dcgm-exporter** | nothing |
| ❌ | **langfuse-worker** | nothing |
| ❌ | **minio** | nothing |
| ❌ | **promtail** | nothing |
| ❌ | **vllm-secondary** | nothing |

Ten services are named by no test at all.

### The bigger caveat: skipped is not passed

`acceptance.py` is deliberately profile-aware — it emits `SKIP` rather than
`FAIL` for a service whose profile is off, and the exit code stays 0. On the
default deployment (`gateway,proxy,auth,smi,llamacpp,argus`) that means the
whole of `tracing` (langfuse, langfuse-worker, clickhouse, minio), `logging`
(loki, promtail), `cadvisor`, `dcgm`, `vllm`, `vllm-secondary` and `ollama`
are **reported green without being exercised**. A green run says "nothing
failed", not "everything passed".

---

## 3. What is not tested, categorised

| # | gap | why it matters |
|---|---|---|
| G1 | **Ten services have no test** | auth-init and prometheus-secrets build files other services depend on; if they regress, everything downstream fails confusingly |
| G2 | **Profile-gated stacks are skipped by default** | tracing, logging, cadvisor, dcgm, multi-model are shipped and claimed, never run |
| G3 | **Restore has no test** | `backup.sh --restore` is the highest-risk operation in the repository and the only one that can destroy data. `--verify` runs its checksums, but nothing restores and compares |
| G4 | **The airgap round trip is manual** | bundle → transfer → `load.sh` was verified by hand once. Nothing keeps it working |
| G5 | **`preflight.sh` / `check_mounts.py` are manual** | only synthetic payloads I ran by hand; no test in the suite |
| G6 | **`with-ca.sh` is manual** | the host-trust path for `dsh`, curl, python and git |
| G7 | **Built images other than Argus and the app** | identity-proxy and the cpu-temp-exporter image have no build-time test |
| G8 | ~~No browser tests for chat~~ **closed** | the app's chat runs in a real browser against the real model, desktop and phone: streaming, thinking, stop and regenerate, attachments, code copy, history, and Argus's no-access notice for a person without access (`app/frontend/e2e/chat.spec.ts`, [CHAT.md](CHAT.md)). Open WebUI itself stays untested and goes when the app's chat is signed off |
| G9 | **Two clients executed, three transcribed** | DSH and Qwen Code now run end to end and their configs are in `clients/`, marked as executed. Claude Code and Continue are written from their own documentation and marked as such; Hermes is unexercised; the OpenAI SDK has no test at all. The distinction is recorded per file in `clients/README.md` so a transcribed config is never mistaken for a verified one |
| G10 | **No upgrade or rollback test** | changing `ARGUS_VERSION` or an image tag and rolling back is untested |
| G11 | **Disaster recovery is untested** | restore onto a *clean host*, which is the actual scenario |
| G12 | **Windows / WSL** | every `.ps1` is unexercised here |

---

## 4. Server-side tests

"Server side" = runs on the host or inside `llm-net`, asserting the stack's own
contract. None of these need a browser.

### S1 — Configuration and compose integrity *(partial: `preflight.sh`)*

| test | asserts | status |
|---|---|---|
| S1.1 | `docker compose config` resolves with only `.env` edited | exists in preflight |
| S1.2 | every bind mount resolves to real content, live **and** on a fresh clone | exists (`check_mounts.py planned`/`containers`) |
| S1.3 | a missing required variable names the variable | manual only |
| S1.4 | every profile combination renders | **missing** |
| S1.5 | `env-samples/*.env` each render against the current compose | **missing** — a sample that no longer matches is invisible today |

`check_mounts.py` now has its own unit tests in `tests/test_check_mounts.py`
(run with the Argus suite; the file is copied into the image's test stage).

They exist because enabling `logging` by default turned `preflight.sh` red on a
perfectly healthy host. `/var/lib/docker` is mode 0710 root:root, so an ordinary
user cannot stat anything inside it and `os.path.exists` returned **False for
`/var/lib/docker/containers`** — a directory that is plainly there and that the
daemon binds successfully, because the daemon is root. Preflight called it
missing and told the operator their checkout had moved, with instructions to
tear the stack down and delete directories that did not need deleting.

The rule is now explicit: absence is only concluded when every directory above
the path is searchable. "Cannot see it" is not "it is not there". The tests
stub the filesystem rather than chmod-ing, because the suite runs as root and
root is not subject to the bits this is about.

### S2 — Service contract, per service

For **every** service, not just the 22 today:

| test | asserts |
|---|---|
| S2.1 | the container reaches its own healthcheck within a bounded time |
| S2.2 | a functional probe (not just liveness) returns the expected shape |
| S2.3 | it logs no `ERROR`/`FATAL` in the first 60 s |
| S2.4 | it is absent when its profile is off, and present when on |

**Specifically missing:** `auth-init` (its three output files exist, are parseable
and have the right modes), `prometheus-secrets` (the token is 0600 and non-empty),
`identity-proxy` (it actually rewrites the `user` field), `ollama` (the embedding
model is present and returns a 768-vector), `cpu-temp-exporter` (a reading is
emitted), `dcgm-exporter`, `promtail` (a log line reaches Loki), `langfuse-worker`
(a trace reaches ClickHouse), `minio` (a bucket exists), `vllm-secondary`
(`api2.<domain>` serves its model).

### S3 — Identity *(good: `audit-auth.sh`, `functional-test.py`)*

| test | status |
|---|---|
| S3.1 | OIDC discovery + real code exchange per client, claims correct | exists |
| S3.2 | forwardAuth allows/bypasses/denies per hostname per the access rules | exists — `ForwardAuthTests` (every host, member vs admin, machine token, unknown and look-alike hosts) and `audit-auth.sh` steps 3-5 |
| S3.3 | `PROTECTED_CHAIN=protected-chain@file` (basic auth, `auth` profile off) | **missing** |
| S3.4 | `config/authelia/directory/users.yml` is hash-free and Argus can read it | exists — `IdentityTests.The_directory_for_argus_lists_people_without_passwords` |
| S3.5 | a changed `APP_DATA_KEY` makes the app refuse to start instead of resetting its keys | exists — `KeyRingTests` |

### S4 — Gateway *(good)*

Covered by `functional-test.py`. Missing: per-person **rate** limits (as opposed
to spend), and behaviour when the engine is down (retry, then a clear error).

### S5 — Argus *(strong unit, weak integration)*

Unit is 928 tests. Missing at the stack level: index → MCP → per-token ACL end to
end against a real GitLab (the `scripts/test-gitlab/` fixture exists but is not
wired into a suite), and the audit JSON stream actually reaching Loki.

**The Indexing card was broken in three separate ways at once**, and every one
of them was found by using the running stack rather than by reading it:

1. **The log was thrown away the moment a run ended.** The card kept the
   child's output only while `state == "running"`; afterwards it rendered
   "exit 3" and nothing else. The reason was in the tail the whole time. This
   is what "I press Index and always get an error" actually was — the error was
   real, and the explanation was discarded by the UI.
2. **`/admin/index/status` raised `NameError`.** `connect_readonly` was never
   imported, so the per-repo freshness table was *always* empty, and the broad
   `except` that caught it stored the message in a field the panel did not
   render. "Never worked" looked exactly like "nothing indexed yet".
3. **`impact_of` was dead for everyone** — see the note below, which belongs to
   the same class of finding.

There was also a dead end: Argus refuses to index when the token cannot see the
whole estate, and its refusal says "re-run with `--allow-partial-enumeration`" —
a flag the panel had no way to pass. The card now has an explicit opt-in,
unchecked by default, and exit 3 points at it.

The admin panel that had this card is gone; the app's Indexing page keeps the
opt-in and the exit-code meanings, and its UI test asserts on both.

**A whole tool was dead, and the suite said it was fine.** `impact_of` built its
allowlist in a `TEMP TABLE`, but the server opens the index with
`PRAGMA query_only = ON`, so the first statement raised *attempt to write a
readonly database* for **every caller who had access**. `run_readonly`'s
catch-all turned that into "The index is unavailable; do not retry this query",
which reads as a storage fault and sends people to look at the disk. The unit
tests passed because the shared fixture connection is *writable*, and a temp
table is perfectly legal there — the one connection they never used is the one
production uses.

Found by exercising the tool against the running stack, not by reading it. The
allowlist now travels as a JSON array joined with `json_each`, which needs no
write and, as a bonus, is a single host parameter — so the recursive walk is no
longer near `SQLITE_MAX_VARIABLE_NUMBER` either. The three tests that pin it
(`test_impact_of_works_on_the_servers_readonly_connection`,
`test_impact_of_allowlist_larger_than_parameter_limit`,
`test_impact_of_excludes_a_repo_outside_the_allowlist`) all go through
`connect_readonly`, which is what the MCP server uses; the first two fail with
the exact production error against the old code.

The general lesson is worth keeping: **a test fixture that is more permissive
than production hides exactly the failures that only production can have.** Any
query function that touches the connection should be exercised through
`connect_readonly` at least once.

Within that unit count, the GitLab **credential modes** are covered properly,
because both failure directions here are expensive and neither is visible from
the outside:

| test | asserts |
|---|---|
| `test_a_token_goes_in_the_private_token_header`, `test_a_password_becomes_a_bearer_token` | the header follows the mode. A minted token presented as `PRIVATE-TOKEN` works, but the mirror image — an OAuth-shaped value in the wrong header — reads as an ACL problem |
| `test_password_mode_signs_in_and_mints_a_token` and friends | the sign-in → mint sequence, that a token from a previous run is revoked first, and that the minted token carries `read_api` + `read_repository` and not full `api` |
| `test_the_password_is_sent_in_the_body_not_the_url` | the password is in a POST body, never in a query string |
| `test_a_rejected_sign_in_does_not_echo_the_password`, `test_a_transport_failure_does_not_echo_the_password` | no failure path — including an `httpx` exception, which carries the request — puts the password into a message |
| `test_a_401_re_mints_and_retries_once`, `test_a_401_that_survives_the_re_mint_is_returned_as_is`, `test_token_mode_never_re_mints` | a minted token that GitLab has expired or revoked is replaced and the read retried **once**; a second `401` is reported rather than looped on; static tokens are never retried |
| `test_a_server_that_will_not_mint_tokens_says_so`, `test_two_factor_is_named_rather_than_reported_as_a_bad_password` | the two failures that are *not* a wrong password are named as such, because changing the password does not fix either |

The measured facts those tests encode — the sign-in form endpoint, the CSRF
token's two names, the flat `name` + repeated `scopes[]` parameters, the
`unsupported_grant_type` that retired the old OAuth password grant, and the one
year default expiry on a blank `expires_at` — were each read off a live GitLab
(`scripts/test-gitlab/`) before being written down, not inferred from the docs.

### S6 — Data and disaster recovery

| test | asserts | status |
|---|---|---|
| S6.1 | `backup.sh` produces a complete directory and `--verify` passes | **missing** |
| S6.2 | **restore round trip**: back up, destroy the volumes, restore, and compare row counts and file counts | **missing** — the most important test in this document |
| S6.3 | `--restore --with-config` restores `.env` and `config/` | **missing** |
| S6.4 | `--install-timer` produces a working systemd unit | **missing** |
| S6.5 | restore onto a **clean host** (no prior volumes) | **missing** (G11) |

### S7 — Observability

Every scrape target `up == 1` **for the profiles that are on**; rule files load;
one alert is driven from a synthetic metric to Alertmanager and observed. Today
only "the datasource is healthy" is checked.

Log *ingestion* is the newest gap to close, and the first live start showed why
it needs a test. Promtail's `keep` filter was documented as "only ingest this
stack's containers" and its regex was `".+"` — every container on the daemon
carrying any compose project label. The bundled test GitLab's whole log was
being shipped into this deployment's Loki, which is both volume and another
project's data appearing in this project's Grafana. It now matches
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
| S9.1 | airgap bundle builds, its checksums verify, and `load.sh --check` passes | manual once |
| S9.2 | full round trip: build → extract → load → `up` → the stack serves | **missing** |
| S9.3 | the stack starts with `--network none` on the compose network, i.e. genuinely offline | **missing** — this is what the offline commits claim |
| S9.4 | images build from a clean cache (all three local ones) | **missing** |

### S10 — Migrations and upgrades

`argus index` twice, compose `up` twice, `down`/`up`, and a version rollback.
The repo has been bitten by idempotency before; nothing asserts it now.

---

## 5. Client-side tests

"Client side" = a consumer **outside** the stack. These are the tests that would
have caught the two problems this session opened with — DSH unable to reach the
API and Open WebUI unable to register Argus — because both were client-side
failures that every server-side check called healthy.

### C1 — HTTP API clients

| test | asserts |
|---|---|
| C1.1 | `curl` with the CA gets `/v1/models`, and lists exactly one model by its real name |
| C1.2 | an OpenAI SDK (`openai` python) completes a chat through `gateway.<domain>` |
| C1.3 | streaming: tokens arrive incrementally, not as one buffered body |
| C1.4 | no key → 401; bad key → 401; over-budget → 429 with a usable message |
| C1.5 | without the CA → a certificate error, i.e. the stack is **not** accidentally plaintext |

### C2 — TLS trust per runtime *(G6)*

`with-ca.sh` must be proven for each runtime, because each reads a different
variable:

| test | runtime | variable |
|---|---|---|
| C2.1 | Node | `NODE_EXTRA_CA_CERTS` (adds) |
| C2.2 | Python `requests`/`httpx`/`urllib` | `SSL_CERT_FILE` (replaces) |
| C2.3 | curl | `CURL_CA_BUNDLE` (replaces) |
| C2.4 | git | `GIT_SSL_CAINFO` (replaces) |
| C2.5 | and that C2.2–C2.4 still verify a **public** host, proving the combined bundle is correct |

### C3 — Agent integrations

| test | asserts |
|---|---|
| C3.1 | DeepSeek Harness reaches the API with `NODE_EXTRA_CA_CERTS` set before launch. **Verified, and re-verified on v2.1.2** — `dsh --profile headless` with `NODE_EXTRA_CA_CERTS` and `--patch` called `mcp__argus__find_symbol`, got `root/eal-core` back, and Argus logged `tool=find_symbol user=dev_alpha outcome=ok`. The variable is read at process start, so it cannot be set afterwards. Config: `clients/deepseek-harness/` |
| C3.2 | Qwen Code connects to Argus, calls a tool and completes. **Now verified** on v2.1.2 with qwen 0.23.3, against the stack's own gateway (`--auth-type openai --openai-base-url https://gateway.<domain>/v1`) rather than a cloud key: it called `find_symbol`, distinguished the definition in `src/decoder.c` from the declaration in `include/eal/decoder.h`, and Argus logged `user=dev_alpha outcome=ok`. Two things had to be learned: `--trust` is required in a headless run or every call waits for confirmation, and the gateway host is `gateway.<domain>` — `api.<domain>` routes to the sign-in and answers with a login redirect that reads as a 401. Config: `clients/qwen-code/` |
| C3.3 | Hermes connects, lists tools and completes (see `docs/HERMES.md`) |
| C3.4 | a generic MCP client connects to `argus.<domain>/mcp` with a GitLab PAT and lists tools. **Verified** — the harness MCP client (`@deepseek-ai/dsh-mcp-client`, streamable-http) handshakes through Traefik, and Open WebUI's MCP client lists 16 tools |
| C3.5 | **per-person ACL**: developer A's PAT does not return developer B's private repository — the question `scripts/test-gitlab/` exists to answer. **Verified and now automated** against a real GitLab CE by `./scripts/test-gitlab/run.sh`, which is one command from a cold start: `DecodeFrame` (eal-core) is visible to `dev_alpha` and denied to `dev_beta`; `RunPipeline` (etl-decoder) the reverse; `ShimEntry` (driver-shim, which has no members) is denied to both, with the "does exist in 1 repository you cannot read" notice. `verify_tools.py` extends it to **all sixteen MCP tools over the wire**, checks each result's declared shape, and asserts that no structured field names a repository the caller cannot read |

### C4 — Browser *(G8, now covered by the app's Playwright suite)*

The app's pages, chat included, run in Playwright on desktop and phone. The
original minimum set, and where each item stands:

| test | asserts |
|---|---|
| C4.1 | `https://admin.<domain>` follows the SSO redirect chain to the panel and back |
| C4.2 | the self-signed certificate produces a warning that can be accepted, and the page then loads |
| C4.3 | a chat renders a streamed answer incrementally: **done in the app's chat** (`chat.spec.ts`) |
| C4.4 | Argus is offered to a non-admin: **done in the app's chat**, with the no-access notice for someone without access |
| C4.5 | all nine Grafana dashboards render with data, no "datasource not found" |
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

ARGUS_TOKEN=<gitlab PAT> NODE_EXTRA_CA_CERTS=config/traefik/certs/tls.crt \
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
./scripts/test-gitlab/run.sh          # up, seed, verify, down
```

That one command replaces the sequence below, and takes the fixture down again
when it is finished — including when verification fails. The fixture is
`restart: "no"` now; it used to be `unless-stopped`, which meant it survived
reboots and sat at 2.63 GiB of RAM and 2.17% CPU indefinitely on a host whose
whole job is to keep the GPU busy with something else.

By hand, which is what `run.sh` wraps:

```bash
docker compose -f scripts/test-gitlab/docker-compose.yml up -d   # first boot: minutes
# Wait for `curl -fsS http://localhost:8929/-/readiness`. The container reports
# `healthy` several minutes before the API can answer, and seeding against a
# GitLab that is still reconfiguring fails in ways that look like a bad seed.
# seed.py shells out to `docker exec`, so it needs the CLI and the socket
docker run --rm --user root --network host \
  -v /var/run/docker.sock:/var/run/docker.sock \
  -v "$(command -v docker)":/usr/local/bin/docker \
  -v "$PWD/scripts/test-gitlab:/t" -w /t --entrypoint python argus:latest /t/seed.py
docker exec argus argus index --config /etc/argus/config.yaml
docker compose -f scripts/test-gitlab/docker-compose.yml down -v   # do not skip this
```

`verify.py` additionally needs `ARGUS_TEST_WORK` set to a path outside the
checkout on an NTFS host — SQLite in WAL mode cannot open its shared-memory file
over that filesystem and `argus index` dies with `disk I/O error`. `run.sh`
points it at a Docker volume for exactly that reason.

Three things that had to be right first, all now recorded in the code:

* Argus could not reach GitLab at all until the fixture was running, and every
  Open WebUI tool connection came back **401** — Argus rejects a chat user it
  cannot resolve access for, and Open WebUI drops the tool silently.
* The clone URL had to be rebased onto the configured GitLab: GitLab advertises
  `http://localhost:8929`, which inside a container is the container itself.
* `verify.py` wrote its report to `docs/verification-report.md`, a path the
  repository restructure left behind. It never failed — it just stopped updating
  the report anybody reads, which is the worst way for a document to break.

Note that a deployment pointed at this fixture (`ARGUS_GITLAB_URL` in `.env`)
has nothing to index while the fixture is down, so `ArgusIndexStale` fires and
the admin console's Overview says the repositories are out of date. That is the
system being right, not broken: the index genuinely cannot refresh.

---

## 6. Priority

Ordered by (risk × likelihood), not by effort:

| # | test | why first |
|---|---|---|
| 1 | **S6.2 restore round trip** | the only operation that can destroy data, and untested |
| 2 | **S1.5 env-samples render** | cheap; every sample is a promise the compose file has not broken |
| 3 | **S2 for the ten unreferenced services** | closes the largest named hole |
| 4 | **C2 TLS trust per runtime** | this is the failure users actually hit; four small tests |
| 5 | **S9.3 genuinely offline start** | the offline commits claim it; nothing checks it |
| 6 | **S3.4 hash-free account list** | just added, verified once by hand |
| 7 | **S9.1/S9.2 airgap round trip** | verified once by hand, easy to regress |
| ~~8~~ | ~~**Automate C3.5**~~ — **done.** `./scripts/test-gitlab/run.sh` runs the whole lifecycle, and `ARGUS_TEST_WORK` moves the index off the NTFS volume that SQLite's WAL mode cannot use. All sixteen tools are contract-tested over the wire; the six `docs_*` tools are reported as NOT COVERED because the fixture has no documentation pack installed |
| 9 | **S10 idempotency** | two `up`s, two indexes, one `down`/`up` |
| 10 | **C4 browser** | highest effort, and the only way to test the UI layer at all |

---

## 7. Running what exists

```bash
cd stack

# unit (no stack needed)
docker build --target test -t argus:test .. && docker run --rm argus:test

# with the stack up
make health          # container state + in-network probes
make smoke           # API surface
./scripts/domain-check.sh
./scripts/audit-auth.sh
./scripts/acceptance.py        # note: SKIP is not PASS
./scripts/functional-test.py   # the person-facing flows
./scripts/compare-dashboards.py  # the app's usage dashboards == Grafana's

# the web in a real browser (in app/frontend): desktop and phone, both themes,
# axe accessibility; E2E_CHAT=1 adds the chat against the real model
E2E_PASSWORD=<admin password> E2E_CHAT=1 npm run e2e
# ... and Argus's per-person access, with the test GitLab up
./scripts/test-gitlab/run.sh --keep   # from the repo root
E2E_ARGUS_USER=dev_beta E2E_ARGUS_PASSWORD=<theirs> E2E_PASSWORD=... E2E_CHAT=1 npm run e2e

# from inside the network
docker run --rm --network llm-net -e MK=<master-key> \
  -v "$PWD/scripts:/s:ro" python:3.13-slim python /s/e2e-check.py
```

For the Argus case the stack's Argus must index the test GitLab, and the
fixture's `dev_beta` must be a person in the app. Keep a copy of `.env` first,
because all of this is temporary:

1. In `.env`, add `argus` to `COMPOSE_PROFILES`, and set
   `ARGUS_GITLAB_URL=http://host.docker.internal:8929`,
   `ARGUS_GITLAB_AUTH=token` and `ARGUS_GITLAB_TOKEN` to the fixture's token
   from `scripts/test-gitlab/seeded.json` (not in git; it is a throwaway
   instance). Then run `docker compose up -d` and wait for the first index.
2. In the app, go to Admin → People and add `dev_beta` with the email
   `dev_beta@argus.test`. Argus matches the chat user by that email.
3. Afterwards:
   - put the old `.env` back and run `docker compose up -d --remove-orphans`
   - run `docker compose --profile argus rm -sf argus ollama`
   - delete `dev_beta` from the app
   - run `./scripts/test-gitlab/run.sh --down`

**Read the SKIP lines.** A green `acceptance.py` on the default profiles has not
exercised tracing, logging, cadvisor, dcgm, the second model or Ollama.
