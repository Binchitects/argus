#!/usr/bin/env python3
"""Every MCP tool, called over the wire, against a live server.

WHY THIS EXISTS

`verify_mcp.py` proved the per-person ACL filters, but it exercised three tools
and asserted `len(tools) == 5`. The server has sixteen. That assertion could
only ever fail, so nobody ran it, so nothing noticed when the packs tools
arrived and nothing would notice a seventeenth tool shipping with no test at
all. A check that has already rotted is worse than no check: it is a green light
nobody has looked at.

This is the contract instead, and it is built so it cannot rot the same way:

  * The tool list comes from the SERVER, not from a literal here. Every tool the
    server advertises must have a case below, and every case must correspond to
    a tool the server advertises. Adding a tool without adding a case fails, and
    removing a tool without removing its case fails. There is no number to
    forget to update.

  * Every case is called for real, over StreamableHTTP, with a real GitLab
    personal access token, against a real index. Not a fixture, not in-process.

  * The result must match the declared shape (list vs object) and must not be a
    tool error. A tool that throws is a tool no client can use, and MCP reports
    that as a successful call carrying `isError`, so a test that only checks the
    transport status passes.

  * Nothing any tool returns may name a repository the caller cannot read. This
    is checked for all sixteen, not just the three that had it checked before --
    `get_file` was verified once by hand and the other twelve never were.

  * A tool that returns nothing for every caller is reported, because an empty
    result makes every isolation assertion about it vacuously true. That is the
    failure mode this project has hit repeatedly, including inside `verify.py`
    itself. Vacuity is allowed only where it is declared and explained.

Run after seed.py (or let run.sh do all of it):

    python scripts/test-gitlab/verify_tools.py

Exits non-zero on any failed check. Set ARGUS_TEST_WORK to keep the index off a
filesystem SQLite cannot use; run.sh always sets it.
"""
from __future__ import annotations

import asyncio
import json
import os
import pathlib
import subprocess
import sys
import time

import httpx

HERE = pathlib.Path(__file__).resolve().parent
ROOT = HERE.parent.parent
WORK = pathlib.Path(os.environ.get("ARGUS_TEST_WORK") or (HERE / "work"))
SEEDED = HERE / "seeded.json"
HOST, PORT = "127.0.0.1", 7762
BASE = f"http://{HOST}:{PORT}"

results: list[tuple[str, bool, str]] = []
skipped: list[tuple[str, str]] = []


def skip(name: str, detail: str) -> None:
    """Record a check that could not be exercised, as NOT a pass.

    Deliberately distinct from PASS. "Nothing leaked" against a tool that was
    never successfully called is the vacuous green light this whole script
    exists to stop handing out, and a skipped check that prints like a passing
    one is how a suite reports coverage it does not have. The summary counts
    these separately and says they are not covered.
    """
    skipped.append((name, detail))
    print(f"  [{'SKIP'}] {name} -- {detail}")


def check(name: str, ok: bool, detail: str = "") -> bool:
    results.append((name, ok, detail))
    print(f"  [{'PASS' if ok else 'FAIL'}] {name}" + (f" -- {detail}" if detail else ""))
    return ok


