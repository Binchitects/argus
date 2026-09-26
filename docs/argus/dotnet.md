# Argus in .NET

`dotnet/` is a second, complete implementation of Argus in C# on .NET 10: the
indexer, the MCP server with all 17 tools, the operator and webhook HTTP
surface, the Prometheus exposition, knowledge-pack building and serving, and
every CLI command. It is a **drop-in** for the Python package, not a rewrite of
the idea:

- the same CLI (`argus index | serve | status | kpi | verify | pack … | …`),
  arguments and exit codes;
- the same `tools/list`, byte for byte, because the catalogue is generated from
  the Python server (`dotnet/scripts/gen-tool-catalog.py`);
- the same SQLite schema, because the Python migrations are embedded in the
  binary rather than copied; an index built by one serves from the other;
- the same pack format, so either builder's `.arguspack` serves from either;
- the same image contract: UID/GID 10001, `/var/lib/argus`, port 7700,
  `/etc/argus/config.yaml`.

Switching a deployment is a rebuild, not a migration, and switching back is the
same rebuild.

## Why run it

| | Python | .NET |
|---|---|---|
| First index pass, conformance corpus (5 C projects + 2 repos, 1,129 files, 34,679 symbols) | 21.1 s | **14.1 s** |
| Incremental pass after a commit to three repos | 1.3 s | **0.9 s** |
| First pass in the browser run below, embeddings included (8 refs) | — | 15.7 s |
| `find_symbol` over MCP/HTTP, official SDK client, p50 / p95 | — | 9.2 ms / 10.8 ms |
| Runtime in the image | CPython + wheels | the ASP.NET runtime; the app is 7.5 MB |
| Windows | via WSL/containers | native (`win-x64` sqlite-vec, git askpass mode built in) |

The tool answers are identical, so the choice is about operations, not
behaviour.

## Layout

| .NET (`dotnet/src/Argus/`) | Python (`src/argus/`) |
|---|---|
| `Configuration/ArgusConfig.cs` | `config.py` |
| `Store/` — `Db`, `Writes`, `Queries`, `Explore`, `PgVector` | `store/` |
| `Indexing/` — `GitLab`, `Mirror`, `Ctags`, `Parsers`, `Worker`, `Resolve`, `WhichRepo`, `Semantic`, `Embed`, `Kpi` | `gitlab.py`, `mirror.py`, `parse/`, `worker.py`, `resolve.py`, … |
| `Access/` — `Acl`, `AuditLog` | `acl.py`, `auditlog.py` |
| `Packs/` — format, store, registry, chunker, builder, `Sources/` (16 adapters) | `packs/`, `store/packs.py` |
| `Server/` — MCP (ModelContextProtocol.AspNetCore), admin, webhook, metrics, jobs | `mcpsrv/` |
| `Cli/`, `Program.cs` | `cli.py` |
| `Util/` — Python-compatible string, JSON and float formatting | — |

`Util/` is why the outputs match: `str.splitlines`, `repr`, `json.dumps`
separators, pydantic's float formatting and stable sorts are reproduced
exactly, because a model reads these strings and a changed byte is a changed
prompt.

## Build, test, run

```bash
cd dotnet
scripts/fetch-sqlite-vec.sh linux-x64        # pinned sqlite-vec 0.1.9, SHA-256 checked
dotnet build -c Release
dotnet test -c Release                        # 120 xUnit tests
src/Argus/bin/Release/net10.0/argus serve --config /path/to/config.yaml
```

Needs git and Universal Ctags on `PATH`, as the Python package does.

After changing a tool's signature or docstring in Python:

```bash
python dotnet/scripts/gen-tool-catalog.py            # regenerate ToolCatalog.g.cs
python dotnet/scripts/gen-tool-catalog.py --check    # CI: fail if it drifted
```

## Deploying it in the stack

Two variables in `stack/.env`:

```bash
ARGUS_DOCKERFILE=dotnet/Dockerfile
ARGUS_IMAGE=argus-dotnet
```

