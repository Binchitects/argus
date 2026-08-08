# First pack measurements

The measurement run for Phase 5. Both packs built from real upstream sources
with the real embedder, on Windows 11, CPU-only Ollama 0.30.7,
`nomic-embed-text`.

Everything below is measured. Where a target was missed or a result was poor,
that is recorded rather than tuned away first — the point of this run was to
find out whether the design's claims survive contact with real documentation.

## What was built

| | Python | React |
|---|---|---|
| source | `python/cpython` @ `3.13` | `reactjs/react.dev` @ `main` |
| commit | `f85d8e4cac1b` | `c7d6b700038c` |
| documents | 516 | 222 |
| chunks | 13,164 | 4,755 |
| API symbols | 18,027 | 125 |
| unresolved symbols | **0** | **0** |
| size | **28.5 MB** | **9.1 MB** |
| licence | PSF-2.0 | CC-BY-4.0 |

```
python  sha256 3c4d390f86f2f47d2239b0f9133535abb9e04b7cff19048a1f2f4077cd51d677
react   sha256 fdb4ef70dab9324e7dc1c22135ebbd5f86586fb44b7ef633a6cc91c42d448c25
```

`objects.inv` is a build artifact and is absent from a source checkout, so the
Python build used the published `docs.python.org/3.13/objects.inv`
(18,544 entries) matched to the 3.13 branch. **18,027 of those resolved to a
document and 0 were unresolved** — the 517 difference is exactly the anchorless
`std:doc` entries the adapter skips by design. That is the first real
confirmation that the symbol-to-document path normalisation works, since
Python names `library/os.path.html` while the doc is `library/os.path.rst`.

## Against the spec's targets

| Target | Result | |
|---|---|---|
| Python pack < 150 MB | **28.5 MB** | met, 5× margin |
| `docs_lookup` < 20 ms | **2.1 ms** median, 3.0 ms max | met |
| `docs_search` < 200 ms excl. embedding | **88.6 ms** median, 90.8 ms max | met |

Measured with both packs open (17,919 chunks), 10 questions, `coarse=600`.

`search_text` ran at 4.7 ms median.

**Query embedding is the real cost: 2,254 ms median.** It is excluded from the
target by the spec's own wording, and it is a property of CPU-only Ollama
rather than of the pack format — but it is ~25× the entire search. Anyone
deploying this should expect the embedder, not the index, to set the latency
users feel. A GPU or a smaller embedding model is the lever.

## Retrieval quality

Ten questions per pack, top hit judged by hand. Honest tally: **6 good,
2 partial, 2 wrong**.

Good — the heading trail is doing visible work:

| question | top hit |
|---|---|
| regular expression named groups | `re — Regular expression operations > Regular Expression Syntax` |
| reading a file line by line | `Input and Output > Reading and Writing Files > Methods of File Objects` |
| what is the difference between a list and a tuple | `Design and History FAQ > Why are there separate tuple and list data types` |
| how do I cancel a fetch request | `Putting it all together > Fix fetching inside an Effect` |
| how do I memoize a component | `reference/react/memo` |
| how do I run code after the browser paints | `Epilogue: Browser paint` |

Wrong:

- **"how do I parse a date string"** returned `email.utils: Miscellaneous
  utilities` rather than `datetime.strptime` or `time.strptime`.
- **"what does useMemo do"** returned `eslint-plugin-react-hooks/lints/use-memo
  > Rule Details` rather than `reference/react/useMemo`.

The pattern in both misses is the same: **a page that discusses an API at
length outranks the page that defines it.** react.dev's lint-rule documents
were the top hit for two of five React questions. Semantic similarity has no
notion of authoritativeness, and nothing in `search_docs` supplies one.

This is worth fixing but was left alone deliberately — tuning ranking after
seeing ten questions is how you fit to ten questions. The same signal that
fixed `docs_lookup` (below) would probably help: prefer a document named after
the thing being asked about.

## Two defects this run found

Neither was visible to 465 passing tests, because both needed real
documentation to appear.

**`docs_lookup` returned the wrong page for the most common React hooks.**
`useState` resolved to `react.dev/learn/typescript#typing-usestate` — the
typing guide's footnote — instead of `reference/react/useState`. The
extraction was correct: the typing guide really does have a `` `useState` ``
heading with a pinned anchor, so it is a real symbol. The bug was that nothing
ranked the authoritative page first. `useMemo` and `memo` failed the same way.
This is the confident-wrong-answer failure in its purest form: exact, correctly
anchored, fully attributed, 0.1 ms, and wrong. Fixed by preferring the document
named after the symbol, with a regression test built from the real collision.

