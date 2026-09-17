from __future__ import annotations

import hmac
import logging
import os
import subprocess
import sys
import threading
import time

import httpx
from mcp.server.fastmcp import FastMCP
from mcp.server.transport_security import TransportSecuritySettings

from starlette.applications import Starlette
from starlette.concurrency import run_in_threadpool
from starlette.datastructures import Headers
from starlette.requests import Request
from starlette.responses import JSONResponse, Response
from starlette.types import ASGIApp, Receive, Scope, Send

from .. import access, acl, auditlog
from ..config import Config
from ..store import explore, writes
from ..store.db import connect, connect_audit, connect_readonly, migrate
from .errors import unauthorized
from . import metrics
from .tools import register_tools

HEALTHZ_PATH = "/healthz"

#: Operator control surface. Gated by ARGUS_ADMIN_TOKEN, NOT by the GitLab
#: bearer identity that guards every tool call: indexing is an estate-wide
#: operator action, and "can read some repo" is not "may reindex everything".
#:
#: FAIL CLOSED. With no ARGUS_ADMIN_TOKEN set these routes are not registered at
#: all and the prefix is not exempted from BearerAuthMiddleware, so the surface
#: does not exist rather than existing unprotected. That is the default.
ADMIN_PREFIX = "/admin/"
#: The shared credential Open WebUI presents (see BearerAuthMiddleware._chat_client).
CHAT_TOKEN_ENV = "ARGUS_CHAT_CLIENT_TOKEN"
#: Who is asking, as Open WebUI forwards it (ENABLE_FORWARD_USER_INFO_HEADERS).
CHAT_EMAIL_HEADER = "x-openwebui-user-email"
#: Authelia's account file, to map a chat user's email to their sign-in username.
USERS_FILE_ENV = "ARGUS_AUTHELIA_USERS_FILE"
#: Present on anything that came through Traefik.
_PROXY_HEADERS = ("x-forwarded-for", "x-forwarded-host", "x-real-ip")
ADMIN_TOKEN_ENV = "ARGUS_ADMIN_TOKEN"

#: Where GitLab posts a push event. NOT under ADMIN_PREFIX on purpose: that
#: prefix is gated by the admin token, which the admin console holds and which
#: must not be handed to GitLab. A webhook secret is a lower-privilege thing --
#: leaking it lets somebody cause an index pass, not read the estate -- so it
#: gets its own value.
WEBHOOK_PATH = "/hook/gitlab"
WEBHOOK_TOKEN_ENV = "ARGUS_WEBHOOK_TOKEN"

#: GitLab's own header for the secret token configured on the webhook.
WEBHOOK_HEADER = "x-gitlab-token"

#: How many repositories may be waiting to be indexed before the queue is
#: collapsed into one full pass. A force-push across a large estate, or a
#: misconfigured webhook firing on every branch, can enqueue faster than a pass
#: drains; past this point indexing everything once is both cheaper and more
#: correct than indexing each thing in turn.
WEBHOOK_QUEUE_LIMIT = 25


def webhook_token() -> str:
    return os.environ.get(WEBHOOK_TOKEN_ENV, "").strip()


def _webhook_enabled() -> bool:
    return bool(webhook_token())


def _admin_token() -> str:
    return os.environ.get(ADMIN_TOKEN_ENV, "").strip()


def _admin_enabled() -> bool:
    return bool(_admin_token())

log = logging.getLogger(__name__)

# The Host-header allowlist FastMCP's own `__init__` would auto-compute for a
# loopback `host` at construction time (see `_build_transport_security`'s
# docstring below for why we no longer let it do that implicitly). Kept as
# the explicit default here so a bare `argus serve` -- no `--allowed-host`
# passed -- is byte-for-byte the same allowlist as before this fix.
DEFAULT_ALLOWED_HOSTS: tuple[str, ...] = ("127.0.0.1:*", "localhost:*", "[::1]:*")


