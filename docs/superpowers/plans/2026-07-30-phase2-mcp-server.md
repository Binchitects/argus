# Phase 2 — Multi-user Retrieval Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Expose the Phase 1 index to developers' Hermes instances as an authenticated MCP server, where every query is filtered to the repositories that developer can see in GitLab.

**Architecture:** An `acl` module exchanges a developer's GitLab personal access token for their project membership, TTL-cached and fail-closed. An `mcpsrv` package serves five tools over HTTP MCP using the official `mcp` SDK's FastMCP, with auth middleware that resolves the caller before any handler runs. Every tool receives an allowlist and passes it into the existing `store/queries.py` functions, which already require it as their first positional argument. A TLS reverse proxy terminates in front; the server binds localhost.

**Tech Stack:** Python 3.11+, `mcp` SDK (FastMCP), httpx, SQLite (read-only connections), Caddy, systemd.

Spec: [`../specs/2026-07-28-local-code-assistant-design.md`](../specs/2026-07-28-local-code-assistant-design.md)
Prerequisite: [`2026-07-30-phase1-hardening-and-measurement.md`](2026-07-30-phase1-hardening-and-measurement.md)

## Established facts this plan depends on

Verified against the installed Hermes source, not assumed:

- `hermes mcp add --url <URL> --auth header` prompts for a token, stores it in `~/.hermes/.env`, and sends **`Authorization: Bearer <token>`** on every call (`hermes_cli/mcp_config.py`). The stored value has any `Bearer ` prefix stripped.
- `hermes mcp test <name>` connects and lists tools — this gives Task 11 a genuine end-to-end check rather than a self-reported one.

## Global Constraints

Every task's requirements implicitly include this section.

- **Python `>=3.11`.** Deployment host is Linux; development is Windows. No 3.12+ syntax.
- **The package is `argus`.** Imports are `from argus...`.
- **`allowed_repo_ids` is the first positional parameter, no default, on every public function in `argus/store/queries.py`.** New query functions added here are bound by it.
- **Never edit an applied migration.** `001`–`005` are applied. New schema goes in `006_*.sql` and above.
- **The server is read-only.** It opens SQLite with `mode=ro`. It must never write index data. The only writes it performs are audit rows, on a separate read-write connection.
- **Fail closed.** An ACL resolution that cannot be completed denies access. It never falls back to "allow".
- **No network in tests.** GitLab is mocked with `httpx.MockTransport`; the MCP surface is exercised in-process.
- **Tool error text is prompt text.** Every error a tool returns is read by an LLM and determines its next action. Write errors as instructions, not status codes.
- Conventional commit prefixes (`feat:`, `fix:`, `test:`, `chore:`, `docs:`).
- Phase 2 scope: **no embeddings, no `semantic_search`, no `which_repo`, no `repo_map`, no cross-repo include resolution.** Those are Phases 3–4.

## File Structure

| File | Responsibility |
|---|---|
| `argus/store/migrations/006_acl_audit.sql` | `acl_cache`, `audit` tables |
| `argus/store/db.py` | Modify — add `connect_readonly` |
| `argus/store/queries.py` | Modify — allowlist chunking, error wrapping, `get_file` cap, `find_references` |
| `argus/store/writes.py` | Modify — `record_audit`, `delete_repo` |
| `argus/acl.py` | GitLab PAT → identity + allowlist, TTL cache, fail-closed |
| `argus/mcpsrv/server.py` | FastMCP app, auth middleware, `/healthz` |
| `argus/mcpsrv/tools.py` | The five tool handlers |
| `argus/mcpsrv/errors.py` | Agent-facing error strings |
| `argus/cli.py` | Modify — `argus serve` |
| `deploy/argus.service`, `deploy/Caddyfile` | systemd unit and TLS proxy config |

---

### Task 1: Read-only connections and the ACL/audit schema

**Files:**
- Create: `argus/store/migrations/006_acl_audit.sql`
- Modify: `argus/store/db.py`
- Test: `tests/store/test_db.py`

**Interfaces:**
- Produces: `connect_readonly(db_path) -> sqlite3.Connection` opened via the `file:...?mode=ro` URI with `check_same_thread=False`; `open_db` unchanged for the indexer.
- Schema: `acl_cache(token_hash PK, user_id, username, repo_ids_json, fetched_at)`, `audit(id, ts, user_id, username, tool, args_json, repo_ids_json)`.

