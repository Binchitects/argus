from __future__ import annotations

import re
import sqlite3
from collections.abc import Sequence

# Conservative: SQLite's *documented* default SQLITE_MAX_VARIABLE_NUMBER is
# 999. Builds since 3.32.0 raise the default to 32766, but we don't rely on
# that -- 900 stays safely under both, and under any host this ever runs on.
SQLITE_MAX_VARS = 900


class QueryError(Exception):
    """A query could not be satisfied as given (e.g. bad FTS5 syntax).

    Tool error text is prompt text: a raw sqlite3.OperationalError like
    "fts5: syntax error near ..." is exactly the wrong string to hand a
    model. Callers should catch this and surface `str(exc)` verbatim --
    it is already written to be actionable.
    """


def _placeholders(allowed_repo_ids: Sequence[int]) -> tuple[str, list[int]]:
    """Validate the allowlist and render it as SQL placeholders.

    A str is explicitly rejected: it is a Sequence, so accepting it would let
    ``find_symbol("all", ...)`` silently degrade into a per-character filter.
    """
    if isinstance(allowed_repo_ids, (str, bytes)) or not isinstance(
        allowed_repo_ids, (list, tuple, set, frozenset)
    ):
        raise TypeError(
            "allowed_repo_ids must be a list/tuple/set of int repo ids, "
            f"got {type(allowed_repo_ids).__name__}"
        )
    ids = list(allowed_repo_ids)
    if any(not isinstance(i, int) or isinstance(i, bool) for i in ids):
        raise TypeError("allowed_repo_ids must contain only int repo ids")
    return ",".join("?" for _ in ids), ids


def _chunks(ids: list[int], reserve: int) -> list[list[int]]:
    """Split the allowlist so no single statement exceeds SQLite's parameter limit.

    A developer in a large GitLab group can have an allowlist bigger than
    SQLite will accept as bound parameters in one statement. Under
    fail-closed semantics, letting that raise sqlite3.OperationalError is an
    availability bug wearing a security costume -- the caller cannot tell
    "the store broke" from "you are denied". Splitting into chunks and
    unioning the per-chunk results in Python keeps the query total instead of
    raising.

    `reserve` is the number of non-allowlist bound parameters the calling
    statement also uses (e.g. a name filter, a MATCH query string), so a
    chunk's ids never push the statement's total parameter count over the
    limit.
    """
    size = max(1, SQLITE_MAX_VARS - reserve)
    return [ids[i:i + size] for i in range(0, len(ids), size)] or [[]]


def find_symbol(allowed_repo_ids: Sequence[int], conn: sqlite3.Connection,
                name: str, kind: str | None = None,
                limit: int = 50) -> list[sqlite3.Row]:
    _, ids = _placeholders(allowed_repo_ids)
    if not ids:
        return []
    reserve = 2 if kind is not None else 1  # name [+ kind]
    rows: list[sqlite3.Row] = []
    for chunk in _chunks(ids, reserve):
        marks = ",".join("?" for _ in chunk)
        sql = (
            "SELECT s.repo_id, r.path_with_namespace, f.path, s.name, s.kind,"
            "       s.line, s.end_line, s.signature, s.scope, s.is_public"
            "  FROM symbols s"
            "  JOIN files f ON f.id = s.file_id"
            "  JOIN repos r ON r.id = s.repo_id"
            f" WHERE s.repo_id IN ({marks}) AND s.name = ?"
        )
        params: list = [*chunk, name]
        if kind is not None:
            sql += " AND s.kind = ?"
            params.append(kind)
        rows.extend(conn.execute(sql, params).fetchall())
    # LIMIT can only be pushed into each per-chunk statement if it's applied
    # again after the union -- otherwise chunk 1 could fill the limit with
    # rows that lose to chunk 2's under the real ordering. Sort the merged
    # set the same way the single-statement ORDER BY did, then slice once.
    rows.sort(key=lambda r: (-r["is_public"], r["path_with_namespace"], r["path"]))
    return rows[:limit]


