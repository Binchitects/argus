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
| `debugger` (composite) | 2,138 | 14,259 | 1,511 | 24.8 MB | ~4 min | 1.74 |
| `sqlite` (archive) | 837 | 8,987 | 36 | 18.3 MB | ~3 min | 2.04 |
| `scripting` (composite) | 9,302 | 46,027 | 9,302 | 70.1 MB | 13 min | 1.52 |
| `cppreference` (archive) | 6,640 | 68,891 | 5,406 | 124.9 MB | ~21 min | 1.81 |
| `cpp` | 9,746 | 123,212 | 37,305 | 174.7 MB | 36 min | 1.42 |
| `wdk` (composite) | 28,176 | 245,727 | 38,041 | 358.6 MB | **74 min** | 1.46 |
| `win32` (composite) | 71,663 | 530,559 | 87,297 | 786.2 MB | **162 min** | 1.48 |
| **total** | **128,882** | **1,040,105** | **179,276** | **1.57 GB** | **~5h 13m** | |

The two marked *(archive)* arrive through `fetch_archive` rather than a git
clone: SQLite has no docs repository on GitHub at all, and cppreference's
repo holds the build tooling rather than the rendered pages. Their provenance
is the archive's sha256 instead of a commit, which is the thing a reader can
actually verify -- re-download, re-hash, compare.

The symbol counts differ by an order of magnitude for a reason worth stating.
cppreference generates one entity per page, so paths *are* an inventory:
`cpp/container/vector/push_back.html` is exactly `std::vector::push_back`,
giving 5,406 exact-lookup names. SQLite documents every pragma on one combined
page, so only the 36 per-statement `lang_*` pages earn a symbol -- a lookup
whose anchor lands in the middle of a 1,000-line page is worse than no lookup.
Its other 801 documents remain fully reachable through `docs_search`.

Every pack reports **0 unresolved symbols**.

The whole estate is 1.4 GB and just under five hours of CPU-only embedding,
built once.

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

---

# Does this actually help an agent?

The question the packs exist to answer, tested against **qwen3.6:35b** running
locally. Fifteen questions with objectively checkable answers -- a header, an
import library, a DLL, an IRQL, a command flag. No LLM grading: the answer
either contains the string or it does not. Same model, temperature 0; the only
variable is whether retrieved pack context is in the prompt.

| area | closed book | with packs |
|---|---|---|
| win32 | 5 / 5 | 5 / 5 |
| **wdk** | **0 / 5** | **5 / 5** |
| scripting | 4 / 5 | 4 / 5 |
| **total** | **9 / 15** | **14 / 15** |

**WDK is where the packs earn their keep.** The model knows Win32 well and
kernel DDI not at all -- 0 of 5 unaided, 5 of 5 with the pack. It could not
name the header for `IoAllocateIrp` or the IRQL for `KeAcquireSpinLock`, and
with the pack it got every one right.

## How retrieval is used matters more than what is in the pack

The first three runs of this test showed the packs making the model **worse**.
None of it was the pack data. Each cause is worth stating, because an agent
wired the wrong way will reproduce every one:

**1. "Use ONLY the reference material" cost 4 correct answers.** win32 went
5/5 to 1/5. Forbidding the model's own knowledge means a retrieval miss
actively destroys an answer it already had. Reference material must add, not
gate.

**2. Semantic search is the wrong tool for a known API name.** Asked which DLL
exports `SetWindowsHookExW`, `docs_search` returned `FindWindowSW` and
`SetWindowOrgEx` -- topically adjacent, factually useless. `docs_lookup`
returns the exact page carrying
`Header: winuser.h; Library: User32.lib; DLL: User32.dll`. An agent that only
searches throws away the pack's best feature.

**3. A chunk is a fragment, and the answer is often in a different one.**
Asked which robocopy option mirrors a tree, search correctly ranked robocopy's
reference page first and returned its Syntax and Examples sections. `/MIR` is
in the options table, in a chunk the per-document cap excluded. Scripting sat
at 1/5 until `docs_get` was added to read the whole page: 4/5.

So the retrieval chain an agent should use is:

```
docs_lookup(name)      exact API names -- authoritative, no embedding
docs_search(question)  prose questions, and to find the right page
docs_get(doc_path)     read that page in full before answering a detail
```

Missing any one of the three measurably costs answers.

## Caveats

Fifteen questions is a smoke test, not a benchmark. It covers factual recall --
headers, libraries, IRQLs, flags -- and says nothing about whether the packs
improve reasoning, code generation, or multi-step tasks. The one remaining
regression (`DISM`, which the model knew and the pack talked it out of) is
recorded rather than tuned away.

## Code generation and reasoning: no measurable effect

The factual run above was extended with six code-generation tasks and four
reasoning tasks, same model and method. Reasoning ran with thinking ENABLED,
because that is qwen3.6's actual reasoning mechanism and testing it with
thinking off would measure something nobody runs.

| kind | closed book | with packs |
|---|---|---|
| factual recall (15 questions) | **9 / 15** | **14 / 15** |
| code generation (6 tasks, 20 elements) | 19 / 20 | 19 / 20 |
| reasoning (4 tasks, 7 elements) | 7 / 7 | 7 / 7 |

**The reasoning result is inconclusive, not negative.** The model answered all
four correctly with no help, so there was no headroom to detect an
improvement -- the same saturation that made the embedding-model comparison
unable to decide anything. A harder set might discriminate; this one cannot.

**The pattern across all three is the useful finding.** On the *same subject* --
WDK -- the model scored 0/5 on "which header declares IoAllocateIrp" while
scoring 7/7 writing driver code and 5/5 explaining IRQL rules.

| what the model already holds | what it does not |
|---|---|
| the shape of a DispatchRead routine | which header declares it |
| why paged pool faults at DISPATCH_LEVEL | which `.lib` exports the allocator |
| that GetAdaptersAddresses needs a resize loop | that the header is `iphlpapi.h` |

Concepts and code shapes are in the weights, heavily represented by books,
blogs and answers. Exact header names, import libraries and per-routine IRQL
constraints are not -- and those are exactly what a developer stops to look up.

So the honest claim for these packs is narrow and worth stating precisely:
**they replace the lookup, not the thinking.** That is what a reference is for.

### What this does not measure

Grading is by required structural elements -- the API that must be called, the
flag that must be set, the constraint that must be respected. That is a proxy:
an answer naming `ExAllocatePool2` and `NonPagedPool` while misusing both
scores as a pass, and this run cannot tell. Ten generation tasks and fifteen
questions is a smoke test. Read it as evidence of *where* the packs help, not
as a quality score.

