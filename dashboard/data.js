window.ARGUS_DATA = {
  "generated_at": "2026-08-02T16:17:05+00:00",
  "reviews": [
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
        {
          "severity": "minor",
          "location": "tests/store/test_queries.py",
          "summary": "fetchone() with no ORDER BY became ambiguous once the fixture grew to three files per repo",
          "verified_by": "traced",
          "survived_scrutiny": true,
          "plan_mandated": false
        }
      ],
      "scores": {
        "verification_depth": 2,
        "seam_awareness": 2,
        "test_scepticism": 3,
        "severity_calibration": 3,
        "signal_to_noise": 3,
        "prior_art_respect": 3
      },
      "suite": {
        "passed": 168,
        "skipped": 0,
        "warnings": 0
      },
      "notes": "Clean approval. Included so the dataset contains a no-Critical case, not only dramatic finds.",
      "_source": "phase2-task05-findrefs--sonnet.md",
      "_score_total": 16
    },
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
        {
          "severity": "critical",
          "location": "argus/mcpsrv/tools.py:246",
          "summary": "find_references returns a namespace string, not the repo_id get_file requires - and the tool description claims otherwise",
          "verified_by": "traced",
          "survived_scrutiny": true,
          "plan_mandated": false
        },
        {
          "severity": "important",
          "location": "argus/store/queries.py:117",
          "summary": "QueryError suggests regex=True, a parameter that exists on no tool",
          "verified_by": "read",
          "survived_scrutiny": true,
          "plan_mandated": false
        },
        {
          "severity": "minor",
          "location": "argus/mcpsrv/tools.py",
          "summary": "no catch-all; raw sqlite errors become prompt text",
          "verified_by": "read",
          "survived_scrutiny": true,
          "plan_mandated": false
        }
      ],
      "scores": {
        "verification_depth": 3,
        "seam_awareness": 3,
        "test_scepticism": 2,
        "severity_calibration": 3,
        "signal_to_noise": 3,
        "prior_art_respect": 3
      },
      "suite": {
        "passed": 197,
        "skipped": 0,
        "warnings": 0
      },
      "notes": "Critical was invisible to every conventional test because both halves worked in isolation; the defect lived in prose.",
      "_source": "phase2-task07-tools--opus.md",
      "_score_total": 17
    },
    {
      "task": "phase2-task10-deploy",
      "base": "4e64f54",
      "head": "323c5f0",
      "reviewer_model": "sonnet",
      "implementer_model": "sonnet",
      "duration_s": 281,
      "tokens": 115711,
      "tool_calls": 25,
      "verdict": "needs_fixes",
      "spec_compliant": false,
      "findings": [
        {
          "severity": "critical",
          "location": "argus/cli.py:_serve",
          "summary": "Host allowlist fixed at FastMCP construction; every proxied /mcp call returns 421",
          "verified_by": "reproduced",
          "survived_scrutiny": true,
          "plan_mandated": false
        },
        {
          "severity": "important",
          "location": "docs/deployment.md",
          "summary": "smoke test curls /healthz, which bypasses the failing middleware and returns 200",
          "verified_by": "traced",
          "survived_scrutiny": true,
          "plan_mandated": false
        },
        {
          "severity": "minor",
          "location": "docker-compose.yml",
          "summary": "caddy image unpinned",
          "verified_by": "read",
          "survived_scrutiny": true,
          "plan_mandated": false
        }
      ],
      "scores": {
        "verification_depth": 3,
        "seam_awareness": 3,
        "test_scepticism": 3,
        "severity_calibration": 3,
        "signal_to_noise": 3,
        "prior_art_respect": 2
      },
      "suite": {
        "passed": 223,
        "skipped": 0,
        "warnings": 0
      },
      "notes": "Caught a total functional failure that unit tests, image builds and compose validation all missed.",
      "_source": "phase2-task10-deploy--sonnet.md",
      "_score_total": 17
    }
  ],
  "index_status": {
    "collected": false,
    "reason": "not requested (pass --config)"
  },
  "problems": [],
  "score_axes": [
    "verification_depth",
    "seam_awareness",
    "test_scepticism",
    "severity_calibration",
    "signal_to_noise",
    "prior_art_respect"
  ]
};