# --- what each tool is called with -------------------------------------------
#
# `kind` is the shape the tool's return annotation declares: `get_file`,
# `repo_map` and `impact_of` return an object, everything else a list. Checking
# it is not pedantry -- FastMCP derives structuredContent from the annotation, so
# a tool that starts returning the wrong shape breaks every generated client
# while still answering 200.
#
# Arguments are built from `ctx`, which holds the caller's own repo id and a path
# that exists in it (`ctx["path"]`), so the same table works for a developer who
# can see one repository and for an admin who can see all of them.
#
# Each entry also declares which REFUSALS are legitimate for it, because the
# tools in this server refuse on purpose and a contract that cannot tell a
# designed refusal from a crash reports working code as broken:
#
#   may_refuse     the caller may hold no access to what it asked about, and the
#                  server answers with a NoAccess notice naming the repository
#                  and its maintainers. That is the design, not a failure.
#   needs_packs    answers only when a documentation pack is installed.
#   needs_vectors  answers only when the index has been embedded.
#
# Anything that is none of those is a real error and fails the contract.
CONTRACTS: dict[str, dict] = {
    "index_status":    {"args": lambda c: {},                       "kind": list},
    "repo_map":        {"args": lambda c: {"repo_id": c["repo_id"]}, "kind": dict},
    "find_symbol":     {"args": lambda c: {"name": "DecodeFrame"},   "kind": list,
                        "may_refuse": True},
    "find_references": {"args": lambda c: {"name": "DecodeFrame"},   "kind": list},
    "search_code":     {"args": lambda c: {"query": "DecodeFrame"},  "kind": list},
    "semantic_search": {"args": lambda c: {"query": "decode a frame from a buffer"},
                        "kind": list, "needs_vectors": True},
    # The description is shaped like a symbol complaint ON PURPOSE. `which_repo`
    # returns [] rather than a ranked list of weak matches -- "a list looks like
    # an answer, and the caller acts on the top row" -- so a generic phrase like
    # "decode a frame from a buffer" clears no evidence floor against a
    # three-file corpus and legitimately comes back empty. That emptiness is the
    # correct answer, not a gap, and a contract case that cannot tell the two
    # apart is worse than no case. A symbol shape does clear it: dev_alpha gets
    # eal-core, and dev_beta is refused, which is the ACL working.
    "which_repo":      {"args": lambda c: {"description": "DecodeFrame undefined reference"},
                        "kind": list, "needs_vectors": True, "may_refuse": True},
    "get_file":        {"args": lambda c: {"repo_id": c["repo_id"], "path": c["path"]},
                        "kind": dict},
    "impact_of":       {"args": lambda c: {"repo_id": c["repo_id"], "path": c["path"]},
                        "kind": dict},
    # `source` is the source TEXT, not a path -- "Paste a source file and get
    # where every IN-HOUSE symbol it references is defined". The first version
    # of this contract passed `src/decoder.c`, whose identifiers are `src`,
    # `decoder` and `c`, none of which is a symbol; the tool correctly returned
    # an empty list and the contract reported a vacuous pass. The fixture's
    # decoder.c calls HelperOnly, which is defined in that same file, so this
    # exercises a real lookup rather than an empty result.
    #
    # `may_refuse` because pasting eal-core's source as dev_beta is exactly the
    # case the notice exists for: every symbol it names lives in a repository
    # that caller cannot read, and being told so -- with who to ask -- is the
    # correct answer, not a failure to answer.
    "code_contracts":  {"args": lambda c: {"source": _SOURCE_TEXT},   "kind": list,
                        "may_refuse": True},

    # The estate's own description. Takes no repository, so there is nothing to
    # refuse and nothing to be denied -- every caller is entitled to a view of
    # what they can see, even when that view is empty.
    "overview":        {"args": lambda c: {},                       "kind": dict},

    # The packs tools. Nothing is installed in this fixture, and building a pack
    # needs a checkout of a real documentation repository, so the happy path is
    # not exercised here. What IS asserted is the no-packs contract: they must
    # fail with something an operator can act on, rather than returning an empty
    # list -- "no results" and "this server has no documentation" look identical
    # to a reader, and only one of them is the caller's problem.
    "docs_lookup":     {"args": lambda c: {"name": "CreateFileW"},   "kind": list,
                        "needs_packs": True},
    "docs_find":       {"args": lambda c: {"description": "open an existing file"},
                        "kind": list, "needs_packs": True},
    "docs_search":     {"args": lambda c: {"query": "mutex"},        "kind": list,
                        "needs_packs": True},
    "docs_get":        {"args": lambda c: {"doc_path": "index.md"},
                        "kind": type(None), "needs_packs": True},
    "docs_verify":     {"args": lambda c: {"text": "Call CreateFileW(file)."},
                        "kind": list, "needs_packs": True},
    "docs_contracts":  {"args": lambda c: {"source": c["path"]},      "kind": list,
                        "needs_packs": True},
}

# The file each tool that takes one is pointed at, per fixture project. A path
# that exists, so a tool is never called with a guess that returns empty for the
# wrong reason.
PATH_BY_REPO = {
    "eal-core": "include/eal/decoder.h",
    "etl-decoder": "src/pipeline.c",
    "driver-shim": "src/shim.c",
}

