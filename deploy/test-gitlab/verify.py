#!/usr/bin/env python3
"""End-to-end verification of Argus against a real GitLab.

Answers the two questions no unit test can, and that have been open since Phase 1:

  Q1  Does the service token actually SEE private projects?
      `gitlab.list_projects` calls /projects with membership=false. For a
      NON-ADMIN token that returns only PUBLIC projects -- so the index would
      silently cover a fraction of the estate and report success. This measures
      it against a real instance instead of reasoning about it.

  Q2  Can developer A read developer B's code through Argus?
      Every test so far proves the CODE filters. This proves the SYSTEM does,
      using two real GitLab personal access tokens against real membership.

Run after `seed.py`:

    python deploy/test-gitlab/verify.py

Exits non-zero if any assertion fails. Writes docs/verification-report.md.
"""
from __future__ import annotations

import json
import pathlib
import subprocess
import sys
import textwrap
import time

import httpx

HERE = pathlib.Path(__file__).resolve().parent
ROOT = HERE.parent.parent
SEEDED = HERE / "seeded.json"
REPORT = ROOT / "docs" / "verification-report.md"
WORK = HERE / "work"

results: list[tuple[str, bool, str]] = []


def check(name: str, ok: bool, detail: str = "") -> bool:
    results.append((name, ok, detail))
    print(f"  [{'PASS' if ok else 'FAIL'}] {name}" + (f" -- {detail}" if detail else ""))
    return ok


