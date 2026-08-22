"""What breaks when the index holds dozens of repositories instead of six?

The baseline and scale corpora measure throughput. Neither can measure the
thing that only appears at estate scale: **a symbol name stops being unique**.
`ERR_clear_error` is one symbol in a six-repo index and several in a fifty-repo
one, and every tool that returns "the" definition has to decide which.

Three questions, none answerable from a small corpus:

1. **How much collision is there really?** Reported rather than assumed --
   the interesting number is what fraction of symbols a caller could not
   disambiguate by name alone.
2. **Does `find_symbol` still return the right definition first**, for names
   that exist in exactly one repo and for names that exist in many?
3. **Does `which_repo` route a description to the right repository**, which
   is the tool whose whole purpose is answering "where does this belong"
   and which cannot be exercised meaningfully below this scale.

    python evals/run_estate.py --config deploy/test-gitlab/argus-host.yaml
"""
from __future__ import annotations

import argparse
import pathlib
import sys
import time

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))

from argus.config import Config
from argus.store import queries
from argus.store.db import connect_readonly

#: (description, repo that should win). Each names behaviour the repo is FOR,
#: without naming the repo, a file in it, or a symbol from it -- otherwise the
#: lexical term answers it and the routing is never tested.
ROUTING = [
    ("parse and validate a TLS certificate chain", "openssl"),
    ("store key-value pairs in memory with expiry", "redis"),
    ("decode a JPEG image into a pixel buffer", "libjpeg-turbo"),
    ("compress a byte stream with a dictionary", "zstd"),
    ("render glyphs from a scalable font file", "freetype"),
    ("execute a SQL query against a relational database", "postgres"),
    ("transfer a file over HTTP from the command line", "curl"),
    ("shape complex text for right-to-left scripts", "harfbuzz"),
    ("match a string against a regular expression", "pcre2"),
    ("serialise structured data into a compact binary form", "protobuf"),
]


def corpus_stats(conn, allowed: list[int]) -> dict:
    repos = conn.execute("SELECT count(*) FROM repos").fetchone()[0]
    files = conn.execute("SELECT count(*) FROM files").fetchone()[0]
    symbols = conn.execute("SELECT count(*) FROM symbols").fetchone()[0]
    return {"repos": repos, "files": files, "symbols": symbols}


def collision_stats(conn) -> dict:
    """How often one symbol name means several different things.

    Counted over distinct (name, repo) pairs rather than raw rows: a symbol
    declared in a header and defined in a source file is one thing named
    twice, and counting rows would report that as a collision.
    """
    row = conn.execute("""
        WITH per_name AS (
            SELECT s.name AS name, COUNT(DISTINCT f.repo_id) AS repos
              FROM symbols s JOIN files f ON f.id = s.file_id
             GROUP BY s.name
        )
        SELECT COUNT(*) AS names,
               SUM(CASE WHEN repos > 1 THEN 1 ELSE 0 END) AS shared,
               MAX(repos) AS worst
          FROM per_name
    """).fetchone()
    names, shared, worst = row["names"], row["shared"] or 0, row["worst"] or 0
    top = conn.execute("""
        SELECT s.name AS name, COUNT(DISTINCT f.repo_id) AS repos
          FROM symbols s JOIN files f ON f.id = s.file_id
         GROUP BY s.name ORDER BY repos DESC, s.name LIMIT 5
    """).fetchall()
    return {"names": names, "shared": shared, "worst": worst,
            "share": shared / names if names else 0.0,
            "top": [(r["name"], r["repos"]) for r in top]}


def routing(conn, allowed: list[int], detail: bool) -> tuple[int, int]:
    hits = 0
    asked = 0
    for description, expected in ROUTING:
        rows = queries.which_repo(allowed, conn, description, limit=5)
        if not rows:
            if detail:
                print(f"    [{expected}] no answer -- {description!r}")
            asked += 1
            continue
        asked += 1
        top = str(rows[0]["path_with_namespace"])
        if expected in top:
            hits += 1
        elif detail:
            got = [f'{r["path_with_namespace"]}({r["confidence"]})'
                   for r in rows[:3]]
            print(f"    [{expected}] got {got} -- {description!r}")
    return hits, asked


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--config", required=True, type=pathlib.Path)
    ap.add_argument("--detail", action="store_true")
    args = ap.parse_args()

    cfg = Config.load(args.config)
    conn = connect_readonly(cfg.index.db_path)
    allowed = [r[0] for r in conn.execute("SELECT id FROM repos")]

    stats = corpus_stats(conn, allowed)
    print(f"corpus: {stats['repos']} repos, {stats['files']:,} files, "
          f"{stats['symbols']:,} symbols\n")

    col = collision_stats(conn)
    print(f"  distinct symbol names   : {col['names']:,}")
    print(f"  names in >1 repo        : {col['shared']:,} ({col['share']:.1%})")
    print(f"  most-shared name spans  : {col['worst']} repos")
    for name, n in col["top"]:
        print(f"      {name[:40]:42s} {n} repos")

    started = time.time()
    hits, asked = routing(conn, allowed, args.detail)
    print(f"\n  which_repo routing      : {hits}/{asked} "
          f"({hits / asked:.0%})" if asked else "")
    print(f"  ({time.time() - started:.1f}s for {asked} questions)")
    conn.close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
