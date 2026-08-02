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

## Bringing the server up

```bash
docker compose up -d
docker compose ps          # expect: server, caddy — never indexer
```

`server` runs `argus serve --config /etc/argus/config.yaml --host 0.0.0.0
--port 7700` (baked into the Dockerfile's `server` stage's `CMD`). That
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

## Verifying the deployment

```bash
docker build --target test   -t argus:test   .   # full suite against pinned ctags
docker build --target server -t argus:server .   # server image builds
docker compose config                            # compose file parses; confirm
                                                  # indexer is NOT in the default `up` set
docker compose up -d
curl -k https://argus.internal/healthz           # {"status": "ok"} once caddy resolves TLS
```
