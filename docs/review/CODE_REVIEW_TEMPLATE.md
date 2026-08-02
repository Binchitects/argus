# Code Review Template

Fill this in for every review. It is designed to be **comparable across models and agents** — the same reviewer prompt, the same task, different models, and the scores below tell you which review was actually worth having.

Copy this file to `docs/review/results/<task-id>--<model>.md` and complete every field. Emit the machine-readable block at the bottom too; the dashboard reads it.

---

## Identification

| Field | Value |
|---|---|
| Task / diff under review | |
| Base → Head | |
| Reviewer model | |
| Reviewer effort/mode | |
| Implementer model | |
| Review wall-clock (s) | |
| Reviewer tokens | |
| Reviewer tool calls | |

---

## Part 1 — Spec compliance

- **Verdict:** ✅ compliant · ❌ issues found · ⚠️ partially verifiable
- **Missing:** requirements skipped, or claimed but not implemented
- **Extra:** anything not requested (over-engineering counts)
- **Misunderstood:** right feature, wrong shape

Every claim needs a `file:line`.

## Part 2 — Findings

For each finding:

| # | Severity | file:line | Defect | Concrete failure scenario | Verified how |
|---|---|---|---|---|---|
| 1 | Critical / Important / Minor | | | inputs/state → wrong outcome | read / traced / reproduced |

**Severity must match the verdict.** An Important finding means the task is *Needs fixes*. A review that files an Important item and then approves is internally inconsistent — that is itself a defect in the review, and it is scored below.

## Part 3 — Test integrity

The single highest-value thing a reviewer does on this codebase.

- Which tests **discriminate** (fail when their bug is reintroduced) versus merely **prove existence** (fail only because the function is absent)?
- Could any assertion be satisfied by something *other* than the behaviour under test — an unrelated fixture, a proxied count, an identical error message from a weaker mechanism?
- Was any test demonstrated failing, or is that only claimed?

## Part 4 — Verdict

- **Task quality:** Approved · Needs fixes
- **Reasoning:** 1–2 sentences, technical.

---

## Scorecard — fill this in honestly

This is what makes reviews comparable. Score each 0–3.

| Axis | 0 | 1 | 2 | 3 | Score |
|---|---|---|---|---|---|
| **Verification depth** | Trusted the implementer's report | Read the diff | Traced control flow through unchanged code | Executed a reproduction or revert | |
| **Seam awareness** | Only looked inside changed functions | Noted callers | Traced a cross-module interaction | Found a defect that exists *only* in the composition | |
| **Test scepticism** | Accepted "tests pass" | Read the tests | Identified a non-discriminating assertion | Proved one non-discriminating by reverting | |
| **Severity calibration** | Labels inconsistent with verdict | Consistent but inflated | Consistent and proportionate | Consistent, proportionate, and argued | |
| **Signal-to-noise** | Manufactured findings to look thorough | Mostly style nits | Real findings + some noise | Every finding actionable | |
| **Prior-art respect** | Re-litigated settled decisions | Some redundancy | Stayed in scope | Stayed in scope *and* said what it deliberately did not re-check | |

**Total: __ / 18**

### Outcome tracking — fill in *after* the fix round

| Metric | Value |
|---|---|
| Findings raised | |
| Findings that survived scrutiny | |
| **False-positive rate** | |
| Findings the fix wave proved real | |
| Defects found *later* that this review missed | |
| Did its own fix introduce a new defect? | |

That last row matters more than it looks. On this project, **three separate fix waves introduced a defect of the same class they were fixing.** A review that only catches the first-order bug and misses the regression its own fix creates is worth measurably less than one that anticipates it.

---

## Machine-readable block

The dashboard parses this. Keep the fence and the key names exactly.

```json
{
  "task": "",
  "base": "",
  "head": "",
  "reviewer_model": "",
  "implementer_model": "",
  "duration_s": 0,
  "tokens": 0,
  "tool_calls": 0,
  "verdict": "approved | needs_fixes",
  "spec_compliant": true,
  "findings": [
    {
      "severity": "critical | important | minor",
      "location": "path/to/file.py:123",
      "summary": "",
      "verified_by": "read | traced | reproduced",
      "survived_scrutiny": null,
      "plan_mandated": false
    }
  ],
  "scores": {
    "verification_depth": 0,
    "seam_awareness": 0,
    "test_scepticism": 0,
    "severity_calibration": 0,
    "signal_to_noise": 0,
    "prior_art_respect": 0
  },
  "suite": { "passed": 0, "skipped": 0, "warnings": 0 },
  "notes": ""
}
```
