"""Put the chat caller's identity where LiteLLM will enforce a budget on it.

Open WebUI authenticates to the gateway with ONE shared key and names the
signed-in person only in a header. Measured against LiteLLM on this stack:

    X-OpenWebUI-User-Email header  ->  attributes spend to that person
    "user" field in the JSON body  ->  ALSO enforces their end-user budget

The header alone does not enforce. An over-budget person was refused through
their own API key and served normally through chat, because an internal-user
budget is checked against a key that user owns and the web UI does not use
one. Mapping the header to `customer` instead does not enforce either, and
mapping it to both roles at once breaks attribution as well.

So this sits between Open WebUI and LiteLLM and does exactly one thing: copy
the identity out of the header and into the body as `user`. The header is
passed through untouched, so attribution keeps working the way it already
did; the body field is what makes the ceiling bind.

Deliberately narrow. It does not mint keys, cache identities, or decide
anything -- a component in the request path of every chat message should be
as close to a pipe as it can be while still doing its job.
"""

from __future__ import annotations

import json
import os

import httpx
from starlette.applications import Starlette
from starlette.background import BackgroundTask
from starlette.requests import Request
from starlette.responses import JSONResponse, Response, StreamingResponse
from starlette.routing import Route

UPSTREAM = os.environ.get("UPSTREAM_URL", "http://litellm:4000").rstrip("/")
HEADER = os.environ.get("IDENTITY_HEADER", "X-OpenWebUI-User-Email")

#: Hop-by-hop headers, plus the two that describe a body this proxy may
#: rewrite. Forwarding a stale Content-Length truncates the request; letting
#: httpx set both is the only correct option once the body can change size.
_DROP = {"host", "content-length", "transfer-encoding", "connection",
         "keep-alive", "upgrade"}

client = httpx.AsyncClient(timeout=httpx.Timeout(900.0, connect=15.0))


def _identity(request: Request) -> str:
    return (request.headers.get(HEADER) or "").strip().lower()


def _with_user(body: bytes, who: str) -> bytes:
    """Add `user` to a JSON body, leaving anything else untouched.

    An existing `user` is NOT overwritten: a caller that named itself meant
    it, and silently replacing that would misattribute a request that was
    already correct.
    """
    if not who or not body:
        return body
    try:
        payload = json.loads(body)
    except (ValueError, UnicodeDecodeError):
        return body          # not JSON: a file upload, or something we do not model
    if not isinstance(payload, dict) or payload.get("user"):
        return body
    payload["user"] = who
    return json.dumps(payload).encode()


async def proxy(request: Request) -> Response:
    body = await request.body()
    who = _identity(request)
    path = request.url.path
    # Only request bodies that carry a caller are rewritten. /models and the
    # health endpoints have no user to name and must pass through verbatim.
    if request.method == "POST":
        body = _with_user(body, who)

    headers = {k: v for k, v in request.headers.items() if k.lower() not in _DROP}
    upstream = httpx.Request(
        request.method, f"{UPSTREAM}{path}",
        params=request.query_params, headers=headers, content=body,
    )
    try:
        response = await client.send(upstream, stream=True)
    except httpx.HTTPError as exc:
        # A gateway that is down must not look like a model that refused.
        return JSONResponse(
            {"error": {"message": f"identity-proxy could not reach the gateway: "
                                  f"{type(exc).__name__}",
                       "type": "upstream_unavailable"}},
            status_code=502,
        )

    # Streamed, not buffered: chat responses are server-sent events and
    # collecting them first would make every reply arrive at once, after the
    # whole generation finished.
    passthrough = {k: v for k, v in response.headers.items()
                   if k.lower() not in _DROP}
    return StreamingResponse(
        response.aiter_raw(), status_code=response.status_code,
        headers=passthrough,
        background=BackgroundTask(response.aclose),
    )


async def healthz(_request: Request) -> Response:
    return JSONResponse({"status": "ok", "upstream": UPSTREAM, "header": HEADER})


app = Starlette(routes=[
    Route("/healthz", healthz, methods=["GET"]),
    Route("/{path:path}", proxy,
          methods=["GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS"]),
])
