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

    python scripts/test-gitlab/verify.py

Exits non-zero if any assertion fails. Writes docs/argus/verification-report.md.
"""
from __future__ import annotations

import json
import os
import pathlib
import subprocess
import sys
import textwrap
import time

import httpx

HERE = pathlib.Path(__file__).resolve().parent
ROOT = HERE.parent.parent
SEEDED = HERE / "seeded.json"
# `docs/argus/`, not `docs/`. The report was moved into the per-area tree when
# the repository was restructured into src-layout, and this constant was not
# moved with it -- so a run wrote a second, new report to the old path and the
# tracked one silently stopped updating. Nothing failed; the report just went
# stale, which is the worst way for a document to break.
REPORT = ROOT / "docs" / "argus" / "verification-report.md"

# Where the mirrors and the index live. Overridable because the default is
# INSIDE the checkout, and SQLite in WAL mode cannot open its shared-memory
# file on some bind-mounted filesystems (measured: an NTFS checkout, where
# `argus index` dies with "disk I/O error" before indexing anything). Point
# this at a native path -- or a Docker volume -- on such a host:
#
#   ARGUS_TEST_WORK=/var/lib/argus-test-work python scripts/test-gitlab/verify.py
#
# `scripts/test-gitlab/run.sh` does exactly that, so the one-command path works
# on every host rather than only on the ones with a friendly filesystem.
WORK = pathlib.Path(os.environ.get("ARGUS_TEST_WORK") or (HERE / "work"))

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
    # Overridable, because "where the fixture GitLab is" depends on where this
    # runs from. On the host network it is `http://localhost:8929` (what seed.py
    # records); on `llm-net`, alongside the stack, the same instance is
    # `http://host.docker.internal:8929`. Hardcoding the first meant the
    # verification could only ever run from one position, which is how it ended
    # up being a script somebody ran by hand from one particular directory.
    gitlab_url = os.environ.get("ARGUS_TEST_GITLAB_URL") or seeded["gitlab_url"]
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

    # ------------------------------------------------- a second branch -----
    # One project is indexed at TWO refs, which is the only way to verify that
    # an unqualified question answers from trunk rather than from whichever
    # branch happened to be indexed last -- the failure that makes a developer
    # working on a release branch act on code that is not the code they have.
    print("\n== Indexing the release branch ==")
    t0 = time.time()
    proc = subprocess.run(
        [sys.executable, "-m", "argus.cli", "index", "--config",
         str(WORK / "config.yaml"), "--branch", "v2"],
        capture_output=True, text=True, cwd=ROOT, timeout=1800,
    )
    branch_elapsed = time.time() - t0
    if proc.returncode != 0:
        print(proc.stderr[-1500:], file=sys.stderr)
    check("the release branch was indexed alongside trunk",
          proc.returncode == 0, f"{branch_elapsed:.1f}s")

    # The cross-repo graph, which a second branch must not break. This is the
    # check that would have caught the bug it was written after: a project
    # indexed at two refs puts a shared header in the index twice, the include
    # resolver saw two equally plausible files and gave up, and every edge into
    # that project disappeared -- `ambiguous` for every include of the header,
    # with nothing anywhere reporting a problem. Measured: 2 edges became 0.
    print("\n== Cross-repo graph ==")
    # Its own read-only connection: `conn` is opened further down, for the
    # counts, and reaching for it here would be an UnboundLocalError at the
    # exact moment this check is supposed to report on the graph.
    edges_conn = connect_readonly(cfg.index.db_path)
    try:
        edges = edges_conn.execute(
            "SELECT COUNT(*) AS n FROM repo_deps").fetchone()["n"]
        ambiguous = edges_conn.execute(
            "SELECT COUNT(*) AS n FROM includes WHERE resolution = 'ambiguous'"
        ).fetchone()["n"]
    finally:
        edges_conn.close()
    check("the cross-repo graph survived indexing a second branch", edges > 0,
          f"{edges} edge(s); a shared header indexed at two refs must resolve")
    # The symptom, named separately: the resolver recording `ambiguous` for a
    # header that exists at two refs is what emptied the graph above.
    check("no include was left ambiguous by the second branch", ambiguous == 0,
          f"{ambiguous} ambiguous include(s)")

    # ------------------------------------------------------- embed it ------
    # Without this the vector half of the index is EMPTY, and `semantic_search`
    # answers "The index is unavailable; do not retry this query." -- an honest
    # refusal, but one that meant the semantic path had never been exercised
    # here at all. Cheap now that the embedder is on the GPU: measured at about
    # 5 ms per symbol, so this fixture's seventy cost under half a second.
    #
    # Not fatal if the embedder is unreachable. A fixture run without Ollama is
    # still a valid ACL verification; it is simply one that cannot say anything
    # about search, and saying so is the point.
    print("\n== Embedding (needs a reachable Ollama) ==")
    t0 = time.time()
    proc = subprocess.run(
        [sys.executable, "-m", "argus.cli", "embed", "--config", str(WORK / "config.yaml")],
        capture_output=True, text=True, cwd=ROOT, timeout=1800,
    )
    embed_elapsed = time.time() - t0
    embedded = proc.returncode == 0
    print(proc.stdout[-1000:])
    if not embedded:
        print(proc.stderr[-1000:], file=sys.stderr)
    check("the vector index was built, so semantic_search can be verified",
          embedded,
          f"{embed_elapsed:.1f}s" if embedded else
          f"embed failed: {proc.stderr.strip().splitlines()[-1][:120] if proc.stderr.strip() else 'see output'}"
          f" -- set ARGUS_OLLAMA_URL to a reachable embedder to close this")

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
        # A SET of project names, not a list of repo rows. The allowlist is a
        # set of `repos` ids, and a project indexed at two branches has two
        # rows -- both of which that developer is entitled to. Comparing to a
        # one-element list would report "got ['eal-core', 'eal-core']" as a
        # leak, which is the opposite of what it is.
        check(f"{username}'s allowlist is exactly their one project",
              set(names) == {u["member_of"]}, f"got {sorted(set(names))}")

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
    #
    # GUARD AGAINST A VACUOUS PASS. If indexing failed there are no symbols at
    # all, and every "never crosses the allowlist" assertion below is trivially
    # true against empty sets -- the exact failure mode this project has hit
    # eight times.
    #
    # The first version of this guard was itself the bug. It printed its FAILURE
    # message as the detail line on success ("no symbols indexed -- isolation
    # assertions would pass vacuously" next to a green PASS), and it skipped one
    # check while letting the rest run and pass against nothing. A guard that
    # says the assertions are meaningless and then reports them as passes is
    # worse than no guard, because the green line is what gets read.
    #
    # So the vacuity flag is folded into every check it applies to: when the
    # index is empty each of them FAILS, which is the honest answer -- none of
    # them has been demonstrated.
    symbols_indexed = counts["symbols"] > 0
    check("index is non-empty, so the isolation checks below are meaningful",
          symbols_indexed,
          f"{counts['symbols']} symbols indexed" if symbols_indexed else
          "no symbols indexed -- the isolation checks below cannot be demonstrated")

    def not_vacuous(condition: bool, detail: str) -> tuple[bool, str]:
        return (symbols_indexed and condition,
                detail if symbols_indexed else "VACUOUS: nothing was indexed")

    a_syms = queries.find_symbol(alpha.allowed_repo_ids, ro, "DecodeFrame")
    b_syms = queries.find_symbol(beta.allowed_repo_ids, ro, "DecodeFrame")
    a_repos = {r["path_with_namespace"].rsplit("/", 1)[-1] for r in a_syms}
    b_repos = {r["path_with_namespace"].rsplit("/", 1)[-1] for r in b_syms}
    print(f"  find_symbol DecodeFrame: alpha={sorted(a_repos)} beta={sorted(b_repos)}")

    # Not "the two sets are disjoint" -- that is true when either is EMPTY,
    # which is precisely how an over-restrictive filter hides. alpha must
    # actually find it, and neither may name a repository outside its own
    # allowlist.
    ok, detail = not_vacuous(
        bool(a_repos) and a_repos <= {"eal-core"} and b_repos <= {"etl-decoder"},
        f"alpha={sorted(a_repos)} (want eal-core) beta={sorted(b_repos)} "
        f"(want a subset of etl-decoder)")
    check("find_symbol returns only what the caller may read, and finds it",
          ok, detail)

    a_refs = queries.find_references(alpha.allowed_repo_ids, ro, "DecodeFrame")
    b_refs = queries.find_references(beta.allowed_repo_ids, ro, "DecodeFrame")
    a_ref_repos = {r["repo"].rsplit("/", 1)[-1] for r in a_refs}
    b_ref_repos = {r["repo"].rsplit("/", 1)[-1] for r in b_refs}
    ok, detail = not_vacuous(
        bool(a_ref_repos) and bool(b_ref_repos)
        and a_ref_repos <= {"eal-core"} and b_ref_repos <= {"etl-decoder"},
        f"alpha={sorted(a_ref_repos)} beta={sorted(b_ref_repos)}")
    check("find_references returns only what the caller may read, and finds it",
          ok, detail)

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
    # `newline="\n"` explicitly, not the platform default. `write_text` with
    # newline=None translates to `os.linesep`, so this file's line endings
    # flipped depending on whether the run happened on Windows or Linux -- and
    # because it is a TRACKED file, every run on the "other" OS produced a
    # 70-line whole-file diff of text that had not changed. A generated file
    # whose diff is always meaningless is a generated file nobody reviews.
    with REPORT.open("w", encoding="utf-8", newline="\n") as fh:
        fh.write("\n".join(lines) + "\n")

    print(f"\n{passed}/{total} checks passed -> {REPORT.relative_to(ROOT)}")
    return 0 if passed == total else 1


if __name__ == "__main__":
    sys.exit(main())