def search_code(allowed_repo_ids: Sequence[int], conn: sqlite3.Connection,
                query: str, limit: int = 50) -> list[sqlite3.Row]:
    _, ids = _placeholders(allowed_repo_ids)
    if not ids:
        return []
    rows: list[sqlite3.Row] = []
    for chunk in _chunks(ids, reserve=1):  # query
        marks = ",".join("?" for _ in chunk)
        try:
            rows.extend(conn.execute(
                "SELECT f.repo_id, r.path_with_namespace, f.path,"
                "       files_fts.rank AS rank,"
                "       snippet(files_fts, 1, '[', ']', '…', 16) AS snippet"
                "  FROM files_fts"
                "  JOIN files f ON f.id = files_fts.rowid"
                "  JOIN repos r ON r.id = f.repo_id"
                f" WHERE files_fts MATCH ? AND f.repo_id IN ({marks})",
                [query, *chunk],
            ).fetchall())
        except sqlite3.OperationalError as exc:
            # A stray quote, an unbalanced NEAR(, or a bare AND/OR/NOT all
            # raise here. This is user input reaching FTS5's query parser,
            # not a bug -- wrap it so the string a model sees is actionable
            # instead of a raw SQLite parser error.
            raise QueryError(
                f"That search syntax is not valid ({exc}). Try plain terms "
                "without quotes or operators, e.g. DecodeFrame, or use "
                "regex=True."
            ) from exc
    rows.sort(key=lambda r: r["rank"])
    return rows[:limit]


def get_file(allowed_repo_ids: Sequence[int], conn: sqlite3.Connection,
             repo_id: int, path: str, max_bytes: int = 65536) -> dict | None:
    _, ids = _placeholders(allowed_repo_ids)
    # DELIBERATE, REVIEWED EXCEPTION to the design's "filter in SQL" rule.
    #
    # The other three queries filter with `WHERE repo_id IN (:allowed)` because
    # they return many rows and the allowlist is the only thing bounding them.
    # This is a point lookup: the caller already names one repo_id, so the check
    # is a single membership test, and doing it in Python has two advantages --
    # an oversized allowlist can never make a single-file fetch raise, and no
    # chunking loop is needed for a query that returns at most one row.
    #
    # The ordering is what makes this equivalent to the SQL form, so keep it:
    # the check runs BEFORE conn.execute, so a row belonging to a disallowed
    # repo is never fetched and then discarded. There is no window in which
    # unauthorised content exists in this process.
    #
    # Do not "restore consistency" by adding an IN (...) clause here -- that
    # reintroduces the parameter-limit failure this pattern exists to avoid.
    if repo_id not in ids:
        return None
    row = conn.execute(
        "SELECT f.repo_id, r.path_with_namespace, f.path, f.lang, f.size, f.content"
        "  FROM files f JOIN repos r ON r.id = f.repo_id"
        " WHERE f.repo_id = ? AND f.path = ?",
        [repo_id, path],
    ).fetchone()
    if row is None:
        return None
    content = row["content"]
    truncated = len(content) > max_bytes
    if truncated:
        content = content[:max_bytes]  # str slicing: always a character boundary
    return {
        "repo_id": row["repo_id"],
        "path_with_namespace": row["path_with_namespace"],
        "path": row["path"],
        "lang": row["lang"],
        "size": row["size"],
        "content": content,
        "truncated": truncated,
    }


def index_status(allowed_repo_ids: Sequence[int],
                 conn: sqlite3.Connection) -> list[sqlite3.Row]:
    _, ids = _placeholders(allowed_repo_ids)
    if not ids:
        return []
    rows: list[sqlite3.Row] = []
    for chunk in _chunks(ids, reserve=0):
        marks = ",".join("?" for _ in chunk)
        rows.extend(conn.execute(
            "SELECT r.id AS repo_id, r.path_with_namespace, r.last_indexed_sha,"
            "       r.last_indexed_at, r.last_run_timed_out, r.last_run_symbols_failed,"
            "       r.last_run_at, r.last_run_error,"
            "       (SELECT COUNT(*) FROM files   WHERE repo_id = r.id) AS files,"
            "       (SELECT COUNT(*) FROM symbols WHERE repo_id = r.id) AS symbols,"
            "       (SELECT COUNT(*) FROM index_errors WHERE repo_id = r.id) AS errors,"
            # index_queue.repo_id is a PRIMARY KEY: one row per repo, with the
            # queued paths JSON-packed into `reason`. COUNT(*) is therefore a 0/1
            # flag, not a count -- a repo with 4,000 stuck paths reported "1".
            # Count the packed paths instead. json_valid() guards a row whose
            # reason is not the payload (hand-written, or pre-dating the format):
            # json_extract would otherwise abort the entire status query with a
            # malformed-JSON error, and the outer COALESCE turns both "no queue
            # row" and "unreadable payload" into 0.
            "       COALESCE((SELECT CASE WHEN json_valid(reason)"
            "                             THEN json_array_length("
            "                                      json_extract(reason, '$.paths'))"
            "                        END"
            "                   FROM index_queue WHERE repo_id = r.id), 0)"
            "         AS queued_retries"
            "  FROM repos r"
            f" WHERE r.id IN ({marks})",
            chunk,
        ).fetchall())
    rows.sort(key=lambda r: r["path_with_namespace"])
    return rows


