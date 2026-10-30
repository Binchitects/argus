# Argus's code graphs: symbols, types, contracts, tests and owners across every repository a person can read

This design answers the request of 2026-10-30: "I want to make argus (code indexer) stronger … more complex graphs … consider huge improvement on argus for next version". Paths are relative to the repository's root. Line numbers are as of commit 05dd8f7d.

This is a design only. Nothing was changed, built or run, and deploy/.env was not read.

**Summary**
- **What it is:** a graph of symbols built into Argus's existing index. It covers definitions with stable keys, calls and references, the type hierarchy, imports between files, and uses across repositories. On top of it sit service contracts (HTTP routes, gRPC methods, topics, tables), tests, owners, history and deployments.
- **How it is built:** from the files Argus already mirrors. There is no build, no `dotnet restore` and no `npm install`. For C#, TS/JS and Python, Code Arena's scanners move into a shared source folder. For the other languages, the universal-ctags already in the image is used with two more fields.
- **Where it lives:** in the same SQLite file as today. There is no graph database and no second store.
- **Who sees what:** each person sees only nodes and edges whose repositories their own GitLab account can read. This is enforced in the SQL that reads the edges and proved by a differential test.
- **What agents get:** three new MCP tools (`symbol_graph`, `mr_impact`, `service_contracts`). Five existing tools also change: `find_symbol`, `find_references`, `change_impact`, `impact_of` and `index_status`. The catalog goes from 37 tools to 40.
- **What people get:** a Code map page in the web app, cross-repository call and type hierarchy in Code Arena's IDE, and an Impact card on every automatic merge request review.
- **Downloads:** none on the default path (§8).
- **When it turns on:** the model-facing tools ship off (`ARGUS_SYMBOL_GRAPH=0`). Each one becomes the default only after it passes the gates in §7, measured with a model and without one. The pages, IDE panels and review card do not change what a model sees, so they ship on.

---

## 0. What this is, and why

**Headline.** Ask Argus who calls a method, what implements an interface, or what a merge request will break in every repository you can read. It answers with the exact lines, each marked with how it was resolved and how sure it is, plus the tests to run, the owners to ask, and the endpoints, topics and tables at the other end.

**Why now.**
- **Argus cannot answer symbol questions today.** It is a text and name index with a repository-level graph (§1). It cannot say who really calls a function, what implements an interface, or what a signature change breaks beyond the first ring of dependent repositories. `find_references` is a full-text search plus a word regex (Queries.cs:223-275).
- **Exact lookup plus one hop is what helps small local models.**
  - In a depth-stratified SWE-bench Verified study (arXiv 2610.33958), adding `find_entity` and `graph_neighbors` raised function Acc@10 from 26.8 to 37.1 for Qwen3-4B and from 16.7 to 27.4 for Qwen3-8B. For Qwen3-32B it went from 32.0 to 34.0, which is not significant (p = 0.27).
  - Tasks that needed 3 or more hops failed in every setup.
  - Weaker models fail when they must write a graph query language. CodexGraph with Qwen2-72B scored 1.95%, against 9.34% for typed tools.
  - The tools are therefore typed, shallow by default, and have no query language.
- **Graph-only answering is cheaper but less accurate.** codebase-memory-mcp scored 83% against 92% for a file-exploring agent, using about a tenth of the tokens. So grep, read and `search_code` stay as they are, and the graph sits beside them.
- **Argus's own edge is what grep cannot see.**
  - Edges across repositories and at run time.
  - A read-only GitLab token on any tier.
  - Offline use.
  - C/C++ include graphs.
  - Local models.

  GitLab Orbit sets the bar for code-plus-SDLC graphs, but it needs Premium or Ultimate, and self-hosting it needs admin rights beside GitLab.

**The base.** Three designs were judged:
- an exact symbol graph with no build;
- a broader agent graph with tests, owners and history;
- an organization-wide architecture map in Postgres.

This document takes the first as its base. It grafts in the second's measurement plan, test graph, owners, history and impact bands, and the third's service-contract extractors, drawing libraries and Confirm button. Everything dropped is listed in §11.

---

## 1. What Argus has today, and where it falls short

### 1.1 What is stored per file

| What | How | Where |
|---|---|---|
| Text | Full content, plus FTS5 with `unicode61`, which does not split camelCase | 001_initial.sql:74-80; Writes.cs:103-126 |
| Definitions | universal-ctags `--fields=+nKSsefl` with C/C++ prototypes. Inheritance (`i`), access (`a`) and typeref (`t`) are not requested. ctags also runs on markdown and text. | Ctags.cs:42-50; Parsers.cs:59; Links.cs:67-71 |
| Symbol row | name, kind, line, end_line, signature, scope (as text), is_public, doc. There is no qualified name, parent or language column; `SymbolRow.Language` is read but never written. | 001_initial.sql:23-37; 013_symbol_docs.sql:18; Writes.cs:146-172 |
| `is_public` | A guess. It is false when the scope contains detail, internal, impl or anonymous; true for header files; otherwise the inverse of ctags' file-restricted flag. | Ctags.cs:31, 81-90 |
| Doc comments | Walked back from the definition line over the stored text | Parsers.cs:101-110, 164-226 |
| `#include` | A line regex that keeps the raw text and the angle flag | Parsers.cs:73-99 |
| Declarations (`file_decls`) | What a file provides and uses, read by a regex per ecosystem (packages, namespaces, imports, submodules, CI includes, images) | Links.cs:24, 73-132, 164-463 |
| Runtime names | Compose, Kubernetes, Helm, Backstage, OpenAPI titles, Pact, URL hosts, `host:port`, Kafka topics and database names, matched by lower-case equality | Links.cs:623-665, 786-807 |
| Vectors | One per public symbol, built from kind, name, doc, scope, signature and path. Bodies are never embedded. | Semantic.cs:8-41 |

**What is not stored at all:** where a symbol is used. There is no table of references, calls, bases or types (001_initial.sql:23-37; 019_cross_repo_graph.sql:1-60).

**File types.** The indexed languages are fixed by extension (Parsers.cs:10-34). `.sql`, Ruby, PHP, Swift, Scala, shell, Terraform and `.mjs/.cjs/.mts/.cts` are never stored. `.hh` and `.ipp` are in `Resolve.HeaderSuffixes` (Resolve.cs:46) but not in the extension table, so they can never be include targets.

### 1.2 How names are resolved

- **Includes.** Only `#include` strings are resolved to files (Resolve.cs:6-20, 46-110, 216-315).
  - The order is: relative to the including file; then a suffix index over every header; then vendored copies dropped and the same repository preferred; then the system-header list; then the branch preference.
  - The result is one of four states with no numeric confidence (008_include_resolution.sql:9-11).
- **Repository links.** Each declared use is matched to the repositories that provide its name (GraphLinks.cs:157-341, 497-578).
  - Each link gets a prior by kind and by how its name matched (LinkKinds.cs:63-91). The prior is multiplied by 0.85 when a single file makes the link (LinkKinds.cs:23-24).
  - A pair's links are combined by noisy-OR (LinkKinds.cs:141). Tiers are strong at 0.85 or more and likely at 0.5 or more (LinkKinds.cs:135).
- **Settlement.** A name that several repositories provide is settled in this order (GraphLinks.cs:353-414):
  1. by the user repository's manifests;
  2. by the same file's other uses;
  3. by its other likely links;
  4. by the same name settled in another file.

  Anything still open becomes a `candidate`, which is never walked. Steps 2 to 4 use the whole estate's graph, including repositories a given reader cannot see.
- **History.** The `cochange` kind and its 0.45 prior exist (LinkKinds.cs:52, 75), but nothing produces them.

### 1.3 What the tools answer

| Tool | What it actually does | Where |
|---|---|---|
| `find_symbol` | An exact `name = ?` lookup, public first, at most 50 | Queries.cs:108-131 |
| `code_contracts` | Takes identifiers of 5 or more characters from pasted code and calls `FindSymbol(limit 1)` for each, ignoring the file's imports | Queries.cs:66-104 |
| `find_references` | An FTS5 phrase match, then a `\bname\b` regex over each whole file, comments included. Results are sorted by repo, path and line, then cut at 100 with no truncated flag. | Queries.cs:223-275; ToolCatalog.cs:53-56 |
| `change_impact` with a symbol | Searches only depth-1 dependents. Deeper ones go to `not_searched`. Every file of the changed repository counts as a use. Caps: 60 lines, 8 per repo, 10 in the changed repo. | GraphQueries.cs:584-780 |
| `impact_of` | A file-level BFS over resolved includes and importers. C# is followed by namespace, TS/JS only by npm package, and Rust not at all. It runs per-file SQL inside the loop. | Queries.cs:563-701 |
| `repo_map`, `system_map`, `dependency_path` | Load all of `repos`, `repo_edges` and `repo_links` on every call | GraphQueries.cs:82-146 |
| `semantic_search` | A global coarse pass of 600 by Hamming distance, then the allowlist. People who can read few repositories get fewer answers. | Queries.cs:739-785 |

