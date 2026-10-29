# Code Arena's own code index and code notes: design

For the request of 2026-10-30: a tool like Graphicle in Code Arena, "to help search repo faster to find
everything in files with less tokens". Line numbers are as of commit be74af35.

This is a design only. Nothing in /home/ghadyani/llm-service was changed, built or installed. I checked every repository claim below against the tree at `be74af35`. The uncommitted gateway work in that tree (`AppDbContext.cs` and the other modified files) was left alone.

**Summary**
- **What it is:** a local index of the working tree, written in C# with only the base class library.
- **Where it lives:** in Code Arena's data folder, never in the repository.
- **How code is read:** one scanner per language family, built on Argus's `CodeText.Blank`, which is linked in as a source file.
- **What the agent gets:** three read-only tools (`code_search`, `code_symbol`, `code_outline`), plus a `code` parameter on the existing `remember` tool.
- **Downloads:** none on the default path.
- **When it turns on:** it ships off (`codeIndex: false`). It becomes the default only after a benchmark against today's Code Arena (grep, glob, read_file, run_shell) passes the gate in section 6. Graphicle's own published result is the bar to beat: 16% *more* tokens than plain Claude Code, with its tools never called.

---

## 1. What it does, and what it deliberately does not do

### What it does for the agent
- **One call answers "where is the code that does X?"** It returns the few places that matter, each cited as `path:start-end`, with the kind of symbol, its qualified name, and the current lines from disk. The lines use the same `%6d\t` numbering as read_file, so the agent can call `edit_file` or `read_file offset=` directly.
- **One call answers "where is `Name` defined and used?"** It returns the definitions, plus every use in code grouped by the function each use sits in. For `ToolDef` that is "19 in Tools.cs: ToolBox.Offered, ToolBox.ForCall, …" instead of 61 grep lines.
- **One call answers "what reaches this?"** It walks callers and references up to two steps out, and names the tests among them.
- **Packed to a budget.** Answers are sized in tokens using the session's own measured characters-per-token (`Agent.CharsPerToken`, Agent.cs:1274-1283, learned from the gateway's counts, default 3.5). They are capped below `KeepWhole`, so the answer is never set aside as a kept result or cut (Agent.cs:1057-1063, 1080-1083).
- **No repeats.** Excerpts already sent in this context come back as one-line references. That record is cleared when `ClearEarlier` or compaction clears earlier results (Agent.cs:1092, 1168-1178).

### What it does for the person
- Fewer rounds and fewer whole-file reads, so answers are faster and cheaper.
- Today, each round re-sends the whole context. read_file returns up to 2,000 lines (LocalTools.cs:11) and grep up to 200 lines (LocalTools.cs:13).
- The IDE gets "Go to Symbol in Workspace…" with no language server installed. Today the IDE only has Go to Symbol in Editor (app.tsx:1058).
- Notes and decisions can be tied to the code they are about. They are marked when that code changes. The agent only proposes them; the person keeps them.

### Keeping grep's strengths
The criticism we must answer is that memory systems "did worse than the raw harness given ripgrep" (theLiminator, HN). Here is how each of grep's strengths is kept.

| grep's strength | How it is kept |
|---|---|
| Exhaustive | grep, glob and read_file stay exactly as they are; nothing hooks, blocks or rewrites them. Ranked answers say they are ranked and give the grep that finds every occurrence. Name answers really are complete for code: the index only narrows down which files to look in, then scans those files as they are on disk now. |
| Regex, text in comments and strings | Left to grep, and the tool descriptions say so. Name answers state that comments and strings are not counted. |
| Always fresh | Before every answer, files changed on disk are synced. A file whose size or modification time differs from its record is re-read before any of its ranges are cited. Excerpt lines are always read from disk at answer time, never from the cache. |
| Transparent | Every item says *why*: the words matched, and the edge followed with its evidence level. The output is deterministic. |
| Zero setup, never broken | The first build runs in the background. Until it is done, answers say how far it has got and fall back to grep. If the index fails, the tool runs the equivalent grep and says so (fail open). |
| Small output | The default budget is 2,000 tokens. A name answer is never larger than `grep -n -w` for the same name (tested). |

### What it deliberately does not do
- **No embeddings and no model calls to build or query it.** Graphicle measured file top-5 accuracy of 75 against BM25's 61, but its agent runs showed no saving. Embeddings stay a possible later arm, measured first.
- **No always-on repo map in the system prompt.** That would break the stable prefix Code Arena keeps for the prompt cache (`ToolBox.InPlace`, Tools.cs:381-392). `code_outline` is the map, on demand.
- **No guard hooks.** It never steers the agent away from grep or read. tokenix's A/B test found that outlines hid usages during renames.
- **No type-checked call graph claims.** Every call edge carries an evidence level; anything left unresolved is counted and reported.
- **No cross-repository graph.** That remains Argus's job, over GitLab-indexed code.
- **No edits by symbol** (Serena-style). Edits stay `edit_file` with exact text.
- **No index inside the repository.** So there is no `.gitignore` to write, and the index never shows up in `git ls-files --others`, and so never in glob or grep.
- **No secrets indexed.** See section 3.
- **No new memory store.** Code notes are ordinary Arena memories (or offline lines, or `.remember/` lines) that carry anchors; see section 5.
- **No communities, "god nodes" or reports.**
- **No unsaved editor text.** The IDE already sends unsaved text as `<file unsaved="true">` blocks (Prompt.cs:43).

---

## 2. Parsing: tree-sitter or a parser written in C#

### The options

