# Deploying Argus

Argus is deployed as Docker containers via a single `docker-compose.yml`, not
systemd units. There is exactly one deployment mechanism for the whole
project — the indexer (a batch job) and the MCP server (a daemon) share the
same compose file and the same pinned, build-verified image family.

This document covers: bringing the server up, the TLS proxy in front of it,
running the indexer, revoking access immediately with `flush-acl`, and where
Ollama fits into the network picture.

## Prerequisites

- Docker with Compose v2 (`docker compose version`).
- A `config.yaml` (see `config.example.yaml`) in the repo root.
- `ARGUS_GITLAB_TOKEN` — the privileged service token that mirrors every
  repository — exported in your shell or in a `.env` file next to
  `docker-compose.yml` (gitignored). Never put it in the compose file or the
  image.

## The shape: one batch job, one daemon

| Service | What it is | Starts with `docker compose up`? |
|---|---|---|
| `indexer` | Batch job. Mirrors GitLab, parses, writes the SQLite index. | **No** — carries the `indexer` compose profile specifically so a plain `up` never begins indexing. |
| `server` | Long-running MCP daemon (`argus serve`). Serves access-controlled retrieval to Hermes over Streamable HTTP. | Yes. |
| `caddy` | TLS reverse proxy in front of `server`. | Yes. |

Run the indexer explicitly, whenever you want a pass:

```bash
docker compose run --rm indexer index  --config /etc/argus/config.yaml
docker compose run --rm indexer status --config /etc/argus/config.yaml
```

`docker compose run` starts an explicitly-named service even though it
carries a profile that isn't active — only `up` (and anything that resolves
a service purely by being in the default profile set) respects the profile
gate. That's what keeps a bare `docker compose up` from ever kicking off an
index run.

## Keeping the index current

An index only advances when something runs `argus index`. Without a schedule
it silently ages: `index_status` reports the staleness faithfully, and
everyone reads stale answers anyway.

Run the refresher, which polls on an interval:

```bash
docker compose --profile indexer up -d refresher
```

It defaults to a pass every 900 seconds; set `ARGUS_INDEX_INTERVAL` to change
it. A failing pass does not stop the loop, so a briefly unreachable GitLab
costs one cycle rather than every cycle until somebody notices. Watch its
logs for a *repeated* non-zero exit -- that is the signal something needs
attention:

```bash
docker compose logs -f refresher
```

Exit 1 from a pass means "ran, but at least one repo is unhealthy", which is
distinct from 3 (GitLab) and 4 (indexing failure). A run that ends 1 every
cycle is a repo that has been failing to mirror for as long as that has been
true.

This is the periodic poll the design specifies as the GitLab push webhook's
fallback. The webhook itself is not built yet; the design notes the poll alone
is sufficient until it is.

**Choose an interval you can afford.** Each pass re-fetches every mirror. The
cost is dominated by repos that changed, but the walk is not free, and the
right number depends on a full-pass duration measured against your own GitLab.

`argus index` now ends with a resolution pass and a graph rebuild. Watch the
`ambiguous` count: a high proportion means many repos ship headers with the
same basename, and `which_repo` will be correspondingly weaker. `argus
resolve` re-runs both without re-indexing.

## Bringing the server up

```bash
docker compose up -d
docker compose ps          # expect: server, caddy — never indexer
```

`server` runs `argus serve --config /etc/argus/config.yaml --host 0.0.0.0
--port 7700 --allowed-host argus.internal --allowed-host argus.internal:*`
(baked into the Dockerfile's `server` stage's `CMD`). That
`--host 0.0.0.0` looks like it contradicts "binds localhost by default," and
it doesn't: `argus serve`'s actual *default* — the one that applies any time
you run it directly on a host, outside a container — is `127.0.0.1`. Inside
compose, `server` is its own container; Caddy is a *separate* container and
cannot reach another container's loopback interface, so the image's `CMD`
opts in to `0.0.0.0` deliberately, scoped entirely to the compose network.
The `server` service publishes no `ports:` to the host, so `0.0.0.0` here
never means "reachable from the LAN" — it means "reachable from Caddy," and
nothing else can reach it. **If you ever run `argus serve` directly on a
host** (not through this compose file), leave `--host` at its default and
put your own TLS terminator in front of it — do not bind `0.0.0.0` yourself
without one.