def _build_transport_security(
    allowed_hosts: list[str] | tuple[str, ...] | None,
) -> TransportSecuritySettings:
    """Build an explicit DNS-rebinding allowlist, independent of bind host.

    FastMCP's `__init__` only auto-populates `transport_security` when its
    `host` constructor argument is itself a loopback literal
    (`127.0.0.1`/`localhost`/`::1`), and it does that exactly once, at
    construction. `_serve` (src/argus/cli.py) builds this app before it applies
    the operator's `--host`, then overrides `app.settings.host` afterwards --
    which changes the bind address but leaves the already-computed
    `transport_security.allowed_hosts` untouched. Behind a reverse proxy
    (Traefik in the stack in `stack/`, forwarding the client's real `Host`
    header -- e.g. `argus.internal`), that stale localhost-only allowlist
    rejects every request with 421, including every `/mcp` call --
    the only thing Hermes actually uses.

    `create_app` calls this unconditionally, so `transport_security` is
    always explicit and never left for FastMCP to infer from `host`. That
    removes the incidental host-at-construction-time coupling that caused
    the bug, rather than papering over the one call site (`_serve`) that
    tripped over it.

    `allowed_hosts=None` (nothing passed on the CLI) reproduces the original
    localhost-only default exactly, including `allowed_origins` with only the
    `http://` scheme -- matching what FastMCP itself would have computed.
    Operator-supplied hosts get both `http://` and `https://` origins, since
    a real deployment's client-facing scheme is `https` (terminated by
    Caddy) while the proxy-to-server hop is often plain `http`.
    """
    if allowed_hosts:
        hosts = list(allowed_hosts)
        origins = [f"{scheme}://{h}" for h in hosts for scheme in ("http", "https")]
    else:
        hosts = list(DEFAULT_ALLOWED_HOSTS)
        origins = [f"http://{h}" for h in hosts]
    return TransportSecuritySettings(
        enable_dns_rebinding_protection=True,
        allowed_hosts=hosts,
        allowed_origins=origins,
    )


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
        self._directory: access.MemberDirectory | None = None

    # ------------------------------------------------------------------ chat
    # Open WebUI calls Argus for whoever is signed in, but can send only one
    # shared credential. It proves it is the chat client with CHAT_TOKEN_ENV and
    # names the person in CHAT_EMAIL_HEADER; access is then read from GitLab
    # membership with the service credential (argus.access) -- read-only, no
    # admin or sudo.
    #
    # The email header is trusted ONLY with that token, and ONLY on a request
    # that did not come through the reverse proxy. Open WebUI calls
    # http://argus:7700 inside the compose network; anything arriving via
    # Traefik carries X-Forwarded-For, so the token leaking to a browser still
    # cannot be used to claim someone else's email.
    def _chat_client(self, token: str) -> bool:
        expected = os.environ.get(CHAT_TOKEN_ENV, "")
        return bool(expected) and hmac.compare_digest(token, expected)

    def _resolve_chat_person(self, email: str) -> acl.Identity:
        if self._directory is None:
            self._directory = access.MemberDirectory(self.cfg.gitlab, client=self.client)
        conn = connect(self.cfg.index.db_path)
        try:
            return access.resolve_person(conn, self._directory, email,
                                         users_file=os.environ.get(USERS_FILE_ENV))
        finally:
            conn.close()

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
        # Sidecar, matching the tool-call path: a denial at the gate must not
        # queue behind an indexing run either.
        conn = connect_audit(self.cfg.index.db_path)
        try:
            writes.record_audit(
                conn, ts=int(time.time()), user_id=None, username=None,
                tool=_DENIED_AT_GATE_TOOL, args_json="{}", repo_ids_json=None,
            )
        finally:
            conn.close()

    async def _audit_denied(self, reason: str = "denied", path: str = "",
                            detail: str | None = None) -> None:
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
        # `detail` carries the sentence the caller was told. Without it the log
        # line says only "token_rejected", and Open WebUI renders the 401 as
        # "failed to connect to argus" -- so the operator goes looking for a
        # network fault while the actual reason sits in a response body nobody
        # kept. Loki indexes the line either way; `| json | detail!=""` finds
        # every refusal that had something to say.
        auditlog.denied(reason=reason, path=path, detail=detail)

    async def __call__(self, scope: Scope, receive: Receive, send: Send) -> None:
        _path = scope.get("path") or ""
        # The admin prefix carries its own credential (see ADMIN_PREFIX) and is
        # exempted only while that credential is configured. Unset, the prefix
        # is gated like everything else and the routes do not exist anyway.
        if (scope["type"] != "http" or _path == HEALTHZ_PATH
                or (_admin_enabled() and _path.startswith(ADMIN_PREFIX))
                or (_webhook_enabled() and _path == WEBHOOK_PATH)):
            await self.app(scope, receive, send)
            return

        token = _extract_bearer(Headers(scope=scope).get("authorization"))
        if token is None:
            await self._audit_denied("missing_token", _path)
            response = unauthorized(
                "Missing or malformed Authorization header. "
                "Expected 'Authorization: Bearer <token>'."
            )
            await response(scope, receive, send)
            return

        try:
            if self._chat_client(token):
                headers = Headers(scope=scope)
                if any(h in headers for h in _PROXY_HEADERS):
                    raise acl.AclDenied("The chat-client credential is accepted only from inside "
                                        "the stack's network, not through the proxy.")
                identity = await run_in_threadpool(
                    self._resolve_chat_person, headers.get(CHAT_EMAIL_HEADER, ""))
            else:
                identity = await run_in_threadpool(self._resolve_identity, token)
        except acl.AclDenied as exc:
            await self._audit_denied("token_rejected", _path, detail=str(exc))
            await unauthorized(str(exc))(scope, receive, send)
            return

        # scope is the same mapping FastMCP's transports carry through to
        # ServerMessageMetadata.request_context, so a tool handler in Task 7
        # sees this same Identity via ctx.request_context.request.state.
        scope.setdefault("state", {})
        scope["state"]["identity"] = identity
        await self.app(scope, receive, send)


