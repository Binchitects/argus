from __future__ import annotations

import re
import sqlite3
from collections.abc import Sequence

from .. import whichrepo
from ..whichrepo import Shape

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
            "       (SELECT COUNT(*) FROM includes WHERE repo_id = r.id"
            "         AND resolution = 'resolved')  AS includes_resolved,"
            "       (SELECT COUNT(*) FROM includes WHERE repo_id = r.id"
            "         AND resolution = 'external')  AS includes_external,"
            "       (SELECT COUNT(*) FROM includes WHERE repo_id = r.id"
            "         AND resolution = 'ambiguous') AS includes_ambiguous,"
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


def repo_map(allowed_repo_ids: Sequence[int], conn: sqlite3.Connection,
             repo_id: int) -> dict:
    """Dependencies and dependents of `repo_id`, filtered to the allowlist.

    `repo_deps` is a global graph, but a caller may only learn about repos
    they can already see. An edge to a repo outside the allowlist is dropped
    entirely rather than reported anonymously -- "depends on 1 repo you cannot
    see" is itself a disclosure.
    """
    _, ids = _placeholders(allowed_repo_ids)
    if not ids or repo_id not in set(ids):
        return {}

    row = conn.execute(
        "SELECT id, path_with_namespace FROM repos WHERE id = ?", (repo_id,)
    ).fetchone()
    if row is None:
        return {}

    def edges(sql: str) -> list[dict]:
        out: list[dict] = []
        for chunk in _chunks(list(ids), 1):
            marks = ",".join("?" for _ in chunk)
            out.extend(
                {"repo_id": r["other_id"],
                 "path_with_namespace": r["path_with_namespace"],
                 "weight": r["weight"]}
                for r in conn.execute(sql.format(marks=marks), (repo_id, *chunk))
            )
        out.sort(key=lambda e: (-e["weight"], e["path_with_namespace"]))
        return out

    return {
        "repo": {"repo_id": row["id"],
                 "path_with_namespace": row["path_with_namespace"]},
        "depends_on": edges(
            "SELECT d.to_repo_id AS other_id, r.path_with_namespace, d.weight"
            "  FROM repo_deps d JOIN repos r ON r.id = d.to_repo_id"
            " WHERE d.from_repo_id = ? AND d.to_repo_id IN ({marks})"),
        "depended_on_by": edges(
            "SELECT d.from_repo_id AS other_id, r.path_with_namespace, d.weight"
            "  FROM repo_deps d JOIN repos r ON r.id = d.from_repo_id"
            " WHERE d.to_repo_id = ? AND d.from_repo_id IN ({marks})"),
    }


#: Per-shape weights. Not magic numbers: each is defended by a test above that
#: fails if it changes materially. A diff or stack trace names files outright,
#: so lexical overlap would only add noise; prose inverts that.
_WEIGHTS: dict[str, dict[str, float]] = {
    Shape.DIFF:   {"direct": 1.0, "lexical": 0.0, "central": 0.0},
    Shape.STACK:  {"direct": 1.0, "lexical": 0.1, "central": 0.0},
    Shape.SYMBOL: {"direct": 1.0, "lexical": 0.2, "central": 0.0},
    Shape.PROSE:  {"direct": 0.5, "lexical": 1.0, "central": 0.3},
}

#: A repo qualifies with any direct hit, or a lexical score at least this
#: fraction of the best repo's. Below it, the answer is "nothing matched".
_FLOOR_RATIO = 0.35


