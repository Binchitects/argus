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
| 5/10 → 10/10 (`qwen3.6:27b`), 5/10 → 9/10 (`qwen3.6:35b`) | pack freshness — archives have no update path |
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
exclude. Milestone 2 is closed.

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

## Milestone 3 — operations

| item | why | today |
|---|---|---|
| Incremental pack rebuild | a docs refresh costs **162 min** for `win32` | full re-embed only |
| `pack update` for archive sources | assumes a git remote | broken by design |
| Webhook-driven indexing | freshness is interval-polled | `index_status` exists to *admit* staleness |
| Metrics endpoint | audit rows exist, no operational view | KPIs are CLI-only |

Incremental rebuild is the most valuable of these. The embedding cache
already makes a rebuild nearly free when chunks are unchanged — a `debugger`
rebuild reported `14,259 reused, 0 computed` and finished in seconds — so
the missing piece is detecting which upstream documents changed, not the
embedding economics.

---

## Milestone 4 — reach

**Upstream the three Hermes patches.** They live in a vendored install, and
a Hermes update silently reverts all three — including the
instructions-forwarding that is what made the tools work at all. The symptom
returns with no error anywhere. This is the single largest durability risk in
the deployment, and `/reload-mcp` is only a per-session workaround.

**GPU embedding.** Query embedding is **2,254 ms median** on CPU-only Ollama,
roughly 25× the entire search. It is the latency a user actually feels, and
it is hardware rather than code.

**More packs**, now that both fetch paths exist — a git clone and a release
archive cover essentially every documentation corpus worth having.

---

## Deliberately not next

Multi-tenant, HA, or a hosted service. Nothing measured points there, and the
current design — one SQLite file, one box, ACL resolved per request against
GitLab — is the reason it is simple enough to be correct. Distributed state
would cost that, and buy something nobody has asked for.
