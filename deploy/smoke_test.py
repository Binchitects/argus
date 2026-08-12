"""Deployment acceptance test -- does this Argus actually serve agents?

Run against a deployment BEFORE pointing developers at it. Every check is a
property an operator would otherwise discover through a confused agent:

    healthz          the process is up
    auth denies      an invalid token is refused (the security boundary)
    handshake        MCP initialize succeeds over streamable HTTP
    instructions     the server sends its usage guidance (measured: passing
                     this through took tool use from 3/20 to 8/20)
    tools            every expected tool is registered, by name
    packs            documentation is installed and answers
    private code     the index answers, scoped to the caller
    latency          each call is timed, so a slow deployment is visible

Exit code is 0 only when every REQUIRED check passes, so this drops into CI
or a post-deploy gate without parsing output.

    python deploy/smoke_test.py --url http://127.0.0.1:8099/mcp --token <PAT>

`--json` prints a machine-readable report for a dashboard.
"""
from __future__ import annotations

import argparse
import asyncio
import json
import sys
import time
import urllib.error
import urllib.request

#: Tools an agent-facing deployment must expose. Named individually rather
#: than counted: "15 tools" stays green when the wrong 15 are registered, and
#: a missing docs_* tool is exactly the failure that makes an agent invent an
#: IRQL instead of looking one up.
REQUIRED_TOOLS = [
    "find_symbol", "find_references", "search_code", "get_file",
    "index_status", "which_repo", "repo_map", "impact_of",
    "docs_lookup", "docs_search", "docs_get", "docs_find",
    "docs_verify", "docs_contracts", "code_contracts",
]

#: A fact with a known answer, used to prove the packs are not merely present
#: but answering. Chosen because memory reliably gets it wrong.
PACK_PROBE = ("FltRegisterFilter", "APC_LEVEL")


class Report:
    def __init__(self) -> None:
        self.rows: list[dict] = []
        self.failed = 0

    def add(self, name: str, ok: bool, detail: str = "",
            ms: float | None = None, required: bool = True) -> None:
        self.rows.append({"check": name, "ok": ok, "detail": detail,
                          "ms": round(ms, 1) if ms is not None else None,
                          "required": required})
        if required and not ok:
            self.failed += 1

    def print_human(self) -> None:
        print()
        for row in self.rows:
            mark = "PASS" if row["ok"] else ("FAIL" if row["required"] else "WARN")
            timing = f"{row['ms']:>8.1f} ms" if row["ms"] is not None else " " * 11
            print(f"  [{mark}] {row['check']:<26} {timing}  {row['detail']}")
        total = len(self.rows)
        ok = sum(1 for r in self.rows if r["ok"])
        print(f"\n  {ok}/{total} checks passed"
              + (f", {self.failed} REQUIRED failure(s)" if self.failed else ""))


def _http_status(url: str, timeout: float = 10.0) -> tuple[int, float]:
    started = time.perf_counter()
    try:
        with urllib.request.urlopen(url, timeout=timeout) as resp:
            return resp.status, (time.perf_counter() - started) * 1000
    except urllib.error.HTTPError as exc:
        return exc.code, (time.perf_counter() - started) * 1000
    except Exception:
        return 0, (time.perf_counter() - started) * 1000


async def _session(url: str, token: str):
    from mcp import ClientSession
    from mcp.client.streamable_http import streamablehttp_client

    ctx = streamablehttp_client(url, headers={"Authorization": f"Bearer {token}"})
    return ctx, ClientSession


