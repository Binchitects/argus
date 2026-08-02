# Phase 5 — Knowledge Packs (Python + React) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build, distribute and query portable knowledge packs of public SDK/API documentation, starting with Python and React.

**Architecture:** An `argus/packs/` package builds packs from public doc git repos via per-source adapters, chunking prose at headings and embedding with the globally pinned model. Each pack is one read-only SQLite file with binary-quantized vectors plus int8 for rescoring. `argus/store/packs.py` queries them — a module with **no allowlist parameter, because it has nothing to filter** — and never touches the private index. Two MCP tools expose it.

**Tech Stack:** Python 3.11+, SQLite (FTS5 contentless, `sqlite-vec` bit + int8), zstd, git, Ollama (`nomic-embed-text`), httpx.

Spec: [`../specs/2026-08-02-knowledge-packs-design.md`](../specs/2026-08-02-knowledge-packs-design.md)

## Global Constraints

Every task's requirements implicitly include this section.

- **Python `>=3.11`.** Deployment host is Linux, development Windows. No 3.12+ syntax, no hardcoded path separators.
- **Packs never touch the private index.** `argus/store/packs.py` must not import `argus/store/queries.py`, and must never open `index.db`. Enforced by a reflection test, not by convention.
- **`allowed_repo_ids` remains first positional, no default, on every public function in `argus/store/queries.py`.** This phase does not modify that file.
- **Never edit an applied migration** (`001`–`007`). Pack schema is a *separate* database and has its own versioning in `pack_meta`.
- **Embedding model is pinned:** `nomic-embed-text`, 768 dimensions, normalized. Recorded in `pack_meta` and **enforced at query time** — a mismatch refuses semantic results and says why, while lexical and symbol lookup keep working.
- **A pack with no recorded license is refused at build time.**
- No network in tests. Doc repos are fixtures; Ollama is mocked.
- Conventional commit prefixes.
- **Baseline is 230 passed, 0 skipped, 0 warnings. None may regress.**
- The Docker `test` stage must keep passing.

### Testing constraints — read these, they are the accumulated lessons

- **Every regression test must be demonstrated to fail** with its production change reverted: capture the failing output, restore, capture the pass. A revert that deletes an entire function proves only that the test *calls* it; a **targeted** revert proves the assertion discriminates.
- **Ten tests on this project passed with their bug fully reintroduced** — twice inside verification scripts written to catch exactly that. Before accepting any assertion, ask what *else* could satisfy it.
- **Never assert isolation or disjointness without first asserting the result is non-empty.** Two empty sets are trivially disjoint. This has bitten this project twice.
- Never assert on aggregate counters that unrelated fixtures can satisfy.

## File Structure

| File | Responsibility |
|---|---|
| `argus/packs/format.py` | Pack schema, open/create, `pack_meta` read/write |
| `argus/packs/chunk.py` | Heading-aware chunking with heading-trail composition |
| `argus/packs/quantize.py` | float32 → bit and → int8; rescoring |
| `argus/packs/build.py` | Build orchestration: fetch → parse → chunk → embed → emit |
| `argus/packs/sources/base.py` | Adapter protocol |
| `argus/packs/sources/python_docs.py` | cpython `Doc/` reST + `objects.inv` |
| `argus/packs/sources/react_docs.py` | `react.dev` MDX + front-matter |
| `argus/packs/registry.py` | Installed-pack registry, install/verify/remove |
| `argus/store/packs.py` | Query API over installed packs (no allowlist) |
| `argus/embed.py` | Ollama embedding client, batched (shared with Phase 4) |
| `argus/mcpsrv/tools.py` | Modify — `docs_lookup`, `docs_search` |
| `argus/cli.py` | Modify — `argus pack build/list/install/info/remove/update` |

---

### Task 1: Pack format and `pack_meta`

**Files:** Create `argus/packs/__init__.py`, `argus/packs/format.py`; Test `tests/packs/test_format.py`

**Interfaces:**
- `PACK_SCHEMA_VERSION = 1`
- `create_pack(path) -> sqlite3.Connection` — applies the schema from the spec
- `open_pack(path) -> sqlite3.Connection` — `file:...?mode=ro&immutable=1`, `row_factory=Row`
- `write_meta(conn, **kv)` / `read_meta(conn) -> dict[str, str]`
- `PackMismatch(Exception)` — raised when a pack cannot be served
- `require_compatible(meta, *, model, dim)` — raises `PackMismatch` on model/dim/schema mismatch