**Why `check_same_thread=False` matters.** `sqlite3.Connection` defaults to rejecting use from a thread other than the one that created it. An HTTP server is concurrent. The indexer's single-connection-threaded-through-everything model does not survive contact with a server, so the server gets its own connection factory.

- [ ] **Step 1: Write the failing tests**

```python
def test_connect_readonly_rejects_writes(tmp_path):
    import pytest, sqlite3
    from argus.store.db import open_db, connect_readonly
    path = tmp_path / "i.db"
    open_db(path).close()
    ro = connect_readonly(path)
    with pytest.raises(sqlite3.OperationalError, match="readonly"):
        ro.execute("INSERT INTO repos (gitlab_id, path_with_namespace, default_branch,"
                   " http_url) VALUES (1, 'g/a', 'main', 'x')")


def test_connect_readonly_can_read(tmp_path):
    from argus.store.db import open_db, connect_readonly
    from argus.store import writes
    path = tmp_path / "i.db"
    conn = open_db(path)
    writes.upsert_repo(conn, gitlab_id=1, path_with_namespace="g/a",
                       default_branch="main", http_url="x")
    assert connect_readonly(path).execute("SELECT COUNT(*) c FROM repos").fetchone()["c"] == 1


def test_acl_and_audit_tables_exist(tmp_path):
    from argus.store.db import open_db
    conn = open_db(tmp_path / "i.db")
    names = {r["name"] for r in conn.execute(
        "SELECT name FROM sqlite_master WHERE type = 'table'")}
    assert {"acl_cache", "audit"} <= names
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `python -m pytest tests/store/test_db.py -k "readonly or acl_and_audit" -v`
Expected: FAIL — `connect_readonly` does not exist; tables absent.

- [ ] **Step 3: Write the migration**

`argus/store/migrations/006_acl_audit.sql`:

```sql
-- Developer token -> GitLab project membership, cached. The token itself is
-- never stored: token_hash is SHA-256 of the token.
CREATE TABLE IF NOT EXISTS acl_cache (
  token_hash    TEXT PRIMARY KEY,
  user_id       INTEGER NOT NULL,
  username      TEXT    NOT NULL,
  repo_ids_json TEXT    NOT NULL,
  fetched_at    INTEGER NOT NULL
);

-- One row per tool call. At 2-5 developers this costs nothing and answers
-- "what did the assistant show them" after the fact.
CREATE TABLE IF NOT EXISTS audit (
  id            INTEGER PRIMARY KEY,
  ts            INTEGER NOT NULL,
  user_id       INTEGER,
  username      TEXT,
  tool          TEXT    NOT NULL,
  args_json     TEXT    NOT NULL,
  repo_ids_json TEXT
);
CREATE INDEX IF NOT EXISTS idx_audit_ts ON audit(ts);
```

- [ ] **Step 4: Implement `connect_readonly`**

```python
def connect_readonly(db_path: Path | str) -> sqlite3.Connection:
    """Open the index read-only. The server must never write index data.

    check_same_thread=False because an HTTP server is concurrent; the caller
    is responsible for using one connection per request or per thread.
    """
    uri = f"file:{Path(db_path).as_posix()}?mode=ro"
    conn = sqlite3.connect(uri, uri=True, check_same_thread=False)
    conn.row_factory = sqlite3.Row
    conn.execute("PRAGMA query_only = ON")
    return conn
