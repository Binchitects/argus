# Running Argus in production

From nothing to agents answering, and the things that bite afterwards.

The short version:

```bash
git clone https://github.com/aliGhadyani/hermes-argus && cd hermes-argus
cp .env.example .env && $EDITOR .env          # GitLab URL + service token
./deploy/bootstrap.sh                          # build, start, index, verify
python deploy/smoke_test.py --url https://argus.example/mcp --token <dev-PAT>
```

`bootstrap.sh` is idempotent. It stops at the first problem with a message
naming the fix, rather than continuing and leaving a half-deployment that
looks up.

---

## 1. What you are deploying

| container | what it is | starts on `up` |
|---|---|---|
| `server` | the MCP daemon developers' agents talk to | yes |
| `caddy` | TLS terminator in front of it | yes |
| `refresher` | periodic re-index | yes |
| `indexer` | batch job — mirror and index | **no** (`indexer` profile) |

The server publishes **no host port**. The only way in is through Caddy.
That is deliberate: developers authenticate with their own GitLab PAT sent as
a bearer token on every call, and publishing 7700 directly would put those
tokens on the wire in plaintext.

## 2. The two tokens

This is the whole security model, and getting it wrong is the one mistake
worth being paranoid about.

| token | lives | can see |
|---|---|---|
| **service token** | the index host only, in `.env` | every repository |
| **developer PAT** | each developer's Hermes config | that developer's repos |

Argus mirrors with the service token, so the index is deliberately complete.
Each query is filtered to the caller's own membership **in SQL, before results
leave the process** — never by asking the model nicely. Revoke someone in
GitLab and their access dies with the ACL cache TTL (600 s).

**Never put the service token in a developer's client.** It would hand them
read access to every repository in the company through an agent.

## 3. Hardware, and what actually costs

Measured on Windows 11, CPU-only Ollama, `nomic-embed-text`:

| | |
|---|---|
| index, 10,212 files | ~21.9 MB per 1k files |
| `which_repo` p95 | **1.92 ms** |
| `docs_lookup` | **2.1 ms** median |
| `docs_search` excl. embedding | **88.6 ms** at 17.9k chunks, 460 ms at 364.8k |
| **query embedding** | **2,254 ms median** |

**The embedder sets the latency users feel, not the index.** Query embedding
is ~25× the entire search. A GPU is the single biggest quality-of-life change
you can make; nothing in the index layer comes close.

Disk: the private index is small. The knowledge packs are not — the full
eleven-pack estate is **1.57 GB**, and the embedding cache adds ~0.8 GB during
builds.

## 4. Knowledge packs: download, do not build

Building all eleven packs is **~5h 13m of CPU embedding**. Most teams should
never do it. Build once, publish the file, and have every other machine
install it:

```bash
argus pack install https://your-host/wdk.arguspack --sha256 <digest>
argus pack list
argus pack info wdk        # licence and attribution, in full
```

A digest mismatch is refused and **leaves zero files behind** — no pack, no
staging file, nothing registered.

If you do build: `deploy/build-packs.sh` is sequential and resumable, and the
embedding cache means an interrupted build re-embeds only what it never
reached.

## 5. Wiring Hermes

Hermes configures MCP servers from `~/.hermes/config.yaml` (on Windows,
`%LOCALAPPDATA%\hermes\config.yaml`):

```yaml
mcp_servers:
  argus:
    url: "https://argus.example/mcp"
    headers:
      Authorization: "Bearer <the developer's own GitLab PAT>"
    timeout: 180
    connect_timeout: 30
```

Two client settings are worth more than any tuning, both measured:

**Use native function calling.** A text protocol scored 10/20 and *collapsed
to 4/20* when told to check facts first, because the added prose broke the
output format. The same instruction under a proper tools schema improved
results instead.

**Pass the server's `instructions` through as the system message.** Argus
sends 1,803 characters at connect time saying that recollection of headers,
libraries and IRQLs is unreliable. Passing it through took tool use from 3 of
20 questions to 8, and accuracy from 12/20 to 14/20. An agent that discards
it leaves that on the table.

`deploy/agent_client_example.py` is a working ~120-line client doing both.

## 6. Verify before you announce it

```bash
python deploy/smoke_test.py --url https://argus.example/mcp --token <dev-PAT>
```

Seven checks, exit code 0 only when the required ones pass, so it drops into
CI or a post-deploy gate:

```
  [PASS] healthz                         3.0 ms  HTTP 200
  [PASS] auth rejects bad token        489.7 ms  denied
  [PASS] mcp handshake                 160.9 ms  protocol 2025-11-25
  [PASS] server instructions                     1803 chars
  [PASS] tools registered               11.0 ms  16 tools
  [PASS] packs answer                 1340.2 ms  FltRegisterFilter -> APC_LEVEL
  [PASS] private index               10764.5 ms  12 repo(s) visible to this token
```

`tools registered` names each required tool rather than counting them: "15
tools" stays green when the wrong 15 are registered, and a missing `docs_*`
tool is exactly the failure that makes an agent invent an IRQL.

`auth rejects bad token` reports **INCONCLUSIVE** rather than passing when the
error is ambiguous. A refusal and a network failure both raise, and only the
former proves anything about the security boundary.

---

## Operations

**Re-index** — a batch job, safe to run on a timer:

```bash
docker compose run --rm indexer index --config /etc/argus/config.yaml
```

**Revoke access immediately**, ahead of the 600 s ACL cache:

```bash
docker compose exec server argus flush-acl \
    --config /etc/argus/config.yaml --user <gitlab-username>
```

**Semantic search over your own code** is opt-in and incremental:

```bash
docker compose run --rm indexer embed --config /etc/argus/config.yaml --limit 5000
```

Start with `--limit` to see the rate before committing to hours. A rerun after
indexing only does the new work, and an interrupted run resumes.

---

## Failure modes worth knowing before they happen

Each of these was hit for real, and each produced a *plausible success* rather
than an error — which is what makes them expensive.

**The client shows the server as `configured` but never calls its tools.**
Hermes discovers MCP tools on a background thread, then snapshots its tool
list once. Discovery measures 1.07 s warm and 2.89 s cold, against a 0.75 s
wait. Argus is 27 ms of that budget, so no server-side tuning wins the race.
`/reload-mcp` fixes the session; see `docs/deployment.md` for the durable fix.

**Ollama serves a smaller context than the model advertises.** The model
reports 131,072; Ollama defaults far lower. With a large tool list the prompt
fills the window and leaves almost nothing to generate, surfacing as a
truncated or empty reply rather than an error. Set `OLLAMA_CONTEXT_LENGTH`.

**`localhost` costs ~2 s per request on Windows.** It resolves to `::1` first,
which is refused when the server binds IPv4 only, and the retry takes about
two seconds. Use `127.0.0.1` in client configuration.

**An expired ACL cache looks like a broken server.** Beyond the 3,600 s stale
grace, every token is denied with 401 until GitLab is reachable again. If
GitLab is down, Argus is down for new identities — by design, because the
alternative is serving permissions it can no longer verify.