**A quarter of the Python pack's heading trails began with `!`.** CPython
writes `:mod:`!os.path`` in 255 library documents — Sphinx's
suppress-the-hyperlink form, where `!` is a display directive and not part of
the name. Titles and trails read `!os.path — Common pathname manipulations`.
Because the trail is prepended before embedding, this was noise inside every
affected Python vector, not merely a cosmetic flaw. The sibling form
`` :meth:`~os.path.join` `` (show only the last component) was also
unhandled. Both are now interpreted, and the Python pack was rebuilt.

## Portability

Verified end to end over HTTP rather than assumed:

- `argus pack install http://…/react.arguspack --sha256 …` into an empty
  directory succeeded, and `pack list` reported it correctly from there.
- The same install with one byte of the digest changed was refused, and **left
  zero files behind** — no pack, no staging file, nothing registered.

Not done: publishing to a public host. That is an outward-facing action and is
the maintainer's to take, not something to do as a side effect of a
measurement run.

## Notes for the next pack

- **React yielded only 125 symbols from 222 documents.** That is the
  precision-first rule working as designed — a heading must be entirely a code
  span *and* carry a pinned anchor — but it is low enough to be worth checking
  against a list of React's public API before adding more sources. Python's
  18,027 came from an inventory; react.dev has none, and the difference shows.
- Build time was dominated by embedding, as expected: the pack format's own
  work is negligible next to 13,164 CPU embeddings.
- `objects.inv` must be fetched separately and **matched to the branch**. An
  inventory from a different version would produce silently wrong anchors, and
  nothing downstream would notice.

---

# The Windows and reference packs

Built with the same embedder and hardware as above. The first two are measured
here; the three large ones are recorded when their builds land.

| | system-design | algorithms |
|---|---|---|
| source | `donnemartin/system-design-primer` | `TheAlgorithms/C-Plus-Plus` |
| commit | `ae9bbd7` | `b9c118f` |
| documents | 9 | 371 |
| chunks | 442 | 2,001 |
| API symbols | 8 | 370 |
| **unresolved symbols** | **0** | **0** |
| size | **1.3 MB** | **4.3 MB** |
| licence | CC-BY-4.0 | MIT |

Zero unresolved symbols on both. That is the check worth watching: a symbol
whose page is missing would still install, still list, and simply never
resolve — the failure is invisible until somebody looks something up.

## Retrieval, hand-checked

Ten questions with a knowable right answer, run through `docs_search` against
both packs installed together.

**8 of 10 top-1 correct. 9 of 10 in the top 3.**

The two that were not are more interesting than the eight that were.

### "cache the results of database queries" — right pack, wrong rank

Returned the Primer's main README first and the `query_cache` case study
second. Defensible rather than wrong: the README genuinely does cover caching
at length, and the case study is one rank below. It is recorded as a miss
because a developer reads the first result.

### "a sorting algorithm that runs in n log n" — bogo_sort

The joke algorithm, O(n · n!), followed by pigeonhole sort. This is the
sharpest limitation the code packs have, and it is structural rather than a
tuning problem:

**A code pack matches vocabulary, not properties.** Every file in `sorting/`
says "sorting algorithm" in its header comment, so all 40 of them are near-
identical in embedding space for a query phrased that way. The one thing that
would separate them — asymptotic complexity — is either absent from the source
or written as `O(n log n)` in a comment, which carries almost no semantic
weight next to the surrounding code.

So the honest statement of what these packs do:

| question shape | works |
|---|---|
| "show me an implementation of X" | yes — `quicksort`, `dijkstra`, `binary search` all top-1 |
| "which X has property Y" | **no** |
| "how do I design X" (prose corpus) | yes — 5 of 6 top-1 |

Not tuned away. Reranking on ten questions is how you fit to ten questions,
and the fix that would actually work — extracting complexity into the indexed
text — is a change to what the adapter emits, justified by a larger sample
than this one.

---

# Quality and cost, per pack

Everything measured on one machine: Windows 11, CPU-only Ollama 0.30.7,
`nomic-embed-text` at 768 dimensions. No GPU. Build times are wall clock for a
cold pack with an empty embedding cache.

