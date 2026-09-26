# Test GitLab — end-to-end verification harness

> **State:** `run.sh` brings the GitLab up and seeds it (projects, people, and
> their tokens in `seeded.json`), which is what the chat's Argus browser tests
> use. Its verifiers (`verify.py`, `verify_tools.py`) drove the Python Argus,
> which is retired: `src/Argus` is its .NET port, conformance-tested against it.
> Until they are ported to drive the .NET service, `run.sh` skips them and says
> so; `verify.py` is no longer in the repository.

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
./tools/test-gitlab/run.sh
```

That is the whole lifecycle in one command: start the instance, wait for the API
to actually be ready, seed the fixtures, run `verify.py`, and take the instance
down again — including when seeding or verification fails, because the run that
leaves it up is the one nobody comes back to.

| flag | what it does |
|---|---|
| *(none)* | up → seed → verify → **down** |
| `--keep` | leave the instance up when it finishes, for interactive work |
| `--skip-verify` | up and seed only |
| `--down` | stop it and delete its data |

**The fixture is not a service.** Its compose file says `restart: "no"` on
purpose, and it is worth being explicit about why: it used to say
`unless-stopped`, which is the one restart policy a disposable fixture must
never have. It survived reboots, came back after everyone had forgotten it, and
measurements on a machine where it had been up five hours and was serving
nothing showed **2.17% CPU and 2.63 GiB resident**. On a host whose entire
purpose is to keep a GPU and 24 cores busy with something else, that is a
fixture quietly competing with the thing it exists to test.

If the instance was already running when `run.sh` started — a previous `--keep`,
or a session you are in the middle of — it is left alone on exit and the script
says so. Tearing down something somebody else deliberately started is worse than
leaving it up.

### Doing it by hand

```bash
docker compose -f tools/test-gitlab/docker-compose.yml up -d
docker compose -f tools/test-gitlab/docker-compose.yml logs -f gitlab
```

First boot takes several minutes and the container reports `healthy` well before
the API is ready, so wait for `curl -fsS http://localhost:8929/-/readiness`
rather than for the healthcheck.

Then seed and verify:

```bash
python tools/test-gitlab/seed.py
python tools/test-gitlab/verify.py
```

Both need `httpx`, which the `argus` image has and a bare host usually does not;
`seed.py` additionally shells out to the `docker` CLI to run `gitlab-rails
runner`. `run.sh` wraps both in the `docker run` invocation that supplies them.

`verify.py` exits non-zero if any check fails and writes
`docs/argus/verification-report.md` with the index measurements (wall-clock,
file/symbol/**public-symbol** counts — that last one is the Phase 4 vector
estimate) alongside the pass/fail table.

`verify_tools.py` is the per-tool contract, and it is the one that cannot rot.
It drives the real MCP protocol over the wire with real developer tokens and
checks, for **every tool the server advertises**:

| check | what it catches |
|---|---|
| every advertised tool has a case, and every case names an advertised tool | a tool shipping untested, or a case left behind by a rename. There is no count to keep in sync |
| it answers with the declared shape (list vs object) | a tool whose result shape changed, which breaks every generated client while still returning 200 |
| no structured field names a repository the caller cannot read | an ACL hole, in all sixteen tools rather than in the three that were checked by hand |
| the project with no members is never named, for anyone | the single claim the whole design rests on |
| a refusal says which repository and who to maintainers ask | a refusal nobody can act on, which is the same as no refusal |
| at least one caller gets a real result | vacuity: "nothing leaked" is trivially true against empty results, which is how this project has repeatedly reported success for a query that never ran |

Tools whose preconditions the fixture cannot provide are **skipped and printed
as not covered**, never counted as passing. Today that is the six `docs_*`
tools, which need a documentation pack installed and fail with an actionable
message when there is none — the absence of a pack is not something an empty
result should be allowed to hide.

Set `ARGUS_TEST_WORK` to move the mirrors and index out of the checkout:

```bash
ARGUS_TEST_WORK=/var/lib/argus-test-work python tools/test-gitlab/verify.py
```

The default is `tools/test-gitlab/work`, inside the checkout, and SQLite in
WAL mode cannot open its shared-memory file on some bind-mounted filesystems —
on an NTFS checkout `argus index` dies with `disk I/O error` before indexing
anything. `run.sh` always sets it, to a Docker volume.

Set `ARGUS_TEST_GITLAB_URL` to reach the same GitLab from a different network
position (`run.sh` uses `http://host.docker.internal:8929` from `llm-net`), and
`ARGUS_OLLAMA_URL` to the stack's embedder so the vector half of the index is
built and `semantic_search` is exercised rather than skipped.

## Tear down

```bash
./tools/test-gitlab/run.sh --down
```

or, by hand:

```bash
docker compose -f tools/test-gitlab/docker-compose.yml down -v
```

`-v` matters: this instance's volumes are the disposable part, and leaving them
behind is how a temporary fixture accumulates tens of gigabytes.

## This is not production

Root password in plaintext, no TLS, monitoring stripped out to keep the container
under control. `seeded.json` holds real tokens for this throwaway instance and is
gitignored. Delete the whole thing when you are done.