def find_references(allowed_repo_ids: Sequence[int], conn: sqlite3.Connection,
                    name: str, limit: int = 100) -> list[dict]:
    """Find lexical occurrences of `name` and flag the ones ctags knows as definitions.

    NAME-BASED, NOT SEMANTIC -- this is scope-blind textual matching, not a
    reference resolver, and that is a deliberate scope decision. ctags (the
    only thing in this index that runs a real parser) extracts *definitions*
    only; nothing here resolves an identifier occurrence back to the
    declaration it actually refers to. Concretely:

      - A returned row can be a real call, a comment that happens to mention
        the name, a string literal, or an unrelated identifier in another
        language that is spelled the same way. This function cannot tell
        those apart.
      - `is_definition=True` means "ctags recorded a symbol with this exact
        name at this exact file and line" -- it is not a claim that this is
        the *only* definition, or that any particular non-definition row
        calls it.
      - Calls made through a macro, a function pointer, or any indirection
        ctags does not see are invisible to this function -- there is
        nothing here to find them with.

    A real reference index needs a parser with scope resolution; that is
    out of scope for this phase. Treat every result as a lead to inspect,
    not a confirmed reference.

    Implementation: FTS5 (`files_fts`) shortlists candidate files cheaply,
    then each candidate's `files.content` is scanned line by line with a
    strict word-boundary regex (`\\bname\\b`) to produce line numbers and
    context. The word-boundary step is not optional -- FTS5's own
    tokenisation is not trusted to enforce identifier boundaries by itself,
    and a naive substring scan of a shortlisted file's lines would let
    `DecodeFrame` match a line that only contains `DecodeFrameV2`. A false
    hit here is worse than a miss: the agent calling this tool will repeat
    it with confidence.
    """
    _, ids = _placeholders(allowed_repo_ids)
    if not ids:
        return []

    pattern = re.compile(r"\b" + re.escape(name) + r"\b")
    fts_query = '"' + name.replace('"', '""') + '"'

    results: list[dict] = []
    for chunk in _chunks(ids, reserve=1):  # name (FTS MATCH string)
        marks = ",".join("?" for _ in chunk)
        try:
            file_rows = conn.execute(
                "SELECT f.id AS file_id, r.path_with_namespace AS repo,"
                "       f.path, f.content"
                "  FROM files_fts"
                "  JOIN files f ON f.id = files_fts.rowid"
                "  JOIN repos r ON r.id = f.repo_id"
                f" WHERE files_fts MATCH ? AND f.repo_id IN ({marks})",
                [fts_query, *chunk],
            ).fetchall()
        except sqlite3.OperationalError:
            # `name` contains characters FTS5's MATCH parser rejects even
            # quoted. This is a references lookup, not a raw search box --
            # there is no query syntax for a caller to fix, so degrade to
            # "no candidates in this chunk" instead of raising QueryError.
            file_rows = []

        for frow in file_rows:
            def_lines = {
                row["line"]
                for row in conn.execute(
                    "SELECT line FROM symbols WHERE file_id = ? AND name = ?",
                    (frow["file_id"], name),
                )
            }
            for lineno, line in enumerate(frow["content"].splitlines(), start=1):
                if pattern.search(line):
                    results.append({
                        "repo": frow["repo"],
                        "path": frow["path"],
                        "line": lineno,
                        "context": line,
                        "is_definition": lineno in def_lines,
                    })

    results.sort(key=lambda r: (r["repo"], r["path"], r["line"]))
    return results[:limit]
