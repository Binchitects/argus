from __future__ import annotations

import json
import logging
import time
from collections.abc import Callable
from pathlib import Path
from typing import Any, TypeVar

from mcp.server.fastmcp import Context, FastMCP
from starlette.concurrency import run_in_threadpool

from .. import acl
from ..config import Config
from ..embed import EMBED_DIM, EMBED_MODEL, EmbeddingUnavailable, embed_batch
from ..packs import format as pack_format
from ..packs.registry import PACK_SUFFIX
from ..store import packs as packs_store, queries, writes
from ..store.db import connect, connect_readonly

T = TypeVar("T")

log = logging.getLogger(__name__)


class DocsUnavailable(Exception):
    """Documentation packs could not be queried, for reasons the model cannot fix.

    Same principle as `IndexUnavailable`: the text is prompt text. "No packs
    are installed" and "this pack was built with a different embedding model"
    are operator problems, and a model told only "error" will retry forever.
    Each message therefore names the specific pack and says what remains
    usable.
    """


class IndexUnavailable(Exception):
    """The read-only index could not be reached for reasons unrelated to the query.

    Error text is prompt text: a raw `sqlite3.OperationalError` (a missing,
    locked, or corrupt database file) is exactly the wrong string to hand a
    model -- it reads like a query problem the model should retry or rewrite,
    when the actual cause is operational and out of its hands.
    """


def _identity(ctx: Context) -> acl.Identity:
    """Read the caller's resolved Identity out of the request BearerAuthMiddleware attached.

    `BearerAuthMiddleware` (server.py) resolves the bearer token and sets
    `scope["state"]["identity"]` before FastMCP's own routing -- including tool
    dispatch -- ever runs. FastMCP threads that same `scope` through to
    `ServerMessageMetadata.request_context`, which becomes
    `ctx.request_context.request`: a Starlette `Request` view onto the exact
    scope the middleware populated. So `.state.identity` here is exactly the
    Identity the middleware attached, never one a tool constructs or defaults
    itself.

    Fails closed if that invariant is ever violated -- a missing request or a
    request with no identity attached both raise here, explicitly, rather
    than falling through to an internals-leaking
    `AttributeError: 'NoneType' object has no attribute 'state'`.
    """
    request = ctx.request_context.request
    identity = getattr(request, "state", None) and getattr(request.state, "identity", None)
    if identity is None:
        raise LookupError(
            "No authenticated identity is attached to this request; refusing "
            "to proceed."
        )
    return identity


async def run_readonly(db_path: Path | str, fn: Callable[[Any], T]) -> T:
    """Run `fn(conn)` against a read-only connection, off the event loop.

    Every tool handler below is `async def`, but `argus.store.queries` and
    `connect_readonly` are plain synchronous sqlite -- the identical hazard
    `BearerAuthMiddleware._resolve_identity` (server.py) documents for ACL
    resolution applies here too: running the connect/query/close sequence
    directly on the request coroutine would execute on uvicorn's single
    event-loop thread and stall every other in-flight request for the
    duration of one query.

    The whole open/use/close sequence runs inside a single
    `starlette.concurrency.run_in_threadpool` call, in one worker thread, so
    it never crosses threads -- required because `connect_readonly` opens
    its connection with `check_same_thread=False` only to make "which thread"
    the caller's choice, not to make cross-thread use safe on its own; this
    function's job is to keep the whole lifetime on exactly one thread.

    `connect_readonly`, never `open_db`: tool queries must not write to the
    index and must not run schema migrations. Migration happens exactly once,
    at server startup, in `create_app`.

    Anything `fn` raises that is a `queries.QueryError` or a `LookupError`
    is a deliberate, already-actionable signal (a bad query, a missing row)
    and propagates unchanged. Anything else -- most notably
    `sqlite3.OperationalError` from a missing, locked, or corrupt database --
    is not something the caller can fix by rewriting its query, so it is
    wrapped into `IndexUnavailable` with a clean, agent-directed message
    instead of a raw SQLite string.
    """
    def _call() -> T:
        try:
            conn = connect_readonly(db_path)
        except Exception as exc:
            raise IndexUnavailable(
                "The index is unavailable; do not retry this query."
            ) from exc
        try:
            return fn(conn)
        except (queries.QueryError, LookupError):
            raise
        except Exception as exc:
            raise IndexUnavailable(
                "The index is unavailable; do not retry this query."
            ) from exc
        finally:
            conn.close()

    return await run_in_threadpool(_call)