```

- [ ] **Step 5: Run the full suite, then commit**

Run: `python -m pytest -q` — all pass, 0 skipped.

```bash
git add argus/store/migrations/006_acl_audit.sql argus/store/db.py tests/store/test_db.py
git commit -m "feat: add read-only connections and the ACL/audit schema"
```

---

### Task 2: The ACL module

**Files:**
- Create: `argus/acl.py`
- Modify: `argus/store/writes.py` (ACL cache read/write helpers)
- Test: `tests/test_acl.py`

**Interfaces:**
- Produces:
  - `Identity` dataclass: `user_id: int`, `username: str`, `allowed_repo_ids: list[int]`
  - `AclDenied(Exception)` — carries an agent-facing message
  - `resolve(conn_rw, cfg, token, *, client=None, now=None) -> Identity`
- Behaviour: SHA-256 of the token is the cache key; the token is never stored. Fresh cache (< `ttl_seconds`, default 600) is used directly. On a miss, `GET /api/v4/user` and `GET /api/v4/projects?membership=true&min_access_level=20&simple=true` are called **with the developer's own token**, so GitLab decides visibility. GitLab project ids are mapped to `repos.id`; unknown ids are dropped. On GitLab being unreachable: a cache entry inside the stale grace window (default 3600s) is served with a warning logged; a cache miss **denies**.

**`min_access_level=20` is Reporter.** Guest (10) cannot read repository code in GitLab, so Reporter is the correct floor for code access.

**The gitlab_id → repos.id mapping must fail closed.** The ACL resolves GitLab project ids; the store filters on `repos.id`. A project the index has never seen maps to nothing and is simply absent from the allowlist. An allowlist that maps to zero known repos yields `[]`, which every query already treats as "return nothing" — never as "skip the filter".

- [ ] **Step 1: Write the failing tests**

`tests/test_acl.py` covering, at minimum:

```python
import httpx, pytest, json
from argus import acl
from argus.config import GitLabConfig
from argus.store.db import open_db
from argus.store import writes

CFG = GitLabConfig(url="https://gl.test", token="service-token")


def _client(handler):
    return httpx.Client(transport=httpx.MockTransport(handler))


def _ok_handler(projects):
    def handler(request):
        if request.url.path.endswith("/user"):
            return httpx.Response(200, json={"id": 7, "username": "dev"})
        if request.url.path.endswith("/projects"):
            page = dict(request.url.params).get("page", "1")
            return httpx.Response(200, json=projects if page == "1" else [])
        return httpx.Response(404)
    return handler


@pytest.fixture
def conn(tmp_path):
    c = open_db(tmp_path / "i.db")
    writes.upsert_repo(c, gitlab_id=101, path_with_namespace="g/a",
                       default_branch="main", http_url="x")
    writes.upsert_repo(c, gitlab_id=102, path_with_namespace="g/b",
                       default_branch="main", http_url="x")
    return c


def test_resolves_and_maps_gitlab_ids_to_repo_ids(conn):
    ident = acl.resolve(conn, CFG, "dev-token",
                        client=_client(_ok_handler([{"id": 101}])))
    rid = conn.execute("SELECT id FROM repos WHERE gitlab_id = 101").fetchone()["id"]
    assert ident.allowed_repo_ids == [rid]
    assert ident.username == "dev"


def test_unknown_gitlab_project_is_dropped_not_allowed(conn):
    ident = acl.resolve(conn, CFG, "dev-token",
                        client=_client(_ok_handler([{"id": 999}])))
    assert ident.allowed_repo_ids == []


def test_token_is_never_stored_in_plaintext(conn):
    acl.resolve(conn, CFG, "super-secret", client=_client(_ok_handler([{"id": 101}])))
    blob = json.dumps([dict(r) for r in conn.execute("SELECT * FROM acl_cache")])
    assert "super-secret" not in blob


def test_cache_hit_makes_no_http_call(conn):
    acl.resolve(conn, CFG, "dev-token", client=_client(_ok_handler([{"id": 101}])))
    def explode(request):
        raise AssertionError("should not have called GitLab on a cache hit")
    acl.resolve(conn, CFG, "dev-token", client=_client(explode))


def test_expired_cache_refetches(conn):
    acl.resolve(conn, CFG, "dev-token", client=_client(_ok_handler([{"id": 101}])),
                now=lambda: 1000)
    ident = acl.resolve(conn, CFG, "dev-token",
                        client=_client(_ok_handler([{"id": 101}, {"id": 102}])),
                        now=lambda: 1000 + 601)
    assert len(ident.allowed_repo_ids) == 2


def test_gitlab_down_with_stale_cache_serves_stale(conn):
    acl.resolve(conn, CFG, "dev-token", client=_client(_ok_handler([{"id": 101}])),
                now=lambda: 1000)
    def down(request):
        raise httpx.ConnectError("unreachable")
    ident = acl.resolve(conn, CFG, "dev-token", client=_client(down),
                        now=lambda: 1000 + 900)
    assert len(ident.allowed_repo_ids) == 1