The catalog defines 37 tools. Its own comment, overview.md and tool-modes.md still say 34 (ToolCatalog.cs:1; docs/argus/overview.md:65; docs/measurements/tool-modes.md:6).

Code Arena grounds each plan step by calling `change_impact(repo, symbol, depth 2)` for up to 12 symbols. It guesses tests from paths (src/CodeArena/Runtime.cs:1469-1537; WorkPlan.cs:21-24).

### 1.4 Access today

- **Lookups** put the allowlist in SQL, chunked at 900 parameters (Queries.cs:16-39). The server opens the index read-only with `query_only` (Db.cs:85-99).
- **Graph tools** read whole tables and filter in C# (GraphQueries.cs:82-146). `impact_of` runs unfiltered SQL over `includes` and `file_decls` and filters afterwards (Queries.cs:571-596, 692).
- **Settlement** (§1.2) uses repositories the reader cannot see.
- **The GitLab-token path caches index row ids.**
  - It stores `repo_ids_json` for 600 s, or up to 3,600 s while GitLab is down, and serves them from the cache (Acl.cs:101-149).
  - `repos.id` has no AUTOINCREMENT (011_repo_branch.sql:171-187), and Acl.cs itself warns that a deleted row's id is given to the next repository indexed (Acl.cs:430-436).
  - Branch pruning and `DeleteRepos` do not clear `acl_cache` (Commands.cs:163-171; Choices.cs:161, 237-263).
  - The platform path already guards against this and has a test (Acl.cs:241-269; tests/Argus.Tests/SlowGitLabTests.cs:212-236). The token path has neither. This gap is inferred from the code and has not been reproduced.
- **`.env` files are stored in full.** `.env` and `.env.example` are indexed as `dotenv`, and their full content goes into `files.content` and FTS (Parsers.cs:23, 33; Writes.cs:103-126). Indexing never calls `SecretMask` (src/Argus/Util/SecretMask.cs:6-11).

### 1.5 Branches, freshness and scale

- **Branches.** Each selected branch is its own `repos` row, read in full with no dedup (docs/argus/branches.md:77-85). Links are made between default branches only (GraphLinks.cs:165-167; GraphQueries.cs:178-182).
- **Re-reading files.** Files are re-read from `git diff --name-status` and skipped when the per-file stamp matches (Mirror.cs:202-227; Worker.cs:221-243; Ctags.cs:35-40).
- **Rebuilding the graph.** After every pass, `ResolveIncludes`, `RebuildRepoDeps` and `RebuildLinks` rebuild the whole estate's graph (Commands.cs:425-440).
- **Measured scale:** 47 repositories, 55,603 files and 1,491,167 symbols in 37.8 min, at 21.9 MB per 1,000 files, which is about 1.2 GB (docs/argus/overview.md:468-479).

### 1.6 Measured so far

| What | Result | Where |
|---|---|---|
| Synthetic 200-repo estate | All 469 links found exactly, in 28 ms | overview.md:116-119 |
| Hand-checked real graph (include-only era) | 13 of 25 cross-repo edges correct | docs/measurements/index-measurements.md:334-402 |
| `graph-eval` against owners' links | Harness exists, never run; still "Groundwork" | GraphEval.cs:8-165; docs/plan.md:2461-2462 |
| Graph questions with a model | Never measured | tool-modes.md:76-79 |
| `which_repo` | 9/10 on 6 repos; 5/10 on prose questions at 47 | index-measurements.md:148-171, 443-475 |
| Code Arena's index, no model | Locate hit@1 53% against 13% for grep ranking; hit@5 78% against 62% | docs/measurements/code-index.md:16-21 |
| Code Arena's index, Qwen3-4B | 0/25 locate in both arms; the index arm used a median 23,655 prompt tokens against 10,879. Gate not met, so it is off. | code-index.md:36-56 |

### 1.7 Where it falls short

1. **No graph of symbols.** True callers, callees, implementations and overrides cannot be answered, in one repository or across several.
2. **References are lexical.** Comments count, and results past 100 are cut silently.
3. **Symbol impact stops at depth 1**, with no idea of tests or owners.
4. **In-repository TS/JS and Python impact is invisible.** Relative imports are skipped (Links.cs:263, 351).
5. **Commented-out imports make links.** JS/TS, Rust and `.proto` are read from raw text (Links.cs:108-114), although overview.md:101-103 says imports in comments are not counted.
6. **Symbol ids are not stable.** Rows are deleted and re-inserted on every change, and vectors are deleted with them (Writes.cs:153-172; 012_symbol_embeddings.sql:19-26).
7. **Runtime links are only names.** There are no HTTP routes, gRPC methods, queues other than Kafka, or tables.
8. **There are no tests, owners or history.** Nothing reads CODEOWNERS, and the history layer is empty.
9. **Access in the graph tools is enforced in C#**, settlement depends on hidden repositories, and the token cache holds row ids.
10. **The whole estate is rebuilt after every pass**, and every graph call loads whole tables.

---

## 2. The graphs

| # | Graph | Nodes | Edges | Walked by default once gated |
|---|---|---|---|---|
| 2.1 | Symbols | Definitions with stable keys | contains | – |
| 2.2 | File imports | Files | imports, includes | Yes |
| 2.3 | Uses inside a repository | Symbols | calls, references, type_ref, instantiates | S, I, T yes; N only if its calibrated precision is 0.5 or more; G never |
| 2.4 | Type hierarchy | Types, members | inherits, implements, overrides, implements_member | Yes |
| 2.5 | Across repositories | Exports by moniker | The kinds of 2.3 and 2.4, through a repo link | Yes |
| 2.6 | Tests | Test symbols | covers, named_for | Listed and ranked |
| 2.7 | Service contracts | Components, operations, RPCs, topics, tables | serves, calls, implements_rpc, stub_of, produces, consumes, defines, reads, writes | Per extractor, after its gate |
| 2.8 | Owners | Rules, users, groups | owns, approved | Shown, never walked |
| 2.9 | History and deployments | Commits, MRs, issues, environments | changed, in_mr, refs_issue, cochanges, deployed_to | Co-change is never walked |
| 2.10 | Repository graph underneath | Repositories | Today's kinds | As today |

**Evidence.** Every edge row carries an evidence letter and a confidence. The priors below are starting values. `graph-eval --symbols` calibrates them into `edge-priors.json`, the same way `link-priors.json` is read today (LinkKinds.cs:93-131).

| Letter | Meaning | Starting prior | Walked |
|---|---|---|---|
| S | Scope: a local, the enclosing type or one of its bases (up to 3 levels), `this`, `self` or `base` | 0.95 | Yes |
| I | Import: bound through the file's usings, TS import bindings or Python imports | 0.95 | Yes |
| T | Type: the receiver's declared type, one link | 0.90 | Yes |
| N | Name: the only definition of that name in the repository | 0.70 | Only if its calibrated precision is 0.5 or more |
| G | Guess: 2 or 3 definitions; one candidate row each | 0.6 / n | Never |
| – | More than 3 definitions, or none | – | Counted per file as unresolved |

A later precise tier (compiler or SCIP) would add a letter P (§11). This release does not.

### 2.1 Symbols with stable keys

- **Node: a definition.** Each `symbols` row holds:
  - `repo_id` and `key`, a 64-bit hash of (GitLab project id, language, qualified name, kind, parameter shape);
  - `qualified`, `name`, `kind`, `lang`, `parent_key`, `file_id`, `start`, `end`, `signature` and `doc`;
  - `body_hash`, `visibility`, and `moniker` for exported symbols.