**Why `immutable=1`:** a pack is frozen by definition, so telling SQLite so removes locking and change-counter checks. It is a correctness statement as much as a speed one — a pack that changes under a reader is a bug.

- [ ] **Step 1: Write the failing tests**

```python
def test_created_pack_has_every_table(tmp_path):
    conn = format.create_pack(tmp_path / "p.argus-pack")
    names = {r["name"] for r in conn.execute(
        "SELECT name FROM sqlite_master WHERE type IN ('table','view')")}
    assert {"pack_meta", "docs", "chunks", "api_symbols", "docs_fts",
            "vec_bin", "vec_i8"} <= names


def test_opened_pack_rejects_writes(tmp_path):
    p = tmp_path / "p.argus-pack"
    format.create_pack(p).close()
    ro = format.open_pack(p)
    with pytest.raises(sqlite3.OperationalError, match="readonly"):
        ro.execute("INSERT INTO pack_meta (key, value) VALUES ('x','y')")


def test_require_compatible_rejects_a_different_model(tmp_path):
    meta = {"embedding_model": "bge-m3", "embedding_dim": "1024",
            "pack_schema_version": "1"}
    with pytest.raises(format.PackMismatch, match="bge-m3"):
        format.require_compatible(meta, model="nomic-embed-text", dim=768)


def test_require_compatible_rejects_a_dimension_mismatch(tmp_path):
    """Same model name, wrong width -- still unservable, and the message must
    name the dimensions rather than only the model."""
    meta = {"embedding_model": "nomic-embed-text", "embedding_dim": "384",
            "pack_schema_version": "1"}
    with pytest.raises(format.PackMismatch, match="384"):
        format.require_compatible(meta, model="nomic-embed-text", dim=768)


def test_require_compatible_accepts_a_matching_pack():
    format.require_compatible(
        {"embedding_model": "nomic-embed-text", "embedding_dim": "768",
         "pack_schema_version": "1"},
        model="nomic-embed-text", dim=768)
```

- [ ] **Step 2: Run to verify failure.** Expected: `ModuleNotFoundError: argus.packs`.
- [ ] **Step 3: Implement** the schema from the spec verbatim, plus `require_compatible`. Its message must name the offending value — a mismatch a human cannot diagnose from the error is a support ticket.
- [ ] **Step 4: Full suite.** ≥ 235 passed, 0 skipped, 0 warnings.
- [ ] **Step 5: Commit** `feat: add knowledge pack format with enforced model pinning`

---

### Task 2: Heading-aware chunking

**Files:** Create `argus/packs/chunk.py`; Test `tests/packs/test_chunk.py`

**Interfaces:**
- `Chunk` dataclass: `heading_path: str`, `anchor: str | None`, `start_line: int`, `body: str`
- `chunk_markdown(text, *, max_chars=1200) -> list[Chunk]`
- `embed_text(chunk) -> str` — **the string that actually gets embedded**

**This is the highest-leverage task in the phase.** `embed_text` must prepend the heading trail:

```
fetch() > Parameters > options > redirect
A string indicating how to handle a redirect response...
```

Without it a chunk embeds to almost nothing useful. With it the chunk is self-locating. The effect is invisible in output and halves retrieval quality, so it needs a test that fails if someone "simplifies" it away.

- [ ] **Step 1: Write the failing tests**

```python
MD = """\
# fetch()
Intro text.

## Parameters
Some parameters.

### options
A string indicating how to handle a redirect response.

## Return value
A Promise.
"""


def test_heading_trail_is_built_from_nesting():
    chunks = chunk.chunk_markdown(MD)
    trails = [c.heading_path for c in chunks]
    assert "fetch() > Parameters > options" in trails


def test_embedded_text_carries_the_heading_trail():
    """The whole point. A bare section body embeds to nothing useful."""
    c = next(c for c in chunk.chunk_markdown(MD)
             if c.heading_path.endswith("options"))
    text = chunk.embed_text(c)
    assert text.startswith("fetch() > Parameters > options")
    assert "redirect response" in text


def test_a_deeper_heading_does_not_inherit_a_sibling():
    trails = [c.heading_path for c in chunk.chunk_markdown(MD)]
    assert "fetch() > Return value" in trails
    assert not any("options > Return value" in t for t in trails)


def test_oversized_section_is_split_but_keeps_its_trail():
    big = "# Top\n\n## Sec\n\n" + ("word " * 2000)
    parts = [c for c in chunk.chunk_markdown(big, max_chars=500)
             if c.heading_path == "Top > Sec"]
    assert len(parts) > 1
    assert all(chunk.embed_text(p).startswith("Top > Sec") for p in parts)


def test_code_fences_are_not_split_mid_block():
    src = "# T\n\n## S\n\n```python\n" + "x = 1\n" * 400 + "```\n"
    for c in chunk.chunk_markdown(src, max_chars=500):
        assert c.body.count("```") % 2 == 0, "split inside a fenced block"
