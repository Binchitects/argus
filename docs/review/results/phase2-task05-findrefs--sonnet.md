# Review — Phase 2 Task 5 (`find_references`, name-based lexical)

Backfilled from the actual review run during development. Included as a **clean-approval**
data point: a review that found nothing Critical or Important is not a weak review, and the
scorecard should be able to say so.

## Part 1 — Spec compliance
✅ Compliant. Interface, return shape, allowlist position and scope boundaries all match.

## Part 2 — Findings

| # | Severity | file:line | Defect | Failure scenario | Verified |
|---|---|---|---|---|---|
| 1 | Minor | `tests/store/test_queries.py` | `_minimal_args_for`'s `search_code` branch used `fetchone()` with no `ORDER BY`; the fixture grew from one file per repo to three | A different pick returns punctuation-heavy content, fed to FTS5 as a raw `MATCH`, raising in an unrelated test | traced |

## Part 3 — Test integrity
The substring test is load-bearing, not decorative: `CALLER_C` places `SharedFunc();` on
line 2 and `SharedFuncV2();` on line 3 of an already-shortlisted file, and the test demands
exactly `[2]`. A substring scan yields `[2, 3]`. Verified `re.escape(name)` inside `\b…\b`,
so an identifier containing regex metacharacters (`foo.bar`, `a+b`, `*`) is neither an
injection nor a crash surface. `is_definition` matches on file **and** line, so it cannot
mark every occurrence in a file that merely contains a definition.

## Part 4 — Verdict
**Approved.**

## Scorecard

| Axis | Score | Why |
|---|---|---|
| Verification depth | 2 | Traced the regex and `is_definition` scoping in source; no execution |
| Seam awareness | 2 | Checked the fixture change against ~20 dependent tests |
| Test scepticism | 3 | Proved the substring test discriminates by reasoning out why a substring scan yields `[2,3]` |
| Severity calibration | 3 | One Minor, verdict Approved — consistent |
| Signal-to-noise | 3 | The single finding was real and later fixed |
| Prior-art respect | 3 | Named exactly what it treated as settled |

**Total: 16 / 18**

## Outcome tracking
The Minor was real and fixed (fixture lookup pinned to `src/a.c`). No false positives.

```json
{
  "task": "phase2-task05-findrefs",
  "base": "4340ca8",
  "head": "66cee3f",
  "reviewer_model": "sonnet",
  "implementer_model": "sonnet",
  "duration_s": 231,
  "tokens": 96698,
  "tool_calls": 9,
  "verdict": "approved",
  "spec_compliant": true,
  "findings": [
    {"severity":"minor","location":"tests/store/test_queries.py","summary":"fetchone() with no ORDER BY became ambiguous once the fixture grew to three files per repo","verified_by":"traced","survived_scrutiny":true,"plan_mandated":false}
  ],
  "scores": {"verification_depth":2,"seam_awareness":2,"test_scepticism":3,"severity_calibration":3,"signal_to_noise":3,"prior_art_respect":3},
  "suite": {"passed":168,"skipped":0,"warnings":0},
  "notes": "Clean approval. Included so the dataset contains a no-Critical case, not only dramatic finds."
}
```
