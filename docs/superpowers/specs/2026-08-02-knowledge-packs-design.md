# Phase 5 — Public Knowledge Packs

**Date:** 2026-08-02
**Status:** Design, pending review

## Problem

Argus indexes a team's own private repositories. Developers also spend a large
share of their time in *other people's* documentation — Win32, .NET, Python,
MDN, React, Linux — and today the assistant has nothing to offer there. It can
find `DecodeFrame` in your codebase but not tell you what `CreateFileW`'s
`dwShareMode` accepts.

This phase adds a second corpus: a **public knowledge base of SDK and API
documentation**, built once and distributed as a **portable pack** so nobody has
to regenerate it. Packs are versioned, updateable, and shareable.

## Non-goals

- **Replacing web search.** This indexes documentation that ships as source, not
  blog posts, Stack Overflow, or changelog chatter.
- **Mixing with the private index.** See "Isolation" — that boundary is load-bearing.
- **Crawling rendered doc sites.** Everything here comes from sources that are
  redistributable and version-pinnable.
- **Being a general web scraper.** Adding a source means writing an adapter,
  deliberately.

## The observation this design rests on

**Every target on the list publishes its documentation as a public git repo.**

| Target | Source repo | Format |
|---|---|---|
| HTML / CSS / JS | `mdn/content` | Markdown + front-matter |
| React | `reactjs/react.dev` | MDX |
| Python | `python/cpython` → `Doc/` | reStructuredText + `objects.inv` |
| .NET | `dotnet/docs` | Markdown |
| Windows (Win32) | `MicrosoftDocs/sdk-api` | Markdown |
| Windows (dev) | `MicrosoftDocs/windows-dev-docs` | Markdown |
| Linux kernel | `torvalds/linux` → `Documentation/` | reST |
| Linux man-pages | `man-pages` | troff |

So the existing **mirror → change-detect → parse → store** pipeline already does
most of the work, and it is the part that has been hardened and verified against
a real remote. "Updateable" becomes `git diff` against the last-indexed SHA —
machinery that already exists, including the `GIT_ASKPASS` credential path
(unused here, since these remotes are public).

No crawling, no rate limits, no terms-of-service exposure, and provenance is a
commit SHA rather than a timestamp.

## Isolation — why packs are separate databases

Argus's security model is one sentence: *every query is filtered to an
allowlist*. It is enforced structurally — `allowed_repo_ids` is the first
positional parameter, no default, on every public function in
`store/queries.py`, proven by a reflection test that also proves each function
*uses* it.

A public knowledge base has no allowlist. Merging the two corpora would mean
teaching that function about rows that skip the check — putting a bypass inside
the one function whose entire job is to never have one.

**Packs are therefore separate SQLite databases**, opened read-only, queried
through `store/packs.py`, which has no allowlist parameter *because it has
nothing to filter*. The private path and the public path never share a query
function. A bug in one cannot leak the other.

```
private:  identity → allowed_repo_ids → store/queries.py → index.db  (rw by indexer)
public:   (no identity)               → store/packs.py   → *.argus-pack (ro, immutable)
```

## The pack format

A pack is **one SQLite file**, self-describing and self-verifying.

```
python-3.13-r1.argus-pack        # the pack
python-3.13-r1.argus-pack.sha256 # integrity
```

Opened `file:...?mode=ro&immutable=1` — `immutable` tells SQLite the file cannot
change under it, which removes locking and change-counter checks and is a
material read speedup for a file that is by definition frozen.

### Schema

