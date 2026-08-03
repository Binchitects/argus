# Phase 3 — Cross-repo intelligence (`which_repo`, `repo_map`)

**Status:** design, approved 2026-08-04
**Depends on:** Phases 1, 2, 5 (shipped)
**Defers to Phase 4:** embeddings over private code, `semantic_search`

## Problem

Argus can answer *"where is this symbol defined"* across every repo a developer
can see. It cannot answer the question the project was started for:

> "help my developers find and apply changes in right repo (all my repos all
> related and needed to build final product)"

That is `which_repo`, and it has never been built. For a codebase whose
defining property is that its repositories are interdependent, this is the gap
that matters most.

## What already exists, unpopulated

Phase 1 built the schema for this work and deliberately left it empty. This is
not new structure; it is filling in structure that was designed for it.

- `includes` has `resolved_file_id`, `resolved_repo_id` and `is_external`
  columns. The writer populates **none** of them:
  `INSERT INTO includes (repo_id, file_id, raw, is_angle)`. Every include is
  stored as an unresolved raw string.
- `repo_deps(from_repo_id, to_repo_id, weight)` exists from migration 001 and
  **no code has ever inserted a row**.

**No new tables are required.** One migration (`008`) adds a single column,
`includes.resolution TEXT`, taking one of `resolved`, `external`, `ambiguous`,
`not_found`. Without it, an ambiguous include and an unfindable one are
indistinguishable — both leave `resolved_file_id` NULL — and the operator
statistic below could not be computed. Existing rows default to NULL, meaning
"never resolved", which is exactly true of every row written so far.

## Scope

Three pieces of work, in dependency order.

1. **Include resolution** — `argus/resolve.py` (new). Decide what each
   `includes.raw` points at; write `resolved_file_id`, `resolved_repo_id`,
   `is_external`.
2. **Graph materialization** — `argus/store/graph.py` (new). Aggregate
   resolved cross-repo edges into `repo_deps`.
3. **Two MCP tools** — `repo_map` and `which_repo`, allowlist-gated like every
   other private-index query, reading through `argus/store/queries.py` with
   `allowed_repo_ids` first-positional and no default.

### Explicitly not in this phase

No embeddings, no `semantic_search`, no vector tables. `which_repo` carries a
semantic-score slot that contributes zero when no vectors exist.

**Decision recorded:** when Phase 4 comes, it extends the knowledge-pack
pipeline — `packs/quantize.py`, `argus/embed.py`, the measured two-stage
search — rather than forking a second embedding stack. Phase 5 measured binary
+ int8 quantization at recall@10 0.946, which reduces the original spec's
~270 MB of float32 vectors for 70-90k symbols to ~8.6 MB coarse plus ~69 MB
rescore. Two embedding stacks in one product would drift.

## Design constraint: CPU-only is the floor

Production hardware is undecided. Measured on the current box, embedding one
query costs **2,254 ms** on CPU Ollama against **89 ms** for a whole pack
search. Nothing in Phase 3 may depend on hardware that might not be bought.

Three of the four input shapes below need no embeddings at all, so
`which_repo` ships useful on any machine. This inverts the original plan,
which put embeddings before `which_repo`.

## `which_repo`

### Input shapes

Developers arrive with any of four things. Detected in this order, first match
winning, because a diff *contains* paths and a stack trace *contains* symbol
names — the most specific pattern must be tested first.

| Shape | Detected by | Primary evidence |
|---|---|---|
| Diff | `diff --git`, `@@ … @@`, `+++`/`---` headers | Hunk header paths → `files` → repo |
| Stack trace | ≥2 lines matching `path:line`, `at <frame>`, or `0x…` | Frame paths → `files`; frame symbols → `symbols` |
| Symbol | Single identifier-shaped token, ≤2 words, matching `symbols.name` | Definition sites, `is_public` weighted higher |
| Prose | Everything else | FTS5 over `files_fts`, plus symbol-name term matches |

### Scoring

Each signal emits **evidence rows**; confidence is derived from evidence
rather than the reverse. `why` is the primary output and the number summarises
it.

```
score(repo) = w_direct  · direct_hits
            + w_lex     · normalised_fts_rank
            + w_sem     · semantic            # 0.0 in Phase 3
            - w_central · centrality_penalty
```

Weights vary by input shape. A diff or stack trace sets `w_direct` high and
`w_lex` near zero: the developer named files, so prose overlap would only add
noise. Prose inverts this.

**The weights and the evidence floor are constants, not magic numbers.** They
live in one module-level table keyed by input shape, and each value is
justified by a test case that fails if it changes materially — the same
discipline applied to the pack search's candidate pool, which was set from a
measured recall curve rather than taste. Their starting values are chosen
during implementation against a fixture of real queries; the requirement here
is that they be visible, documented, and defended by tests, not that they be
guessed correctly now.

**The evidence floor is defined concretely:** a repo qualifies if it has at
least one direct hit, or a lexical score at or above a stated fraction of the
best-scoring repo's. If no repo qualifies, the result is empty. This makes
"return nothing" a deterministic outcome rather than a judgement call.

