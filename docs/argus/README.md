# Argus


Argus is a code index and documentation server. It answers questions about
**your** repositories and about **public API documentation**, and it exposes
both through the Model Context Protocol (MCP) so an agent can call them as
tools rather than guessing from memory.

It runs under the `argus` profile and is reachable at
`https://argus.DOMAIN/mcp`.

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

`semantic_search`, `which_repo` and `docs_find` turn a question into a vector
with `nomic-embed-text-v1.5` on **`llamacpp-embed`**: llama.cpp on the CPU,
speaking the OpenAI protocol, so the engine keeps the whole GPU. Measured warm on
the reference host, a query embeds in **15 ms median** on 4 cores
(`EMBED_CPUS`, `EMBED_THREADS`), a fraction of what the Python Argus's CPU
embedder took (94 ms warm, 2,254 ms cold). `embed-init` fetches the model once
into `EMBED_MODEL_DIR` and checks it against Hugging Face's SHA-256; the
acceptance run checks that the embedder answers with `ARGUS_EMBED_DIM` (768)
dimensions, the width the index and every pack were built with.

---

## Checking a draft: `docs_verify` and `argus verify`

`docs_verify` is a tool the model can call on an answer it has already written.
It reports only what the documentation **contradicts** — a claim it confirms, or
says nothing about, passes through untouched — because the measured harm here is
a retrieval miss displacing something the model had right: pack context placed
*before* an answer took Win32 accuracy from 5/5 to 1/5.

But a tool is advisory, and the failure this was built for is a model that makes
no tool calls at all. Measured: asked to review kernel code, one model answered
in 2.2 seconds naming `wcscpy_s` (user-mode), `<string.h>` (user-mode) and
`ucrt.lib` (user-mode) — three real things and the wrong answer to a question
about kernel code. The instructions already told it to verify. It did not.

`argus verify` is the same check with an exit code, which is what a *hook* can
use — every agent client can run a shell command when the model finishes and
block on the result, and almost none can be made to call an MCP tool at that
moment.

```bash
argus verify --config /etc/argus/config.yaml --text-file -   # draft on stdin
argus verify --config /etc/argus/config.yaml --text "..." --json
```

| exit | meaning | what a hook should do |
|---|---|---|
| `0` | nothing contradicted — including when the packs are silent about every identifier, and when the draft is empty | let the turn end |
| `2` | the documentation contradicts the draft; the contradictions are on stderr | **block**, and hand stderr back to the model |
| `6` | could not check — no packs installed, or unreadable ones | let the turn end |

`6` is not `2` on purpose. A mandatory verifier that cannot verify must not
become an agent that can never finish a sentence, so everything except a
contradiction fails open, including an unexpected error. The contradiction text
is phrased as an instruction rather than a fact — the reader is a model that has
just finished an answer and has to decide what to do next.

`clients/claude-code/verify-after.sh` wires it into Claude Code's `Stop` hook.
See [clients/README.md](../../clients/README.md#forcing-verify-after).

---

## Authentication

**Argus does not use the app's sign-in.** Every caller presents their own GitLab
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

In the platform's `deploy/.env` (they reach Argus as `ARGUS_GITLAB_USERNAME`
and `ARGUS_GITLAB_PASSWORD`; a standalone Argus takes those names directly):

```dotenv
GITLAB_URL=https://gitlab.example.com
GITLAB_USERNAME=svc-argus
GITLAB_PASSWORD=...
```

Password mode is *inferred* whenever a username is present, and — worth
knowing before it bites — **a username wins over a token**. A username left in
`.env` from an earlier experiment keeps password mode on even after
`GITLAB_TOKEN` is filled in, so a stale username silently outranks a working
token: empty `GITLAB_USERNAME` when you move to a token. (A standalone Argus
can name the mode with `ARGUS_GITLAB_AUTH=token` or `password`.)

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

## Storage

The base `docker-compose.yml` is **self-contained**. Argus gets a named volume
(`argus-data`: the index, the installed packs, the audit log) and a config file
that travels with the repo (`config/argus/config.yaml`), so a fresh host starts
with an empty index and populates it by running the indexer. Built knowledge
packs are loaded from the pack library (the repository's `packs/`, or
`ARGUS_PACK_LIBRARY_DIR`) under **Admin → Packs**.

---

## The Host header

Argus validates `Host`. Its built-in default only accepts `argus.internal`, so
every request arriving through Traefik would be rejected before reaching a
handler. The compose `command:` therefore passes the proxy hostname explicitly:

```yaml
- --allowed-host=argus.${DOMAIN:-llm.localhost}
- --allowed-host=argus.${DOMAIN:-llm.localhost}:*
- --allowed-host=argus
- --allowed-host=argus:*
```

If you change `DOMAIN`, these follow automatically. If you put Argus behind
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