```sql
-- Self-description. A consumer reads this BEFORE trusting anything else.
CREATE TABLE pack_meta (
  key TEXT PRIMARY KEY, value TEXT NOT NULL
);
-- name, version, built_at, builder_version,
-- embedding_model, embedding_dim, embedding_normalized,
-- source_repo, source_commit, license, license_url, attribution,
-- doc_count, chunk_count

CREATE TABLE docs (
  id INTEGER PRIMARY KEY,
  path TEXT NOT NULL UNIQUE,     -- path within the source repo
  title TEXT,
  url TEXT,                      -- canonical published URL, for citation
  lang TEXT,                     -- html | css | js | python | dotnet | win32 | linux
  content BLOB NOT NULL,         -- zstd-compressed UTF-8
  content_len INTEGER NOT NULL   -- uncompressed length
);

-- Headings-aware chunks. A chunk is a section, carrying its heading trail.
CREATE TABLE chunks (
  id INTEGER PRIMARY KEY,
  doc_id INTEGER NOT NULL REFERENCES docs(id),
  heading_path TEXT,             -- "fetch() > Parameters > options"
  anchor TEXT,                   -- deep-link fragment
  start_line INTEGER,
  text BLOB NOT NULL             -- zstd-compressed
);

-- API inventory from structured feeds: objects.inv, front-matter, TOC metadata.
CREATE TABLE api_symbols (
  id INTEGER PRIMARY KEY,
  name TEXT NOT NULL,            -- CreateFileW, Array.prototype.map, os.path.join
  kind TEXT,                     -- function | class | method | property | attribute
  namespace TEXT,
  doc_id INTEGER REFERENCES docs(id),
  anchor TEXT,
  signature TEXT
);
CREATE INDEX idx_api_name ON api_symbols(name);

CREATE VIRTUAL TABLE docs_fts USING fts5(
  title, body, content='', tokenize='unicode61 remove_diacritics 2'
);

-- Binary quantized: 768 bits = 96 bytes per chunk. The coarse pass.
CREATE VIRTUAL TABLE vec_bin USING vec0(
  chunk_id INTEGER PRIMARY KEY, embedding bit[768]
);
-- int8: 768 bytes per chunk. Read ONLY for the top-k rescore.
CREATE VIRTUAL TABLE vec_i8 USING vec0(
  chunk_id INTEGER PRIMARY KEY, embedding int8[768]
);
```

`docs_fts` is **contentless** (`content=''`) rather than external-content,
because `docs.content` is compressed — FTS5 cannot proxy into a BLOB it cannot
read. Terms are stored; snippets come from decompressing the row. This is a
deliberate departure from the private index and the reason is worth stating: the
external-content trick that saves ~300 MB there does not apply when the content
column is compressed.

### Why binary + rescoring

At ~1M chunks, 768-dimensional vectors cost:

| Representation | Bytes/vector | Total |
|---|---|---|
| float32 | 3072 | ~3.0 GB |
| int8 | 768 | ~770 MB |
| **bit** | **96** | **~96 MB** |

Search runs against `vec_bin` (Hamming distance, very fast, ~96 MB touched),
takes the top ~300 candidates, then rescores **only those** against `vec_i8`.
That recovers most of the recall the coarse pass loses, while reading under
250 KB of int8 data per query instead of 770 MB.

This is what makes "huge", "portable" and "fast" simultaneously true rather than
a pick-two.

### The model pin is enforced, not documented

`pack_meta.embedding_model` records the exact model and dimension. **A pack whose
model does not match the querying instance refuses to serve semantic results**
and says why.

This is not defensive tidiness. Vectors from a different embedding space produce
*plausible-looking, subtly wrong* results — the failure mode that looks like it
works. Lexical and API-symbol search still function, so a mismatched pack
degrades rather than dies.

Packs are pinned to **`nomic-embed-text`, 768 dimensions, normalized** — the same
model Phase 4 uses for the private index, so one model stays resident.

## Ingestion

Per source, an **adapter** supplying three things:

1. **Repo coordinates** — remote, branch, and the subtree that actually contains docs.
2. **A document parser** — Markdown/MDX/reST/troff → `(title, url, lang, body, headings)`.
3. **An inventory feed**, where one exists — Sphinx `objects.inv` for Python, MDN
   front-matter, MS docs metadata blocks. These give exact `symbol → doc + anchor`
   mappings cheaply, and they are what makes "what are the parameters of X"
   answerable precisely rather than approximately.

Adapters live in `argus/packs/sources/` and are registered by name. Adding a
source is writing one adapter; it does not touch the pipeline.

### Chunking is heading-aware, and that matters

Code chunks at function boundaries. **Prose must chunk at headings**, and each
chunk must carry its heading trail into the embedded text:

```
fetch() > Parameters > options > redirect
A string indicating how to handle a redirect response...
```

Without the trail, a chunk reading "A string indicating how to handle..."
embeds to almost nothing useful. With it, the chunk is self-locating. This is
the single highest-leverage decision in retrieval quality for documentation, and
it is why doc chunking cannot reuse the code path.