#: Sent to every client at connect time. MCP carries this to the agent's
#: system context, so it is the one place a server can influence when its
#: tools get used -- and measured, that matters more than the tools.
#:
#: An agent given these tools and left to choose called one on 3 of 20
#: questions, on a set where nearly every question had a documented answer it
#: did not know: 12/20 against 8/20 closed book, while hard-coded retrieval
#: over the same corpus reaches 84%. Adding exactly this text as a system
#: message took tool use to 8 of 20 and accuracy to 14/20.
#:
#: It is deliberately about *when to distrust yourself* rather than a list of
#: what each tool does -- the tools already describe themselves, and the
#: measured failure was never that the agent picked the wrong tool. It was
#: that a confident model does not think to look.
SERVER_INSTRUCTIONS = """This server indexes your organisation's private code and installs public
documentation packs (Windows SDK, WDK, MSVC C++, PowerShell and shell
tooling, algorithms, system design).

Recollection of exact API details is unreliable even when it feels certain:
header names, import libraries, IRQL constraints, diagnostic codes and command
flags are the facts models most often get confidently wrong. When a question
turns on one of those, check it here before answering rather than after.

- docs_lookup when you know the name.
- docs_find when you know only what something does.
- docs_search then docs_get when you need a page, and the whole page when the
  answer is one row of a reference table.
- docs_verify to check a draft you have already written; it reports only what
  the documentation contradicts, so it cannot overwrite something you had
  right.

When you state an API's requirement -- an IRQL, a header, a library, an error
code -- COPY the documented string verbatim and name the API it belongs to.
Do not restate it in your own words and do not infer one from what a routine
appears to do. If these tools are silent on an API, say its requirement is not
documented rather than supplying one.

That rule is the single largest measured effect here. Across five real driver
files, contract claims made from memory were wrong 100% of the time, and
claims made with the documented facts present but paraphrased were wrong 33%
of the time. Quoted verbatim, 18 claims were wrong 0 times. Paraphrasing
re-enters generation, where a prior like "initialisation routine means
PASSIVE_LEVEL" competes with the fact and often wins; copying does not.

Otherwise use retrieved documentation to correct yourself, not to replace what
you already know: where these tools are silent, your own answer stands.

ORIENT YOURSELF BEFORE GUESSING NAMES.

In an unfamiliar codebase, or on any question that spans more than one
repository, call `overview` first. It describes what each repository IS -- its
README, its layout, the abstractions somebody documented, and the cross-repo
dependencies -- so the rest of your search starts from somewhere instead of
from a guessed symbol name. Names alone are not an architecture.

FINDING CODE BY WHAT IT DOES.

When you need something and do not know what it is called -- "what reclaims
keys whose time to live has elapsed", "where do we retry uploads" --
`semantic_search` is the tool. It matches on meaning, and since the index reads
each symbol's doc comment, it matches on what a function DOES rather than on
what it is named. Read the `doc` field on every result before choosing: it is
the sentence that says whether the symbol is the one you want, and it is
returned for exactly that reason. Measured: for a question phrased this way,
ranking on names alone returns the WRONG function and ranking with the doc
returns the right one.

Each result also carries `kind`, `signature`, `scope`, `path` and the
repository, which is how you tell where a hit belongs in the application."""


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


