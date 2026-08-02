# Review — Phase 2 Task 7 (the five MCP tools)

Backfilled from the actual review run during development.

## Part 1 — Spec compliance
❌ Issues found. Allowlist plumbing, threadpool discipline and the two-repo colliding
fixture were all correct. But `get_file`'s description asserted a field the producing
tool never emits.

## Part 2 — Findings

| # | Severity | file:line | Defect | Failure scenario | Verified |
|---|---|---|---|---|---|
| 1 | Critical | `argus/mcpsrv/tools.py:246` | `find_references` returns `repo` (namespace string), no `repo_id`; `get_file` accepts `repo_id: int` only — and `_GET_FILE_DESC` tells the model `find_references` supplies one | Agent finds a mention in `g/beta/src/caller.c`, follows the description to read the file, and dead-ends on a false instruction | traced |
| 2 | Important | `argus/store/queries.py:117` | `QueryError` suggests retrying with `regex=True`, a parameter on no tool and no query function | Model's first recovery after any malformed FTS query is guaranteed invalid | read |
| 3 | Minor | `argus/mcpsrv/tools.py` | No catch-all; raw `sqlite3.OperationalError` becomes prompt text | Locked/corrupt DB leaks internals to the model | read |

## Part 3 — Test integrity
All repo-scoping tests use two repos with identical `src/a.c`, identical `SharedName`
and byte-identical `src/def.c` — so the allowlist is genuinely the only discriminator.
`test_tool_call_refuses_without_auth` uses a GitLab mock that raises if reached, proving
refusal precedes ACL resolution rather than following it.

## Part 4 — Verdict
**Needs fixes.** Security and concurrency correct; the primary agent workflow dead-ends.

## Scorecard

| Axis | Score | Why |
|---|---|---|
| Verification depth | 3 | Traced actual return shapes through `queries.py` rather than trusting the description |
| Seam awareness | 3 | The Critical exists *only* in the composition — both halves pass their own tests |
| Test scepticism | 2 | Verified the fixture forces discrimination; did not revert anything |
| Severity calibration | 3 | Critical → Needs fixes, consistent and argued |
| Signal-to-noise | 3 | Every finding actionable |
| Prior-art respect | 3 | Explicitly declined to re-litigate the approved auth boundary |

**Total: 17 / 18**

## Outcome tracking
All three findings survived and were fixed. No false positives. The fix wave introduced
no new defect (convergence check clean).

```json
{
  "task": "phase2-task07-tools",
  "base": "9adc1d5",
  "head": "3075b75",
  "reviewer_model": "opus",
  "implementer_model": "sonnet",
  "duration_s": 162,
  "tokens": 88010,
  "tool_calls": 7,
  "verdict": "needs_fixes",
  "spec_compliant": false,
  "findings": [
    {"severity":"critical","location":"argus/mcpsrv/tools.py:246","summary":"find_references returns a namespace string, not the repo_id get_file requires - and the tool description claims otherwise","verified_by":"traced","survived_scrutiny":true,"plan_mandated":false},
    {"severity":"important","location":"argus/store/queries.py:117","summary":"QueryError suggests regex=True, a parameter that exists on no tool","verified_by":"read","survived_scrutiny":true,"plan_mandated":false},
    {"severity":"minor","location":"argus/mcpsrv/tools.py","summary":"no catch-all; raw sqlite errors become prompt text","verified_by":"read","survived_scrutiny":true,"plan_mandated":false}
  ],
  "scores": {"verification_depth":3,"seam_awareness":3,"test_scepticism":2,"severity_calibration":3,"signal_to_noise":3,"prior_art_respect":3},
  "suite": {"passed":197,"skipped":0,"warnings":0},
  "notes": "Critical was invisible to every conventional test because both halves worked in isolation; the defect lived in prose."
}
```