async def run_packs(packs_dir: Path | str, fn: Callable[[list], T]) -> T:
    """Open every installed pack, run `fn(packs)`, close them -- off the event loop.

    One `run_in_threadpool` call for the whole open/query/close sequence, for
    exactly the reason `run_readonly` documents: pack queries are synchronous
    sqlite, and running them on the request coroutine would stall every other
    in-flight request. Anything `fn` needs -- including embedding the query,
    which is a blocking HTTP call to Ollama -- happens inside this one hop, so
    a search never crosses threads and never touches the loop.

    Packs are opened per call rather than held open at startup. The
    alternative is a connection created on one thread and used on whichever
    threadpool worker happens to serve the request, which is precisely the
    cross-thread use `run_readonly` exists to avoid. Reopening costs a file
    open and an already-resident extension load.
    """
    def _call() -> T:
        directory = Path(packs_dir)
        paths = sorted(directory.glob(f"*{PACK_SUFFIX}")) if directory.is_dir() else []
        if not paths:
            raise DocsUnavailable(
                "No documentation packs are installed on this server, so there "
                "is nothing to look up. Do not retry -- this is a server "
                "configuration matter for the operator, not a query you can "
                "rephrase."
            )
        try:
            opened = packs_store.open_packs(paths)
        except Exception as exc:
            raise DocsUnavailable(
                "The installed documentation packs could not be opened; do not "
                "retry this query."
            ) from exc
        try:
            return fn(opened)
        except (DocsUnavailable, packs_store.PackQueryError):
            raise
        except Exception as exc:
            raise DocsUnavailable(
                "The documentation packs are unavailable; do not retry this "
                "query."
            ) from exc
        finally:
            packs_store.close_packs(opened)

    return await run_in_threadpool(_call)


async def _record_audit(db_path: Path | str, *, user_id: int | None, username: str | None,
                        tool: str, args: dict, repo_ids: list[int] | None) -> None:
    """Append one audit row for a tool-call attempt, on its own connection.

    Deliberately a *separate* `run_in_threadpool` hop from `run_readonly`'s,
    not a piggyback on it, for two reasons:

    1. This is a write, and it needs a write connection (`connect`, never
       `connect_readonly`) -- opening it inside `run_readonly`'s closure
       would mean that helper's contract is no longer "read-only query,
       wrapped for a clean error", which is exactly the property
       `test_run_readonly_wraps_unexpected_errors` and
       `test_run_readonly_lets_query_error_and_lookup_error_through` pin.
    2. This write must happen whether the query succeeded or raised (see
       `_with_audit` below); `run_readonly` already has its own, unrelated
       exception-wrapping logic (`IndexUnavailable`) that only applies to
       the query itself.

    Each hop still keeps its own connection's entire open/use/close sequence
    on the single worker thread the enclosing `run_in_threadpool` call
    grants it, preserving the one-thread-per-connection invariant
    `run_readonly` documents -- there are just two such hops per tool call
    instead of one. The extra hop is negligible next to the query and the
    GitLab round-trips `acl.resolve` already tolerates elsewhere in this
    server.

    Never raises: a failed audit write (disk full, database locked) must
    not turn a real, successful tool result into a failure for the
    developer. The failure is logged and swallowed here so a caller that
    awaits this in a `finally` block never has it replace or mask whatever
    exception (or result) the tool call itself produced.
    """
    def _write() -> None:
        conn = connect(db_path)
        try:
            writes.record_audit(
                conn, ts=int(time.time()), user_id=user_id, username=username,
                tool=tool, args_json=json.dumps(args),
                repo_ids_json=json.dumps(repo_ids) if repo_ids is not None else None,
            )
        finally:
            conn.close()

    try:
        await run_in_threadpool(_write)
    except Exception:
        log.warning("failed to record audit row for tool=%s", tool, exc_info=True)


