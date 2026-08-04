from __future__ import annotations

import argparse
import shutil
import subprocess
import sys
import time
from pathlib import Path

from .config import Config, ConfigError
from .embed import EmbeddingUnavailable
from .gitlab import GitLabError, list_projects
from .mcpsrv import DEFAULT_ALLOWED_HOSTS, create_app
from .mirror import GitError, ensure_mirror, head_sha, sync_worktree
from .packs import format as pack_format
from .packs import registry
from .packs.build import BuildError, build_pack, fetch_source
from .packs.registry import RegistryError
from .packs.sources import SOURCES
from .resolve import resolve_includes
from .store import queries, writes
from .store.db import open_db
from .store.graph import rebuild_repo_deps
from .worker import index_repo

DEFAULT_SERVE_HOST = "127.0.0.1"
DEFAULT_SERVE_PORT = 7700

#: Exit code for pack build/install/registry failures. Distinct from the
#: existing 2 (config), 3 (gitlab) and 4 (indexing) so a script can tell a
#: pack problem from an index one.
EXIT_PACK = 5


def preflight() -> str | None:
    """Return an error message if the environment cannot index, else None."""
    exe = shutil.which("ctags")
    if exe is None:
        return (
            "ctags not found on PATH. Install Universal Ctags:\n"
            "  Linux:   sudo apt install universal-ctags\n"
            "  Windows: winget install UniversalCtags.Ctags"
        )
    try:
        out = subprocess.run(
            [exe, "--version"], capture_output=True, text=True, timeout=10
        ).stdout
    except (OSError, subprocess.SubprocessError) as exc:
        return f"could not run ctags --version: {exc}"
    if "Universal Ctags" not in out:
        return (
            f"{exe} is not Universal Ctags (reported: {out.splitlines()[0] if out else '?'}).\n"
            "Exuberant Ctags has no --output-format=json and cannot be used."
        )
    return None


