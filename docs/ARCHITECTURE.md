# Architecture

Three layers, five long-running containers, one network.

```
 browser ──────────────┐                         editors / agents (MCP, OpenAI API)
                       ▼                                  │            │
            ┌──────────────────────┐   /mcp  ◄───────────┘            │
            │ argus  :8080          │                                 │
            │  React app (static)   │                                 ▼
            │  /api   sign-in, chat,│   chat, per person   ┌──────────────────┐
            │         admin         ├─────────────────────►│ litellm  :4000   │
            │  /admin code index ops│   people, keys,      │  keys, budgets,  │
            │  /mcp   17 tools      │   budgets (master)   │  spend           │
            │  /hook  GitLab push   │                      └───┬──────────┬───┘
            └──┬─────────┬──────────┘                          │          │
               │         │ embeddings                          ▼          ▼
     GitLab ◄──┘         ▼                            ┌────────────┐ ┌──────────┐
   (REST + git)  ┌────────────────┐                   │ llamacpp   │ │ postgres │
                 │ llamacpp-embed │                   │ chat model │ │          │
                 │ nomic, CPU     │                   │ GPU        │ └──────────┘
                 └────────────────┘                   └────────────┘
```

## The layers

**Models.** `llamacpp` serves the chat model on the GPU with the memory
policy measured for each supported model (weights pinned in RAM when they fit,
MoE experts in system RAM, optional multi-token prediction). `llamacpp-embed`
serves nomic-embed-text on the CPU over the OpenAI `/v1/embeddings` protocol.
`model-init` downloads both on first start and verifies checksums.

**Gateway.** LiteLLM is the one OpenAI-compatible endpoint for the chat model,
with Postgres behind it. Every person is an *internal user* keyed by email with
a budget; every key belongs to a person. Nothing reaches the engine without
going through a key, so spend is always attributable and budgets always bind.

**App.** One .NET process (`dotnet/src/Argus`) serves:

| path | who | what |
|---|---|---|
| `/` | browsers | the React app (`frontend/`), with a strict CSP |
| `/api/auth/*` | anyone | sign-in and sign-out |
| `/api/*` | a signed-in person (session cookie) or their `ak_` key | chat, conversations, keys, settings; `/api/admin/*` for administrators |
| `/admin/*` | an administrator's session, or `ARGUS_ADMIN_TOKEN` | the code index's operator surface: index runs, packs, explore, metrics |
| `/mcp` | an `ak_` key or a GitLab token | the 17 tools over MCP (streamable HTTP) |
| `/hook/gitlab` | GitLab, with `ARGUS_WEBHOOK_TOKEN` | index a repository on push |
| `/healthz` | anyone | liveness |

## Identity

People live in `app.db` (SQLite, beside the index): username, email, role
(`admin`/`user`), a PBKDF2-SHA256 password hash (600,000 iterations), and
optionally the GitLab username an administrator linked. A session is a random
token in an `HttpOnly`, `SameSite=Lax` cookie (`Secure` over HTTPS), stored
only as its SHA-256. Any change made with a session must carry the
`X-Argus-Request` header, which a cross-site form cannot set. Failed sign-ins
are throttled per name and per address; successful ones never are.

**What a person may see in the code** is decided by GitLab, not by this app.
Their email (or linked GitLab username) is matched to a GitLab account with the
read-only service token, and their repositories are those whose member lists
include them at Reporter or above. Refusals say which repository holds a match
and who can grant access. The same resolution applies to chat tool calls, to
`ak_` keys on `/mcp`, and — through GitLab itself — to GitLab tokens.

## A chat turn

1. The browser posts the message; the backend stores it and opens a stream.
2. It calls LiteLLM with **the person's own virtual key** (created the first
   time they chat), the conversation so far, and the 17 tools as OpenAI
   function definitions.
3. The model's reasoning and text are forwarded as server-sent events as they
   arrive. A tool call is run **in-process, as that person** — the same code
   the MCP endpoint runs — and its result goes back to the model.
4. Up to eight rounds, then the model must answer from what it has. The
   answer, the reasoning and every tool call and result are stored, so a
   conversation reloads exactly as it happened. Closing the tab stops the
   generation upstream.

A gateway refusal (a spent budget, an unknown model) reaches the person as a
message in the conversation, not as a broken page.

## The code index

`argus index` (or the schedule, or the Indexing page, or a GitLab push)
mirrors every project the service token can see, extracts symbols with
Universal Ctags, doc comments and Python docstrings, resolves `#include`s
across repositories into a dependency graph, and embeds each symbol with its
doc for semantic search. Indexing is incremental by blob SHA, per branch.
Knowledge packs are prebuilt SQLite files of public documentation (Win32, WDK,
C++, .NET, Python, …) with their own embeddings, installed from a URL or path.
See [code-index/](code-index/).

## Where state lives

| data | where | rebuildable? |
|---|---|---|
| people, sessions, `ak_` keys, conversations | `argus-data` volume, `app.db` | **no** — back it up |
| code index, audit log | `argus-data`, `index.db`, `index-audit.db` | index yes (from GitLab), audit no |
| knowledge packs | `argus-data`, `packs/` | yes (reinstall) |
| mirrors and worktrees | `argus-data`, `mirrors/`, `trees/` | yes |
| gateway people, keys, budgets, spend | `postgres-data` | **no** — back it up |
| model weights | `LLAMACPP_MODEL_DIR`, `EMBED_MODEL_DIR` | yes (download) |

`make backup` takes everything marked **no**, plus `.env`, whose
`LITELLM_SALT_KEY` is needed to read the gateway database back.
