# Roadmap after v1.0

Ordered by **evidence status**, not by feature appeal. The first milestone
holds the only failure the bench still catches and the only shipped feature
with no real-world evidence. Everything later is a known cost rather than a
known defect.

A roadmap sorted by "what do we not yet know is true" finds real problems
earlier than one sorted by "what would be nice".

## Where v1.0 actually stands

| proven | unproven |
|---|---|
| 11 packs, 0 unresolved symbols, 11/11 answered through the real agent | ~~`semantic_search` never run on real data~~ — **done: 76,636 vectors, 3 exact / 3 partial / 0 wrong** |
| 5/10 → 10/10 (`qwen3.6:27b`), 5/10 → 9/10 (`qwen3.6:35b`) | ~~pack freshness — archives have no update path~~ — **done: `pack update` against a published index, and an archive refetch no longer keeps pages upstream deleted** |
| Container healthy, 7/7 acceptance, 741 tests | recall under a narrow ACL allowlist |
| ACL enforced structurally and audited | anything beyond a single machine |

---

## Milestone 2 — the agent that decided not to check

### 2.1 Forced verify-after

**The one measured failure left, and the expensive kind.** `qwen3.6:35b`
failed `security-review` with **zero tool calls in 2.2 seconds**, answering
`wcscpy_s` / `<string.h>` / `ucrt.lib` from memory — a real function, and a
user-mode answer to a question about kernel code. `qwen3.6:27b` made 5 calls
on the same task and got it right. Every other failure mode this project had
is closed; this one is not, and it produces *confident* wrong answers.

The shape to try: let the model draft, then run `docs_verify` on the draft
automatically and feed back only what the documentation contradicts.

There is already evidence this is the right direction rather than a guess.
Verify-after cannot displace knowledge the model already had, because it only
speaks where documentation disagrees. Retrieval-*first* demonstrably can:
putting pack context in front of a model before it answered took Win32
accuracy from 5/5 to **1/5**.

**Measure:** forced-verify vs model-choice, same 10-task bench, both models.
Success is 35b reaching 10/10 without losing a task it currently passes.
**Risk:** latency. 35b's median is 2.4 s with tools; a mandatory second pass
roughly doubles it, and that cost lands on every question including the ones
that never needed checking.

**Status: the enforcement point exists; the measurement does not.** `docs_verify`
has been an MCP tool all along, and that was the problem -- every client can run
a SHELL COMMAND when the model finishes and block on its exit code, and almost
none can be made to call a *tool* at that moment. So verify-after could not be
forced in any client, however good the tool was. `argus verify` is that same
check with an exit code, and `clients/claude-code/verify-after.sh` wires it into
Claude Code's `Stop` hook, which blocks the turn and feeds the contradictions
back to the model.

The exit codes carry the contract, and the interesting one is 6: "could not
check" -- no packs installed, or unreadable ones -- which does **not** block. A
mandatory verifier that cannot verify must not become an agent that can never
finish a sentence, and "I could not check" is a different answer from "you are
wrong". Everything except a contradiction fails open, including an unexpected
error.

What is left is the bench this item was always measured by: forced-verify
against model-choice on the same ten tasks, both models, with 35b reaching 10/10
without losing a task it currently passes. That needs the agent and the larger
model in the loop, and neither is available here. Until it runs, this is a fix
that has been *made possible and wired up*, not a fix that has been *shown to
work* -- which is a weaker claim than the DONE markers elsewhere in this file,
and is written that way on purpose.

### 2.2 Prove Phase 4 on real data — DONE

76,636 vectors built over postgres, openssl, git, curl, redis and freetype.
Hand-checked on six description-shaped questions: **3 exact, 3 partial,
0 wrong, and every question landed in the right file.** The three partials
share one shape — the index matches vocabulary, not role, so "expire keys
past their TTL" returned `expireSlaveKeys` rather than `activeExpireCycle`
from the same file. Full write-up in `pack-measurements.md`.

The recall limit below is now measured too, and does not bite: starvation
turns on topical alignment rather than allowlist size, and where it starves
the missing results score ~0.55 -- noise the smaller budget was right to
exclude. **2.2 is closed; 2.1 is not** — an earlier edit here said "Milestone
2 is closed", which was true of this sub-item and not of the milestone.
Forced verify-after remains the one measured failure with no fix attempted.

### The original plan for 2.2

`semantic_search` shipped with unit tests and **zero evidence on a real
corpus**: the index holds 286,785 symbols, 76,636 of them embeddable, and
0 vectors existed. That is the weakest thing in v1.0 — a feature whose only
evidence is a fixture with two orthogonal vectors.

At the measured 55 vectors/sec that is roughly 23 minutes of CPU embedding.

**Measure afterwards:** hand-checked recall on questions with a knowable
answer, and `SEMANTIC_COARSE` tuned against the real ACL-post-filter
behaviour rather than against the reasoning in its docstring. The
post-filter recall limit is documented but has never been observed: a caller
whose allowlist is a small slice of the corpus can get fewer hits than exist
for them, and nobody has measured how small "small" has to be before it
bites.

