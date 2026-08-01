from __future__ import annotations

import logging
import time

import httpx
from mcp.server.fastmcp import FastMCP
from starlette.applications import Starlette
from starlette.concurrency import run_in_threadpool
from starlette.datastructures import Headers
from starlette.requests import Request
from starlette.responses import JSONResponse, Response
from starlette.types import ASGIApp, Receive, Scope, Send

from .. import acl
from ..config import Config
from ..store import writes
from ..store.db import connect, migrate
from .errors import unauthorized
from .tools import register_tools

HEALTHZ_PATH = "/healthz"

log = logging.getLogger(__name__)

# The tool column is NOT NULL, but a request denied here (in the middleware,
# ahead of FastMCP's own routing) has no tool identity yet -- the JSON-RPC
# body naming one, if any, is unparsed at this point. This fixed sentinel
# marks "denied before any tool was dispatched" rather than leaving the
# column blank or parsing the body just to populate it.
_DENIED_AT_GATE_TOOL = "<auth_denied>"


def _extract_bearer(header_value: str | None) -> str | None:
    """Pull the token out of a well-formed ``Authorization: Bearer <token>``.

    Returns None -- "malformed", uniformly -- for a missing header, a
    non-Bearer scheme, or a Bearer header with no (or blank) token. All three
    must be rejected before acl.resolve is ever called: none of them is a
    credential acl.resolve could meaningfully evaluate.

    The scheme is matched case-insensitively -- RFC 7235 draws no
    distinction between ``Bearer`` and ``bearer`` -- while the token itself
    stays case-sensitive.
    """
    if not header_value:
        return None
    scheme, sep, token = header_value.partition(" ")
    if not sep or scheme.lower() != "bearer":
        return None
    token = token.strip()
    return token or None


class BearerAuthMiddleware:
    """Raw ASGI middleware gating every request except ``/healthz``.

    Extracts the bearer token, resolves it to an `Identity` via
    `argus.acl.resolve`, and attaches it to the request as
    `request.state.identity` before the wrapped app (FastMCP's own routing,
    including tool calls added in Task 7) ever runs. A missing, malformed, or
    non-Bearer header, or an `AclDenied` from resolution, short-circuits with
    a 401 and never reaches the wrapped app at all.

    Written as a plain ASGI callable rather than Starlette's
    `BaseHTTPMiddleware` because the streamable-HTTP/SSE transports hold
    long-lived streaming connections; `BaseHTTPMiddleware` buffers and can
    interfere with that.

    Connection strategy: `acl.resolve` upserts the ACL cache, so it needs a
    read-write connection, and `sqlite3` connections are not safe to share
    across concurrent requests even with `check_same_thread=False`. This
    middleware opens one plain `connect()` connection per incoming HTTP
    request and closes it before returning -- no connection is held across
    requests or threaded through server state, and no schema migration runs
    here (see `create_app`). Task 7's read-only tool queries follow the same
    per-request pattern with `connect_readonly`. A short-lived sqlite
    connection is cheap next to the GitLab round-trip `acl.resolve` already
    makes on a cache miss, so this is the simplest defensible choice, not a
    performance compromise.

    Blocking I/O and the event loop: `acl.resolve` performs synchronous
    `httpx.Client` calls (a `/user` request plus up to `MAX_PAGES` sequential
    `/projects` pages, each with a 15s timeout), and `open_db`/`sqlite3` are
    synchronous too. Run directly on `__call__`'s coroutine, that would
    execute on uvicorn's single event-loop thread and stall every other
    in-flight connection for the duration of one cache-miss auth. `_resolve_identity`
    therefore runs inside `starlette.concurrency.run_in_threadpool`, which
    hands the whole open/use/close sequence to a single worker thread so it
    never crosses threads, keeping `sqlite3`'s default `check_same_thread=True`
    satisfied.
    """

    def __init__(self, app: ASGIApp, cfg: Config, client: httpx.Client | None = None):
        self.app = app
        self.cfg = cfg
        self.client = client

    def _resolve_identity(self, token: str) -> acl.Identity:
        """Open a connection, resolve the identity, close the connection.

        Runs entirely inside one `run_in_threadpool` worker thread (see the
        class docstring) so the open/use/close sequence never crosses
        threads, satisfying `sqlite3`'s `check_same_thread=True` default.
        """
        conn = connect(self.cfg.index.db_path)
        try:
            return acl.resolve(conn, self.cfg.gitlab, token, client=self.client)
        finally:
            conn.close()

    def _write_denied_audit(self) -> None:
        """Record that a request was denied before any tool ever ran.

        Called from both places `__call__` rejects a request ahead of tool
        dispatch: a missing/malformed/non-Bearer/blank-token header (`token
        is None`) and an `AclDenied` from resolution. Either way this is
        exactly what an audit log exists to capture (Task 8) -- a developer
        attempted access and was refused. `user_id` and `username` are None:
        no identity was ever resolved (in the `token is None` case, none was
        even attempted). `tool` is the fixed `_DENIED_AT_GATE_TOOL` sentinel,
        not a name read from the request body -- this middleware runs ahead
        of FastMCP's own JSON-RPC parsing, and reading the body here for a
        value with no other use would mean consuming the ASGI receive stream
        for it.

        Opens its own read-write connection (`connect`, never
        `connect_readonly`), separate from `_resolve_identity`'s: whenever
        both run in the same request (the `AclDenied` path), this call
        happens strictly after `_resolve_identity` has already returned
        control, so the two never overlap on the same connection -- but each
        keeps its own independent open/use/close cycle regardless.
        """
        conn = connect(self.cfg.index.db_path)
        try:
            writes.record_audit(
                conn, ts=int(time.time()), user_id=None, username=None,
                tool=_DENIED_AT_GATE_TOOL, args_json="{}", repo_ids_json=None,
            )
        finally:
            conn.close()

    async def _audit_denied(self) -> None:
        """Run `_write_denied_audit` off the event loop; never let it raise.

        Same one-thread-per-connection discipline as `_resolve_identity`:
        the whole open/execute/commit/close sequence runs inside a single
        `run_in_threadpool` call. A failed audit write (disk full, database
        locked) must not turn an already-decided 401 into a 500 or an
        unhandled exception -- the failure is logged and swallowed so the
        caller in `__call__` always reaches `unauthorized(...)` next.
        """
        try:
            await run_in_threadpool(self._write_denied_audit)
        except Exception:
            log.warning("failed to record audit row for a denied request", exc_info=True)

    async def __call__(self, scope: Scope, receive: Receive, send: Send) -> None:
        if scope["type"] != "http" or scope.get("path") == HEALTHZ_PATH:
            await self.app(scope, receive, send)
            return

        token = _extract_bearer(Headers(scope=scope).get("authorization"))
        if token is None:
            await self._audit_denied()
            response = unauthorized(
                "Missing or malformed Authorization header. "
                "Expected 'Authorization: Bearer <token>'."
            )
            await response(scope, receive, send)
            return

        try:
            identity = await run_in_threadpool(self._resolve_identity, token)
        except acl.AclDenied as exc:
            await self._audit_denied()
            await unauthorized(str(exc))(scope, receive, send)
            return

        # scope is the same mapping FastMCP's transports carry through to
        # ServerMessageMetadata.request_context, so a tool handler in Task 7
        # sees this same Identity via ctx.request_context.request.state.
        scope.setdefault("state", {})
        scope["state"]["identity"] = identity
        await self.app(scope, receive, send)