**The centrality penalty applies only to inferred evidence, never to direct
hits.** Down-weighting high in-degree repos is right for prose — a shared
logging library matches every query that mentions logging, and is usually the
wrong place to make a product change. It is wrong when a stack frame points
directly into that library, where the shared repo genuinely is the answer. A
repo named explicitly in a diff or frame is never penalised for being popular.

### The refusal case

If no repo clears a minimum evidence floor, `which_repo` returns **empty with
a stated reason**, not a ranked list of the least-bad options.

A ranked list looks like an answer, and a 35B model acts on the top row. "No
repo in your allowlist matches this; try `search_code` with a distinctive
term" is more useful than a confident wrong repo, and far more useful than
three of them. This is the same rule that governs `docs_lookup`'s exactness
and the anchorless-symbol skip.

## Include resolution

A wrong edge here silently corrupts `repo_deps`, which corrupts centrality,
which corrupts every future answer — with no symptom anywhere. The algorithm
is specified rather than left to implementation taste.

### The suffix index

One pass builds a lookup over every header-extension file in `files`, keyed by
each of its path suffixes **aligned to `/` boundaries**. `src/eal/eal_thread.h`
registers under `src/eal/eal_thread.h`, `eal/eal_thread.h`, and
`eal_thread.h`.

Boundary alignment is not a detail: a naive `endswith` makes `eal_thread.h`
match `not_eal_thread.h`. That is the same class of defect as the
substring-blame bug in Phase 1, which deleted healthy symbols.

### Per include

1. **Quoted and relative** — resolve against the including file's directory
   first, as C does. `"../common/util.h"` from `src/eal/x.c` →
   `src/common/util.h`. A hit in the same repo resolves immediately.
2. **Suffix match** against the global index:
   - exactly one match → resolved
   - several → prefer same repo, then a repo this one already depends on, then
     fewest path components
   - still tied → **record unresolved with reason `ambiguous`, emit no edge**
   - none → `is_external = 1`
3. Angle-bracketed includes that match an indexed file are **internal**. C
   projects routinely `#include <eal/x.h>` via `-I`. Only unmatched angle
   includes are external.

### Ambiguity is not guessed

`util.h` may exist in a dozen repos. Choosing the "most likely" one produces
an edge that is invisible when wrong, permanent, and feeds the centrality
score behind every future answer.

An ambiguous include therefore emits no edge and is **counted**. The count
surfaces through `index_status`, because resolution quality is an operational
property: "34% of your includes are ambiguous" tells an operator their `-I`
layout defeats suffix matching, which no tool tuning will fix.

### Ordering

Resolution runs as a **separate pass after all repos are indexed**. An include
can point into a repo not yet indexed this cycle; resolving per-repo would
make the graph depend on indexing order.

Path comparison is case-sensitive, matching git. A project relying on
case-insensitive includes on Windows will show up as unresolved rather than
mis-resolved.

## Graph materialization

`repo_deps` is rebuilt wholesale inside one transaction. It is small, and
incremental maintenance would be a bug farm with no payoff. `weight` is the
count of distinct including files.

A failed pass leaves the previous graph intact rather than a half-updated one
— the same rule as the pack builder's temp-file-and-rename.

## Access control

`repo_deps` is a **global** graph; `repo_map` and `which_repo` may reveal only
repos in the caller's allowlist.

A developer who can see `eal-core` but not `etl-decoder` must not learn from
`repo_map` that `etl-decoder` depends on `eal-core`. **Filtering happens at
query time, not at build time**, so one shared graph serves everyone without
leaking edges. Both tools take `allowed_repo_ids` as the first positional
argument with no default, as every function in `store/queries.py` does.

## Error handling

- **An absent graph is a valid state, not an error.** Before the first
  resolution pass `repo_deps` is empty: `repo_map` returns empty with a
  reason, and `which_repo` runs with the centrality term at zero. Tools
  degrade rather than fail, as a model-mismatched pack does.
- A resolution failure aborts the pass and leaves the prior graph in place.
- Unresolvable and ambiguous includes are recorded with reasons, not dropped
  silently.

## Testing

Following this project's established rules, which exist because ten tests once
passed with their bug fully reintroduced:

- Every test demonstrated failing under a **targeted** revert. Deleting a whole
  function only proves the test calls it.
- **Non-emptiness asserted before any isolation or disjointness claim.**
- **ACL tests are load-bearing**: two developers, disjoint access, one shared
  graph; neither may see an edge touching a repo outside their allowlist.
- Specific defect tests: `not_eal_thread.h` must not resolve to
  `eal_thread.h`; a header name present in two repos must emit **zero** edges,
  not one; resolution must be independent of repo indexing order.
- `which_repo` must return **empty** rather than a ranked list when no
  evidence clears the floor.

## Success criteria

- `which_repo` answers all four input shapes with evidence, on a CPU-only box,
  with no embeddings present.
- `repo_map` reports dependencies and dependents, filtered to the caller's
  allowlist.
- Resolution statistics (resolved / external / ambiguous) visible to an
  operator.
- No repo outside a caller's allowlist is observable through either tool,
  including by inference from an edge.
- Full suite green locally and in the container, 0 skipped.
