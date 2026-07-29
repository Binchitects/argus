# CodeIndex — Local Code Assistant for Hermes Agent

**Date:** 2026-07-28
**Status:** Design approved, pending implementation plan

## Problem

A small team (2–5 developers) maintains 3M+ lines of predominantly C/C++ spread across many
self-hosted GitLab repositories that together build one product. Developers need an AI assistant
that can find code across all of those repos, identify which repo a given change belongs in, and
help apply the change locally — with each developer only able to see repos they have GitLab access to.

Hermes Agent is already installed and running against a local Ollama. It has no repo indexing or
code-retrieval capability of any kind: its toolset registry provides web search, terminal, file,
conversational memory, and session search, but no vector store, no symbol index, and no cross-repo
search. Its `context_engine` toolset is an empty stub. The index does not exist and must be built.

This project builds **CodeIndex**: an MCP server that indexes all GitLab repos and exposes
access-controlled code retrieval to each developer's Hermes instance.

## Non-goals

- Replacing GitLab's permission model. GitLab remains the sole authority on who may see what.
- Building the repos. No `compile_commands.json`, no clangd, no compilation is required for v1.
- Serving as a general document RAG. This indexes source code and its adjacent docs, nothing else.
- Multi-branch indexing. v1 indexes each repo's default branch only.
- Write access to GitLab. CodeIndex never pushes, never opens merge requests. Developers apply
  changes in their own local checkouts through Hermes's existing file tools.

## Topology

Two machines, one direction of dependency.

**Index host — Linux, shared GPU box.** Runs all stateful components:

- Ollama serving `qwen3.6:35b` (chat) and `nomic-embed-text` (embeddings)
- The CodeIndex server (HTTP MCP) behind a TLS reverse proxy
- Bare git mirrors plus one default-branch worktree per repo
- A single SQLite database holding the entire index

**Developer workstations — Windows.** Run only the Hermes CLI, configured to reach the index host:

```
model.base_url  → http://<index-host>:11434/v1        # inference
mcp.codeindex   → https://<index-host>/mcp            # retrieval, --auth header
```

Hermes keeps full local filesystem access, so it reads and edits the developer's own checkout.
CodeIndex tells it *where* the change belongs; Hermes's existing local tools *make* the change.

## Components

Seven modules, each with a single responsibility and an interface that can be tested in isolation.

| Module | Responsibility | Depends on |
|---|---|---|
| `mirror` | Enumerate GitLab projects, maintain bare mirrors + worktrees, detect changed files per repo | GitLab API, git |
| `parse` | Changed files → symbols, chunk boundaries, `#include` edges | ctags, tree-sitter |
| `store` | SQLite schema; all reads and writes; FTS5 lexical index; `sqlite-vec` vectors | — |
| `embed` | Batched embedding of selected units | Ollama |
| `graph` | Cross-repo resolution: symbol → definitions, include edge → repo edge | `store` |
| `acl` | GitLab PAT → identity + project allowlist, TTL-cached | GitLab API |
| `mcpsrv` | MCP tool surface over HTTP; auth; filters every query through `acl` | all of the above |

### Package layout

```
codeindex/
  config.py          # typed config load from /etc/codeindex/config.yaml + env
  cli.py             # codeindex serve | index | status | flush-acl
  mirror.py          # GitLab discovery, fetch, diff detection
  parse/
    filters.py       # what to skip: binary, vendored, generated, oversize
    ctags.py         # symbol extraction
    treesitter.py    # chunk boundaries, doc comments
    includes.py      # #include extraction and resolution
  store/
    schema.sql
    queries.py       # every query takes allowed_repo_ids as first positional arg
  embed.py
  graph.py
  acl.py
  worker.py          # serialized background index queue
  mcpsrv/
    server.py        # HTTP MCP transport, auth middleware
    tools.py         # the eight tool handlers
    errors.py        # agent-facing error strings
tests/
  fixtures/          # tiny multi-repo C/C++ corpus with known symbols
```

**Language and runtime:** Python 3.13 (already installed on workstations; the index host needs its
own). SQLite with FTS5 and `sqlite-vec`. At 2–5 developers there is no justification for operating
Qdrant or Postgres: one database file gives atomic writes, trivial backup, and zero daemon
management. Regex search shells out to ripgrep against the worktrees rather than maintaining a
trigram index — faster than anything we would build and one less subsystem to keep consistent.