```

- [ ] **Step 2: Run to verify failure.**
- [ ] **Step 3: Implement.** Maintain a heading stack; a heading at level *n* pops everything at level ≥ *n*. Split oversized sections at paragraph boundaries, never inside a fenced code block — a half-open fence poisons every downstream consumer.
- [ ] **Step 4: Full suite.**
- [ ] **Step 5: Commit** `feat: add heading-aware chunking that carries the heading trail`

---

### Task 3: Quantization and rescoring

**Files:** Create `argus/packs/quantize.py`; Test `tests/packs/test_quantize.py`

**Interfaces:**
- `to_bits(vec: Sequence[float]) -> bytes` — 768 floats → 96 bytes, sign-bit packing
- `to_int8(vec) -> bytes` — scaled to int8
- `rescore(query_vec, candidates: list[tuple[int, bytes]]) -> list[tuple[int, float]]` — cosine against int8, sorted

**The claim this task must substantiate:** binary-coarse plus int8-rescore retains most of float32's recall. That is an empirical claim and the plan does not get to assert it — **measure it.**

- [ ] **Step 1: Write the failing tests**, including the one that matters:

```python
def test_binary_coarse_plus_rescore_retains_recall_against_float_baseline():
    """The design rests on this number. Measure it, do not assume it.

    Deterministic synthetic corpus: 2000 vectors, 50 queries, compare the
    top-10 float32 ground truth against binary-coarse(300) -> int8-rescore.
    """
    rng = random.Random(1234)
    dim = 768
    corpus = [_unit([rng.gauss(0, 1) for _ in range(dim)]) for _ in range(2000)]
    queries = [_unit([rng.gauss(0, 1) for _ in range(dim)]) for _ in range(50)]

    recalls = []
    for q in queries:
        truth = {i for i, _ in _topk_float(q, corpus, 10)}
        coarse = _topk_hamming(q, corpus, 300)
        got = {i for i, _ in quantize.rescore(
            q, [(i, quantize.to_int8(corpus[i])) for i in coarse])[:10]}
        recalls.append(len(truth & got) / 10)

    mean = sum(recalls) / len(recalls)
    assert mean >= 0.85, f"recall@10 {mean:.3f} below the 0.85 the design assumes"