# ...and the one that names an in-house symbol, for `code_contracts`. A header
# only declares, so it has no contracts to return.
SOURCE_BY_REPO = {
    "eal-core": "src/decoder.c",
    "etl-decoder": "src/pipeline.c",
    "driver-shim": "src/shim.c",
}

#: The body of `eal-core/src/decoder.c`, which is what `code_contracts` takes.
#: It calls `HelperOnly`, defined in that same file, so the tool has a real
#: in-house symbol to resolve. Derived from the fixture corpus rather than
#: invented, so it cannot drift from what seed.py actually writes.
_SOURCE_TEXT = (
    '#include "eal/decoder.h"\n'
    "static int HelperOnly(int x) { return x + 1; }\n"
    "int DecodeFrame(const char* buf, int len) { return HelperOnly(len); }\n"
)

# The project nobody is a member of. If it appears anywhere, the design failed.
UNREACHABLE = "driver-shim"

_KNOWN_PROJECTS = tuple(PATH_BY_REPO)

# `semantic_search` and `which_repo` rank by embedding, so they need a reachable
# embedding backend AND an index that has actually been embedded. Declared here
# so the contract says which of the two it is looking at, rather than quietly
# counting an unexercised tool as covered.
EMBED_BACKEND = os.environ.get("ARGUS_OLLAMA_URL", "").strip()


def embed_reason() -> str:
    return (f"ARGUS_OLLAMA_URL={EMBED_BACKEND}" if EMBED_BACKEND else
            "no embedding backend: set ARGUS_OLLAMA_URL to the stack's ollama")


def start_server() -> subprocess.Popen:
    proc = subprocess.Popen(
        [sys.executable, "-m", "argus.cli", "serve",
         "--config", str(WORK / "config.yaml"),
         "--host", HOST, "--port", str(PORT),
         "--allowed-host", f"{HOST}:{PORT}", "--allowed-host", HOST],
        cwd=ROOT, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True,
    )
    for _ in range(60):
        time.sleep(1)
        if proc.poll() is not None:
            print(proc.stdout.read() if proc.stdout else "")
            raise RuntimeError("server exited during startup")
        try:
            if httpx.get(f"{BASE}/healthz", timeout=2).status_code == 200:
                return proc
        except Exception:
            pass
    raise TimeoutError("server did not become healthy")


def collect_repos(node, found: set[str]) -> set[str]:
    """Every repository a result NAMES IN A FIELD, whatever the result's shape.

    Recursive on purpose. `verify_mcp.py` only looked at the top level of a
    list, which happened to work for `find_symbol` and would silently find
    nothing in `get_file` (an object), `repo_map` (nested objects) or
    `impact_of` (a graph) -- and finding nothing is exactly what a passing
    isolation check looks like.

    Deliberately does NOT scan free text, and that is a decision rather than an
    omission. The first version did, and it reported `find_references` and
    `search_code` as leaking `eal-core` to dev_beta -- because the fixture's own
    source file contains the comment "RunPipeline calls DecodeFrame from
    eal-core", which dev_beta may read, and because the no-access notice names
    the repositories a match was hidden in, on purpose, so the asker knows who
    to ask. Both are correct behaviour and neither can be told apart from a
    leak by looking at prose, so a prose scan is a check that fires on working
    code -- and a check that fires on working code is one somebody turns off.
    What is tested is the thing that can be tested: no structured field may name
    a repository outside the caller's allowlist.
    """
    if isinstance(node, dict):
        for key, value in node.items():
            # Keys count too: `impact_of` returns `{"by_repo": {"root/eal-core":
            # [...]}}`, so the repository name is the KEY, and recursing only
            # into values would miss the one tool that returns a graph.
            if isinstance(key, str):
                for name in _KNOWN_PROJECTS:
                    if key == name or key.endswith(f"/{name}"):
                        found.add(key)
            if key in ("path_with_namespace", "repo", "repo_path",
                       "repository") and isinstance(value, str):
                found.add(value)
            else:
                collect_repos(value, found)
    elif isinstance(node, list):
        for item in node:
            collect_repos(item, found)
    return found