## Tool surface

The eight tools Hermes sees. Tools are named after the *questions developers ask*, not after
retrieval mechanisms, because a 35B local model selects tools far more reliably when the tool name
matches the intent.

| Tool | Signature | Answers |
|---|---|---|
| `which_repo` | `(description: str) → [{repo, confidence, why}]` | "Which repo do I change for X?" (see below) |
| `find_symbol` | `(name: str, kind?: str) → [{repo, path, line, signature}]` | Exact definitions across all repos |
| `find_references` | `(symbol: str) → [{repo, path, line, context}]` | Every caller, product-wide, cross-repo |
| `search_code` | `(query: str, repos?: [str], regex?: bool) → [{repo, path, line, snippet}]` | Fast lexical/regex over 3M lines |
| `semantic_search` | `(question: str, repos?: [str]) → [{repo, path, summary, score}]` | Vague conceptual queries |
| `repo_map` | `(repo?: str) → {nodes, edges}` | Dependency graph; what breaks if I change this |
| `get_file` | `(repo: str, path: str, range?: [int,int]) → str` | ACL-checked content fetch |
| `index_status` | `() → [{repo, sha, indexed_at, files, symbols, vectors, errors}]` | Freshness, so the agent can qualify stale answers |

### How `which_repo` scores

This is the tool the whole project exists for, so its ranking is specified rather than left to
implementation taste. For a given description, each allowed repo receives:

1. **Semantic score** — max cosine similarity between the description and any file-summary or
   public-symbol vector in that repo.
2. **Lexical score** — normalized FTS5 rank for the description's distinctive terms within the repo.
3. **Centrality adjustment** — a repo that many others depend on (high in-degree in `repo_deps`) is
   *down*-weighted for change requests. Shared low-level libraries match many queries by nature, but
   are usually the wrong place to make a product change; the caller is more often correct.

The three combine into a confidence value, and `why` returns the concrete evidence — the specific
files and symbols that drove the match — so the developer can judge the answer rather than trust it.

## Indexing pipeline

**Two GitLab tokens, and their separation is the security model.** A privileged *service token*
owned by the index host mirrors every repo — the index is deliberately complete. Each *developer
token* is used only at query time. The index knows everything; no single query returns everything.

**Phase 0 — Discovery.** Service token walks `GET /api/v4/projects` (paginated, `simple=true`).
Each project gets a bare mirror at `data/mirrors/<gitlab_id>.git` and one worktree at
`data/trees/<gitlab_id>` pinned to the default branch. The worktree exists so ripgrep and ctags
have real files to read.

**Phase 1 — Change detection.** `git fetch --prune`, then compare the new default-branch SHA
against `repos.last_indexed_sha`. `git diff --name-status <old>..<new>` yields the exact changed
set. First run degenerates to `git ls-files`. All downstream work operates on that file list.

Force-push is detected with `git merge-base --is-ancestor <old> <new>`; a false result means the
old SHA is orphaned and that repo gets a full reindex.

Renames (`R` entries) are treated as delete + add. Rename tracking buys nothing here.

**Phase 2 — Parse.** Files are filtered first:

- Binary (null byte in first 8KB)
- Larger than 1 MB
- Under a vendored/build path (`third_party/`, `vendor/`, `node_modules/`, `build/`, `out/`, `x64/`, `Debug/`, `Release/`)
- Marked `linguist-generated` in `.gitattributes`
- Matching the repo's optional `.codeindexignore`

Survivors go through:
- **ctags** → symbol name, kind, file, line, signature, scope. For C/C++: functions, classes,
  structs, enums, unions, macros, typedefs, namespaces.
- **tree-sitter** → function/class chunk boundaries and doc comments.
- **includes** → regex extraction of `#include "..."` and `#include <...>`. Resolution attempts
  the containing repo first, then suffix-matches the include path against all repo worktrees.
  Unresolved includes are recorded as `is_external = 1`, which usefully documents the
  third-party surface.

**Phase 3 — Store.** Upsert files, symbols, include edges; delete rows for deleted paths; write
file content into `files.content` and feed FTS5.

**Phase 4 — Embed, selectively.** This is the cost control that makes 3M lines tractable.

