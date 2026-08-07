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
| files indexed | 1,199 (1,024 skipped: binary, oversized, excluded dirs) |
| symbols | 33,102 |
| includes | 3,422 |
| errors | **0** |
| wall clock | **14.8 s** cold, full pass |
| `index.db` | 32.4 MB |

Per repo: libjpeg-turbo 394 files / 3.7 s, freetype 536 / 4.8 s, libpng 134 /
1.8 s, zlib 127 / 1.5 s.

### Include resolution

| state | count | share |
|---|---|---|
| resolved | 2,247 | 65.7% |
| external | 792 | 23.1% |
| not found | 342 | 10.0% |
| **ambiguous** | **41** | **1.2%** |

**The ambiguous rate is the good news.** The design's stated worry was that
many repos shipping headers with the same basename would defeat suffix
matching. At 1.2% across four real projects, it does not. `freetype` accounts
for 40 of the 41.

`external` is system headers (`stdio.h`, `string.h`) — expected. `not_found`
is dominated by generated headers and platform-conditional includes.

## The defect this run found

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
| `libjpeg-turbo -> zlib` | 1 | yes |
| `freetype -> libpng` | 1 | yes |
| *(two seeded toy edges)* | 1 each | yes |

Five edges, all correct. Cost of the fix: exactly two includes reclassified
(resolved 2,249 → 2,247). Precise, not blunt.

## `which_repo` accuracy

Ten questions, one per input shape, hand-checked against repos whose correct
answer is known.

**9 of 10 top-1 correct.** Latency **0.5 ms median, 0.9 ms max**.

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

- **Scale.** 1,199 files is not 3M lines. Nothing here justifies or refutes
  Postgres; `index.db` at 32.4 MB for this corpus extrapolates poorly.
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
