# KPIs

Chosen on one rule: **every indicator here would have caught a defect this
project actually shipped.** A metric that only ever goes up is decoration. The
useful ones are the ones that would have gone visibly wrong while everything
else looked fine.

Three tiers, by how they are measured. The split matters — most of what goes
wrong in a retrieval system is invisible to anything a machine can count.

---

## Tier 1 — Automatic, from the index

```bash
argus kpi --config /etc/argus/config.yaml
argus kpi --config /etc/argus/config.yaml --json >> /var/log/argus-kpi.jsonl
```

Append the JSON weekly. **A KPI looked at once is a number; the same KPI
looked at weekly is the only thing that catches slow decay.**

### Baseline, 2026-08-07

Measured against the test GitLab seeded with four real public C projects
(zlib, libpng, freetype, libjpeg-turbo). Production numbers will differ by
orders of magnitude — this is a shape, not a target.

| Indicator | Baseline | Direction | Why it exists |
|---|---|---|---|
| `repos_indexed` / `repos` | 7 / 7 | ↑ | Coverage. A gap means enumeration or mirroring is failing. |
| `repos_never_indexed` | 0 | ↓ | Has no age, so it cannot appear in staleness — invisible without its own counter. |
| `repos_unhealthy` | 0 | ↓ | Errored, timed out, or symbol extraction failed. |
| `median_repo_age_hours` | 1.4 | ↓ | Is the refresher running? |
| `stalest_repo_hours` | 1.4 | ↓ | **The important one.** An average hides one repo that stopped updating three weeks ago. |
| `symbols_per_1k_files` | 27,608 | ↑ | **The ctags canary.** |
| `resolved_include_rate` | 0.657 | ↑ | Graph completeness. |
| `ambiguous_include_rate` | 0.012 | ↓ | Leading indicator for `which_repo` quality. |
| `cross_repo_edges` | 5 | ↑ | The dependency graph emptying out. |
| `index_mb` / `mb_per_1k_files` | 30.9 / 25.8 | ↓ | Cost, and the input to the Postgres question. |

### The two that earn their place

**`symbols_per_1k_files` — the ctags canary.** Phase 1 shipped a defect where
a missing ctags let files be marked indexed with **zero symbols, permanently**.
Every other number stayed healthy: file counts rose, no errors were recorded,
`index_status` reported success. This number would have cratered. A test
asserts it halves when extraction stops.

**`ambiguous_include_rate` — the `which_repo` leading indicator.** A rising
share means many repos ship headers with the same basename, so the resolver
refuses to guess and the dependency graph thins. Answers degrade slowly and
nothing errors. At 1.2% across four real C projects, suffix matching is not
being defeated; a move to 10%+ means your `-I` layout has changed in a way no
tool tuning fixes.

**Watch rates, not counts.** Counts grow with the estate. Shares are
comparable across time.

---

## Tier 2 — Hand-checked, quarterly

These are the numbers that matter most, and **no machine can compute them.**
An automatic proxy for answer quality produces a number that rises while the
answers get worse.

Keep a fixed set of ~10 real questions per surface — questions your developers
actually asked, with a known correct answer. Re-run them each quarter and
record the **misses**, not just the score.

| Indicator | Baseline | How |
|---|---|---|
| `which_repo` top-1 accuracy | **8/10** | One question per input shape: prose, symbol, stack trace, diff |
| `docs_search` usefulness | **6 good / 2 partial / 2 wrong** | Ten real documentation questions |
| `docs_lookup` exactness | 8/8 exact | Known API names resolve to the defining page |

Both current miss-sets share a cause worth re-checking each time: **a page or
repo that *discusses* something outranking the one that *defines* it.** That
was true of `docs_search` in Phase 5 and of `which_repo`'s prose path today.

**Do not tune constants against this set.** Ten questions is a smoke test, not
a training set — fitting to it is how you fit to ten questions. If the misses
share a cause, fix the cause.

---

## Tier 3 — Engineering health

| Indicator | Current | Direction |
|---|---|---|
| Tests passing | 554 | ↑ |
| Tests skipped | 0 | ↓ — a skip is coverage that silently stopped running |
| Container suite green | yes | — |
| Defects found *after* merge | see below | ↓ |

### Where defects were found

The most useful engineering metric this project has, because it says which
gate is doing the work:

| Found by | Phase 3 | What it implies |
|---|---|---|
| Per-task review | 6 | Working as intended |
| **Whole-branch review** | **1 Critical, 3 Important** | Cross-module seams — invisible to task-scoped review |
| **Convergence check** | 1 Important | Fix waves introduce defects; Phase 1 saw 3 |
| **First real data** | **2** | Neither reachable from any fixture |
| Hollow tests found | 7 | Tests that could not fail |

**Track "hollow tests found".** Seven tests in Phase 3 passed while the
behaviour they named was broken, and several came from the plan itself. Each
was caught by a targeted revert — breaking the code and confirming the test
notices. That practice is why the count is known at all; a project that does
not do it has the same defects and no number.

The pattern to watch: **if per-task review starts finding everything and the
whole-branch review finds nothing, the whole-branch review has stopped
working** — not the code getting better.

---

## What is deliberately not measured

- **Query latency.** Currently 0.5 ms for `which_repo`, 2 ms `docs_lookup`,
  89 ms `docs_search`. All far below anything a developer notices, so tracking
  them would be watching noise. Revisit if the index grows two orders of
  magnitude.
- **Embedding latency**, at 2,254 ms on CPU Ollama — a property of the
  hardware, not the code, and it will change the day a GPU appears.
- **Usage counts.** The `audit` table records every tool call and could
  support them, but a rising call count says nothing about whether the answers
  were right, and would be easy to mistake for success.