Embedded:
- Every **public symbol**: `signature + doc comment + path + repo`, **not the body**
- One summary vector per file: path, header comment, list of symbol names
- Docs (`*.md`, `*.txt` under `docs/`), chunked normally

C++ has no `export` keyword, so "public symbol" needs an explicit definition. A symbol is public if
it is **declared in a header file** (`.h`, `.hpp`, `.hxx`, `.inl`) and its enclosing scope is not a
namespace named `detail`, `internal`, or `impl`, and is not an anonymous namespace. Additionally, non-`static` functions
defined in `.c`/`.cpp` files count as public even without a header declaration, since they are
linkable across translation units. Everything else — `static` functions, members of `detail`
namespaces, and symbols local to a `.cpp` — is indexed for `find_symbol` and `find_references` but
does not get a vector.

Not embedded: function bodies, header boilerplate, generated code, tests (configurable).

Expected scale at 3M lines of C/C++: roughly 70–90k vectors rather than ~600k. At 768 dimensions
that is ~270 MB in `sqlite-vec`, and a full rebuild runs in under an hour rather than overnight.

Rationale: a C++ function body embeds mostly to "generic C++ control flow". Its signature plus doc
comment plus path is what carries the intent a developer is searching for.

**Phase 5 — Graph materialization.** Resolved cross-repo include edges aggregate into
`repo_deps(from_repo_id, to_repo_id, weight)`. `repo_map` and `which_repo` read this table.

**Triggering.** GitLab push webhook → `POST /hooks/gitlab`, validated against a shared secret →
repo enqueued in `index_queue`. **Plus** a periodic poll every 15 minutes as a fallback, because
self-hosted GitLab webhooks fail silently more often than expected, particularly when the index
host sits behind NAT. A single serialized worker drains the queue; with one box, concurrency buys
nothing and risks git lock contention.

Embedding is a **separate queue stage** from parsing. Lexical and symbol layers land and become
queryable immediately; vectors backfill. A broken or slow embedder degrades `semantic_search`
only — it never blocks the layers developers use most.

Each repo has a per-pass time budget (default 10 minutes); the worker yields and round-robins so a
2M-line repo cannot starve a 5k-line one.

## Access control

### Flow

```
Hermes (dev workstation)
  │  Authorization: Bearer <dev's GitLab PAT>
  ▼
mcpsrv ──► acl.resolve(token)
  │          ├─ cache hit (TTL 10 min) ──► {user_id, username, allowed_repo_ids}
  │          └─ miss ──► GET /api/v4/user
  │                      GET /api/v4/projects?membership=true&min_access_level=20
  │                        └─ GitLab itself decides visibility
  ▼
tool handler(allowed_repo_ids, …)
  ▼
store query  ──  WHERE repo_id IN (:allowed)   ← mandatory, SQL-level
```

`min_access_level=20` is Reporter. Guest (10) cannot read repository code in GitLab, so Reporter is
the correct floor for code access.

Tokens are hashed with SHA-256 for cache keys and never stored raw.

### The enforcement guarantee

`allowed_repo_ids` is a **required positional argument** on every function in `store/queries.py` —
never an optional keyword defaulting to "all". A developer adding a tool six months from now cannot
forget it; the call will not type-check or run. This converts a runtime vulnerability into an
import-time error.

Security bugs of this class almost never come from someone deliberately bypassing a check. They
come from a new code path that simply never called it.

### Leaks closed explicitly

- **Graph traversal.** `repo_map` and `which_repo` walk dependency edges. An edge pointing at an
  inaccessible repo would leak its name, so such edges are filtered to allowed repos and rendered
  as `(restricted)` rather than named.
- **Result replay.** `get_file` re-checks the allowlist rather than trusting that a prior search
  already did.

### Revocation and audit

The cache TTL bounds the revocation window: removing someone in GitLab takes effect within
~10 minutes, or immediately via `codeindex flush-acl` / `POST /admin/acl/flush`.

Every query appends to `audit(ts, user_id, tool, args_json, repo_ids_json)`. At this team size the
cost is negligible and it answers "what did the assistant show them" after the fact.

### Failing closed

When GitLab is unreachable:
- **Cached and within grace window (1 hour):** serve stale permissions, log loudly. People already
  working keep working.
- **Cache miss:** **deny.** No one gets access the server could not verify.

## Storage schema

