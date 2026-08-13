"""Where does the single-SQLite design actually break?

The case for moving to Postgres is concurrency, not per-query speed: a
`find_symbol` costs 3.4 ms and no engine change makes that matter inside a
multi-second agent turn. What could matter is many developers querying while
the indexer writes, because SQLite allows one writer at a time.

So this measures the thing that would justify the migration, rather than the
thing that would not. Two dimensions:

  read scaling    N concurrent readers, p50/p95/throughput at each N
  writer contention   the same, with a writer committing throughout, which is
                      what an indexing run looks like to a query

Run against the live server so the numbers include the whole path -- ACL
resolution, the threadpool hop, audit writes -- not just SQLite.

    python evals/run_load_test.py --url http://127.0.0.1:8099/mcp --token ...
"""
from __future__ import annotations

import argparse
import asyncio
import json
import statistics
import time

QUERIES = [
    ("find_symbol", {"name": "SharedName"}),
    ("find_symbol", {"name": "main"}),
    ("search_code", {"query": "retry backoff"}),
    ("index_status", {}),
    ("which_repo", {"description": "certificate chain verification"}),
]


async def one_client(url: str, token: str, rounds: int, out: list[float],
                     errors: list[str]) -> None:
    from mcp import ClientSession
    from mcp.client.streamable_http import streamablehttp_client

    try:
        async with streamablehttp_client(
                url, headers={"Authorization": f"Bearer {token}"}
        ) as (read, write, _):
            async with ClientSession(read, write) as session:
                await session.initialize()
                for i in range(rounds):
                    name, args = QUERIES[i % len(QUERIES)]
                    started = time.perf_counter()
                    try:
                        await session.call_tool(name, args)
                        out.append((time.perf_counter() - started) * 1000)
                    except Exception as exc:
                        errors.append(f"{name}: {type(exc).__name__}")
    except Exception as exc:
        errors.append(f"connect: {type(exc).__name__}: {str(exc)[:80]}")


async def writer_load(db_path: str, stop: asyncio.Event, commits: list[int]) -> None:
    """Commit to the index continuously -- what an indexing run looks like.

    Writes to a scratch table rather than to real rows: the point is to hold
    the write lock repeatedly, not to corrupt the index being measured.
    """
    import sqlite3

    conn = sqlite3.connect(db_path, timeout=30)
    conn.execute("CREATE TABLE IF NOT EXISTS _loadtest (id INTEGER PRIMARY KEY, v TEXT)")
    conn.commit()
    n = 0
    try:
        while not stop.is_set():
            conn.execute("INSERT INTO _loadtest (v) VALUES (?)", (f"row-{n}",))
            conn.commit()
            n += 1
            await asyncio.sleep(0.005)
    finally:
        conn.execute("DROP TABLE IF EXISTS _loadtest")
        conn.commit()
        conn.close()
        commits.append(n)


async def measure(url: str, token: str, clients: int, rounds: int,
                  db_path: str | None) -> dict:
    latencies: list[float] = []
    errors: list[str] = []
    commits: list[int] = []
    stop = asyncio.Event()

    writer = None
    if db_path:
        writer = asyncio.create_task(writer_load(db_path, stop, commits))

    started = time.perf_counter()
    await asyncio.gather(*[
        one_client(url, token, rounds, latencies, errors) for _ in range(clients)
    ])
    elapsed = time.perf_counter() - started

    if writer:
        stop.set()
        await writer

    ok = len(latencies)
    return {
        "clients": clients,
        "calls": ok,
        "errors": len(errors),
        "error_kinds": sorted(set(errors))[:3],
        "elapsed_s": round(elapsed, 2),
        "throughput_rps": round(ok / elapsed, 1) if elapsed else 0,
        "p50_ms": round(statistics.median(latencies), 1) if ok else None,
        "p95_ms": round(statistics.quantiles(latencies, n=20)[18], 1) if ok > 20 else None,
        "max_ms": round(max(latencies), 1) if ok else None,
        "writer_commits": commits[0] if commits else 0,
    }


async def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--url", default="http://127.0.0.1:8099/mcp")
    ap.add_argument("--token", required=True)
    ap.add_argument("--rounds", type=int, default=10)
    ap.add_argument("--db", default=None,
                    help="index.db path; when given, a writer runs throughout")
    args = ap.parse_args()

    print(f"Argus load test -- {args.url}\n")
    rows = []
    for clients in (1, 2, 4, 8, 16):
        result = await measure(args.url, args.token, clients, args.rounds, None)
        rows.append(result)
        print(f"  readers={result['clients']:3}  calls={result['calls']:4}  "
              f"err={result['errors']:3}  {result['throughput_rps']:7.1f} req/s  "
              f"p50={result['p50_ms']}ms  p95={result['p95_ms']}ms  "
              f"max={result['max_ms']}ms")
        if result["error_kinds"]:
            print(f"        errors: {result['error_kinds']}")

    if args.db:
        print("\n  --- with a writer committing throughout (an indexing run) ---")
        for clients in (4, 16):
            result = await measure(args.url, args.token, clients, args.rounds, args.db)
            result["with_writer"] = True
            rows.append(result)
            print(f"  readers={result['clients']:3}  calls={result['calls']:4}  "
                  f"err={result['errors']:3}  {result['throughput_rps']:7.1f} req/s  "
                  f"p50={result['p50_ms']}ms  p95={result['p95_ms']}ms  "
                  f"writer_commits={result['writer_commits']}")
            if result["error_kinds"]:
                print(f"        errors: {result['error_kinds']}")

    with open("evals/load-test-results.json", "w", encoding="utf-8") as handle:
        json.dump(rows, handle, indent=2)
    print("\n  results: evals/load-test-results.json")


if __name__ == "__main__":
    asyncio.run(main())