def test_gitlab_down_with_no_cache_denies(conn):
    def down(request):
        raise httpx.ConnectError("unreachable")
    with pytest.raises(acl.AclDenied):
        acl.resolve(conn, CFG, "unknown-token", client=_client(down))


def test_revoked_token_denies(conn):
    def unauthorized(request):
        return httpx.Response(401, json={"message": "401 Unauthorized"})
    with pytest.raises(acl.AclDenied, match="token"):
        acl.resolve(conn, CFG, "revoked", client=_client(unauthorized))


def test_stale_beyond_grace_denies(conn):
    acl.resolve(conn, CFG, "dev-token", client=_client(_ok_handler([{"id": 101}])),
                now=lambda: 1000)
    def down(request):
        raise httpx.ConnectError("unreachable")
    with pytest.raises(acl.AclDenied):
        acl.resolve(conn, CFG, "dev-token", client=_client(down),
                    now=lambda: 1000 + 100_000)
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `python -m pytest tests/test_acl.py -v`
Expected: FAIL — `No module named 'argus.acl'`.

- [ ] **Step 3: Implement**

`argus/acl.py`:

```python
from __future__ import annotations

import hashlib
import json
import logging
import sqlite3
import time
from dataclasses import dataclass

import httpx

from .config import GitLabConfig

log = logging.getLogger(__name__)

TTL_SECONDS = 600
STALE_GRACE_SECONDS = 3600
MIN_ACCESS_LEVEL = 20  # Reporter. Guest (10) cannot read repository code.
PER_PAGE = 100
MAX_PAGES = 1000


class AclDenied(Exception):
    """Access could not be established. The message is read by an agent."""


@dataclass(frozen=True)
class Identity:
    user_id: int
    username: str
    allowed_repo_ids: list[int]


def _hash(token: str) -> str:
    return hashlib.sha256(token.encode("utf-8")).hexdigest()


def _map_to_repo_ids(conn: sqlite3.Connection, gitlab_ids: list[int]) -> list[int]:
    """Map GitLab project ids to local repo ids, dropping unknown projects.

    Dropping is the fail-closed behaviour: a project the index has never seen
    simply is not in the allowlist. An empty result stays empty — every query
    treats [] as 'return nothing', never as 'skip the filter'.
    """
    if not gitlab_ids:
        return []
    marks = ",".join("?" for _ in gitlab_ids)
    rows = conn.execute(
        f"SELECT id FROM repos WHERE gitlab_id IN ({marks})", gitlab_ids
    ).fetchall()
    return sorted(r["id"] for r in rows)


def _fetch(cfg: GitLabConfig, token: str, client: httpx.Client) -> tuple[int, str, list[int]]:
    me = client.get(f"{cfg.url}/api/v4/user", headers={"PRIVATE-TOKEN": token})
    if me.status_code in (401, 403):
        raise AclDenied(
            "Your GitLab token was rejected. Refresh it and re-run "
            "`hermes mcp add argus --url <url> --auth header`."
        )
    if me.status_code != 200:
        raise AclDenied(f"GitLab returned {me.status_code} for /user.")
    user = me.json()

    gitlab_ids: list[int] = []
    for page in range(1, MAX_PAGES + 1):
        resp = client.get(
            f"{cfg.url}/api/v4/projects",
            params={"membership": "true", "min_access_level": MIN_ACCESS_LEVEL,
                    "simple": "true", "per_page": PER_PAGE, "page": page},
            headers={"PRIVATE-TOKEN": token},
        )
        if resp.status_code != 200:
            raise AclDenied(f"GitLab returned {resp.status_code} listing your projects.")
        batch = resp.json()
        if not batch:
            break
        gitlab_ids.extend(int(p["id"]) for p in batch)
    return int(user["id"]), user["username"], gitlab_ids


def resolve(conn: sqlite3.Connection, cfg: GitLabConfig, token: str, *,
            client: httpx.Client | None = None, now=None) -> Identity:
    now = now or time.time
    if not token:
        raise AclDenied("No credential was sent. Configure the server with --auth header.")

    key = _hash(token)
    cached = conn.execute(
        "SELECT user_id, username, repo_ids_json, fetched_at FROM acl_cache"
        " WHERE token_hash = ?", (key,)
    ).fetchone()
    age = (now() - cached["fetched_at"]) if cached else None

    if cached is not None and age < TTL_SECONDS:
        return Identity(cached["user_id"], cached["username"],
                        json.loads(cached["repo_ids_json"]))

    owns_client = client is None
    client = client or httpx.Client(timeout=15.0)
    try:
        user_id, username, gitlab_ids = _fetch(cfg, token, client)
    except AclDenied:
        raise
    except Exception as exc:
        # GitLab unreachable. Serve stale inside the grace window; otherwise deny.
        if cached is not None and age < STALE_GRACE_SECONDS:
            log.warning("GitLab unreachable (%s); serving ACL cached %.0fs ago", exc, age)
            return Identity(cached["user_id"], cached["username"],
                            json.loads(cached["repo_ids_json"]))
        raise AclDenied(
            "Cannot verify your GitLab access right now and no recent cached "
            "permission exists, so access is denied. Retry shortly."
        ) from exc
    finally:
        if owns_client:
            client.close()

    repo_ids = _map_to_repo_ids(conn, gitlab_ids)
    conn.execute(
        "INSERT INTO acl_cache (token_hash, user_id, username, repo_ids_json, fetched_at)"
        " VALUES (?, ?, ?, ?, ?)"
        " ON CONFLICT(token_hash) DO UPDATE SET user_id = excluded.user_id,"
        "   username = excluded.username, repo_ids_json = excluded.repo_ids_json,"
        "   fetched_at = excluded.fetched_at",
        (key, user_id, username, json.dumps(repo_ids), int(now())),
    )
    conn.commit()
    return Identity(user_id, username, repo_ids)
```