```sql
repos(id, gitlab_id UNIQUE, path_with_namespace, default_branch,
      last_indexed_sha, last_indexed_at, http_url)

files(id, repo_id, path, lang, size, blob_sha, content,
      UNIQUE(repo_id, path))

symbols(id, repo_id, file_id, name, kind, line, end_line,
        signature, scope, is_exported)

includes(id, repo_id, file_id, raw, resolved_file_id, resolved_repo_id, is_external)

chunks(id, repo_id, file_id, kind, start_line, end_line, text)

repo_deps(from_repo_id, to_repo_id, weight)

vec_items(rowid, embedding float[768])        -- sqlite-vec virtual table
vec_meta(rowid, repo_id, source_kind, ref_id) -- source_kind: symbol | file | chunk

files_fts(path, content)                      -- FTS5, external-content over files

acl_cache(token_hash PRIMARY KEY, user_id, username, repo_ids_json, fetched_at)
audit(id, ts, user_id, tool, args_json, repo_ids_json)
index_errors(id, repo_id, path, stage, message, ts)
index_queue(repo_id PRIMARY KEY, enqueued_at, reason)
```

File content is stored once in `files.content` to serve snippet extraction without disk reads.
`files_fts` uses FTS5 **external-content mode** (`content='files'`) so the search index stores only
terms, not a second copy of the text — roughly 300 MB saved. Deletes must therefore go through the
documented external-content delete pattern, not a bare `DELETE` on the FTS table.

The worktrees remain the source for ripgrep regex and ctags. Content in the database and content in
the worktree both derive from the same commit, so they cannot drift.

## Failure handling

The governing principle: **tool error text is prompt text.** When something breaks, the returned
string goes into a 35B model's context and determines what it does next. Errors are written to
steer the agent, not to describe the exception.

| Failure | Behavior |
|---|---|
| Index host unreachable mid-conversation | Tool returns: *"CodeIndex unavailable — fall back to ripgrep/read in the local checkout and tell the user the answer is repo-local only."* Hermes keeps its local file tools; the developer degrades to single-repo work rather than a dead session. |
| GitLab unreachable during ACL resolve | Stale cache within grace window, else deny. |
| ctags/tree-sitter crashes on a file | Quarantine the file, record in `index_errors`, continue the repo. One malformed header must never abort a 200k-file index. |
| Indexer crashes mid-repo | `last_indexed_sha` advances only after the repo's full changed set commits. Restart redoes that repo's diff; all upserts are idempotent. |
| Embedding model down or slow | Separate queue stage; lexical and symbol layers unaffected. |
| Force-push / history rewrite | Detected via `merge-base --is-ancestor`; triggers full reindex of that repo. |
| One huge repo starves the queue | Per-repo time budget with round-robin yielding. |
| Revoked developer token | GitLab returns 401 → tool returns an actionable message telling the developer to refresh their PAT. |

## Testing strategy

Test-first. The ACL tests are written **before** the store query layer exists, because they are what
force the allowlist parameter to be positional and required. Write the store first and the allowlist
inevitably becomes an optional keyword — it is more convenient at every individual call site, and
each of those small conveniences is a future leak.

| Module | Approach |
|---|---|
| `parse` | Golden-file tests over small C/C++ fixtures with known symbols and includes. This is where the bugs will be: templates, macros, and nested namespaces are where extractors quietly go wrong. |
| `store` | In-memory SQLite. Two non-negotiable tests: every query function **rejects a call with no allowlist**, and a query allowed `[repo 1]` **never returns a row from repo 2**. |
| `acl` | Mocked GitLab HTTP: cache hit, miss, expiry, stale-grace, deny-on-miss-during-outage, 401 on revoked token. |
| `mirror` | Real git against temp fixture repos — no GitLab needed. Create, commit, force-push; assert diff detection including the orphaned-SHA path. |
| `graph` | Fixture multi-repo include set; assert cross-repo edges resolve and unresolved ones land as external. |
| `embed` | Mocked Ollama; assert selection rules (signatures yes, bodies no) and batch behavior. |
| `mcpsrv` | End-to-end over the MCP protocol with a fake store; assert every tool refuses unauthenticated calls. |
| Integration | Three tiny fixture repos through the full pipeline; assert `find_symbol` resolves a definition in repo A from a reference in repo B. |

## Operations

### Bootstrap order