```

Also: `to_bits` produces exactly 96 bytes for 768 dims; round-tripping through int8 preserves ordering for clearly-separated vectors; `rescore` on an empty candidate list returns `[]` rather than raising.

- [ ] **Step 2: Run to verify failure.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Full suite.** **Report the measured recall in the commit message** — it is the number the whole size argument depends on. If it comes in below 0.85, that is a finding: report it rather than lowering the threshold.
- [ ] **Step 5: Commit** `feat: add binary and int8 quantization with measured rescoring recall`

---

### Task 4: Embedding client

**Files:** Create `argus/embed.py`; Test `tests/test_embed.py`

**Interfaces:**
- `EMBED_MODEL = "nomic-embed-text"`, `EMBED_DIM = 768`
- `embed_batch(texts: list[str], *, client=None) -> list[list[float]]` — batched Ollama `/api/embed`, vectors L2-normalized
- `EmbeddingUnavailable(Exception)`

Shared with Phase 4. Normalization is not optional: binary quantization is sign-based and rescoring uses cosine, both of which assume unit vectors.

- [ ] **Step 1–5:** tests (batching, normalization to unit length, a failing Ollama raising `EmbeddingUnavailable` rather than returning partial results, no network via `httpx.MockTransport`); verify failure; implement; full suite; commit.

---

### Task 5: Source adapter protocol + Python adapter

**Files:** Create `argus/packs/sources/base.py`, `argus/packs/sources/python_docs.py`; Test `tests/packs/test_python_source.py`

**Interfaces:**
- `Source` protocol: `name`, `repo_url`, `branch`, `subtree`, `license`, `license_url`, `attribution`, `iter_docs(root) -> Iterator[Doc]`, `iter_symbols(root) -> Iterator[ApiSymbol]`
- `Doc`: `path`, `title`, `url`, `lang`, `body`
- `ApiSymbol`: `name`, `kind`, `namespace`, `doc_path`, `anchor`, `signature`

Python adapter parses reST under `Doc/` and reads **`objects.inv`** — the Sphinx inventory giving exact `symbol → document + anchor`. That is what makes `docs_lookup("os.path.join")` precise rather than approximate.

`objects.inv` is a small binary format: a plaintext header then zlib-compressed lines. Parse it directly; do not add a Sphinx dependency.

- [ ] **Step 1: Write the failing tests** against a checked-in miniature fixture (a handful of reST files plus a real, small `objects.inv`): titles and canonical URLs extracted; `os.path.join` present with the right anchor; a symbol whose anchor is missing is skipped rather than emitted with a broken link; license metadata is present and non-empty.
- [ ] **Steps 2–5:** verify failure; implement; full suite; commit.

---

### Task 6: React adapter

**Files:** Create `argus/packs/sources/react_docs.py`; Test `tests/packs/test_react_source.py`

MDX with YAML front-matter, no inventory feed — so `iter_symbols` derives symbols from headings that name APIs (`useState`, `useEffect`). Deliberately narrower than Python's: **if a heading is not clearly an API name, emit no symbol rather than a guess.** A wrong `docs_lookup` hit is worse than a miss, because the model reports it with confidence.

- [ ] **Step 1: Write the failing tests** — front-matter title wins over the first heading; JSX/import lines are stripped from prose before chunking; `useState` yields a symbol, a prose heading like "Adding interactivity" does not; canonical URL derived from the file path.
- [ ] **Steps 2–5:** verify failure; implement; full suite; commit.

---

### Task 7: Pack builder

**Files:** Create `argus/packs/build.py`; Test `tests/packs/test_build.py`

**Interfaces:** `build_pack(source, *, work_dir, out_path, version, embed_fn) -> Path`

Fetch (reusing `argus/mirror.py`) → parse via adapter → chunk → embed → write. Content zstd-compressed. `pack_meta` records source commit, license, attribution, model, dim, counts, builder version.

**Refuses to build when the source has no license recorded.** Emitting a pack you cannot lawfully share is a builder failure, not a user surprise.

- [ ] **Step 1: Write the failing tests** — a built pack opens, has non-zero doc/chunk counts, and `pack_meta` carries the source commit and license; **a source with `license = ""` raises before any file is written** (assert no output file exists, not merely that it raised); vectors present in both `vec_bin` and `vec_i8` for every chunk; `docs_fts` returns a hit for known text.
- [ ] **Steps 2–5:** verify failure; implement; full suite; commit.

---

### Task 8: Registry — install, verify, remove

**Files:** Create `argus/packs/registry.py`; Test `tests/packs/test_registry.py`

**Interfaces:** `install(url_or_path, *, dest_dir, expected_sha256=None) -> InstalledPack`; `list_installed(dest_dir)`; `remove(name, dest_dir)`; `fetch_index(url, *, client=None)`

**A pack failing its SHA-256 must not be registered** — a truncated download becoming a silently half-empty knowledge base is exactly the "looks like it works" failure this project keeps finding.

- [ ] **Step 1: Write the failing tests** — install verifies and registers; **a corrupted pack is rejected AND leaves nothing registered and no file behind** (assert both); a pack whose `pack_meta` model differs is installable but flagged incompatible; `list_installed` reports name, version, model, size, license.
- [ ] **Steps 2–5:** verify failure; implement; full suite; commit.

---

### Task 9: Query module — `store/packs.py`

**Files:** Create `argus/store/packs.py`; Test `tests/store/test_packs.py`

**Interfaces:**
- `lookup_symbol(packs, name, lang=None, limit=20) -> list[dict]`
- `search_docs(packs, query_vec, lang=None, limit=10, coarse=300) -> list[dict]`
- `search_text(packs, query, lang=None, limit=20) -> list[dict]`

No allowlist parameter — there is nothing to filter. Results carry `source`, `url`, `license` so the model can attribute.

**Cross-pack ranking is free here and the reason is worth recording:** because the embedding model is pinned globally, vectors from different packs occupy the same space, so cosine scores are directly comparable without normalization.

- [ ] **Step 1: Write the failing tests** — the **isolation reflection test first**:

```python
def test_packs_module_cannot_reach_the_private_index():
    """Structural, not conventional. The public path must not be able to read
    the private one even by mistake."""
    src = pathlib.Path(inspect.getfile(packs)).read_text(encoding="utf-8")
    assert "store.queries" not in src and "from .queries" not in src
    assert "index.db" not in src
    assert not any(
        m.__name__.endswith("store.queries")
        for m in vars(packs).values() if inspect.ismodule(m))


