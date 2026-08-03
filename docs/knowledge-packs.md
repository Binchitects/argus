# Knowledge packs

A knowledge pack is a single file containing a public documentation corpus —
prose, API symbols, and embeddings — ready to search. Build one once, share it,
and everyone else skips the build.

Packs are entirely separate from the private code index. They hold public
documentation, so there is no access control on them and no per-user
filtering: `docs_lookup` and `docs_search` take no identity, and a test asserts
they cannot reach the private index even by mistake.

## What a pack contains

| Table | Holds |
|---|---|
| `pack_meta` | provenance, licence, attribution, embedding model, counts |
| `docs` | one row per page: title, canonical URL, zstd-compressed body |
| `chunks` | heading-aware slices, each carrying its heading trail |
| `api_symbols` | exact name → page + anchor, from the upstream project's own index |
| `docs_fts` | FTS5 terms for lexical search |
| `vec_bin` | 96 bytes/chunk — the coarse Hamming pass |
| `vec_i8` | 768 bytes/chunk — read only to rescore the coarse pass's candidates |

float32 vectors would be 3072 bytes per chunk. At a million chunks that is the
difference between a ~96 MB scan and a ~3 GB one, which is what makes a pack a
file you download rather than a service you host.

## Using packs

Everything below works without a GitLab config. `--packs-dir` exists precisely
so the public tooling stands alone; `--config` is also accepted and reads
`packs.dir` from it.

```bash
argus pack list --packs-dir ~/.argus/packs
```

```bash
argus pack install https://example.org/python-3.13.arguspack --sha256 <digest> --packs-dir ~/.argus/packs
```

A pack that fails its checksum is **not installed** — no file, no registry
entry. A truncated download silently becoming a half-empty knowledge base is
the failure this check exists to prevent, so always pass `--sha256` when
installing from a URL.

```bash
argus pack info python --packs-dir ~/.argus/packs
```

`info` prints provenance, licence and attribution in full. **That output is how
you meet the redistribution obligation** — a pack is built from someone else's
documentation, and it stays redistributable only while it says whose it is and
under what terms.

```bash
argus pack remove python --packs-dir ~/.argus/packs
argus pack update --index-url https://example.org/packs/index.json --packs-dir ~/.argus/packs
```

## Building a pack

Requires Ollama running with the pinned embedding model pulled:

```bash
ollama pull nomic-embed-text
```

```bash
argus pack build --source python --work-dir /tmp/cpython --out python-3.13.arguspack --version 3.13 --fetch
```

`--fetch` clones or updates the source first. Without it, `--work-dir` must
already be a checkout **whose repository root is that directory** — `git
rev-parse` searches upwards, so a work directory merely sitting inside another
repository would otherwise record that repository's commit as the pack's
provenance.

Available sources: `python` (CPython, PSF-2.0), `react` (react.dev, CC-BY-4.0).

A build refuses to produce a pack whose source records no licence,
licence URL, or attribution. It also writes to a temporary file and renames on
success, so a failed build leaves no output and does not destroy an existing
pack at that path.

## The embedding model is pinned

Every pack records the model and dimension it was built with, and a pack built
with a different model is refused for semantic search — vectors from different
models are not comparable, and mixing them produces plausible-looking, subtly
wrong results.

A mismatched pack still installs and still serves `docs_lookup` and lexical
search, which do not depend on the embedding space. `argus pack list` marks it
`[INCOMPATIBLE]`, and `docs_search` returns a message naming the pack and the
model rather than a traceback.

Because the model is pinned globally, vectors from *different packs* do occupy
the same space — so results from a Python pack and a React pack are ranked
against each other directly, with no per-pack normalisation.

## Retrieval

Semantic search is two-stage: a Hamming-distance pass over the 96-byte binary
vectors, then cosine rescoring of the survivors using the int8 vectors.

Recall depends on how many candidates the coarse pass keeps. Measured against
an exact float32 baseline on a synthetic corpus:

| candidate pool | 100 | 200 | 300 | 400 | 600 | 800 | 1000 |
|---|---|---|---|---|---|---|---|
| recall@10 | 0.592 | 0.736 | 0.838 | 0.882 | **0.946** | 0.956 | 0.970 |

The default is 600. The loss is in the coarse cut, not the quantisation —
end-to-end recall sits within 0.002 of the ceiling set by which candidates
survive the Hamming pass — so recall is bought with overfetch, and overfetch is
cheap: the 96-byte scan is unchanged and only the count of 768-byte rows read
grows.

These figures are from a synthetic corpus and validate the mechanism, not the
product. Recall on real embeddings over real documentation is measured
separately.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | success (including `list` on an empty registry — that is a normal state) |
| 2 | configuration error |
| 3 | GitLab error |
| 4 | indexing failure |
| 5 | pack build, install or registry failure |