The initial index is the long pole; run it before onboarding anyone.

1. Install `universal-ctags`, `ripgrep`, `git`, Python 3.13 on the index host
2. `ollama pull nomic-embed-text`; pin the chat model to `qwen3.6:35b` — **not** `:latest`.
   Today `qwen3.6:latest` and `qwen3.6:35b` share digest `07d35212591f`, but that will not hold.
3. Create the GitLab service token with `read_api` + `read_repository`
4. Write `/etc/codeindex/config.yaml`; start the systemd unit; confirm `/healthz`
5. Run the initial full index (hours at this volume)
6. Register the GitLab push webhook; confirm the 15-minute poll fallback is active
7. Per developer: `hermes config set model.base_url http://<host>:11434/v1`, then
   `hermes mcp add codeindex --url https://<host>/mcp --auth header`

### Transport

The CodeIndex server binds localhost; Caddy (or nginx) terminates TLS in front of it with an
internal-CA or self-signed certificate. This is required, not optional: `hermes mcp add --auth
header` sends the developer's GitLab PAT on every tool call, and over plain HTTP that is a
credential in cleartext on the wire.

**Ollama traffic needs the same treatment for a different reason.** The `model.base_url` connection
carries no credentials, but it carries the source code itself — every retrieved snippet travels to
the index host as prompt text. Ollama has no built-in TLS or authentication, so it must either sit
behind the same reverse proxy (`https://<host>/ollama/v1`) or be firewalled to the developer subnet
with `OLLAMA_HOST` bound to a private interface. Exposing port 11434 broadly would publish an
unauthenticated inference endpoint to anyone who can reach the box.

### Ollama tuning

One `qwen3.6:35b` serving 2–5 concurrent developers with long retrieval contexts will be the thing
people complain about — not retrieval quality. Set `OLLAMA_NUM_PARALLEL` and
`OLLAMA_MAX_LOADED_MODELS` so the embedding model stays resident alongside the chat model rather
than thrashing on every index update.

### Backup

The only irreplaceable state is the SQLite database, and even that is rebuildable from GitLab —
just slowly. Mirrors are re-clonable and worktrees are derived. Backup is a file copy.

### Sizing

Mirrors (~1.5× source with history) + worktrees (~1×) + database. The database runs roughly
300 MB content + 270 MB vectors + ~100 MB FTS terms and symbol rows ≈ 700 MB. Budget ~20 GB of disk
for 3M lines of C/C++.

## Delivery phases

Seven modules is too much to land in one step. The phases below are ordered so that **each one ends
at a state your developers can actually use**, and so the security boundary exists before any data
leaves the box.

**Phase 1 — Indexed and searchable, single user.**
`config` + `store` (with the required-allowlist signature from the start) + `mirror` + `parse` +
`worker`. CLI only: `codeindex index`, `codeindex status`. No server yet. Ends with a populated
database over your real repos and a measured answer to how long a full index actually takes.

**Phase 2 — Multi-user retrieval.**
`acl` + `mcpsrv` + the five non-semantic tools (`find_symbol`, `find_references`, `search_code`,
`get_file`, `index_status`) + TLS proxy + systemd unit. Ends with developers querying from their own
Hermes with real GitLab-derived permissions. **This is the phase that delivers most of the value** —
exact symbol lookup across 3M lines is the bulk of what the team will ask for.

**Phase 3 — Cross-repo intelligence.**
`graph` + `repo_map` + `which_repo` (lexical and centrality components only). Ends with the
"which repo do I change" capability that motivated the project.

**Phase 4 — Semantic layer.**
`embed` + `semantic_search` + the semantic component of `which_repo` scoring. Deliberately last,
because it is the most expensive to build and validate, and phases 1–3 are independently useful if
retrieval quality on C++ signatures turns out to need iteration.

Webhook triggering can land in any phase after 1; until then the 15-minute poll is sufficient.

## Open items for the implementation plan

- Embedding model choice is `nomic-embed-text` by default and swappable via config. If retrieval
  quality on C++ signatures proves weak in practice, a code-specific embedder is a config change,
  not a redesign.
- Per-repo clangd upgrade path: the `parse` module's symbol extraction sits behind an interface, so
  any repo that builds cleanly with a `compile_commands.json` can later be upgraded to a
  compiler-accurate index without touching the rest of the system. Out of scope for v1.
