"""Read-only operator queries over the index, with NO access filtering.

WHY THIS IS A SEPARATE MODULE

Every query in `store/queries.py` takes `allowed_repo_ids` as its first
positional argument. That is not a convention, it is the mechanism: an MCP tool
cannot read a repository without passing the caller's allowlist, because there
is no way to call the function without one. Adding an unfiltered query to that
module would put a function next to sixteen that all look the same and differ
only in whether they are safe -- and the one time somebody reached for the
wrong one, nothing in the signature, the imports or the tests would say so.

So the unfiltered ones live here, under names that say who they are for. The
only caller is the admin surface in `mcpsrv/server.py`, which is gated by
`ARGUS_ADMIN_TOKEN`: the estate-wide operator credential, whose holder is
already entitled to see everything. `tests/store/test_explore.py` asserts that
no MCP tool module so much as imports this one, because that is the mistake
worth making impossible rather than merely unlikely.

WHAT IT IS FOR

Answering "why did the agent not find it?" -- the question an operator asks
when a tool returns nothing and they cannot tell whether the symbol is absent,
named differently, private, or never indexed. `find_symbol` answers for one
caller and one exact name; this answers for the estate and a fragment, and it
shows what the index actually holds rather than what a query made of it.
"""
from __future__ import annotations

import sqlite3

#: An operator typing into a box gets a fast page or no page. High enough to
#: see a real result set, low enough that the JSON is not the bottleneck.
DEFAULT_LIMIT = 50
MAX_LIMIT = 200


def _clamp(limit: int | None) -> int:
    if not limit or limit < 1:
        return DEFAULT_LIMIT
    return min(int(limit), MAX_LIMIT)


def _like(value: str) -> str:
    """A substring pattern with LIKE's own metacharacters escaped.

    Without this, a search for `printf_100%` matches everything (a bare `%` is
    a wildcard) and `a_b` matches `axb`. The operator sees a result set that
    does not contain what they typed and concludes the index is wrong.
    """
    escaped = (value.replace("\\", "\\\\").replace("%", "\\%")
               .replace("_", "\\_"))
    return f"%{escaped}%"


def repos(conn: sqlite3.Connection) -> list[dict]:
    """Every repository in the index, with what it holds.

    The counts are per repository and NOT per ref: `files` and `symbols` hang
    off `repo_id`, and a project indexed at two branches shares them. The
    branch column is what tells the refs apart.
    """
    rows = conn.execute(
        "SELECT r.id AS repo_id, r.path_with_namespace, r.branch,"
        "       r.default_branch, r.last_run_at, r.last_indexed_at,"
        "       r.last_run_error,"
        "       (SELECT COUNT(*) FROM files   f WHERE f.repo_id = r.id) AS files,"
        "       (SELECT COUNT(*) FROM symbols s WHERE s.repo_id = r.id) AS symbols,"
        "       (SELECT COUNT(*) FROM symbols s WHERE s.repo_id = r.id"
        "         AND s.is_public = 1) AS public_symbols"
        "  FROM repos r ORDER BY r.path_with_namespace, r.branch").fetchall()
    return [dict(row) for row in rows]


def symbols(conn: sqlite3.Connection, *, pattern: str = "", repo: str = "",
            limit: int | None = None) -> dict:
    """Symbols whose NAME contains `pattern`, optionally within one repository.

    A fragment rather than an exact name, because the operator is guessing:
    they know roughly what the function is called, or they are checking whether
    a rename landed. `find_symbol` requires the exact name and is for callers
    who already know it.
    """
    limit = _clamp(limit)
    where, params = [], []
    if pattern:
        where.append("s.name LIKE ? ESCAPE '\\'")
        params.append(_like(pattern))
    if repo:
        where.append("r.path_with_namespace = ?")
        params.append(repo)
    clause = (" WHERE " + " AND ".join(where)) if where else ""
    # limit + 1 so "there are more" is a fact rather than a guess. A result set
    # that silently stops at the limit reads as "that is all there is".
    rows = conn.execute(
        "SELECT s.name, s.kind, s.scope, s.signature, s.line, s.end_line,"
        "       s.is_public, s.doc, f.path, f.lang,"
        "       r.path_with_namespace, r.branch,"
        "       s.repo_id"
        "  FROM symbols s"
        "  JOIN files f ON f.id = s.file_id"
        "  JOIN repos r ON r.id = s.repo_id"
        f"{clause}"
        " ORDER BY s.name, r.path_with_namespace, f.path"
        " LIMIT ?", (*params, limit + 1)).fetchall()
    return {"rows": [dict(row) for row in rows[:limit]],
            "capped": len(rows) > limit, "limit": limit}


def files(conn: sqlite3.Connection, *, pattern: str = "", repo: str = "",
          limit: int | None = None) -> dict:
    """Files whose PATH contains `pattern`, with how much was extracted from each.

    The symbol count is the point: a file that is indexed with zero symbols is
    a file whose language the extractor did not recognise, which is the
    difference between "the agent cannot find it" and "it is not in the index".
    """
    limit = _clamp(limit)
    where, params = [], []
    if pattern:
        where.append("f.path LIKE ? ESCAPE '\\'")
        params.append(_like(pattern))
    if repo:
        where.append("r.path_with_namespace = ?")
        params.append(repo)
    clause = (" WHERE " + " AND ".join(where)) if where else ""
    rows = conn.execute(
        "SELECT f.path, f.lang, f.size, f.blob_sha, f.is_vendored,"
        "       r.path_with_namespace, r.branch, f.repo_id,"
        "       (SELECT COUNT(*) FROM symbols s WHERE s.file_id = f.id) AS symbols"
        "  FROM files f"
        "  JOIN repos r ON r.id = f.repo_id"
        f"{clause}"
        " ORDER BY r.path_with_namespace, f.path"
        " LIMIT ?", (*params, limit + 1)).fetchall()
    return {"rows": [dict(row) for row in rows[:limit]],
            "capped": len(rows) > limit, "limit": limit}