---

# The reliable measurement: 120 questions, ground truth from the packs

Every earlier attempt wrote questions from memory and checked them afterwards.
The worst of them marked the packs **wrong for being right**: the expected IRQL
for `IoCreateDevice` was PASSIVE_LEVEL from recollection, Microsoft publishes
`<= APC_LEVEL`, and the grounded answer was scored a failure for following the
documentation.

So the question set is generated **from the pack pages** -- question and answer
extracted from the same page, meaning the expected answer is whatever the
upstream project actually publishes. 120 questions, 30 each from win32, wdk,
cpp and scripting, seed fixed at 20260809 so the set is reproducible.

This is not circular. The claim under test is *does retrieval make the agent
agree with the official documentation*, which is what a reference is for, and
the closed-book arm measures how often the model already knows the documented
fact. Were the documentation wrong, both arms would be wrong together.

Selection is biased toward the hard end: names of 9 characters or more (short
names are often common words), generic headers like `windows.h` dropped as
guessable, and sampling shuffled across the whole corpus rather than taking
the first N of an alphabetical list. The result is genuinely obscure --
`DXVAHD_FEATURE_CAPS`, `MprAdminPortEnum`, `secur32.lib`.

Grading reads only the final `VERDICT:` line, and `unknown` is a permitted
answer so hedging scores as a miss. Matching against a whole reply is how an
earlier run produced a false 10/10.

## Result: qwen3.6:35b, 38% -> 81%

| pack | closed book | with packs | |
|---|---|---|---|
| **win32** | 9 / 30 | **29 / 30** | +20 |
| **wdk** | 9 / 30 | **25 / 30** | +16 |
| **scripting** | 2 / 30 | **18 / 30** | +16 |
| cpp | 26 / 30 | 25 / 30 | -1 |
| **total** | **46 / 120** | **97 / 120** | **+51** |

**53 answers fixed, 2 broken.** A 26:1 ratio, on a set large enough that the
result is not noise.

| question kind | closed book | with packs |
|---|---|---|
| which header declares X (82) | 38 / 82 | **72 / 82** |
| name the command from its description (30) | 2 / 30 | **18 / 30** |
| which import library (6) | 4 / 6 | 5 / 6 |
| at what IRQL (2) | 2 / 2 | 2 / 2 |

## What the shape of it says

**`cpp` is the control, and it behaves like one.** 26/30 unaided: the model
knows which standard header declares `std::vector::push_back`, because that is
written down in a million places. Retrieval adds nothing there and costs one
answer. A pack for material the model already holds is not worth its disk.

**Reverse lookup is the hardest shape and the most realistic.** Given only what
a command does, name it: 2/30 unaided. That is how a scripting question
actually arrives -- the developer knows the goal, not the name -- and it is
where a pack earns most.

**Win32 and WDK are where the value is**, exactly as the smaller run suggested
but now on 60 questions rather than 10. 9/30 unaided in both; 29/30 and 25/30
with retrieval.

The two broken answers are the residual risk, unchanged in character from the
earlier finding: retrieved context can still displace something the model had
right. At 53:2 that trade is worth making, but it is not zero.


## Corrected: the cpp pack, measured properly

The 120-question run put `cpp` at 26/30 closed book against 25/30 with packs,
and the obvious reading was to stop installing it. That reading was wrong, and
so was the test.

`cpp` is **61% MSVC-specific** -- 1,515 compiler errors, 595 warnings, 517
build options, 955 C runtime pages -- against 733 standard-library pages. The
question set sampled only `std::` symbols, so it asked exclusively about the
part any competent model already knows, and judged the whole pack on a fifth
of it.

Asked about the other four fifths -- given a diagnostic message, name the code
that produces it:

| kind | closed book | with packs |
|---|---|---|
| error codes (20) | 1 / 20 | **20 / 20** |
| warning codes (20) | 2 / 20 | **20 / 20** |
| **total** | **3 / 40** | **40 / 40** |

37 fixed, 0 broken. The pack stays.

| what `cpp` is asked | closed book | with packs | verdict |
|---|---|---|---|
| which header declares `std::…` | 26 / 30 | 25 / 30 | model already knows |
| which code produces this diagnostic | 3 / 40 | 40 / 40 | model knows almost none |

**The general lesson is about testing, not about C++.** A pack is not one
thing. Generating questions from whichever slice is easiest to extract
measures that slice, and a confident conclusion drawn from it can be exactly
backwards -- here it would have removed the pack with the largest single
measured gain in the project.

---

# Five retrieval strategies, measured

Same 120 questions, same model (qwen3.6:35b), temperature 0. Only the strategy
changes.

| pack | A closed | B retr-first | C verify-after | D hybrid | **E double-tap** |
|---|---|---|---|---|---|
| cpp | 26/30 | 25/30 | 26/30 | 26/30 | 25/30 |
| scripting | 2/30 | 18/30 | 2/30 | 8/30 | **18/30** |
| wdk | 9/30 | 25/30 | 18/30 | 23/30 | **25/30** |
| win32 | 9/30 | 29/30 | 14/30 | 20/30 | **30/30** |
| **total** | **46/120** | 97/120 | 60/120 | 77/120 | **98/120** |

* **A** the model alone.
* **B** pack context in the prompt, then answer. Fixed 51, **broke 3**.
* **C** answer, then correct only what the docs contradict. Fixed 14, **broke 0**.
* **D** route on the draft: verify if it committed, retrieve if it hedged.
* **E** retrieval-first, then verify that answer too. Rescued 1 of B's
  mistakes, broke 0.

## Why the hybrid lost, and why it matters

D looked like the principled design -- ignorance needs retrieval, error needs
verification, so detect which and apply the matching remedy. It scored 77/120,
below B.

The routing counts say why: **100 of 120 questions went to verification and
only 20 to retrieval.** `committed()` detects hedging, and this model rarely
hedges. It asserts a confident wrong header instead, which reads as "committed"
and routes to a tool that can only correct a claim -- when what it needed was
the fact supplied.

**You cannot route on the model's confidence, because its confidence is
uncorrelated with its knowledge.** That is the same property that makes these
packs worth building, so it should not have been a surprise, and it is written
here because the design felt obviously right and was wrong.

## Why double-tap wins

E does not route. It retrieves *and* verifies, every time: retrieval supplies
what the model lacks, verification catches what retrieval displaced. Neither
stage has to guess which failure it is facing.

