# Argus

**A private, self-hosted code index that lets a local LLM answer questions across all your repositories at once — without any code leaving your network.**

Argus mirrors every repository from your self-hosted GitLab, extracts a symbol and dependency graph from them, and serves access-controlled code retrieval to [Hermes Agent](https://github.com/NousResearch/hermes-agent) over MCP. Each developer sees exactly the repos GitLab says they can see — no second permissions system to maintain.

Built for a specific, awkward situation: **millions of lines of C/C++ spread across many repositories that together build one product**, where the question developers actually need answered is *"which repo do I change for this, and what breaks if I do?"*

---

## Why not just use RAG?

The obvious approach — chunk every file, embed everything, throw it in a vector database — performs badly on large C/C++ codebases, and it fails in a way that's easy to miss until you've already built it.

Header files are enormous, repetitive, and semantically near-identical to one another. Embedding them floods the vector space with near-duplicates that crowd out real answers. Meanwhile the questions developers actually ask — *where is `Parse` defined*, *who calls this*, *what breaks if I change this struct* — are **exact lookups**, and embeddings answer those worse than a symbol table does. Ask a pure-RAG system about `Init()` in a codebase with forty of them and it will confidently return the wrong one.

Argus inverts the usual priority:

| Layer | Answers | Cost |
|---|---|---|
| **Symbol graph** (ctags + `#include` edges) | Exact definitions, references, cross-repo ownership | Cheap, updates in seconds |
| **Lexical** (SQLite FTS5 + ripgrep) | Exact strings and regex over millions of lines | Cheap, instant |
| **Semantic** (embeddings) | Vague conceptual queries only | Expensive — applied *selectively* |

Embeddings are the garnish, not the meal. They're computed for **public symbol signatures, file summaries, and docs** — never function bodies. A C++ function body embeds mostly to "generic C++ control flow"; its *signature plus doc comment plus path* is what carries the intent someone is searching for. That's ~70–90k vectors instead of ~600k, and a full rebuild in under an hour instead of overnight.

And in C/C++, the `#include` graph **is** the cross-repo dependency graph. Recovering it needs no build system, no `compile_commands.json`, and no compiler — just parsing. That's what lets Argus answer "which repo owns this?" across an entire product.

---

## Architecture

```mermaid
flowchart LR
    subgraph GL["Self-hosted GitLab"]
        R1[(repo A)]
        R2[(repo B)]
        R3[(repo C)]
    end

    subgraph HOST["Index host — one Linux box"]
        MIR["mirror<br/><i>bare clones + worktrees</i>"]
        PAR["parse<br/><i>ctags · includes</i>"]
        STO[("store<br/><i>SQLite · FTS5</i>")]
        ACL["acl<br/><i>PAT → repo allowlist</i>"]
        MCP["mcpsrv<br/><i>HTTP MCP</i>"]
        OLL["Ollama<br/><i>qwen3.6:35b</i>"]
    end

    subgraph DEV["Developer workstation"]
        HER["Hermes Agent"]
        WT["local checkout"]
    end

    GL -->|service token<br/>reads every repo| MIR
    MIR --> PAR --> STO
    STO --> MCP
    ACL --> MCP
    MCP -->|TLS, per-dev token| HER
    OLL -->|inference| HER
    HER -->|reads & edits| WT
    HER -.->|developer PAT| ACL
    ACL -.->|membership check| GL
```

**Two tokens, and their separation is the entire security model.** A privileged *service token* mirrors every repository, so the index is deliberately complete. Each developer's *own* GitLab token is exchanged at query time for their project membership, and every query is filtered to that allowlist **in SQL, before results leave the process** — never by asking the model nicely.

That distinction matters more than it looks. Telling an LLM "only answer about repos X and Y" is not access control; it's a suggestion, and a comment inside indexed source can override it. Revoke someone in GitLab and their index access dies with the cache TTL.

The enforcement is structural, not conventional:

```python
# Every public function in store/queries.py — no exceptions.
def find_symbol(allowed_repo_ids, conn, name, kind=None, limit=50): ...
#              ^^^^^^^^^^^^^^^^^ first positional, no default
```

A reflection test walks the module and fails on any function that doesn't take it first with no default. It fails on code that doesn't exist yet — which is the point. Security bugs of this class almost never come from someone deliberately bypassing a check; they come from a new code path six months later that simply never called it. Encoding the requirement in the signature turns a runtime vulnerability into an import-time error.

---

## What Hermes sees

Tools are named after the *questions developers ask*, not after retrieval mechanisms. A 35B local model picks the right tool far more reliably when the name matches the intent — tool design is prompt engineering for smaller models.

**Shipped — your private code, access-controlled:**

| Tool | Answers |
|---|---|
| `find_symbol` | Exact definitions across every repo |
| `find_references` | Every mention, product-wide, including cross-repo |
| `search_code` | Fast lexical search over millions of lines |
| `get_file` | Access-checked content fetch |
| `index_status` | Per-repo freshness, so the agent can qualify stale answers |
| `which_repo` | *"Which repo do I change for X?"* — from a description, a symbol, a stack trace, or a diff |
| `repo_map` | Which repos a given repo depends on, and which depend on it, from resolved `#include` edges |
| `impact_of` | *"What breaks if I change this file?"* — every file that includes it, transitively, with depth |
| `semantic_search` | *"Where do we handle retry backoff for uploads?"* — by meaning, when the question contains no identifier |

**Shipped — public documentation, no access control (there is nothing to gate):**

| Tool | Answers |
|---|---|
| `docs_lookup` | Exact API name → the page and anchor that *define* it |
| `docs_search` | Conceptual questions against Python and React docs |

Every phase is now shipped. `semantic_search` embeds the signature, kind, scope and path of each **public** symbol — never function bodies, which embed to generic control flow and bury real answers under near-duplicates. That is ~70–90k vectors where bodies would be ~600k. Build them with `argus embed --config …`; it is incremental, so a rerun after indexing new code only does the new work, and an interrupted run resumes.

Its ACL filter runs *after* the vector scan, because `vec0` KNN cannot join. Nothing from a repo you cannot see is ever returned — not a row, a score, or an id — so the result is indistinguishable from a corpus containing only your repos. The tradeoff is recall, not correctness: a caller whose allowlist is a small slice of the corpus can get fewer hits than exist for them, and `SEMANTIC_COARSE` is the dial.

`index_status` looks like a throwaway and isn't: it's what stops an agent confidently answering from a three-week-stale index. It can say *"this repo was last indexed 4 hours ago"* instead of silently guessing.

---

## Knowledge packs

Your developers don't only ask about your code. They ask what `useState` returns and how `os.path.join` treats an absolute segment — and answering that from a model's memory is how you get confident, outdated, unciteable answers.

A **knowledge pack** is one SQLite file holding a public documentation corpus: prose, API symbols, and embeddings. Build it once, share it, and nobody else has to regenerate it.

```bash
argus pack install https://example.org/python-3.13.arguspack --sha256 <digest>
argus pack list
argus pack info python          # licence and attribution, in full
```

Eleven are built and measured, every one reporting **0 unresolved symbols**:

| | Documents | Chunks | Symbols | Size |
|---|---|---|---|---|
| `win32` — Windows SDK + samples | 71,663 | 530,559 | 87,297 | 786.2 MB |
| `wdk` — driver DDI + samples | 28,176 | 245,727 | 38,041 | 358.6 MB |
| `cpp` — MSVC, CRT, STL | 9,746 | 123,212 | 37,305 | 174.7 MB |
| `cppreference` — C++ standard library | 6,640 | 68,891 | 5,406 | 124.9 MB |
| `scripting` — PowerShell, cmd, Unix | 9,302 | 46,027 | 9,302 | 70.2 MB |
| `python` — 3.13 | 516 | 13,164 | 18,027 | 28.5 MB |
| `debugger` — WinDbg + how-to | 2,138 | 14,259 | 1,511 | 24.8 MB |
| `sqlite` — SQL, pragmas, FTS5 | 837 | 8,987 | 36 | 18.3 MB |
| `react` — react.dev | 222 | 4,755 | 125 | 9.1 MB |
| `algorithms` — TheAlgorithms/C++ | 371 | 2,001 | 370 | 4.3 MB |
| `system-design` — the Primer | 9 | 442 | 8 | 1.3 MB |

Zero unresolved symbols is the check worth watching: a symbol whose page is missing still installs, still lists, and simply never resolves. The failure is invisible until somebody looks something up.

**All eleven answer correctly through the real agent.** One question per pack, driven through the actual `hermes -z` CLI — MCP discovery, tool registration, the server's instructions, native function calling, the model — and graded by substring match against a ground-truth token taken from the pack itself, so the verdict does not depend on reading the prose:

| | | | |
|---|---|---|---|
| `win32` → `advapi32.lib` | `wdk` → `dispatch_level` | `cpp` → `/std:c++20` | `cppreference` → `amortized` |
| `debugger` → `.reload` | `scripting` → `/mir` | `python` → `discard` | `react` → `pair` |
| `sqlite` → `vacuum` | `algorithms` → `sort` | `system-design` → `content delivery` | **11 / 11** |

Latency is the model, not the index: 65 s to 886 s per question on CPU-only inference with 46 tools in the prompt, while retrieval itself is milliseconds. The same `win32` question took 900 s cold and **78.8 s** warm — an 11× swing that is model load, not search. Reproduce with [`evals/run_hermes_packs.py`](evals/run_hermes_packs.py).

Three properties make them worth the format:

**Lookups are exact, not approximate.** Symbols come from the upstream project's own index — Sphinx's `objects.inv` for Python, react.dev's pinned MDX anchors — so `docs_lookup("os.path.join")` resolves to the definition, not to whichever paragraph mentions the name.

**Every result is attributable.** `source`, `url`, `license` and `attribution` ride along on every hit, and the tool descriptions tell the model to cite them. `argus pack info` prints the licence in full — that output is how you meet the redistribution obligation.

**They're small enough to move.** Embeddings are stored binary-quantized (96 bytes/chunk) for a coarse pass, with int8 (768 bytes) for rescoring. float32 would be 3072. At a million chunks that's the difference between a 96 MB scan and a 3 GB one.

Measured recall@10 against an exact float32 baseline: **0.946**. Full numbers, including the misses, in [`docs/pack-measurements.md`](docs/pack-measurements.md). Usage in [`docs/knowledge-packs.md`](docs/knowledge-packs.md).

Packs are a *separate corpus*. Nothing in the pack query path can reach the private index — that's asserted structurally by a test, not left to convention.

---

## Status

Argus ships in phases, each ending somewhere genuinely usable.

| Phase | Scope | State |
|---|---|---|
| **1 — Indexer** | Mirroring, change detection, symbol + include extraction, SQLite store, access-gated queries, CLI | ✅ **Complete** |
| **2 — Multi-user retrieval** | ACL module, HTTP MCP server, 5 code tools, container, TLS | ✅ **Complete** |
| **5 — Knowledge packs** | Portable public documentation packs, 2 doc tools, `argus pack` CLI | ✅ **Complete** |
| **3 — Cross-repo intelligence** | Include resolution, `repo_map`, `which_repo` | ✅ **Complete** |
| **4 — Semantic layer** | Selective embeddings over private code, `semantic_search` | ✅ **Complete** |

**741 tests**, passing locally, 0 skipped.

Health indicators, how they are measured, and the charts behind them are in [`docs/kpis.md`](docs/kpis.md) — every figure measured, none estimated.

Phase 2 delivered most of the value — exact symbol lookup across the whole product, with real GitLab-derived permissions, before a single embedding existed. Phase 5 then added a second, entirely separate corpus: public documentation, which needs no access control and can be shared as a file.

Phase 4 is deliberately last. It is the most expensive to build, the most likely to need iteration, and phases 1–3 stand on their own without it.

---

## Getting started

### Requirements

- Python 3.11+
- git
- [Universal Ctags](https://github.com/universal-ctags/ctags) — **not** Exuberant Ctags, which has no JSON output
  - Linux: `sudo apt install universal-ctags`
  - Windows: `winget install UniversalCtags.Ctags`
- A GitLab personal access token with `read_api` and `read_repository`
- [Ollama](https://ollama.com) with `nomic-embed-text` pulled — only for `docs_search` and for *building* packs. Everything else, including `docs_lookup`, works without it.

Argus refuses to start if ctags is missing or is the wrong implementation. Without that check you'd get a complete-looking index with no symbol layer at all — and no error to tell you.

Expect the embedder, not the index, to dominate `docs_search` latency: measured 2,254 ms to embed a query on CPU Ollama against 89 ms for the search itself.

### Install

Docker is the recommended path, because it pins ctags — see below for why that matters.

```bash
docker compose build
```

Or natively:

```bash
pip install -e ".[dev]"
```

### Configure

```bash
cp config.example.yaml config.yaml
```

Set the token in the environment — it's read in preference to the file, so it never has to live in YAML:

```bash
export ARGUS_GITLAB_TOKEN=glpat-xxxxxxxxxxxx
```

```yaml
gitlab:
  url: https://gitlab.internal

index:
  data_dir: /var/lib/argus
  db_path: /var/lib/argus/index.db
  max_file_bytes: 1048576
  repo_time_budget_seconds: 600
  exclude_dirs: [third_party, vendor, node_modules, build, out, x64, Debug, Release]

# Optional. Defaults to <data_dir>/packs.
packs:
  dir: /var/lib/argus/packs
```

### Run

```bash
argus index --config config.yaml
```

Index a single repository:

```bash
argus index --config config.yaml --repo group/one-repo
```

Check per-repo freshness:

```bash
argus status --config config.yaml
```

The first index takes hours on a large estate. After that, runs are incremental: Argus diffs the last-indexed commit against the new head and touches only what changed.

### Running in Docker

```bash
export ARGUS_GITLAB_TOKEN=glpat-xxxxxxxxxxxx
```

```bash
docker compose run --rm indexer index --config /etc/argus/config.yaml
```

```bash
docker compose run --rm indexer status --config /etc/argus/config.yaml
```

The indexer is a batch job, not a daemon, so nothing starts on its own — `docker compose up` would be the wrong gesture. Mirrors, worktrees and the index live in the `argus-data` volume; your `config.yaml` is mounted read-only. Set `index.data_dir` and `index.db_path` to `/var/lib/argus` in that file.

**Why the image pins ctags.** Argus depends on universal-ctags behaviour that varies by version: the C/C++ `prototype` kind ships *disabled by default* (without `--kinds-c=+p` the index silently loses most of a C/C++ public API), and C++ anonymous namespaces surface as generated identifiers like `__anond398a7c10111` rather than the literal `"anonymous"`. A host with a different ctags changes what gets indexed and what counts as a public symbol, with no error.

The Dockerfile's `test` stage runs the **entire test suite against the pinned toolchain during the build**, so a ctags that behaves differently fails the build instead of producing an image that indexes incorrectly and reports success. The image currently pins Universal Ctags 5.9.0 (Debian bookworm) and all 127 tests pass against it.

---

## Design notes

A few decisions that aren't obvious from the code:

**Storage is one SQLite file.** At 2–5 developers there's no justification for operating Qdrant or Postgres. One file gives atomic writes, trivial backup, and zero daemon management. Regex search shells out to ripgrep against the worktrees rather than maintaining a trigram index — faster than anything worth building, and one less subsystem to keep consistent.

**`last_indexed_sha` advances only after a repo's entire changed set commits.** That single rule is the whole crash-recovery story: a run that dies partway replays the same diff next time, and every write is idempotent. It's also why a ctags failure blocks the advance — marking files "indexed" with zero symbols would lose them permanently, since they'd never appear in a future diff.

**Tool error text is prompt text.** When something breaks, the string returned goes straight into an LLM's context and determines what it does next. `"Argus unavailable — fall back to ripgrep in the local checkout and say the answer is repo-local only"` produces a far better outcome than a 503. You're programming the fallback path in English.

**Renames are delete + add.** Rename tracking buys nothing for an index that stores per-path rows.

**Every path-producing git command passes `-z`.** Without it git applies `core.quotePath` and returns non-ASCII paths C-escaped and quoted, so `файл.c` gets indexed as the literal `"\321\204\320\260..."`. This cost a real bug before it was caught.

---

## Development

```bash
pytest -v
```

The suite uses **no network and no mocks of the tools under test** — it builds real git repositories in temp directories, runs the real ctags binary, and exercises real SQLite. Tests that mock the thing they're testing prove nothing.

The design spec and the full implementation plan live in [`docs/superpowers/`](docs/superpowers/), including the reasoning behind each architectural choice and the mid-flight corrections where implementation proved the plan wrong.

---

## License

See [LICENSE](LICENSE).

## Measured

Every number here was measured on one machine (Windows 11, CPU-only Ollama,
`nomic-embed-text`), not estimated. Where a figure is inconclusive it says so.

### Does retrieval improve an agent?

160 questions against **qwen3.6:35b**, closed book versus with pack retrieval.
Question *and* answer are extracted from the pack pages, so ground truth is
what Microsoft and tldr publish rather than what the test author remembered.

| pack | closed book | with packs | |
|---|---|---|---|
| `win32` | 9 / 30 | **30 / 30** | +21 |
| `wdk` | 9 / 30 | **25 / 30** | +16 |
| `scripting` | 2 / 30 | **21 / 30** | +19 |
| `cpp` (MSVC diagnostics) | 3 / 40 | **40 / 40** | +37 |
| `cpp` (standard library) | 26 / 30 | 25 / 30 | -1 |
| **total** | **49 / 160 (31%)** | **141 / 160 (88%)** | **+92** |

**94 answers fixed, 3 broken.** The `cpp` standard-library row is the control
and behaves like one: the model already knows which header declares
`std::vector::push_back`, so retrieval adds nothing there. The packs earn
their disk where the model is ignorant, not where it is fluent.

**Retrieval must be wired correctly or the same packs make answers worse.**
Eight strategies were measured. Every one that constrained the model to the
reference destroyed answers it already had: "use ONLY the reference" took
win32 from 5/5 to 1/5, and "do not rely on memory" took cpp from 26/30 to
13/30. Reference material must add, never gate.

| strategy | score |
|---|---|
| closed book | 46 / 120 |
| verify-after only | 60 / 120 |
| hybrid, routed on model confidence | 78 / 120 |
| extract-only framing | 84-95 / 120 |
| **retrieve, answer, verify** | **101 / 120** |

Use all five documentation tools, each for a different failure: `docs_lookup`
when you know the name, `docs_find` when you know only the behaviour,
`docs_search` to locate a page, `docs_get` to read it whole, `docs_verify` to
check a draft afterwards. And never route on the model's confidence -- it is
uncorrelated with its knowledge, which is the whole reason the packs exist.

### What the packs cost

| pack | documents | chunks | symbols | size | build |
|---|---|---|---|---|---|
| `system-design` | 9 | 442 | 8 | 1.3 MB | < 1 min |
| `algorithms` | 371 | 2,001 | 370 | 4.3 MB | < 1 min |
| `scripting` | 9,302 | 46,027 | 9,302 | 70.1 MB | 13 min |
| `cpp` | 9,746 | 123,212 | 37,305 | 174.7 MB | 36 min |
| `wdk` | 28,176 | 245,727 | 38,041 | 358.6 MB | 74 min |
| `win32` | 71,663 | 530,559 | 87,297 | 786.2 MB | 162 min |
| **total** | **128,567** | **947,968** | **172,323** | **1.4 GB** | **~5 h** |

Every pack reports **0 unresolved symbols**. Build time is almost entirely
embedding, at a steady ~55 chunks/sec, so `minutes = chunks / 55 / 60`. An
interrupted build resumes from a cache rather than starting over -- the wdk
rebuild took 3 minutes instead of 74, reusing 239,155 embeddings.

### Retrieval performance

| corpus | search, excluding query embedding |
|---|---|
| 17,919 chunks | 88.6 ms |
| 364,800 chunks | **460 ms** |

5.2x cost for 20.4x the corpus -- sublinear. Query embedding dominates what a
user feels at ~2,500 ms on CPU; that is hardware, not code.

### Index performance

| | 1,026 files | 10,212 files |
|---|---|---|
| `which_repo` p95 | 1.58 ms | **1.92 ms** |
| ambiguous include rate | 1.28% | **0.09%** |
| MB per 1k files | 28.4 | 21.9 |

Suffix matching gets *better* with scale, not worse. `which_repo` stayed flat
only because an indexed `basename` column replaced a full scan; before that,
the p95 was 15.5 ms and rising linearly.

### Getting the tools in front of the model

A server the client never asks is worth exactly zero, and that failure is
quiet. Wiring Argus into Hermes, the tools registered correctly and the model
still answered "I was unable to locate any documentation" -- because the client
waits a bounded time for MCP discovery, then snapshots its tool list once.

| phase | time |
|---|---|
| Argus answering `initialize` + `tools/list` + `resources/list` + `prompts/list` | **27 ms** |
| client building its HTTP client and importing the MCP SDK | ~1,040 ms |
| total discovery, warm / cold | 1.07 s / **2.89 s** |
| client's wait before snapshotting | **0.75 s** |

Argus is 27 ms of a budget it loses by 320 ms. No server-side tuning wins
that; an infinitely fast server still loses. The lesson generalises past this
one client: **measure the handshake from the client's side**, because the
server's own latency can be a rounding error in what decides whether it gets
used at all.

Worth stating plainly: a client that reports a server as `configured` has told
you it parsed the config, not that the model can call anything.
[docs/deployment.md](docs/deployment.md) has the diagnosis, a one-liner that
checks whether the tools reached the snapshot, and the fix.

### Engineering

| | |
|---|---|
| tests | **741 passing** |
| hollow tests found by targeted revert | **9** |
| bugs whose failure mode was a plausible success | **3** (see below) |
| cross-repo edge precision, hand-checked | 13 / 25 -> after fixes, 0 fabricated at weight > 8 |

A *hollow test* passes while the behaviour it names is broken. Each was caught
by breaking the code deliberately and confirming the test noticed. A suite
without that step has the same hollow tests and no number.

The three worst bugs found here shared one signature: **they produced a
plausible success rather than an error.** A client logged "registered 19
tools" and the agent never saw them. A clone succeeded and the build blamed
the path it had just written. A YAML parser returned a dict and silently
omitted a key — which would have shipped a pack with zero symbols that built,
installed and listed without complaint. None is caught by "does it crash" or
"does it return something"; each needs a check on the *content* of the
success.

Full detail: [docs/pack-measurements.md](docs/pack-measurements.md),
[docs/index-measurements.md](docs/index-measurements.md),
[docs/kpis.md](docs/kpis.md), [evals/](evals/).
