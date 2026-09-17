# Changelog

Each release's full notes are in its annotated tag: `git tag -n99 <version>`.

- **v2.8.0** (2026-09-17) — **NVMe temperature** on both dashboards, from sensors node-exporter was already reading. No new exporter: it mounts the host `/sys`, so its hwmon collector had been scraping the kernel's `nvme` sensors all along and Prometheus already held `node_hwmon_temp_celsius` for both drives — the only gap was that no panel read it. The chips arrive as `nvme_nvme0` / `nvme_nvme1` with nothing linking a chip to a drive model, so `label_replace` names them cheapest-layer-first: a generic fallback, so a drive added later still appears at all, and two exact matches on top for the real models. `temp1` is the SMART composite, and the thresholds are a drive's (yellow 70, red 85) rather than a core's, because an NVMe throttles well before a CPU does

- **v2.7.1** (2026-09-17) — indexing a second branch emptied the cross-repo graph for the **whole estate**: a shared header appeared once per ref, the include resolver could not choose between two identical paths and recorded it `ambiguous`, so every edge into that project vanished — measured on the fixture as **2 edges to 0**, with nothing anywhere reporting a problem. Resolution now prefers the branch the include came from, and it is a preference rather than a filter, so a header that exists only on another branch still resolves. The cold fixture lifecycle now indexes trunk and a release branch and asserts the graph survived with zero ambiguous includes
- **v2.7.0** (2026-09-17) — **Argus reads what the code does, not just what it is called.** The doc comment above every definition was already in the index (`files.content` plus `symbols.line`) and had never been read; it is now extracted, stored, led into the embedded text, and returned on every symbol result. Measured on a deliberately falsifiable fixture: for "what reclaims keys whose time to live has elapsed", ranking on names alone returns the wrong function and ranking with the doc returns the right one — and the mirror question flips the same way. Also fixes four freshness bugs found by running it (`symbols_sha` could not notice an extractor change, **`argus index` never embedded at all**, so the poller and the webhook were indexing code `semantic_search` could never see, vectors could not notice their text had changed, and vec0 tables accumulated orphans without bound), and adds an `overview` tool describing what each repository *is*
- **v2.6.1** (2026-09-17) — `ArgusIndexErrored` fired for ever over a repository whose worktree directory existed but was not a usable worktree: the check tested only that the path was there, so it took the checkout branch, failed, and could never take the worktree-add branch that would have rebuilt it. An alert that cannot clear is what teaches people to ignore the ones that can. Verified on the live broken data — `usable False -> True`, then a real pass with `stale 0`, `errored 0` and no firing alerts. The Dockerfile guard also found its own gap and now discovers the repo's top-level trees instead of listing two
- **v2.6.0** (2026-09-17) — **verify-after has an enforcement point.** `docs_verify` was an MCP tool, and that was the problem: every client can run a shell command when the model finishes and block on its exit code, almost none can be made to call a tool at that moment. `argus verify` is the same check with an exit code — 0 clean, **2 contradicted (blocking)**, 6 could not check and deliberately **not** blocking, because a deployment without packs would otherwise become an agent that cannot finish a sentence. `clients/claude-code/verify-after.sh` wires it into a Stop hook; the enforcement is tested, the 10-task bench the roadmap measures this by is not run and the roadmap says so
- **v2.5.6** (2026-09-17) — an Explore page in the admin console that answers what four identical-looking chat failures actually are: not in the code, named differently, private, or never indexed. Symbols by fragment, files by path, repository counts — including the file showing **0 symbols**, which is the difference between "the agent cannot find it" and "it is not in the index". Its queries are unfiltered by design, so they live in `store/explore.py` apart from the sixteen access-scoped ones, with a test asserting no MCP tool module imports them. Fixed live: the route cleared `row_factory`, so it answered 200 with an error beside empty lists and the console drew "Nothing is indexed yet" while the index held seventy symbols
- **v2.5.5** (2026-09-17) — **index on push, not only on a timer.** `POST /hook/gitlab` takes a GitLab push event and indexes the repository that changed, gated by its own `ARGUS_WEBHOOK_TOKEN` (unset = the route does not exist). A push during a pass is queued rather than dropped, drained one repository per pass, and an overfull queue collapses into one full pass; events it has no use for are acknowledged rather than refused, because GitLab disables a webhook that keeps failing. The poll stays on as the floor. Verified live: 401 without the secret, 202 with it, and queued/already_queued for a second and third delivery
- **v2.5.4** (2026-09-17) — the roadmap called pack update for archive sources broken by design; it was wrong twice, and checking turned up three real gaps. **`pack update` had no producer** — nothing could make the index JSON it reads, a format with a mandatory checksum and a consumer-facing URL that existed only in the parsing code, so `argus pack index` now writes it. The update command itself had no test, only its parts did. And an archive refetch kept pages the new release deleted, because extraction wrote into the existing tree without clearing it, so the pack reported the new version while serving documentation that no longer exists
- **v2.5.3** (2026-09-17) — **every MCP tool is contract-tested against live Argus.** `verify_mcp.py` asserted `len(tools) == 5` against a server with sixteen, so it could only ever fail and nobody ran it; it is replaced by `verify_tools.py`, which takes the tool list **from the server** so there is no count to keep in sync, calls every tool over StreamableHTTP with real GitLab tokens, checks each result's declared shape, and asserts no structured field names a repository the caller cannot read. Refusals the server makes on purpose are classified rather than reported as breakage, and tools the fixture cannot exercise print as **NOT COVERED** rather than counting as passing: 41/41 with 18 reported skips. Also fixes `ensure_mirror` not retargeting a mirror whose GitLab URL changed (a host migration would have failed every repository at once, with an error naming an address no longer in the configuration), a vacuity guard that printed its failure text on success, and the Dockerfile COPY whitelist that has now broken `docker build` four times
- **v2.5.2** (2026-09-17) — the bundled test GitLab is a fixture again. It was `restart: unless-stopped`, so it survived reboots and sat at 2.63 GiB and 2.17% CPU indefinitely while serving nothing; it is `restart: "no"` now, with `scripts/test-gitlab/run.sh` as its lifecycle — up, wait, seed, verify, teardown, **including on failure**, because the run that leaves it up is precisely the one nobody comes back to. Three rotted things found on the way: `run.sh` waited on a readiness endpoint GitLab 404s through Docker NAT, `verify.py` wrote its report to a path the docs restructure left behind, and its work dir had to move off the checkout for SQLite WAL to work at all
- **v2.5.1** (2026-09-17) — **embeddings on the GPU.** The embedder was CPU-only and capped at two cores, which made it the entire cost of an indexed lookup. Measured warm: **94 ms -> 5 ms median per embed, 18x**, for 849 MB of VRAM; bulk passes move by the same factor (70 symbols: 6.58 s -> 0.43 s), and engine throughput is unchanged (19.3 vs 19.8 tok/s, inside the run-to-run spread). `acceptance.py` now asserts `ollama ps` reports GPU, because Ollama falls back to the CPU silently and nothing else in the stack would notice
- **v2.5.0** (2026-09-17) — **the index measures itself and reindexes itself.** Argus exports Prometheus metrics for index freshness, an `ArgusIndexStale` alert watches them, the serve process runs the periodic reindex that was documented but never wired up, and the admin console's Overview shows the same numbers. Also fixes a repository with no branches silently disappearing from a run, an admin-panel test gate that sat mid-file so half the checks could not fail, and `package.sh` shipping config files the containers cannot read when the builder has a tight umask
- **v2.4.0** (2026-09-16) — the admin panel is a real console: a sidebar with Overview, People, Model, Indexing, Monitoring and Settings; search and pagination over people; per-person pages; **delete an account** (which did not exist); CSV export with formula-injection escaping; service health; a theme toggle. Plus two guards it was missing — it would delete the account you are signed in as, which it did to the live administrator during testing, and it would delete the last admin
- **v2.3.0** (2026-09-16) — **thinking level is now a per-chat choice**, from the Open WebUI model picker: the stack creates a `Deep think` / `Balanced` / `Quick` / `No thinking` model per level, verified end to end at 2654 / 1251 / 0 characters of reasoning. The `Reasoning Effort` field Open WebUI already has does nothing on this stack — LiteLLM drops it for a custom `openai/` api_base — which the docs had been quietly wrong about
- **v2.2.0** (2026-09-16) — `clients/`: a copy-pasteable sample config per agent harness, each marked with whether it was actually executed. **Qwen Code was tested for the first time** (qwen 0.23.3, against the stack's own gateway), DeepSeek Harness re-verified after the restructure, and the four gotchas that only appear when a real client drives it are now written down
- **v2.1.2** (2026-09-16) — "failed to connect to Argus" in Open WebUI now says why. Open WebUI renders any 401 as a connection failure, and the sentence explaining the refusal went into the response body and nowhere else, so the logs said only `reason=token_rejected`. `denied` events now carry `detail`, which is what makes the usual cause — the person has no GitLab account — visible from `docker compose logs argus`
- **v2.1.1** (2026-09-16) — airgapped deployment verified by booting a bundle on a clean project, which found four ways it could fail on a host that cannot fix it: the locally-built images were tied to `COMPOSE_PROJECT_NAME` (rename the project and `up` dies on `No such image`), nothing checked the bundle covered the profiles about to run, the image archives shipped mode 0600 so extracting as root then running as yourself gave permission denied, and the documented split-join silently wrote a 0-byte file on a read-only mount. All four fixed, plus a profile-coverage check in `load.sh --check`
- **v2.1.0** (2026-09-16) — a repository you can navigate and packaging scripts that check their own output. `argus/` → `src/argus/`, `llm-stack/` → `stack/`, one `docs/` tree, repository tooling in `scripts/`, and the older standalone Caddy deployment retired. `release.sh` gained an offline mode; the drop-in archive had been shipping neither the admin panel nor the CPU exporter and refusing to build at all on a machine where the stack had run; the airgap bundler never read its own archive back, and now does
- **v2.0.0** (2026-09-16) — indexing you can watch and logs you can search: Loki on by default, an Indexing dashboard, and five dead or silent tools fixed. `impact_of` failed for every permitted caller, `semantic_search` for everyone, the per-repo progress table had never worked, an unreachable GitLab exited on a raw traceback, and the panel discarded a failed run's log — leaving only an exit code. Plus GitLab username/password auth, a redesigned admin panel, and every path as an `.env` variable
- **v1.14.0** (2026-09-15) — offline bundles, a settable thinking level, and four silent deploy failures; RTX 5090 / NVFP4 samples, a verified backup, and the Argus audit trail
- **v1.13.0** (2026-09-14) — Argus per person in Open WebUI, read-only; 'ask a maintainer' notices; all instructions in README
- **v1.12.1** (2026-09-14) — fresh-clone deploy verified
- **v1.12.0** (2026-09-14) — docker compose up is the whole deployment
- **v1.11.0** (2026-09-13) — the documentation tool accepts the questions it is actually asked
- **v1.10.0** (2026-09-13) — deployable from a clean clone
- **v1.9.0** (2026-09-07) — argus by default, and the 177B MoE question answered
- **v1.8.0** (2026-09-07) — acceptance-tested
- **v1.7.0** (2026-09-07) — the domain is settable, and setup is re-runnable
- **v1.6.0** (2026-09-06) — a clean checkout deploys unattended
- **v1.5.0** (2026-09-04) — tool calls that survive streaming
- **v1.4.0** (2026-09-04) — one number per person, and the key that produced it
- **v1.3.0** (2026-09-04) — monitoring that outlives the engine
- **v1.2.0** (2026-09-04) — long context on a 24 GB card

## 1.1.0

Forty-nine commits since v1.0.0. The theme is retrieval quality: v1.0 could
index and serve, but nobody had measured whether `docs_find` actually answered
description-shaped questions. It did not, and most of the reason was data
rather than ranking.

Note that `pyproject.toml` still read `0.1.0rc1` throughout v1.0 -- the version
was never bumped at that release. It now tracks the tag.

### docs_find answers roughly twice as often

Measured over a 36-question set, top-10: **25% -> 44%** unscoped, and **58%**
when the caller names the source.

* **`docs_find` now serves the hybrid arm.** `search_symbols_hybrid` was
  implemented, documented, and had no callers -- the tool ran the purely
  lexical arm. Term overlap between a question and its answer's description is
  35%, so a term scorer had a low ceiling however it weighted; twelve of
  twenty-five answers shared one word with the question or none.
* **Every pack now has zero blank descriptions.** `docs_find` searches that
  field and skips rows where it is empty, so a blank description made a symbol
  invisible while it still occupied disk. cpp went from 100% blank, python
  from 50%.
* **Descriptions come from the page, not the page's title.** cpp two-word
  descriptions 56% -> 2.2%, python 62% -> 16.3%. `_countof`'s entire
  searchable text had been "_countof Macro".
* **A chunk now says which of a page's symbols it documents.** A 369-symbol
  page returned an arbitrary 8 of them, ordered by rowid.
* **The tool description names the installed sources**, and a `lang` naming no
  installed pack widens instead of returning nothing. Measured through Hermes:
  the model passes `lang` on 5 of 8 calls, including `scripting` for a
  PowerShell question -- knowable only from that list.

### GitLab authentication

* **`gitlab.auth: password`** for username/password sign-in, alongside the
  existing access token. The two are not interchangeable: an access token goes
  in `PRIVATE-TOKEN`, an OAuth token in `Authorization: Bearer`.
  `argus/credentials.py` owns that distinction and every API caller asks it
  for headers.
* The password is read from `ARGUS_GITLAB_PASSWORD` only; a `password` key in
  the config file is **refused**, not ignored.
* **Verified against GitLab 19.2.1: recent GitLab has removed the password
  grant**, and no headless username/password path replaces it. The error says
  so and names the fix rather than reading like a bad password. Use
  `auth: token` unless your GitLab predates the removal.

### Indexing at estate scale

First run against 47 repositories, 55,603 files, **1,491,167 symbols**, 37.8
minutes, zero failures or timeouts.

* **`which_repo` ranks on the raw score**, not the display-clamped
  `confidence`. Every score above 1.0 compared equal and ties broke
  alphabetically -- lz4 beat zstd for "compress a byte stream with a
  dictionary" because `l` sorts before `z`.
* **Known and unfixed:** `which_repo`'s lexical evidence matches query words
  against identifiers, and at 1.5M symbols "store", "key" and "memory" are
  identifiers nearly everywhere. Asked to "store key-value pairs in memory
  with expiry", redis did not place. Routing is 5/10 on the estate set.

### Packs

Rebuilt against current upstream: cpp, python (3.14), wdk, win32, scripting.
The python pack records the branch it was actually built from -- it claimed
`main` while built from `3.14`.

**Trap worth knowing:** incremental rebuild keys on document content, so an
adapter change does *not* propagate to unchanged documents. Delete the
destination pack to force a full build after changing an adapter; the build
reports a healthy symbol count either way.

## 1.0.0

Initial release. 11 packs, ACL enforced structurally and audited, container
healthy, 741 tests.

## 0.1.0-rc1

First tagged release: a private GitLab code index with per-developer access
control, nine MCP tools, a cross-repo dependency graph, and portable public
documentation packs. Shipped Phases 1, 2, 3 and 5; 539 tests, green in the
container.

Measured on real code -- four public C projects, because production GitLab was
not reachable -- 1,199 files, 33,102 symbols, 0 errors, a 14.8 s cold pass, 1.2%
ambiguous includes, and `which_repo` 8/10 top-1 at a 0.5 ms median. Full numbers
and misses in `docs/index-measurements.md`.

**Not yet validated at production scale.** Pilot with a limited repo set and a
handful of developers; `docs/roadmap.md` Step 0 says what the pilot is meant to
produce.