## Who asked what: the Argus dashboard

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

Promtail ships these lines to Loki — on every deployment — with `event`,
`outcome` and `tool` as labels. The app's **Argus** dashboard (Observe → Dashboards) shows calls
by tool and by person, no-access answers, errors, refusals, p95 latency, and a
searchable audit trail with each call's arguments. Calls from Qwen Code, Claude
Code or any MCP client and from the platform's chat all appear under the
person's GitLab username.

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

The app's **Admin → Indexing** page reads the same run directly through Argus's
admin endpoint, so it shows the live tail without waiting for Loki, and keeps
that log on screen after the run ends. It also says whether automatic
reindexing is on, in the units a person reads (`every 15 minutes`), because
"do I have to press this button every time?" was previously answerable only by
finding a cron job that did not exist.

`ARGUS_AUDIT_LOG=0` on the argus service stops the stdout lines; the audit table
is unaffected. Loki keeps logs for 14 days (`config/loki/loki-config.yml`).

---

## What the code does, not what it is called

Argus indexes each symbol's **doc comment** — the sentence above a definition —
and uses it both to match and to answer.

It did not always. The embedded text was a symbol's kind, name, signature, scope
and path: what it is *called*, and nothing about what it *does*. So a question
phrased the way a person asks it could only match vocabulary. Measured on a real
corpus: asked "what expires keys past their TTL", semantic search returned
`expireSlaveKeys` — which contains the words "expire" and "keys" and does
something else entirely. The routine that actually reclaims expired keys has
neither word in its name.

The sentence was in the index the whole time. `files.content` holds the entire
file and each symbol records the line it starts on, so the comment above a
definition had been in the database, unread, since the first version. Argus now
reads it into `symbols.doc`, leads the embedded text with it, and returns it on
every symbol-level result.

Verified on a fixture built to be falsifiable — two functions with equally
plausible names and opposite documentation:

| question | matching on names | matching with the doc |
|---|---|---|
| "what reclaims keys whose time to live has elapsed" | the wrong one | **the right one** |
| "propagate an expiry decision to a replica" | the wrong one | **the right one** |

Symbols with no doc comment score identically either way, so the doc only adds
where somebody wrote one. Two things worth knowing: a doc comment that says what
a function does **not** do is evidence for the thing it negates, and the embedded
text carries its own version (`EMBED_TEXT_VERSION`) so that changing what goes
into a vector rebuilds the vectors rather than only the symbols edited next.

