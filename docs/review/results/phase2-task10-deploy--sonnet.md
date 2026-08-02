# Review — Phase 2 Task 10 (serve, flush-acl, container, TLS)

Backfilled from the actual review run during development.

## Part 1 — Spec compliance
❌ Issues found. CLI work and tests were solid; the deployment — the task's core
deliverable — was non-functional end to end.

## Part 2 — Findings

| # | Severity | file:line | Defect | Failure scenario | Verified |
|---|---|---|---|---|---|
| 1 | Critical | `argus/cli.py` `_serve` / `argus/mcpsrv/server.py` `create_app` | FastMCP computes its DNS-rebinding Host allowlist at construction; overriding `app.settings.host` afterwards never updates `transport_security.allowed_hosts` | Caddy proxies `Host: argus.internal`; every `/mcp` call — the only endpoint Hermes uses — returns `421 Invalid Host Header` | reproduced |
| 2 | Important | `docs/deployment.md` | The documented smoke test curls `/healthz`, a custom Starlette route appended outside the session manager that receives `security_settings` | Smoke test returns 200 while the product is entirely unreachable — false confidence | traced |
| 3 | Minor | `docker-compose.yml` | `caddy:2-alpine` unpinned | Reproducibility drift | read |

Reproduction, in-process, no network:

```
at construction : ['127.0.0.1:*', 'localhost:*', '[::1]:*']
after host set  : ['127.0.0.1:*', 'localhost:*', '[::1]:*']
```

## Part 3 — Test integrity
Checked each test for discrimination rather than existence. `test_serve_defaults_to_localhost`
asserts `!= "0.0.0.0"`; `test_flush_acl_with_user_clears_only_that_user` asserts the *other*
user's row survives; the two message tests assert mutually-exclusive substrings. All
discriminate. `test_serve_bad_config_returns_2` proves existence only — acceptable, it
exercises untouched shared code.

## Part 4 — Verdict
**Needs fixes.** 223 passing tests, two clean image builds and a valid compose config all
reported success while the deployment rejected every real call.

## Scorecard

| Axis | Score | Why |
|---|---|---|
| Verification depth | 3 | Executed an in-process probe proving the allowlist does not track the host override |
| Seam awareness | 3 | Defect exists only between the SDK's construction-time defaults and the proxy's forwarded Host |
| Test scepticism | 3 | Classified every test discriminates-vs-proves-existence, unprompted |
| Severity calibration | 3 | Critical → Needs fixes |
| Signal-to-noise | 3 | Three findings, all real |
| Prior-art respect | 2 | Stayed in scope; did not state what it declined to re-check |

**Total: 17 / 18**

## Outcome tracking
All findings survived. No false positives. A security warning fired on this review — it was
investigated and judged a false positive: the reviewer's probe bound no port, started no
server, and left the tree clean.

```json
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
    {"severity":"critical","location":"argus/cli.py:_serve","summary":"Host allowlist fixed at FastMCP construction; every proxied /mcp call returns 421","verified_by":"reproduced","survived_scrutiny":true,"plan_mandated":false},
    {"severity":"important","location":"docs/deployment.md","summary":"smoke test curls /healthz, which bypasses the failing middleware and returns 200","verified_by":"traced","survived_scrutiny":true,"plan_mandated":false},
    {"severity":"minor","location":"docker-compose.yml","summary":"caddy image unpinned","verified_by":"read","survived_scrutiny":true,"plan_mandated":false}
  ],
  "scores": {"verification_depth":3,"seam_awareness":3,"test_scepticism":3,"severity_calibration":3,"signal_to_noise":3,"prior_art_respect":2},
  "suite": {"passed":223,"skipped":0,"warnings":0},
  "notes": "Caught a total functional failure that unit tests, image builds and compose validation all missed."
}
```