async def _with_audit(db_path: Path | str, tool: str, identity: acl.Identity,
                      args: dict, call: Callable[[], Any]) -> Any:
    """Await `call()`, recording exactly one audit row for the attempt either way.

    An audit log exists to answer "what did the assistant show them", and
    that question is just as real for a call that raised (a denied
    `get_file`, a query error) as for one that returned data -- an attempted
    access is worth recording, not only a successful one. The `finally`
    below fires on both paths and does not affect which one the caller
    ultimately sees: `_record_audit` never raises, so a real exception from
    `call()` propagates unchanged, and a real result is returned unchanged.

    `args` must be built by the caller from ONLY the tool's own typed
    parameters (never `ctx`, never `identity`) -- `ctx` carries the request
    that the raw bearer token lives on, and passing it here (or anything
    derived from it beyond the already-resolved, token-free `Identity`)
    would risk that token reaching `args_json`.
    """
    try:
        return await call()
    finally:
        await _record_audit(
            db_path, user_id=identity.user_id, username=identity.username,
            tool=tool, args=args, repo_ids=identity.allowed_repo_ids,
        )



class UnknownBranch(queries.QueryError):
    """Asked for a branch that is not indexed, naming the ones that are.

    A QueryError because run_readonly propagates exactly those unchanged: it
    is a mistake the caller can fix by rewriting the call, unlike the
    IndexUnavailable case it would otherwise be flattened into. An agent told
    "the index is unavailable; do not retry" cannot discover that it simply
    asked for the wrong branch.
    """


def _scoped(conn, identity, branch: str | None):
    """The caller's allowlist, narrowed to one branch per project.

    Raising on an unknown branch rather than returning nothing is the point of
    the feature: a developer who asks about v2 and silently receives trunk
    answers has no way to notice, and acts on code that is not the code they
    are changing. An empty result would read as "no such symbol".
    """
    scoped = queries.scope_to_branch(identity.allowed_repo_ids, conn, branch)
    if branch is not None and not scoped and identity.allowed_repo_ids:
        available = queries._branches_available(identity.allowed_repo_ids, conn)
        raise UnknownBranch(
            f"branch {branch!r} is not indexed. Indexed branches: "
            + (", ".join(available) if available else "(none)"))
    return scoped


async def find_symbol_impl(db_path: Path | str, identity: acl.Identity, name: str,
                            kind: str | None = None,
                            branch: str | None = None) -> list[dict]:
    rows = await run_readonly(
        db_path,
        lambda conn: queries.find_symbol(_scoped(conn, identity, branch), conn,
                                         name, kind=kind),
    )
    return [dict(row) for row in rows]


async def find_references_impl(db_path: Path | str, identity: acl.Identity,
                               name: str,
                               branch: str | None = None) -> list[dict]:
    # queries.find_references already returns list[dict] (unlike the other
    # three, which return list[sqlite3.Row]) -- no conversion needed.
    #
    # queries.find_references returns "repo" (the path_with_namespace
    # *string*) and no repo_id at all, unlike find_symbol/search_code/
    # index_status, which all return repo_id alongside path_with_namespace.
    # get_file accepts only a numeric repo_id, so without this enrichment a
    # find_references hit cannot be chained into get_file -- the primary
    # "find a mention, read that file" workflow dead-ends. Stamp repo_id
    # onto each row using the path_with_namespace -> repo_id map that
    # queries.index_status(identity.allowed_repo_ids, conn) already returns,
    # built INSIDE this same run_readonly closure so the whole lookup stays
    # one run_in_threadpool call (see run_readonly's docstring).
    def _run(conn: Any) -> list[dict]:
        scoped = _scoped(conn, identity, branch)
        rows = queries.find_references(scoped, conn, name)
        if not rows:
            return rows
        # Built from the branch-SCOPED ids, not the whole allowlist. Once a
        # project can be indexed at several refs it owns several repo rows
        # with the same path_with_namespace, and a dict keyed on that name
        # keeps whichever row the query happened to return last. The result
        # was rows correctly scoped to the requested branch but stamped with
        # another branch's repo_id -- so chaining into get_file(repo_id, path)
        # read the wrong branch's copy of the file and said nothing. `scoped`
        # holds exactly one row per project, which makes the key unique again.
        repo_id_by_namespace = {
            status["path_with_namespace"]: status["repo_id"]
            for status in queries.index_status(scoped, conn)
        }
        for row in rows:
            row["repo_id"] = repo_id_by_namespace.get(row["repo"])
        return rows

    return await run_readonly(db_path, _run)