- [ ] **Step 4: Run the full suite, then commit**

```bash
git add argus/acl.py tests/test_acl.py
git commit -m "feat: add ACL module resolving GitLab tokens to repo allowlists"
```

---

### Task 3: Allowlist chunking and query hardening

**Files:**
- Modify: `argus/store/queries.py`
- Test: `tests/store/test_queries.py`

**Interfaces:**
- `_placeholders` gains chunked execution so an allowlist larger than SQLite's host-parameter limit works instead of raising.
- `search_code` wraps FTS5 syntax errors in an agent-facing message.
- `get_file(allowed_repo_ids, conn, repo_id, path, max_bytes=…)` truncates and says so.

**Why chunking is a security concern, not just a limit.** SQLite's default `SQLITE_MAX_VARIABLE_NUMBER` is 999. Each repo id becomes its own placeholder, so a developer in a large GitLab group raises `sqlite3.OperationalError`. Under fail-closed semantics an exception must never be confused with "deny" — it is an availability bug that looks like a security decision. Chunk the ids and union the results.

**Why the FTS5 wrap matters.** `search_code` passes the query straight into `MATCH`. A stray quote, an unbalanced `NEAR(`, or a bare `AND` raises `sqlite3.OperationalError`. Per the spec's own principle, an unwrapped SQLite exception is exactly the wrong string to hand a 35B model.

- [ ] **Step 1: Write the failing tests**

```python
def test_allowlist_larger_than_sqlite_parameter_limit(two_repos):
    conn, ids = two_repos
    big = list(range(5000, 5000 + 1500)) + [ids["g/alpha"]]
    rows = queries.find_symbol(big, conn, "SharedName")
    assert len(rows) == 1 and rows[0]["repo_id"] == ids["g/alpha"]


def test_malformed_fts_query_returns_actionable_error(two_repos):
    conn, ids = two_repos
    with pytest.raises(queries.QueryError, match="search syntax"):
        queries.search_code([ids["g/alpha"]], conn, 'unbalanced "quote')


def test_get_file_truncates_and_says_so(two_repos):
    conn, ids = two_repos
    row = queries.get_file([ids["g/alpha"]], conn, ids["g/alpha"], "src/a.c", max_bytes=4)
    assert len(row["content"]) <= 64
    assert row["truncated"] is True
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `python -m pytest tests/store/test_queries.py -k "parameter_limit or malformed_fts or truncates" -v`
Expected: FAIL — `OperationalError: too many SQL variables`; `QueryError` undefined; `get_file` has no `max_bytes`.

- [ ] **Step 3: Implement**

Add `class QueryError(Exception)` to `queries.py`. Add a chunking helper and use it in every function that expands the allowlist:

```python
SQLITE_MAX_VARS = 900  # conservative; SQLite's default limit is 999