def _index(cfg: Config, only: str | None, reset_retries: bool = False) -> int:
    problem = preflight()
    if problem:
        print(problem, file=sys.stderr)
        return 4

    conn = open_db(cfg.index.db_path)

    projects = list_projects(cfg.gitlab)
    if only:
        projects = [p for p in projects if p.path_with_namespace == only]

    if reset_retries:
        # Explicit operator escape hatch: an automatic clear only fires once
        # a path indexes successfully again, which requires the underlying
        # cause (ACL, path length, AV quarantine) to already be fixed. This
        # lets an operator forget the history immediately instead of waiting
        # for that to happen on its own.
        if only:
            if not projects:
                # --repo was given but matched no known repo; don't clear anything
                print(f"repo '{only}' not found in projects from GitLab")
            else:
                cursor = conn.execute(
                    "DELETE FROM retry_attempts WHERE repo_id IN"
                    " (SELECT id FROM repos WHERE path_with_namespace = ?)",
                    (only,),
                )
                conn.commit()
                rows_cleared = cursor.rowcount
                print(f"reset retry counters for '{only}' ({rows_cleared} rows)")
        else:
            cursor = conn.execute("DELETE FROM retry_attempts")
            conn.commit()
            rows_cleared = cursor.rowcount
            if rows_cleared > 0:
                print(f"reset {rows_cleared} retry counter entries")
            else:
                print("no retry counters to reset")

    if not projects:
        print("no repos matched")
        return 0

    any_repo_unhealthy = False
    for project in projects:
        repo_id = writes.upsert_repo(
            conn, gitlab_id=project.gitlab_id,
            path_with_namespace=project.path_with_namespace,
            default_branch=project.default_branch, http_url=project.http_url,
        )
        old = conn.execute(
            "SELECT last_indexed_sha FROM repos WHERE id = ?", (repo_id,)
        ).fetchone()["last_indexed_sha"]

        started = time.time()
        try:
            mirror_dir = ensure_mirror(cfg.index, project,
                                       clone_url=project.http_url,
                                       token=cfg.gitlab.token)
            sha = head_sha(mirror_dir, project.default_branch)
            if sha == old:
                # index_repo is the only other writer of last-run state, and
                # this path never calls it. Without this, a repo polled every
                # hour for six months and correctly up to date every time
                # reported a six-month-old last_run_at -- indistinguishable
                # from one nothing has looked at since. Clearing the flags is
                # right here: a previous pass that timed out or lost ctags
                # held the SHA, so it could not have reached this branch.
                writes.record_run_state(conn, repo_id, timed_out=False,
                                        symbols_failed=False, ts=int(time.time()))
                print(f"{project.path_with_namespace}: up to date")
                continue
            tree = sync_worktree(cfg.index, project.gitlab_id, mirror_dir, sha)
            result = index_repo(conn, cfg.index, project, mirror_dir, tree, sha, old)
        except GitError as exc:
            any_repo_unhealthy = True
            writes.record_error(conn, repo_id, None, "git", str(exc), int(time.time()))
            # Record the failure rather than leaving the PREVIOUS pass's flags
            # and timestamp standing: a repo whose fetch has failed every run
            # for weeks otherwise showed as clean and freshly checked.
            writes.record_run_state(conn, repo_id, timed_out=False,
                                    symbols_failed=False, ts=int(time.time()),
                                    error=str(exc))
            print(f"{project.path_with_namespace}: FAILED ({exc})", file=sys.stderr)
            continue
        except Exception as exc:   # noqa: BLE001 - one bad repo must not end the run
            # Nothing caught a non-GitError escaping index_repo, so it aborted
            # the whole run: every repo after this one went unindexed, and it
            # happened after the retry queue had been read and before any run
            # state was recorded. Contain it to this repo and leave a record.
            any_repo_unhealthy = True
            writes.record_error(conn, repo_id, None, "index", repr(exc),
                                int(time.time()))
            writes.record_run_state(conn, repo_id, timed_out=False,
                                    symbols_failed=False, ts=int(time.time()),
                                    error=repr(exc))
            print(f"{project.path_with_namespace}: FAILED ({exc!r})", file=sys.stderr)
            continue

        if result.timed_out or result.symbols_failed:
            any_repo_unhealthy = True

        flags = ""
        if result.timed_out:
            flags += " TIMED-OUT"
        if result.symbols_failed:
            flags += " SYMBOLS-FAILED"
        print(
            f"{project.path_with_namespace}: indexed={result.indexed} "
            f"deleted={result.deleted} skipped={result.skipped} "
            f"errors={result.errors}{flags} "
            f"({time.time() - started:.1f}s)"
        )
    # One pass over the whole database, after every repo. An include can point
    # into a repo indexed later in this same cycle, so resolving per repo would
    # make the graph depend on indexing order.
    try:
        counts = resolve_includes(conn)
        edges = rebuild_repo_deps(conn)
    except Exception as exc:  # noqa: BLE001 - must not escape as an uncaught traceback
        # Nothing else catches this: `main` handles only ConfigError and
        # GitLabError, so an uncaught error here -- most notably
        # sqlite3.IntegrityError from rebuild_repo_deps's FK on
        # repo_deps.to_repo_id, raised whenever an include still points at a
        # repo deleted since the last pass -- would discard the whole
        # per-repo run summary printed above and exit via a raw traceback.
        # That traceback carries no exit code of its own, so it cannot be
        # told apart from `return 1` below ("ran, but a repo is unhealthy")
        # by a caller checking $?. Report it the same way a per-repo failure
        # is reported and reuse exit code 4: this is a failure of the run
        # itself, the same category as a missing ctags binary, not a
        # per-repo health flag.
        print(f"resolve/rebuild failed: {exc!r}", file=sys.stderr)
        return 4
    print(f"includes: {counts.get('resolved', 0)} resolved, "
          f"{counts.get('external', 0)} external, "
          f"{counts.get('ambiguous', 0)} ambiguous, "
          f"{counts.get('not_found', 0)} not found")
    print(f"repo graph: {edges} cross-repo edges")

    # Exit codes 2/3/4 are already claimed (config, gitlab, preflight/resolve);
    # use a distinct code so a cron job can tell "ran, but a repo is
    # unhealthy" apart from those startup/run failures.
    return 1 if any_repo_unhealthy else 0