| Option | What it costs | What it gives |
|---|---|---|
| **tree-sitter via TreeSitter.DotNet 1.3.0** (nuget.org, MIT, 2026-01-22, netstandard2.0, no dependencies) | **A download that needs the person's yes:** the nupkg is **53,401,399 bytes**. It holds 25–30 grammars per platform (63.6–71.6 MB of native code per RID). The grammars we would keep, per RID on linux-x64: core 242,656 B; c-sharp 5,110,072; typescript 1,443,832; tsx 1,476,584; javascript 440,000; python 489,352; go 237,928; java 437,768; rust 1,145,296. That is ≈ **11.0 MB per RID** for all eight, or ≈ 9.2 MB for the first wave, across 5 RIDs. Other RIDs: c-sharp is 5,119,208 on linux-arm64, 5,206,528 on win-x64 and 5,090,336 / 5,171,504 on osx-x64 / arm64. The package would also have to go into `tools/offline-nuget` and `tools/fetch-code-arena-packs.sh`, because `--offline` restores only from there (publish-code-arena.sh:41-69). Its native libraries are extracted at run time (`IncludeNativeLibrariesForSelfExtract`, CodeArena.csproj:41), and we would pin the grammar ABI. | Exact syntax trees. But still no binding resolution: calls, imports and ranking are ours to write either way. |
| **SQLite** (a storage option, listed here because it is the other native dependency) | Microsoft.Data.Sqlite.Core 10.0.12 plus SQLitePCLRaw.bundle_e_sqlite3 / config / provider / core 3.0.5 plus the SQLite 3.53.4 natives (nupkg 27.5 MB; per RID 1.47 MB linux-x64, 1.53 MB linux-arm64, 1.97 MB win-x64, 1.72 / 1.70 MB osx). These are already in the local NuGet cache because Argus uses them, so nothing new would be fetched from the internet. They would still go into offline-nuget and end the "base class library only" rule (CodeArena.csproj:3-4). | FTS5's BM25. But its tokenizer does not split camelCase, so we would tokenize before inserting anyway. |
| **C# scanners, one per language family** | No packages. It does need the comment and string blanker, which Argus already has: `src/Argus/Util/CodeText.cs` uses only `System.Text`, and it handles C# verbatim, interpolated and raw strings, Java text blocks, Go raw strings, TS/JS template holes and Python triple quotes (CodeText.cs:30-31, 214-291). Its tests stay in `tests/Argus.Tests/CodeTextTests.cs`. | Declarations, ranges, imports and call tokens accurate enough for ranking and grouping, which is what the tools need. Weak spots are listed below. |
| ctags (what Argus uses, Ctags.cs:96) | An outside program. It is not on this machine (`ctags: command not found`) and not shipped on Windows or macOS. | Used only as an independent check in tests and the benchmark, where CI installs it (ci.yml:34 does so for the argus job). |
| Language servers (LanguageServers.cs, 13 servers) | Each must be installed; they run only in a trusted folder and only while the IDE page holds the socket (WebLsp.cs). | Exact references. A possible later "precise" arm, measured. Not version 1. |

### Recommendation: write the scanners in C#
1. **It keeps the one-file, base-class-library build intact.** That means no offline-nuget change, no Dockerfile package step, no native code extracted for 5 RIDs, and no 53 MB download.
2. **Tree-sitter only solves the easy half.** The hard part, resolving names to definitions, is the same code either way. The ranking needs symbol ranges and identifiers, not full trees.
3. **The comment and string blanker already exists, and it is tested.** Link it the way `HistoryWords.cs` is linked (CodeArena.csproj:22-25). Only `CodeText.cs` is linked; Argus's `Parsers.cs` and `Links.cs` need Sqlite-touching splits and solve a cross-repository problem, so they are not taken.
4. **It is measured, and tree-sitter is the fallback.** Each scanner sits behind an `ICodeScanner` interface. Tests compare its definitions with universal-ctags in CI. If any language's definition recall stays under 97%, or the benchmark traces misses to parsing, commit 23 swaps in tree-sitter for that language, with the person's yes.

### How a scanner works
- Run `CodeText.Blank` (with `keepStrings: true` for import strings). Lines and columns are kept.
- Tokenize the result. Track bracket depth and a stack of containers.
- At `{`, `=>` or `;`, classify the "statement head", meaning the tokens since the last `;`, `{` or `}` at that depth.

**C#**
- File-scoped and block namespaces.
- class, struct, interface, enum, record [class|struct], with primary constructors, generics, base lists and `where` clauses.
- Methods, constructors, local functions, properties (block or `=>`), indexers, events, operators, delegates, fields and consts.
- Attributes are skipped.
- Both branches of `#if` are scanned, and duplicate declarations are removed.
- Top-level statements become a `<top-level>` symbol.
- A symbol's range runs from its first line (attributes included) to its matching `}` or `;`. The signature is one line, at most 200 characters.
- The doc's first line comes from the `///` or `//` comment lines directly above, in the original text.

**TS / JS / TSX**
- `function`, async and generator functions, `class … extends … implements`, `interface`, `type X =`, `enum`, arrow functions and function expressions bound to `const` / `let`, class members (`get`, `static`, `#private`), `export default`.
- PascalCase functions are tagged as components.
- Object-literal methods are skipped (noise).

**Python**
- Indentation-based.
- `def` and `async def`, `class Name(bases):`, with the range starting at the first decorator.
- Nested definitions, UPPER_CASE module constants, and the `__main__` block.
- A symbol ends at its last line indented deeper than its header.

**Markdown** headings become `heading` symbols running to the next heading of the same or higher level. Any other text file is indexed by file plus 60-line windows, with no symbols.

**Known weak spots, tested and measured:**
- An apostrophe in JSX text (`<p>don't</p>`) can open a string for the blanker. The scanner checks that brackets balance per file; if they do not, it re-scans the JSX regions with JSX text treated as text, and marks the file's symbols as approximate.
- C/C++ macros are not covered; that family comes later.
- Python's dynamic dispatch is not resolved.

