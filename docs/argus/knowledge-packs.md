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

## Which operating system an API arrived in

The Microsoft references keep the OS requirement Microsoft ships in each
page's front matter, and it leads both the symbol contract and the page text:

```
Minimum client: Windows XP [desktop apps only]
Minimum server: Windows Server 2003 [desktop apps only]
Header: fileapi.h
Library: Kernel32.lib
DLL: Kernel32.dll
```

It is populated on **52,506 of sdk-api's 65,908 pages** and **11,275 of the
driver reference's 25,903**, across 285 distinct values running from Windows
2000 Professional to Windows 11 24H2 and Server 2025.

This is the one requirement a header name cannot imply. Knowing an API is
declared in `fileapi.h` says nothing about whether the machine it has to run
on exports the function at all, and that is usually the first thing worth
knowing about a Windows API. With the field captured, `docs_lookup` answers
"what does this need" and `docs_search` can be asked what arrived in a given
release — neither of which was answerable from the pack before, because the
adapter parsed these keys and then dropped them.

The driver reference needs a second axis. A WDF driver targets a KMDF or UMDF
release rather than an OS build, and pages like `WdfDriverCreate` state no OS
at all, so `req.kmdf-ver` and `req.umdf-ver` travel with the OS fields and are
what those pages have instead. Measured on the built pack: of 37,938 symbols,
11,275 state an OS floor, 1,770 state a framework version and no OS, and 12,779
are ordinary function, struct and enum pages with neither — a gap in
Microsoft's own metadata rather than in the parse. sdk-api is unaffected: its
values for both framework keys are empty on every page.

`req.redist` travels with them where it exists (2,893 pages), because a
redistributable is a different kind of requirement from an OS version: the
API is there, and something else has to be installed first.

One detail worth keeping in mind if you change the contract format: 253 of
those 52,506 values contain a semicolon — `Windows 10, version 1809 (10.0;
Build 17763)`. `docs_contracts` splits the field on `;` and reads only the keys
it knows, so the trailing fragment is ignored rather than becoming a field.
A test holds that.

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

`update` compares every installed pack against a published index and installs
the newer ones. A pack that is not in the index — a locally built one, say —
is left alone and reported, rather than removed or failed on. A pack whose
download fails its checksum leaves the **working pack in place**: updating is
the one operation here that can destroy something that currently works, so the
new artifact is verified before the old one is replaced.

## Publishing an index

`update` reads a JSON index, and `pack index` writes one:

```bash
argus pack build --source python --work-dir /tmp/cpython \
  --out /srv/packs/python-3.13.arguspack --version 3.13 --fetch
argus pack index --packs-dir /srv/packs \
  --base-url https://packs.example.org \
  --out /srv/packs/index.json
```

Then serve that directory over HTTP and point `--index-url` at the file. The
index looks like this:

```json
{
  "schema": 1,
  "packs": [
    {
      "name": "python",
      "version": "3.13",
      "url": "https://packs.example.org/python-3.13.arguspack",
      "sha256": "1f0c…",
      "size_bytes": 41234567,
      "license": "PSF-2.0",
      "attribution": "…",
      "source_commit": "…"
    }
  ]
}
```

`--base-url` is where the packs will be **served from**, not where they sit on
the machine that built them; each entry's `url` is that base joined with the
pack's filename. The checksum is computed from the artifact at publish time, so
an index cannot claim a digest the file does not have — which is the failure the
mandatory checksum exists to catch. A file in the directory that is not a
readable pack is skipped and named in a `skipped` list rather than published,
because an entry pointing at something uninstallable fails on the *consumer's*
machine, where the cause is much harder to see.

`pack index` also takes a pack name to publish a subset, for a directory that
holds several versions.

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

Available sources: `python`, `react`, `cpp`, `dotnet`, `scripting`, `sqlite`,
`cppreference`, `debugger`, `algorithms`, `system-design`, the two composites
`win32` and `wdk` (API reference *and* samples in one pack), and the halves on
their own — `win32-docs`, `wdk-docs`, `win32-samples`, `wdk-samples`.
`argus pack build --help` prints the list.

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

### Lexical queries are prose, not FTS5 expressions

`docs_search` quotes every term in the query as a phrase before handing it to
FTS5, so nothing in the input is parsed as syntax. Ask `what is a mutex?` or
search for `std::atomic_exchange` and both work; previously the first failed on
the question mark and the second on `:`, which is FTS5's column operator, and
both raised an error rather than returning results.

The trade is that FTS5 operators are no longer honoured. `a AND b`, `star*` and
`NEAR(...)` are searched for as literal words. That is the right default for a
tool whose caller is a language model sending prose rather than someone who has
read the FTS5 grammar. A query containing nothing searchable returns no results
rather than an error.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | success (including `list` on an empty registry — that is a normal state) |
| 2 | configuration error |
| 3 | GitLab error |
| 4 | indexing failure |
| 5 | pack build, install or registry failure |