def _status(cfg: Config) -> int:
    conn = open_db(cfg.index.db_path)
    # Operator tool: pass the full known set explicitly rather than bypassing
    # the allowlist parameter. The ACL module arrives in Phase 2.
    all_ids = [r["id"] for r in conn.execute("SELECT id FROM repos")]
    rows = queries.index_status(all_ids, conn)
    if not rows:
        print("no repos indexed")
        return 0
    for row in rows:
        when = (
            time.strftime("%Y-%m-%d %H:%M", time.localtime(row["last_indexed_at"]))
            if row["last_indexed_at"] else "never"
        )
        sha = (row["last_indexed_sha"] or "-")[:8]
        flags = ""
        if row["last_run_timed_out"]:
            flags += " TIMED-OUT"
        if row["last_run_symbols_failed"]:
            flags += " SYMBOLS-FAILED"
        if row["last_run_error"]:
            flags += f" RUN-FAILED({row['last_run_error'][:80]})"
        print(
            f"{row['path_with_namespace']:<40} sha={sha} at={when} "
            f"files={row['files']} symbols={row['symbols']} errors={row['errors']} "
            f"queued_retries={row['queued_retries']}{flags}"
        )
    return 0


def _resolve(cfg: Config) -> int:
    conn = open_db(cfg.index.db_path)
    try:
        counts = resolve_includes(conn)
        edges = rebuild_repo_deps(conn)
    finally:
        conn.close()
    for state in ("resolved", "external", "ambiguous", "not_found"):
        print(f"{state:<12} {counts.get(state, 0)}")
    print(f"{'edges':<12} {edges}")
    return 0


def _serve(cfg: Config, host: str, port: int, allowed_hosts: list[str] | None) -> int:
    """Build the MCP app and run it, bound to ``host``/``port``.

    Binds localhost (`DEFAULT_SERVE_HOST`) unless the operator passes
    `--host` explicitly -- the server trusts the auth gate for identity, not
    the network perimeter, so it must never default to a wildcard bind.
    `docs/deployment.md` puts Caddy in front for TLS; this process is meant
    to be reached only through that proxy or a loopback-only tunnel.

    `allowed_hosts` (from repeatable `--allowed-host`) is threaded into
    `create_app` so it lands in `transport_security.allowed_hosts` *at
    construction* -- FastMCP's DNS-rebinding Host-header allowlist is
    computed once, when the app object is built, and is never revisited when
    `app.settings.host` is reassigned below. A reverse proxy (Caddy) forwards
    the client's real Host header (e.g. `argus.internal`), not this
    process's own bind host, so leaving the allowlist at its loopback-only
    default behind such a proxy makes every real `/mcp` call 421. `None`
    (the flag not given) reproduces that original loopback-only default
    unchanged -- see `argus.mcpsrv.server._build_transport_security`.
    """
    app = create_app(cfg, allowed_hosts=allowed_hosts)
    app.settings.host = host
    app.settings.port = port
    app.run(transport="streamable-http")
    return 0


def _flush_acl(cfg: Config, user: str | None) -> int:
    """Delete cached ACL resolutions so a GitLab revocation takes effect now.

    Without this the only way to revoke access faster than the 600s TTL
    (`argus.acl.TTL_SECONDS`) is restarting the service. `--user` scopes the
    delete to one GitLab username (acl_cache can hold more than one row per
    user -- one per distinct token they've authenticated with); omitted, it
    clears every cached identity.

    Mirrors `_index`'s `--reset-retries` distinction between "the thing you
    named doesn't exist" and "there was nothing to clear": a `--user` that
    matches no cached row is reported by name, separately from the
    zero-rows-cleared message a bare `flush-acl` prints when the cache is
    already empty -- an operator chasing a stale revocation needs to know
    which of those happened.
    """
    conn = open_db(cfg.index.db_path)
    if user:
        cursor = conn.execute("DELETE FROM acl_cache WHERE username = ?", (user,))
        conn.commit()
        rows_cleared = cursor.rowcount
        if rows_cleared == 0:
            print(f"user '{user}' not in acl cache; nothing cleared")
        else:
            print(f"cleared acl cache for '{user}' ({rows_cleared} rows)")
    else:
        cursor = conn.execute("DELETE FROM acl_cache")
        conn.commit()
        rows_cleared = cursor.rowcount
        if rows_cleared > 0:
            print(f"cleared {rows_cleared} acl cache entries")
        else:
            print("no acl cache entries to clear")
    return 0



