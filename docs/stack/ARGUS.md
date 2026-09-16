# Argus


Argus is a code index and documentation server. It answers questions about
**your** repositories and about **public API documentation**, and it exposes
both through the Model Context Protocol (MCP) so an agent can call them as
tools rather than guessing from memory.

It runs under the `argus` profile and is reachable at
`https://argus.<LLM_DOMAIN>/mcp`.

---

## Why it is in this stack

A local model is good at reasoning and bad at recall. Header names, import
libraries, IRQL constraints, error codes and command flags are exactly the
facts a model states confidently and gets wrong. Argus puts those facts in
front of the model at answer time.

The effect is measurable, and it comes from *copying* rather than
*paraphrasing*. Argus's own server instructions report that across five real
driver files, contract claims made from memory were wrong 100% of the time,
claims made with the documentation present but reworded were wrong 33% of the
time, and claims quoted verbatim were wrong 0 times out of 18.

---

## What it serves

**Your code.** Argus indexes GitLab repositories: symbols, references, include
graphs and file contents. `find_symbol`, `find_references`, `search_code`,
`semantic_search`, `get_file`, `repo_map`, `which_repo`, `impact_of` and
`code_contracts` work across every repository the caller is allowed to see.

**Documentation packs.** Self-contained archives of public reference material —
Windows SDK and WDK, MSVC C++, cppreference, .NET, Python, PowerShell and shell
tooling, algorithms, system design. `docs_lookup` (you know the name),
`docs_find` (you know only the behaviour), `docs_search` + `docs_get` (you need
the page) and `docs_verify` (check a draft you already wrote).

Sixteen tools in total. Packs are licensed material — each result carries its
own attribution, e.g. WDK pages are CC BY 4.0.

---

## Authentication

**Argus does not use Authelia.** Every caller presents their own GitLab
personal access token as a bearer token, and Argus uses that token to decide
which repositories they may see. Replacing it with an SSO session would erase
the per-caller identity that the ACL depends on, so the Traefik route applies
`default-chain@file` and passes the `Authorization` header through untouched —
the same reasoning as the LiteLLM gateway route.

```
anonymous                    ->  401
valid GitLab PAT             ->  only that user's repositories
ARGUS_GITLAB_TOKEN (service) ->  everything the service account can read
```

`ARGUS_GITLAB_TOKEN` in `.env` is the **privileged service token**. It is used
for indexing, never handed to a developer, and never leaves the host.

Documentation packs are public reference material and are readable by any
authenticated caller regardless of repository access.

---

## When no token can be issued for the account

Everything above assumes someone can create a personal access token for the
service account. That is not always possible: an administrator can withhold
token creation from an account or a group, and a locked-down GitLab may only
offer the sign-in form. For that case Argus can sign in with a username and a
password.

```dotenv
ARGUS_GITLAB_USERNAME=svc-argus
ARGUS_GITLAB_PASSWORD=...
```

Set `ARGUS_GITLAB_AUTH=password` as well to be explicit. It is *inferred*
whenever a username is present, and — worth knowing before it bites — **a
username wins over a token**. A username left in `.env` from an earlier
experiment keeps password mode on even after `ARGUS_GITLAB_TOKEN` is filled in,
so a stale username silently outranks a working token. Naming the mode removes
the ambiguity.

Argus then does what a browser does: fetch GitLab's sign-in form, post the
username and password to `/users/sign_in` with the form's CSRF token, check the
session against `/api/v4/user`, and create a personal access token through the
web endpoint carrying `read_api` and `read_repository` — nothing wider. Every
request after that uses the minted token, and the password reaches exactly one
request. Before minting, a token Argus created on a previous run is revoked, so
restarts do not accumulate credentials.

The token is not the session cookie, because `git`'s askpass protocol has no way
to present a cookie and git-over-HTTP rejects one outright (measured: `401` on
`info/refs`). That is also why the whole sign-in exists rather than a simpler
"just use the session".

Two consequences follow, and both are deliberate:

**The password is worth more than the token it produces.** It is reusable
everywhere that account signs in, so it is a bigger secret than a read-only
PAT. Anything that can read this container's environment — `docker inspect`, a
crash dump, a support bundle — reads it. Prefer a token whenever one is
possible; also run `./scripts/airgap-bundle.sh` rather than `--with-env` when
shipping a bundle, since the former empties every `*PASSWORD` by name.

