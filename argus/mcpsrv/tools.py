from __future__ import annotations

from collections.abc import Callable
from pathlib import Path
from typing import Any, TypeVar

from mcp.server.fastmcp import Context, FastMCP
from starlette.concurrency import run_in_threadpool

from .. import acl
from ..config import Config
from ..store import queries
from ..store.db import connect_readonly

T = TypeVar("T")


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


async def find_symbol_impl(db_path: Path | str, identity: acl.Identity, name: str,
                            kind: str | None = None) -> list[dict]:
    rows = await run_readonly(
        db_path,
        lambda conn: queries.find_symbol(identity.allowed_repo_ids, conn, name, kind=kind),
    )
    return [dict(row) for row in rows]


async def find_references_impl(db_path: Path | str, identity: acl.Identity, name: str) -> list[dict]:
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
        rows = queries.find_references(identity.allowed_repo_ids, conn, name)
        if not rows:
            return rows
        repo_id_by_namespace = {
            status["path_with_namespace"]: status["repo_id"]
            for status in queries.index_status(identity.allowed_repo_ids, conn)
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


async def search_code_impl(db_path: Path | str, identity: acl.Identity, query: str) -> list[dict]:
    # queries.QueryError's message is otherwise already actionable prompt
    # text (see queries.py) and FastMCP's tool dispatch turns any exception
    # raised here into an isError=True CallToolResult carrying str(exc) --
    # never a raw traceback or a bare SQLite message -- so only the dangling
    # regex=True suggestion needs to be caught and rewritten; everything else
    # about the message is left alone.
    try:
        rows = await run_readonly(
            db_path,
            lambda conn: queries.search_code(identity.allowed_repo_ids, conn, query),
        )
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


def register_tools(server: FastMCP, cfg: Config) -> None:
    """Register the five Phase 2 retrieval tools on `server`.

    Each tool pulls the caller's `Identity` from the request context (see
    `_identity`) and passes `identity.allowed_repo_ids` as the first
    positional argument to the matching `argus.store.queries` function --
    never one it constructs or defaults itself. Every handler routes its
    sqlite work through `run_readonly` so it never blocks the event loop
    (see that function's docstring).
    """
    db_path = cfg.index.db_path

    @server.tool(name="find_symbol", description=_FIND_SYMBOL_DESC)
    async def find_symbol(name: str, kind: str | None = None, *, ctx: Context) -> list[dict]:
        identity = _identity(ctx)
        return await find_symbol_impl(db_path, identity, name, kind=kind)

    @server.tool(name="find_references", description=_FIND_REFERENCES_DESC)
    async def find_references(name: str, *, ctx: Context) -> list[dict]:
        identity = _identity(ctx)
        return await find_references_impl(db_path, identity, name)

    @server.tool(name="search_code", description=_SEARCH_CODE_DESC)
    async def search_code(query: str, *, ctx: Context) -> list[dict]:
        identity = _identity(ctx)
        return await search_code_impl(db_path, identity, query)

    @server.tool(name="get_file", description=_GET_FILE_DESC)
    async def get_file(repo_id: int, path: str, *, ctx: Context) -> dict[str, Any]:
        identity = _identity(ctx)
        return await get_file_impl(db_path, identity, repo_id, path)

    @server.tool(name="index_status", description=_INDEX_STATUS_DESC)
    async def index_status(*, ctx: Context) -> list[dict]:
        identity = _identity(ctx)
        return await index_status_impl(db_path, identity)