### Languages first
- **First wave:** C# (1,198 files here), TypeScript, TSX and JavaScript (688), and Python (100). These make up this repository, and they lead Argus's language table (Parsers.cs:10-34).
- **Second wave:** Go, Java and Kotlin (close to C#), then Rust.
- **Everything else** is searchable as text from the first commit, without symbols.

---

## 3. The graph and the index

### Nodes
- **File**
  - The relative path, with `/` separators, as `Workspace.Show` prints it.
  - The language, size, modification time and SHA-256.
  - Flags: test (`tests/`, `*Tests.cs`, `*.test.ts(x)`, `test_*.py`, `*_test.go`), generated (`*.Designer.cs`, `*.g.cs`, `// <auto-generated`, `@generated`; this repo has 129 migration `Designer.cs` files, up to 321 KB each), vendored (`vendor/`, `third_party/`), minified (average line over 300 characters), docs, text-only, and skipped-sensitive.
- **Symbol**
  - A stable id `path#Qualified[~n]`, for example `src/CodeArena/Tools.cs#ToolDef` or `#Foo.Bar~2` for the second overload.
  - Kind, name, qualified name, signature, start and end lines, parent, visibility and the doc's first line.
  - **Body hash:** the first 8 bytes of the SHA-256 of the symbol's blanked text with whitespace collapsed. It changes only when code changes, not comments or formatting.
- **Namespace / module:** the C# namespaces merged across files, each Python module, and each Go package folder.

### Edges
Every edge carries an evidence level: `scope`, `type`, `name` or `guess`.
- `contains`: implied by each symbol's parent.
- `imports`: file to file, or file to namespace/module.
- `inherits` / `implements`: from base lists, `extends` and `implements`.
- `calls`: an identifier followed by `(` or `<…>(`, including `new T(`.
- `references`: a type used in `T.Member`, a base list, a generic argument, a declaration or a cast.
- **Unresolved uses** (only the name is known) are kept and counted. "Callers of X" always says "+ N unresolved calls named X".

### How calls and imports are resolved
Candidates are tried in order:
1. **`scope`: what the code's own context says.**
   - **C#:**
     - the enclosing type and its bases within the repository (partial classes merged);
     - `this.` and `base.`;
     - a type receiver (`T.M()`, `new T()`), resolved through the file's namespace, its parent namespaces and its `using` / `using static` / alias / `global using` directives;
     - extension methods in namespaces the file uses.
   - **TS/JS:**
     - an imported binding is followed to its exporting file, through up to 3 re-export hops;
     - relative specifiers try `.ts`, `.tsx`, `.js`, `.jsx` and `index.*`;
     - `compilerOptions.paths` is read from tsconfig, following `extends` and `references`. Here that is `"@/*": ["./src/*"]` in src/web/tsconfig.app.json:18.
     - `System.Text.Json` reads tsconfig with comments and trailing commas allowed.
   - **Python:**
     - `from m import f` resolves to f in module m;
     - `import m` followed by `m.f()` resolves to m.f;
     - `self.m()` resolves to the class and its bases;
     - relative imports and `src/` layouts are handled.
2. **`type`: the receiver's declared type, when it is written in the same file.** Fields (`ToolDef _t`), parameters, `var x = new R(…)` and `R x =`. The callee is then looked up among that type's members.
3. **`name`: the name is defined exactly once.** Non-test code is preferred.
4. **`guess`: two or three definitions share the name.** They are ranked by the nearest common folder, then namespace closeness, and all of them are kept and labelled.
5. More than three candidates: the use stays unresolved.

### Ranking
Every arm is computed in memory at query time.

**Tokenizing**
- Identifiers are split on camelCase, PascalCase, snake_case and digits, and the whole identifier is kept too: `RunToolsAsync` gives run, tools, async and runtoolsasync. Everything is lowercased.
- Words of one character are dropped, along with code keywords per language and English stop words.
- A light stemmer removes plurals and -ing/-ed. A Porter stemmer is a benchmark arm.

**The arms**
- **L**, BM25 over symbol fields (k1 1.2, b 0.75). Each field's term frequency is weighted:

  | Field | Weight |
  |---|---|
  | name | ×3 |
  | qualified name and path | ×2 |
  | signature and doc | ×1.5 |
  | body identifiers | ×1 |
  | comment and string words | ×0.7 |

  Strings are included because people search for error messages.
- **F**, BM25 over whole files. This reuses the per-file term lists.
- **Q**, query coverage: the share of query terms found in the symbol's *current* lines, computed on the 30 best files read from disk, at most 8 MB per query.
- **G**, graph proximity: the seeds are the top 3 symbols by L. A candidate scores max(w × seed score / hops) over at most 2 hops, with w = 0.5 for calls or called-by, 0.4 for contains or inherits, and 0.2 for a file import.
- **P**, priors (a multiplier):

  | Prior | Multiplier |
  |---|---|
  | test | ×0.6 (×1 when the query mentions tests) |
  | generated or vendored | ×0.3 |
  | docs | ×0.7 (×1 when the query asks for docs) |
  | changed in the working tree (`git status`) | ×1.25 |
  | read or edited in this session | ×1.15 |

**The score:**
`score = (L̂ + 0.5·F̂ + 0.6·G + 0.3·Q) × P`
The hats mean normalised by the maximum. These weights are only a starting point; they are tuned on the benchmark's development split and reported on the held-out split. Reciprocal rank fusion with k = 60 is an alternative arm.

**Selection** is greedy, with diversity:
- at most 2 excerpts per file on the first pass;
- overlapping ranges are merged;
- a direct neighbour of result [1] is added if the budget allows, as "called by [1]".

**Identifier route**
- A query takes this route when its words are code names: a call `x()`, a `::`, `#` or dotted path, an underscore, an inner capital, backticks, or a single capitalised word *that the index knows as a symbol*.
- It takes at most 4 names, and the rest of the query must be lookup words (where, defined, used, callers…).
- The answer is the definitions, plus every use in code grouped by enclosing symbol.
- **This route is complete for code by construction.** File postings narrow the candidates, then those files are scanned as they are now. Uses inside comments and strings are left out, and the answer says so.

### Storage (no packages)
**Location**
- `<DataDir>/index/<sha16>.bin`, where sha16 is the same as WorkspaceState's: SHA-256 of the root, first 16 hex characters (WorkspaceState.cs:28-29).
- Each extra `--add-dir` root gets its own file. `code_search` covers every allowed root.
- The folder is created with `PrivateFiles` (0700 / 0600, Config.cs:485+).

**Format**
- A header: magic `CAIX`, format version, scanner versions per language, root, git HEAD, time of writing.
- Then a string table, file records (with each file's imports and the names its code uses), symbol records, and each file's term list (term id plus weighted term frequency).
- **Edges are not stored.** `CodeGraph` works them out when asked, from the files as they are now: the file's names narrow where a symbol is used, and those files are scanned again (kept while their size and time stay). Measured on this repository: the lookup tables take 17 ms to build; resolving every use in src/CodeArena's 85 C# files takes 0.7 s (8 ms a file), src/web/src/code-arena's 106 TypeScript files 0.4 s; one symbol's uses across the folder take 0–100 ms (`KeptResults.Keep`: its 2 callers, `Agent.RunToolsAsync` and `Agent.ClearEarlier`, both `scope`, in 93 ms).
- Global postings are not stored; they are rebuilt when the file loads. An incremental update therefore only replaces one file's record.

**Writing**
- Written atomically (temp file, then move, as WorkspaceState.cs:103-116 does), debounced 10 s after the last change, and again at session end.
- If two sessions on one folder both write, the last writer wins. That is harmless, because the file is derived data.
- An unreadable file, or a new format or scanner version, is rebuilt with a one-line note.

**Expected size for this repository** (2,455 tracked files, 42 MB of cs/ts/tsx/py text, 975k lines): about 10 MB on disk and under 60 MB in memory. Commit 8 measures both.

### Incremental updates
- **At session start:** load the file, then sync in the background, on at most ProcessorCount/2 threads.
  - The file list comes from `Workspace.FilesUnder`, so the index sees the same files grep sees (Workspace.cs:143-156, which uses `git ls-files --cached --others --exclude-standard`).
  - Each file's size and modification time are compared with its record. A difference triggers a SHA-256 check, and a re-parse only when the hash differs.
  - Tools wait at most 2 s for the first build. After that they answer from what is built and say what is left.
- **In the IDE:** the index reads WebIde's existing `DiskWatch` with its own cursor, `SinceAsync(cursor, TimeSpan.Zero)` (DiskWatch.cs:139-164), so DiskWatch needs no change.
  - `all` (an overflow) triggers a full sync by size and time.
  - A change to `.git`'s HEAD or index (the `""` path) triggers the same.
- **In the terminal:** the index starts its own `DiskWatch`. Where the system will not watch (`Watching == false`), it syncs by size and time before each query, at most once a second.
- **After the agent's own actions:**
  - `BeforeWrite` (ToolContext) marks the edited path dirty right away.
  - `run_shell` marks everything "maybe changed", so the next query syncs by size and time first.
- **Large change sets:** up to 200 changed files are synced inline. Beyond that, answers come from unchanged files plus a direct scan of the changed ones, labelled "N files re-indexing".

### .gitignore and secrets
**.gitignore**
- The index uses the same listing as grep, so .gitignore holds exactly as it does for grep.
- Outside git, the same fixed skip list applies (Workspace.cs:261-265).
- The person's config can only narrow the set: `codeIndexExclude` globs (never indexed) and `codeIndexLower` globs (indexed but ranked ×0.3, CodeGraph's "deprioritize"). A repository can never widen what is indexed.

**Secrets: the file tools have no protection today** (read_file, grep and glob read a `.env` that git does not ignore). The index adds its own filter, adapted from Graphify's three-stage `_is_sensitive`. Matching files' contents are never indexed, and `/index` lists them as skipped:
- **Folders:** `.ssh`, `.gnupg`, `.aws`, `.gcloud`, `secrets/`.
- **Names:**
  - `.env*` (except `.example`, `.sample` and `.template`);
  - `*.pem`, `.key`, `.p12`, `.pfx`, `.crt`, `.der`, `.p8`;
  - `id_rsa*`, `.netrc`, `.npmrc`, `.pypirc`, `.git-credentials`;
  - names ending in `secret`, `token`, `password`, `credential` or `private_key`.
- **Content:**
  - `-----BEGIN … PRIVATE KEY-----`;
  - token shapes: `AKIA[0-9A-Z]{16}`, `ghp_`, `github_pat_`, `glpat-`, `xox[abprs]-`, `sk-` followed by 20 or more characters.

Inside code files, string literals of those token shapes are masked in excerpts ("«secret-like value hidden»"). The agent reads the file itself if it really needs the value.

**Permission rules:** each cited path also passes the person's deny rules as a `read_file` call would (`PermissionRules.Denied`, Rules.cs:62). A denied file's excerpts are left out, with a count of how many were hidden.

### Limits
- 100,000 files and 1 GiB of source per root. Past that, the index stops and says so.
- Text indexing for files up to 2 MB, the IDE search's limit (Ide.cs:39-46). Symbols for files up to 1 MB, Argus's limit (ArgusConfig.cs:61-67). grep keeps its own 5,000,000-byte limit.
- At most 20,000 symbols per file and 2,000,000 per root.
- Binary files are skipped (`Files.LooksBinary`, Workspace.cs:351).
- Minified files are text only.
- A query reads at most 8 MB from disk.

---

## 4. The agent's tools

Three tools, all `Kind = ToolKind.Read`. That means no prompts, and they are allowed in plan mode, in sub-agents (`ReadOnly`, Tools.cs:67; `ForSubAgent`, Tools.cs:381) and in code mode as `arena.local.code_search(…)` (CodeMode.cs:113-115, 171).

They are registered in `Runtime.InitAsync` right after the local tools (Runtime.cs:370) when `Config.CodeIndex` is on, following the `projectMemory` pattern (Config.cs:211/318/459, Runtime.cs:383). Local tools are always offered directly and never on demand, so their place in the request stays stable. The three definitions add about 1,800 characters (about 500 tokens) to every request, and the benchmark counts that cost.

### `code_search`
```json
{"type":"object","properties":{
  "query":{"type":"string","description":"What to find: what the code does (where big tool results are cut), a name (ToolDef, KeptResults.Keep, run_tools), or both."},
  "path":{"type":"string","description":"Only under this folder or file, or matching this glob (src/**/*.cs)."},
  "tests":{"type":"boolean","description":"Rank tests like other code (default: below it, unless the query says test)."},
  "budget":{"type":"integer","description":"Tokens the answer may take (default 2000, 300-4000)."}},
 "required":["query"]}
```
**Description:**
> Find code by what it does or by its name, from this folder's code index: the places that matter most, each cited as path:start-end with its symbol and its lines (numbered as read_file numbers them), up to a budget. A name gives its definitions and every use in code, grouped by the function each is in. Ranked, not every match, unless it says complete: for every occurrence of a pattern, a regex, or text in comments and strings, use grep.

**Example: a "words" answer** (real lines at `be74af35`; `…` marks lines left out here, which the real answer shows whole):
```
code_search "where are big tool results cut or kept aside" · words · 4 places · ≈1,240 of 2,000 tokens · fresh (2,455 files)
[1] src/CodeArena/Agent.cs:1052-1066 · method Agent.RunToolsAsync (lines 974-1077) · why: result, cut, kept, limit · calls [3] [4]
  1052	        var limit = MaxToolChars;
  1053	        for (var i = 0; i < planned.Count; i++)
  …
  1057	            if (text.Length > KeepWhole && KeptResults.Keep(Context, text) is { } kept)
  …
  1061	            else if (text.Length > limit)
  …
[2] src/CodeArena/Agent.cs:1079-1083 · properties Agent.MaxToolChars, Agent.KeepWhole · why: result, kept · used by [1]
  1080	    private int MaxToolChars => Math.Clamp(Model.Context * 3 / 2, 4_000, 40_000);
  …
  1083	    private int KeepWhole => Math.Min(MaxToolChars, 16_000);
[3] src/CodeArena/KeptResults.cs:22-44 · method KeptResults.Keep · why: kept, result · called by [1], Agent.ClearEarlier
  …
[4] src/CodeArena/KeptResults.cs:57-70 · method KeptResults.Preview · why: result, shown · called by [1]
  …
More, one line each:
- src/CodeArena/Agent.cs:1092 · method Agent.ClearEarlier · cleared, results
- src/CodeArena/KeptResults.cs:73 · method KeptResults.Cleared · cleared
Ranked, not every match: 6 of 19 symbols holding these words. Every line: grep "kept|cut" path=src/CodeArena
```

**Example: the identifier route** (counts from `git grep -w` at `be74af35`):
```
code_search "ToolDef" · name · 1 definition · uses in 22 files · complete for code (comments and strings not counted) · ≈640 of 2,000 tokens
[1] src/CodeArena/Tools.cs:34-68 · class ToolDef · "A tool the model can call: its name, description and JSON schema, and what runs."
  members: Name, Description, Parameters, Run, Kind, Server, RemoteName, Trusted, ChangesNothing, Summary, AlwaysAsks, Group, Spec() :60, ReadOnly :67
Uses, by the symbol they are in:
- src/CodeArena/Tools.cs (19): ToolBox.Offered, ToolBox.ForSubAgent, ToolBox.InPlace, …
- src/CodeArena/Runtime.cs (5): Runtime.Served :1053, Runtime.ReviewAsync :1413, Runtime.Remote :1553
- src/CodeArena/Rules.cs (4): PermissionRules.Denied :62, .Allowed :66, .Matches :69, .Subject :88
- src/CodeArena/Memory.cs (4): MemoryStore.RememberTool :265, .RecallTool :291, .HistoryTools :321, SessionSearch.Tool :431
- … 18 more files (5 tests): LocalTools.cs (3), Agent.cs (3), CodeMode.cs (2), Reviews.cs (2), …
Notes tied to this code: none.
Every occurrence, comments too: grep -w ToolDef
```

**Output rules**
- **Header grammar** (stable, so code-mode programs can parse it): `[n] path:a-b · kind Qualified · why: … · edges`.
- Excerpts use read_file's `%6d\t` format. Lines over 300 characters end with "…".
- A symbol of 40 lines or fewer is shown whole. A longer one shows its signature, then ±3 lines around each match, with `…` between parts.
- Whole lines only.
- The answer ends with a coverage line: complete or ranked, how many were shown out of how many, and the grep that finds every occurrence.
- If anything was hidden, it says how many: by rules, as secrets, or as already shown.

### `code_symbol`
```json
{"type":"object","properties":{
  "name":{"type":"string","description":"A symbol: Name, Type.Member, or path#Type.Member as code_search cites it."},
  "show":{"type":"string","enum":["source","callers","callees","impact"],"description":"source (the default: its code, its members and who calls it), callers, callees, or impact (what reaches it, steps out, and the tests among them)."},
  "depth":{"type":"integer","description":"impact: steps out (1-3, default 2)."},
  "budget":{"type":"integer","description":"Tokens the answer may take (default 1500, 300-4000)."}},
 "required":["name"]}
```

**Example: impact** (real call sites):
```
code_symbol KeptResults.Keep · impact, 2 steps · 4 symbols in 1 file · ≈380 of 1,500 tokens
Step 1
- src/CodeArena/Agent.cs:974-1077 · method Agent.RunToolsAsync · calls it at :1057 (scope)
- src/CodeArena/Agent.cs:1092 · method Agent.ClearEarlier · calls it at :1103 (scope)
Step 2
- src/CodeArena/Agent.cs:546 · method Agent.TurnAsync · calls RunToolsAsync at :720 (scope)
- src/CodeArena/Agent.cs:1168 · method Agent.MaybeCompactAsync · calls ClearEarlier at :1178 (scope)
Tests within 2 steps: none (tests reach it through the agent loop, further out).
Unresolved: no other call named Keep is left unresolved in these files.
Every line naming it: grep -w "KeptResults.Keep"
```
An ambiguous name returns its candidates as `path#Qualified`, so the next call can name one exactly. That is the counterpart of Graphify's exit code 3.

### `code_outline`
```json
{"type":"object","properties":{
  "path":{"type":"string","description":"A file or folder (default: the working directory)."},
  "budget":{"type":"integer","description":"Tokens (default 1200, 300-4000)."}}}
```
- **For a file:** its symbols, nested, with line ranges and one-line signatures. For example, `src/CodeArena/KeptResults.cs (115 lines)` gives: `11 class KeptResults` / `23-44 string? Keep(ToolContext c, string text)` / `47-55 string? Read(ToolContext c, string id)` / `58-70 string Preview(string text, string id)` / …
- **For a folder:** its subfolders with file and symbol counts, then the files with the most references into them, each with its main types and functions (an aider-style map), fitted to the budget.

### How the tools reach the model
- **Built-in.** The "How to work" bullet in Prompt.cs:69 changes, only when the index is on, to:

  > find code with code_search (where something is done, a name's definition and uses, what a change reaches): it cites path:lines; grep when you need every occurrence, a regex, or text in comments and strings; read_file for more of a cited place.

  The prompt stays stable either way.
- **New `ToolContext` members:**
  - `CodeIndex? Code`;
  - `Func<double>? CharsPerToken` and `Func<int>? ResultChars` (KeepWhole), which cap the budget;
  - `HashSet<string>? Shown`, per agent context, cleared by ClearEarlier and compaction;
  - `Func<string, bool>? MayRead`, the deny rules;
  - `IReadOnlySet<string>? GivenMemories`, the Agent's `_given`, Agent.cs:374.
- **Sub-agents** get their own `Shown` set.
- **Code mode.** A program gets results whole and prints only what it needs. Loops such as "callers of these six symbols" are where that pays off.
- **The IDE.** It runs the same Runtime, so the tools are already there. Tool cards get chips for the top 3 citations (answer.tsx `placeOf` learns to read `[n] path:a-b`).
- **Plans.** `GroundAsync` (Runtime.cs:1443-1497) skips `local/` projects today. It would also ground plan steps from the local index's callers and tests, while Argus keeps adding dependents in other repositories.

### CLI and IDE
- **`code-arena index`:** build or refresh this folder's index, then print its status (files, symbols, edges, languages, skipped files with reasons, build time, memory used). With `--rebuild`, rebuild from scratch.
- **`code-arena search "query" [--symbol NAME] [--budget N] [--json]`:** the same answers with no model involved, for people, debugging and the benchmark's retrieval arm. `--json` gives `{route, items:[{path,start,end,kind,name,why}], complete, shown, total}`.
- **REPL `/index`:** `status`, `rebuild`, or `off` for this session.
- **IDE:**
  - "Go to Symbol in Workspace…" (Ctrl+T) through `GET /api/symbols?q=`, in the existing quick pick;
  - an index state item in the status bar (language-status.tsx);
  - code notes in the Memory panel (section 5).

---

## 5. Memory tied to code

**Principle: no new store.** A code note is an ordinary memory that carries **anchors**. The index keeps only derived links (memory id to symbol) and a verdict for each. Both are recomputed and never hold the memory's text, so forgetting a memory anywhere removes it everywhere. That also means Graphicle's deletion receipts are not needed.

### The anchor
- Format: `path#Qualified[~n]@h8`, for example `src/CodeArena/Agent.cs#Agent.MaxToolChars@5c1e09a2`.
- `h8` is the symbol's body hash from section 3: code changes count; comments and whitespace do not.

### Verdicts, computed locally
- **current:** the same path and qualified name, with the same hash.
- **changed:** the same path and name, with a different hash.
- **moved:** the qualified name or the hash is found in another file. The person is offered "re-anchor".
- **gone:** nothing matches.

### Where anchors live (the three memories that already exist)
1. **Arena memory**, when signed in (`Llm.Core/Chat/Memories.cs`):
   - A new column `Anchors text[] NOT NULL`, with `defaultValueSql '{}'` and an upgrade test, following the rule for migrations with data.
   - `/api/code-arena/memories` accepts `anchors` and `proposed`, and returns `anchors` in `View` (CodeArenaMemory.cs:120) and in recall.
   - A new `POST /api/code-arena/memories/{id}/keep` approves a proposal through the existing flow (Memories.cs:299-303).
   - `Memory.Proposed` and `Replaces` already exist (Memories.cs:61-65).
2. **Offline memory files** (Memory.cs:181-191):
   - A line carries `  (code: anchor; anchor)` at its end.
   - Proposals wait in `<name>-<sha10>.proposed.md`.
   - Import sends the anchors and the proposed state.
3. **`.remember/` lines** (ProjectMemory.cs): the same `(code: …)` suffix is recognised, linked and checked. These notes are the team's and are committed, so they are approved through git review. The agent does not write anchors there unless asked.

### Proposed by the agent, approved by the person
- `remember` gains `"code":{"type":"array","items":{"type":"string"},"description":"The code it is about, as code_search cites it (path#Type.Member, or a name): tied to it, the memory waits for the person to keep it, and is marked when that code changes."}`.
- Each entry must resolve through the index to exactly one symbol. Otherwise the call is refused with the candidates.
- The memory is sent with `proposed: true`. The result reads: "Proposed for this project (waiting for you: /memory or Memory in the IDE), tied to Agent.MaxToolChars."
- Proposals are never served to the model before they are kept.
- The person keeps or drops them in three places: `/memory` in the terminal (Repl.cs:808), the IDE Memory panel (memory-panel.tsx, which gains a "Waiting" list with Keep and Drop), or the web Memory page.

### How notes are served, without duplicates
- **Per-message recall stays Arena's.** Code Arena appends one line naming any recalled memories whose anchors are `changed` or `gone`: "check before relying on them".
- **`code_search` and `code_symbol` attach linked notes** for the symbols they show: at most 3 per symbol and 6 per answer, one line each, with their verdict. Any memory id already in `_given` (Agent.cs:857-860) is skipped, and attached ids are added to it.
- **The link map** is built at session start from `MemoryStore.Listed`, which holds every memory the session may read (Memory.cs:41-43), plus the `.remember` lines. It is refreshed when `/memory` changes something.

### Stale handling
- A changed note is shown, labelled "code changed since this was noted", not hidden: it may still be true.
- The IDE panel and `/memory` show a badge with three actions:
  - **Still true:** re-anchor with the current hash;
  - **Edit**;
  - **Forget**.
- The agent may propose an updated memory. A proposal that contradicts an existing memory is linked through the existing `Replaces` field.

---

## 6. Measurement

### Harness
- `evals/run_code_index.py`, following `evals/run_tool_modes.py`.
- Results go to `evals/code-index-results.json` and `docs/measurements/code-index.md`, with a row in the evals/README.md table.
- It drives the real program: `code-arena -p --output json` (Cli.cs:934-990), with each run in its own `CODE_ARENA_HOME`.
  - The person's key is copied into a 0600 config in that home, removed at the end, and never printed.
  - Tool calls are read from the session's `.jsonl` file, using the id the JSON answer returns. Commit 1 also adds them to the JSON answer.
- **Arms:**
  - **grep:** Code Arena as it is today, with `codeIndex` false. It keeps grep, glob, read_file, git and run_shell, so the baseline is the strongest one we have, not a weakened one.
  - **index:** the same agent with `codeIndex` true.
  - **index-1:** `code_search` only, which tests "one broad tool".
- **Models:** two of the person's own, as tool-modes used (`Qwen3-4B-Q4_K_M`, `Qwen3.8-27B-UD-Q4_K_XL`). Each task runs 3 times per arm and model, the same sandbox in each arm.
- **Corpora:**
  - **llm-service:** a local clone into `~/measure-sets/code-index/`, so the person's working tree is never touched. It has 1,392 commits.
  - **owasp-benchmark (Java, already on disk):** used once the Java scanner exists.
  - With the person's yes: `pallets/flask` (Python, BSD-3; GitHub reports 12,216 KB) and `colinhacks/zod` (TypeScript, MIT; 27,266 KB), both pinned by commit.
  - Optionally, the person's own `~/PAM` and `~/security-center`.
- **Ground truth comes from the repositories, never from whoever writes the task** (the evals/README.md rule):
  - **locate** (60 tasks on llm-service, 30 per other repo):
    - Real commits touching 1–4 non-test source files. The median commit here touches 9, so these are filtered.
    - The corpus is checked out at the parent commit. The prompt is the commit subject plus "name the files and functions you would change; change nothing".
    - Graded on recall of the changed files, plus overlap between cited `path:a-b` ranges and the diff's hunks. The second grade needs no scanner.
  - **callers** (20 C#, 20 TS):
    - In a throwaway copy, rename a method's declaration (all overloads in that type), then build: `tools/dn build src/CodeArena`, or `tsc --noEmit -p src/web` (`node_modules/typescript` is present).
    - The compiler's errors are the exact call sites. Graded on recall and precision by line.
    - This is the family that checks grep's completeness is not lost.
  - **impact** (10): add a required parameter instead of renaming; the compiler's errors are the direct impact.
  - **string** (20): string literals that appear only once in code; "where is this message raised". The answer is that line, which grep finds trivially; the index must not do worse.
  - **fix** (10): commits that change src/CodeArena together with tests/CodeArena.Tests. Revert the source part only, ask for the test to pass, and grade by running that test class in the sandbox.
- **Measured per run:**
  - success;
  - prompt tokens, uncached and cached separately, and completion tokens;
  - requests (model rounds);
  - tool calls by name, and characters of tool results;
  - wall-clock time;
  - every `path:a-b` cited by a tool, checked against the file at that moment.
- **Measured for the index:** cold build time, warm sync time, query p50/p95, peak resident memory and file size.
- **Retrieval only** (no model, deterministic, cheap): file hit@1/5/10 of `code-arena search` on the locate tasks, against BM25 alone (graph off) and against a `git grep` heuristic (files ranked by query-word hits). This catches ranking regressions but does not decide the default.
- **Splits:** tasks are split 50/50 into development and test by the parity of the commit hash. Weights are tuned only on development.
- **Statistics:** paired by task over the mean of 3 runs. Wilcoxon signed-rank for tokens and calls; a paired bootstrap 95% interval for the difference in success.

### The gate for on-by-default
Every condition must hold for each model, on the test split.
1. **Success is no worse.** The lower 95% bound of (index − grep) is at least −2 points overall, and no family is more than 5 points worse. callers and impact are named explicitly, because completeness is grep's strength.
2. **Tokens.** Median total tokens per task (prompt plus completion, cached counted) are at least 20% lower, with Wilcoxon p < 0.05, on llm-service and on at least one other repository. No repository may be 5% or more higher.
3. **Tool calls and model rounds.** Medians no higher than grep's.
4. **Adoption.** `code_*` tools are called in at least 60% of index-arm tasks. Otherwise their definitions are pure overhead, which is what Graphicle measured.
5. **Time.** Median wall-clock no more than 10% slower.
6. **Cost on llm-service:**

   | Measure | Limit |
   |---|---|
   | cold build | ≤ 15 s |
   | warm sync for ≤ 20 changed files | ≤ 300 ms |
   | query p95 | ≤ 200 ms |
   | extra resident memory | ≤ 150 MB |
   | index file | ≤ 50 MB |

7. **Correctness.** Zero cited ranges that do not match the disk.

If it passes, `codeIndex` becomes true by default in its own commit, with the numbers in the commit message, the CHANGELOG and docs, and the person sees them first. If it fails, it stays opt-in, the negative results are published, and the failed condition decides what comes next.

**Memory** is gated on accuracy, not on tokens. Stale flags are checked on llm-service's history: anchors set at commit A are compared with each later diff's non-whitespace, non-comment hunks that overlap the symbol. The target is recall ≥ 0.95 and precision ≥ 0.80; Graphicle reported 0.90–1.00 and 0.68–0.86.

---

## 7. Commits, in order

Each commit is small, tested and shippable on its own, and pushed before the next.

**Where tests live:**
- `tests/CodeArena.Tests/`: `LocalToolTests` style over temporary folders, and `Harness` + `FakeGateway` for end-to-end runs (Fakes.cs:545+).
- `tests/Llm.Tests/`: Arena.
- `src/web/src/code-arena/*.test.ts(x)`: the IDE.
- `evals/`: run deliberately, not unit tests.

1. **The JSON answer counts tool calls.** `-p --output json` adds `tool_calls: {name: n}` and `tool_chars`.
   - *CliTests:* a scripted turn with two grep calls reports `{"grep":2}`.
2. **Benchmark skeleton and the grep-only baseline.** `evals/run_code_index.py` builds the tasks from git and the compiler, and runs the grep arm. `docs/measurements/code-index.md` gets the baseline.
   - *Check:* `--check` regenerates the tasks with the same seed and they come out identical. Answer keys are built before any model runs.
   - *Needs a yes:* none for llm-service; flask and zod clones (12.2 MB / 27.3 MB as GitHub reports) and the person's own repositories each need one.
3. **Link Argus's `Util/CodeText.cs`** into CodeArena.csproj, and add it to the Dockerfile's COPY (Dockerfile:68-69 copies the linked files).
   - *CodeIndexScanTests:* blanked C# raw strings and TS templates keep their line count, and braces inside them are not counted. The blanker's own tests stay in Argus.Tests.
4. **The file set** (`CodeFiles.cs`): `FilesUnder` plus the sensitive filter and the size, binary, generated, test and vendored tags.
   - *Tests:*
     - `.env`, `id_rsa`, `secrets/x.yaml` and a file holding a PEM private key are not content-indexed; `.env.example` is;
     - a git-ignored file is absent and an untracked one present, exactly as for grep;
     - `*.Designer.cs` is tagged generated.
5. **The C# scanner.**
   - *CodeScanCSharpTests:* exact symbols and ranges for partial classes, records with primary constructors, expression bodies, local functions, generics with `where`, attributes, raw strings containing braces, `#if` branches, file-scoped namespaces and top-level statements.
   - *A repository self-check:* scanning src/CodeArena, every reported start line contains its name and every range's brackets balance.
   - *A ctags comparison:* definition recall ≥ 98% on src/CodeArena, skipped where ctags is missing.
6. **The TS / JS / TSX scanner.**
   - *Tests:* arrow-function consts, classes and members, interfaces, types, enums, `export default`, re-exports, `require` and dynamic `import()`. A JSX apostrophe triggers the bracket check and the re-scan.
   - *A ctags comparison* on src/web/src/code-arena.
7. **The Python scanner.**
   - *Tests:* decorators, `async def`, nested definitions, class bases, `from . import x`, `__main__`. A ctags comparison on evals/*.py.
8. **The store** (`CodeIndex.cs`): in memory plus `index.bin`, atomic writes, loading, sync by size, time and hash, and scanner versions.
   - *Tests:*
     - save then load gives the same index;
     - one changed file means exactly one re-parse (a counter);
     - a corrupt file or a format bump rebuilds with a note;
     - the folder is 0700 and the file 0600;
     - two runtimes writing at once leave a loadable file;
     - size and time are measured on llm-service and written to the docs.
9. **Resolution** (`CodeGraph.cs`): imports and calls with evidence levels, plus the unresolved table.
   - *CodeResolveTests:* fixtures of 3–5 files per language, covering:
     - same class → `scope`;
     - `using A.B;` plus `new C()` → `A.B.C`;
     - a typed field receiver → `type`;
     - two `Run` definitions → `guess` with both;
     - TS `@/lib/api` through tsconfig `paths` with `extends`;
     - Python relative imports.
10. **Search and packing** (`CodeSearch.cs`), as a library.
    - *CodeSearchTests:*
      - ordering is deterministic;
      - the identifier route equals `git grep -w` over code lines of a fixture (comments and strings excluded, and said so);
      - the budget holds (characters ≤ budget × CharsPerToken);
      - a file edited after the build is re-parsed before it is cited;
      - every excerpt line equals the disk line;
      - a name answer is never larger than `grep -n -w`;
      - denied paths and secret files never appear.
11. **`code-arena search`, `code-arena index` and `/index`,** plus the retrieval-only benchmark arm.
    - *CliTests:* search over a temporary repository prints `[1] path:a-b · kind Name`; `/index` lists files skipped as secrets.
    - *Evals:* hit@k numbers in the docs.
12. **The three tools behind `codeIndex`** (default false; `CodeTools.cs`), the new ToolContext members, and the prompt bullet.
    - *Unit tests:* each tool's output shape.
    - *End to end:* `Reply.Call(("code_search", …))` gives a result with `path:a-b`; `FakeGateway.ToolNames` holds the tools only when the flag is on; `SystemOnly` holds the bullet only when on; a sub-agent can call it (AgentTests.cs:287-315 pattern); plan mode allows it; skip-shown resets after ClearEarlier (CompactionTests pattern).
    - *CodeModeTests (PythonFact):* `arena.local.code_search` is listed and callable.
13. **Freshness:** the IDE's DiskWatch cursor, the terminal's own DiskWatch or sync fallback, and the BeforeWrite and run_shell marks.
    - *CodeIndexFreshTests* (DiskWatchTests style):
      - a file written during a session is found by the next search without a full rescan;
      - an overflow triggers a full sync;
      - a `git checkout` triggers a sync;
      - 500 changed files give a "re-indexing" label and still complete name answers.
14. **Full benchmark runs** with the grep, index and index-1 arms on the corpora approved in commit 2. *Evals:* numbers and the gate's verdict in `docs/measurements/code-index.md`.
15. **The gate's outcome.** If it passed: `codeIndex` default true, plus CHANGELOG and docs/code-arena.md. If it failed: the negative results are documented and the tools stay opt-in.
    - *Tests:* the Config default is asserted; the Harness keeps the flag off unless a test brings it, as it does for `projectMemory` (Fakes.cs:560-562).
    - *Needs:* the person sees the numbers first.
16. **IDE: Go to Symbol in Workspace,** the status bar item, and citation chips on code_* tool cards.
    - *WebTests:* `/api/symbols?q=` ranks prefix and camel-hump matches.
    - *vitest:* quick-pick ordering, and `placeOf` reading `[n] path:a-b`.
17. **Local plan grounding** in `GroundAsync` for `local/` projects (callers and tests), with Argus kept for other repositories. *PlanProveTests / PlanReviewTests:* a local plan step with symbols gets an `Impact` from the index.
18. **Arena: `Memory.Anchors`.** The migration with `defaultValueSql '{}'`, an upgrade test showing existing rows survive, anchors and proposed on POST, anchors returned by GET and recall, and `POST /{id}/keep`.
    - *Tests in tests/Llm.Tests:* round trip, a proposal not served before it is kept, the person's own only.
    - *Must land after* the gateway work now uncommitted in `AppDbContext.cs` has been committed, so the two do not mix.
19. **Code Arena: `remember` with `code`.** Validation against the index, proposals, offline `(code: …)` lines and the `.proposed.md` file, keep and drop in `/memory`, and the IDE panel's Waiting list.
    - *ArenaMemoryTests:* the body carries anchors and `proposed: true`; an unknown or ambiguous symbol is refused with its candidates; offline round trip; import carries anchors.
    - *vitest:* the panel's Keep calls the endpoint.
20. **Links and verdicts:** notes attached to code_* answers, the recall annotation, and `.remember` lines recognised.
    - *CodeNotesTests:*
      - an edited body gives `changed`;
      - an edit to whitespace or comments stays `current`;
      - a moved file gives `moved`;
      - a deleted symbol gives `gone`;
      - a memory already in `_given` is not sent again;
      - at most 3 notes per symbol.
21. **Stale-flag measurement and "Still true"** in the IDE and `/memory`. *Evals:* precision and recall on llm-service's history, in the docs. *vitest:* the badge and re-anchoring.
22. **Go, Java/Kotlin and Rust scanners.** Tests as in 5–7, plus a ctags comparison. The owasp-benchmark corpus is already on disk, so this needs no download.
23. **(Only if measured necessary; needs the person's yes) tree-sitter for any language under 97% definition recall, or where the benchmark traces misses to parsing.**
    - *Download:* TreeSitter.DotNet **1.3.0**, nupkg **53,401,399 bytes**, from nuget.org.
    - *Shipped:* only the needed grammars, ≈ 9.2 MB per RID for the first wave (≈ 11.0 MB for all eight).
    - *Also changes:* `tools/fetch-code-arena-packs.sh` and `tools/offline-nuget/README.md`.
    - *Tests:* the `ICodeScanner` contract tests pass for both implementations; the published linux-x64 file starts in CI with the native libraries extracted.

**What needs the person's yes:**
- Commit 23 (the download).
- Commit 2/14's extra corpora: two clones, or the person's own repositories.
- Commit 15's flip to on by default, after they see the numbers.

Nothing else downloads. Commits 5–7 and 22 run the ctags comparisons in CI only once the code-arena job installs `universal-ctags`, as the argus job already does (ci.yml:34). That installs a package on GitHub's runner, not on the person's machine.