---

## Milestone 2.5 — docs_find recall, and who is responsible for it

Not in the original plan, and it earned a place by measurement. `docs_find`
answers **44% of 36 description-shaped questions at top-10 unscoped, 58%
scoped**. The gap between those two numbers is larger than every ranking
change made to the tool put together, and closing it is not a ranking
problem: the tool cannot know which source to search, because that is what
the caller knows and the server does not.

Measured through the real agent: Hermes passes `lang` on **5 of 8 calls**.
The remaining third is where the scoped figure leaks away.

| item | status |
|---|---|
| Adapter description quality | **done** — every pack at 0 blank descriptions; cpp two-word descriptions 56% → 2.2%, python 62% → 16.3% |
| `docs_find` serves the hybrid arm | **done** — it was implemented, documented, and had no callers |
| Chunk-precise symbol selection, per-chunk cap | **done** — a 369-symbol page returned an arbitrary 8 |
| Tool description names the installed sources | **done** — `scripting` for a PowerShell question is only knowable from that list |
| Wrong `lang` widens instead of returning nothing | **done** — hit on the first day by a `csharp` guess |
| **Raise `lang` adoption above 62%** | **open** — the largest remaining lever, and it lives in Hermes rather than here |

The open item is a prompting problem, not a retrieval one, which is why it
sits at the end of this milestone rather than inside the server.

---

## Milestone 3.5 — what the code DOES, not what it is called

The measured failure this closes: asked "what expires keys past their TTL",
`semantic_search` returned `expireSlaveKeys` where `activeExpireCycle` was the
answer. Same file, both plausible names, identical signatures, and the only
thing distinguishing them is the sentence above each one saying what it does.

That sentence was already in the index. `files.content` holds the entire file
and `symbols.line` locates the symbol inside it, so the comment immediately
above a definition had been sitting in the database, unread, since the first
version. `argus.parse.docs` reads it; `symbols.doc` stores it; the embedded text
leads with it; and every symbol-level result returns it.

**Measured, on a fixture built to be falsifiable** — two functions with equally
plausible names and opposite documentation, asked the natural question:

| query | ranking on name + signature | ranking with the doc |
|---|---|---|
| "what reclaims keys whose time to live has elapsed" | `expire_slave_keys` 0.616 **(wrong)** | `active_expire_cycle` 0.703 **(right)** |
| "propagate an expiry decision to a replica" | `active_expire_cycle` 0.564 **(wrong)** | `expire_slave_keys` 0.680 **(right)** |

The ranking flips on both. Symbols with no doc comment score identically before
and after (+0.0000), so the doc only adds where somebody wrote one.

**Two traps worth recording.** A negation is evidence for the thing it negates:
the first version of the fixture said the wrong function "is NOT the routine
that reclaims expired keys", and that clause pulled it to within 0.0002 of the
right answer. Documenting what a function does *not* do is not neutral.

And the embedded text needed its own version (`EMBED_TEXT_VERSION`), because a
vector was considered current if a row existed with the same model and
dimension -- which cannot notice that the TEXT changed. Without it, adding the
doc to the embedded text would have rebuilt nothing and reached only the symbols
whose file was edited afterwards.

### What an agent gets now

- **`overview`** -- what each repository IS: README, layout, languages,
  documented abstractions, and cross-repo dependencies in both directions.
  Names alone are not an architecture.
- **`semantic_search`** with a `doc` on every result, so a capability question
  ("what do you have that does X") is answerable by reading rather than by
  guessing which of two similar names is the one.
- The server instructions say to orient with `overview` first and to read `doc`
  before choosing a result.

### Branch-agnostic, verified rather than assumed

One project is indexed at trunk **and** at a release branch, with a
branch-only symbol and different documentation for a shared name. Verified
over MCP, and now part of `tools/test-gitlab/verify_tools.py`:

- an unqualified question answers from **trunk**, and cannot see the branch-only
  symbol;
- naming the branch returns that branch's content;
- an **unindexed** branch raises `UnknownBranch` naming the branches that ARE
  indexed, rather than returning an empty list that reads as "no such symbol";
- `semantic_search` is branch-scoped in both directions;
- `index_status` reports one row per (repo, branch).

---

## Milestone 3 — operations

| item | why | today |
|---|---|---|
| Incremental pack rebuild — **DONE** | was 44 min to reproduce a byte-identical file | `content_sha` per document; automatic when a usable pack sits at the destination. Measured: wdk 205,848 chunks in **26 s**, win32 478,762 in **74 s** |
| `pack update` for archive sources — **DONE** | assumed a git remote | the registry index path (`pack update --index-url`) works for both source kinds, and it is now tested end to end: install v1, index says v2, assert v2 — including that a FAILED update leaves the working pack working |
| Metrics endpoint — **DONE** | audit rows existed with no operational view | `/admin/metrics` on Argus, scraped by Prometheus, with four alert rules; the admin console's Overview reads the same snapshot |
| Index explorer — **DONE** | a tool returning nothing gave no way to tell "not in the code" from "not indexed" | the console's Explore page searches symbols by fragment and files by path across the estate, reading `/admin/explore`. The unfiltered queries live in `store/explore.py`, and a test asserts no tool module imports them |
| Webhook-driven indexing — **DONE** | freshness was interval-polled, so a push sat unindexed for up to 15 minutes | `POST /hook/gitlab`, gated by its own `ARGUS_WEBHOOK_TOKEN`; a push during a pass is queued rather than dropped, the queue drains one repository per pass, and an overfull queue collapses into one full pass. The poll stays as the floor |