# queries.search_code's QueryError message ends with "... or use regex=True."
# but search_code_impl's (and the search_code tool's) signature takes only
# `query` -- there is no `regex` parameter here or on queries.search_code
# either, so that suggestion is stale at its source and cannot be honoured.
# A model's first recovery attempt after a syntax error would otherwise be a
# guaranteed invalid-argument call. queries.py is off-limits for this fix, so
# strip the dangling suggestion here instead.
_DANGLING_REGEX_SUGGESTION = ", or use regex=True."


async def search_code_impl(db_path: Path | str, identity: acl.Identity, query: str,
                           branch: str | None = None) -> list[dict]:
    # queries.QueryError's message is otherwise already actionable prompt
    # text (see queries.py) and FastMCP's tool dispatch turns any exception
    # raised here into an isError=True CallToolResult carrying str(exc) --
    # never a raw traceback or a bare SQLite message -- so only the dangling
    # regex=True suggestion needs to be caught and rewritten; everything else
    # about the message is left alone.
    try:
        rows = await run_readonly(
            db_path,
            lambda conn: queries.search_code(_scoped(conn, identity, branch),
                                             conn, query),
        )
    except UnknownBranch:
        # A QueryError subclass, so it would otherwise be caught below and
        # rebuilt as a plain QueryError -- losing the type before the caller
        # ever sees it. Nothing here needs rewriting; the message is already
        # about the branch, not the FTS query.
        raise
    except queries.QueryError as exc:
        message = str(exc).replace(_DANGLING_REGEX_SUGGESTION, ".")
        raise queries.QueryError(message) from exc
    return [dict(row) for row in rows]


async def get_file_impl(db_path: Path | str, identity: acl.Identity, repo_id: int,
                         path: str) -> dict[str, Any]:
    result = await run_readonly(
        db_path,
        lambda conn: queries.get_file(identity.allowed_repo_ids, conn, repo_id, path),
    )
    if result is None:
        # queries.get_file returns exactly None for both "no such repo_id in
        # your allowlist" and "no such path in that repo" -- deliberately not
        # distinguished (see queries.py), so this message must not imply
        # which one it was; doing so would let a caller use this tool as an
        # oracle for which repo ids exist.
        raise LookupError(
            f"No file at repo_id={repo_id}, path={path!r}. Either that repo "
            "id is not one you have access to, or that path does not exist "
            "in it -- call index_status to see which repo ids you can use."
        )
    return result


async def index_status_impl(db_path: Path | str, identity: acl.Identity) -> list[dict]:
    rows = await run_readonly(
        db_path,
        lambda conn: queries.index_status(identity.allowed_repo_ids, conn),
    )
    return [dict(row) for row in rows]


async def repo_map_impl(db_path: Path | str, identity: acl.Identity,
                        repo_id: int) -> dict[str, Any]:
    return await run_readonly(
        db_path,
        lambda conn: queries.repo_map(identity.allowed_repo_ids, conn, repo_id),
    )


async def which_repo_impl(db_path: Path | str, identity: acl.Identity,
                          description: str,
                          branch: str | None = None) -> list[dict]:
    # No `limit` parameter: the registered `which_repo` tool below never
    # exposes one to a caller, so this always ran with queries.which_repo's
    # own default anyway. Dropping it here removes a parameter nothing could
    # ever set to something other than that default, rather than plumbing an
    # unused knob through the MCP tool signature for a value this server has
    # never needed callers to tune.
    return await run_readonly(
        db_path,
        lambda conn: queries.which_repo(_scoped(conn, identity, branch), conn,
                                        description),
    )


async def docs_lookup_impl(packs_dir: Path | str, name: str,
                           lang: str | None = None, limit: int = 20) -> list[dict]:
    return await run_packs(
        packs_dir,
        lambda opened: packs_store.lookup_symbol(opened, name, lang=lang, limit=limit),
    )