The cost is real -- six model calls per question against two -- and the margin
over plain retrieval is one answer in 120. What E actually buys is the
**removal of a failure mode**: B broke 3 correct answers on this set, and on a
larger corpus that class of harm grows with retrieval noise while E's
verification pass keeps checking it.

| strategy | fixes ignorance | fixes error | breaks correct answers | model calls |
|---|---|---|---|---|
| retrieval-first | yes | partly | **3** | 2 |
| verify-after | no | yes | **0** | 2-3 |
| **double-tap** | **yes** | **yes** | **0** | 6 |

## Recommendation

Use **double-tap** where answer quality matters more than latency: retrieve,
answer, then run `docs_verify` on the answer and revise only the contradictions.
Use plain retrieval where six calls per question is too slow. Do not route on
confidence.

## Eight strategies, and where the ceiling actually is

| strategy | total | cpp | scripting | wdk | win32 |
|---|---|---|---|---|---|
| A closed book | 46/120 | 26 | 2 | 9 | 9 |
| B retrieval-first | 97/120 | 25 | 18 | 25 | 29 |
| C verify-after | 60/120 | 26 | 2 | 18 | 14 |
| D hybrid routing | 77/120 | 26 | 8 | 23 | 20 |
| **E double-tap** | **98/120** | 25 | 18 | 25 | **30** |
| F extract, memory banned | 84/120 | **13** | 17 | 24 | 30 |
| G consensus of 3 framings | 87/120 | 16 | 17 | 24 | 30 |
| H extract, memory allowed | 95/120 | 23 | 17 | 25 | 30 |

**Double-tap remains best.** Everything after it was an attempt to beat it and
none did.

### The one mistake, made three times

Every strategy that constrained the model to the reference destroyed answers it
already had:

| constraint | cost |
|---|---|
| "use ONLY the reference material" | win32 5/5 -> 1/5 |
| retrieval noise displacing knowledge | 3 correct answers broken |
| "do not rely on memory" (arm F) | cpp 26/30 -> **13/30** |

Arm H is F with the memory prohibition removed and nothing else changed: cpp
recovers 13 -> 23. The prohibition was the entire damage.

### Prompt engineering is finished here

Four framings land within three answers of each other, and the three consensus
framings disagreed on **0 of 120** questions. The model is stable and reads
this corpus as well as it is going to. The next twenty answers are not in the
prompt.

### Where they are instead

| pack | best | remaining gap |
|---|---|---|
| win32 | 30/30 | 0 |
| cpp | 26/30 | 4 |
| wdk | 25/30 | 5 |
| **scripting** | **18/30** | **12** |

Over half the residual loss is one question shape: scripting's reverse lookup,
*given a description, name the command*. That is a retrieval problem, not a
reading problem -- the identifier is absent from the question, so `docs_lookup`
can never fire and semantic search must recover a command from its behaviour.

The scripting pack stores each command's description verbatim in
`api_symbols.signature`, and nothing searches that field. It is reachable only
through chunk embeddings today. A full-text index over symbol descriptions is
the concrete next move, in the same family as the `docs_get` gap: a missing
query surface rather than a tuning problem.

---

# Final: 101/120, and where the remaining ceiling is

After adding `docs_find` (search symbol descriptions) to the retrieval chain
and rebuilding the scripting pack with clean descriptions:

| pack | A closed | B retr-first | C verify | D hybrid | E double-tap |
|---|---|---|---|---|---|
| cpp | 26/30 | 25/30 | 26/30 | 26/30 | 25/30 |
| scripting | 2/30 | **21/30** | 2/30 | 9/30 | **21/30** |
| wdk | 9/30 | 25/30 | 18/30 | 23/30 | 25/30 |
| win32 | 9/30 | **30/30** | 14/30 | 20/30 | **30/30** |
| **total** | **46/120** | **101/120** | 60/120 | 78/120 | **101/120** |

Scripting 18 -> 21 and the total 98 -> 101, from the new query surface plus
the description fix.

## The finding that matters more than the number

**`docs_find` answers 29 of 30 scripting questions correctly on its own, and
moved the end-to-end score by 3.** The right command now reaches the model on
nearly every one of those questions, and it still gets 9 wrong.

That is the same wall the prompt arms hit. Retrieval on this set is close to
solved; what remains is the model reading a correct answer out of correct
context. No query surface fixes that, and neither did four framings of the
prompt.

## Verification is insurance, not a gain

On this run double-tap **rescued 0 and broke 0** -- it tied plain retrieval
rather than beating it. Earlier, with noisier retrieval, it rescued 1 and
prevented the 3 answers retrieval-first destroyed. Its value is proportional
to how wrong retrieval is, so it earns its two extra calls on a noisy corpus
and nothing on a clean one.

## Cost of the rebuild

Fixing the descriptions and rebuilding scripting cost 7 minutes rather than
13: **45,631 embeddings reused, 396 computed.** Only the chunks whose text
actually changed were re-embedded.

## What is still unmeasured

* Code generation and multi-claim analysis. Every number here is single-fact
  recall, which is the shape that grades cleanly and the shape `docs_verify`
  handles most easily. A code block asserts many facts at once and is where
  verification should matter most.
* `docs_find` on a user's own phrasing. The 29/30 is on questions generated
  from the same descriptions being searched, so it shows the field is
  searchable, not that arbitrary wording finds it. Hand-written queries did
  markedly worse.
* `algorithms` and `system-design`, which have no symbol table yielding clean
  factual questions and appear in no arm of this comparison.

---

# Code generation, multi-claim: the packs' strongest result

Every earlier number was single-fact recall. Real work is not that shape: a
code block asserts many facts at once, and one wrong `#include` makes the whole
thing not build.

25 tasks, each naming two APIs from **different** headers, so a correct answer
must get four independent facts right -- two headers, two libraries. Graded per
claim rather than per task, because a whole-block pass/fail hides the effect.

| strategy | claims correct |
|---|---|
| closed book | **28 / 100 (28%)** |
| **retrieval-first** | **73 / 100 (73%)** |
| verify-after | 45 / 100 (45%) |

`docs_verify` fired on 13 of 25 drafts and **gained 17 claims while losing 0**.

**This is where verification earns its design.** On single-fact questions it
gained 14 and looked marginal against retrieval's 51. On multi-claim code it
corrects fact by fact: retrieval can only bias a whole generation, while
verification checks each `#include` and each `.lib` independently. Zero claims
lost across 100 is the property the design was built for, now demonstrated on
the shape it was built for.

It is also the largest lift in the project: **28% to 73%**. Writing code that
names four correct facts is far harder than answering one, and unaided the
model manages barely a quarter of them.