def test_search_spans_two_packs_and_ranks_across_them():
    """Non-empty FIRST -- ranking across empty sets proves nothing."""
    rows = packs.search_docs([py_pack, react_pack], qvec, limit=10)
    assert rows, "no results: the ranking assertion below would be vacuous"
    assert {r["source"] for r in rows} == {"python", "react"}


def test_a_model_mismatched_pack_refuses_semantic_but_serves_lexical():
    with pytest.raises(format.PackMismatch):
        packs.search_docs([mismatched], qvec)
    assert packs.lookup_symbol([mismatched], "os.path.join")
```

- [ ] **Steps 2–5:** verify failure; implement; full suite; commit.

---

### Task 10: MCP tools — `docs_lookup`, `docs_search`

**Files:** Modify `argus/mcpsrv/tools.py`; Test `tests/mcpsrv/test_docs_tools.py`

Descriptions are a deliverable, not documentation — they are what a 35B model reads to choose. Each must state that results come from **public documentation with a named source and license**, so the model attributes rather than asserts.

Queries are sync SQLite: they must run inside a **single** `run_in_threadpool` call, like every other tool — this is the invariant Task 6 of Phase 2 established and Task 7 preserved.

- [ ] **Step 1: Write the failing tests** — both tools registered and callable; results carry source URL and license; `docs_search` on a mismatched pack returns an actionable message rather than a traceback; **neither tool can reach the private index** (assert a private-repo symbol is absent from results); the threadpool invariant holds.
- [ ] **Steps 2–5:** verify failure; implement; full suite; commit.

---

### Task 11: CLI — `argus pack …`

**Files:** Modify `argus/cli.py`; Create `docs/knowledge-packs.md`; Test `tests/test_cli.py`

`argus pack build|list|install|info|remove|update`. `info` prints provenance, license and attribution — that output is how a user meets the redistribution obligation.

Existing exit codes 2/3/4 unchanged; new failures get a distinct code.

- [ ] **Step 1: Write the failing tests** — `install` on a corrupted file exits non-zero and registers nothing; `info` prints license and attribution (assert the actual strings); `list` on an empty registry is not an error; `build` without a license fails.
- [ ] **Steps 2–5:** verify failure; implement; full suite; commit.

---

### Task 12: Build and publish the first two packs

**Files:** Create `docs/pack-measurements.md`

Requires network and Ollama. **Not an agent task** — it is the measurement run, and its numbers decide whether the size targets in the spec survive contact with reality.

- [ ] **Step 1:** `ollama pull nomic-embed-text`
- [ ] **Step 2:** Build the Python pack; record wall-clock, doc/chunk counts, final size.
- [ ] **Step 3:** Build the React pack; same.
- [ ] **Step 4:** Compare against the spec's targets: Python pack < 150 MB; `docs_lookup` < 20 ms; `docs_search` < 200 ms excluding query embedding. **Record what actually happened**, including misses.
- [ ] **Step 5:** Spot-check retrieval quality by hand on ~10 real questions per pack — "how do I cancel a fetch", "what does `useMemo` do", "`os.path.join` on Windows". Note where heading trails help or fail.
- [ ] **Step 6:** Publish to the HTTP host with `index.json`, then `argus pack install` from a clean machine to prove the portable path end to end.
- [ ] **Step 7:** Commit `docs: record first pack measurements`

---

## Completion criteria

- [ ] `pytest -q` passes, 0 skipped, 0 warnings; Docker `test` stage green
- [ ] Measured binary+rescore recall@10 ≥ 0.85, recorded in a commit message
- [ ] A model-mismatched pack refuses semantic results and still serves lexical
- [ ] A corrupted pack is rejected and leaves nothing registered
- [ ] `store/packs.py` provably cannot reach the private index
- [ ] A pack without a license cannot be built
- [ ] Both packs installable from a clean machine via `argus pack install`

## Deliberately not in this phase

Win32, .NET, MDN and Linux packs — the format gets proven on the small pair first. A combined `full` pack. Any change to the private index's query path.

## Note on task depth

Tasks 1–3 and 9 carry complete test code: they are the format, the retrieval-quality mechanism, the empirical claim the size argument rests on, and the isolation boundary. Tasks 4–8 and 10–11 are specified to interface and test level — **expand each into full TDD steps immediately before executing it**, not now, since the adapters' details depend on what the real doc repos actually contain.