def _chunks(ids: list[int], reserve: int) -> list[list[int]]:
    """Split the allowlist so no statement exceeds SQLite's parameter limit.

    reserve is the number of non-allowlist parameters in the statement.
    """
    size = max(1, SQLITE_MAX_VARS - reserve)
    return [ids[i:i + size] for i in range(0, len(ids), size)] or [[]]
```

Each query runs once per chunk and concatenates, applying `limit` to the merged result. Wrap the `MATCH` execution:

```python
    try:
        rows = conn.execute(sql, params).fetchall()
    except sqlite3.OperationalError as exc:
        raise QueryError(
            f"That search syntax is not valid ({exc}). Try plain terms without "
            'quotes or operators, e.g. DecodeFrame, or use regex=True.'
        ) from exc
```

`get_file` gains `max_bytes: int = 65536`, returns a plain dict with `truncated: bool`, and truncates on a character boundary.

- [ ] **Step 4: Run the full suite, then commit**

```bash
git add argus/store/queries.py tests/store/test_queries.py
git commit -m "feat: chunk allowlists, wrap FTS errors, cap get_file output"
```

---

### Task 4: Strengthen the allowlist enforcement test

**Files:**
- Modify: `tests/store/test_queries.py`
- Test: itself

**Interfaces:** No production change. The existing reflection test proves every public query *declares* `allowed_repo_ids` first; it cannot detect a function that accepts the parameter and never uses it. Phase 2 adds new query functions, so strengthen it before they land.

- [ ] **Step 1: Write the failing test**

Replace the signature-only check with one that also proves isolation, parameterised over every public function so new ones are covered automatically:

```python
def _public_query_functions():
    return [
        (name, fn) for name, fn in inspect.getmembers(queries, inspect.isfunction)
        if not name.startswith("_") and fn.__module__ == queries.__name__
    ]


@pytest.mark.parametrize("name,fn", _public_query_functions())
def test_every_public_query_actually_filters(name, fn, two_repos):
    """Declaring the parameter is not enough — it must change the result."""
    conn, ids = two_repos
    a, b = ids["g/alpha"], ids["g/beta"]
    kwargs = _minimal_args_for(name, conn, b)   # helper supplying required args
    allowed_none = fn([], conn, **kwargs)
    allowed_wrong = fn([a], conn, **kwargs)
    allowed_right = fn([b], conn, **kwargs)
    assert not allowed_none, f"{name} returned data for an empty allowlist"
    assert allowed_wrong != allowed_right, \
        f"{name} returns the same data regardless of the allowlist — it is not filtering"