Re-index to pick this up on an existing deployment. Adding the column bumps the
symbol-extractor contract version, so the **first pass re-parses every file**
once and then reports `embedded: N` — the index pass now embeds what it indexed,
which is what keeps `semantic_search` current on a stack where nobody runs
`argus embed` by hand. See [Indexing on push](#indexing-on-push-and-merge) for why that
mattered.

### Asking what you have

For "what do you have that does X", use `semantic_search`: it matches on meaning
and every result carries the symbol's `doc`, which is the sentence that tells
you whether it is the one you want. Read it before choosing.

In an unfamiliar estate, call `overview` first. It describes what each repository
**is** — its README, its layout, the abstractions somebody documented, and the
cross-repo dependencies — so searching starts from somewhere instead of from a
guessed name. A list of symbol names is not an architecture.

---

## Is the index still telling the truth?

Every alert rule in this stack answers a question about the machine: is the GPU
too hot, is the disk filling, is an engine listening. All of them can be green
while Argus answers every question out of an index that stopped updating last
Tuesday, and an out-of-date index is the worst failure this stack has — the
answers stay exactly as confident as they were on the day the data was good.
Nothing goes red, so nothing looks wrong.

Two things close that hole, and they only work together.

**The index reindexes itself.** In this stack the app asks Argus for a pass on
the **schedule** set under **Admin → Indexing** (every 15 minutes until
changed; in words, or cron, in a time zone; empty = only pushes and **Index
now**). Argus's own timer, `ARGUS_INDEX_INTERVAL`, is for an Argus that runs
without the app (`0`, off, here). Either way the pass runs *inside* the serve
process rather than as a second container because both would write the same
SQLite index: the two would fail each other with `database is locked`. Sharing
one index lock also means a scheduled pass is visible on the Indexing page
exactly like a manual one — progress, log and exit code — and it clears the
staleness below the moment it finishes.

With Argus's own timer, if the index is found stale or empty at startup the
first pass runs after 30 seconds instead of waiting out a full interval.

### Choosing what is indexed

**Admin → Indexing → Repositories** lists every repository the service account
can see (each pass refreshes the list; **Refresh from GitLab** does at once):

- **Indexed** on or off per repository, or for several at once. Off takes it out
  of the index at once, its files, symbols and text search with it (after the
  pass running, if one is). **New repositories** says whether one GitLab lists for
  the first time is indexed (by default, yes: as before).
- **Branches**: the default branch is always indexed; the dialog lists the
  repository's branches as GitLab has them, each with its latest commit, to tick
  more, and takes patterns (`release/*`) for branches to come. Each branch is an
  index of its own. `ARGUS_INDEX_BRANCHES` still adds patterns for every
  repository, and **Index now** takes extra ones for one run.
- **Index**: each indexed branch at its commit — hash (linked to GitLab),
  subject line, when it was committed and last checked, its files and symbols,
  and whether it is current, out of date or failed.
- **Update** brings one repository up to date from its latest commits now (only
  what changed since the commit indexed is read again), or after the pass
  running, queued like a push.
- While a pass runs, **Index now** shows how far it is (a percentage over the
  repositories, and of the one on now its changed files), and each repository's
  row says **Indexing** with its percentage, or **Queued**. The index process
  reports this as `@progress` lines on stdout, which the server keeps out of
  the run log.

The choices live in the index database (`repo_choices`, migration 016), so a
standalone Argus has them too (`/admin/repos`).

### Indexing on push and merge

The poll is the floor, not the mechanism. With it alone a push sits unindexed
for up to fifteen minutes, and every answer in that window comes from the
previous commit with nothing saying so — the same failure as a stopped index,
just smaller and more frequent. Point a GitLab webhook at Argus and the change
is indexed in seconds.

**Admin → Indexing → Push and merge webhook → Turn on** makes the secret. The
app shows it once, with the address and the steps; Argus keeps only its SHA-256
(in the index database, `argus_meta`), and the app keeps nothing. In GitLab,
on the group (a group webhook covers every project in it) or on one project:

```
URL:          https://argus.<domain>/hook/gitlab
Secret token: <the secret, as the page showed it>
Trigger:      Push events, Merge request events
```

Making a webhook takes a Maintainer (project) or an Owner (group) in GitLab,
once, by hand: Argus's own token stays read-only. Leave SSL verification on
only with a trusted certificate (`ACME_EMAIL`). A GitLab on the same network
refuses local addresses until **Admin → Settings → Network → Outbound requests
→ Allow requests to the local network from webhooks** is set.

A merge request counts only when it is **merged**: that is when its target
branch changes. Opened, updated or closed, it is acknowledged and ignored. The
card lists the last 30 deliveries (what came, for which repository, what came
of it, a refused secret included), and **New secret** and **Turn off** do what
they say; a rotation stops the old secret at once.

GitLab sends the secret back in `X-Gitlab-Token`; Argus hashes it and compares
in constant time, and answers `202`. With no secret set the route answers
`404`, so an unconfigured deployment has no unauthenticated way to make the
indexer run. It is a separate secret from `ARGUS_KEY` on purpose: this one is
stored in GitLab's own configuration, so it is the lower-privilege credential.
Leaking it lets somebody cause an index pass; leaking the admin token lets them
read the estate. A standalone Argus can still take it from
`ARGUS_WEBHOOK_TOKEN`; the environment's value then wins, and the page says so.

A push that arrives while a pass is running is **queued**, not dropped — on a
busy estate that is the normal case, and dropping it would mean the change waits
for the next poll, which is exactly the latency the webhook exists to remove.
The queue drains one repository per finished pass, so the passes never contend
for the same SQLite write lock, and the same repository asked for twice is
queued once. Past 25 queued repositories the backlog collapses into a single
full pass, because indexing everything once is cheaper than working through the
list. The Indexing page shows the queue, and each pass says whether a person,
the schedule or a webhook started it.

Deliveries Argus has no use for are still **acknowledged** — a tag push, an
issue, a branch deletion, a merge request not yet merged. GitLab treats a non-2xx as a failed delivery, retries
with backoff and eventually disables the webhook, so answering `400` to a tag
push would cost the operator their webhook over a non-problem.

The poll stays on. It is what covers a missed delivery, a webhook nobody
configured, and a repository no webhook fires for.

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
same `ARGUS_KEY` the app uses, as a bearer token, because
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

The app's **Admin → Overview** reads the same computation, so the tile, the
dashboard line and the alert cannot disagree; `CONTRIBUTING`-style threshold changes
belong in `.env`, not in the app.

---

## Connecting an agent

Argus is a plain StreamableHTTP MCP server, so any MCP-capable client can use
it. For the Hermes workstation client:

```yaml
mcp_servers:
  argus:
    url: https://argus.llm.localhost/mcp
```

See [hermes.md](../hermes.md) for the whole client setup -- model and
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
| Container unhealthy for ~90 s at boot | Normal — the first request opens every pack |
| Empty `repo_map`, no symbols | Index is empty; run the indexer (Admin → Indexing) |
| Connection error from a client | `NO_PROXY` missing the domain, or hosts entry absent |