Caveat the number invites: this checks that the right header and library are
NAMED, not that the code compiles. A plausible program naming the right
headers scores full marks.

---

# Out of domain: installing packs has a small tax

The control every earlier measurement was missing. Six packs are always
installed, but an agent asks about everything -- so what happens on questions
the packs do not cover?

20 questions across HTTP, Python, SQL, git, POSIX, regex, networking, Java and
JavaScript, none of them in any installed pack.

| | closed book | with packs |
|---|---|---|
| total | **16 / 20** | **15 / 20** |

**Packs fixed 1, broke 2.** Retrieval fires on "which HTTP status means Too
Many Requests", returns Win32 and WDK pages, and costs a net answer.

Small, but real, and it is the same mechanism that took win32 from 5/5 to 1/5
under "use ONLY the reference": irrelevant context displacing knowledge the
model already had. The relevance filter reduced this -- an identifier-bearing
query now drops chunks mentioning none of its identifiers -- but a question
naming no identifiers at all has nothing to filter on.

**What this means for a deployment.** The packs are worth installing and the
tax is worth paying: +45 claims on code generation and +92 answers on facts,
against -1 out of 20 elsewhere. But an agent that retrieves on *every* prompt
pays it on every unrelated question, so scope retrieval to the domains the
packs cover where that is cheap to detect.

---

# The last three gaps, closed

## Double-tap on code generation

The obvious omission from the code-generation run: it compared closed book,
retrieval-first and verify-after, but not the strategy that won on single
facts.

| strategy | claims correct |
|---|---|
| closed book | 28 / 100 |
| retrieval-first | 73 / 100 |
| verify-after | 45 / 100 |
| **double-tap** | **74 / 100** |

74 against 73, and `docs_verify` fired on **1 of 25** retrieved answers.
Retrieval had already put the right headers in the code, so there was almost
nothing left to contradict.

That mirrors the single-fact result exactly -- 101 against 101, verification
rescuing 0 -- and settles what double-tap is for. It is **insurance**: worth
its extra calls when retrieval is noisy, worth nothing when retrieval is
clean, and across four runs it has never once lost a claim or an answer. Pay
for it where a wrong answer is expensive, not everywhere.

## algorithms and system-design

Both packs had appeared in no arm of any comparison.

| pack | closed book | with packs |
|---|---|---|
| `algorithms` | 4 / 20 | **18 / 20** |
| `system-design` | 2 / 8 | 3 / 8 |
| total | 6 / 28 | **21 / 28** |

**15 fixed, 0 broken.**

`algorithms` is the strongest per-question result in the project: 4/20 to
18/20. The questions ask which topic directory holds a named implementation --
a fact that exists only as the corpus's own layout, so the model has no way to
know it and the pack states it exactly. That is the ideal shape for a pack:
knowledge that is real, checkable, and absent from training data.

`system-design` barely moves, and the reason is in its size rather than its
quality. Nine case studies, so the questions ask which one addresses a
described problem, and the model can often guess from the description alone --
"design a URL shortener" is recognisably pastebin whether or not you have read
the page. A pack of nine documents has little to add to a model that already
knows the domain.

## Complete picture, every shape measured

| shape | questions | closed book | best with packs |
|---|---|---|---|
| Win32/WDK/scripting/cpp facts | 120 | 46 (38%) | **101 (84%)** |
| MSVC diagnostics | 40 | 3 (8%) | **40 (100%)** |
| multi-claim code generation | 100 claims | 28 (28%) | **74 (74%)** |
| algorithms + system-design | 28 | 6 (21%) | **21 (75%)** |
| **out of domain (control)** | 20 | **16 (80%)** | **15 (75%)** |

Everything the packs cover improves substantially. The one place they cost
something is the control, where a question they were never meant to answer
still triggers retrieval.

## Native function calling changes the answer

The text-protocol loop could not separate two very different failures: a model
that *will not* call tools, and a model that *cannot emit the format*. Adding
an instruction to check first made that harness worse (10/20 -> 4/20), which
pointed at the format.

`/api/chat` with a `tools` schema is the interface the model was trained for.
It emits a structured `tool_calls` entry, so there is no format to break, and
guidance about *when* to call something no longer competes with guidance about
*how* to spell it.

| harness | correct | tasks calling a tool | tool calls |
|---|---|---|---|
| text protocol, free | 10 / 20 | 6 / 20 | 6 |
| text protocol, mandated | **4 / 20** | 1 / 20 | 1 |
| **native, free** | 12 / 20 | 3 / 20 | 3 |
| **native, nudged** | **14 / 20** | **8 / 20** | 8 |

Closed book on the same 20: 8/20.

**Two findings, and the second is the one that matters.**

The instruction that *halved* accuracy under the text protocol *raises* it
under native calling, 12/20 to 14/20, and nearly triples tool use, 3 to 8. The
earlier collapse was the protocol breaking, not the model refusing. A negative
result that reversed once the interface was right -- worth keeping precisely
because it would have justified the wrong conclusion.

But even nudged, with a schema it understands and a system message saying its
recollection is unreliable, **the model calls a tool on only 8 of 20
questions** -- and on this question set nearly every one has a documented
answer it does not know. Hard-coded retrieval reaches 84% on this corpus.
The agent, free to choose, reaches 70% of that.

## What to build in the agent, not the server

The gap is not retrieval quality and not tool descriptions. It is that a
confident model does not think to look. Three things follow:

* **Use the native API.** It is worth two answers and triple the tool use
  before any other change, and it makes instructions actually land.
* **Nudge in a system message.** Cheap, and it moved tool use from 3 to 8.
* **Do not rely on either.** For question shapes where the documentation is
  authoritative and the model is known weak -- headers, libraries, IRQLs,
  diagnostic codes -- call `docs_lookup` in the harness before the model
  answers. Every large gain measured in this document did exactly that, and no
  amount of prompting reproduced it.

---

# Real-world phrasing: the number that qualifies all the others

Every question set so far asked directly -- "which header declares X". Real
questions arrive as symptoms: a bugcheck, a linker error, a leak, a review
comment. 20 scenarios in that shape:

| | closed book | with packs |
|---|---|---|
| total | 13 / 20 | **15 / 20** |

Fixed 3, broke 1. Against 46 -> 101 on directly-phrased questions.

| kind | closed | with packs | why |
|---|---|---|---|
| link-error | 3/3 | 3/3 | the symbol name is IN the error text |
| compile-error | 1/2 | 2/2 | same -- the identifier is quoted |
| leak | 1/2 | 2/2 | the API is named in the description |
| debug | 1/3 | 2/3 | the *answer* is not named, only the symptom |
| design | 0/2 | 0/2 | case studies; nine documents, model guesses anyway |
| msvc | 2/2 | **1/2** | retrieval displaced a correct answer |