#### `--allowed-host` — required, and it must match `deploy/Caddyfile`

The MCP SDK's `FastMCP` protects every `/mcp` call with DNS-rebinding
protection: it only accepts requests whose `Host` header is on an explicit
allowlist, computed once when the server object is built. `deploy/Caddyfile`
reverse-proxies with `flush_interval -1` but does **not** rewrite the `Host`
header — Caddy forwards the client's original header unchanged — so a
developer hitting `https://argus.internal/mcp` arrives at `server` with
`Host: argus.internal`. Without `--allowed-host argus.internal` that request
is rejected with **421 Invalid Host Header**, and this is true of *every*
`/mcp` call, i.e. everything Hermes actually does — while `/healthz` (a
separate route this check doesn't cover) keeps returning 200 the whole time,
which is exactly why the smoke test below no longer stops at `/healthz`.

**If you change `deploy/Caddyfile`'s site address away from
`argus.internal`, update both `--allowed-host` flags in the Dockerfile's
`server` stage `CMD` to match** — the two must always agree. The bare form
(`argus.internal`) matches the Host header a client sends when its URL has
no explicit port (what `hermes mcp add argus --url https://argus.internal`
below actually does); the `:*` wildcard form additionally covers a client
that includes an explicit port. Defaults are otherwise unchanged: running
`argus serve` directly, with no `--allowed-host` at all, still only accepts
the original loopback allowlist (`127.0.0.1`, `localhost`, `::1`).

### Why TLS is mandatory, not optional

`hermes mcp add argus --url <url> --auth header` sends the developer's own
GitLab personal access token as a bearer credential on **every single MCP
call**. Over plain HTTP on a shared LAN, that's a live credential in
cleartext on the wire, sniffable by anyone else on the same network segment.
There is no deployment mode where this is acceptable, which is why the
compose file has no path to reach `server` except through `caddy`.

`deploy/Caddyfile` terminates TLS using Caddy's internal CA (`tls internal`)
by default — appropriate because Argus typically lives on an internal LAN
hostname with no public DNS record for Let's Encrypt to validate against.
Edit the site address in `deploy/Caddyfile` from `argus.internal` to your
real hostname, then either:

- **Trust Caddy's internal CA once per developer machine** — export it from
  the running container and install it as a trusted root:

  ```bash
  docker compose exec caddy cat /data/caddy/pki/authorities/local/root.crt \
      > argus-internal-ca.crt
  # then install argus-internal-ca.crt into your OS/browser trust store
  ```

- **Or** accept the one-time self-signed certificate warning when running
  `hermes mcp add`, if your client tooling supports that.
- **Or**, if this host has a real public domain pointed at it, delete the
  `tls internal` line in `deploy/Caddyfile` and open port 80 (add `- "80:80"`
  to the `caddy` service's `ports:` in `docker-compose.yml`) — Caddy will
  then obtain and auto-renew a browser-trusted Let's Encrypt certificate
  instead.

Point Hermes at the proxy, never at the server directly:

```bash
hermes mcp add argus --url https://argus.internal --auth header
```

## Revoking access immediately: `flush-acl`

Each developer's GitLab-membership resolution is cached for 600 seconds
(`argus.acl.TTL_SECONDS`) so every tool call doesn't round-trip GitLab. That
means a revocation in GitLab normally takes effect within 10 minutes on its
own. To make it effective **immediately** — e.g. someone left the team and
you don't want to wait — flush the cache instead of restarting the service:

```bash
# Clear one developer's cached ACL resolution
docker compose exec server argus flush-acl \
    --config /etc/argus/config.yaml --user jdoe

# Clear everyone's (e.g. after a bulk GitLab permissions change)
docker compose exec server argus flush-acl --config /etc/argus/config.yaml
```

The command reports the actual number of rows cleared, and distinguishes a
username that matched nothing in the cache from a cache that was already
empty — the former usually means a typo or a developer who never
authenticated, the latter means there was nothing to do. Their next request
re-resolves against GitLab and gets the up-to-date answer.

## Ollama: firewall it, don't publish it

Ollama (the local model Hermes uses for inference) is not part of the Argus
compose file, but it lives on the same trusted network and deserves the same
scrutiny, for a different reason:

- It carries **no GitLab credential** — nothing like the PAT the server
  handles — so it isn't a credential-theft target the way an unproxied
  `server` port would be.
- It **does** carry your source code: every prompt Hermes sends it includes
  retrieved code, and Ollama's HTTP API has **no authentication of its own**.
  Anyone who can reach its port can read every prompt and every completion
  that flows through it — which, for this project, means your codebase.

Treat it exactly like an internal service that happens to have no login
screen:

- Never bind it to `0.0.0.0` on a host with any exposure beyond the
  developer subnet, and never publish its port through a NAT or cloud
  security group to anything wider than that subnet.
- Prefer putting it behind the same Caddy instance (see the commented
  `ollama.internal` block in `deploy/Caddyfile`) if it needs to be reachable
  from more than one host, so it at least gets TLS and access logging.
- Otherwise, firewall it so only the developer subnet can reach its port —
  it should never be reachable from outside the office/VPN network Hermes
  clients run on.

## Knowledge packs on a deployed server

Packs are public documentation and carry no access control, but they are still
files the server reads at query time. Install them into the directory
`packs.dir` names (default `<data_dir>/packs`) and restart nothing — packs are
opened per request, so a pack dropped in becomes queryable immediately.

```bash
argus pack install https://example.org/python-3.13.arguspack \
  --sha256 <digest> --config /etc/argus/config.yaml
argus pack list --config /etc/argus/config.yaml
```

**Always pass `--sha256` when installing from a URL.** A truncated download
that silently became a half-empty knowledge base is the failure the check
exists to prevent; without a digest there is nothing to detect it. A pack that
fails verification is not installed — no file, no registry entry.

Two operational notes:

- **`docs_search` needs Ollama; `docs_lookup` does not.** If the embedder is
  unreachable, `docs_search` degrades to lexical matching and labels every row
  `retrieval: "lexical"` rather than failing. Watch for that label in logs —
  it means the embedder has been down and answers have been less precise.
- **A pack built with a different embedding model is refused for semantic
  search**, by design, and says so by name. It still serves `docs_lookup` and
  lexical search. `argus pack list` marks it `[INCOMPATIBLE]`; that is the
  thing to alert on, because it will not fix itself.

The pack query path cannot reach the private index — that is enforced by a
test that reads the module's own source, not by convention — so installing a
third-party pack cannot expose private code. It can still serve wrong
documentation, so install packs you trust and verify their digests.

## Verifying the deployment

```bash
docker build --target test   -t argus:test   .   # full suite against pinned ctags
docker build --target server -t argus:server .   # server image builds
docker compose config                            # compose file parses; confirm
                                                  # indexer is NOT in the default `up` set
docker compose up -d
curl -k https://argus.internal/healthz           # {"status": "ok"} once caddy resolves TLS
```

**`/healthz` passing is not enough — it does not prove `/mcp` works.**
`/healthz` is a plain custom Starlette route registered outside the
`StreamableHTTPSessionManager` that the DNS-rebinding Host-header check
actually runs inside, so it returns 200 regardless of whether
`--allowed-host` is configured correctly. This project shipped with `/mcp`
rejecting every real call with `421 Invalid Host Header` while this exact
`curl /healthz` smoke test passed. The check that actually exercises the
thing Hermes uses has to hit `/mcp` itself, with the same `Host` header a
real client sends:

```bash
# Use the same GitLab personal access token you'd hand to `hermes mcp add`.
curl -k -s -o /dev/null -w '%{http_code}\n' \
    -X POST https://argus.internal/mcp \
    -H 'Content-Type: application/json' \
    -H 'Accept: application/json, text/event-stream' \
    -H "Authorization: Bearer ${ARGUS_GITLAB_TOKEN}" \
    -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"smoke","version":"0"}}}'
```

**Expect anything other than `421`.** A valid, currently-authorized token
returns `200`; an invalid or expired one returns `401` from the auth gate
(which runs, and can short-circuit, before the Host-header check ever
sees the request — so a missing/bad token alone does *not* confirm the
allowlist is right, only that it's reachable at all). Sending no
JSON-RPC body at all, or one with no session established yet, can also
surface as `400` from FastMCP's own protocol handling — still not `421`.
The one code that specifically means Caddy's forwarded `Host` header
isn't on the server's `--allowed-host` list is `421`; that is the one
failure this check exists to catch, and it is the one `curl .../healthz`
above cannot.