def create_app(
    cfg: Config,
    *,
    client: httpx.Client | None = None,
    allowed_hosts: list[str] | tuple[str, ...] | None = None,
) -> FastMCP:
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

    `allowed_hosts` is the operator-controlled DNS-rebinding allowlist (see
    `_build_transport_security`); `None` reproduces the original
    localhost-only default. It is built into `transport_security` here, at
    construction, rather than left for `_serve` to reconcile onto
    `app.settings` after the fact -- see `_build_transport_security`'s
    docstring for why that reconciliation was the bug.

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

    server = _ArgusFastMCP(
        cfg, client=client, name="argus",
        instructions=SERVER_INSTRUCTIONS,
        transport_security=_build_transport_security(allowed_hosts),
    )

    @server.custom_route(HEALTHZ_PATH, methods=["GET"])
    async def healthz(request: Request) -> Response:
        return JSONResponse({"status": "ok"})

    if _admin_enabled():
        _register_admin_routes(server, cfg)

    # The GitLab push webhook. Registered only when a secret is configured --
    # same discipline as the admin surface: unset means the route does not
    # exist, rather than existing and accepting anything that finds it.
    if _webhook_enabled():
        _register_webhook_route(server, cfg)

    # Periodic reindexing. Deliberately NOT gated on the admin token: keeping
    # the index current is a core function, not an operator convenience, and a
    # deployment that never reindexes serves yesterday's answers no matter who
    # can reach the console. Off unless ARGUS_INDEX_INTERVAL says otherwise --
    # see _scheduler for why it runs in this process and not as its own
    # container.
    if index_interval() > 0:
        threading.Thread(target=_scheduler, args=(cfg, _cfg_path(cfg)),
                         daemon=True).start()

    register_tools(server, cfg)

    return server


#: One index run at a time. A second concurrent pass over the same SQLite index
#: is not merely wasteful: both would write, and writers serialise, so the two
#: would spend the run blocking each other while appearing to progress.
_index_job: dict = {"state": "idle", "branches": [], "started": None,
                    "finished": None, "returncode": None, "tail": [],
                    "trigger": None,
                    # Repositories a webhook asked for while a pass was running.
                    # A list, not a set, so the order they arrived is the order
                    # they are drained and the panel can show it.
                    "pending": [],
                    # Set when the queue overflowed: the next pass covers
                    # everything instead of working through the backlog.
                    "pending_full": False}
_index_lock = threading.Lock()

#: Seconds between automatic index passes. 0 -- the default -- means only a
#: person pressing the button ever reindexes.
DEFAULT_INDEX_INTERVAL = 0

#: Grace period before the first automatic pass, when the index needs one.
#: Long enough for the container to be reachable and its healthcheck to have
#: passed; short enough that a fresh drop-in deployment is not sitting on an
#: empty index for a quarter of an hour while the admin console insists
#: everything is fine.
FIRST_PASS_GRACE = 30


def index_interval() -> int:
    """Seconds between automatic passes; <= 0 disables them. Read per call, not
    at import, so it can be changed and tested like any other setting."""
    try:
        return int(os.environ.get("ARGUS_INDEX_INTERVAL", DEFAULT_INDEX_INTERVAL))
    except ValueError:
        return DEFAULT_INDEX_INTERVAL


def _index_is_current(db_path) -> bool:
    """Is every repository in the index fresh?

    A missing or unreadable index is not current, which is exactly what makes
    a fresh deployment -- empty named volume, no tables yet -- index itself
    shortly after it comes up instead of waiting out a full interval.
    """
    try:
        snap = metrics.snapshot(db_path)
    except Exception:               # noqa: BLE001 - unreadable == not current
        return False
    return bool(snap["repos"]) and snap["stale_repos"] == 0


def _cfg_path(cfg) -> str:
    """Where `argus index` will read its configuration from.

    The same value the admin routes use, so a run started by the schedule and
    a run started by the button cannot be indexing different estates.
    """
    return str(getattr(cfg, "source_path", "") or
               os.environ.get("ARGUS_CONFIG", "/etc/argus/config.yaml"))


def _start_index(cfg_path: str, branches: list[str] | None = None,
                 allow_partial: bool = False, trigger: str = "manual") -> bool:
    """Claim the single index slot and run in the background.

    Returns False when a pass is already in flight, having started nothing.
    The claim and the state update happen under one lock, so two callers --
    a person and the schedule, or two people -- cannot both believe they won.
    """
    with _index_lock:
        if _index_job["state"] == "running":
            return False
        _index_job.update(state="running", branches=list(branches or []),
                          allow_partial=allow_partial, trigger=trigger,
                          started=time.time(), finished=None,
                          returncode=None, tail=[])
    threading.Thread(target=_run_index,
                     args=(cfg_path, list(branches or []), allow_partial),
                     daemon=True).start()
    return True