**The split is entirely about whether the question names the thing being asked
about.** "Unresolved external symbol __imp_CryptAcquireContextW" carries the
identifier, so `docs_lookup` fires and the answer is exact. "A driver
bugchecks with IRQL_NOT_LESS_OR_EQUAL inside a DPC" names the symptom and not
the routine, so there is nothing to look up and semantic search must bridge
the gap.

**So 38% -> 84% overstates what a deployment will see.** That figure is on
questions that hand retrieval its key. The honest range is: near-total on
symptoms that quote an identifier, marginal on symptoms that do not, and the
mix depends on what your developers paste into the agent. Stack traces, linker
errors and compiler diagnostics quote identifiers, which is the good case and
also the common one.

The one regression is worth naming: on an MSVC question the model had right,
retrieval displaced it. Same mechanism as everywhere else in this document,
still not fully solved by the relevance filter.

---

# The contracts tools, measured -- and the fix that works

`docs_contracts` and `code_contracts` were built from one observation: asked to
review a real minifilter, the model asserted an IRQL it had invented. The
reasoning was that `docs_verify` catches exactly that claim but is invoked
least when the error is most confident, so the facts should arrive *before* the
review instead.

Five real driver files (2,152 lines), four arms, temperature 0. Every claim of
the form "API *requires* IRQL" is extracted and adjudicated against the packs,
so what is counted is objective.

| arm | wrong | asserted | error rate |
|---|---|---|---|
| A closed book | 1 | 1 | **100%** |
| B tools offered, model chooses | 1 | 1 | **100%** |
| C contract sheet injected | 4 | 12 | **33%** |
| **D sheet injected + quote verbatim** | **0** | **18** | **0%** |

## Supplying the facts halves the error rate. Forbidding the paraphrase removes it.

Arm C had the correct contract for every API in the prompt and still got a
third of its claims wrong -- `FltRegisterFilter` asserted as PASSIVE_LEVEL
against a documented `<= APC_LEVEL`, with `<= APC_LEVEL` sitting a few lines
above in the same prompt.

Arm D adds one rule: copy the documented string verbatim, name the API it
belongs to, never restate it in your own words, and if an API is absent say so
rather than supplying a requirement. Eighteen contract claims, none wrong.

The mechanism is that **quoting is a copy and restating is a completion.**
Paraphrasing re-enters generation, where a prior -- "initialisation routine
means PASSIVE_LEVEL" -- competes with the fact and wins about a third of the
time. Copying gives it nowhere to compete.

Arm D is also the *most* assertive arm, 18 claims against arm A's 1, so it did
not earn its score by hedging. That was the failure mode to watch for: a
reviewer that says "check the IRQL here" is never wrong and never useful.

This rule now ships in `SERVER_INSTRUCTIONS`, so every MCP client receives it
at connect time.

## Offering a tool still does nothing

Arm B had native function calling, the server's instructions, and a
`docs_contracts` description reading "use FIRST on any code task". It called
nothing, on all five files, as it called nothing on the three review tasks
before them. Eight tasks, zero calls.

**An agent that must decide to check will not.** Anything the packs contribute
has to be put in front of the model, not offered to it.

## The grader was wrong first, and it inverted the conclusion

An earlier version of this section reported arm D at 10 wrong of 31 and
concluded "retrieval cannot fix a prior". That was the measurement, not the
model.

The claim extractor accepted any API within 120 characters of an IRQL token.
Driver code is full of `NT_ASSERT(KeGetCurrentIrql() == PASSIVE_LEVEL)`, so a
review *correctly* describing that assertion scored as a false claim about
`KeGetCurrentIrql`'s own contract -- a statement about a call site, not a
requirement.

Requiring a contract verb between the API and the level ("must be called at",
"requires", "is callable at"), and excluding the routines that read or change
IRQL as subjects, took arm D from 10 wrong to **0**. The conclusion reversed
completely.

Two things worth keeping from that: a harness that cannot be imported cannot
be unit-tested -- this one ran `asyncio.run(main())` at import, so checking the
regex fired the whole twenty-minute evaluation -- and a grader that has never
been shown a case it should reject is not a grader.

---|---|---|---|
| A closed book | 2 | 2 | **100%** |
| B tools offered, model chooses | 4 | 4 | **100%** |
| C contract sheet injected | 8 | 16 | **50%** |

Two results, and the second is the important one.

## Offering a tool does nothing. Eight tasks, zero calls.

Arm B had native function calling, the server's instructions in the system
message, and a `docs_contracts` description reading "use FIRST on any code
task". It called nothing, on all five files, as it called nothing on the three
review tasks before them.

That is now measured enough times to state plainly: **an agent that must
decide to check will not.** Anything the packs are to contribute has to be put
in front of the model, not offered to it.

## The facts do not help when they ARE in front of it

Arm C had the correct contract for every API in the file, in the prompt, and
still got eight claims wrong:

```
FltRegisterFilter:            said PASSIVE_LEVEL, documented <= APC_LEVEL
ExInitializeLookasideListEx:  said PASSIVE_LEVEL, documented <= DISPATCH_LEVEL
KeGetCurrentIrql:             said PASSIVE_LEVEL, documented Any level
```

The error rate halves, 100% to 50%, which is real. But the absolute count
*rises*, because the sheet emboldens the model to assert four times as many
facts, and half of those are wrong anyway.

**So the diagnosis behind these tools was wrong.** The model was not asserting
PASSIVE_LEVEL because it could not recall the documented value. It asserts
PASSIVE_LEVEL because that is the prior for anything shaped like
initialisation, and a correct value three lines above in the prompt does not
interrupt the completion. Retrieval cannot fix a prior.

In fairness to the model, "PASSIVE_LEVEL" where the documentation says
`<= APC_LEVEL` is an over-restriction rather than an inversion -- PASSIVE_LEVEL
is inside the permitted range. It is still wrong as a contract claim, and it is
exactly the error that flags correct code as buggy.

## What this changes

`docs_contracts` and `code_contracts` stay: halving an error rate is worth
having, they cost one call, and the retrieval they do is correct. But they are
not the anti-hallucination mechanism this document previously implied.

What the numbers actually support:

* **Injection over invitation** -- for every tool, not just these.
* **Do not let a model state a contract in prose.** The reliable path is the
  one already measured at 84%: ask a direct question, take the documented
  string, and put THAT in the output. The moment the model paraphrases a
  contract into a review sentence, its prior competes with the fact and
  sometimes wins.
