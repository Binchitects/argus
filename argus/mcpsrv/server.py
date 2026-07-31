from __future__ import annotations

import httpx
from mcp.server.fastmcp import FastMCP
from starlette.applications import Starlette
from starlette.datastructures import Headers
from starlette.requests import Request
from starlette.responses import JSONResponse, Response
from starlette.types import ASGIApp, Receive, Scope, Send

from .. import acl
from ..config import Config
from ..store.db import open_db
from .errors import unauthorized

HEALTHZ_PATH = "/healthz"
_BEARER_PREFIX = "Bearer "


def _extract_bearer(header_value: str | None) -> str | None:
    """Pull the token out of a well-formed ``Authorization: Bearer <token>``.

    Returns None -- "malformed", uniformly -- for a missing header, a
    non-Bearer scheme, or a Bearer header with no (or blank) token. All three
    must be rejected before acl.resolve is ever called: none of them is a
    credential acl.resolve could meaningfully evaluate.
    """
    if not header_value or not header_value.startswith(_BEARER_PREFIX):
        return None
    token = header_value[len(_BEARER_PREFIX):].strip()
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
    middleware opens one connection per incoming HTTP request via
    `argus.store.db.open_db` and closes it before returning -- no connection
    is held across requests or threaded through server state. Task 7's
    read-only tool queries follow the same per-request pattern with
    `connect_readonly`. A short-lived sqlite connection is cheap next to the
    GitLab round-trip `acl.resolve` already makes on a cache miss, so this is
    the simplest defensible choice, not a performance compromise.
    """

    def __init__(self, app: ASGIApp, cfg: Config, client: httpx.Client | None = None):
        self.app = app
        self.cfg = cfg
        self.client = client

    async def __call__(self, scope: Scope, receive: Receive, send: Send) -> None:
        if scope["type"] != "http" or scope.get("path") == HEALTHZ_PATH:
            await self.app(scope, receive, send)
            return

        token = _extract_bearer(Headers(scope=scope).get("authorization"))
        if token is None:
            response = unauthorized(
                "Missing or malformed Authorization header. "
                "Expected 'Authorization: Bearer <token>'."
            )
            await response(scope, receive, send)
            return

        conn = open_db(self.cfg.index.db_path)
        try:
            identity = acl.resolve(conn, self.cfg.gitlab, token, client=self.client)
        except acl.AclDenied as exc:
            await unauthorized(str(exc))(scope, receive, send)
            return
        finally:
            conn.close()

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
    requires no authentication; every other route is gated by
    `BearerAuthMiddleware`. No tools are registered here -- that is Task 7.

    `client` overrides the `httpx.Client` used by `acl.resolve` on every
    request; production callers leave it None, in which case `acl.resolve`
    opens (and closes) its own real client per call. Tests pass an
    `httpx.Client(transport=httpx.MockTransport(...))` so no test reaches a
    real GitLab host.
    """
    server = _ArgusFastMCP(cfg, client=client, name="argus")

    @server.custom_route(HEALTHZ_PATH, methods=["GET"])
    async def healthz(request: Request) -> Response:
        return JSONResponse({"status": "ok"})

    return server
