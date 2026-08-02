# Test GitLab — end-to-end verification harness

A disposable GitLab CE instance for proving things unit tests structurally cannot.

## Why this exists

Two questions had stayed open through all of Phase 1 and Phase 2 because nothing
short of a real GitLab could answer them:

**Q1 — does the service token actually see private projects?**
`gitlab.list_projects` calls `/projects` with `membership=false`. For a *non-admin*
token that returns only **public** projects. If your repositories are private and
the service token is not a member, Argus would index a fraction of the estate and
report success. Every capacity figure, every "which repo owns this" answer, and
every coverage claim rests on this being right.

**Q2 — can developer A read developer B's code through Argus?**
Every test to date proves the *code* filters: the allowlist reaches each query
first-positional, an empty allowlist returns nothing, the fixtures collide so the
allowlist is the only discriminator. None of that proves the *system* filters,
because none of it uses a real GitLab token resolved against real membership.

## The fixture is built to be falsifiable

Three **private** projects with genuine cross-repo `#include`s:

| Project | Contains | Member |
|---|---|---|
| `eal-core` | defines `DecodeFrame`, plus a `detail::` helper and a `static` one | `dev_alpha` (Reporter) |
| `etl-decoder` | includes `eal/decoder.h`, calls `DecodeFrame` | `dev_beta` (Reporter) |
| `driver-shim` | includes `eal/decoder.h`, calls `DecodeFrame` | **nobody** |

`DecodeFrame` is defined in one repo and called from both others on purpose: a
broken filter shows up as **extra rows**, not as an error. And `driver-shim` has
no members at all — if either developer can reach it through Argus, the design
has failed, and the check says so rather than passing quietly.

## Running it

```bash
docker compose -f deploy/test-gitlab/docker-compose.yml up -d
```

First boot takes several minutes. Watch until the API answers:

```bash
docker compose -f deploy/test-gitlab/docker-compose.yml logs -f gitlab
```

Then seed and verify:

```bash
python deploy/test-gitlab/seed.py
```

```bash
python deploy/test-gitlab/verify.py
```

`verify.py` exits non-zero if any check fails and writes `docs/verification-report.md`
with the index measurements (wall-clock, file/symbol/**public-symbol** counts — that
last one is the Phase 4 vector estimate) alongside the pass/fail table.

## Tear down

```bash
docker compose -f deploy/test-gitlab/docker-compose.yml down -v
```

## This is not production

Root password in plaintext, no TLS, monitoring stripped out to keep the container
under control. `seeded.json` holds real tokens for this throwaway instance and is
gitignored. Delete the whole thing when you are done.