* **A checkable claim is worth more than a hedge.** Arm C asserted 16 facts to
  arm A's 2. Half were wrong, but all 16 were falsifiable against the packs in
  milliseconds -- which is how this table exists at all. A reviewer that says
  "check the IRQL here" is never wrong and never useful.

---

# The debugger pack, and three ways a build lies

`debugger` covers WinDbg and the Debugging Tools for Windows: the command
reference (`debuggercmds`, 977 pages) and the how-to material (`debugger`,
1,161 pages) in one pack. 2,138 documents, 14,259 chunks, 1,511 symbols, 0
unresolved, 24.8 MB.

It is recorded separately because building it surfaced three failures that
all produced a *plausible success* rather than an error, which is the class
of bug that survives a test suite.

## `api_name` looks like an inventory and is not one

869 of the 977 command pages carry `api_name`. It is two different things:

| title | `api_name` | |
|---|---|---|
| `!analyze (WinDbg)` | `analyze` | clean name, `!` dropped |
| `!pcitree (WinDbg)` | `pcitree` | clean name, `!` dropped |
| `.abandon (Abandon Process)` | `.abandon (Abandon Process)` | title copy |
| `tct (Trace to Next Call...)` | `tct (Trace to Next Call...)` | title copy |

Building on it alone yields an inventory half of which is prose. The sibling
docset is worse: there `api_name` copies the title on *every* page, so
`Activating a Debugging Client` would be indexed as an API name.

Titles are consistent, so the command comes from the title; the bare
`api_name` is kept only as an alias when it is a single token. `docs_lookup`
is exact-match and never fuzzy, so carrying both spellings is what makes
`!analyze` and `analyze` both land.

## A YAML indent that would have shipped an empty inventory

`_BLOCK_ITEM` required leading whitespace. YAML permits a block sequence at
its key's own indentation and this docset uses exactly that -- `api_name:`
then `- analyze` at column 0. Every previously-built repo indents, so the bug
was invisible.

The damage was not limited to aliases. `topic_type` is a block list too, so
the `apiref` gate below rejected every page: the pack would have shipped
**zero symbols** while building, installing and listing without complaint.

## An alias that shadows a real command

`!dt` carries the bare alias `dt` -- which is a different, very common
command, and `dt` cannot invoke `!dt`. Left in, `docs_lookup("dt")` returns
both and the one you asked for is no longer distinguishable from the one you
cannot type that way.

Measured across the corpus: **3 such aliases** (`dt`, `dpa`, `version`) out of
586, against 567 that add a genuinely new spelling. Suppressing an alias that
names *another page's* command drops 3 and keeps 567.

| | symbols |
|---|---|
| before the collision guard | 1,514 |
| after | **1,511** |

## Verified end to end

Through the reference client -- native function calling, Argus's instructions,
`qwen3.6:35b`, the same path Hermes uses:

> **Q.** In WinDbg, which command displays the contents of a structure at an
> address, and what is the command to reload symbols?
>
> **A.** `dt` (Display Type) ... `.reload`, or `.reload /f` to force.

It called `docs_lookup({"name": "dt"})` and received the Display Type page --
the disambiguation the collision guard buys. Adding this ninth pack left the
earlier `FltRegisterFilter` answer unchanged (`<= APC_LEVEL`, `FltMgr.lib`),
so the new corpus does not interfere with the existing ones.

---

# Two models, two arms, ten task families

Does Argus close the gap, and does model size close it instead? Ten task
families -- test development, code review, performance, coding style, SDK,
WDK, win32, scripting, security review, code safety -- one question each, run
against `qwen3.6:27b` (dense, 27.8B) and `qwen3.6:35b` (MoE, 36.0B), closed
book and then with Argus over native function calling.

The two models differ in **architecture as well as size**, so a gap between
them is not a clean parameter-count result and is not reported as one.

| model | arm | pass | tool calls | median |
|---|---|---|---|---|
| `qwen3.6:27b` | closed book | 5 / 10 | 0 | 17.1 s |
| `qwen3.6:27b` | **with Argus** | **10 / 10** | 26 | 22.2 s |
| `qwen3.6:35b` | closed book | 5 / 10 | 0 | 5.5 s |
| `qwen3.6:35b` | **with Argus** | **9 / 10** | 19 | 2.4 s |

## Scale did not help; retrieval did

**Both models scored 5/10 closed book, failing the same five tasks.** Not
similar scores -- the same five, task for task:

| | closed book | with Argus |
|---|---|---|
| coding-style, performance, scripting, sdk-support, code-safety | both PASS | both PASS |
| test-development (`dispatch_level`) | both FAIL | both PASS |
| code-review (`apc_level`) | both FAIL | both PASS |
| wdk-support (`apc_level`) | both FAIL | both PASS |
| win32-support (`fileapi.h`) | both FAIL | both PASS |
| security-review (`ntstrsafe.*`) | both FAIL | 27b PASS, 35b FAIL |

An 8-billion-parameter difference and a different architecture moved nothing.
Both models handled amortized complexity and MSVC flag syntax; both missed
driver IRQLs and the documented header for `CreateFileW` -- which is
`fileapi.h`, not the `windows.h` that memory reaches for. These are recall
failures on facts too specialised to be well represented in either model's
weights, and capacity is not the missing ingredient.

## The one remaining failure is the familiar one

`qwen3.6:35b` failed `security-review` with **zero tool calls, in 2.2
seconds**. Asked what replaces `wcscpy` in KERNEL code, it answered
`wcscpy_s` / `<string.h>` / `ucrt.lib` from memory. That is a real function
and a user-mode answer to a kernel question; the documented replacement lives
in `ntstrsafe.h` / `Ntstrsafe.lib`. The 27b model made 5 calls on the same
task and got it right.

This is the finding that keeps recurring here: **a tool the model must decide
to call will sometimes not be called**, and when that happens the wrong answer
arrives fast and confident. The larger model was more willing to trust itself,
which on this task cost it the point.

Latency inverts with retrieval, and the direction is worth noting: 35b's
median fell from 5.5 s to 2.4 s *with* tools. A looked-up fact is shorter to
produce than a reasoned-out one.

## The answer key is verified before any model runs

`verify_answer_key()` checks all 17 expected tokens against the packs and
refuses to start if one is missing. An answer key written from memory would
score a model wrong for being right -- the exact failure this project exists
to measure. It earned itself immediately: `/std:c++20` raised an FTS5 syntax
error rather than silently reporting "not found", which would have read as a
gap in the corpus and sent someone editing a correct key.

