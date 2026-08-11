# From zero to working agents

Everything needed to stand Argus up on a new machine and connect Hermes to it.
Ordered so that each step is verifiable before the next one depends on it --
the failures this project actually hit were mostly silent, and each check
below exists because something once looked fine and was not.

---

## 1. What runs where

| piece | where it runs | why |
|---|---|---|
| Argus MCP server | one server, near the GitLab instance | holds the private index; cloning repos over a WAN is the slow part |
| knowledge packs | on that same server | 1.4 GB of SQLite, opened per request |
| Ollama (embeddings) | same host as Argus | `docs_search` embeds every query; a network hop per search is worse than the search |
| Hermes | each developer's PC | connects to Argus over HTTPS with a per-developer token |

Developers install nothing but Hermes. The packs are **not** distributed to
laptops.

---

## 2. Prerequisites

* Python 3.13
* `git` on PATH
* Ollama, with `nomic-embed-text` pulled
* A GitLab service token that can see **every** repository you intend to index

```bash
ollama pull nomic-embed-text
```

**Verify the token before anything else.** `argus/gitlab.py` enumerates with
`membership=false`, which for a non-admin token returns only *public*
projects. Measured on a test instance: an admin token saw 3 of 3 private
projects, a non-admin token saw 1 of 3.

```bash
curl -s -H "PRIVATE-TOKEN: $ARGUS_GITLAB_TOKEN" \
  "$GITLAB_URL/api/v4/projects?membership=false&simple=true&per_page=100" \
  | python -c "import json,sys; print(len(json.load(sys.stdin)))"
```

If that count is lower than what you actually have, stop. Every later
measurement will be confidently wrong. `argus index` refuses to run in this
state, but check anyway -- it is cheaper than discovering it later.

---

## 3. Index the private code

```bash
export ARGUS_GITLAB_TOKEN=<service token>
export ARGUS_GIT_ASKPASS_TOKEN=$ARGUS_GITLAB_TOKEN
argus index --config /etc/argus/config.yaml
argus status --config /etc/argus/config.yaml
```

Measured on 10,212 files across 12 repositories: 7 m 48 s cold, zero errors.
Roughly linear in files, so estimate from your own first run rather than from
this one.

Branches: by default only each project's default branch is indexed. To index
release branches too:

```yaml
index:
  branches: ["main", "v*"]
```

The default branch is always indexed whether or not it matches a pattern.
Cost is roughly linear in branches matched.

Keep it fresh with `argus index --interval 3600`, or the `refresher` compose
service.

---

## 4. Build the knowledge packs

```bash
bash deploy/build-packs.sh
```

Sequential on purpose: embedding is CPU-bound, and four concurrent builds pin
every core for hours. Ollama serialises the calls anyway, so concurrency costs
heat and buys nothing.

| pack | documents | size | build |
|---|---|---|---|
| system-design | 9 | 1.3 MB | < 1 min |
| algorithms | 371 | 4.3 MB | < 1 min |
| scripting | 9,302 | 70 MB | 13 min |
| cpp | 9,746 | 175 MB | 36 min |
| wdk | 28,176 | 359 MB | 74 min |
| win32 | 71,663 | 786 MB | 162 min |

About five hours in total, once. **Run it in a terminal that outlives your
session** -- a build killed part-way leaves no pack. It is resumable: finished
packs are skipped, and within a pack every embedding already computed is
reused from `packs/.embcache.db`. A rebuild after a source change took 7
minutes instead of 13 (45,631 embeddings reused, 396 computed).

Install what you built:

```bash
argus pack install packs/win32-1.0.pack --packs-dir /var/lib/argus/packs
```

Install the packs matching your work. Measured: they help most where the model
is ignorant (`win32` 9/30 -> 30/30) and not at all where it is fluent
(`cpp` standard library 26/30 -> 25/30).

---

## 5. Run the server

```bash
docker compose --profile server up -d
```

Check it answers, and that authentication is actually on:

```bash
curl -sf http://localhost:8080/healthz && echo OK
curl -s -o /dev/null -w '%{http_code}\n' http://localhost:8080/mcp   # expect 401
```

