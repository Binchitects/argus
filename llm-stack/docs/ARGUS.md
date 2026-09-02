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

`deploy/argus-local.yml` bind-mounts `${ARGUS_HOME}/deploy/test-gitlab/work/index.db`
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

## Connecting an agent

Argus is a plain StreamableHTTP MCP server, so any MCP-capable client can use
it. For the Hermes workstation client:

```yaml
mcp_servers:
  argus:
    url: https://argus.llm.localhost/mcp
```

See [MODELS.md](MODELS.md) for pointing the same client at the inference
gateway.

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
| Empty `repo_map`, no symbols | Index is empty; run the indexer, or use `deploy/argus-local.yml` |
| Connection error from a client | `NO_PROXY` missing the domain, or hosts entry absent |
| `ARGUS_HOME` error on `up` | The overlay is in use but the variable is unset |