def main() -> int:
    if not SEEDED.exists():
        print(f"missing {SEEDED}; run seed.py first", file=sys.stderr)
        return 2
    seeded = json.loads(SEEDED.read_text(encoding="utf-8"))
    gitlab_url = seeded["gitlab_url"]
    admin = seeded["admin_token"]
    users = seeded["users"]
    projects = seeded["projects"]

    sys.path.insert(0, str(ROOT))
    from argus import acl, gitlab as gl
    from argus.config import Config, GitLabConfig, IndexConfig
    from argus.store.db import open_db, connect_readonly
    from argus.store import queries

    WORK.mkdir(exist_ok=True)
    cfg = Config(
        gitlab=GitLabConfig(url=gitlab_url, token=admin),
        index=IndexConfig(data_dir=WORK, db_path=WORK / "index.db"),
    )
    # `argus index` is invoked as a subprocess below, so it needs a real config
    # file rather than the in-process object.
    (WORK / "config.yaml").write_text(textwrap.dedent(f"""\
        gitlab:
          url: {gitlab_url}
          token: "{admin}"
        index:
          data_dir: {WORK.as_posix()}
          db_path: {(WORK / 'index.db').as_posix()}
        """), encoding="utf-8")

    # ---------------------------------------------------------------- Q1 ---
    print("\n== Q1: does the service token see PRIVATE projects? ==")
    seen = gl.list_projects(cfg.gitlab)
    seen_names = {p.path_with_namespace.split("/")[-1] for p in seen}
    expected = set(projects)
    check("service token enumerates every seeded private project",
          expected <= seen_names,
          f"expected {sorted(expected)}, saw {sorted(seen_names)}")

    # The same call with a NON-admin token is the failure mode the spec warned
    # about. Prove it differs, so the answer is measured rather than assumed.
    dev_name, dev = next(iter(users.items()))
    dev_cfg = GitLabConfig(url=gitlab_url, token=dev["token"])
    dev_seen = gl.list_projects(dev_cfg)
    check("a NON-admin token sees fewer projects (membership=false caveat is real)",
          len(dev_seen) < len(seen),
          f"admin saw {len(seen)}, {dev_name} saw {len(dev_seen)}")

    # ---------------------------------------------------------- index it ---
    print("\n== Indexing with the service token ==")
    t0 = time.time()
    proc = subprocess.run(
        [sys.executable, "-m", "argus.cli", "index", "--config", str(WORK / "config.yaml")],
        capture_output=True, text=True, cwd=ROOT, timeout=1800,
    )
    elapsed = time.time() - t0
    print(proc.stdout[-2000:])
    if proc.returncode != 0:
        print(proc.stderr[-2000:], file=sys.stderr)
    check("index run completed", proc.returncode == 0, f"{elapsed:.1f}s")

    conn = open_db(cfg.index.db_path)
    counts = {
        "repos": conn.execute("SELECT COUNT(*) c FROM repos").fetchone()["c"],
        "files": conn.execute("SELECT COUNT(*) c FROM files").fetchone()["c"],
        "symbols": conn.execute("SELECT COUNT(*) c FROM symbols").fetchone()["c"],
        "public_symbols": conn.execute(
            "SELECT COUNT(*) c FROM symbols WHERE is_public = 1").fetchone()["c"],
        "includes": conn.execute("SELECT COUNT(*) c FROM includes").fetchone()["c"],
    }
    print(f"  counts: {counts}")
    check("symbols were extracted", counts["symbols"] > 0)
    check("cross-repo includes were recorded", counts["includes"] > 0)

    # ---------------------------------------------------------------- Q2 ---
    print("\n== Q2: can developer A read developer B's code? ==")
    ro = connect_readonly(cfg.index.db_path)
    idents = {}
    for username, u in users.items():
        ident = acl.resolve(conn, cfg.gitlab, u["token"])
        idents[username] = ident
        names = [
            conn.execute("SELECT path_with_namespace p FROM repos WHERE id = ?", (rid,)
                         ).fetchone()["p"].split("/")[-1]
            for rid in ident.allowed_repo_ids
        ]
        print(f"  {username} -> {names}")
        check(f"{username}'s allowlist is exactly their one project",
              names == [u["member_of"]], f"got {names}")

    alpha, beta = idents["dev_alpha"], idents["dev_beta"]
    check("the two developers' allowlists are disjoint",
          not (set(alpha.allowed_repo_ids) & set(beta.allowed_repo_ids)))

    # driver-shim has no members at all. If it is reachable, the design failed.
    shim_id = conn.execute(
        "SELECT id FROM repos WHERE path_with_namespace LIKE '%driver-shim'").fetchone()
    if shim_id:
        sid = shim_id["id"]
        check("driver-shim is in NOBODY's allowlist",
              sid not in alpha.allowed_repo_ids and sid not in beta.allowed_repo_ids)
        check("get_file refuses driver-shim for dev_alpha",
              queries.get_file(alpha.allowed_repo_ids, ro, sid, "src/shim.c") is None)

    # DecodeFrame is defined in eal-core and called from BOTH other repos, so a
    # broken filter shows up as extra rows rather than as an error.
    # GUARD AGAINST A VACUOUS PASS. If indexing failed there are no symbols at
    # all, and every "never crosses the allowlist" assertion below is trivially
    # true against two empty sets -- the exact failure mode this project has hit
    # eight times. Refuse to report those as passes.
    indexed_ok = check(
        "index is non-empty, so the isolation checks below are meaningful",
        counts["symbols"] > 0,
        "no symbols indexed -- isolation assertions would pass vacuously")

    a_syms = queries.find_symbol(alpha.allowed_repo_ids, ro, "DecodeFrame")
    b_syms = queries.find_symbol(beta.allowed_repo_ids, ro, "DecodeFrame")
    if indexed_ok:
        check("DecodeFrame is actually findable by the repo that defines it",
              len(a_syms) > 0, f"alpha found {len(a_syms)}")
    a_repos = {r["path_with_namespace"] for r in a_syms}
    b_repos = {r["path_with_namespace"] for r in b_syms}
    print(f"  find_symbol DecodeFrame: alpha={sorted(a_repos)} beta={sorted(b_repos)}")
    check("find_symbol never crosses the allowlist", not (a_repos & b_repos) or not a_repos or not b_repos,
          f"alpha={sorted(a_repos)} beta={sorted(b_repos)}")

    a_refs = queries.find_references(alpha.allowed_repo_ids, ro, "DecodeFrame")
    b_refs = queries.find_references(beta.allowed_repo_ids, ro, "DecodeFrame")
    check("find_references never crosses the allowlist",
          not ({r["repo"] for r in a_refs} & {r["repo"] for r in b_refs}),
          f"alpha={sorted({r['repo'] for r in a_refs})} beta={sorted({r['repo'] for r in b_refs})}")

    check("an EMPTY allowlist returns nothing, not everything",
          queries.find_symbol([], ro, "DecodeFrame") == []
          and queries.find_references([], ro, "DecodeFrame") == [])

    # ------------------------------------------------------------- report ---
    passed = sum(1 for _, ok, _ in results if ok)
    total = len(results)
    lines = [
        "# Argus end-to-end verification",
        "",
        f"Run against a real GitLab CE at `{gitlab_url}`.",
        f"**{passed}/{total} checks passed.**",
        "",
        "## Index measurements",
        "",
        "| Metric | Value |",
        "|---|---|",
        f"| Full index wall-clock | {elapsed:.1f}s |",
    ] + [f"| {k} | {v} |" for k, v in counts.items()] + [
        "",
        "## Checks",
        "",
        "| Check | Result | Detail |",
        "|---|---|---|",
    ] + [f"| {n} | {'PASS' if ok else '**FAIL**'} | {d} |" for n, ok, d in results]
    REPORT.parent.mkdir(parents=True, exist_ok=True)
    REPORT.write_text("\n".join(lines) + "\n", encoding="utf-8")

    print(f"\n{passed}/{total} checks passed -> {REPORT.relative_to(ROOT)}")
    return 0 if passed == total else 1


if __name__ == "__main__":
    sys.exit(main())
