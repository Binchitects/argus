"""Materialize the cross-repo dependency graph.

`repo_deps` is rebuilt wholesale rather than maintained incrementally. It is
small -- one row per ordered repo pair that actually shares a header -- and
incremental maintenance of a derived graph is a bug farm for no gain: a missed
deletion leaves a phantom edge that nothing will ever notice.
"""

from __future__ import annotations

import sqlite3

from ..resolve import Resolution


def _edge_rows(conn: sqlite3.Connection) -> list[tuple[int, int, int]]:
    """(from_repo, to_repo, distinct including files) for cross-repo edges."""
    return [
        (row["from_repo_id"], row["to_repo_id"], row["weight"])
        for row in conn.execute(
            "SELECT repo_id AS from_repo_id, resolved_repo_id AS to_repo_id,"
            "       COUNT(DISTINCT file_id) AS weight"
            "  FROM includes"
            " WHERE resolution = ?"
            "   AND resolved_repo_id IS NOT NULL"
            "   AND resolved_repo_id != repo_id"
            " GROUP BY repo_id, resolved_repo_id",
            (Resolution.RESOLVED,),
        )
    ]


def rebuild_repo_deps(conn: sqlite3.Connection) -> int:
    """Replace `repo_deps` from the resolved includes. Returns the edge count.

    The delete and the insert share one transaction, so a failure part-way
    leaves the previous graph rather than a mixture of two passes.
    """
    rows = _edge_rows(conn)
    with conn:  # commits on success, rolls back on exception
        conn.execute("DELETE FROM repo_deps")
        conn.executemany(
            "INSERT INTO repo_deps (from_repo_id, to_repo_id, weight) "
            "VALUES (?, ?, ?)", rows,
        )
    return len(rows)