Three defects in the harness were found and fixed before these numbers were
trusted, all of which would have published as findings:

- **401 read as 0/10.** The first full run scored both models 0/10 with zero
  tool calls. Every row held `<ERROR: unhandled errors in a TaskGroup>`, which
  unwrapped to `401 Unauthorized`: the ACL cache had aged 9.6 h past its
  window and Argus correctly denied. Nothing about the models.
- **A `forbid` rule that fired on a correct answer.** `passive_level` was
  forbidden on the lookaside task, but the documented contract is
  `<= DISPATCH_LEVEL`, which *includes* PASSIVE_LEVEL, so a fully correct
  answer names both. Removed: the wrong answer is PASSIVE_LEVEL *instead of*
  DISPATCH_LEVEL, which the `expect` check already catches.
- **A re-grade that manufactured a failure.** Re-scoring stored answers
  flipped a real 35b PASS to FAIL, because that record was exactly 1500
  characters -- the storage cap -- and `wdm.h` sat past the cutoff. The
  run-time grade against the full text was right. Cap raised to 8000.

Reproduce with [`evals/run_model_bench.py`](../evals/run_model_bench.py);
results in [`evals/model-bench-results.json`](../evals/model-bench-results.json).

---

# Phase 4 on real code: 76,636 vectors

`semantic_search` shipped with unit tests and no evidence on a real corpus.
This is that evidence. The index holds 286,785 symbols across postgres,
openssl, git, curl, redis and freetype; 76,636 are public with a signature and
therefore embeddable.

| | |
|---|---|
| symbols indexed | 286,785 |
| embeddable (public, signed) | **76,636** |
| vectors built | 76,636 bin + 76,636 int8, **0 stale** |
| index growth | 224.3 MB -> 305.2 MB |

76,636 lands inside the "~70-90k vectors instead of ~600k" the design
predicted from embedding signatures rather than bodies, which is the first
confirmation that estimate was not wishful.

## Retrieval, hand-checked

Six description-shaped questions -- no identifier in any of them, which is
exactly the shape `find_symbol` cannot answer and `search_code` answers only
if you guess the source's words.

| question | top hit | |
|---|---|---|
| compute a SHA-256 hash of a buffer | `sha256_final` (redis `src/sha256.c`) | partial |
| parse a URL into scheme host and path | `OSSL_parse_url` | **exact** |
| acquire a lightweight lock on a shared memory buffer | `LockBufferForCleanup` | partial |
| expire keys that have passed their time to live | `expireSlaveKeys` (`src/expire.c`) | partial |
| render a glyph outline into a bitmap | `FT_Glyph_To_Bitmap` | **exact** |
| verify a certificate chain against trusted roots | `ssl_verify_cert_chain` | **exact** |

**3 exact, 3 partial, 0 wrong. Every question landed in the right file.**

The three partials share one shape, and it is the same limitation the code
packs showed: **the index matches vocabulary, not role.** Asked to expire
keys past their TTL it returned `expireSlaveKeys` from `src/expire.c` rather
than `activeExpireCycle` from the same file -- both are expiry functions whose
signature and path say "expire", and nothing in the embedded text says which
one is the main loop and which is the replica path. Same for
`LockBufferForCleanup` against `LWLockAcquire`, and `sha256_final` against a
one-shot hash.

That is worth stating plainly rather than tuning away on six questions: this
tool reliably answers "which file handles X", and answers "which exact
function" about half the time. Landing in the right file is most of the value
when the alternative is grepping a 2,400-file repository, but it is not the
same claim as an exact lookup, and `find_symbol` remains the tool for that.

## Latency

| stage | |
|---|---|
| query embedding | **2,260 ms** median |
| vector search over 76,636 symbols | **310 ms** median |

The split is the same one the packs showed and the same conclusion follows:
**the embedder sets the latency a user feels, not the index.** Search over
76,636 private symbols costs 310 ms; producing the query vector costs 7x that
on CPU-only Ollama. A GPU or a smaller embedding model is the lever, and it is
hardware rather than code.

## Not yet measured

The ACL post-filter recall limit. `vec0` KNN cannot join, so candidates are
retrieved globally and then restricted, which means a caller whose allowlist
is a small slice of the corpus can receive fewer hits than exist for them.
Every query above ran with all 12 repositories visible. How narrow an
allowlist has to be before this bites is documented in `semantic_search`'s
docstring as a known cost and remains unobserved.

---

# Milestone 2.1: does forcing the check help?

The failure this arm was built for: `qwen3.6:35b` answered a kernel-safety
question in 2.2 s with **zero tool calls**, confidently and wrongly, while the
tools sat unused in its schema list. Offering a tool is not the same as the
model using one.

`verify-after` removes the choice. The model drafts closed book, `docs_verify`
runs on that draft whether it wanted it or not, and only what the
documentation CONTRADICTS is fed back. Silence leaves the draft untouched --
re-prompting on silence invites a correct answer to become a different one.

| model | closed book | with Argus | verify-after |
|---|---|---|---|
| `qwen3.6:27b` | 5/10 @ 16.8 s | **10/10** @ 17.7 s | 9/10 @ 29.1 s |
| `qwen3.6:35b` | 5/10 @ 5.2 s | 9/10 @ 2.3 s | **10/10** @ 13.3 s |

**The target failure is fixed.** 35b passes `security-review` under forced
verification, and every row records `calls=1` -- the check happened on every
task, which is the property the arm exists to guarantee.

## Neither arm dominates, and that is the finding

Verify-after gained 35b its missing task and cost 27b one -- the same
`security-review`, which 27b had passed with-argus using 5 tool calls.

The mechanism explains both directions. Verify-after guarantees exactly ONE
check, of a finished draft. With-argus allows MANY targeted lookups, but only
if the model chooses to make them. So verify-after rescues a model that will
not look, and under-serves one that would have looked repeatedly: `docs_verify`
speaks only where the packs contradict, so a draft wrong in a way the
documentation does not directly deny survives.

They fix different failures. That argues for combining them -- offer the tools
AND verify the result -- rather than choosing, which is the next thing to
measure rather than assume.

## Cost

Forced verification is not free: 35b's median goes 2.3 s -> 13.3 s, because
every question now pays a verification round trip including the six it already
answered correctly from memory. Against a model that answers wrong in 2.2 s,
that is a good trade. Against 27b, which already checks, it buys nothing and
costs 11 s.

## Three arms, and three bogus tables before them