def which_repo(allowed_repo_ids: Sequence[int], conn: sqlite3.Connection,
               description: str, limit: int = 5) -> list[dict]:
    """Rank repos a change probably belongs in, with the evidence for each.

    Returns [] rather than a ranked list of weak matches when nothing clears
    the evidence floor: a list looks like an answer, and the caller acts on
    the top row.

    Every score component here is computed from allowed repos only, and is
    unaffected by content in repos outside the allowlist -- including the
    lexical term, which counts matching files per allowed repo directly
    (`_lexical_match_counts`) rather than reusing `search_code`'s bm25-ranked
    top-N. bm25's IDF is computed over the whole `files_fts` table, so
    ranking-and-slicing first would let a hidden repo's match volume decide
    which *allowed* rows survive the cut -- up to erasing a visible repo from
    the result entirely. See `_lexical_match_counts` for the fix.

    The semantic term is absent, not zero-weighted by accident -- Phase 4 adds
    it. Diffs, stack traces and symbols do not depend on it at all.
    """
    _, ids = _placeholders(allowed_repo_ids)
    if not ids or not description.strip():
        return []

    allowed = set(ids)
    shape = whichrepo.detect_shape(description)
    weights = _WEIGHTS[shape]

    direct: dict[int, list[str]] = {}
    lexical: dict[int, float] = {}

    for path in whichrepo.extract_paths(description):
        for row in _files_named(conn, allowed, path):
            direct.setdefault(row["repo_id"], []).append(
                f"file {row['path']}")

    for name in whichrepo.extract_symbols(description)[:10]:
        for row in find_symbol(list(allowed), conn, name, limit=20):
            direct.setdefault(row["repo_id"], []).append(
                f"{row['kind']} {row['name']} at {row['path']}:{row['line']}")

    if weights["lexical"]:
        # `description` is handed to FTS5 verbatim. A stack trace or diff
        # routinely contains "/", ":", "(" -- characters FTS5's MATCH parser
        # rejects even though they are perfectly ordinary in a path. That is
        # a syntax problem with treating free text as a query, not evidence
        # that nothing matches, so a malformed query degrades to "no lexical
        # evidence" instead of aborting the whole ranking. Direct evidence
        # (paths, symbols) is extracted separately above and is unaffected.
        try:
            counts = _lexical_match_counts(conn, allowed, description)
            for repo_id, n in counts.items():
                lexical[repo_id] = float(n)
        except QueryError:
            # Deliberate, not defensive noise: swallowing this is exactly the
            # degrade-to-"no lexical evidence" behaviour described above. All
            # that is lost is this one input among several -- direct hits
            # (paths, symbols) and centrality are computed independently and
            # still rank the repo normally.
            pass

    if not direct and not lexical:
        return []

    best_lex = max(lexical.values(), default=0.0) or 1.0
    centrality = _in_degree(conn, allowed) if weights["central"] else {}
    max_central = max(centrality.values(), default=0) or 1

    scored: list[dict] = []
    for repo_id in allowed:
        hits = direct.get(repo_id, [])
        lex = lexical.get(repo_id, 0.0) / best_lex
        if not hits and lex < _FLOOR_RATIO:
            continue

        score = weights["direct"] * min(len(hits), 5) / 5.0 + weights["lexical"] * lex
        # Only inferred evidence is penalised. A repo named outright in a diff
        # or a stack frame is never punished for being widely depended upon.
        if not hits:
            score -= weights["central"] * (centrality.get(repo_id, 0) / max_central)

        if score <= 0:
            continue
        why = hits[:5] or [f"lexical match on {lexical.get(repo_id, 0):.0f} file(s)"]
        scored.append({
            "repo_id": repo_id,
            "path_with_namespace": _repo_name(conn, repo_id),
            "confidence": round(min(score, 1.0), 3),
            "shape": shape,
            "why": why,
        })

    scored.sort(key=lambda r: (-r["confidence"], r["path_with_namespace"]))
    return scored[:limit]


def _escape_like(value: str) -> str:
    """Escape a value bound into a LIKE pattern so it matches only itself.

    SQLite's LIKE treats a bare `_` or `%` in the *bound value*, not only in
    literal pattern text, as a wildcard -- `_` matches any one character and
    `%` matches any run of characters. Snake_case filenames are near-
    universal in C and Python, so an unescaped query for `unique_beta.c`
    would also match a sibling file like `uniqueXbeta.c` in a different
    repo. Escape the backslash first, or the backslashes just inserted in
    front of `_`/`%` would themselves be escaped on the next pass.
    """
    return value.replace("\\", "\\\\").replace("_", "\\_").replace("%", "\\%")


def _files_named(conn, allowed: set[int], path: str) -> list[sqlite3.Row]:
    rows = []
    escaped = _escape_like(path)
    for chunk in _chunks(list(allowed), 2):  # path (equality) + path (LIKE)
        marks = ",".join("?" for _ in chunk)
        rows.extend(conn.execute(
            f"SELECT repo_id, path FROM files WHERE repo_id IN ({marks})"
            "  AND (path = ? OR path LIKE '%/' || ? ESCAPE '\\')",
            (*chunk, path, escaped)).fetchall())
    return rows