async def docs_get_impl(packs_dir: Path | str, doc_path: str,
                        source: str | None = None,
                        max_chars: int = 60_000) -> dict | None:
    """Read a whole documentation page, having found it with lookup or search.

    The measured gap this fills: retrieval identified robocopy's reference
    page correctly and returned two sections of it, neither containing the
    flag being asked about. Chunks answer "where is this discussed"; a
    reference page's specific row needs the page.
    """
    return await run_packs(
        packs_dir,
        lambda opened: packs_store.get_doc(opened, doc_path, source=source,
                                           max_chars=max_chars),
    )


async def docs_search_impl(packs_dir: Path | str, query: str,
                           lang: str | None = None, limit: int = 10) -> list[dict]:
    """Embed the query and search, all inside one threadpool hop.

    Two degradations, both explicit rather than silent:

    * If the embedder is unreachable the search falls back to lexical matching
      over the same packs and every row is labelled ``retrieval: "lexical"``.
      Returning nothing would be worse, and returning lexical hits while
      calling them semantic would be worse still.
    * A pack built with a different embedding model is refused, naming the
      pack. It is not silently skipped -- that would return fewer results with
      nothing to say a whole source went missing -- and it is not answered
      lexically either, because a mismatched pack means a misconfigured
      deployment the operator needs to see.
    """
    def _run(opened: list) -> list[dict]:
        selected = packs_store.select_packs(opened, lang)
        try:
            vectors = embed_batch([query])
        except EmbeddingUnavailable as exc:
            log.warning("embedder unavailable, falling back to lexical: %s", exc)
            rows = packs_store.search_text(opened, query, lang=lang, limit=limit)
            for row in rows:
                row["retrieval"] = "lexical"
                row["note"] = (
                    "Semantic search was unavailable, so these are keyword "
                    "matches. Treat them as less precise than usual."
                )
            return rows

        mismatched = []
        for pack in selected:
            try:
                pack_format.require_compatible(
                    pack.meta, model=EMBED_MODEL, dim=EMBED_DIM
                )
            except pack_format.PackMismatch:
                mismatched.append(
                    f"{pack.name} (built with "
                    f"{pack.meta.get('embedding_model', 'an unknown model')})"
                )
        if mismatched:
            raise DocsUnavailable(
                f"Semantic search is unavailable: {', '.join(mismatched)} was "
                f"built with a different embedding model than this server uses "
                f"({EMBED_MODEL}), so its vectors are not comparable. Use "
                f"docs_lookup with an exact API name instead -- that does not "
                f"depend on embeddings. The operator must rebuild or remove "
                f"the pack to restore semantic search."
            )

        rows = packs_store.search_docs(opened, vectors[0], lang=lang, limit=limit)
        for row in rows:
            row["retrieval"] = "semantic"
        return rows

    return await run_packs(packs_dir, _run)



_DOCS_GET_DESC = (
    "Read a WHOLE documentation page from a public pack, given the `doc_path` "
    "that docs_lookup or docs_search returned. Use it whenever the answer is a "
    "specific detail on a long reference page -- one flag among a hundred, one "
    "row of an options table, one field of a struct. docs_search returns "
    "fragments and deliberately caps how many come from the same page, so the "
    "fragment holding the detail you need may not be among them. Measured: "
    "asked which robocopy option mirrors a directory tree, search correctly "
    "ranked robocopy's reference page first and returned its Syntax and "
    "Examples sections -- /MIR is in the options table, which was not "
    "returned. Pass `source` (the pack name) when the same path could exist in "
    "two packs."
)


_DOCS_LOOKUP_DESC = (
    "Look up an exact API name in PUBLIC documentation packs (Python, React, "
    "and any other pack this server has installed) -- NOT this organisation's "
    "private code. Use it when you know the name: 'os.path.join', 'useState', "
    "'createRoot'. Matching is EXACT (case-insensitive), never fuzzy, because "
    "the anchor comes from the upstream project's own index -- so a hit lands "
    "on the definition itself, not on a page that merely mentions the name. "
    "A miss returns an empty list; use docs_search for an approximate name or "
    "a concept. Narrow with `lang` to a source name such as 'python' or "
    "'react'. Every result carries `source`, `url`, `license` and "
    "`attribution`: cite the url and name the source rather than presenting "
    "the text as your own knowledge."
)