A 200 on `/mcp` without a token means the server is open. Stop and fix that
before handing out any address.

---

## 6. Connect Hermes

Hermes configures MCP servers from its own interface: **Settings -> MCP
servers -> Add**.

| field | value |
|---|---|
| Transport | Streamable HTTP |
| URL | `https://argus.your-domain/mcp` |
| Header | `Authorization: Bearer <developer's GitLab PAT>` |

The token is the **developer's own** GitLab personal access token, not the
service token. Argus resolves it to that person's repository memberships on
every request, so two developers asking the same question get answers scoped
to what each may see. Never distribute the service token to a workstation.

### The two client settings that matter

Both are measured, and both are worth more than any tuning:

**Use native function calling.** Hermes must present the tools through its
model's function-calling API, not as a text protocol the model is asked to
imitate. A text protocol scored 10/20 on a question set and *collapsed to
4/20* when told to check facts first, because the added prose broke the output
format. The same instruction under a proper tools schema improved results
instead.

**Pass the server's instructions through as the system message.** Argus sends
an `instructions` block at connect time saying that recollection of headers,
libraries and IRQLs is unreliable. Passing it through took tool use from 3 of
20 questions to 8, and accuracy from 12/20 to 14/20. An agent that discards it
leaves that on the table.

`deploy/agent_client_example.py` is a working ~120-line client doing both,
against a live server. Use it to check the connection independently of Hermes:

```bash
python deploy/agent_client_example.py https://argus.your-domain/mcp <PAT> \
    "Which import library must I link to call CryptAcquireContextW?"
```

It prints each tool call to stderr. If you see `-> docs_lookup(...)` and then
`Advapi32.lib`, the whole chain works.

### If the agent answers with one word, or nothing at all

Symptom: the client reports "no final response was produced", or the model
emits a single word. It looks like a refusal or a broken model. It is neither.

Ollama **silently clamps `num_ctx` to what fits memory** and reports no error.
Measured on a 63.7 GB machine with `qwen3.6`:

| | |
|---|---|
| model advertises (`qwen35moe.context_length`) | 262,144 |
| client requests (`options.num_ctx`) | 262,144 |
| **actually served** (`usage.total_tokens`) | **32,768** |

The client then sizes its prompt against the advertised figure -- an 81,850
character system prompt plus 46 tool schemas, about 36k tokens -- which
overflows the real window. Ollama truncates the prompt to 32,767 and **one
token** remains to generate. `finish_reason` comes back `length`, so the
client retries with a larger `max_tokens` (8192 -> 16384 -> 24576), raising a
limit that was never the binding constraint. Every retry fails identically.

Nothing in this chain errors. The only field that exposes it is
`usage.total_tokens`, and it lands on a suspicious power of two.

Fix by making the server actually serve the context:

```bash
OLLAMA_CONTEXT_LENGTH=131072 ollama serve
```

On Windows, persist it so a restart does not silently revert:

```powershell
[Environment]::SetEnvironmentVariable('OLLAMA_CONTEXT_LENGTH','131072','User')
```

Persisted variables reach only processes started *after* the environment is
refreshed -- a new login, or a newly launched shell. An already-running Ollama
keeps the old value, which is exactly how this reappears after a reboot that
"changed nothing".

Confirm the fix from the client side, not the server's: ask any question and
check that `usage.total_tokens` is no longer pinned to a round power of two.

Hermes additionally refuses to start against a window below **64,000** tokens,
which is a useful oracle -- a served 32,768 is half its structural minimum, so
the model is unusable with tools and no client-side setting can rescue it.

### If Hermes lists the server as configured but never calls its tools

The symptom is specific: Hermes shows `argus (http) - configured`, no error
appears anywhere, and the model answers questions about documented APIs with
"I was unable to locate any documentation for X". No `docs_*` tool is offered.

This is a startup race inside Hermes, not an Argus fault. Hermes discovers MCP
tools on a background thread, then waits a bounded time before the agent
snapshots its tool list **once** and never re-reads it. Discovery that lands
after the snapshot is invisible for the whole session.

Measured on a healthy local server:

