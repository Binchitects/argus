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

### An interface method has two names, and only one was indexed

45% of the reference is COM methods. Their pages are titled `IFoo::Bar
(header.h)` and their UIDs spell the same entity `module.IFoo.Bar`. The
adapter indexed the UID form and — because the title regex wanted a kind word
("function", "method", "structure") that a COM title does not carry — never
the documented one. `docs_lookup("IFoo::Bar")` returned nothing at all:
29,557 symbols in the win32 pack were unreachable by the name Microsoft
documents, C++ writes, and every compiler error quotes.

That is the worst shape a miss can take here. The server's instructions read
an empty result as "not documented" and forbid answering from memory, so the
caller does not get a wrong answer — it gets a refusal, and the more carefully
it follows instructions the more convincing the refusal is.

Both spellings are indexed now, which is most of the win32 pack's growth from
87,206 symbols to 118,242. It was found by
the Windows-version evaluation ([measurements](../measurements/pack-measurements.md)), not by a user.

One detail worth keeping in mind if you change the contract format: 253 of
those 52,506 values contain a semicolon — `Windows 10, version 1809 (10.0;
Build 17763)`. `docs_contracts` splits the field on `;` and reads only the keys
it knows, so the trailing fragment is ignored rather than becoming a field.
A test holds that.

## Which Qt version an API is in