_DOCS_SEARCH_DESC = (
    "Search PUBLIC documentation packs (Python, React, and any other pack "
    "this server has installed) by meaning -- NOT this organisation's private "
    "code; use search_code for that. Use it for concepts and questions "
    "('how do I reset state when a prop changes', 'what does joining paths "
    "do with an absolute segment') rather than exact names, which docs_lookup "
    "resolves precisely. Narrow with `lang` to a source name such as 'python' "
    "or 'react'. Each result carries the document title, the heading trail the "
    "text sits under, an anchored `url`, `source`, `license` and "
    "`attribution`: cite the url and name the source rather than presenting "
    "the text as your own knowledge. Each result also carries `retrieval`: "
    "'semantic' normally, or 'lexical' when the embedding service was "
    "unreachable and the server fell back to keyword matching -- treat "
    "'lexical' results as less precise."
)


async def impact_of_impl(db_path: Path | str, identity: acl.Identity,
                         repo_id: int, path: str,
                         max_depth: int = 3) -> dict[str, Any]:
    return await run_readonly(
        db_path,
        lambda conn: queries.impact_of(
            identity.allowed_repo_ids, conn, repo_id, path, max_depth=max_depth),
    )


_IMPACT_OF_DESC = (
    "Find out WHAT BREAKS if you change a specific file. Give it a repo_id "
    "and a file path -- usually a header -- and it returns every file that "
    "includes it, directly or transitively, grouped by repository, with the "
    "`depth` at which each was reached (1 = includes it directly). Use it "
    "before editing a shared header, changing a struct layout, or altering a "
    "function signature, and use it to decide what to re-test after. This is "
    "the file-level answer; repo_map gives the same picture at repository "
    "granularity when you only need to know which teams to warn. Files in "
    "repos you cannot access are never reported and are never traversed, so "
    "a result can understate the true blast radius if the change is used by "
    "code you cannot see. `truncated: true` means there were more affected "
    "files than were returned -- the real radius is larger, so treat the "
    "change as wide-reaching rather than assuming the list is complete."
)

_FIND_SYMBOL_DESC = (
    "Find where a named symbol (function, class, method, struct, etc.) is "
    "DEFINED, across the repos you have access to. Answers questions like "
    "'where is DecodeFrame defined?' or 'what class implements X?'. Returns "
    "each definition site: repo_id, path_with_namespace, file path, line "
    "range, kind, signature, scope, and `is_public` (0/1 -- 1 means public "
    "visibility). Optionally narrow with "
    "`kind` (e.g. 'function', 'class', 'struct'). This is a DEFINITION "
    "lookup only -- it does not find call sites, comments, or other "
    "mentions of the name; use find_references for that."
)

_FIND_REFERENCES_DESC = (
    "Find occurrences of an identifier by NAME across the repos you have "
    "access to. This is NAME-BASED, LEXICAL matching, not a semantic "
    "reference resolver: it cannot tell a real call from a comment, a "
    "string literal, or an unrelated identifier in another language spelled "
    "the same way, and it CANNOT see references made through a macro or a "
    "function pointer. Each result is flagged `is_definition: true` only "
    "when ctags recorded an actual definition at that exact file and line; "
    "every other result is a lead to inspect, not a confirmed reference. "
    "Qualify any claim you make from this tool's results accordingly -- "
    "treat them as candidates, not confirmed usages."
)

_SEARCH_CODE_DESC = (
    "Full-text search over the CONTENTS of every file you have access to. "
    "Use this to find code by what it contains or does (e.g. 'where is "
    "retry/backoff logic for GitLab requests?'), not to look up a known "
    "symbol name (use find_symbol) or to find uses of an identifier (use "
    "find_references). Query syntax is SQLite FTS5: plain words and short "
    "phrases work best (e.g. `DecodeFrame` or `retry backoff`). Quoting, "
    "boolean operators (AND/OR/NOT), and NEAR(...) can trigger a syntax "
    "error; when that happens this tool reports back what to change rather "
    "than a raw database error -- follow that guidance and resubmit with "
    "plain terms."
)