## Cost

| pack | documents | chunks | symbols | size | build | MB / 1k chunks |
|---|---|---|---|---|---|---|
| `system-design` | 9 | 442 | 8 | 1.3 MB | < 1 min | 2.94 |
| `algorithms` | 371 | 2,001 | 370 | 4.3 MB | < 1 min | 2.15 |
| `cpp` | 9,746 | 123,212 | 37,305 | 174.7 MB | **36 min** | 1.42 |
| `wdk` (composite) | 28,153 | 239,145 | 38,038 | 347.4 MB | **74 min** | 1.45 |
| `scripting` (composite) | 9,302 | — | 9,302 | — | pending | — |
| `win32` (composite) | ~71,700 | — | — | — | pending | — |

Documents and symbols for the two pending packs come from running the adapters
over the real checkouts; their chunk counts, sizes and times are not yet
measured and are deliberately left blank rather than estimated.

**Cost is almost entirely embedding.** Measured throughput was 57 chunks/sec
on `cpp` and 54 on `wdk` — steady across a 2× difference in corpus size, so a
build time is predictable from a chunk count once you have one:

```
minutes ≈ chunks / 55 / 60
```

**Storage improves with scale.** MB per 1,000 chunks falls from 2.94 on the
smallest pack to ~1.42 on the largest. The per-pack overhead — schema, FTS
dictionary, vector index headers — is fixed, so it amortises.

**Chunks per document vary by an order of magnitude**, which is why a build
estimate taken from a document count is worthless: `cpp` averages 12.6
chunks/doc (long articles), `wdk` 8.5, `algorithms` 5.4. An early estimate here
projected `cpp` at 47,000 chunks from a 500-document sample and it came in at
123,212 — 2.6× off, because the sample was alphabetically first and therefore
short.

### What an interruption costs

A build deletes its half-written pack on any failure, so before the embedding
cache existed an interrupted `wdk` cost all 74 minutes. With the cache, a
rerun re-chunks (seconds) and re-embeds only what it never reached. The first
composite build already showed this working — `5751 reused, 233394 computed` —
where the reused entries were text shared with an earlier build.

Cache cost is ~0.8 GB for these corpora, keyed on the embed text plus model.
It is a sidecar: deleting it costs time, never correctness.

## Quality

| pack | unresolved symbols | hand-checked retrieval | notes |
|---|---|---|---|
| `system-design` | **0** | 5 / 6 top-1 | one miss ranked the right pack, wrong page |
| `algorithms` | **0** | 3 / 4 top-1 | fails on property questions — see below |
| `cpp` | **0** | not yet | |
| `wdk` (composite) | **3 → 0** | not yet | 3 was a real defect, fixed and rebuilt |
| `scripting` | 0 (pre-build) | not yet | |
| `win32` | pending | not yet | |

**Unresolved symbols is the cheapest quality signal in the build.** A symbol
whose page is missing still installs, still lists, and simply never resolves —
the failure is invisible until someone looks that name up. `wdk` reporting 3
where every other pack reported 0 was what exposed a real defect: `iter_docs`
skipped oversized and binary files while `iter_symbols` did not, so a sample
anchored to a skipped file produced a symbol pointing at a page that was never
written.

Combined hand-check across the two measured packs: **8 of 10 top-1, 9 of 10 in
the top 3.** The full question set and both misses are recorded above.

### The limit worth knowing before you rely on this

A code pack matches vocabulary, not properties. "A sorting algorithm that runs
in n log n" returns `bogo_sort` — O(n · n!) — because all forty files in
`sorting/` say "sorting algorithm" in a header comment and complexity carries
almost no semantic weight beside code.

| question shape | works |
|---|---|
| "show me an implementation of X" | yes |
| "how do I design X" | yes |
| "which X has property Y" | **no** |

### Retrieval cost, once installed

| corpus | search, excluding embedding |
|---|---|
| 17,919 chunks (2 packs) | 88.6 ms |
| 364,800 chunks (4 packs) | **460 ms** |

5.2× for 20.4× the corpus — sublinear, so the two-stage binary/int8 design
holds at size. Query embedding remains the dominant cost at **~2,500 ms** on
CPU, roughly five times the search itself. That is hardware, not code, and a
GPU is the only lever that moves it.

Both figures were taken while a pack build saturated the CPU, so they are
pessimistic rather than flattering.
