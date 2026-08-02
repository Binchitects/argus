from __future__ import annotations

from starlette.responses import JSONResponse


def unauthorized(message: str) -> JSONResponse:
    """Build the 401 returned for any authentication failure.

    The body carries the raw message as `error` -- for an AclDenied this is
    text written to be read and acted on by an agent (e.g. "refresh your
    token and re-run `hermes mcp add ...`"), so it must reach the caller
    unmodified rather than behind a generic "unauthorized" string.
    """
    return JSONResponse(
        {"error": message},
        status_code=401,
        headers={"WWW-Authenticate": "Bearer"},
    )