**A minted token expires.** With no `expires_at` GitLab applies its own default
(measured: one year), and an administrator can revoke the token at any moment.
Rather than leave a server authenticating with a credential that is simply gone,
Argus treats a `401` on a read as "the token died, get another": it forgets the
cached token, signs in again, and retries the request once. A second `401`
is reported as-is, so a genuinely unauthorised account does not loop. Only
password mode retries — a static token that GitLab rejects is wrong, and
retrying it produces the same answer twice.

Two failures are reported by name rather than as a bad password, because
changing the password does not fix either:

- the account has **two-factor authentication**, which a scripted sign-in cannot
  satisfy — use a token;
- the account may sign in, but an administrator has disabled **token creation**
  for it.

---

## GitLab on a private CA

Three `.env` values point Argus at your GitLab:

| `.env` | meaning |
|---|---|
| `ARGUS_GITLAB_URL` | which GitLab. **Overrides** `url:` in `config/argus/config.yaml`, which is a committed default naming a throwaway test instance |
| `ARGUS_GITLAB_CA_CERT` | path to the CA that signed GitLab's certificate, **as the container sees it** |
| `ARGUS_GITLAB_VERIFY` | `false` to skip verification entirely. Last resort |

`ARGUS_GITLAB_CA_CERT` and `ARGUS_GITLAB_VERIFY` both apply to the API **and** to
every `git clone`, because Argus reaches GitLab over two transports that cannot
see each other's TLS configuration. Configuring one and not the other is the
failure that costs the most time, because it reads as a bad credential: the API
enumerates every project, and the first clone then dies with

```
server certificate verification failed. CAfile: none CRLfile: none
```

Drop the PEM in `config/argus/tls/` — a directory that exists in the repo so the
bind mount never has to be created by Docker — and it appears inside the
container at `/etc/argus/tls/<name>`:

```dotenv
ARGUS_GITLAB_CA_CERT=/etc/argus/tls/gitlab-ca.pem
```

The public roots stay loaded alongside it, so a GitLab that redirects to a public
host keeps working.

When the certificate is self-signed and **no CA file exists anywhere**, set:

```dotenv
ARGUS_GITLAB_VERIFY=false
```

That turns verification off for every request and every clone to that GitLab, so
anything able to answer on its hostname can read `ARGUS_GITLAB_TOKEN` — the most
privileged string in the deployment. It is a last resort, not a convenience.
Setting it **and** `ARGUS_GITLAB_CA_CERT` is refused at startup rather than
silently resolved, and `ARGUS_GITLAB_VERIFY` rejects any value that is not a
boolean rather than guessing, because guessing wrong toward "do not verify" is a
security bug. `config/argus/tls/README.md` repeats this next to the directory
itself.

Verify the token before indexing. Against a private CA, `curl` needs the same
treatment:

```bash
curl -s --cacert config/argus/tls/gitlab-ca.pem \
  -H "PRIVATE-TOKEN: $ARGUS_GITLAB_TOKEN" \
  "$ARGUS_GITLAB_URL/api/v4/projects?membership=false&simple=true&per_page=100" \
  | python3 -c "import json,sys; print(len(json.load(sys.stdin)))"
```

---

## Storage: drop-in vs. reusing an existing index

The base `docker-compose.yml` is **self-contained**. Argus gets a named volume
(`argus-data`) and a config file that travels with the repo
(`config/argus/config.yaml`), so a fresh host starts with an empty index and
populates it by running the indexer. Nothing points at a path outside the
project.

To reuse an index and pack estate that already exist on this machine — the
index is ~305 MB and the pack estate ~3.7 GB, so re-indexing is not free — add
the overlay:

```bash
docker compose -f docker-compose.yml -f deploy/argus-local.yml up -d
```

`stack/deploy/argus-local.yml` bind-mounts `${ARGUS_HOME}/scripts/test-gitlab/work/index.db`
and `${ARGUS_HOME}/packs` in place of the volume, and fails fast if `ARGUS_HOME`
is unset. **The index is mounted read-write, not read-only**: the server writes
audit and ACL-cache rows on every authenticated request, so a read-only mount
makes it fail at request time rather than at startup.

---

## The Host header

Argus validates `Host`. Its built-in default only accepts `argus.internal`, so
every request arriving through Traefik would be rejected before reaching a
handler. The compose `command:` therefore passes the proxy hostname explicitly:

```yaml
- --allowed-host=argus.${LLM_DOMAIN:-llm.localhost}
- --allowed-host=argus.${LLM_DOMAIN:-llm.localhost}:*
- --allowed-host=argus
- --allowed-host=argus:*
```