The first verify-after run reported 16 of 20 rows as FAIL and 35b at 0/10.
Every one was `<ERROR: unhandled errors in a TaskGroup>`: the Argus server had
died mid-run. Read at face value it was a dramatic, entirely fictional result.

That was the third infrastructure failure to wear a FAIL badge on this bench --
a 401 from an aged ACL cache, a timeout constant, and now a dead server. The
tell each time was a pattern too clean to be real: contiguous failures from one
point onward, `calls=0` on all of them, and a model failing tasks it had
already passed. Models do not fail in blocks.

Fixed as a class rather than a symptom. `anyio` wraps every transport failure
in a TaskGroup whose `str()` is "unhandled errors in a TaskGroup (1
sub-exception)", which says nothing; the harness now unwraps to the real
exception, records **ERROR** as a verdict distinct from FAIL, and excludes
those rows from the denominator. A shrunken denominator is visible. A silent
FAIL is not.

---

# The ACL recall limit, finally observed

`semantic_search` filters by allowlist AFTER the vector scan, because `vec0`
KNN cannot join. That was documented from the start as a known cost -- a
caller seeing a small slice of the corpus could lose hits to a globally-spent
budget -- and never once observed. Every measurement so far ran with all 12
repositories visible.

Measured now, and the documented worry is the wrong shape.

## It turns on topical alignment, not allowlist size

Query: *"read and decode a compressed image file header"*, `limit=10`,
default `coarse=600`.

| allowlist | share of corpus | hits |
|---|---|---|
| `postgres` | 33.9% | 10/10 |
| `openssl` | 22.8% | 10/10 |
| `redis` | 14.1% | 10/10 |
| `curl` | 5.6% | 10/10 |
| `zlib` | 1.2% | 10/10 |
| **`libpng`** | **0.9%** | **10/10** |

No starvation anywhere, including a repo holding 693 of 76,636 vectors.
Relevant vectors rank high *globally*, so they survive a global budget however
small their repository is.

Starvation appears only when the allowlist has nothing to do with the query:

| query aligned with | restricted to | coarse=600 | coarse=4000 |
|---|---|---|---|
| postgres | `libpng` (0.9%) | **0/10** | 1/10 |
| postgres | `zlib` (1.2%) | 1/10 | 10/10 |
| openssl | `libpng` (0.9%) | 1/10 | 10/10 |
| libpng | `postgres` (33.9%) | 10/10 | 10/10 |

## And where it starves, the missing results are noise

The obvious reading of that table is "raise `coarse` and recover recall". The
scores say otherwise.

Restricting *"vacuum dead tuples from a database table"* to zlib:

| | hits | scores |
|---|---|---|
| `coarse=600` | 1 | 0.543 |
| `coarse=4000` | 5 | 0.546 - 0.579 |

The five rescued hits are `_tr_flush_bits`, `_tr_flush_block`, `bi_flush` and
a `Dispose` method -- zlib's least-distant vectors for a question zlib cannot
answer. Contrast a question it can:

| | hits | scores |
|---|---|---|
| *"inflate a deflate compressed stream"*, `coarse=600` | 5 | **0.720 - 0.738** |

`inflateCopy`, `inflateSync`, `inflateInit_`. No starvation, at the default
budget, in a repo that is 1.2% of the corpus.

**So the starved case is one where fewer results is the correct answer.**
A ~0.55 score means "nothing here matches"; ~0.72 and up means a real hit.
Turning the dial up converts an honest empty result into four irrelevant ones.

## What this closes

The two fixes this "known cost" would have justified -- raising
`SEMANTIC_COARSE`, or adding `repo_id` as a `vec0` metadata column so the
filter runs inside the scan -- would both buy noise at more expense. Neither
is worth doing, and now there is a measurement saying so rather than an
assumption either way.

Milestone 2 is closed: 2.1 measured forced verify-after and found its
boundary, 2.2 built 76,636 vectors and hand-checked them, and the one
property left unobserved turns out not to bite.

---

# What a pack refresh actually costs

The roadmap said a `win32` refresh costs 162 minutes and used that to justify
building incremental rebuild. That number is the **cold** build, and the
embedding cache already existed, so the premise needed checking before writing
change-detection nobody needs.

Measured: rebuild each pack from an unchanged source, cache warm.

| pack | chunks | warm rebuild | chunks/sec |
|---|---|---|---|
| `system-design` | 442 | 2.7 s | 167 |
| `algorithms` | 2,001 | 6.2 s | 322 |
| `sqlite` | 8,987 | 28.0 s | 321 |
| `cppreference` | 68,891 | **340 s** | **203** |

**The rate is not flat.** Small packs pay a fixed overhead that dominates;
large ones lose about 37% of the mid-range rate. Predicting `cppreference`
from the 320 chunks/sec of the two packs below it gave 215 s against 340 s
actual -- so the extrapolation below uses the measured large-pack rate, not
the peak.

| pack | chunks | cold | warm (est. 203 c/s) | speedup |
|---|---|---|---|---|
| `cpp` | 123,212 | 36 min | 10.1 min | 3.6x |
| `wdk` | 245,727 | 74 min | 20.2 min | 3.7x |
| `win32` | 530,559 | **162 min** | **43.6 min** | 3.7x |
| total | 899,498 | 272 min | **73.9 min** | 3.7x |

## Both halves of the roadmap's claim were wrong

**162 minutes was never the refresh cost.** The embedding cache already turns
it into ~44, without anyone building anything. Quoting the cold figure
overstated the problem by 3.7x.

**But "nearly free" was also wrong.** A `debugger` rebuild reporting
`14,259 reused, 0 computed` in seconds made the cache look total; at 530,559
chunks the same mechanism still costs 44 minutes. The cache eliminates
embedding, and embedding was not the only cost.

## Where the remaining time goes

Not embedding -- every vector was a cache hit. What is left is work done once
per chunk regardless: re-parsing 71,663 documents, re-chunking to 530,559
pieces, 530,559 lookups against a 100.8 MB cache, and writing a 786 MB pack
with its FTS index and vector tables.

All of it to produce a file identical to the one already on disk.

## So incremental rebuild is still worth building, for a smaller reason

The target is no longer "162 minutes to 5". It is **44 minutes to seconds when
upstream changed 50 documents out of 71,663** -- which is what a documentation
refresh actually looks like.

That needs a per-document content hash stored in the pack and compared against
the source, so unchanged documents keep their existing chunks, symbols and
vectors instead of being rebuilt into the same bytes. The saving comes from
skipping documents, not from skipping embedding, which the cache already does.