_GET_FILE_DESC = (
    "Fetch the content of one file by repo_id and path, from a repo you "
    "have access to. Use a `repo_id` value returned by find_symbol, "
    "find_references, search_code, or index_status -- this tool takes only "
    "the numeric repo id, not a repo name or path_with_namespace. Returns "
    "language, size, and content; content longer than 64KB is cut to a "
    "PREFIX and `truncated: true` is set on the result -- when you see that "
    "flag, treat `content` as the start of the file, not the whole thing. "
    "Fails if the repo id is not one you have access to, or the path does "
    "not exist in it; the two failures are deliberately not distinguished, "
    "so do not treat a failure here as proof a repo doesn't exist."
)

_REPO_MAP_DESC = (
    "Show which repos a given repo depends on, and which depend on it, based "
    "on resolved #include edges across the repos you have access to. Use it "
    "to answer 'what breaks if I change this' before editing a shared header. "
    "`weight` is how many distinct files create the dependency, so a weight of "
    "1 is a single #include and a weight of 300 is a core dependency. Repos "
    "you cannot access are omitted entirely -- an empty result may mean no "
    "dependencies, or that they are all in repos you cannot see. Returns "
    "empty if the dependency graph has not been built yet."
)

_INDEX_STATUS_DESC = (
    "Report the indexing state of every repo you have access to: last "
    "indexed commit sha and time, file/symbol/error counts, and -- this is "
    "the reason to call it -- whether the MOST RECENT indexing run timed "
    "out (`last_run_timed_out`) or failed to extract symbols "
    "(`last_run_symbols_failed`), when that run happened (`last_run_at`), "
    "and how many paths are still queued for retry (`queued_retries`). "
    "Call this BEFORE concluding that an empty find_symbol or "
    "find_references result means the symbol doesn't exist: if the repo's "
    "`last_run_symbols_failed` or `last_run_timed_out` is true, its index "
    "is incomplete, not authoritative, and an empty result there means "
    "'not extracted yet', not 'does not exist'."
)

_WHICH_REPO_DESC = (
    "Work out WHICH REPOSITORY a change belongs in, across the repos you have "
    "access to. This is the tool for 'where do I add this?' when you do not "
    "already know the repo. Accepts any of: a plain-language description of "
    "the task -- describe what you're doing, e.g. 'add H.265 support to the "
    "decoder' -- a symbol or function name, a stack trace "
    "or error log pasted verbatim, or a diff you are reviewing -- paste "
    "whichever you actually have, including multi-line text. Each candidate "
    "comes back with `confidence` and a `why` list naming the specific files "
    "and symbols that drove the match, so you can judge the answer instead of "
    "trusting it. An EMPTY result means nothing matched well enough to be "
    "worth reporting, not that the code does not exist -- fall back to "
    "search_code with a distinctive term. Use repo_map afterwards to see what "
    "else depends on the repo you pick."
)