If you change `LLM_DOMAIN`, these follow automatically. If you put Argus behind
a different name, add it here or every request returns a Host-validation error.

---

## Checking it works

Health needs no credentials; everything else does.

```bash
curl -sk https://argus.llm.localhost/healthz
```

An unauthenticated MCP call must be refused:

```bash
curl -sk -o /dev/null -w '%{http_code}\n' -X POST https://argus.llm.localhost/mcp \
  -H 'Content-Type: application/json' -d '{}'
```

`200` then `401` means the route and the auth boundary are both correct.

A full MCP session is three steps — `initialize`, which returns an
`Mcp-Session-Id` header, then `notifications/initialized`, then real calls.
Every request needs `Accept: application/json, text/event-stream`; responses
come back as SSE `data:` lines, not plain JSON.

```bash
curl -sk -D - -X POST https://argus.llm.localhost/mcp \
  -H "Authorization: Bearer $ARGUS_GITLAB_TOKEN" \
  -H 'Content-Type: application/json' \
  -H 'Accept: application/json, text/event-stream' \
  -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"probe","version":"1"}}}'
```

A healthy server reports `serverInfo.name = argus`, 16 tools, and its
instructions block. Two useful end-to-end probes:

- `docs_lookup` with `{"name":"FltRegisterFilter"}` should return
  `IRQL: <= APC_LEVEL` from the WDK pack — proves the pack estate is mounted.
- `find_symbol` with a symbol from one of your repositories should return a
  repo, path and line — proves the GitLab ACL and the index are both live.

---

## Who asked what: Argus in Grafana

Every tool call and every refused request is written to the audit table in
Argus's sidecar database, and **also printed as one JSON line on stdout**
(`argus/auditlog.py`):

```json
{"ts": "2026-09-14T06:48:04Z", "event": "tool_call", "tool": "get_file", "user": "dev_alpha", "user_id": 2, "outcome": "ok", "error": null, "duration_ms": 16.6, "repos_visible": 1, "args": {"repo_id": 3, "path": "src/decoder.c"}}
{"ts": "2026-09-14T06:44:54Z", "event": "denied", "reason": "missing_token", "path": "/mcp"}
```

`outcome` is `ok`, `no_access` (only repositories the person cannot read
matched; Argus named the maintainers instead) or `error`. `denied` means no
bearer token, or a GitLab token GitLab rejected; no tool ran.

Promtail ships these lines to Loki — `logging` is in the default
`COMPOSE_PROFILES`, so this works on a fresh deployment — with `event`,
`outcome` and `tool` as labels. The **Argus** dashboard in Grafana shows calls
by tool and by person, no-access answers, errors, refusals, p95 latency, and a
searchable audit trail with each call's arguments. Calls from Qwen Code, Claude
Code or any MCP client and from Open WebUI all appear under the person's GitLab
username.

Indexing writes to the same stream, which is what gives a pass a history
instead of only an exit code. `argus index` emits one line per event —
`index_start`, `index_repo` (one per repository and branch, with `outcome` of
`ok`, `up_to_date`, `timed_out`, `symbols_failed`, `failed`, `mirror_failed` or
`no_branches`) and `index_end`. The **Indexing** dashboard charts those: runs
by outcome, failures named by repository, time per repository, files indexed per
pass, and the raw log. `repo` and `branch` are JSON fields rather than labels on
purpose — an estate can have thousands of repositories, and a label each would
multiply Loki streams for nothing. Filter with `| json | repo="group/name"`.

`no_branches` is the one outcome that is not a problem: the project exists in
GitLab and has no commits, so there is nothing to index. It used to produce no
event, no log line and no database row at all, which meant a pass reported four
repositories and the index held three with nothing anywhere saying which one
was missing. An empty repository deliberately gets **no** `repos` row, because
that table is what staleness is measured against and a repository that can never
be indexed would alert forever.

The admin panel's **Indexing** card reads the same run directly through Argus's
admin endpoint, so it shows the live tail without waiting for Loki, and keeps
that log on screen after the run ends. It also says whether automatic
reindexing is on, in the units a person reads (`every 15 minutes`), because
"do I have to press this button every time?" was previously answerable only by
finding a cron job that did not exist.

`ARGUS_AUDIT_LOG=0` on the argus service stops the stdout lines; the audit table
is unaffected. Loki keeps logs for 14 days (`config/loki/loki-config.yml`).

---

## Is the index still telling the truth?

