# First indexing measurements

The Step 0 run from `docs/roadmap.md`. Production GitLab was not reachable, so
the test GitLab was seeded with **four real public C projects** instead of toy
fixtures. That substitution matters: the whole point of Phase 3 is resolving
`#include` edges across real header layouts, and hand-written fixtures cannot
produce a vendored copy, a generated header, or a `contrib/` tree.

Repos chosen for genuine cross-repo dependencies: libpng includes `zlib.h`,
freetype includes `png.h`.

Everything below is measured. The failures are recorded because they are the
point of the exercise.

## The corpus, and why it is pinned

```bash
python deploy/test-gitlab/seed_corpus.py --tier baseline   # ~50 s
python -m argus.cli index --config deploy/test-gitlab/work/config.yaml
```

| project | ref |
|---|---|
| zlib | `v1.3.1` |
| libpng | `v1.6.44` |
| freetype | `VER-2-13-3` |
| libjpeg-turbo | `3.0.4` |

**The first version of this corpus was not reproducible, and that was not
harmless.** It was cloned from each project's default branch and squashed to a
single commit, so the upstream revision was gone — the surviving clones
reported `1.3.2.1-motley` and `1.8.0.git`, moving development versions rather
than releases. Re-cloning a month later gives different code, so a changed
number could not be attributed to a change in Argus rather than a change
upstream. Every figure on this page was, in that sense, an anecdote.

Re-pinning to release tags changed real numbers, which is the proof it
mattered:

| | master corpus | pinned releases |
|---|---|---|
| files | 1,199 | 1,026 |
| resolved | 2,247 | 2,205 |
| not found | 342 | 230 |
| cross-repo edges | 5 | 4 |

The largest single cause: **libjpeg-turbo 3.0.4 does not vendor zlib at all.**
`src/spng/zlib/` existed only on master. One of the two bundled copies that
motivated the vendored-copy detection is not in a release — which is itself
worth knowing, and is exactly the kind of fact an unpinned corpus hides.

## Precondition

The roadmap requires checking this before trusting any number, because
`argus/gitlab.py` enumerates with `membership=false`, which for a **non-admin**
token returns only public projects.

```
token user: root   is_admin: True
membership=false sees 7 projects
the 4 private real repos visible: 4/4  OK
```

Passed here. **This remains unverified against a production instance**, where
the service token may not be an admin.

## The run

| | |
|---|---|
| repos | 7 (4 real + 3 seeded toys) |
| files indexed | 1,026 (1,092 skipped: binary, oversized, excluded dirs) |
| symbols | 35,948 |
| includes | 3,210 |
| errors | **0** |
| wall clock | **20.4 s** cold, full pass |
| `index.db` | 29.1 MB |

Per repo: freetype 518 files / 7.8 s, libjpeg-turbo 280 / 4.3 s, libpng 117 /
2.1 s, zlib 103 / 2.2 s.

### Include resolution

| state | count | share |
|---|---|---|
| resolved | 2,205 | 68.7% |
| external | 734 | 22.9% |
| not found | 230 | 7.2% |
| **ambiguous** | **41** | **1.3%** |

**The ambiguous rate is the good news.** The design's stated worry was that
many repos shipping headers with the same basename would defeat suffix
matching. At 1.3% across four real projects, it does not. `freetype` accounts
for 40 of the 41.

`external` is system headers (`stdio.h`, `string.h`) — expected. `not_found`
is dominated by generated headers and platform-conditional includes.

## The defect this run found

*(Measured on the original master corpus. Kept because the mechanism is the
finding, and because release 3.0.4 no longer contains the tree that caused
it — which makes it exactly the kind of defect a pinned corpus stops
reproducing and an unpinned one stops explaining.)*

The graph came out with **6 cross-repo edges, one of which was false**:
`zlib -> libjpeg-turbo`, in a graph where zlib depends on nothing.

The mechanism generalises well beyond this instance:

- zlib's own `zconf.h` is **generated at build time** and absent from its
  source tree.
