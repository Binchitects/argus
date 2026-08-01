from __future__ import annotations

from collections.abc import Callable
from typing import Any, TypeVar

from mcp.server.fastmcp import Context, FastMCP
from starlette.concurrency import run_in_threadpool

from .. import acl
from ..config import Config
from ..store import queries
from ..store.db import connect_readonly

T = TypeVar("T")


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
    """
    return ctx.request_context.request.state.identity


async def run_readonly(db_path: Any, fn: Callable[[Any], T]) -> T:
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
    """
    def _call() -> T:
        conn = connect_readonly(db_path)
        try:
            return fn(conn)
        finally:
            conn.close()

    return await run_in_threadpool(_call)


async def find_symbol_impl(db_path: Any, identity: acl.Identity, name: str,
                            kind: str | None = None) -> list[dict]:
    rows = await run_readonly(
        db_path,
        lambda conn: queries.find_symbol(identity.allowed_repo_ids, conn, name, kind=kind),
    )
    return [dict(row) for row in rows]


async def find_references_impl(db_path: Any, identity: acl.Identity, name: str) -> list[dict]:
    # queries.find_references already returns list[dict] (unlike the other
    # three, which return list[sqlite3.Row]) -- no conversion needed.
    return await run_readonly(
        db_path,
        lambda conn: queries.find_references(identity.allowed_repo_ids, conn, name),
    )


async def search_code_impl(db_path: Any, identity: acl.Identity, query: str) -> list[dict]:
    # queries.QueryError is deliberately left to propagate uncaught: its
    # message is already written to be actionable prompt text (see
    # queries.py), and FastMCP's tool dispatch turns any exception raised
    # here into an isError=True CallToolResult carrying str(exc) -- never a
    # raw traceback or a bare SQLite message.
    rows = await run_readonly(
        db_path,
        lambda conn: queries.search_code(identity.allowed_repo_ids, conn, query),
    )
    return [dict(row) for row in rows]


async def get_file_impl(db_path: Any, identity: acl.Identity, repo_id: int,
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


async def index_status_impl(db_path: Any, identity: acl.Identity) -> list[dict]:
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
    "range, kind, signature, scope, and visibility. Optionally narrow with "
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