# ---------------------------------------------------------------------------
# argus pack ...
# ---------------------------------------------------------------------------

def _packs_dir(args) -> Path:
    """Resolve where packs live, from --packs-dir or --config.

    Both are accepted, and --packs-dir is why the public tooling works on its
    own. `Config.load` requires a GitLab URL and token; demanding those before
    someone can install a public documentation pack would be absurd for a
    corpus whose whole point is that anyone can use it.
    """
    if getattr(args, "packs_dir", None):
        return Path(args.packs_dir)
    if getattr(args, "config", None):
        return Config.load(args.config).packs_dir
    raise ConfigError("pass --packs-dir or --config to say where packs live")


def _describe(pack) -> str:
    flag = "" if pack.compatible else "  [INCOMPATIBLE]"
    size_mb = pack.size_bytes / (1024 * 1024)
    return (f"{pack.name:<16} {pack.version:<10} {pack.embedding_model:<20} "
            f"{size_mb:>8.1f} MB  {pack.license}{flag}")


def _pack_build(args) -> int:
    source_cls = SOURCES.get(args.source)
    if source_cls is None:
        print(f"unknown source {args.source!r}; known: {', '.join(sorted(SOURCES))}",
              file=sys.stderr)
        return EXIT_PACK
    source = source_cls()

    work_dir = Path(args.work_dir)
    commit = None
    if args.fetch:
        print(f"fetching {source.repo_url} ({source.branch}) into {work_dir} ...")
        commit = fetch_source(source, work_dir)

    print(f"building {source.name} pack from {work_dir} ...")
    try:
        out = build_pack(
            source, work_dir=work_dir, out_path=Path(args.out),
            version=args.version, source_commit=commit or args.commit,
        )
    except EmbeddingUnavailable as exc:
        # Separate from BuildError because the fix is elsewhere: start Ollama
        # and pull the model, then rerun.
        print(f"embedding failed: {exc}", file=sys.stderr)
        print("is ollama running, and has the model been pulled?", file=sys.stderr)
        return EXIT_PACK

    conn = pack_format.open_pack(out)
    try:
        meta = pack_format.read_meta(conn)
    finally:
        conn.close()
    print(f"wrote {out} ({out.stat().st_size / (1024 * 1024):.1f} MB)")
    print(f"  docs {meta.get('doc_count')}  chunks {meta.get('chunk_count')}  "
          f"symbols {meta.get('symbol_count')} "
          f"(unresolved {meta.get('unresolved_symbol_count')})")
    return 0


def _pack_list(args) -> int:
    packs = registry.list_installed(_packs_dir(args))
    if not packs:
        # Not an error: an empty registry is a normal state, and a non-zero
        # exit would break any script that lists before installing.
        print("no packs installed")
        return 0
    print(f"{'NAME':<16} {'VERSION':<10} {'MODEL':<20} {'SIZE':>11}  LICENSE")
    for pack in packs:
        print(_describe(pack))
    return 0


def _pack_install(args) -> int:
    dest = _packs_dir(args)
    installed = registry.install(args.source, dest_dir=dest,
                                expected_sha256=args.sha256)
    print(f"installed {installed.name} {installed.version} -> {installed.path}")
    if not installed.compatible:
        print(f"warning: {installed.incompatible_reason}", file=sys.stderr)
        print("lookup and text search still work; semantic search does not.",
              file=sys.stderr)
    return 0