## Distribution

Plain HTTP/S3 with a manifest index. No registry, no platform size caps.

```
https://<host>/packs/index.json
https://<host>/packs/python-3.13-r1.argus-pack
https://<host>/packs/python-3.13-r1.argus-pack.sha256
```

`index.json` lists every available pack with name, version, size, SHA-256,
embedding model, source commit, license, and attribution.

```
argus pack list                     # what is available
argus pack install python@3.13      # download, verify SHA-256, register
argus pack update                   # refresh anything with a newer version
argus pack info python              # provenance, license, attribution, counts
argus pack remove python
```

Integrity is checked on install and **the pack is rejected on mismatch** — a
truncated download must not become a silently half-empty knowledge base.

### Licensing is a redistribution obligation

These corpora are redistributable, but not unconditionally:

| Source | License | Requires |
|---|---|---|
| MDN | CC-BY-SA 2.5+ | attribution, share-alike |
| MS Learn / sdk-api | CC-BY-4.0 | attribution |
| React docs | CC-BY-4.0 | attribution |
| Python docs | PSF License | attribution, notice |
| Linux kernel docs | GPL-2.0 / CC-BY-SA-4.0 | attribution |
| man-pages | mixed per page | per-page notice |

Every pack carries license and attribution in `pack_meta`, `argus pack info`
prints it, and the MCP tool includes source URL and license in results. Building
a pack you cannot lawfully share is a failure of the builder, not the user, so
**the builder refuses to emit a pack for a source without a recorded license**.

## Query path and tools

Two new MCP tools, served from the same server but through `store/packs.py`:

| Tool | Answers |
|---|---|
| `docs_lookup(symbol, lang?)` | "What are the parameters of `CreateFileW`?" — exact API inventory hit, returns signature, description, canonical URL |
| `docs_search(question, lang?)` | "How do I cancel a fetch in JS?" — binary-coarse → int8-rescore semantic search over chunks, returns heading trail + URL |

Both state in their descriptions that results come from public documentation
with a named source and license, so the model attributes rather than asserts.

Neither takes an allowlist, and neither can reach the private index — they are
wired to a different module against different files.

## Performance targets

Stated so they are testable, not aspirational:

| Metric | Target |
|---|---|
| `docs_lookup` (exact symbol) | < 20 ms |
| `docs_search` end-to-end, 1M chunks | < 200 ms excluding query embedding |
| Pack size, full Python docs | < 150 MB |
| Pack size, all eight sources | < 1.5 GB total |
| Install (verify + register) | < 10 s per pack, excluding download |
| Rebuild after upstream update | proportional to `git diff`, not corpus size |

## Update model

A pack records `source_commit`. Rebuilding fetches the source repo, diffs
against that commit, and re-parses only changed documents — re-embedding only
their chunks. A doc's chunks are replaced atomically, so a partial rebuild
cannot leave a document half-embedded.

Consumers do not rebuild. They download the new pack version. That is the point:
**one person builds, everyone else installs.**

## Verification

Against the same standard as Phase 1 and 2 — the checks that would have caught
the defects this project actually shipped:

- A pack whose `embedding_model` differs from the instance's **refuses semantic
  results and says why**, while lexical and symbol lookup still work.
- A corrupted pack fails the SHA-256 check and is **not** registered.
- Binary-coarse + rescore returns results overlapping a float32 baseline above a
  stated recall threshold on a held-out query set — *measured*, not assumed.
- `store/packs.py` cannot reach the private index, and `store/queries.py` cannot
  reach a pack. Asserted by reflection, like the allowlist rule.
- A pack built from a source with no recorded license is refused at build time.
- Heading-trail chunking is present in the embedded text — a regression to bare
  section bodies must fail a test, since it is invisible in output but halves
  retrieval quality.

## Open questions for the plan

- Which sources ship in the first pack set. Python and React are small and fast
  to validate; `sdk-api` and `dotnet/docs` are the large ones and should follow
  once the format is proven.
- Whether `docs_search` should span multiple installed packs in one call or
  require a `lang` filter. Cross-pack ranking needs score normalization across
  independently built packs, which is not free.
- Whether to publish a `full` pack alongside per-language packs, given the 1.5 GB
  target.