| phase | time |
|---|---|
| Argus answering `initialize` + `tools/list` + `resources/list` + `prompts/list` | **27 ms** |
| building the HTTP client, importing the MCP SDK (client side) | ~1040 ms |
| **total `discover_mcp_tools()`, warm** | **1.07 s** |
| **total, cold process** | **2.89 s** |
| Hermes's wait before snapshotting | **0.75 s** |

Argus is 27 ms of that budget, so no server-side tuning can win the race; an
infinitely fast server still loses. Two things make the failure silent:

- the discovery thread's exceptions are swallowed into a `debug` log, and
- the automatic late-refresh net lives in `tui_gateway/server.py` and gates on
  `tui_gateway`'s own discovery thread. The desktop app starts from
  `hermes_cli.main dashboard`, which owns a *different* thread in
  `hermes_cli/mcp_startup.py`. The net reads a thread that was never started,
  sees `None`, and concludes discovery already finished -- which is exactly
  what it should conclude in the healthy case, so nothing is logged.

**Immediate workaround, nothing patched:** run `/reload-mcp` in Hermes. It
performs the same rebuild and the tools appear for that session. Manual, per
session.

**Durable fix:** raise the wait in `hermes_cli/mcp_startup.py` so it covers a
cold start. 5 s leaves real headroom over the measured 2.89 s; 1.5 s does not.
The cost is bounded -- the thread runs once per process and joining a finished
thread returns immediately, so an unreachable server delays one agent build by
at most that much, once, and a server that is not configured at all costs
nothing.

Verify the fix without guessing, in a fresh process:

```bash
python -c "import logging; from hermes_cli.mcp_startup import \
start_background_mcp_discovery as s, wait_for_mcp_discovery as w; \
s(logger=logging.getLogger('v'), thread_name='dashboard-mcp-discovery'); w(); \
from model_tools import get_tool_definitions as g; \
print(sorted(n for d in g(quiet_mode=True) \
for n in [d['function']['name']] if 'argus' in n))"
```

An empty list means the race is still lost. A list containing
`mcp_argus_docs_lookup` means the agent will see the tools.

This patch is applied to a vendored install and **will be overwritten when
Hermes updates.** Re-check it after every upgrade; the `/reload-mcp`
workaround always works in the meantime.

---

## 7. Verify it is actually helping

```bash
python evals/generate_questions.py /var/lib/argus/packs evals/mine.json 30
ARGUS_OLLAMA_URL=http://localhost:11434 \
    python evals/run_ab.py /var/lib/argus/packs evals/mine.json
```

This generates questions **from your own packs** and answers them with and
without retrieval. Ground truth comes from the documentation rather than from
whoever writes the test -- the mistake that invalidated five earlier rounds
here was expecting `PASSIVE_LEVEL` for `IoCreateDevice` when Microsoft
publishes `<= APC_LEVEL`, and scoring the correct answer as a failure.

Expect a large gap on API facts and roughly none on material the model already
knows. If retrieval does *not* win on your corpus, that is worth knowing
before you roll it out.

---

## 8. Setting up another PC

Nothing but Hermes:

1. Install Hermes.
2. Add the MCP server: URL, `Authorization: Bearer <their own PAT>`.
3. Confirm native function calling and pass-through of server instructions.

No packs, no index, no Ollama on the workstation. If a developer's answers are
worse than a colleague's, check those two client settings first -- they are
worth more than anything else on the client side.

---

## What to expect, honestly

| shape | without packs | with packs |
|---|---|---|
| API facts, question names the API | 38% | **84%** |
| MSVC diagnostics | 8% | **100%** |
| multi-claim code generation | 28% | **74%** |
| **real-world phrasing** (a bugcheck, a linker error) | **65%** | **75%** |
| questions the packs do not cover | 80% | 75% |

The headline figures are measured on questions that *name* the API. Real
questions often do not: "unresolved external symbol __imp_CryptAcquireContextW"
carries the identifier and retrieval nails it, while "a driver bugchecks in a
DPC" does not and the gain is small. Stack traces, linker errors and compiler
diagnostics all quote identifiers, which is the good case and also the common
one.

The last row is the tax: an agent retrieving on every prompt pays a little on
every unrelated question. It is worth paying, and it is worth knowing about.