def _lexical_match_counts(conn, allowed: set[int], query: str) -> dict[int, int]:
    """Per-repo count of files matching `query` in FTS5, restricted to `allowed`.

    A direct COUNT(*) ... GROUP BY repo_id, not `search_code`'s bm25-ranked
    top-N. `which_repo` only ever wants "how many allowed files matched, per
    repo" -- a match is a match, so which one ranks higher within a repo is
    irrelevant to that question, and computing bm25 at all would drag in its
    IDF, which is computed over the *entire* files_fts table including repos
    outside `allowed`. Content the caller cannot see would then be able to
    change which allowed rows survive a rank-and-slice -- as far as making an
    allowed repo disappear from the result once a hidden repo accumulates
    enough matches to shift the IDF far enough. A plain per-repo count never
    ranks anything, so it cannot be swayed by rows it never looks at.

    Chunked like the other allowlist-filtered queries in this module (the
    query string is the one reserved non-allowlist parameter per chunk).
    Raises `QueryError` on invalid FTS5 syntax, exactly like `search_code` --
    a diff or stack trace can still produce a MATCH expression FTS5 rejects,
    and the caller (`which_repo`) already knows how to degrade that into "no
    lexical evidence" instead of failing the whole query.
    """
    ids = list(allowed)
    if not ids:
        return {}
    counts: dict[int, int] = {}
    for chunk in _chunks(ids, reserve=1):  # query
        marks = ",".join("?" for _ in chunk)
        try:
            rows = conn.execute(
                "SELECT f.repo_id AS repo_id, COUNT(*) AS n"
                "  FROM files_fts"
                "  JOIN files f ON f.id = files_fts.rowid"
                f" WHERE files_fts MATCH ? AND f.repo_id IN ({marks})"
                " GROUP BY f.repo_id",
                [query, *chunk],
            ).fetchall()
        except sqlite3.OperationalError as exc:
            raise QueryError(
                f"That search syntax is not valid ({exc}). Try plain terms "
                "without quotes or operators, e.g. DecodeFrame, or use "
                "regex=True."
            ) from exc
        for row in rows:
            counts[row["repo_id"]] = counts.get(row["repo_id"], 0) + row["n"]
    return counts


def _in_degree(conn, allowed: set[int]) -> dict[int, int]:
    """In-degree of each allowed repo, counting only edges whose SOURCE is
    also in the allowlist.

    This used to aggregate `repo_deps` globally and filter only the target
    (`to_repo_id`) endpoint. An edge FROM a repo the caller cannot see still
    inflated a visible repo's in-degree under that scheme -- and in-degree
    feeds `which_repo`'s centrality subtraction for Shape.PROSE, so a caller
    could infer "some repo I cannot see depends on this one", and roughly how
    many, purely from a confidence delta. That is exactly the disclosure
    `repo_map` deliberately refuses to make (see its docstring). Constraining
    BOTH endpoints to the allowlist closes it.

    As a side effect this also means no join to `repos` is needed: a
    dangling `from_repo_id` (a repo deleted since the last graph rebuild)
    can never pass an IN (...) membership test against a *live* allowlist.

    Chunked on both endpoints, the same way `repo_map`'s `edges()` helper
    chunks a single endpoint -- except here two IN (...) clauses share the
    same host-parameter budget, so each side's chunk size is halved. Chunks
    partition the allowlist, so summing counts across the chunk x chunk grid
    double-counts nothing: each edge's (from, to) pair falls in exactly one
    (from_chunk, to_chunk) cell.
    """
    ids = list(allowed)
    if not ids:
        return {}
    chunks = _chunks(ids, SQLITE_MAX_VARS // 2)
    counts: dict[int, int] = {}
    for from_chunk in chunks:
        from_marks = ",".join("?" for _ in from_chunk)
        for to_chunk in chunks:
            to_marks = ",".join("?" for _ in to_chunk)
            for row in conn.execute(
                "SELECT to_repo_id, COUNT(*) AS n FROM repo_deps"
                f" WHERE from_repo_id IN ({from_marks})"
                f"   AND to_repo_id IN ({to_marks})"
                " GROUP BY to_repo_id",
                [*from_chunk, *to_chunk],
            ):
                counts[row["to_repo_id"]] = counts.get(row["to_repo_id"], 0) + row["n"]
    return counts


def _repo_name(conn, repo_id: int) -> str:
    row = conn.execute("SELECT path_with_namespace FROM repos WHERE id = ?",
                       (repo_id,)).fetchone()
    return row["path_with_namespace"] if row else ""