- **Parameter shape** is the parameter types as written, with names dropped (C#, TS, Java, Kotlin). For Python it is the parameter names. For the ctags languages it is the signature as written.
  - The key contains no path and no overload ordinal. A symbol moved to another file keeps its key, and a new overload does not re-key its siblings.
  - If two definitions collide (both `#if` branches, TS declaration merging), the later one in path order gets the path's hash appended.
- **Edge:** `contains`, by `parent_key`.
- **How built:**
  - **C#, TS/JS and Python:** the shared scanners (§6) give the qualified name, signature, range, parent, the doc's first line and the body hash. The body hash is the first 8 bytes of SHA-256 of the blanked text with whitespace collapsed (CodeScan.cs:121-174). The full doc comment is still read the way Argus reads it today.
  - **C/C++, Go, Java, Kotlin and Rust:** ctags with `--fields=+nKSseflia`. `i` gives inheritance and `a` gives access. Scope is joined into a qualified name.
  - **Markdown and text** get no ctags symbols. This removes noise from `find_symbol`.
- **Visibility** comes from modifiers, not from the file-restricted guess:
  - C#, Java and Kotlin: access keywords, or ctags' `a` field.
  - TS: `export`.
  - Python: a leading underscore.
  - Go: a capital first letter.
  - Rust: `pub`.
  - C/C++: the header or `static` rule, as today.
- **Writes upsert by key.** An unchanged symbol keeps its row, its vector and anything attached to it. Vectors move to a table keyed by (key, text hash), with no cascade, and are collected when no symbol holds the key.
- **Monikers** exist only where a cross-repository edge can use them this release:
  - `nuget:Package/Ns.Type.Member(shape)`
  - `npm:pkg/path#name`
  - `pypi:dist/module.qualified`
  - `include:header/path.h#name`
- **Scan facts are kept per blob.** One scanner run per (blob sha, scanner versions) is stored as zstd JSON (ZstdSharp.Port is already referenced, Argus.csproj:12). It holds the file's symbols, imports, uses and contract claims. A branch row whose file has the same blob reuses it, and so does a vendored copy.
- **How sure:** a scanner replaces ctags for a language only if its definition recall against ctags is 97% or more. C# is at 98% today (tests/CodeArena.Tests/CodeScanCSharpTests.cs:170-201). TS and Python still need the check.
- **Cost (estimate):**
  - About today's 1.49M rows. The new columns add about 15% to the symbol table.
  - Scan facts take about 1–3 KB per distinct blob, compressed.
  - Bumping the contract to 6 forces one full re-read, which took 37.8 min last time.

### 2.2 File imports inside a repository

- **Node:** a file.
- **Edge:** `file_imports(repo_id, from_file, to_file, how, state)`.
  - `how` is one of: relative, tsconfig-path, package-self, python-relative, rust-mod, include.
  - `state` is one of: resolved, external, ambiguous, not_found.
- **How built:**
  - `Links.cs` stops skipping `./` and `.` specifiers.
  - TS/JS paths are resolved the way Code Arena resolves them: `.js` mapped to `.ts`, index files, and tsconfig `paths`, `baseUrl`, `extends` and `references` (CodeGraph.cs:751-937). Python relative imports are resolved by package (CodeGraph.cs:967-1056). Rust `mod` and `use` are resolved by path.
  - `.mjs/.cjs/.mts/.cts` become indexed, and `.hh/.ipp` become storable include targets.
  - JS/TS, Rust and `.proto` text is blanked before the regex, so commented-out imports stop making links.
  - The existing resolved includes join the same table.
- **How sure:** a relative path resolves or it does not, so a resolved import gets 0.97. An ambiguous target is never walked, as with includes today.
- **Cost:** one row per import statement, about 10–30 per file. `impact_of` walks this table with one query per depth level instead of one per file.

### 2.3 Uses inside a repository

- **Edge:** `sym_edges(repo_id, from_key, to_key, kind, evidence, confidence, n, first_line, file_id)`.
  - `kind` is one of: calls, references, type_ref, instantiates.
  - Duplicate (from, kind, to) rows are folded, with `n` counting them.
  - Per-file unresolved and guess counts go in `file_unresolved`.
  - Exact lines are not stored past `first_line`. They are read at answer time from the caller file's scan facts.
- **How built:** at index time, per repository, over `files.content`, using CodeGraph's rules moved onto a text provider (§6). For each use:
  1. **S:** locals, the enclosing type and its bases up to 3 levels, `this`, `self` or `base` (CodeGraph.cs:480-497, 595-705).
  2. **I:** C# usings and global usings; TS bindings through relative paths, tsconfig and up to 3 re-export hops; Python absolute and relative imports.
  3. **T:** the receiver's declared type, one link (CodeGraph.cs:711-747). Longer chains such as `a.b.c()` are recorded as N or G.
  4. **N:** exactly one definition in the repository. Non-test code is preferred.
  5. **G:** two or three definitions. They are stored as candidates and never walked.
  6. **Unresolved:** more than three, counted.
- **A fix comes first.** A receiver after `)` or `]` is recorded as an expression. Today such a call gets receiver null (CodeScan.cs:256, 793, 1236), so `x().Run()` binds to the enclosing type's own `Run` with Scope evidence (CodeGraph.cs:635-643).
- **Languages without a shared scanner** (C/C++, Go, Java, Kotlin, Rust): uses come from the shared tokenizer over blanked text, with its call flag. They resolve only to N or G, and answers say so ("Go: name level only").
- **Incremental:** a per-repository postings table `sym_use(repo_id, name, file_id)` lists the files that use each name. When a file adds or removes definitions, only the files that use those names are resolved again, not the whole repository.
- **How sure:** none of this is measured yet. The expectation is 0.95 precision for S, I and T. The published reference is PyCG on Python: 99.2% precision and 69.9% recall.
  - Expected recall against a compiler is 60–75%. Dependency injection, reflection, dynamic dispatch, duck typing and names held in strings are missed.
  - So every answer ends with "not found does not mean not used", plus the count of lexical-only matches.
- **Cost (estimate):** 4–6 folded edges per symbol, so 6–9M rows at today's estate. With indexes on (repo_id, from_key) and (repo_id, to_key) that is 0.5–0.8 GB, or +40–65% on today's ~1.2 GB. Resolution runs in memory per repository.

### 2.4 Type hierarchy

- **Edges** (in `sym_edges`):
  - `inherits` and `implements`, from type to type;
  - `overrides` and `implements_member`, from method to base or interface method.

  Derived types and implementations are read in reverse at query time.
- **How built:**
  - Base lists come from the shared scanners (C# base list, TS `extends` and `implements`, Python class bases) and from ctags' `i` field (Java, Kotlin, C++, Rust `impl`).
  - Base names are resolved with §2.3's rules.
  - Overrides are matched by name and arity against base members, up to 6 levels.
  - A call to `IFoo.M` is stored against `IFoo.M`. Asking for the callers of `Impl.M` therefore also returns those calls, marked "via interface IFoo".
  - Go's structural interfaces are not inferred. A method-set match is a G, and the answer says so.
- **How sure (expected):**
  - Bases: 0.95 when resolved by S or I, 0.75 by N.
  - Overrides: 0.97 with C# `override` or Java/TS `@Override`; 0.90 when the arity matches a virtual, abstract or interface member; otherwise 0.70.
- **Cost:** small. Types are about a tenth of all symbols.

### 2.5 Across repositories

- **Nodes:** an `exports` view of public symbols that have a moniker.
- **Edges:** `sym_xedges(from_repo_id, from_key, kind, moniker, via_kind, via_name, n, first_line)`. The target repository is not stored. At query time it is joined to `exports` among the repositories the reader can read (§4).
- **How built:**
  - A use crosses a repository boundary only if its import or include resolved to something another repository provides. That is the existing `file_decls` provider set plus a `repo_links` row of tier likely or higher, of kind package, namespace, import, include or proto.
  - The use is then bound to the provider's export by moniker, with §2.3's rules.
  - A bare name never crosses a repository.
  - An ambiguous provider is settled only by the calling repository's own manifests and submodules, never by other repositories' evidence.
  - This release covers C#, TS/JS, Python, and C/C++ through `include:header#name`. Go, Java, Kotlin and Rust stay at the repository level (§11).
- **How sure:** the edge prior times the repository link's confidence. For example, I at 0.95 times a package link at 0.97 is about 0.92. Package versions are not modelled: the provider's default branch is used, and the answer says so.
- **Cost:** rows exist only for uses of packages inside the estate, a small share of §2.3. They are recomputed for a pair of repositories only when either side's exports or the link between them change.

### 2.6 Tests

- **Nodes:** test symbols, found by framework markers:
  - `[Fact]`, `[Theory]`, `[Test]`, `[TestMethod]`
  - pytest `test_*`
  - `describe`, `it`, `test`
  - `@Test`
  - Go `func TestX`
  - Rust `#[test]`
- **Edges:**
  - `covers`: from a test to a symbol it reaches through calls, at 1–2 hops. At most 20 per test, keeping the nearest.
  - `named_for`: `FooTests` to `Foo`.
- **How built:** derived from §2.3 after each pass of the repository. No test is ever run. Each answer gives a filter command as text: `dotnet test --filter`, `pytest path::name`, `vitest -t`, `go test -run`.
- **How sure (expected):** about 0.9 at hop 1 and about 0.7 at hop 2. Mocks and dependency injection lower recall.
- **Cost:** no extra parse. At most tests × 20 rows.

### 2.7 Service contracts

The plan.md backlog item "Argus at run time" (docs/plan.md:1171-1177) is done here at the level of the operation, not just the repository.

- **Nodes:**
  - **Component:** a deployable unit inside a repository, anchored at a directory. It is found from:
    - a Dockerfile's folder;
    - compose services with `build:`;
    - Kubernetes workloads;
    - Helm `Chart.yaml`;
    - a `.csproj` with `Sdk=Microsoft.NET.Sdk.Web`;
    - Backstage `catalog-info.yaml`, now read as YAML with the YamlDotNet already referenced (Argus.csproj:13) instead of a regex.
  - **HTTP operation:** the method plus a normalized template, for example `GET /v1/orders/{}`.
  - **RPC:** `package.Service/Method`.
  - **Topic or queue.**
  - **Table:** `schema.table`.
- **Claims are kept per repository.** `contract_claims(repo_id, file_id, line, end_line, component_id, sym_key, kind, role, norm_name, scope, detail, confidence, extractor)` holds each repository's claims. They are joined by `norm_name` at query time, only among readable repositories. No topic, table or host exists as a global row.
- **How built:** per-framework rules over blanked text with literals kept, in one more pass of the existing extractor. The rules are a YAML list shipped with Argus.

  | Family | Servers and declarations | Clients and uses | Matching |
  |---|---|---|---|
  | HTTP | OpenAPI 2/3 operations (today only the title is read); ASP.NET `[Route]` with `[HttpGet]`, `MapGet` and `MapGroup`; Spring; Express, Fastify and Nest; FastAPI and Flask; Go net/http, gin and chi. The handler's range comes from §2.1. | `HttpClient`, `fetch`, `axios`, `requests`, `httpx`, `RestTemplate`, `WebClient`, Go `http.Get`. A base URL is read from the same project's config (`appsettings*.json`, `application*.yml`, Helm values, compose and k8s `env:`). Only `host[:port]` and key names are kept, never values. | Segment by segment, with parameters as wildcards. Only within a repository, or between repositories that the runtime layer already links, or when the client's host resolves to a component. |
  | gRPC | `.proto` parsed for package, service, rpc and message. Servers by generated names: C# `: Svc.SvcBase`, Go `RegisterSvcServer`, Java `SvcImplBase`, Python `add_SvcServicer_to_server`. | Stubs: `Svc.SvcClient`, `NewSvcClient`, `SvcGrpc.newStub`, `SvcStub` | By proto identity, when the package resolves to one readable definition |
  | Messaging | Kafka declarations (today's regexes become claims); Strimzi `KafkaTopic` | Kafka produce, subscribe and `@KafkaListener`; RabbitMQ `BasicPublish`, `QueueBind` and `@RabbitListener`; NATS `Publish` and `Subscribe`; SQS and SNS URLs and ARNs; Service Bus `CreateSender` and `CreateProcessor`. A name held in a same-project config key is resolved. | Within the same broker when it is known; otherwise a candidate |
  | Tables | `.sql` becomes an indexed language. A tolerant DDL reader handles CREATE, ALTER and DROP TABLE. Migrations: Flyway `V<n>__*.sql`, EF Core migrations and the ModelSnapshot, Alembic `op.*`. | EF `[Table]`, `ToTable` and `DbSet<T>`; JPA `@Table`; SQLAlchemy `__tablename__`; SQL strings with FROM, JOIN, INTO, UPDATE or DELETE FROM | Within the database resolved from connection strings (today's `runtime:database`); otherwise a candidate |

- **Contract copy drift.** A vendored `.proto` or OpenAPI file whose blob differs from its source repository's copy is a drift finding. It reuses vendored-copy detection (Resolve.cs:112-195) and is a fact, not a guess.
- **How sure (starting priors):**
  - HTTP:
    - spec operation 0.99;
    - route with a literal template and its prefix in the same file 0.90, prefix in another file 0.75;
    - call with a literal path and a resolved host 0.85;
    - a match by template only is a candidate at 0.60.
  - gRPC: implementation 0.95, stub 0.90.
  - Messaging:
    - declared topic 0.97;
    - literal name in a known client call 0.90;
    - name through a same-project config key 0.80.
  - Tables:
    - DDL or migration 0.97;
    - EF snapshot 0.98;
    - ORM attribute 0.95, naming convention 0.75;
    - table in a SQL string 0.85;
    - a cross-repository table within a resolved database 0.85.
  - URLs, hosts and topic names built at run time are counted and listed as unresolved, never guessed.
  - The published reference is Code2DFD: 93% precision and 85% recall on 17 Java applications.
  - **Per-extractor gate:** an extractor's edges are walked only at 0.95 precision or more at strong and 0.80 or more at likely (§7). Otherwise it stays candidate-only.
- **Cost (estimate):** 5% or less extra index time. Tens of thousands of rows at today's estate.

### 2.8 Owners

- **Nodes:** owner rules, plus GitLab users and groups as named in them.
- **Edges:**
  - `owns`: a rule's path pattern to its owners, with the section, required approvals and `file:line`.
  - `approved`: the approvers of recent MRs, read on demand through the existing read-only MR tools.
- **How built:**
  - A CODEOWNERS parser with GitLab's rules. It reads the first of the root, `.gitlab/` and `docs/` copies, in GitLab's order (checked against GitLab's documentation in the commit's test). It handles sections, `^optional` sections, `[n]` approvals, default owners on a section line, and the last matching pattern winning within a section.
  - Backstage `spec.owner`, read as YAML.
  - Nothing parses CODEOWNERS today.
- **How sure:** CODEOWNERS is a declaration and is shown exactly as written. An owner who is not an active member of the project is flagged as stale. Membership comes from the read Argus already makes with its read-only credential (Acl.cs:241-269).
- **Personal data:** usernames only. An email entry in CODEOWNERS is shown as "(an email entry)", never as the address.
- **Cost:** one parse per CODEOWNERS file.

### 2.9 History and deployments

- **Nodes:** commits, MRs, issues and environments.
- **Edges:**
  - `changed`: commit to file, and to symbol where the hunk ranges overlap the symbol's range and its body hash changed.
  - `in_mr` and `refs_issue`.
  - `cochanges`: file to file inside one repository.
  - `deployed_to`: repository or component to environment, with the sha and time.
- **How built:**
  - **From the bare mirrors.** Mirror.cs uses `clone --mirror`, so the history is on disk. Joins come from commit messages ("See merge request group/p!n", "#n", "Closes #n") with no API call. The last 20 commits are kept per file.
  - **Co-change in one repository.**
    - Merges, commits over 30 files, pure renames, lockfile-only commits and bots are dropped.
    - Support must be 3 or more. Confidence is support / changes(A), with a 180-day half-life. At most 10 partners per file.
    - It is read incrementally from the last processed sha.
    - This is the first producer of the `cochange` kind, at file level.
  - **Deployments.** Read-only `GET /projects/:id/environments` and `/deployments`, cached per pass. They give "prod runs sha X, N commits behind main".
- **How sure:**
  - Joins are exact for what the messages say. A squash with no reference makes no join.
  - Co-change is a ranking signal only: ROSE measured about 30% precision, and Hassan and Holt 49% at 51% recall. It sits in the history layer and is never walked.
  - Deployments are observed facts.
- **Cost:** a few seconds per repository, then only the pushed range. Two cached API calls per project per pass.

### 2.10 The repository graph underneath

- **Access moves into SQL.** `GraphQueries.Load` is replaced by bounded neighbourhood walks in SQL (§4, §5).
- **Settlement uses only local evidence.** At index time a name is settled only by the user repository's own manifests and submodules. The same-file, graph and other-file steps (GraphLinks.cs:353-414) move to query time, among readable providers.
- **Incremental rebuild.** `ResolveIncludes`, `RebuildRepoDeps` and `RebuildLinks` run only for repositories whose declarations changed, plus their neighbours. They no longer run over the whole estate after every pass (Commands.cs:430-433).
- **Contracts name the runtime links.** A runtime link now says which endpoints, topics and tables carry it. The contract layers join the default layers only after `graph-eval` shows no precision loss.
- **`repo_map` says what is used.** For each link it lists which of the other repository's symbols are used, with counts per moniker, so "A uses B" says what A uses.

---

## 3. The tools, for agents and for people

### 3.1 MCP tools

| Tool | Status | Inputs | Answer | Budget (tokens) |
|---|---|---|---|---|
| `find_symbol` | Changed | `name` (plain, dotted, `repo:path#Qualified` or a key), `kind`, `branch` | Up to 10 definitions. Each has its key, qualified name, signature, first doc line, `repo path:start-end`, visibility, and counts of callers, callees, implementations and unresolved uses. When the name is ambiguous, a hint for narrowing it. | 1,500 |
| `symbol_graph` | New | `symbol`; `relation` = callers, callees, references, bases, derived, implementations, overrides, imports, imported_by, tests or history; `hops` 1–3 (default 1); `min_confidence` (default 0.5); `include_tests`; `repos`; `code` (default false); `budget` 500–6,000 (default 2,000) | A folded tree: repo, then file, then enclosing symbol, one line each, with its evidence letter and confidence. Tests are tagged, and "via interface IFoo" and "via package X 0.97" are noted. Guesses are listed separately and never expanded. A hub (over 200 edges) is folded into counts per repo. What falls outside the budget is counted, with a `next` cursor. `code=true` packs the definition, call lines ±3 and callees' signatures with `path:a-b` citations, using Code Arena's packer. | 2,000 |
| `find_references` | Changed | `name` or key, `branch`, `lexical` | Resolved uses first, grouped by enclosing symbol, with evidence. Lexical-only matches follow, flagged `lexical:true`. Comments are excluded unless asked for. Counts per repo and a truncated flag replace the silent cut at 100. Until the gate is met, the lexical form stays the default. | 2,000 |
| `change_impact` | Changed | `target` = repo, symbol, file list or diff text; `depth` 1–4 (default 2); `include_tests` | Without a symbol: as today, with access in SQL. With symbols: a walk back over calls, references, implements and overrides, inside the repository and across repositories, and through contracts. Results come in three bands. **Will break:** S, I, T callers of a removed symbol or a changed signature, matched by arity, outside the change. **Check:** callers of changed behaviour, and consumers of a changed route, RPC, topic or table. **Maybe:** N edges, and co-change partners not in the change. After the bands: tests to run (as text), owners, deployed environments, `not_searched` and folded hubs. | 2,500 |
| `mr_impact` | New | `mr` = `group/proj!iid` | The diff is read with the read-only credential. The MR head's changed files are scanned in memory, as `file_at` does, and dropped. Changed symbols are found by line range and body hash. For each: the `change_impact` bands, tests, owners and suggested reviewers, and the contracts it adds, removes or changes. The automatic review card is built from this. | 3,000 |
| `service_contracts` | New | `target` = repo, route (`GET /orders/{id}`), rpc, topic, table or symbol; `direction` | Who serves or defines the target and who calls or uses it, with `file:line`, how the host or broker was resolved, and confidence. Ends that cannot be resolved are listed as unresolved. For a repo: its interface list and contract-copy drift. | 2,000 |
| `impact_of` | Changed | as today | Walks §2.2, now including relative TS/JS, Python and Rust imports, plus symbol edges into the file's public symbols. One SQL query per depth. The 200-file cap and truncated flag are kept. | as today |
| `repo_map`, `system_map`, `dependency_path` | Changed underneath | as today | The same questions, read with access in SQL. Runtime edges name their endpoints, topics and tables. `repo_map` lists the monikers used. | as today |
| `index_status` | Changed | as today | Adds graph health per repo and branch: edges by evidence letter, the share of uses unresolved, scanner versions, and the date and precision of the last `graph-eval`. | as today |

**Answer format.**
- Text answers are folded lines. `structuredContent` keeps the JSON for programs and pages (ToolRuntime.cs:213-234).
- Counts include only rows the reader can read.
- Every caller or reference answer ends with "not found does not mean not used: N lexical-only matches (find_references lexical=true)" and a `next:` hint.
- Answers are packed at 3.5 characters per token. The defaults stay well below the chat's 24,000-character threshold, above which a result is set aside as a file (ChatOptions.cs:10-15).

Example (names invented):

```
OrdersService.Cancel(Guid) · method · shop/orders src/Orders/OrdersService.cs:88-121 · key 9f3c1e…
callers, 1 hop, confidence ≥ 0.5 (14 readable):
shop/orders
  src/Orders/OrdersController.cs:52      OrdersController.Delete       S 0.95
  tests/Orders.Tests/CancelTests.cs:31   CancelTests.Cancels_paid      S 0.95  test
shop/billing  via package Shop.Orders.Client 0.97
  src/Billing/RefundJob.cs:77            RefundJob.Run                 I 0.92
guesses (not walked): 2 in shop/admin
not found does not mean not used: 3 lexical-only matches (find_references lexical=true)
next: change_impact target=9f3c1e…
```

### 3.2 What stays, and the agent profile

- **Unchanged:** `search_code`, `semantic_search`, `get_file`, `grep_at`, `file_at` and the review tools. The graph narrows where to look; grep still finds every occurrence.
- **Agent profile.** The tool definitions are already past the chat's 6,000-character budget and load on demand (OnDemandTools.cs:6-37).
  - A compact profile for models of 30B or smaller holds `find_symbol`, `symbol_graph`, `change_impact`, `mr_impact`, `service_contracts`, `get_file` and `grep_at` in 2,500 characters of definitions or less. It fits without `load_tools`.
  - It comes with a short Argus skill: find the exact entity, take one hop, read the cited lines, then edit.
  - The chat, code mode (`arena.argus.*`, CodeTool.cs:11, 83) and Code Arena choose the profile by model size.
- **Code Arena's plan grounding** (Runtime.cs:1469-1537) reads the new `change_impact` bands behind its own setting. It names tests from §2.6 instead of guessing them from paths (WorkPlan.cs:21-24).

### 3.3 The web app's views

**Where they run.**
- The pages live in src/web.
- Llm.Api calls Argus as the signed-in person, with the chat token plus `x-llm-user-email` as the chat already does (src/Llm.Api/Chat/ArgusMcp.cs:488-517). It never uses the admin token.
- No answer is cached across people.

**How they are drawn.**
- Graphs use cytoscape 3.34.3 with cytoscape-fcose 2.2.0, and elkjs 0.9.3 for layered views. All three are already in src/web/package-lock.json through mermaid 12.0.0. Promoting them to direct dependencies downloads nothing.
- At most 2,000 elements are drawn.
- Every graph has a keyboard-accessible table view, which is the default on phones.

**The views.**
1. **Code map page.**
   - A search box (`find_symbol`) leads to a symbol card: signature, doc, `repo/path:lines`, a GitLab link, owners, the deployed environments and the last MRs that changed it.
   - Tabs: Callers, Callees, Hierarchy, Implementations, References, Tests, Impact, Contracts, History.
   - In the centre is an ego graph of 1–2 hops. Nodes are boxed by repository. Edges are solid for S, I and T, dashed for N, and candidates are hidden behind "show possible".
   - Every row has an evidence chip and a "why" popover showing the line and the import or link that resolved it.
   - Hubs fold into "+214 callers in 9 repos".
   - Empty states say why, for example "Go: name level only".
   - A footer reads "covers N of your M readable projects".
2. **Impact view.**
   - Paste an MR link or a diff (`mr_impact`).
   - Changed symbols on the left; dependents in columns by hop (elkjs layered), grouped into the three bands.
   - Side panels list the tests to run and the owners to ask.
   - "Copy as Markdown" makes text a person can post. Argus never writes to GitLab.
3. **Service map.**
   - Components grouped by GitLab group.
   - Edges labelled with their route, RPC, topic or table, filtered by layer and confidence.
   - Contract-copy and deploy drift findings.
4. **Confirm.** A person who can read both ends of an edge marks it right or wrong. Verdicts are stored in Argus's sidecar database, because the index is opened read-only (Db.cs:85-99). They are audited and exported as `graph-eval` truth. Only aggregate numbers go into docs.
5. **Admin → Argus → Graph health** (admin token only):
   - coverage per repo and language;
   - edges by evidence letter and unresolved rates;
   - scanner versions and index time;
   - the `graph-eval` results with their calibration table;
   - the preview switches.
6. **Merge requests.** An Impact card in automatic reviews and on the review page, built from `mr_impact`, behind a setting.
7. **Code Arena IDE.** Call Hierarchy, Type Hierarchy and "References across repositories" panels from `symbol_graph`. The local per-folder index keeps answering for the open folder.

---

## 4. Permissions

A person's readable repositories stay as they are: the GitLab projects where they are Reporter or above (access level ≥ 20), through the token path or the read-only service-credential path, narrowed by Arena rules, with every branch row of each project. These rules hold for every new node and edge.

1. **Access is carried as GitLab project ids, never as index row ids.**
   - `Identity` gains `AllowedProjects` beside `AllowedRepoIds` (Acl.cs:18).
   - Each request lays them over the current rows, as the platform path already does (Acl.cs:430-472).
   - The token path's `acl_cache` stores only GitLab ids.
   - `repos.id` becomes AUTOINCREMENT. In SQLite that means a table rebuild in the migration, with `sqlite_sequence` set to the current maximum.
   - `DeleteRepos` and branch pruning clear `acl_cache`.
   - A test reproduces a reused row id inside the 10-minute cache and shows it cannot be read.
2. **Every read puts the allowlist in SQL, on every end.** The allowlist is bound as one parameter:

   ```sql
   repo_id IN (SELECT id FROM repos WHERE gitlab_id IN (SELECT value FROM json_each(@projects)))
   ```

   - It works under `query_only` and removes the 900-id chunking. It also removes the risk of passing 999 parameters in `SemanticSearch` (Queries.cs:768-775).
   - A cross-repository row is checked on both ends.
   - Walks run one indexed query per hop over the frontier. They never pass through a repository the person cannot read: for that person, a path A → C → B with C unreadable does not exist.
   - `GraphQueries.Load`, `impact_of` and the coarse pass of `semantic_search` move to the same predicate.
3. **Resolution at index time never looks past one repository.** GitLab grants whole projects, so one repository's own edges reveal nothing about another. The N and G levels, and the unresolved counts, are computed inside the repository.
4. **Nothing binds across repositories by bare name.**
   - Ambiguous providers are settled only by the caller's own manifests and submodules.
   - Candidates are chosen, and their confidence split, at query time among readable providers. Confidences, counts and "ambiguous" labels therefore never depend on hidden repositories.
   - Repository-link settlement (§2.10) follows the same rule.
5. **Contracts meet only inside the query.** Routes, topics and tables are per-repository claims joined by name at query time among readable repositories. A topic's existence or count in an unreadable repository never shows.
6. **Hidden things are never counted or named.**
   - "Resolved into a repository you cannot read" and "unresolved" fall into one bucket.
   - Graph tools give no access notices.
   - The existing notice stays only for lookups with no readable answer (Tools.cs:134-186), and `ARGUS_ACCESS_NOTICES=0` still turns it off.
7. **Owners and history come only from readable repositories.** Emails are never shown, and the compliance data inventory gets an entry for usernames in owner data.
8. **Secrets are masked before storing.**
   - `.env`, `.properties` and `appsettings` values of secret shape are masked before `files.content` and FTS store them, after the runtime declarations have been read from them.
   - Contract claims keep only hosts and key names.
   - Excerpts pass `SecretMask` and Code Arena's token-shape masks (CodeSearch.cs:646-658).
   - Docs and samples use invented names.
9. **GitLab stays read-only.** Every new call is a GET with the read-only credential or the person's own token, and nothing is written:

   | Call | Scope | Used by |
   |---|---|---|
   | `GET /projects/:id/merge_requests/:iid/changes`, approvals | read_api (already used by the review tools) | `mr_impact`, owners |
   | `GET /projects/:id/environments`, `/deployments` | read_api | §2.9 |
   | Repository files and history | the mirrors already on disk | everything else |

   An estate that grants only `read_repository` sees "deployments not available", and nothing else changes.

**How it is proved.**
- **A static test** fails any Store SQL on a graph table that lacks the predicate.
- **A runtime query recorder** fails any graph SELECT run without the `@projects` parameter.
- **A differential test** builds an index with repositories A, B and C. Someone allowed only A and B must get answers from every graph tool that are byte-identical to an index built without C. It also runs over 50 random allowlists.
- **A property test** checks that every returned row has all its ends in the allowlist.
- **A Playwright e2e** signs in a person with narrow access, who must not see a hidden repository's nodes on the Code map. Each run uses its own `--output`.
- **The rule:** a tool whose tests cannot be made green does not ship.

---

## 5. Storage and scale

**Today.** The index is one SQLite file in WAL mode, which the server opens with `query_only`. Postgres appears only in the optional pgvector backend for vectors (docs/argus/pgvector-backend.md).

**No graph database.**
- Every question is a bounded walk of 1–3 hops, which is a few indexed lookups.
- Access must stay in the same SQL that reads the edges.
- Kuzu, the embedded choice, was archived on 2025-10-10. Neo4j or FalkorDB would be a new server and download.
- Graphicle and codebase-memory-mcp keep code graphs in SQLite, and GitLab Orbit in ClickHouse.

**New and changed tables:**

| Table | Key and indexes | Notes |
|---|---|---|
| `symbols` (changed) | PK (repo_id, key); (repo_id, name); (moniker) | §2.1; upserted by key |
| `symbol_vectors` | (key, text_hash) | Replaces the cascade from symbols |
| `scan_facts` | (blob_sha, versions) | zstd JSON, shared by branch rows |
| `file_imports` | (repo_id, from_file); (repo_id, to_file) | §2.2 |
| `sym_use` | (repo_id, name) | Name postings for incremental re-resolution |
| `sym_edges` | (repo_id, from_key, kind); (repo_id, to_key, kind) | §2.3, §2.4 |
| `file_unresolved` | (file_id) | Unresolved and guess counts |
| `sym_xedges` | (from_repo_id, from_key); (moniker) | §2.5; the target is joined at query time |
| `test_cover` | (repo_id, key); (repo_id, test_key) | §2.6 |
| `components`, `contract_claims` | (repo_id); (kind, norm_name) | §2.7 |
| `owner_rules` | (repo_id) | §2.8 |
| `file_changes`, `cochange`, `deployments` | (repo_id, file_id); (repo_id, file_a) | §2.9 |
| sidecar `edge_verdicts` | (kind, from, to) | Confirm, audited |

**A walk, one hop of callers:**

```sql
SELECT e.repo_id, e.from_key, e.kind, e.evidence, e.confidence, e.n, e.first_line
FROM sym_edges e
WHERE e.to_key IN (SELECT value FROM json_each(@frontier))
  AND e.kind IN ('calls', 'references')
  AND e.confidence >= @min
  AND e.repo_id IN (SELECT id FROM repos WHERE gitlab_id IN (SELECT value FROM json_each(@projects)))
LIMIT 50 * json_array_length(@frontier);
```

Fan-out is capped at 50 per node and 2,000 nodes per answer.

**Size at today's estate** (47 repos, 55,603 files, ~1.2 GB). All figures are estimates, to be measured:

| Part | Estimate |
|---|---|
| Symbol columns | +15% of the symbol table |
| `sym_edges` | 6–9M rows, 0.5–0.8 GB |
| `scan_facts` | ~100 MB |
| Everything else | Tens of MB |

**Gates:**
- index size +50% or less;
- full index time +30% or less (37.8 min today);
- an incremental one-file pass of 2 s or less;
- p95 of 300 ms or less for 1 hop and 1.5 s or less for 3 hops, on the 200-repo synthetic estate and on the real one.

If the size gate fails, the `references` kind goes first, then N edges for the ctags languages.

**Branches.** In-repository edges are stored per branch row. Because scan facts are shared by blob, a branch costs mostly its diff. Cross-repository edges stay between default branches, as links do today.

**Past one box.** The SQL is plain and portable: `json_each` becomes `= ANY(@ids)` on Postgres, through the Npgsql already referenced (Argus.csproj:14). The graph tables move to Postgres only if the measured gates fail at the company's estate of about 200 repositories (docs/plan.md:1184-1186). They do not move this release.

---

## 6. Sharing scanners with Code Arena's code index

**What moves** to `src/CodeIndex/`, a folder of source files that both projects link, as `CodeText.cs` is linked today (CodeArena.csproj:27-31). It uses the base class library only:
- `CodeText`, moved from src/Argus/Util;
- all of `CodeScan.cs`: the C#, TS/JS and Python scanners, tokens, body hash, signature and doc line (CodeScan.cs:11-26);
- `Terms`, the camelCase tokenizer (CodeIndex.cs:12-118);
- `CodeFiles`' `Sensitive`, `TokenShape` and `LanguageOf` (CodeFiles.cs:33-81);
- `CodeGraph`'s resolution rules, behind a new `IFileSet` and `ITextProvider`. Today they read disk through `Files.ReadText` and `File.Exists` (CodeGraph.cs:182-208, 873-895). Argus backs them with `files.content`; Code Arena with the working tree.

**What changes while moving:**
- **One line rule.** Lines break on `\n`, with `\r\n` counted once, as git and ctags count. Argus today splits lines as Python does, also on `\v`, `\f` and others (src/Argus/Util/PyStr.cs:25-40). Stored line references may shift once; no backward compatibility is needed.
- **The receiver fix** from §2.3, with a test.
- **Scanner versions join Argus's per-file stamp.** `SymbolContractVersion` goes to 6 (Ctags.cs:34-40).
- **TS and Python definition-recall tests against ctags**, which must reach 97% or more. Only C# has such a test today.
- **The Dockerfiles copy the new folder:** src/Argus/Dockerfile (today it copies only src/Argus/, :37), src/Llm.Api/Dockerfile:69-70 and deploy/services/workspaces/Dockerfile:14-15. tools/publish-code-arena.sh and CI change with them.

**What is not shared:** Code Arena's on-disk store and sync, its index host, its tools, the git file listing, and `CodeSearch`'s linear scan. Argus's FTS and postings replace the last.

**What each side gains:**
- **Argus gets** symbol uses tied to definitions, inheritance, a sensitive-file filter and body hashes, with no new dependency.
- **Code Arena gets** answers across repositories from Argus, under the same keys: the IDE panels in §3.3, and plan grounding with real tests.

**`.env` files.** Code Arena never reads them, while Argus reads them on purpose for runtime links. The shared `Sensitive` rule therefore takes a mode: Argus reads declarations first, then masks values before storing (§4, rule 8).

---

## 7. Measurement, and the gates before anything is on by default

### 7.1 The baseline harness comes first

`evals/run_argus_graph.py` is built before any graph code. Its tasks are mined from history Argus already mirrors: llm-service, plus one company repository per language family chosen by the owner. Nothing is downloaded. The truth is fixed before any model runs.

**Splits:**
- development and test by the parity of the commit hash;
- a time split, so that co-change and owners use only commits before the task's MR.

**Task families:**

| Family | Truth |
|---|---|
| locate | The 60 code-index tasks, reused |
| callers | Rename a method in a throwaway copy, then build (`tools/dn build`, or `tsc --noEmit -p src/web`). The compiler's errors are the exact call sites. Commits that changed a signature give a recall-only lower bound. |
| impact | Add a required parameter; the compiler's errors are the direct impact. Also: from an MR's first changed symbol, predict its other changed files. |
| hierarchy | "What implements I", from the compiler (§7.2) |
| tests | Test files changed in the same MR |
| owners | The MR's actual approvers and merger, from the read-only API |
| cross-repo | MRs in two repositories that cite the same issue |
| edit (model only) | A historical signature change. Success means every gold call site is updated. A wrong edit is a file outside gold-plus-callers, or a removed symbol whose callers were left. Tests run in the eval sandbox, on llm-service only. |

**Arms:**
- grep only;
- Argus today (37 tools);
- Argus with the graph, through the agent profile;
- Argus with the graph but graph signals off (lexical and FTS only). This is the ablation the code-index measurement lacked.

### 7.2 Without a model

- **`argus graph-eval --symbols TRUTH`.**
  - Lines read `from-id -> to-id calls|inherits|implements|overrides`, and `!->` for negatives.
  - It reports precision, recall and F1 by evidence letter, language and kind, plus calibration buckets with expected calibration error. `--write-priors` and `--apply` work as in the repository mode (GraphEval.cs:8-165).
  - Truth comes from compilers already on the dev box, through two dev-only tools in tools/:
    - the TypeScript 6.0.3 compiler API (src/web/node_modules/typescript) for src/web;
    - the SDK's own Roslyn (~/.dotnet/sdk/10.0.403/Roslyn/bincore) for src/** C#.
  - `graph-eval --sample 300` writes a stratified sheet for hand labelling where no compiler truth exists, such as Python.
- **Contracts.** llm-service is its own truth corpus:
  - src/web's `fetch` calls against Llm.Api's mapped routes, for HTTP;
  - Llm.Api's EF migrations and snapshot, for tables.

  Company routes, topics and tables come through Confirm.
- **Repository links on the company estate.** Through Confirm, which closes Groundwork item 2 (docs/plan.md:2461-2462).
- **Replay.** "Incremental equals full rebuild" replays the last 200 commits of llm-service and compares sorted dumps of every graph table.
- **Retrieval and cost:** hit@k, recall@10, precision@10, MRR, answer tokens, p50 and p95 latency, index size and time.

### 7.3 With a model

- **Gate models:** qwen3.6:27b and Qwen3.8-27B, the models of model-comparison.md and tool-modes.md. Qwen3-4B is reported but does not gate: it passed 0 of 25 locate tasks in both arms of the code-index gate.
- **Runs:** 3 per task per arm, paired, with a bootstrap 95% bound.
- **Corpora:** llm-service and the chosen company repositories.
- **Measured:** pass rate, prompt and completion tokens, rounds, tool calls by name, wrong-edit rate, time, and the share of tasks where the new tools were used.

### 7.4 The gates

Each layer and tool ships behind its switch, and each switch flips in its own commit with the numbers in the commit message and the CHANGELOG.

| What turns on | Every condition must hold |
|---|---|
| Walking an evidence level | S, I, T: precision 0.95 or more, with a lower 95% bound of 0.90 or more, per language family. All walked edges: 0.85 or more. Expected calibration error 0.1 or less. N is walked only if its calibrated precision is 0.5 or more. |
| Call recall | 0.70 or more against compiler truth, for C# and TS |
| A contract extractor | 0.95 or more at strong and 0.80 or more at likely, on llm-service plus confirmed company edges; otherwise candidate-only |
| Contract layers in `repo_map`'s defaults | No precision loss in repository `graph-eval` |
| `change_impact` bands, without a model | recall@10 at least 15 points above today's `change_impact`, with precision@10 no lower. Tests hit@5 0.6 or more. Owners hit@3 above the most-recent-committer baseline. |
| `ARGUS_SYMBOL_GRAPH`, resolved `find_references`, the agent profile, Code Arena's grounding | For both gate models on both corpora: success lower bound −2 points or better against Argus today, and either 20% fewer prompt tokens or +5 points of success or function Acc@10. Median prompt tokens per task no higher than Argus today (a hard gate: the code-index arm failed by doubling them). No family more than 5 points worse. No regression above 2 points on tool-modes' docs tasks or the code-index locate set. New tools used in 60% or more of graph tasks. Wrong-edit rate no higher. |
| Anything at all | §5's cost gates; the access tests in §4 green; the replay test green |

**What ships on without a model gate:**
- the access fixes and the SQL predicate;
- secret masking;
- stable keys and the storage changes;
- the pages, IDE panels and review card, which show evidence letters and keep below-gate edges behind "show possible".

**If a gate fails:** that part stays behind its switch, the negative result is published, and the failed condition decides what comes next.

Results go to docs/measurements/argus-graph.md, each with its date, commit and models.

---

## 8. What needs the owner's yes

**Nothing on the default path is a download:**
- the shared scanners are base-class-library C#;
- ctags is already in the image (src/Argus/Dockerfile:29);
- YamlDotNet, ZstdSharp.Port and Npgsql are already referenced (Argus.csproj:12-14);
- SQLite's `json_each`, FTS5 and recursive CTEs are built in;
- cytoscape, fcose and elkjs are already in src/web's lockfile;
- the truth oracles (TypeScript 6.0.3 and the SDK's Roslyn) are already on the dev box.

| # | What | Size | Why | When |
|---|---|---|---|---|
| 1 | Loading qwen3.6:27b and Qwen3.8-27B on the live stack for the model runs. No download if their weights are still on disk from 2026-10-26, but it changes what the stack serves during the runs. code-index.md:51-52 left loading another model to the person. | – | §7.3 | Commit 5 (baseline) and commit 29 |
| 2 | jedi + parso wheels, as a Python truth oracle (alternative: @sourcegraph/scip-python 0.6.8, 20,318,820 bytes unpacked, licence to check) | About 1.7 MB (estimate; exact sizes checked before asking) | Python precision beyond a hand-labelled sample | Commit 12, optional |
| 3 | A second corpus, if the company repositories are not enough: the flask and zod clones already pending from the code-index plan (12.2 MB and 27.3 MB as GitHub reports) | About 40 MB | Model gate on a second repository | Commit 29, optional |
| 4 | Open-source architecture corpora, only if Confirm gives too few contract labels: microservices-demo (38,292 KB), dotnet/eShop (27,435 KB), train-ticket (82,675 KB), as GitHub reports | About 145 MB | Contract extractor precision | Commits 20–22, optional |
| 5 | TreeSitter.DotNet 1.3.0 | 53,401,399-byte nupkg, about 11 MB of grammars per RID | Only if a language's definition recall measures below 97% | Not planned |

**Decisions that are not downloads:**
- which company repositories form the corpus (one per language family);
- whether this is the next release: docs/plan.md:2434 holds "v7.7 — A model of your own";
- each default flip, after the numbers are seen.

---

## 9. Commits, in order

Each commit is small, tested and shippable on its own. It passes Argus.Tests, and CodeArena.Tests where shared code moves. It is pushed and redeployed live before the next. Playwright runs each get their own `--output`.

| # | Commit | What it changes | How it is verified |
|---|---|---|---|
| 0 | Plan Argus's graphs | docs/design/argus-graph.md and a docs/plan.md entry | compliance-docs.py, release-scan.py |
| 1 | Access by GitLab project ids | `AllowedProjects`; the token-path cache keeps GitLab ids only; `repos.id` AUTOINCREMENT by table rebuild; `DeleteRepos` and pruning clear `acl_cache` | A test of a reused row id inside the 10-minute cache; an upgrade test on an index with data |
| 2 | Access in SQL for today's graph | The `@projects` predicate in `GraphQueries.Load`, `impact_of` and `semantic_search`'s coarse pass | Static predicate test; query recorder; the A/B/C differential test on `repo_map`, `system_map`, `dependency_path`, `change_impact` and `impact_of`; semantic recall for a narrow reader |
| 3 | Settlement by local evidence | Index time: own manifests and submodules only; the rest among readable providers at query time | Differential test; GraphTests' 469 synthetic links unchanged |
| 4 | Secrets masked before storing | `SecretMask` in Writes after declarations; contract bump | A fixture shows runtime links unchanged and no secret-shaped value in content or FTS (generated fake shapes only) |
| 5 | Baseline harness | evals/run_argus_graph.py, the families and splits; arms grep and Argus today | `--check` regenerates identical tasks; baselines in docs/measurements/argus-graph.md |
| 6 | Shared scanners | `src/CodeIndex/`; one line rule; receiver fix; three Dockerfiles, publish script, CI | Both suites green; TS and Python recall ≥ 97% against ctags; receiver test |
| 7 | Stable symbols | Migration 024 (key, qualified, parent_key, lang, body_hash, visibility, moniker); upsert by key; `symbol_vectors`; ctags `+ia`; no ctags on markdown and text; contract 6 | Keys survive an edit elsewhere in the file and a file move; unchanged symbols are not embedded again; upgrade test with data |
| 8 | Scan facts by blob | `scan_facts` reused by branch rows and vendored copies | Index size, and a second branch's index time, measured on the 47-repo estate |
| 9 | File imports | New file types; `file_imports`; blanked JS/TS, Rust and `.proto`; `impact_of` on it | One test per resolution form; a commented import makes no link; one query per depth |
| 10 | Uses in a repository | `sym_use`, `sym_edges`, S/I/T/N/G, `file_unresolved`, incremental re-resolution | Fixtures per language; the replay test (incremental equals full); rows, size and time in the measurements doc |
| 11 | Type hierarchy | inherits, implements, overrides, implements_member; callers via interface | Fixtures for C# `override`, TS `implements`, Python bases, Java via ctags |
| 12 | `graph-eval --symbols` | Truth format, reports, calibration, `--write-priors`, `--sample`; tools/ truth generators (Roslyn, TS API) | First C# and TS precision tables committed; priors calibrated |
| 13 | Graph tools behind `ARGUS_SYMBOL_GRAPH` | `find_symbol` changed, `symbol_graph`, `find_references` resolved with a truncated flag, `index_status` health | Output shape, budget and folding tests; readable-only counts; differential test extended |
| 14 | Tests graph | Markers, covers (≤ 2 hops, ≤ 20), named_for; `relation=tests` | Fixtures per framework; the tests family measured |
| 15 | Across repositories | Exports with monikers; `sym_xedges`; candidates at query time | Differential test covers them; `graph-eval` on a two-repo C# pair |
| 16 | Owners | CODEOWNERS with GitLab rules; Backstage as YAML; approvers; stale flag | Section, `^optional`, `[n]` and last-match cases; no email in any answer; the owners family measured |
| 17 | History | Commit→MR→issue joins; in-repo co-change; `relation=history` | Time-split evaluation of "also changed"; the first `cochange` rows |
| 18 | `change_impact` bands and `mr_impact` | Symbol, files and diff targets; MR read-only; tests, owners, `not_searched`; Code Arena grounding behind its own setting | The impact family against today's `change_impact`; PlanProve-style tests in CodeArena.Tests |
| 19 | Incremental repository graph | Includes, deps and links recomputed only for changed repositories and their neighbours | Replay test; time on the 200-repo synthetic estate and the real one |
| 20 | Components and HTTP | Component detection; OpenAPI operations; routes; clients with base URLs; matching; `service_contracts` v1 | One fixture per framework; llm-service's src/web→Llm.Api routes as truth |
| 21 | gRPC and messaging | `.proto` services and methods, servers and stubs; Kafka as claims, RabbitMQ, NATS, SQS/SNS, Service Bus; contract-copy drift | Fixtures; synthetic estate cases |
| 22 | Tables | `.sql` indexed; DDL; Flyway, EF migrations and snapshot, Alembic; ORM mappings; SQL strings | llm-service's EF model as truth |
| 23 | Deployments | Environments and deployments (read-only, cached); deploy drift in the symbol card, `change_impact` and `mr_impact` | Fake GitLab tests; "not available" without read_api |
| 24 | Agent profile | ≤ 2,500 characters of definitions plus the skill text; chosen by model size in chat, code mode and Code Arena | A size test on the definitions; ChatTools tests |
| 25 | Code map page | cytoscape and fcose promoted; symbol card, ego graph, table view; Llm.Api calling as the person, no cross-person cache | Playwright with a narrow-access person; vitest for the table view |
| 26 | Impact view, Service map, Graph health, Confirm | elkjs layered view; drift lists; admin page; verdicts in the sidecar, audited, exported as truth | Playwright; Confirm refused when one end is unreadable |
| 27 | Review card and IDE panels | Impact card in automatic reviews (setting); Call and Type Hierarchy and cross-repo references in Code Arena's IDE | Review tests; Code Arena web tests |
| 28 | Repository `graph-eval` on the company estate | From Confirm verdicts; priors calibrated | Only aggregate numbers in docs; closes Groundwork item 2 |
| 29 | Model measurement | Every family × four arms × two gate models × 3 runs; flips only for gates met, each in its own commit | §7.4, results in docs/measurements/argus-graph.md |
| 30 | Docs and release | graph.md, overview.md, tool-modes.md and ToolCatalog.cs:1 (37 → 40, not 34), clients.md, roadmap.md rewritten (no stale "not multi-tenant/HA"), CHANGELOG with measured numbers; tag, then `gh release create` with title and changelog | compliance-docs.py, release-scan.py |

**The headline is reachable at commit 27.** If time runs short, drop from the end in this order: 23, 22, 21, then 20.

---

## 10. Risks

1. **Wrong edges from name resolution.** Overloads, dynamic dispatch, duck typing and Go interfaces can mislead, and S edges have never been measured anywhere.
   - Evidence letters on every row; G never walked.
   - Priors calibrated by `graph-eval --symbols`; the precision gates.
2. **Recall gaps** from dependency injection, reflection, routes in strings and hosts chosen at run time. Expected recall is 60–75%.
   - Every answer says "not found does not mean not used" and gives the lexical count.
   - Unresolved ends are listed.
3. **Leaks across access** through counts, candidate splits, settlement or walks.
   - Index-time work stays inside one repository; splits happen at query time.
   - The static test, the recorder, the differential and property tests, and the e2e gate every tool.
   - The token cache is fixed first.
4. **Growth.** The 6–9M-edge estimate may be wrong, and the gates may force fewer edge kinds. The order of what goes first is set in §5.
5. **Hub symbols** (loggers, `ToString`, helpers) blow up walks.
   - Fan-out caps and hub folding.
   - Calls into the standard library and third parties are left out.
6. **The model gate may fail.** Code Arena's index failed with Qwen3-4B, using twice the tokens.
   - The gate uses 27B models, token use is a hard gate, and the ablation separates the graph's own share.
   - The tools stay behind the flag; the pages, IDE and review card still ship.
7. **Uneven languages.** Call precision reaches the scanner level only for C#, TS/JS and Python. The others get ctags hierarchy and name-level calls, and answers say so.
8. **The shared scanners couple two products.** A scanner change re-reads every file, about 38 min at 47 repos, and line numbers shift once. Both test suites run on every change to `src/CodeIndex`.
9. **Too many tools.** The chat's tool budget is already exceeded.
   - Three new tools, one typed traverse, no query language.
   - The agent profile fits in 2,500 characters.
10. **Contract rules are text rules.** Templates collide and dynamic names are missed.
    - Matching only between linked services or within a broker or database.
    - A gate per extractor; unresolved sites counted.
11. **Masking `.env` values could drop runtime links.** Declarations are read before masking, and a fixture test checks the links.
12. **Co-change is noisy and can leak the future into evals.** It is a ranking signal only, never walked, and evaluated with a time split.
13. **Owner data is personal.** Usernames only, through readable repositories only; recorded in the compliance data inventory.
14. **More GitLab API reads.** Cached, rate-limited, lazy, and stale-tolerant while GitLab is down, as the ACL path already is.
15. **Scope.** 31 commits plus calibration and model runs, while v7.7 holds the model lab. The commits from 20 onward are the first to move to a later release.

---

## 11. What is deliberately not in it

- **Taint traces from Opengrep.** `Scanners.Known` lists opengrep (src/Argus/Scanning/Scanners.cs:25), but nothing passes `--taint-intrafile`, and the installed binary was not checked for the flag. Data flow is a release of its own.
- **A Roslyn semantic tier for C#.** About 27 MB of new packages in the image and 1–2 GB of RAM per large project, with no measured gain. The SDK's Roslyn already serves as the truth oracle.
- **SCIP ingest and a precise P tier.** Almost no estate CI produces `index.scip`, it would need read_api artifact reads, and the local compilers already serve as the yardstick. It is a later add-on.
- **Type chains longer than one link, and monikers for Go, Java, Kotlin and Rust.** They widen only if `graph-eval` shows the precision holds.
- **Merging Argus with Code Arena's local index for unsaved edits.** The panels ship first.
- **A port of Code Arena's `code_search` ranker.** It duplicates `search_code` and `semantic_search`, and its model gate failed.
- **Separate `tests_for`, `owners`, `flow` and `code_outline` tools.** They are folded into `symbol_graph`, the impact tools and `service_contracts`.
- **Cross-repository co-change** by shared issue key or branch name, **personalized PageRank** for the repository maps, and **coverage edges** from JUnit artifacts.
- **Hand-drawn SVG, and pages in the standalone argus-web.** Adding the graph libraries there would need a yes.
- **A Postgres map store with row-level security as a second store.** The SQLite predicate plus the tests give the same guarantee. It would be reconsidered only if the measured gates fail.
- **3,000-project targets and valid-from/valid-to dates on every row.** Both are speculative next to the 47 repositories measured.
- **Column-level lineage, GraphQL, Avro, Terraform, Helm template reading, config-key edges, and gateway routes** (Ingress, nginx, YARP, Ocelot, Traefik).
- **Drift beyond contract copies and deployments.** Version skew needs package versions, which are not modelled. Orphan, dead-call and undocumented findings move with extractor recall and would be noise.
- **A person-activity graph from git authors.**
- **Removing or renaming `change_impact`.** Code Arena and the business mapper call it.
- **Joern, CodeQL** (its licence forbids this use), **scip-java and scip-clang** (they need builds), **Neo4j, FalkorDB and Kuzu**, and **any free-form query language**.
- **Qwen3-4B as a gating model**, and **`code=true` packing as a default.**