then `docker compose build argus && docker compose up -d argus`. The data
volume, config, tokens and the console's settings are unchanged. Remove both
lines and rebuild to go back.

`dotnet/Dockerfile` builds from the **repository root** (it embeds the Python
migrations and reuses the Python suite's fixtures). Its test stage runs the
whole xUnit suite against the image's own ctags and git, and the runtime image
carries the receipt at `/usr/share/argus/build-verified`, so an image exists
only if the suite passed in it. The healthcheck is `argus healthcheck`; the
compose healthcheck works with either image.

## How it is proven equal

**Conformance** (`dotnet/tests/conformance/run.py`) runs both implementations
on the same inputs and compares everything observable: a fake GitLab serving
five real C projects and hand-made edge cases (a release branch, a private
repo, a 1 MB file, renames and deletions), a deterministic fake Ollama, and
four users at different access levels.

| compared | result |
|---|---|
| index tables after a full and an incremental pass — files, symbols with doc comments, includes and resolution, the repo graph, vendoring, retry state, embeddings | identical, row for row and bit for bit |
| every MCP tool, as an admin, a reporter, a guest and the chat client acting for a user; each on its own index and .NET on Python's index | 45/45 × 4 identities × 2, identical |
| HTTP: health, auth refusals, DNS-rebinding, admin explore/packs/status, webhooks | 18/18 identical, plus metric samples |
| packs built from 14 sources | byte-identical contents; incremental rebuild identical |
| `pack list/info/index`, `verify`, and the six docs tools over the built packs | identical |

**End to end** (`dotnet/tests/e2e/run.py`, 84 checks) puts the .NET server
behind the real admin console and drives it with Chromium: start a pass from
*Indexing*, see the second start refused while it runs, watch it finish, search
it on *Explore*, install a pack with a wrong digest (refused, nothing left
behind), then with the right one, install another over HTTP, update from a
published index, remove one through the confirm dialog, then push to GitLab
and see the webhook pass land. Non-admins and requests without the proxy's
headers are refused. The MCP side uses the official MCP Python SDK over
streamable HTTP (bearer tokens, and the chat client's forwarded user) and over
stdio, and checks access limits hold on each path. The Prometheus exposition is
parsed with the Prometheus client's own parser.

```bash
python dotnet/tests/conformance/run.py --work /tmp/argus-conformance
python dotnet/tests/e2e/run.py --work /tmp/argus-e2e --console-python <python3.12+ with the console's pins>
```

## Differences

One, deliberate: the metrics exposition writes each family's `HELP`/`TYPE`
once and its samples together. The Python exposition interleaves families per
repository, which the text format does not allow once there are two
repositories. The samples are the same.

## What the port's testing found in both

Running the real console and a real pack against both implementations found
two defects the Python suite could not see, because its tests stubbed the
function in between. Both are fixed in both implementations, with tests that
fail without the fix:

- **`argus verify` could never block.** It filtered `verify_text`'s findings
  on a top-level `status` they never carry (the verdicts are nested under each
  API's `corrections`), so every draft exited 0 and the Stop hook built on it
  let contradicted answers through. It now exits 2 and says, for example,
  *MessageBoxW dll: you said 'shell32.dll'; the documentation says
  'User32.dll'*.
- **`docs_verify` quoted prose as the contract.** The adapters append each
  API's description after a ` -- ` marker, and the last field absorbed it:
  the documented DLL read `User32.dll -- Displays a modal dialog box.` — the
  string a model is told to copy verbatim. Contradicted fields now also carry
  `stated`, what the draft said.

## Python docstrings, fixed in both

Neither indexer used to read a Python docstring: ctags was not run with
`--fields=+l`, so no symbol carried a language and the docstring reader, which
is keyed on it, never ran. Both now ask for the language, and the symbol
contract version is 3, so an existing index re-extracts every file on its next
pass with no manual step.