**Incremental rebuild landed, and carries one trap worth knowing.**
`content_sha` covers the DOCUMENT, so an adapter that derives symbols
differently leaves every unchanged document's symbols exactly as they were.
A cpp rebuild after teaching the adapter to read page ledes would have kept
the old title-echo descriptions, reported a healthy symbol count, and shipped
the fix applied to nothing. Delete the destination to force a full build
after changing an adapter; a source refresh is unaffected.

The rest of this section is kept for the correction it records.

Incremental rebuild was the most valuable of these, but for a smaller
reason than this section originally claimed — and the correction goes both
ways.

**162 minutes was never the refresh cost.** The embedding cache already turns
it into ~44, with nothing built. Quoting the cold figure overstated the
problem by 3.7×.

**But "nearly free" was wrong too.** A `debugger` rebuild reporting
`14,259 reused, 0 computed` in seconds made the cache look total; at 530,559
chunks the same mechanism still costs 44 minutes — re-parsing 71,663
documents, re-chunking, half a million cache lookups, and writing a 786 MB
pack, all to produce the file already on disk.

So the target is **44 minutes → seconds when upstream changed 50 documents
out of 71,663**, which is what a docs refresh actually looks like. That needs
a per-document content hash in the pack, compared against the source, so
unchanged documents keep their existing chunks, symbols and vectors. The
saving comes from skipping *documents* — skipping embedding is already done.

---

## Milestone 4 — reach

**Upstream the vendored Hermes patches.** They live in a vendored install, and
a Hermes update silently reverts them — including the instructions-forwarding
that is what made the tools work at all. The symptom returns with no error
anywhere. This is the single largest durability risk in the deployment, and
`/reload-mcp` is only a per-session workaround.

**Hermes caches MCP tool schemas, and a stale cache is silent.** Found while
testing `lang` scoping end to end: `cache/mcp_schema_cache.json` still held
the previous `docs_find` description, so the model was choosing tools from
text the server no longer served. Clearing it is a required step after any
description change. Nothing warns; the tool simply behaves as it did before
the change, which reads as the change not working.

**~~GPU embedding.~~ DONE.** Query embedding was **2,254 ms median** on
CPU-only Ollama, roughly 25× the entire search — the latency a user actually
feels, and hardware rather than code. `ollama` now carries the same
`gpu-reservation` anchor the engines use, and `ollama ps` reports **100% GPU**.

Measured on the reference host (RTX 3090, 20.2 GB already held by
`llama-server`, 3.5 GB free), warm, 30 runs:

| | median | p95 | min | max |
|---|---|---|---|---|
| CPU (`OLLAMA_CPUS=2`) | 94 ms | — | 11 ms | 106 ms |
| GPU (100% offload) | **5 ms** | 8 ms | 4 ms | 8 ms |

**18× on the median.** The resident cost is 849 MB, `ollama ps`'s own figure
including its CUDA context, which takes free VRAM from 3,466 MB to 2,634 MB.
Checked for the obvious regression — a second CUDA process on a card the engine
has 20 GB of — and there is none: engine throughput measured 19.3 tok/s median
with the embedder resident against 19.8 tok/s before, inside the run-to-run
spread (the baseline itself ranged 13.4–20.3 across prompts).

It is not only query latency. A full pass embeds one vector per public symbol,
so the bulk path moves by the same factor: **70 symbols in 0.43 s against
6.58 s**, which extrapolates a 10,000-symbol estate from 15.7 minutes to about
one. That was the other half of why embedding passes were something you ran
overnight.

The CPU figure moved a long way from the 2,254 ms originally recorded, which is
worth stating rather than quietly replacing: that number was taken cold, and
`OLLAMA_KEEP_ALIVE=-1` now keeps the model resident. 94 ms is the honest
like-for-like comparison, and 5 ms is what the GPU buys over it.

On a host where the engine needs the whole card, `OLLAMA_GPU_LAYERS` forces a
partial offload and `OLLAMA_GPU_DEVICE` keeps the embedder on a different GPU.
Both are documented in `docs/configuration.md`.

**More packs**, now that both fetch paths exist — a git clone and a release
archive cover essentially every documentation corpus worth having.

---

## Deliberately not next

Multi-tenant, HA, or a hosted service. Nothing measured points there, and the
current design — one SQLite file, one box, ACL resolved per request against
GitLab — is the reason it is simple enough to be correct. Distributed state
would cost that, and buy something nobody has asked for.