Every alert rule in this stack answers a question about the machine: is the GPU
too hot, is the disk filling, is an engine listening. All of them can be green
while Argus answers every question out of an index that stopped updating last
Tuesday, and an out-of-date index is the worst failure this stack has — the
answers stay exactly as confident as they were on the day the data was good.
Nothing goes red, so nothing looks wrong.

Two things close that hole, and they only work together.

**The index reindexes itself.** `ARGUS_INDEX_INTERVAL` (default 900 s) makes the
`argus` serve process run a pass on a timer. It runs *inside* the serve process
rather than as a second container because both would write the same SQLite
index, and `store.connect` sets no `busy_timeout`: the two would fail each other
with `database is locked` and neither failure would say why. Sharing one
`_index_lock` also means a scheduled pass is visible on the console's Indexing
page exactly like a manual one — progress, log and exit code — and it clears
the staleness below the moment it finishes.

If the index is found stale or empty at startup, the first pass runs after 30
seconds instead of waiting out a full interval. A fresh drop-in deployment with
an empty named volume indexes itself rather than sitting empty for fifteen
minutes while the console insists everything is fine.

**The index is measured, and it alerts.** `GET /admin/metrics` on port 7700
exports, per repository and branch:

| Metric | Meaning |
| --- | --- |
| `argus_index_last_run_timestamp_seconds` | Unix time of the last pass; **0** if it has never run, so a repository cannot hide from a rule by being absent |
| `argus_index_last_indexed_timestamp_seconds` | the last pass that actually changed something |
| `argus_index_files`, `argus_index_symbols` | how big the indexed copy is |
| `argus_index_stale` | 1 when there has been no successful pass within `ARGUS_INDEX_STALE_AFTER` |
| `argus_index_timed_out`, `argus_index_symbols_failed`, `argus_index_errored` | what went wrong on the last pass |
| `argus_index_repos`, `argus_index_stale_repos`, `argus_index_errored_repos`, `argus_index_scrape_ok` | the estate-wide roll-up, and whether this scrape could read the index at all |

The endpoint is under `/admin/`, so it carries a credential — it names every
repository in the estate, and an open endpoint for that is a map of the
organisation handed to anything that can reach the port. The stack sends the
same `ARGUS_ADMIN_TOKEN` the console uses, as a bearer token, because
Prometheus can only read a credential from a file
(`authorization.credentials_file`). `prometheus-secrets` writes it out of
`.env` on every `up`, so there is one secret to rotate rather than two.

`config/prometheus/rules/argus.yml` turns those into four alerts:
`ArgusIndexStale` (per repository), `ArgusIndexErrored`,
`ArgusIndexUnreadable` (the index query itself failed — the scrape still
returns 200 and `up` is still 1, which is why this watches
`argus_index_scrape_ok` rather than `up`) and `ArgusIndexEmpty` (Argus is up
and knows about nothing, which used to present as an empty table that looked
like a fresh install).

The console's **Overview** reads the same computation, so the tile, the Grafana
line and the alert cannot disagree; `CONTRIBUTING`-style threshold changes
belong in `.env`, not in the console.

---

## Connecting an agent

Argus is a plain StreamableHTTP MCP server, so any MCP-capable client can use
it. For the Hermes workstation client:

```yaml
mcp_servers:
  argus:
    url: https://argus.llm.localhost/mcp
```

See [HERMES.md](HERMES.md) for the whole client setup -- model and
Argus together -- and the top-level README for the gateway itself.

**On Windows, check your proxy exclusions.** If `HTTP_PROXY`/`HTTPS_PROXY` are
set, `NO_PROXY` must include the stack's domain or the client tries to reach
`argus.llm.localhost` through the corporate proxy and fails with a bare
"Connection error":

```
NO_PROXY=localhost,127.0.0.1,::1,.local,llm.localhost,.llm.localhost
```

`*.localhost` also resolves automatically in browsers but **not** in curl or
SDK clients — run `scripts/setup-hosts` once so `argus.llm.localhost` resolves
from the command line.

---

## Troubleshooting

| Symptom | Cause |
|---|---|
| `401` on every MCP call | No bearer token, or the GitLab PAT is expired |
| Host-validation error | Proxy hostname missing from `--allowed-host` |
| `readonly database` | Index bind-mounted `:ro`; it must be `:rw` |
| Container unhealthy for ~90 s at boot | Normal — the first request opens every pack |
| Empty `repo_map`, no symbols | Index is empty; run the indexer, or use `stack/deploy/argus-local.yml` |
| Connection error from a client | `NO_PROXY` missing the domain, or hosts entry absent |
| `ARGUS_HOME` error on `up` | The overlay is in use but the variable is unset |