def _scheduler(cfg, cfg_path: str) -> None:
    """Reindex on a timer, in THIS process rather than in a second container.

    WHY NOT `argus index --interval 900` AS ITS OWN SERVICE

    That command already loops, and a second compose service is the obvious
    shape. It is also the wrong one. Both processes would open the same SQLite
    index for writing, and `store.connect` sets no `busy_timeout`, so the
    second writer gets `database is locked` immediately instead of waiting:
    the poller and the admin console's "Index now" button would fail each
    other at random, and neither failure would say why.

    Running the schedule here means one process holds one `_index_lock`, and
    every pass -- scheduled or manual -- is serialised by it.

    It also makes the automatic pass visible. It runs through the same
    `_index_job` the console polls, so the Indexing page shows a scheduled
    run's progress, log and exit code exactly as it shows a manual one, and
    `argus_index_last_run_timestamp_seconds` moves the moment it finishes,
    which is what clears ArgusIndexStale. A second container could not do
    that: its logs would be in Loki but its progress would not be on the page
    the operator is looking at.

    This is the half of the freshness story that was missing. Nineteen alert
    rules and a stale gauge are useless against a deployment where nothing
    ever reindexes -- the gauge would be a permanent warning, which is how
    people learn to ignore warnings.
    """
    interval = index_interval()
    if interval <= 0:
        return
    fresh = _index_is_current(cfg.index.db_path)
    delay = interval if fresh else FIRST_PASS_GRACE
    auditlog.index_scheduled(interval=interval, first_pass_in=delay,
                             reason=None if fresh else "the index is not current")
    while True:
        time.sleep(delay)
        # Start-to-start, not end-to-start: `_start_index` returns as soon as
        # the worker thread has claimed the slot, so a long pass does not push
        # the next one later and later.
        delay = interval
        if not _start_index(cfg_path, trigger="schedule"):
            auditlog.index_scheduled(interval=interval,
                                     skipped="a run is already in progress")


def _run_index(cfg_path: str, branches: list[str],
               allow_partial: bool = False, only: str | None = None) -> None:
    """Run `argus index` as a CHILD PROCESS, never in this one.

    create_app's contract is that the server never writes index data -- it
    opens the index read-only and migrates once at startup precisely so
    inbound traffic cannot mutate it. Indexing in-process would make that
    false. A child gets its own connection and its own write transaction, and
    the serve process keeps the guarantee it documents.

    The child's output is both kept for the panel's live tail AND echoed to
    this process's stdout. Both matter, for different readers: the panel shows
    one run to one operator who is watching it, while stdout is what Docker
    captures, which is what Promtail ships to Loki, which is what makes a run
    visible in Grafana and readable a week later. Capturing without echoing
    meant indexing was the one part of Argus with no log history at all --
    the run happened, failed, and left nothing behind but an exit code.
    """
    argv = [sys.executable, "-m", "argus.cli", "index", "--config", cfg_path]
    for b in branches:
        argv += ["--branch", b]
    if only:
        # A webhook names the repository that changed. Indexing the whole estate
        # because one file moved is the difference between a pass that finishes
        # in seconds and one that finishes in half an hour -- and the poll is
        # what refreshes everything else, on its own schedule.
        argv += ["--repo", only]
    if allow_partial:
        # The panel's opt-in for "index what the token can see". Without a way
        # to pass this, a refusal told the operator to re-run with a flag they
        # had no way to supply from the UI -- a dead end that read as a bug.
        argv.append("--allow-partial-enumeration")
    try:
        proc = subprocess.Popen(argv, stdout=subprocess.PIPE,
                                stderr=subprocess.STDOUT, text=True)
        tail: list[str] = []
        assert proc.stdout is not None
        for line in proc.stdout:
            tail.append(line.rstrip())
            del tail[:-200]          # bounded: an estate-wide pass is chatty
            with _index_lock:
                _index_job["tail"] = list(tail)
            # Verbatim, unprefixed: an audit line is one JSON object starting
            # with {"ts", and the Promtail config selects on exactly that to
            # lift event/outcome labels. A prefix would silently unhook it.
            print(line.rstrip(), flush=True)
        rc = proc.wait()
    except Exception as exc:         # noqa: BLE001
        rc = -1
        with _index_lock:
            _index_job["tail"] = [f"failed to start: {exc!r}"]
    with _index_lock:
        _index_job.update(state="idle", finished=time.time(), returncode=rc)
    _drain_pending(cfg_path)