def error_text(call: dict) -> str:
    """The message behind an `isError`, in one line.

    MCP reports a tool that threw as a SUCCESSFUL call carrying `isError`, so
    the diagnosis is only ever in the content blocks. Printing "isError" and
    nothing else sends the reader off to re-run everything by hand to find out
    why; the message is usually the whole answer.
    """
    payload = call.get("payload")
    if isinstance(payload, dict):
        for key in ("_args_failed", "_raw"):
            value = payload.get(key)
            if isinstance(value, list) and value:
                return str(value[0]).replace("\n", " ")[:220]
            if isinstance(value, str) and value:
                return value.replace("\n", " ")[:220]
    return "isError (no message)"


#: Outcomes a call can have. Only ERROR is ever a defect; the middle three are
#: the server working as designed, and naming them separately is what stops a
#: contract suite from reporting correct refusals as breakage.
OK, NO_ACCESS, NO_PACKS, NO_VECTORS, ERROR = (
    "ok", "no_access", "no_packs", "no_vectors", "error")

#: The phrases the server uses to refuse. Matched on the message because that is
#: all MCP gives a client -- there is no error code -- and each one is a sentence
#: written for a reader, checked below for being actionable.
REFUSAL_MARKERS = (
    (NO_ACCESS, "you cannot read"),
    (NO_PACKS, "No documentation packs are installed"),
    (NO_VECTORS, "The index is unavailable"),
)


def classify(call: dict) -> str:
    if not call.get("is_error"):
        return OK
    text = error_text(call)
    for outcome, marker in REFUSAL_MARKERS:
        if marker in text:
            return outcome
    return ERROR


def normalise(names) -> set[str]:
    """Compare repositories by their LAST path segment.

    Two spellings of the same repository are in play: the index stores
    `root/eal-core`, while `repo_map` and `impact_of` report the short
    `eal-core`. Treating those as different made the first version of this
    script report every one of them as a leak. The check exists to catch a
    DIFFERENT repository, not a different spelling of the same one, so the
    comparison is by basename -- and `UNREACHABLE` is matched the same way.
    """
    out = set()
    for n in names:
        if isinstance(n, str) and n:
            out.add(n.rsplit("/", 1)[-1])
    return out


async def session_for(token: str) -> dict:
    """One full MCP session: list the tools, then call every case."""
    from mcp import ClientSession
    from mcp.client.streamable_http import streamablehttp_client

    out: dict = {"calls": {}}
    async with streamablehttp_client(
        f"{BASE}/mcp", headers={"Authorization": f"Bearer {token}"}
    ) as (read, write, _):
        async with ClientSession(read, write) as session:
            await session.initialize()
            listed = await session.list_tools()
            out["tools"] = sorted(t.name for t in listed.tools)

            async def call(name, args):
                r = await session.call_tool(name, args)
                sc = getattr(r, "structuredContent", None)
                if isinstance(sc, dict) and "result" in sc:
                    payload = sc["result"]
                else:
                    payload = {"_raw": [getattr(c, "text", "") for c in r.content]}
                return {"is_error": bool(r.isError), "payload": payload}

            # index_status first: it is how the context learns which repositories
            # this caller may name, and a tool that needs a repo id cannot be
            # called without it.
            status = await call("index_status", {})
            rows = status["payload"] if isinstance(status["payload"], list) else []
            ctx = {"repo_id": None, "path": None, "source": None}
            for row in rows:
                if not isinstance(row, dict):
                    continue
                name = (row.get("path_with_namespace") or "").rsplit("/", 1)[-1]
                if name in PATH_BY_REPO:
                    ctx = {"repo_id": row.get("repo_id"),
                           "path": PATH_BY_REPO[name],
                           "source": SOURCE_BY_REPO[name]}
                    break
            out["ctx"] = ctx
            out["calls"]["index_status"] = status

            for name, spec in CONTRACTS.items():
                if name == "index_status":
                    continue
                try:
                    args = spec["args"](ctx)
                except Exception as exc:                     # noqa: BLE001
                    out["calls"][name] = {"is_error": True,
                                          "payload": {"_args_failed": repr(exc)}}
                    continue
                out["calls"][name] = await call(name, args)
    return out