async def run(url: str, token: str, report: Report) -> None:
    base = url.rsplit("/mcp", 1)[0]

    status, ms = _http_status(f"{base}/healthz")
    reachable = status == 200
    report.add("healthz", reachable,
               f"HTTP {status}" if status else "unreachable", ms)

    if not reachable:
        # Every check below needs a live server, and running them anyway
        # produces confident nonsense: the bad-token probe cannot tell a
        # DENIAL from a CONNECTION FAILURE, so an unreachable server reported
        # "ACCEPTED A BAD TOKEN" -- a fabricated security alarm. Measured on
        # a stopped server during development of this script.
        report.add("auth rejects bad token", False,
                   "not checked -- server unreachable")
        report.add("mcp handshake", False, "not checked -- server unreachable")
        return

    # The security boundary, checked as a boundary: a deployment that answers
    # a garbage token is misconfigured no matter what else works. Asserted
    # before any successful call, so a pass here cannot come from a cache
    # warmed by this very run.
    from mcp import ClientSession
    from mcp.client.streamable_http import streamablehttp_client

    started = time.perf_counter()
    denied = False
    try:
        async with streamablehttp_client(
                url, headers={"Authorization": "Bearer not-a-real-token"}
        ) as (r, w, _):
            async with ClientSession(r, w) as session:
                await session.initialize()
        # No exception at all: the server completed a handshake for a token
        # it has never seen. That is the one unambiguous failure here.
        detail = "ACCEPTED A BAD TOKEN"
    except BaseException as exc:
        text = " ".join(str(e) for e in getattr(exc, "exceptions", [exc]))
        denied = "401" in text or "403" in text or "Unauthorized" in text
        # A refusal and a network failure both raise. Only the former proves
        # anything about the security boundary, so an ambiguous error is
        # reported as ambiguous rather than scored as a pass.
        detail = "denied" if denied else f"INCONCLUSIVE: {text[:60]}"
    report.add("auth rejects bad token", denied, detail,
               (time.perf_counter() - started) * 1000)

    started = time.perf_counter()
    try:
        async with streamablehttp_client(
                url, headers={"Authorization": f"Bearer {token}"}
        ) as (r, w, _):
            async with ClientSession(r, w) as session:
                init = await session.initialize()
                handshake_ms = (time.perf_counter() - started) * 1000
                report.add("mcp handshake", True,
                           f"protocol {init.protocolVersion}", handshake_ms)

                instructions = init.instructions or ""
                report.add("server instructions", len(instructions) > 200,
                           f"{len(instructions)} chars")

                started = time.perf_counter()
                listed = await session.list_tools()
                names = {t.name for t in listed.tools}
                missing = [t for t in REQUIRED_TOOLS if t not in names]
                report.add("tools registered", not missing,
                           f"{len(names)} tools"
                           + (f", MISSING {missing}" if missing else ""),
                           (time.perf_counter() - started) * 1000)

                symbol, expected = PACK_PROBE
                started = time.perf_counter()
                try:
                    res = await session.call_tool("docs_lookup", {"name": symbol})
                    text = "".join(getattr(c, "text", "") for c in (res.content or []))
                    ok = expected.lower() in text.lower()
                    detail = (f"{symbol} -> {expected}" if ok
                              else f"{symbol} did not return {expected}")
                except Exception as exc:
                    ok, detail = False, f"{type(exc).__name__}: {exc}"[:80]
                report.add("packs answer", ok, detail,
                           (time.perf_counter() - started) * 1000)

                # Warm-up, deliberately untimed. The FIRST tool call on a
                # connection pays costs no later call does -- opening the
                # index, the first audit write, cold OS caches -- and
                # measured here that was 10.7s against a 79ms steady-state
                # median. Reporting the cold number as "how fast is Argus"
                # would be wrong by two orders of magnitude, and reporting it
                # without saying so would send an operator profiling a
                # non-problem.
                try:
                    await session.call_tool("index_status", {})
                except Exception:
                    pass

                started = time.perf_counter()
                try:
                    res = await session.call_tool("index_status", {})
                    text = "".join(getattr(c, "text", "") for c in (res.content or []))
                    repos = text.count("path_with_namespace")
                    # Not required: a fresh deployment legitimately has an
                    # empty index, and failing the gate for that would teach
                    # operators to ignore this script.
                    report.add("private index", repos > 0,
                               f"{repos} repo(s) visible to this token",
                               (time.perf_counter() - started) * 1000,
                               required=False)
                except Exception as exc:
                    report.add("private index", False,
                               f"{type(exc).__name__}: {exc}"[:80],
                               required=False)
    except BaseException as exc:
        text = " ".join(str(e) for e in getattr(exc, "exceptions", [exc]))
        report.add("mcp handshake", False, text[:100],
                   (time.perf_counter() - started) * 1000)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--url", default="http://127.0.0.1:8099/mcp")
    parser.add_argument("--token", required=True,
                        help="A developer GitLab PAT the deployment accepts")
    parser.add_argument("--json", action="store_true")
    args = parser.parse_args()

    report = Report()
    print(f"Argus deployment acceptance -- {args.url}")
    asyncio.run(run(args.url, args.token, report))

    if args.json:
        print(json.dumps(report.rows, indent=2))
    else:
        report.print_human()
    return 1 if report.failed else 0


if __name__ == "__main__":
    sys.exit(main())