- libjpeg-turbo **vendors an entire copy of zlib** at `src/spng/zlib/`.
- That copy was therefore the *only* candidate for `#include "zconf.h"`, so
  the ambiguity guard never fired and the include resolved with full
  confidence into an unrelated repository.

**The resolver guarded against too many candidates. It had no guard against
the only candidate being the wrong one.** A unique match is not evidence of
correctness when the canonical file is missing, and vendored copies of common
libraries are everywhere in C — freetype carries one too, under `src/gzip/`.

Fixed by dropping candidates that sit under a directory named after a
*different* indexed repository. The first version of that check ignored which
repo owned the candidate and flagged `eal/include/eal/eal_thread.h` — a
library namespacing headers under its own name, the most ordinary layout in C.
Three existing tests caught it. Without them it would have erased legitimate
edges across every well-organised project.

### The graph, after

| edge | weight | correct |
|---|---|---|
| `libpng -> zlib` | 16 | yes — the real dependency |
| `freetype -> libpng` | 1 | yes |
| *(two seeded toy edges)* | 1 each | yes |

Four edges, all correct. On the master corpus there was a fifth,
`libjpeg-turbo -> zlib`, which was also correct — release 3.0.4 simply does
not carry the vendored zlib that produced it.

The fix that removed the *false* edge cost exactly two includes reclassified
(measured on the master corpus: resolved 2,249 → 2,247). Precise, not blunt.

## `which_repo` accuracy

Ten questions, one per input shape, hand-checked against repos whose correct
answer is known.

**9 of 10 top-1 correct.** Latency **0.8 ms median, 1.9 ms max**.

| shape | question | expected | got |
|---|---|---|---|
| symbol | `png_read_image` | libpng | libpng |
| symbol | `deflateInit2_` | zlib | zlib |
| symbol | `jpeg_start_decompress` | libjpeg-turbo | libjpeg-turbo |
| symbol | `FT_Load_Glyph` | freetype | freetype |
| prose | fix a crash decoding a progressive jpeg | libjpeg-turbo | libjpeg-turbo |
| prose | adjust the deflate compression level | zlib | **libpng** |
| prose | add support for a new font hinting mode | freetype | freetype |
| stack | `png_do_expand (pngrtran.c:1234)` | libpng | libpng |
| stack | `inflate (inflate.c:700)` | zlib | zlib |
| diff | `src/psaux/psintrp.c` | freetype | freetype |

It was **7 of 10** before the first vendored-copy fix and **8 of 10** before
the second. `deflateInit2_` resolved to libjpeg-turbo, because its vendored
zlib genuinely defines the symbol. Evidence found inside a vendored copy is
worth 0.2 of canonical evidence — down-weighted rather than dropped, because
unlike a false graph edge it is a *real* definition, just not the one you
would edit.

### How `inflate.c` was fixed, and the approach that did not work

freetype vendors zlib under `src/gzip/`, a directory named after neither
repository. The earlier note here predicted that catching it needed
**content-level duplicate detection**. That prediction was tested and is
**wrong**:

| file | blob sha | size |
|---|---|---|
| `zlib/inflate.c` | `5f5d4922b715` | 53,660 |
| `libjpeg-turbo/src/spng/zlib/inflate.c` | `5f5d4922b715` | 53,660 |
| `freetype/src/gzip/inflate.c` | `c8125680b0c9` | 57,147 |

Byte-identical for libjpeg-turbo, and **not** for freetype — the copy was
modified. Across the whole index only 10 blob shas span repos at all. Hashing
would have fixed exactly the case that was already fixed.

What survives modification is the **filename cluster**. freetype's `src/gzip`
holds 88% of zlib's names; libjpeg-turbo's `src/spng/zlib` holds 95%.