class _ArgusFastMCP(FastMCP):
    """FastMCP that installs `BearerAuthMiddleware` on every ASGI app it builds.

    FastMCP constructs a brand-new `Starlette` instance on each call to
    `streamable_http_app()` / `sse_app()` (only its internal session manager
    is cached), so the middleware is (re)installed inside the override rather
    than once at construction time -- installing it only in `__init__` would
    silently stop applying the moment either method is called again.
    """

    def __init__(self, cfg: Config, *, client: httpx.Client | None = None, **kwargs):
        super().__init__(**kwargs)
        self._argus_cfg = cfg
        self._argus_client = client

    def streamable_http_app(self) -> Starlette:
        app = super().streamable_http_app()
        app.add_middleware(BearerAuthMiddleware, cfg=self._argus_cfg, client=self._argus_client)
        return app

    def sse_app(self, mount_path: str | None = None) -> Starlette:
        app = super().sse_app(mount_path)
        app.add_middleware(BearerAuthMiddleware, cfg=self._argus_cfg, client=self._argus_client)
        return app


def create_app(cfg: Config, *, client: httpx.Client | None = None) -> FastMCP:
    """Build the Argus MCP server skeleton.

    Serves Streamable HTTP (and SSE) via the official `mcp` SDK's FastMCP, so
    it works regardless of which transport Hermes negotiates. `/healthz`
    requires no authentication; every other route -- including every tool
    call -- is gated by `BearerAuthMiddleware`. The five Phase 2 retrieval
    tools (`argus.mcpsrv.tools.register_tools`) are registered on the
    returned server below, after migration.

    `client` overrides the `httpx.Client` used by `acl.resolve` on every
    request; production callers leave it None, in which case `acl.resolve`
    opens (and closes) its own real client per call. Tests pass an
    `httpx.Client(transport=httpx.MockTransport(...))` so no test reaches a
    real GitLab host.

    Migration runs exactly once, here, at startup -- never on the
    per-request connection `BearerAuthMiddleware` opens. `migrate()` applies
    schema, and this project's own discipline is that an applied migration is
    never edited; `connect_readonly`'s docstring is stricter still ("the
    server must never write index data"). Migrating on the per-request
    connection would hand unauthenticated inbound traffic the ability to
    trigger a schema change purely by arriving first -- e.g. the very first
    request after deploying a build carrying a new migration applies it.
    This call is still required, not merely an optimisation: a plain
    `connect()` against a database that has never been migrated creates an
    empty file with no tables, and the ACL-cache lookup inside `acl.resolve`
    would then fail with "no such table".
    """
    conn = connect(cfg.index.db_path)
    try:
        migrate(conn)
    finally:
        conn.close()

    server = _ArgusFastMCP(cfg, client=client, name="argus")

    @server.custom_route(HEALTHZ_PATH, methods=["GET"])
    async def healthz(request: Request) -> Response:
        return JSONResponse({"status": "ok"})

    register_tools(server, cfg)

    return server