def _drain_pending(cfg_path: str) -> None:
    """Start the next queued pass, if a webhook asked for one while this ran.

    WHY A QUEUE AND NOT A DROP

    A push webhook that arrives during a pass is the normal case on a busy
    estate, not the exception. Dropping it means the change it reported waits
    for the next poll -- which is exactly the latency the webhook exists to
    remove -- and the operator has no way to tell that it happened.

    WHY ONE AT A TIME, AND A CEILING

    Each pass is a separate `argus index` child, so starting all of them at once
    is starting N processes that would immediately serialise on the same SQLite
    write lock. Draining one per completion keeps the queue flat and the log
    readable. If the queue passes `WEBHOOK_QUEUE_LIMIT` -- a force-push across a
    big estate, or a webhook configured for every branch -- the backlog is
    collapsed into one full pass instead, because indexing everything once is
    both cheaper and more correct than working through the list.

    Failures to start are not retried here. The poll is the floor that
    guarantees freshness; this is only ever an accelerator.
    """
    with _index_lock:
        if _index_job["state"] == "running":
            return
        full = bool(_index_job.get("pending_full"))
        pending = list(_index_job.get("pending") or [])
        if not full and not pending:
            return
        _index_job["pending"] = []
        _index_job["pending_full"] = False
        only = None if full else pending.pop(0)
        # Everything not started now goes back on the queue for the next drain.
        if pending:
            _index_job["pending"] = pending
        _index_job.update(state="running", branches=[], allow_partial=False,
                          trigger="webhook", started=time.time(),
                          finished=None, returncode=None, tail=[])
    auditlog.index_webhook(repo=only or "*", queued=len(pending))
    threading.Thread(target=_run_index, args=(cfg_path, [], False, only),
                     daemon=True).start()


def _enqueue_webhook(repo: str, cfg_path: str) -> dict:
    """Accept a repository for indexing, now or as soon as the current pass ends.

    Returns what was decided, so the caller can report it to GitLab and the
    tests can assert on it rather than on timing.
    """
    with _index_lock:
        if _index_job["state"] == "running":
            pending = list(_index_job.get("pending") or [])
            if repo in pending:
                # A branch push and a tag push to the same project arrive as two
                # events. Indexing it twice is waste, and the queue is drained
                # one at a time, so the duplicate would cost a whole pass.
                return {"status": "already_queued", "repo": repo,
                        "queued": len(pending)}
            pending.append(repo)
            if len(pending) > WEBHOOK_QUEUE_LIMIT:
                _index_job["pending"] = []
                _index_job["pending_full"] = True
                auditlog.index_webhook(repo=repo, collapsed=len(pending))
                return {"status": "collapsed_to_full_pass", "repo": repo,
                        "queued": 0}
            _index_job["pending"] = pending
            auditlog.index_webhook(repo=repo, queued=len(pending))
            return {"status": "queued", "repo": repo, "queued": len(pending)}
        _index_job.update(state="running", branches=[], allow_partial=False,
                          trigger="webhook", started=time.time(),
                          finished=None, returncode=None, tail=[])
    auditlog.index_webhook(repo=repo, started=True)
    threading.Thread(target=_run_index, args=(cfg_path, [], False, repo),
                     daemon=True).start()
    return {"status": "started", "repo": repo, "queued": 0}


def _register_webhook_route(server, cfg) -> None:
    """`POST /hook/gitlab` -- index the repository a push just changed.

    WHAT THIS REPLACES

    Freshness was interval-polled: `ARGUS_INDEX_INTERVAL` defaults to 900s, so
    a push sat unindexed for up to fifteen minutes and every answer in that
    window came from the previous commit with nothing saying so. The poll is
    still the floor -- it is what covers a missed delivery, a webhook nobody
    configured, and a repository the webhook does not fire for -- and this is
    the accelerator in front of it.

    WHY IT IS NOT UNDER /admin/

    The admin token is held by the console and grants the estate-wide operator
    surface. A webhook secret is configured in GitLab and travels through
    GitLab's own storage, so it is the lower-privilege credential and it gets
    its own value: leaking it lets somebody cause an index pass, which is
    recoverable, rather than read the estate, which is not.

    FAIL CLOSED, and answer 401 without saying which half was wrong -- an
    attacker learning "the token is right but the shape is wrong" is an
    attacker learning something.

    Always answers 2xx for a delivery it accepts, even when it did nothing with
    it. GitLab treats a non-2xx as a failed delivery and retries with backoff,
    then disables the webhook; a `push` to a branch Argus does not index is not
    an error and must not cost the operator their webhook.
    """
    cfg_path = _cfg_path(cfg)

    @server.custom_route(WEBHOOK_PATH, methods=["POST"])
    async def gitlab_hook(request: Request) -> Response:
        supplied = request.headers.get(WEBHOOK_HEADER, "")
        # compare_digest, like the admin token: a plain == leaks the secret a
        # byte at a time to anyone who can time the response.
        if not supplied or not hmac.compare_digest(supplied, webhook_token()):
            auditlog.denied(reason="webhook_token_rejected", path=WEBHOOK_PATH,
                            detail=f"header {WEBHOOK_HEADER} missing or wrong")
            return JSONResponse({"error": "forbidden"}, status_code=401)

        try:
            body = await request.json()
        except Exception:            # noqa: BLE001
            return JSONResponse({"error": "body is not JSON"}, status_code=400)
        if not isinstance(body, dict):
            return JSONResponse({"error": "body is not an object"}, status_code=400)

        kind = body.get("object_kind") or request.headers.get("x-gitlab-event", "")
        if kind and str(kind).lower() not in ("push", "push hook"):
            # A tag push, an issue, a pipeline: not this endpoint's business.
            # Answered 200 so GitLab does not retry or disable the webhook.
            return JSONResponse({"status": "ignored", "event": str(kind)})

        project = body.get("project") or {}
        repo = ((project.get("path_with_namespace") if isinstance(project, dict)
                 else "") or "").strip()
        if not repo:
            # A push event with no project is a malformed delivery, not a
            # repository we failed to find -- say so rather than indexing
            # everything on a guess.
            return JSONResponse({"error": "no project.path_with_namespace"},
                                status_code=400)

        # The deletion flag: a branch deletion is a push event, and there is
        # nothing to index for a ref that no longer exists. The pass would
        # otherwise spend its time reconciling a repository that did not change.
        if body.get("after") and set(str(body["after"])) == {"0"}:
            return JSONResponse({"status": "ignored", "reason": "ref deleted",
                                 "repo": repo})

        decision = _enqueue_webhook(repo, cfg_path)
        return JSONResponse(decision, status_code=202)