def _pack_info(args) -> int:
    dest = _packs_dir(args)
    matches = [p for p in registry.list_installed(dest) if p.name == args.name]
    if not matches:
        print(f"no installed pack named {args.name!r} in {dest}", file=sys.stderr)
        return EXIT_PACK
    pack = matches[0]
    meta = pack_format.read_meta(pack_format.open_pack(pack.path))

    print(f"name          {pack.name}")
    print(f"version       {pack.version}")
    print(f"path          {pack.path}")
    print(f"size          {pack.size_bytes / (1024 * 1024):.1f} MB")
    print(f"model         {pack.embedding_model} ({pack.embedding_dim}d)")
    print(f"compatible    {'yes' if pack.compatible else 'no'}")
    if not pack.compatible:
        print(f"              {pack.incompatible_reason}")
    print(f"source        {meta.get('source_repo', '')}")
    print(f"branch        {meta.get('source_branch', '')}")
    print(f"commit        {pack.source_commit}")
    print(f"docs          {meta.get('doc_count', '?')}")
    print(f"chunks        {meta.get('chunk_count', '?')}")
    print(f"symbols       {meta.get('symbol_count', '?')}")
    # This output is how a user meets the redistribution obligation, so the
    # licence and attribution are printed in full and never truncated.
    print()
    print(f"license       {pack.license}")
    print(f"license url   {meta.get('license_url', '')}")
    print("attribution")
    print(f"  {pack.attribution}")
    return 0


def _pack_remove(args) -> int:
    dest = _packs_dir(args)
    if registry.remove(args.name, dest):
        print(f"removed {args.name}")
        return 0
    print(f"no installed pack named {args.name!r} in {dest}", file=sys.stderr)
    return EXIT_PACK


def _pack_update(args) -> int:
    dest = _packs_dir(args)
    available = {entry.name: entry for entry in registry.fetch_index(args.index_url)}
    installed = registry.list_installed(dest)
    if args.name:
        installed = [p for p in installed if p.name == args.name]
        if not installed:
            print(f"no installed pack named {args.name!r} in {dest}", file=sys.stderr)
            return EXIT_PACK

    updated = 0
    for pack in installed:
        entry = available.get(pack.name)
        if entry is None:
            print(f"{pack.name}: not in the index, leaving alone")
            continue
        if entry.version == pack.version:
            print(f"{pack.name}: {pack.version} is current")
            continue
        print(f"{pack.name}: {pack.version} -> {entry.version}, downloading ...")
        registry.install(entry.url, dest_dir=dest, expected_sha256=entry.sha256)
        updated += 1
    print(f"{updated} pack(s) updated")
    return 0


