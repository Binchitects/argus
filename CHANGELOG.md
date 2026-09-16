# Changelog

Each release's full notes are in its annotated tag: `git tag -n99 <version>`.

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