def _register_admin_routes(server, cfg) -> None:
    cfg_path = _cfg_path(cfg)

    def _authorised(request: Request) -> bool:
        # compare_digest, not ==: a plain comparison leaks the shared secret
        # one byte at a time to anyone who can time the response.
        #
        # Two transports for the same secret. `x-argus-admin-token` is what the
        # admin panel sends. `Authorization: Bearer` is accepted as well because
        # Prometheus can only send a bearer credential from a file
        # (`authorization.credentials_file`) -- it has no way to set an
        # arbitrary header in a scrape config, and the metrics endpoint names
        # every repository in the estate, so leaving it unauthenticated to keep
        # the header count down would be the wrong trade.
        supplied = request.headers.get("x-argus-admin-token", "")
        if not supplied:
            bearer = _extract_bearer(request.headers.get("authorization"))
            supplied = bearer or ""
        return bool(supplied) and hmac.compare_digest(supplied, _admin_token())

    @server.custom_route(ADMIN_PREFIX + "index", methods=["POST"])
    async def admin_index(request: Request) -> Response:
        if not _authorised(request):
            return JSONResponse({"error": "forbidden"}, status_code=403)
        try:
            body = await request.json()
        except Exception:            # noqa: BLE001
            body = {}
        branches = [b for b in (body.get("branches") or []) if isinstance(b, str) and b.strip()]
        allow_partial = bool(body.get("allow_partial"))
        if not _start_index(cfg_path, branches, allow_partial, trigger="manual"):
            with _index_lock:
                started = _index_job["started"]
            return JSONResponse({"error": "an index run is already in progress",
                                 "started": started}, status_code=409)
        return JSONResponse({"status": "started", "branches": branches,
                             "allow_partial": allow_partial})

    @server.custom_route(ADMIN_PREFIX + "metrics", methods=["GET"])
    async def admin_metrics(request: Request) -> Response:
        """The index, in Prometheus's text format.

        Under the admin prefix, so it carries the admin credential: this names
        every repository in the estate along with how big each one is, and an
        open endpoint for that would be a map of the organisation handed to
        anything that can reach the port.
        """
        if not _authorised(request):
            return JSONResponse({"error": "forbidden"}, status_code=403)
        try:
            body = metrics.render(cfg.index.db_path)
        except Exception as exc:            # noqa: BLE001
            # A scrape must not 500: Prometheus reads a failed scrape as the
            # target being down, which is a different and misleading incident.
            # `argus_index_scrape_ok 0` is what the ArgusIndexUnreadable rule
            # watches instead.
            body = metrics.render_error(exc)
        return Response(body, media_type="text/plain; version=0.0.4; charset=utf-8")

    @server.custom_route(ADMIN_PREFIX + "explore", methods=["GET"])
    async def admin_explore(request: Request) -> Response:
        """Browse what the index actually holds. Operator surface, read-only.

        Answers the question an operator asks when a tool returns nothing and
        they cannot tell why: is the symbol absent, named differently, private,
        or never indexed at all? `find_symbol` answers for one caller and one
        exact name; this answers for the estate and a fragment.

        NOT access-filtered, deliberately and by design: the admin token is the
        estate-wide operator credential and its holder can already see
        everything the index contains -- `/admin/metrics` names every repository
        too. The queries live in `store/explore.py` rather than `store/queries.py`
        precisely so they cannot be reached from a tool path, where the missing
        allowlist would be a hole rather than a decision.
        """
        if not _authorised(request):
            return JSONResponse({"error": "forbidden"}, status_code=403)
        q = (request.query_params.get("q") or "").strip()
        repo = (request.query_params.get("repo") or "").strip()
        try:
            limit = int(request.query_params.get("limit") or explore.DEFAULT_LIMIT)
        except ValueError:
            limit = explore.DEFAULT_LIMIT
        try:
            conn = connect_readonly(cfg.index.db_path)
            try:
                # NOT `row_factory = None`. The status route below clears it
                # because it reads its rows positionally; these queries build
                # dicts from them, and clearing it makes every row a plain tuple,
                # which raises "cannot convert dictionary update sequence" and
                # produced an empty page indistinguishable from an empty index.
                # `connect_readonly` sets sqlite3.Row for exactly this reason.
                out = {
                    "repos": explore.repos(conn),
                    "symbols": explore.symbols(conn, pattern=q, repo=repo,
                                               limit=limit),
                    "files": explore.files(conn, pattern=q, repo=repo,
                                           limit=limit),
                    "query": q, "repo": repo,
                }
            finally:
                conn.close()
        except Exception as exc:     # noqa: BLE001
            # A 200 with the error in it, like the metrics route: the operator
            # asked a question and the answer is "the index cannot be read",
            # which is not the same as the request being wrong.
            #
            # The console MUST render this. An error field nobody displays, next
            # to empty lists, is indistinguishable from an empty index -- which
            # is exactly how the bug above was found, by an operator concluding
            # the index was empty while it held seventy symbols.
            return JSONResponse({"error": f"{type(exc).__name__}: {exc}"[:200],
                                 "repos": [], "symbols": {"rows": [], "capped": False},
                                 "files": {"rows": [], "capped": False}})
        return JSONResponse(out)

    @server.custom_route(ADMIN_PREFIX + "index/status", methods=["GET"])
    async def admin_index_status(request: Request) -> Response:
        if not _authorised(request):
            return JSONResponse({"error": "forbidden"}, status_code=403)
        with _index_lock:
            job = dict(_index_job)
        rows = []
        try:
            conn = connect_readonly(cfg.index.db_path)
            try:
                conn.row_factory = None
                cur = conn.execute(
                    "SELECT path_with_namespace, branch, default_branch,"
                    "       last_run_at, last_run_timed_out, last_run_symbols_failed"
                    "  FROM repos ORDER BY last_run_at DESC NULLS LAST, path_with_namespace")
                for r in cur.fetchall():
                    rows.append({"repo": r[0], "branch": r[1], "default_branch": r[2],
                                 "last_run_at": r[3], "timed_out": bool(r[4]),
                                 "symbols_failed": r[5]})
            finally:
                conn.close()
        except Exception as exc:     # noqa: BLE001
            job["repos_error"] = repr(exc)[:200]

        # The same snapshot the Prometheus exposition renders, so the number on
        # the admin console and the number the ArgusIndexStale rule pages on
        # cannot disagree. The panel deliberately does NOT decide staleness for
        # itself: two answers to "is this index current?" is one too many, and
        # the one on screen is the one people believe.
        try:
            snap = metrics.snapshot(cfg.index.db_path)
            summary: dict = {
                "repos": len(snap["repos"]),
                "stale": snap["stale_repos"],
                "errored": snap["errored_repos"],
                "stale_after": snap["stale_after"],
                "version": snap["version"],
                "never_run": sum(1 for r in snap["repos"] if not r["last_run_at"]),
                "files": sum(r["files"] for r in snap["repos"]),
                "symbols": sum(r["symbols"] for r in snap["repos"]),
                # Named so the console can say WHICH repository, not just how
                # many. Capped: this is a banner, not the indexing table.
                "stale_names": [f'{r["repo"]}@{r["branch"]}'
                                for r in snap["repos"] if r["stale"]][:8],
            }
        except Exception as exc:     # noqa: BLE001
            summary = {"error": f"{type(exc).__name__}: {exc}"[:200]}
        # The cadence comes from the process that actually runs the schedule,
        # not from the console's own environment: the console has no way to
        # know it otherwise, and a settings page reading a stale copy of a
        # value is worse than one that does not show it.
        #
        # `webhook` for the same reason. The console cannot see whether Argus
        # has a webhook secret, and "reindexes every 15 minutes" reads very
        # differently depending on whether pushes also arrive immediately.
        return JSONResponse({"job": job, "repos": rows, "index": summary,
                             "interval": index_interval(),
                             "webhook": _webhook_enabled(),
                             "pending": list(job.get("pending") or [])})