def register_tools(server: FastMCP, cfg: Config) -> None:
    """Register the retrieval tools on `server`.

    Seven over the private code index, plus two over the public documentation
    packs. The documentation tools take no identity and write no audit row --
    see their registration below.

    Each tool pulls the caller's `Identity` from the request context (see
    `_identity`) and passes `identity.allowed_repo_ids` as the first
    positional argument to the matching `argus.store.queries` function --
    never one it constructs or defaults itself. Every handler routes its
    sqlite work through `run_readonly` so it never blocks the event loop
    (see that function's docstring).

    Every handler also records one audit row per call via `_with_audit`
    (Task 8) -- on success and on failure alike, since a denied or errored
    attempt is exactly what an audit log exists to capture. `args` passed to
    `_with_audit` is built from each tool's own typed parameters only, never
    from `ctx` or `identity`, so the caller's bearer token can never reach
    `args_json` (see `_with_audit`'s docstring).
    """
    db_path = cfg.index.db_path
    packs_dir = cfg.packs_dir

    @server.tool(name="docs_get", description=_DOCS_GET_DESC)
    async def docs_get(doc_path: str, source: str | None = None) -> dict | None:
        # Public documentation: no identity, no allowlist, no audit row, for
        # the same reason as docs_lookup below.
        return await docs_get_impl(packs_dir, doc_path, source=source)

    @server.tool(name="docs_lookup", description=_DOCS_LOOKUP_DESC)
    async def docs_lookup(name: str, lang: str | None = None) -> list[dict]:
        # No identity, no allowlist, no audit row. These packs are public
        # documentation: there is no access decision to make and nothing to
        # record one against, and writing to the private index from the public
        # path is exactly the coupling this split exists to prevent.
        return await docs_lookup_impl(packs_dir, name, lang=lang)

    @server.tool(name="docs_search", description=_DOCS_SEARCH_DESC)
    async def docs_search(query: str, lang: str | None = None) -> list[dict]:
        return await docs_search_impl(packs_dir, query, lang=lang)

    @server.tool(name="impact_of", description=_IMPACT_OF_DESC)
    async def impact_of(repo_id: int, path: str, max_depth: int = 3,
                        *, ctx: Context) -> dict[str, Any]:
        identity = _identity(ctx)
        return await _with_audit(
            db_path, "impact_of", identity,
            {"repo_id": repo_id, "path": path, "max_depth": max_depth},
            lambda: impact_of_impl(db_path, identity, repo_id, path,
                                   max_depth=max_depth),
        )

    @server.tool(name="find_symbol", description=_FIND_SYMBOL_DESC)
    async def find_symbol(name: str, kind: str | None = None,
                          branch: str | None = None, *, ctx: Context) -> list[dict]:
        identity = _identity(ctx)
        return await _with_audit(
            db_path, "find_symbol", identity,
            {"name": name, "kind": kind, "branch": branch},
            lambda: find_symbol_impl(db_path, identity, name, kind=kind,
                                     branch=branch),
        )

    @server.tool(name="find_references", description=_FIND_REFERENCES_DESC)
    async def find_references(name: str, branch: str | None = None, *,
                              ctx: Context) -> list[dict]:
        identity = _identity(ctx)
        return await _with_audit(
            db_path, "find_references", identity,
            {"name": name, "branch": branch},
            lambda: find_references_impl(db_path, identity, name, branch=branch),
        )

    @server.tool(name="search_code", description=_SEARCH_CODE_DESC)
    async def search_code(query: str, branch: str | None = None, *,
                          ctx: Context) -> list[dict]:
        identity = _identity(ctx)
        return await _with_audit(
            db_path, "search_code", identity, {"query": query, "branch": branch},
            lambda: search_code_impl(db_path, identity, query, branch=branch),
        )

    @server.tool(name="get_file", description=_GET_FILE_DESC)
    async def get_file(repo_id: int, path: str, *, ctx: Context) -> dict[str, Any]:
        identity = _identity(ctx)
        return await _with_audit(
            db_path, "get_file", identity, {"repo_id": repo_id, "path": path},
            lambda: get_file_impl(db_path, identity, repo_id, path),
        )

    @server.tool(name="index_status", description=_INDEX_STATUS_DESC)
    async def index_status(*, ctx: Context) -> list[dict]:
        identity = _identity(ctx)
        return await _with_audit(
            db_path, "index_status", identity, {},
            lambda: index_status_impl(db_path, identity),
        )

    @server.tool(name="repo_map", description=_REPO_MAP_DESC)
    async def repo_map(repo_id: int, *, ctx: Context) -> dict[str, Any]:
        identity = _identity(ctx)
        return await _with_audit(
            db_path, "repo_map", identity, {"repo_id": repo_id},
            lambda: repo_map_impl(db_path, identity, repo_id),
        )

    @server.tool(name="which_repo", description=_WHICH_REPO_DESC)
    async def which_repo(description: str, branch: str | None = None, *,
                         ctx: Context) -> list[dict]:
        identity = _identity(ctx)
        # description is truncated to 200 chars for the audit row only -- a
        # pasted stack trace or diff can be thousands of lines, and the audit
        # row records what was asked, not the whole payload (see
        # _with_audit's docstring). The full, untruncated description still
        # goes to which_repo_impl below.
        return await _with_audit(
            db_path, "which_repo", identity,
            {"description": description[:200], "branch": branch},
            lambda: which_repo_impl(db_path, identity, description, branch=branch),
        )