The `qt` pack holds the Qt C++ reference from Qt 4 to Qt 6 in one file: the
Qt 4.8.7, 5.15.2 and 6.10.3 documentation sets. Each set is the last release
of its major version. Each says when an API arrived ("This function was
introduced in Qt 5.10") and keeps the obsolete members on their own pages, so
the three cover every 4.x, 5.x and 6.x release.

A page is a document per version, linked to that version on doc.qt.io
(`https://doc.qt.io/qt-6/qstring.html`, `.../qt-5/...`,
`.../archives/qt-4.8/...`), and its title says which: `QString Class (Qt 5.15)`.
Symbols come from qdoc's own `.index` files, which name every class, member
function, signal, slot, property, enum value, typedef, macro and QML type with
its status. A symbol has a row per version that documents it, newest first,
and every row leads with the same line, then the header, the release it
arrived in and the module:

```
Qt 4.8, Qt 5.15, Qt 6.10 in QtCore5Compat; Header: <QRegExp>; Module: QtCore5Compat -- class QRegExp
Qt 4.8, Qt 5.15 (obsolete), not in Qt 6; Header: <QString>; Module: QtCore -- [static] QString QString::fromAscii(const char *str, int size = -1)
Qt 4.8, Qt 5.15, Qt 6.10 (deprecated since 6.4); Header: <QString>; Module: QtCore -- [constexpr] qsizetype QString::count() const -- Use size() or length() instead.
Qt 4.8, Qt 5.15 in QtWidgets, Qt 6.10; Header: <QWidget>; Module: QtWidgets -- [slot] void QWidget::show()
```

"in QtWidgets" marks the version where a name changed module, which is most of
a port from Qt 4 to 5 or from 5 to 6. Class pages carry the same line under
their title ("Documented in: ..."), so a search finds it too.

A search can ask for one version: `lang="qt4"`, `"qt5"` or `"qt6"` scopes
`docs_lookup`, `docs_find` and `docs_search` to that version's pages, and
`lang="qt"` means all three. The pack declares these slices itself (the
`facets` entry of its metadata), so the server needs no list of them, and
`docs_find` names them with the installed sources.

Build it from the offline documentation, unpacked as `qt4/` (Qt 4's
`doc/html`), `qt5/` (`Docs/Qt-5.15.2/<module>`) and `qt6/` (`<module>`) under
one directory. The directory is not a git checkout and there is nothing to
`--fetch`: the pack records the three releases as its provenance
(`qt4=4.8.7,qt5=5.15.2,qt6=6.10.3`).

```bash
argus pack build --source qt --work-dir .packwork/docsrc/qt --out qt.arguspack --version 1.0
```

Member lists (`-members.html`) and the source listings of examples are left
out. Parsing the three sets takes about 10 seconds; the build's time is the
embedding of its 190,970 chunks.

## Governance frameworks: the grc pack

The `grc` pack holds the governance frameworks whose texts may be shared, each
clause a page in its own words: NIST SP 800-53 Rev. 5 (each control and
enhancement, its discussion, related controls and baselines), SP 800-171
Rev. 3, the Cybersecurity Framework 2.0 (with its implementation examples),
the AI RMF 1.0 with its Playbook, and the EU AI Act's articles and annexes.
ISO's, PCI DSS's, the CCM's and the Common Criteria's texts are not shared, so
they are not in it; [docs/compliance](../compliance/README.md) names their
clauses.

A clause's id is its symbol (`AC-2(1)`, `03.01.01`, `GV.OC-01`, `GOVERN 1.1`,
`Art.50`, `Annex III`), and each framework is a facet: `lang="nist80053"`,
`"nist800171"`, `"nistcsf"`, `"airmf"` or `"euaiact"` keeps a search to it.
The pages link to each publication (the AI Act's to the article on EUR-Lex).

Its work directory is written from the downloaded sources (the addresses are
in `tools/grc-catalogs.py`), and records their checksums as its provenance:

```bash
python3 tools/grc-catalogs.py --from <the downloaded sources> --texts .packwork/docsrc/grc
argus pack build --source grc --work-dir "$PWD/.packwork/docsrc/grc" --out packs/grc-1.0.arguspack --version 1.0
```

It has 1,437 pages and 5,349 passages, about 10 MB; the embedding takes a few
minutes on a GPU.

## Security: the security pack

The `security` pack is what is known of software weaknesses and how to find
and fix them, for the security review and Fix it:

| Source | What | Symbols | Licence |
|---|---|---|---|
| MITRE CWE | each weakness: consequences, detection, mitigations, the demonstrative examples (bad and good code), observed CVEs, related attack patterns | `CWE-89` | MITRE's terms of use |
| MITRE CAPEC | each attack pattern: execution flow, prerequisites, mitigations, the weaknesses it uses | `CAPEC-66` | MITRE's terms of use |
| OWASP Cheat Sheet Series | how to fix, by topic and framework | the cheat sheet's title | CC BY-SA 4.0 |
| OWASP ASVS 5.0 | a page per section, each requirement with its level | `V6.2.1` | CC BY-SA 4.0 |
| OWASP WSTG | each test | `WSTG-INJT-05` | CC BY-SA 4.0 |
| OWASP Top 10s | web 2025 and 2021, API 2023, LLM applications 2025 | `A05:2025`, `API1:2023`, `LLM01:2025` | CC BY-SA 4.0 |
| CodeQL query help | each security query's help, metadata and examples (never the engine) | `cs/sql-injection` | MIT |
| .NET security rules | each rule of the Security category | `CA2100` | CC BY 4.0 |
| Bandit, gosec, eslint-plugin-security | each check or rule, with its CWE | `B608`, `G201`, `security/detect-object-injection` | Apache-2.0 |

Each page ends with its source and licence. Facets keep a search to a source:
`cwe`, `cwe-top25` (the 2025 Top 25), `capec`, `cheatsheets`, `asvs`, `wstg`,
`top10`, `api-top10`, `llm-top10`, `codeql` and `codeql-<language>`
(`codeql-csharp`, `codeql-python`...), `dotnet-rules`, `bandit`, `gosec`,
`eslint-security`. Not taken: Semgrep's registry rules and the SEI CERT wiki,
whose terms do not allow redistributing them, and the CIS benchmarks.

```bash
tools/fetch-security-sources.sh
python3 tools/security-texts.py --from .packwork/docsrc/security --out .packwork/docsrc/security-texts
argus pack build --source security --work-dir "$PWD/.packwork/docsrc/security-texts" --out packs/security-1.0.arguspack --version 1.0
```

The fetch takes about 50 MB (shallow, sparse clones and MITRE's XML), each
repository's commit and each catalogue's version recorded as the provenance.
The pack has 2,676 pages, 3,023 symbols and 24,675 passages, about 45 MB; the
embedding takes about two minutes on a GPU.

## Known vulnerabilities: the advisories pack

The `advisories` pack holds GitHub's reviewed security advisories (the
GitHub Advisory Database, CC BY 4.0) for npm, NuGet, PyPI, Maven, Go,
crates.io, RubyGems, Packagist and the rest, each marked when CISA's Known
Exploited Vulnerabilities catalog lists its CVE. A page per advisory (its
summary, CVEs, severity and CVSS vector, CWEs, the versions it affects and
the versions that fix them, its details and references); its `GHSA-…` id and
each `CVE-…` are symbols, and each ecosystem a facet (`npm`, `nuget`, `pypi`,
`maven`, `go`, `crates`, `rubygems`, `packagist`, `other`). Withdrawn
advisories are left out.

Beside its pages it carries a table of what each advisory affects (the
package, and the versions from where it was introduced up to the fix or the
last affected one), which the `deps_check` tool matches a repository's
locked versions against with no model. NVD's own records are not taken:
their CVEs are the advisories' aliases.

Argus renews it itself. With `ARGUS_ADVISORIES_RENEW_HOURS` set (the
stack sets 24), the server renews an installed advisories pack when it is
that old, and Admin → Packs has **Renew** for any time. A renewal fetches
again what changed in the advisory database and the KEV catalog, writes the
pages, and builds the pack over the one installed. Only the advisories that
changed are embedded again, with the stack's own embedder. On a connected
machine that builds packs for offline hosts:

```bash
argus pack renew --packs-dir packs
argus pack index --packs-dir packs --out packs/index.json --base-url https://packs.example.com/ advisories
```

An offline host then takes it with `argus pack update` (or `airgap.sh`). The
first fetch is a shallow, sparse clone of the reviewed advisories (about
340 MB unpacked, two minutes) and the KEV catalog (2 MB); after that only
what changed comes down. The first build embeds every advisory (some
220,000 chunks: two hours on a GPU); a daily renewal embeds a few hundred.
Google's OSV storage is not used, as it refuses some locations. On 2026-10-27: 36,038
advisories, 67,652 affected ranges, 1,760 known-exploited CVEs.

### deps_check and sbom

`deps_check` reads a project's lock files and manifests at any commit (a
merge request's head, a branch, a tag or a commit), as the person asking:
npm's `package-lock.json`, `yarn.lock` and `pnpm-lock.yaml`; NuGet's
`packages.lock.json`, `PackageReference` and `Directory.Packages.props`;
pip's `requirements*.txt`, `poetry.lock`, `uv.lock` and `Pipfile.lock`;
`go.mod` and `go.sum`; `Cargo.lock`; Maven's `pom.xml` and Gradle's build
files; `Gemfile.lock`; `composer.lock`. Vendored code (`node_modules`,
`vendor`, `third_party`) is left out. Each pinned version is compared as its
ecosystem orders versions (SemVer, PEP 440, Maven's, RubyGems') with every
range an advisory gives for the package. Each finding gives the package and
version, whether the project asks for it itself or which package brings it
in, the file and line, the advisory and its CVEs, severity and CWEs, whether
it is known to be exploited, the version that fixes it and the smallest
upgrade that fixes all of its advisories; the known-exploited and most
severe come first, and the answer says how old the advisories are.

`sbom` gives the same packages as a CycloneDX 1.5 bill of materials, each
with its package URL.

### scan_at

`scan_at` runs the security scanners on a project at any commit, as the
person, within their GitLab rights. It works only where the `scanners`
feature is installed (`ARGUS_SCANNERS_URL`); elsewhere the answer says how
an admin turns it on. Argus sends the commit's files from the project's
mirror (`git archive`). With `history` (the default) it sends a git bundle
of the commit instead, so gitleaks reads every commit before it too. The
bundle's ref (`refs/arena/scan/...`) is removed again, and nothing sent is
kept on either side. A history over 1 GB is sent as the commit's files
alone.

The scanners run in `deploy/services/scanners`, on a network shared with
Argus only, with no way out:

- opengrep's engine with Arena's own rules (`rules/`, one file per language,
  each rule with its CWE, severity and confidence). The image is not built
  unless every rule finds its weak examples and none of its fixed ones;
- Bandit for Python;
- gitleaks for secrets, redacted: the answer has where a secret is, never
  its text;
- Trivy's checks of configuration (Dockerfiles, Kubernetes, Helm,
  Terraform), the ones built into Trivy.

Findings on the same line are folded into one, with every scanner and rule
named. gosec and the .NET analyzers are not there: they build the project,
which fetches its packages.

A change not committed anywhere yet (Fix it's, before its merge request) is
scanned from its files: the app posts them to `/admin/scan-files` (Argus's
admin token, never a model's tool), up to 200 files and 20 MB, paths inside
the repository only, and Argus sends them to the scanners as a tar, with no
history.

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

Requires the embedding server the stack runs (`llamacpp-embed`, nomic-embed-text)
reachable at `ARGUS_EMBED_URL` -- the same model the index uses, so a pack's
vectors and a query's vectors share one space:

```bash
export ARGUS_EMBED_URL=http://localhost:8081   # or wherever llamacpp-embed answers
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
`cppreference`, `qt`, `grc`, `security`, `advisories`, `debugger`, `algorithms`, `system-design`, the two composites
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