def _pack(args) -> int:
    return {
        "build": _pack_build, "list": _pack_list, "install": _pack_install,
        "info": _pack_info, "remove": _pack_remove, "update": _pack_update,
    }[args.pack_command](args)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="argus")
    sub = parser.add_subparsers(dest="command", required=True)

    p_index = sub.add_parser("index", help="Mirror and index repositories")
    p_index.add_argument("--config", required=True, type=Path)
    p_index.add_argument("--repo", help="Index only this path_with_namespace")
    p_index.add_argument("--reset-retries", action="store_true",
                         help="Clear retry counters before indexing (manual recovery only; do not use on a schedule)")

    p_status = sub.add_parser("status", help="Show per-repo index freshness")
    p_status.add_argument("--config", required=True, type=Path)

    p_resolve = sub.add_parser(
        "resolve", help="Re-resolve includes and rebuild the dependency graph")
    p_resolve.add_argument("--config", required=True, type=Path)

    p_serve = sub.add_parser("serve", help="Run the MCP retrieval server")
    p_serve.add_argument("--config", required=True, type=Path)
    p_serve.add_argument("--host", default=DEFAULT_SERVE_HOST,
                         help=f"Bind address (default: {DEFAULT_SERVE_HOST})")
    p_serve.add_argument("--port", type=int, default=DEFAULT_SERVE_PORT,
                         help=f"Bind port (default: {DEFAULT_SERVE_PORT})")
    p_serve.add_argument(
        "--allowed-host", action="append", dest="allowed_hosts", metavar="HOST",
        help=(
            "Host header value the DNS-rebinding check will accept on /mcp "
            "(repeatable). Default: the loopback set "
            f"({', '.join(DEFAULT_ALLOWED_HOSTS)}) -- unchanged from a bare "
            "`argus serve`. A reverse-proxied deployment (docs/deployment.md) "
            "MUST pass the proxy-facing hostname Caddy forwards, e.g. "
            "--allowed-host argus.internal, or every /mcp call is rejected "
            "with 421 Invalid Host Header. Passing --allowed-host replaces "
            "the default set entirely rather than adding to it; pass it more "
            "than once to allow more than one hostname."
        ),
    )

    p_flush_acl = sub.add_parser(
        "flush-acl", help="Clear cached ACL resolutions ahead of their TTL"
    )
    p_flush_acl.add_argument("--config", required=True, type=Path)
    p_flush_acl.add_argument("--user", help="Only clear this GitLab username's cache entries")

    p_pack = sub.add_parser("pack", help="Build, install and inspect knowledge packs")
    pack_sub = p_pack.add_subparsers(dest="pack_command", required=True)

    def _where(parser: argparse.ArgumentParser) -> None:
        # Either is enough. --packs-dir keeps the public tooling usable without
        # a GitLab URL and token, which Config.load would otherwise demand.
        parser.add_argument("--config", type=Path, help="Read packs.dir from this config")
        parser.add_argument("--packs-dir", type=Path, help="Directory holding installed packs")

    p_build = pack_sub.add_parser("build", help="Build a pack from a documentation source")
    p_build.add_argument("--source", required=True,
                         help=f"One of: {', '.join(sorted(SOURCES))}")
    p_build.add_argument("--work-dir", required=True, type=Path,
                         help="Checkout of the source repository")
    p_build.add_argument("--out", required=True, type=Path, help="Pack file to write")
    p_build.add_argument("--version", required=True, help="Version to record in the pack")
    p_build.add_argument("--commit", help="Source commit, if work-dir is not a git checkout")
    p_build.add_argument("--fetch", action="store_true",
                         help="Clone or update the source into --work-dir first")

    p_plist = pack_sub.add_parser("list", help="List installed packs")
    _where(p_plist)

    p_pinstall = pack_sub.add_parser("install", help="Install a pack from a path or URL")
    p_pinstall.add_argument("source", help="Pack file path or https URL")
    p_pinstall.add_argument("--sha256", help="Expected SHA-256; install is refused on mismatch")
    _where(p_pinstall)

    p_pinfo = pack_sub.add_parser(
        "info", help="Show provenance, licence and attribution for an installed pack")
    p_pinfo.add_argument("name")
    _where(p_pinfo)

    p_premove = pack_sub.add_parser("remove", help="Remove an installed pack")
    p_premove.add_argument("name")
    _where(p_premove)

    p_pupdate = pack_sub.add_parser("update", help="Update installed packs from an index")
    p_pupdate.add_argument("name", nargs="?", help="Only this pack (default: all)")
    p_pupdate.add_argument("--index-url", required=True, help="Published pack index JSON")
    _where(p_pupdate)

    args = parser.parse_args(argv)

    # Handled before Config.load: pack commands may run with only --packs-dir,
    # and loading a full config would demand GitLab credentials they never use.
    if args.command == "pack":
        try:
            return _pack(args)
        except ConfigError as exc:
            print(f"config error: {exc}", file=sys.stderr)
            return 2
        except (BuildError, RegistryError, GitError) as exc:
            print(f"pack error: {exc}", file=sys.stderr)
            return EXIT_PACK

    try:
        cfg = Config.load(args.config)
    except (ConfigError, OSError) as exc:
        print(f"config error: {exc}", file=sys.stderr)
        return 2

    try:
        if args.command == "index":
            return _index(cfg, args.repo, args.reset_retries)
        if args.command == "serve":
            return _serve(cfg, args.host, args.port, args.allowed_hosts)
        if args.command == "flush-acl":
            return _flush_acl(cfg, args.user)
        if args.command == "resolve":
            return _resolve(cfg)
        return _status(cfg)
    except GitLabError as exc:
        print(f"gitlab error: {exc}", file=sys.stderr)
        return 3


if __name__ == "__main__":
    raise SystemExit(main())