The trap is that overlap is symmetric, so the same measurement reports
`zlib/(root)` at 74% "matching libjpeg-turbo" — **the original flagged as the
copy**. Dropping zlib's canonical files from resolution would be far worse
than the bug. The discriminator is **depth**: a bundled copy sits deeper than
the repo that owns those names (zlib at depth 0, freetype's at 2,
libjpeg-turbo's at 3). `find_vendored_dirs()` requires ≥4 shared names, ≥60%
of the directory, *and* that the resembled repo holds them nearer its own
root. The verdict is persisted to `files.is_vendored` because detection needs
every path at once and cannot run per query.

### Two defects the pinned corpus exposed

Re-pinning briefly dropped the score to 8/10, and chasing that one question
found a defect far larger than the question.

**A filename was never looked up as a file.** `which_repo("inflate.c")`
returned **nothing at all**. `extract_paths` recognised only diff headers and
stack frames, so a filename in ordinary prose was not a path; and
`find_symbol("inflate.c")` matches no symbol, because a filename is not one.
It fell between the two and produced no evidence.

An empty result is not a small failure here. `which_repo` returns `[]`
deliberately, to avoid presenting weak matches as an answer — so the caller
reads `[]` as *"that code is not indexed"*. A confident denial is worse than a
wrong repo, and every `.c` and `.h` name a developer could type produced one.

A second, compounding bug in `extract_symbols`: tokens were split on `.` and
qualified on the **last** part. That is right for `std::vector` and
`obj.method`, and catastrophic for `inflate.c`, whose last part `c` fails the
three-character minimum — so the token was discarded outright. Qualifying on
*any* part fixes it while leaving the qualified-name cases alone.

**Both are fixed and both are guarded**, and neither was reachable from any
fixture: it took a real corpus where a filename is the obvious thing to ask
about.

**What the questions were actually testing.** `detect_shape` classifies
`src/psaux/psintrp.c` as **prose**, not diff, and
`png_do_expand (pngrtran.c:1234)` as **prose**, not stack — a diff needs a
`diff --git` header and a stack needs two frames. So the claim that these ten
questions cover "one per input shape" was **false**: eight of the ten
exercised the prose path. The labels are kept below because they describe
what a developer would type, but they do not describe which code path runs.
Real diff and stack inputs are covered by unit tests, not here.

**How the miss surfaced.** With `psintrp` discarded, the query reduced to the
path component `src` — and libjpeg-turbo 3.0.4 bundles **minified jQuery**
under `doc/html/`, where ctags extracts a JavaScript property named `src`.
Bundled third-party JavaScript outranked freetype's real evidence. Fixing the
extraction removed the miss without touching a weight, but the underlying
observation stands: minified vendored assets contribute junk symbols, and
nothing currently excludes them.

### The remaining miss

**"adjust the deflate compression level" → libpng.** libpng has `compression`
struct members; zlib scored 0.50 behind it. This is the prose path, which is
the weakest by design: three of the four input shapes need no embeddings, so
`which_repo` ships without them and prose is what Phase 4 improves. Not tuned
away — adjusting weights after seeing ten questions is how you fit to ten
questions.

## What this does and does not settle

**Settled:**

- The pipeline runs end to end on real code with zero errors.
- Suffix matching is not defeated by real header layouts (1.2% ambiguous).
- Cross-repo edges are recoverable from `#include` alone, with no build system.
- `which_repo` is fast enough to be invisible (sub-millisecond).

**Not settled — needs a production instance:**

- **Scale.** 1,026 files is not 3M lines. Nothing here justifies or refutes
  Postgres; `index.db` at 29.1 MB for this corpus extrapolates poorly.
- **The enumeration precondition**, against a token that may not be an admin.
- **`_LEXICAL_SMOOTHING` and `_FLOOR_RATIO`**, still reasoned defaults. Ten
  questions is not a tuning set.
- Whether vendoring is as common in a single organisation's repos as it is
  across independent open-source projects. It may be rarer — or, in a vendored
  monorepo-of-repos, considerably worse.

## Reproducing

```bash
python scratchpad/seed_real_repos.py     # clone + push the four projects
argus index   --config ~/.argus-measure/argus.yml
argus resolve --config ~/.argus-measure/argus.yml
```

---

# The scale run

The baseline corpus is four small C libraries. Everything about cost and
correctness at size was extrapolation until this run. Nine real projects, all
at pinned release tags, seeded with
`deploy/test-gitlab/seed_corpus.py --tier scale`.

| | baseline | scale | factor |
|---|---|---|---|
| repos | 7 | 12 | 1.7× |
| files indexed | 1,026 | **10,212** | 10.0× |
| symbols | 35,948 | 286,785 | 8.0× |
| includes | 3,210 | 46,815 | 14.6× |
| `index.db` | 29.1 MB | 224 MB | 7.7× |
| MB per 1k files | 28.4 | **21.9** | 0.77× |
| cold full pass | 20.4 s | 7 m 48 s | 23× |
| errors | 0 | **0** | |

Added: curl `curl-8_11_0`, redis `7.4.1`, git `v2.47.0`,
openssl `openssl-3.4.0`, postgres `REL_17_2`, ffmpeg `n7.1`.

**Ambiguous includes fell to 0.09%** (43 of 46,815), from 1.3% on four repos.
The design's stated worry — that many repos shipping headers with the same
basename would defeat suffix matching — gets *better* with scale, not worse.
Storage per file improved too, because a bigger corpus amortises the FTS
dictionary.

**Indexing throughput degraded**: zlib 40 files/s against postgres 12 files/s.
Some of that is file size, and it is not yet isolated. Do not extrapolate a
cold-pass estimate for a real estate from the total until it is.

## The path lookup was linear in corpus size

| | 1,026 files | 10,212 files |
|---|---|---|
| `which_repo` p50 | 0.37 ms | 0.91 ms |
| `which_repo` p95 | 1.58 ms | **15.5 ms** |

Effectively all of the p95 was one query shape. `_files_named` cost 15.1 ms
whether it returned one row or three — cost independent of result size is the
signature of a scan. `path LIKE '%/' || ?` has a leading wildcard, so no index
on `path` can serve it.

Migration 010 adds an indexed `basename` generated column. After: **0.007 ms**,
identical rows, and p95 back to 1.9 ms. See the migration for why it is a
virtual generated column rather than a real one with a backfill.

## The dependency graph was 67% wrong

The scale run produced **42 cross-repo edges**. These are famous projects, so
ground truth is knowable — and hand-checking every edge found **28 entirely
spurious**, including `zlib -> postgres`, `libpng -> git` and
`libjpeg-turbo -> openssl`. zlib depends on nothing at all.

This was invisible at four repos and would have been invisible at a hundred
without someone reading the list.

### Mechanism 1 — system headers, 90.1% of all cross-repo resolutions

`EXTERNAL` was only reached when *nothing* matched. So any repository shipping
a file named `string.h` captured every `#include <string.h>` in the corpus.
postgres ships `src/include/port/win32/` — a tree of POSIX shims (`sys/socket.h`,
`netdb.h`, `dlfcn.h`) — plus `src/include/common/string.h`, and became a sink:

```
503 x  #include <string.h>   openssl/apps/*.c  ->  postgres/src/include/common/string.h
```

That single include text created `openssl -> postgres` at weight 555.

**This is the `zconf.h` defect a second time.** Its lesson was already written
into this file — *a unique match is not evidence of correctness when the
canonical file is missing* — but the fix was scoped to vendored directories.
`<string.h>`'s canonical file is missing for the same reason: it is in libc,
not in any indexed repo. The same failure walked in through a different door.

Fixed with a closed set of C/POSIX/platform header names, consulted **only
when the including repo has no candidate of its own**. That ordering is what
makes the list safe to be generous with: a project that legitimately ships
`param.h` or `stat.h` and includes it via `-I` still resolves locally, so the
rule can never cost a real intra-repo edge — all it can suppress is a
cross-repo claim, and for these names a cross-repo claim is the bug.

### Mechanism 2 — more local evidence made the answer worse

postgres's vendored snowball code does `#include "header.h"`, and postgres
ships two of them. Because there were *two*, the same-repo branch — which
required exactly one — was skipped, and control fell through to a **global**
shortest-path tiebreak. curl's `include/curl/header.h` at depth 2 beat
postgres's own at depth 3.

Fifty includes, one fabricated `postgres -> curl` dependency, and the cause was
that the including repo had *more* candidates rather than fewer. Fixed by
restricting candidates to the including repo whenever it has any: the answer is
one of those, or it is nothing.

### After

**42 edges → 25**, with every large false edge gone (555, 313, 50 → 0).

| | before | after |
|---|---|---|
| edges | 42 | 25 |
| entirely spurious | 28 (67%) | 12 (48%) |
| worst false edge weight | 555 | 8 |

Every genuine cross-repo include now carries the library's own namespace
(`openssl/evp.h`, `curl/curl.h`, `eal/decoder.h`) or is its canonical header
(`zlib.h`, `png.h`).

### The residue, not fixed

Twenty-one includes still cross a boundary on a generic single-segment name:
`config.h` (8), `glib.h` (3), `alloc.h` (3), `atomic.h` (2), and singletons
`builtin.h`, `builtins.h`, `mem.h`, `version.h`, `port.h`. Ten of the twelve
remaining false edges have weight 1.

**Deliberately left alone.** Extending the closed list with `config.h`,
`mem.h` and `port.h` would be fitting a name list to nine repositories — the
same mistake as tuning a retrieval constant against ten questions. It needs
its own measurement across a different corpus, not another entry bolted on to
the one that produced it.

# The 47-repo estate: what only shows up at scale

Every corpus before this one held at most six repositories. Two defects were
invisible there and are the normal case here.

## The corpus

| | |
|---|---|
| repositories | 47 |
| files indexed | 55,603 |
| symbols | 1,491,167 |
| wall time | 37.8 min |
| slowest repo | 228 s (cmake, 15,214 files) |
| failures, timeouts, symbol failures | **0** |
| includes | 160,650 resolved, 53,968 external, 2,596 ambiguous, 9,744 not found |
| cross-repo edges | 242 |

Indexing itself scaled without incident. Nothing timed out against the 3600 s
budget; the worst repository used 228 s of it.

## A symbol name stops being unique

| | |
|---|---|
| distinct symbol names | 733,481 |
| names in more than one repo | **59,465 (8.1%)** |
| most-shared name | `main`, in **43 of 47 repos** |

`data`, `end`, `name` and `next` each span 42. Any tool that returns "the"
definition of a name is choosing, and at six repositories it was mostly
choosing correctly by accident.

## Defect 1: the confidence clamp destroyed the ranking

`which_repo` scored candidates, then sorted on
`round(min(score, 1.0), 3)` -- the value clamped for display. Every score
above 1.0 therefore compared equal, and the tie-break was the repository's
name, alphabetically.

Asked to "compress a byte stream with a dictionary", lz4, zlib and zstd all
scored exactly `1.000`. lz4 won because `l` sorts before `z`. Three plausible
repositories, separated by real evidence, collapsed into one arbitrary pick.

Fixed by ranking on the raw score and keeping the clamp for display only.
zstd moved from third to second, which is the proof the raw scores differed
all along. **It did not change the score: 5/10 before, 5/10 after.** Correct
ordering was not the thing standing between the question and its answer.

## Defect 2: common English words are identifiers everywhere

The remaining five failures share one shape, and it is worse than a ranking
bug. Asked to "store key-value pairs in memory with expiry", the winners were
abseil-cpp (a function named `key`), bullet3 (a member named `store`) and
freetype (`store`, `memory`). redis did not place at all.

`which_repo`'s lexical evidence matches query words against identifiers. At
1.5M symbols, "store", "key", "memory", "data" and "next" are identifiers in
nearly every repository, so they carry no discriminating power while still
scoring. The larger the estate, the more repositories match on words that
mean nothing -- the score rises with corpus size rather than with relevance.

This is the same shape as the pack-side finding that global term frequency
must down-weight common words, and the fix is likely the same: weight an
identifier match by how many repositories contain that identifier. Not
attempted here; recorded with the corpus that makes it measurable.

## Two operational notes

`llvm-project` is absent. At ~2 GB even shallow, its clone failed with
`unexpected eof` on ten consecutive attempts, and `http.postBuffer` and
`http.lowSpeedLimit` tuning did not help. An estate of 47 measures collision
and routing exactly as well.

The seeder used to stop at the first clone failure, so php-src -- large, and
failing the same way -- prevented six small repositories listed after it from
being attempted at all. Failures are now collected and reported at the end.
