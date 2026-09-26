# CHANGELOG

One `##` per release, newest first. Under it, only the sections that release
has. Each release's full notes are also in its annotated tag — `git tag -n99
v2.9.0`.

Sections used:

- `:rocket:` **Epics and highlights** — the one or two things worth reading
- `:sparkles:` **New features & Enhancements**
- `:bug:` **Bugs fixed**
- `:boom:` **Breaking changes & Deprecations**
- `:arrow_up:` **Deps updates**

## Unreleased — v3.0.0

### :rocket: Epics and highlights

- **One app, three parts.** The project is now a .NET 10 backend serving a
  React frontend, llama.cpp for the models, and LiteLLM for the gateway. The
  Python package, Open WebUI, the Python admin console, the identity proxy,
  Traefik, Authelia, Prometheus, Grafana, Alertmanager, Loki, Langfuse, vLLM,
  Ollama and Redis are gone, with their configuration and scripts
- **A chat that searches your code as it answers.** The app's chat streams the
  model's reasoning and every code-index tool call as it happens; tools run in
  the backend as the signed-in person, with their GitLab access, and every chat
  goes to the gateway with that person's own key, so their budget binds
- **Accounts in the app.** People, roles, passwords (PBKDF2-SHA256, 600k
  iterations), sessions, and two kinds of personal key — code index keys for
  MCP clients and model keys for the gateway — managed on People and Settings
- **Tested in a browser.** A Playwright suite drives the built app against the
  real backend with fake GitLab, LiteLLM and embeddings on real sockets, from
  sign-in to a spent budget; CI runs it with the xUnit suite and the compose
  checks

### :sparkles: New features & Enhancements

- Chat: conversations saved and reloaded exactly, model picker, tools switch,
  Stop (ends generation upstream), rename, delete, Markdown without raw HTML
- Administration under `/manage`: overview with service health, people (add
  with a generated password, role, GitLab link, budget, reset, disable,
  delete), indexing, explore, knowledge packs; light and dark themes
- `argus user add|list|passwd|role|disable|enable`; the first administrator
  is created from `ARGUS_ADMIN_*` on first start
- Embeddings from llama.cpp (`llamacpp-embed`, nomic-embed-text on CPU) over
  the OpenAI protocol (`ARGUS_EMBED_URL`)
- `argus backup` includes the app database; `make backup` adds the gateway's
  database and `.env`, with checksums
- `argus verify --claude-hook` is a complete Claude Code Stop hook
- Optional HTTPS from PEM files (`ARGUS_TLS_CERT`, `ARGUS_TLS_KEY`)
- `make health` and `make smoke` (and PowerShell equivalents) check a live
  deployment end to end

### :bug: Bugs fixed

- `argus verify` never exited 2: it read a `status` that `verify_text` nests
  under `corrections`, so the Stop hook passed every contradicted draft. Its
  message now quotes what the draft said
- `docs_verify` reported the description as part of the last contract field
  (`User32.dll -- Displays a modal dialog box.`); contradicted fields carry
  `stated`
- Python docstrings never reached the index: ctags was not asked for each
  symbol's language. Symbol contract version 3 re-extracts on the next pass

### :boom: Breaking changes & Deprecations

- Sign-in is the app's own. Existing Authelia accounts are not migrated: add
  people on People (or with `argus user add`); their gateway spend history in
  LiteLLM is kept, keyed by the same email
- The stack is plain HTTP on `ARGUS_HTTP_PORT` (default 8080) unless
  `ARGUS_TLS_*` is set; there is no reverse proxy and no `*.llm.localhost`
- MCP clients authenticate with a code index key (`ak_…`) from Settings, or a
  GitLab token as before; the Open WebUI chat-client token is gone
- `.env` is shorter: regenerate it from `stack/env-samples/`
- Monitoring and tracing are no longer part of the stack; `/admin/metrics`
  still serves the index's Prometheus metrics for anyone who scrapes it

## v2.9.0 (2026-09-18)

### :rocket: Epics and highlights

- **The Windows packs know which Windows.** Microsoft publishes the OS each API
  arrived in, in the front matter of every sdk-api page, and the adapter parsed
  those keys and dropped them — so every pack built from that reference knew an
  API's header and its `.lib` and not whether the target machine exports the
  function at all. The OS fields now lead the contract both `docs_lookup` and
  `docs_search` return
- **The admin console manages packs.** A Knowledge packs page: installed packs
  with their version, embedding model, size and licence, plus install from a
  URL, update from a published index, and remove
- **A grounded evaluation, and the defect it found.** 52 questions whose
  answers are read from Microsoft's own front matter take the packs from
  **7/52 to 51/52** — and turned up 29,557 symbols that no lookup could reach

### :sparkles: New features & Enhancements

- Packs carry `req.target-min-winverclnt` (loaded on 52,506 of sdk-api's 65,908
  pages) and `req.target-min-winversvr` (50,530), across 285 values from
  Windows 2000 Professional to Windows 11 24H2 and Server 2025
- Packs carry `req.redist` (2,893 pages) — a different kind of requirement from
  an OS version: the API is there, and something else has to be installed first
- The driver reference gains `req.kmdf-ver` and `req.umdf-ver`. A WDF driver
  targets a framework release rather than an OS build, and pages like
  `WdfDriverCreate` state no OS at all. Inert for sdk-api, whose values for both
  keys are empty on every page
- Two new packs, `win32-samples` (136.2 MB) and `wdk-samples` (76.8 MB) — the
  sample corpora the composites always advertised and neither shipped pack
  contained. What calling an API looks like in a working program, against the
  reference's description of what it does. The set is now 11 packs, 1.87 GB,
  444,058 symbols, 0 unresolved each
- New admin endpoints `GET /admin/packs`, `POST /admin/packs/install|update|remove`,
  all under the admin token because all four write a directory the MCP path
  never touches. Install and update run as **jobs** — a pack is up to a
  gigabyte and the console's client gives up after ten seconds. Remove is one
  unlink and is deliberately not a job
- Update reads `ARGUS_PACK_INDEX_URL`. Unset, the button is absent and the card
  names the variable rather than failing, the same discipline as the webhook
  token
- New evaluation harness, `evals/run_windows_versions.py`, with an `--ablate`
  mode that builds the same pages twice so one variable moves

### :bug: Bugs fixed

- **Interface methods were unreachable under their documented name.** 45% of
  the reference is COM methods, titled `IFoo::Bar (header.h)` — a qualified name
  with no kind word, which the title regex required. The UID spells the same
  entity with a dot, so only that was indexed: 29,557 symbols carried a `.` and
  exactly 1 carried a `::`. `docs_lookup("IMFCaptureSource::GetMirrorState")`
  returned nothing. Worst shape a miss can take here — the server's
  instructions read an empty result as "undocumented" and forbid answering from
  memory, so the caller gets a refusal rather than a wrong answer
- README's pack table carried pre-rebuild sizes and claimed samples the `win32`
  pack does not contain; it is the API reference alone, 65,906 of sdk-api's
  65,908 files
- Corrected the pack doc's source list, which still named two sources of sixteen

### :boom: Breaking changes & Deprecations

- None. Packs built by an earlier adapter still install and still answer; they
  simply do not carry a version floor, and `docs_lookup` for a COM method under
  its `::` spelling still misses until they are rebuilt

## v2.8.0 (2026-09-17)

### :rocket: Epics and highlights

- **NVMe temperature on both dashboards, with no new exporter.** node-exporter
  mounts the host `/sys`, so its hwmon collector had been scraping the kernel's
  `nvme` sensors all along and Prometheus already held
  `node_hwmon_temp_celsius` for both drives. The gap was that no panel read it

### :sparkles: New features & Enhancements

- `resources.json` and `stack-performance.json` gain NVMe temperature panels.
  The chips arrive as `nvme_nvme0` / `nvme_nvme1` with nothing linking a chip to
  a drive model, so `label_replace` names them cheapest-layer-first: a generic
  fallback, so a drive added later still appears at all, and two exact matches
  on top for the real models
- Thresholds are a drive's — yellow 70, red 85 — because an NVMe throttles well
  before a core does

## v2.7.1 (2026-09-17)

### :bug: Bugs fixed

- **Indexing a second branch emptied the cross-repo graph for the whole
  estate.** A shared header appeared once per ref, the include resolver could
  not choose between two identical paths and recorded it `ambiguous`, so every
  edge into that project vanished — measured on the fixture as 2 edges to 0,
  with nothing anywhere reporting a problem

### :sparkles: New features & Enhancements

- Resolution now prefers the branch the include came from, as a preference
  rather than a filter, so a header that exists only on another branch still
  resolves
- The cold fixture lifecycle indexes trunk and a release branch and asserts the
  graph survived with zero ambiguous includes

## v2.7.0 (2026-09-17)

### :rocket: Epics and highlights

- **Argus reads what the code does, not just what it is called.** The doc
  comment above every definition was already in the index (`files.content` plus
  `symbols.line`) and had never been read. Measured on a deliberately
  falsifiable fixture: for *"what reclaims keys whose time to live has
  elapsed"*, ranking on names alone returns the wrong function and ranking with
  the doc returns the right one — and the mirror question flips the same way

### :sparkles: New features & Enhancements

- Doc comments are extracted, stored, led into the embedded text, and returned
  on every symbol result
- New `overview` tool describing what each repository *is*: its README, its
  layout, its key symbols
- Multi-branch indexing: name a branch and get that branch, name none and get
  trunk. An unindexed branch is refused by name, listing the branches that are
- Branch-agnostic behaviour is verified rather than assumed — eight checks over
  a real two-branch index, part of the cold fixture lifecycle

### :bug: Bugs fixed

- `symbols_sha` could not notice an extractor change
- `argus index` never embedded at all, so the poller and the webhook were
  indexing code `semantic_search` could never see
- Vectors could not notice their text had changed
- vec0 tables accumulated orphans without bound while polluting the KNN stage

## v2.6.1 (2026-09-17)

### :bug: Bugs fixed

- `ArgusIndexErrored` fired for ever over a repository whose worktree directory
  existed but was not a usable worktree: the check tested only that the path was
  there, so it took the checkout branch, failed, and could never take the
  worktree-add branch that would have rebuilt it. An alert that cannot clear is
  what teaches people to ignore the ones that can
- The Dockerfile guard found its own gap and now discovers the repo's top-level
  trees instead of listing two

## v2.6.0 (2026-09-17)

### :rocket: Epics and highlights

- **verify-after has an enforcement point.** `docs_verify` was an MCP tool, and
  that was the problem: every client can run a shell command when the model
  finishes and block on its exit code; almost none can be made to call a *tool*
  at that moment

### :sparkles: New features & Enhancements

- `argus verify` is the same check with an exit code — 0 clean, **2 contradicted
  (blocking)**, 6 could not check and deliberately **not** blocking, because a
  deployment without packs would otherwise become an agent that cannot finish a
  sentence
- `clients/claude-code/verify-after.sh` wires it into a Stop hook

## v2.5.6 (2026-09-17)

### :sparkles: New features & Enhancements

- An Explore page in the admin console that answers what four identical-looking
  chat failures actually are: not in the code, named differently, private, or
  never indexed. Symbols by fragment, files by path, repository counts —
  including the file showing **0 symbols**, which is the difference between "the
  agent cannot find it" and "it is not in the index"
- Its queries are unfiltered by design, so they live in `store/explore.py` apart
  from the access-scoped ones, with a test asserting no MCP tool module imports
  them

### :bug: Bugs fixed

- The route cleared `row_factory`, so it answered 200 with an error beside empty
  lists and the console drew "Nothing is indexed yet" while the index held
  seventy symbols

## v2.5.5 (2026-09-17)

### :rocket: Epics and highlights

- **Index on push, not only on a timer.** `POST /hook/gitlab` takes a GitLab
  push event and indexes the repository that changed, gated by its own
  `ARGUS_WEBHOOK_TOKEN` (unset = the route does not exist)

### :sparkles: New features & Enhancements

- A push during a pass is queued rather than dropped, drained one repository per
  pass; an overfull queue collapses into one full pass
- Events it has no use for are acknowledged rather than refused, because GitLab
  disables a webhook that keeps failing. The poll stays on as the floor

## v2.5.4 (2026-09-17)

### :bug: Bugs fixed

- **`pack update` had no producer.** Nothing could make the index JSON it reads,
  a format with a mandatory checksum and a consumer-facing URL that existed only
  in the parsing code. `argus pack index` now writes it
- The update command itself had no test, only its parts did
- An archive refetch kept pages the new release deleted, because extraction
  wrote into the existing tree without clearing it — so the pack reported the
  new version while serving documentation that no longer exists

## v2.5.3 (2026-09-17)

### :rocket: Epics and highlights

- **Every MCP tool is contract-tested against live Argus.** `verify_mcp.py`
  asserted `len(tools) == 5` against a server with sixteen, so it could only
  ever fail and nobody ran it

### :sparkles: New features & Enhancements

- `verify_tools.py` takes the tool list **from the server** so there is no count
  to keep in sync, calls every tool over StreamableHTTP with real GitLab tokens,
  checks each result's declared shape, and asserts no structured field names a
  repository the caller cannot read
- Refusals the server makes on purpose are classified rather than reported as
  breakage; tools the fixture cannot exercise print as **NOT COVERED** rather
  than counting as passing — 41/41 with 18 reported skips

### :bug: Bugs fixed

- `ensure_mirror` did not retarget a mirror whose GitLab URL changed, so a host
  migration would have failed every repository at once with an error naming an
  address no longer in the configuration
- A vacuity guard in `verify.py` printed its failure text on success and let the
  isolation checks pass against nothing
- The Dockerfile COPY whitelist, which had then broken `docker build` four times

## v2.5.2 (2026-09-17)

### :bug: Bugs fixed

- **The bundled test GitLab is a fixture again.** It was
  `restart: unless-stopped`, so it survived reboots and sat at 2.63 GiB and
  2.17% CPU indefinitely while serving nothing. It is `restart: "no"` now, with
  `scripts/test-gitlab/run.sh` as its lifecycle — up, wait, seed, verify,
  teardown, **including on failure**
- `run.sh` waited on a readiness endpoint GitLab 404s through Docker NAT
- `verify.py` wrote its report to a path the docs restructure left behind
- Its work dir had to move off the checkout for SQLite WAL to work at all

## v2.5.1 (2026-09-17)

### :rocket: Epics and highlights

- **Embeddings on the GPU.** The embedder was CPU-only and capped at two cores,
  which made it the entire cost of an indexed lookup. Measured warm:
  **94 ms → 5 ms median per embed, 18×**, for 849 MB of VRAM

### :sparkles: New features & Enhancements

- Bulk passes move by the same factor: 70 symbols, 6.58 s → 0.43 s
- `acceptance.py` now asserts `ollama ps` reports GPU, because Ollama falls back
  to the CPU silently and nothing else in the stack would notice
- Engine throughput unchanged: 19.3 vs 19.8 tok/s, inside the run-to-run spread

## v2.5.0 (2026-09-17)

### :rocket: Epics and highlights

- **The index measures itself and reindexes itself.** Argus exports Prometheus
  metrics for index freshness, an `ArgusIndexStale` alert watches them, and the
  serve process now runs the periodic reindex that was documented but never
  wired up

### :sparkles: New features & Enhancements

- The admin console's Overview shows the same numbers, from the same snapshot
  the alert is built on — so the tile, the line in Grafana and the page cannot
  disagree

### :bug: Bugs fixed

- A repository with no branches silently disappeared from a run
- An admin-panel test gate sat mid-file, so half the checks could not fail
- `package.sh` shipped config files the containers cannot read when the builder
  has a tight umask

## v2.4.0 (2026-09-16)

### :rocket: Epics and highlights

- **The admin panel becomes a console.** A sidebar with Overview, People, Model,
  Indexing, Monitoring and Settings; search and pagination over people;
  per-person pages; **delete an account** (which did not exist); CSV export with
  formula-injection escaping; service health; a theme toggle

### :bug: Bugs fixed

- It would delete the account you are signed in as — which it did to the live
  administrator during testing
- It would delete the last admin

## v2.3.0 (2026-09-16)

### :rocket: Epics and highlights

- **Thinking level is now a per-chat choice**, from the Open WebUI model picker:
  the stack creates a `Deep think` / `Balanced` / `Quick` / `No thinking` model
  per level, verified end to end at 2654 / 1251 / 0 characters of reasoning

### :bug: Bugs fixed

- The `Reasoning Effort` field Open WebUI already has does nothing on this
  stack — LiteLLM drops it for a custom `openai/` api_base — which the docs had
  been quietly wrong about

## v2.2.0 (2026-09-16)

### :sparkles: New features & Enhancements

- `clients/`: a copy-pasteable sample config per agent harness, each marked with
  whether it was actually executed
- **Qwen Code was tested for the first time** (qwen 0.23.3, against the stack's
  own gateway), DeepSeek Harness re-verified after the restructure, and the four
  gotchas that only appear when a real client drives it are now written down

## v2.1.2 (2026-09-16)

### :bug: Bugs fixed

- "failed to connect to Argus" in Open WebUI now says why. Open WebUI renders
  any 401 as a connection failure, and the sentence explaining the refusal went
  into the response body and nowhere else, so the logs said only
  `reason=token_rejected`
- `denied` events now carry `detail`, which is what makes the usual cause — the
  person has no GitLab account — visible from `docker compose logs argus`

## v2.1.1 (2026-09-16)

### :bug: Bugs fixed

- **The airgap bundle, verified by booting it on a clean project** — which found
  four ways it could fail on a host that cannot fix it:
  - the locally-built images were tied to `COMPOSE_PROJECT_NAME`, so renaming
    the project made `up` die on `No such image`
  - nothing checked the bundle covered the profiles about to run
  - the image archives shipped mode 0600, so extracting as root then running as
    yourself gave permission denied
  - the documented split-join silently wrote a 0-byte file on a read-only mount

### :sparkles: New features & Enhancements

- A profile-coverage check in `load.sh --check`

## v2.1.0 (2026-09-16)

### :sparkles: New features & Enhancements

- **A repository you can navigate**, and packaging scripts that check their own
  output: `argus/` → `src/argus/`, `llm-stack/` → `stack/`, one `docs/` tree,
  repository tooling in `scripts/`, and the older standalone Caddy deployment
  retired
- `release.sh` gained an offline mode

### :bug: Bugs fixed

- The drop-in archive had been shipping neither the admin panel nor the CPU
  exporter, and refused to build at all on a machine where the stack had run
- The airgap bundler never read its own archive back, and now does

## v2.0.0 (2026-09-16)

### :rocket: Epics and highlights

- **Indexing you can watch and logs you can search**: Loki on by default, an
  Indexing dashboard, and five dead or silent tools fixed

### :bug: Bugs fixed

- `impact_of` failed for every permitted caller
- `semantic_search` failed for everyone
- The per-repo progress table had never worked
- An unreachable GitLab exited on a raw traceback
- The panel discarded a failed run's log, leaving only an exit code

### :sparkles: New features & Enhancements

- GitLab username/password auth
- A redesigned admin panel
- Every path as an `.env` variable

## v1.14.0 (2026-09-15)

### :sparkles: New features & Enhancements

- Offline bundles
- A settable thinking level
- RTX 5090 / NVFP4 samples
- A verified backup
- The Argus audit trail

### :bug: Bugs fixed

- Four silent deploy failures

## v1.13.0 (2026-09-14)

### :sparkles: New features & Enhancements

- Argus per person in Open WebUI, read-only
- "ask a maintainer" notices
- All instructions in the README

## v1.12.1 (2026-09-14)

### :bug: Bugs fixed

- Fresh-clone deploy verified

## v1.12.0 (2026-09-14)

### :sparkles: New features & Enhancements

- `docker compose up` is the whole deployment

## v1.11.0 (2026-09-13)

### :sparkles: New features & Enhancements

- The documentation tool accepts the questions it is actually asked

## v1.10.0 (2026-09-13)

### :sparkles: New features & Enhancements

- Deployable from a clean clone

## v1.9.0 (2026-09-07)

### :sparkles: New features & Enhancements

- Argus by default, and the 177B MoE question answered

## v1.8.0 (2026-09-07)

### :sparkles: New features & Enhancements

- Acceptance-tested

## v1.7.0 (2026-09-07)

### :sparkles: New features & Enhancements

- The domain is settable, and setup is re-runnable

## v1.6.0 (2026-09-06)

### :sparkles: New features & Enhancements

- A clean checkout deploys unattended

## v1.5.0 (2026-09-04)

### :bug: Bugs fixed

- Tool calls that survive streaming

## v1.4.0 (2026-09-04)

### :sparkles: New features & Enhancements

- One number per person, and the key that produced it

## v1.3.0 (2026-09-04)

### :sparkles: New features & Enhancements

- Monitoring that outlives the engine

## v1.2.0 (2026-09-04)

### :sparkles: New features & Enhancements

- Long context on a 24 GB card

## v1.1.0 (2026-08-23)

Forty-nine commits since v1.0.0. The theme is retrieval quality: v1.0 could
index and serve, but nobody had measured whether `docs_find` actually answered
description-shaped questions. It did not, and most of the reason was data rather
than ranking.

Note that `pyproject.toml` still read `0.1.0rc1` throughout v1.0 — the version
was never bumped at that release. It now tracks the tag.

### :rocket: Epics and highlights

- **`docs_find` answers roughly twice as often.** Measured over a 36-question
  set, top-10: **25% → 44%** unscoped, and **58%** when the caller names the
  source

### :sparkles: New features & Enhancements

- **`docs_find` now serves the hybrid arm.** `search_symbols_hybrid` was
  implemented, documented, and had no callers — the tool ran the purely lexical
  arm. Term overlap between a question and its answer's description is 35%, so a
  term scorer had a low ceiling however it weighted; twelve of twenty-five
  answers shared one word with the question or none
- **Every pack now has zero blank descriptions.** `docs_find` searches that field
  and skips rows where it is empty, so a blank description made a symbol
  invisible while it still occupied disk. cpp went from 100% blank, python from
  50%
- **Descriptions come from the page, not the page's title.** cpp two-word
  descriptions 56% → 2.2%, python 62% → 16.3%. `_countof`'s entire searchable
  text had been "_countof Macro"
- **A chunk now says which of a page's symbols it documents.** A 369-symbol page
  returned an arbitrary 8 of them, ordered by rowid
- **The tool description names the installed sources**, and a `lang` naming no
  installed pack widens instead of returning nothing. Measured through Hermes:
  the model passes `lang` on 5 of 8 calls, including `scripting` for a
  PowerShell question — knowable only from that list
- **`gitlab.auth: password`** for username/password sign-in, alongside the
  existing access token. The two are not interchangeable: an access token goes
  in `PRIVATE-TOKEN`, an OAuth token in `Authorization: Bearer`.
  `argus/credentials.py` owns that distinction and every API caller asks it for
  headers
- The password is read from `ARGUS_GITLAB_PASSWORD` only; a `password` key in
  the config file is **refused**, not ignored
- **Indexing at estate scale**, first run: 47 repositories, 55,603 files,
  **1,491,167 symbols**, 37.8 minutes, zero failures or timeouts
- **`which_repo` ranks on the raw score**, not the display-clamped `confidence`.
  Every score above 1.0 compared equal and ties broke alphabetically — lz4 beat
  zstd for "compress a byte stream with a dictionary" because `l` sorts before
  `z`
- Packs rebuilt against current upstream: cpp, python (3.14), wdk, win32,
  scripting. The python pack records the branch it was actually built from — it
  claimed `main` while built from `3.14`

### :boom: Breaking changes & Deprecations

- **Verified against GitLab 19.2.1: recent GitLab has removed the password
  grant**, and no headless username/password path replaces it. The error says so
  and names the fix rather than reading like a bad password. Use `auth: token`
  unless your GitLab predates the removal

### :bug: Bugs fixed

- **Known and unfixed:** `which_repo`'s lexical evidence matches query words
  against identifiers, and at 1.5M symbols "store", "key" and "memory" are
  identifiers nearly everywhere. Asked to "store key-value pairs in memory with
  expiry", redis did not place. Routing is 5/10 on the estate set

## v1.0.0 (2026-08-12)

### :sparkles: New features & Enhancements

- Initial release. 11 packs, ACL enforced structurally and audited, container
  healthy, 741 tests

## v0.1.0-rc1 (2026-08-07)

First tagged release: a private GitLab code index with per-developer access
control, nine MCP tools, a cross-repo dependency graph, and portable public
documentation packs. Shipped Phases 1, 2, 3 and 5; 539 tests, green in the
container.

### :rocket: Epics and highlights

- Measured on real code — four public C projects, because production GitLab was
  not reachable — 1,199 files, 33,102 symbols, 0 errors, a 14.8 s cold pass,
  1.2% ambiguous includes, and `which_repo` 8/10 top-1 at a 0.5 ms median. Full
  numbers and misses in `docs/index-measurements.md`

### :boom: Breaking changes & Deprecations

- **Not yet validated at production scale.** Pilot with a limited repo set and a
  handful of developers; `docs/roadmap.md` Step 0 says what the pilot is meant
  to produce