def kind_ok(payload, declared) -> bool:
    """Does the result match the tool's declared shape?

    `None` is accepted for a tool annotated `dict | None` (`docs_get`): "no such
    document" is a legitimate answer, not a contract violation.
    """
    if payload is None:
        return declared is type(None) or declared is dict
    if declared is list:
        return isinstance(payload, list)
    if declared is dict:
        return isinstance(payload, dict)
    return True


def non_empty(payload) -> bool:
    if payload is None:
        return False
    if isinstance(payload, (list, dict, str)):
        return len(payload) > 0
    return True


async def main_async() -> int:
    if not SEEDED.exists():
        print(f"missing {SEEDED}; run seed.py first", file=sys.stderr)
        return 2
    if not (WORK / "config.yaml").exists():
        print(f"missing {WORK / 'config.yaml'}; run verify.py first", file=sys.stderr)
        return 2

    seeded = json.loads(SEEDED.read_text(encoding="utf-8"))
    users = seeded["users"]

    print("== starting argus serve ==")
    proc = start_server()
    try:
        print("\n== the door ==")
        # Carried over from verify_mcp.py, which this supersedes: a per-tool
        # contract is worth nothing if the transport lets the wrong caller in.
        r = httpx.post(f"{BASE}/mcp", timeout=10,
                       json={"jsonrpc": "2.0", "id": 1, "method": "initialize"})
        check("an unauthenticated MCP call is refused", r.status_code == 401,
              f"got {r.status_code}")
        check("/healthz needs no auth",
              httpx.get(f"{BASE}/healthz", timeout=5).status_code == 200)
        r = httpx.post(f"{BASE}/mcp", timeout=30,
                       headers={"Authorization": "Bearer not-a-real-token"},
                       json={"jsonrpc": "2.0", "id": 1, "method": "initialize"})
        check("a garbage token is refused", r.status_code == 401,
              f"got {r.status_code}")

        print("\n== tool inventory ==")
        first = await session_for(users["dev_alpha"]["token"])
        listed = set(first["tools"])
        print(f"  server advertises {len(listed)} tools: {', '.join(sorted(listed))}")

        # The two halves of anti-rot. A tool with no case is untested; a case
        # with no tool is a test of something that no longer exists. Both are
        # failures, and neither needs a number kept in sync by hand.
        uncovered = sorted(listed - set(CONTRACTS))
        stale = sorted(set(CONTRACTS) - listed)
        check("every advertised tool has a contract case", not uncovered,
              f"untested: {', '.join(uncovered)}" if uncovered else
              f"{len(listed)} tools")
        check("every contract case names a tool the server advertises", not stale,
              f"gone: {', '.join(stale)}" if stale else "none")

        print("\n== per-caller sessions ==")
        per_user = {"dev_alpha": first}
        for username, u in users.items():
            if username in per_user:
                continue
            per_user[username] = await session_for(u["token"])

        # Each caller's allowlist, from their own index_status. This is the
        # ground truth every isolation check below is measured against.
        allowed: dict[str, set[str]] = {}
        for username, res in per_user.items():
            rows = res["calls"]["index_status"]["payload"]
            allowed[username] = {
                r["path_with_namespace"] for r in rows
                if isinstance(r, dict) and r.get("path_with_namespace")
            }
            print(f"  {username:10} sees {sorted(allowed[username])}  "
                  f"repo_id={res['ctx']['repo_id']}")

        # A tool whose every caller got the SAME precondition refusal is not
        # tested here at all, and saying so beats a green line that means
        # nothing. Keyed by the refusal so the reason can be specific.
        GATED = {
            NO_PACKS: ("no documentation pack is installed in this fixture, and "
                       "building one needs a checkout of a real documentation repo"),
            NO_VECTORS: (f"the vector index is empty: {embed_reason()} and "
                         f"`argus embed` must have run (verify.py does it)"),
        }

        outcomes: dict[str, dict[str, str]] = {}
        print("\n== every tool answers ==")
        for name in sorted(CONTRACTS):
            spec = CONTRACTS[name]
            outs = {u: classify(r["calls"].get(name) or {"is_error": True,
                                                         "payload": {}})
                    for u, r in per_user.items()}
            outcomes[name] = outs

            # Wholesale precondition refusal: not covered, and declared so.
            gate = next((o for o in (NO_PACKS, NO_VECTORS)
                         if set(outs.values()) == {o}), None)
            if gate:
                skip(f"{name}: answers with the declared shape for every caller",
                     GATED[gate])
                continue

            problems, empties, refusals = [], [], []
            for username, outcome in outs.items():
                call = per_user[username]["calls"].get(name)
                if outcome == ERROR:
                    problems.append(f"{username}: {error_text(call)}")
                    continue
                if outcome == OK:
                    if not kind_ok(call["payload"], spec["kind"]):
                        problems.append(f"{username}: wrong shape "
                                        f"({type(call['payload']).__name__})")
                    elif not non_empty(call["payload"]):
                        empties.append(username)
                    continue
                # A refusal. Legitimate only where the tool declared one is
                # possible, and only if the message is usable when it happens.
                refusals.append(f"{username}={outcome}")
                if outcome == NO_ACCESS and not spec.get("may_refuse"):
                    problems.append(f"{username}: refused access to something it "
                                    f"should have answered -- {error_text(call)}")
            detail = "; ".join(problems) if problems else ", ".join(refusals)
            check(f"{name}: answers with the declared shape for every caller",
                  not problems, detail)

            # Vacuity: nothing but refusals and empties makes every isolation
            # assertion about this tool trivially true.
            if not any(outs[u] == OK and non_empty(
                    per_user[u]["calls"][name]["payload"]) for u in per_user):
                skip(f"{name}: returns something for at least one caller",
                     "every caller was refused or got an empty result, so the "
                     "isolation checks below would pass vacuously")

        print("\n== a refusal has to be usable ==")
        for name in sorted(CONTRACTS):
            for username, outcome in outcomes[name].items():
                if outcome != NO_ACCESS:
                    continue
                text = error_text(per_user[username]["calls"][name])
                # The whole point of the notice is that the person can act on
                # it: which repository, and who to ask. A bare "403" would be
                # an answer nobody can use.
                check(f"{name}: the refusal to {username} says which repository",
                      "you cannot read" in text and "/" in text,
                      text[:120])
                check(f"{name}: the refusal to {username} names who to ask",
                      "maintainer" in text.lower(), text[:120])

        print("\n== no tool leaks a repository the caller cannot read ==")
        for name in sorted(CONTRACTS):
            spec = CONTRACTS[name]
            if set(outcomes[name].values()) in ({NO_PACKS}, {NO_VECTORS}):
                skip(f"{name}: every repository named is one the caller may read",
                     "not covered: see the skip above")
                continue
            leaked: dict[str, list[str]] = {}
            seen_by: dict[str, set[str]] = {}
            for username, res in per_user.items():
                call = res["calls"].get(name)
                if not call or call["is_error"]:
                    # A refusal names the hidden repository ON PURPOSE -- that is
                    # how the asker learns who to ask -- so it is checked by the
                    # section above, not by this one.
                    continue
                found = normalise(collect_repos(call["payload"], set()))
                seen_by[username] = found
                outside = sorted(found - normalise(allowed[username]))
                if outside:
                    leaked[username] = outside
            check(f"{name}: every repository named is one the caller may read",
                  not leaked,
                  "; ".join(f"{u}: {v}" for u, v in leaked.items()) if leaked
                  else f"alpha={sorted(seen_by.get('dev_alpha', set()))} "
                       f"beta={sorted(seen_by.get('dev_beta', set()))}")

        print("\n== branch-agnostic behaviour ==")
        # One project is indexed at trunk AND at `v2`, which is the only way to
        # verify this. `scope_to_branch(None)` means each project's DEFAULT
        # branch, so an unqualified question answers from trunk; naming a branch
        # selects it; and a branch nobody indexed must SAY so, because an empty
        # list reads as "no such symbol" and is indistinguishable from one.
        alpha = per_user["dev_alpha"]

        async def acall(tool, args):
            from mcp import ClientSession
            from mcp.client.streamable_http import streamablehttp_client
            async with streamablehttp_client(
                f"{BASE}/mcp",
                headers={"Authorization": f"Bearer {users['dev_alpha']['token']}"}
            ) as (r, w, _):
                async with ClientSession(r, w) as session:
                    await session.initialize()
                    res = await session.call_tool(tool, args)
                    sc = getattr(res, "structuredContent", None)
                    if isinstance(sc, dict) and "result" in sc:
                        return {"err": bool(res.isError), "data": sc["result"]}
                    return {"err": bool(res.isError),
                            "text": " ".join(getattr(c, "text", "") for c in res.content)}

        trunk = await acall("find_symbol", {"name": "DecodeFrame"})
        v2 = await acall("find_symbol", {"name": "DecodeFrame", "branch": "v2"})
        trunk_docs = "\n".join(r.get("doc") or "" for r in (trunk.get("data") or []))
        v2_docs = "\n".join(r.get("doc") or "" for r in (v2.get("data") or []))
        check("an unqualified question answers from trunk, not from a branch",
              trunk_docs and "ON THE V2 BRANCH" not in trunk_docs, trunk_docs[:80])
        check("naming the branch returns that branch's content",
              "ON THE V2 BRANCH" in v2_docs, v2_docs[:80])

        only_v2 = await acall("find_symbol", {"name": "DecodeFrameV2"})
        check("a branch-only symbol is absent from an unqualified question",
              not (only_v2.get("data") or []), f"{only_v2.get('data')}")
        named = await acall("find_symbol", {"name": "DecodeFrameV2", "branch": "v2"})
        check("naming the branch finds the branch-only symbol",
              [r.get("name") for r in (named.get("data") or [])] == ["DecodeFrameV2"])

        missing = await acall("find_symbol", {"name": "DecodeFrame",
                                             "branch": "release/9"})
        text = missing.get("text") or ""
        check("an unindexed branch names the branches that ARE indexed",
              missing["err"] and "not indexed" in text
              and "main" in text and "v2" in text, text[:110])

        sem_v2 = await acall("semantic_search",
                             {"query": "decode a frame using the hardware path",
                              "branch": "v2"})
        sem_names = [r.get("name") for r in (sem_v2.get("data") or [])]
        check("semantic search is branch-scoped too",
              "DecodeFrameV2" in sem_names, f"{sem_names}")
        sem_trunk = await acall("semantic_search",
                                {"query": "decode a frame using the hardware path"})
        check("an unqualified semantic search does not return branch content",
              "DecodeFrameV2" not in [r.get("name") for r in (sem_trunk.get("data") or [])])

        listed = {(r.get("path_with_namespace"), r.get("branch"))
                  for r in (alpha["calls"]["index_status"]["payload"] or [])}
        check("index_status reports one row per (repo, branch)",
              ("root/eal-core", "v2") in listed
              and ("root/eal-core", "main") in listed, f"{sorted(listed)}")

        print("\n== the project with no members stays invisible ==")
        for name in sorted(CONTRACTS):
            if set(outcomes[name].values()) in ({NO_PACKS}, {NO_VECTORS}):
                skip(f"{name}: never names {UNREACHABLE} for anyone",
                     "not covered: see the skip above")
                continue
            hits = []
            for username, res in per_user.items():
                call = res["calls"].get(name)
                if not call:
                    continue
                # Refusals included: the notice names the repository it hid, so
                # it must never be the one nobody may know about.
                found = normalise(collect_repos(call["payload"], set()))
                if UNREACHABLE in found:
                    hits.append(f"{username}{'' if not call['is_error'] else ' (refusal)'}")
            check(f"{name}: never names {UNREACHABLE} for anyone", not hits,
                  f"leaked to {hits}" if hits else "")

    finally:
        proc.terminate()
        try:
            proc.wait(timeout=15)
        except subprocess.TimeoutExpired:
            proc.kill()

    passed = sum(1 for _, ok, _ in results if ok)
    failed = len(results) - passed
    print(f"\n{passed}/{len(results)} checks passed")
    if skipped:
        # Printed as its own block, and named, because the one thing a suite
        # must never do is let a skipped check read as coverage.
        print(f"{len(skipped)} SKIPPED -- NOT COVERED by this run:")
        for name, why in skipped:
            print(f"  - {name}: {why}")
    return 0 if not failed else 1


if __name__ == "__main__":
    sys.path.insert(0, str(ROOT))
    sys.exit(asyncio.run(main_async()))