```

Write `_minimal_args_for` explicitly, with a branch per current function name, and make it **raise** on an unknown function name so a newly added query cannot silently skip the check.

- [ ] **Step 2–5:** Run to confirm it passes for existing functions; confirm it fails by temporarily adding a deliberately-unfiltered query function; remove that; commit.

```bash
git commit -m "test: prove every public query filters, not just declares the allowlist"
```

---

### Task 5: `find_references` — name-based lexical

**Files:**
- Modify: `argus/store/queries.py`
- Test: `tests/store/test_queries.py`

**Interfaces:**
- `find_references(allowed_repo_ids, conn, name, limit=100) -> list[dict]` returning `repo`, `path`, `line`, `context`, and `is_definition`.

**This is name-based, not semantic, and the tool description must say so.** ctags gives definitions, not references; nothing in the index resolves an identifier to a declaration. This finds occurrences of the identifier and marks which ones are known definition sites. It cannot distinguish a call from a comment mentioning the name, and it misses calls made through macros or function pointers. A real reference index needs a parser with scope resolution — Phase 3 or later.

- [ ] **Step 1: Write the failing tests** — covering: finds a caller in another file; marks the definition site with `is_definition=True`; respects the allowlist; returns `[]` for an unknown name; does not match a substring of a longer identifier (`DecodeFrame` must not match `DecodeFrameV2`).

- [ ] **Step 2: Run to verify failure.**

- [ ] **Step 3: Implement** using FTS5 to shortlist candidate files, then a word-boundary scan of `files.content` to produce line numbers and context — FTS5 tokenisation alone will not respect identifier boundaries reliably, and a false `DecodeFrame`/`DecodeFrameV2` match is exactly the error an agent will repeat confidently.

- [ ] **Steps 4–5:** Full suite; commit.

```bash
git commit -m "feat: add name-based find_references"
```

---

### Task 6: MCP server skeleton with auth middleware

**Files:**
- Create: `argus/mcpsrv/__init__.py`, `argus/mcpsrv/server.py`, `argus/mcpsrv/errors.py`
- Modify: `pyproject.toml` (add `mcp`)
- Test: `tests/mcpsrv/test_server.py`

**Interfaces:**
- `create_app(cfg) -> FastMCP` with `/healthz`; auth middleware that reads `Authorization: Bearer <token>`, calls `acl.resolve`, and attaches the `Identity` to the request context. A missing or malformed header returns 401 **before** any handler runs.

- [ ] **Step 1: Write the failing tests** — unauthenticated call is rejected; a malformed header is rejected; a valid token reaches a handler with the right `Identity`; `/healthz` needs no auth; an `AclDenied` becomes a 401 carrying its agent-facing message.

- [ ] **Steps 2–5:** Verify failure; implement; full suite; commit.

```bash
git commit -m "feat: add MCP server skeleton with bearer-token auth middleware"
```

---

### Task 7: The five tools

**Files:**
- Create: `argus/mcpsrv/tools.py`
- Test: `tests/mcpsrv/test_tools.py`

**Interfaces:** `find_symbol`, `find_references`, `search_code`, `get_file`, `index_status` — each takes the caller's `Identity` from context and passes `identity.allowed_repo_ids` as the first positional argument to the corresponding `queries` function.

**Tool descriptions are load-bearing.** They are the only thing telling a 35B model which tool answers which question. Each description states what the tool answers, and `find_references` states plainly that it is name-based so the model qualifies its answers.

`index_status` must surface the run-state flags added in the hardening plan's Task 5, so the agent can distinguish "no such symbol" from "this repo's symbols were never extracted".

- [ ] **Step 1: Write the failing tests** — each tool returns the caller's repos only; each refuses without auth; `search_code` surfaces `QueryError` as an actionable string rather than a traceback; `get_file` reports truncation; `index_status` reports a timed-out repo as partial.

- [ ] **Steps 2–5:** Verify failure; implement; full suite; commit.

```bash
git commit -m "feat: expose the five Phase 2 retrieval tools over MCP"
```

---

### Task 8: Audit logging

**Files:**
- Modify: `argus/store/writes.py`, `argus/mcpsrv/server.py`
- Test: `tests/mcpsrv/test_audit.py`

**Interfaces:** `writes.record_audit(conn_rw, *, ts, user_id, username, tool, args_json, repo_ids_json)`. Every tool call appends one row on a **separate read-write connection** — the query path stays read-only.

- [ ] **Step 1: Write the failing tests** — a successful call writes exactly one row with the right tool name and user; a denied call writes a row too (an attempted access is worth recording); the arguments recorded contain no credential.

- [ ] **Steps 2–5:** Verify failure; implement; full suite; commit.

```bash
git commit -m "feat: record an audit row per tool call"
```

---

### Task 9: `delete_repo`, for projects removed from GitLab

**Files:**
- Modify: `argus/store/writes.py`, `argus/worker.py` or `argus/cli.py`
- Test: `tests/store/test_writes.py`

**Interfaces:** `delete_repo(conn, repo_id)` removing the repo and all dependent rows **including FTS entries**.

**Why this cannot be a plain `DELETE`.** `DELETE FROM repos` cascades to `files` via the foreign key, but `files_fts` is external-content with no triggers, so its rows are orphaned and the index desynchronises. `delete_repo` must issue the FTS `'delete'` command for each file first. Phase 2's ACL makes repo removal a live concern: a project archived or deleted in GitLab should stop appearing.

- [ ] **Step 1: Write the failing test** — after `delete_repo`, `files`, `symbols`, `includes` and `files_fts` all have zero rows for that repo, and a full-text search for its content returns nothing.

- [ ] **Steps 2–5:** Verify failure; implement; full suite; commit.

```bash
git commit -m "feat: add delete_repo with correct FTS cleanup"
```

---

### Task 10: `argus serve`, systemd unit, TLS proxy

**Files:**
- Modify: `argus/cli.py`
- Create: `deploy/argus.service`, `deploy/Caddyfile`, `docs/deployment.md`
- Test: `tests/test_cli.py`

**Interfaces:** `argus serve --config PATH [--host 127.0.0.1] [--port 7700]`. Binds localhost by default. Also `argus flush-acl --config PATH [--user USERNAME]`, which deletes `acl_cache` rows so a revocation takes effect immediately instead of waiting out the TTL.

`flush-acl` is not optional garnish — the spec's revocation story is "within the TTL, or immediately via a flush", and Task 11 Step 6 exercises it. Without it the only way to revoke faster than 10 minutes is to restart the service.

**TLS is required, not optional.** `hermes mcp add --auth header` sends the developer's GitLab PAT on every call. Over plain HTTP on a LAN that is a credential in cleartext on the wire.

The deployment doc must also cover **Ollama**, which carries no credentials but does carry your source code as prompt text and has no authentication of its own — it belongs behind the same proxy or firewalled to the developer subnet, never on an open port.

- [ ] **Step 1: Write the failing tests** — `serve` rejects a bad config with exit 2; `--host`/`--port` are honoured; the default bind is localhost, asserted explicitly so a future change to `0.0.0.0` fails the suite; `flush-acl` empties `acl_cache`, and with `--user` empties only that user's rows.

- [ ] **Steps 2–5:** Verify failure; implement; full suite; commit.

```bash
git commit -m "feat: add argus serve with systemd and TLS proxy deployment config"
```

---

### Task 11: End-to-end verification against real Hermes

**Files:**
- Create: `docs/phase2-verification.md`

**Interfaces:** Consumes the whole Phase 2 stack. No tests — this exercises the real integration, which no in-process test can.

**Requires the operator's machine, a running server, and a real GitLab token.**

- [ ] **Step 1: Start the server** and confirm `/healthz` through the TLS proxy.

- [ ] **Step 2: Register with Hermes**

```bash
hermes mcp add argus --url https://<index-host>/mcp --auth header
```

- [ ] **Step 3: Verify the connection lists all five tools**

```bash
hermes mcp test argus
```

- [ ] **Step 4: Verify the access boundary with two different developers' tokens.** Confirm a developer who lacks access to a repository cannot retrieve its content through `get_file`, and that it is absent from `find_symbol` results. **This is the check the whole design exists for — do not skip it.**

- [ ] **Step 5: Ask real questions through Hermes** and record which tool the model chose. If it reaches for `search_code` when `find_symbol` was correct, the tool descriptions need work — that is a real finding, not a curiosity.

- [ ] **Step 6: Verify revocation.** Remove a developer from a project in GitLab, wait out the TTL (or run the cache flush), and confirm the repository disappears from their results.

- [ ] **Step 7: Write up** `docs/phase2-verification.md` and commit.

---

---

## A note on task depth

Tasks 1–3 carry complete code because they are the security boundary and the
places where a subtle mistake is unrecoverable. Tasks 4–9 are specified to
interface and test level rather than full listings, deliberately: their details
depend on what the hardening plan's Task 5 actually produces and on the
measurement run's numbers. **Expand each into full TDD steps immediately before
executing it**, not now — writing complete code today against interfaces that
Task 5 may adjust would produce a plan that is confidently wrong.

## Completion Criteria

- [ ] `python -m pytest -q` passes, 0 skipped
- [ ] Every public function in `queries.py` proven to *filter*, not merely declare, the allowlist
- [ ] An unauthenticated MCP call is rejected before any handler runs
- [ ] GitLab unreachable + no cache = denied; GitLab unreachable + recent cache = served
- [ ] `hermes mcp test argus` lists five tools
- [ ] A developer without access to a repo cannot reach its content by any tool
- [ ] Revoking access in GitLab removes it from results within the TTL

## Deliberately Not In Phase 2

`semantic_search`, `which_repo`, `repo_map`, embeddings, cross-repo include resolution, and webhook-driven indexing. Whether the webhook path is needed at all depends on the incremental-run timing measured in the hardening plan's Task 6 — if a poll cycle is fast enough, the webhook is unnecessary complexity.
