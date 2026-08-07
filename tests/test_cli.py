import subprocess
import types
from pathlib import Path

import pytest
from mcp.server.fastmcp import FastMCP

from argus import cli
from argus.gitlab import Project
from argus.mcpsrv import DEFAULT_ALLOWED_HOSTS


def git(cwd, *args):
    subprocess.run(["git", *args], cwd=cwd, check=True, capture_output=True, text=True)


@pytest.fixture
def origin(tmp_path):
    repo = tmp_path / "origin"
    repo.mkdir()
    git(repo, "init", "-b", "main")
    git(repo, "config", "user.email", "t@t.test")
    git(repo, "config", "user.name", "Test")
    (repo / "a.c").write_text("int Alpha(void){return 1;}\n")
    git(repo, "add", "-A")
    git(repo, "commit", "-m", "first")
    return repo


@pytest.fixture
def config_file(tmp_path, origin):
    path = tmp_path / "config.yaml"
    path.write_text(
        f"gitlab:\n  url: https://gl.test\n  token: tok\n"
        f"index:\n  data_dir: {(tmp_path / 'data').as_posix()}\n"
        f"  db_path: {(tmp_path / 'data' / 'i.db').as_posix()}\n"
    )
    return path


@pytest.fixture
def fake_projects(monkeypatch, origin):
    project = Project(gitlab_id=42, path_with_namespace="g/eal",
                      default_branch="main", http_url=str(origin))
    monkeypatch.setattr(cli, "list_projects", lambda cfg: [project])
    return [project]


def test_index_then_status(config_file, fake_projects, capsys):
    assert cli.main(["index", "--config", str(config_file)]) == 0
    assert cli.main(["status", "--config", str(config_file)]) == 0
    out = capsys.readouterr().out
    assert "g/eal" in out
    assert "files=1" in out


def test_index_single_repo_filter(config_file, fake_projects, capsys):
    assert cli.main(
        ["index", "--config", str(config_file), "--repo", "g/nope"]
    ) == 0
    assert "no repos matched" in capsys.readouterr().out


def test_status_on_empty_index_is_not_an_error(config_file, capsys):
    assert cli.main(["status", "--config", str(config_file)]) == 0
    assert "no repos indexed" in capsys.readouterr().out


def test_bad_config_path_returns_nonzero(capsys):
    assert cli.main(["status", "--config", "/nonexistent/c.yaml"]) == 2


def test_preflight_passes_with_universal_ctags():
    """This host has Universal Ctags installed, so preflight must be silent."""
    assert cli.preflight() is None


def test_index_refuses_to_run_without_ctags(config_file, fake_projects,
                                            monkeypatch, capsys):
    monkeypatch.setattr(cli.shutil, "which", lambda name: None)
    assert cli.main(["index", "--config", str(config_file)]) == 4
    err = capsys.readouterr().err
    assert "ctags not found" in err
    assert "UniversalCtags.Ctags" in err


def test_index_returns_nonzero_when_a_repo_fails(config_file, tmp_path,
                                                 monkeypatch, capsys):
    bad_project = Project(gitlab_id=99, path_with_namespace="g/bad",
                          default_branch="main",
                          http_url=str(tmp_path / "does-not-exist"))
    monkeypatch.setattr(cli, "list_projects", lambda cfg: [bad_project])

    assert cli.main(["index", "--config", str(config_file)]) == 1
    assert "FAILED" in capsys.readouterr().err


def test_index_runs_resolve_before_rebuild(config_file, fake_projects, monkeypatch):
    """resolve_includes must run before rebuild_repo_deps inside `_index`
    itself -- not just in the `resolve` subcommand, which
    test_resolve_subcommand_runs_both_passes_and_reports_counts pins
    separately (it monkeypatches the same names but only exercises `_resolve`,
    so it cannot catch a swap that is local to `_index`).

    rebuild_repo_deps raises sqlite3.IntegrityError (FK on
    repo_deps.to_repo_id) whenever an include still points at a repo deleted
    since the last pass; resolving first clears those rows. Swapping the two
    lines inside `_index` turns a deleted repo into an uncaught crash of the
    whole `argus index` run -- this test pins the call order, not just that
    both get called.
    """
    calls = []
    monkeypatch.setattr(cli, "resolve_includes",
                        lambda conn: (calls.append("resolve"), {})[1])
    monkeypatch.setattr(cli, "rebuild_repo_deps",
                        lambda conn: (calls.append("graph"), 0)[1])

    assert cli.main(["index", "--config", str(config_file)]) == 0
    assert calls == ["resolve", "graph"]


def test_index_resolve_rebuild_failure_is_contained(config_file, fake_projects,
                                                     monkeypatch, capsys):
    """A failure in the resolve/rebuild pass must not escape as an uncaught
    traceback, and must not be reported as exit code 1 (the code that means
    "ran, but a repo is unhealthy") -- that would tell an operator the wrong
    thing happened. `main` catches only ConfigError and GitLabError, and
    nothing else wrapped resolve_includes/rebuild_repo_deps before, so this
    is the only thing standing between an IntegrityError in
    rebuild_repo_deps (see its docstring: an include still pointing at a
    repo deleted since the last pass) and a crashed `argus index` run.
    """
    monkeypatch.setattr(cli, "rebuild_repo_deps",
                        lambda conn: (_ for _ in ()).throw(
                            __import__("sqlite3").IntegrityError("FOREIGN KEY constraint failed")))

    result = cli.main(["index", "--config", str(config_file)])
    assert result == 4, f"expected exit code 4 (run failure), got {result}"
    err = capsys.readouterr().err
    assert "resolve/rebuild failed" in err


def _repo_row(config_file, path_with_namespace="g/eal"):
    from argus.store.db import open_db

    conn = open_db(config_file.parent / "data" / "i.db")
    try:
        return conn.execute(
            "SELECT last_run_at, last_run_error, last_run_timed_out,"
            "       last_run_symbols_failed"
            "  FROM repos WHERE path_with_namespace = ?",
            (path_with_namespace,),
        ).fetchone()
    finally:
        conn.close()


def test_up_to_date_repo_still_refreshes_last_run_at(config_file, fake_projects,
                                                     capsys):
    """A repo correctly polled and current must not read as stale.

    `if sha == old: continue` skips index_repo entirely, and index_repo is
    the only thing that wrote last_run_at -- so a repo that has been checked
    every hour for six months and needed no work reported a six-month-old
    last-run timestamp.
    """
    from argus.store.db import open_db

    assert cli.main(["index", "--config", str(config_file)]) == 0

    conn = open_db(config_file.parent / "data" / "i.db")
    conn.execute("UPDATE repos SET last_run_at = 1")   # pretend it ran long ago
    conn.commit()
    conn.close()

    assert cli.main(["index", "--config", str(config_file)]) == 0
    assert "up to date" in capsys.readouterr().out
    assert _repo_row(config_file)["last_run_at"] > 1, (
        "an up-to-date repo was checked but its last-run state was never refreshed"
    )


def test_fetch_failure_is_recorded_and_cleared_on_recovery(
    config_file, fake_projects, monkeypatch, capsys
):
    """A repo whose fetch fails every run must not read as clean.

    The GitError branch left the previous pass's flags and a stale
    last_run_at in place, so `argus status` showed a repo that has not been
    reachable for weeks as healthy.
    """
    from argus.mirror import GitError

    real_ensure_mirror = cli.ensure_mirror
    broken = {"fetch": True}

    def maybe_refused(*args, **kwargs):
        if broken["fetch"]:
            raise GitError("fetch refused by remote")
        return real_ensure_mirror(*args, **kwargs)

    # A flag rather than monkeypatch.undo(): undo() would also roll back the
    # fake_projects fixture's list_projects patch and send the recovery run
    # at the real GitLab.
    monkeypatch.setattr(cli, "ensure_mirror", maybe_refused)
    assert cli.main(["index", "--config", str(config_file)]) == 1

    row = _repo_row(config_file)
    assert row["last_run_at"] is not None
    assert row["last_run_error"] and "fetch refused" in row["last_run_error"]

    capsys.readouterr()
    assert cli.main(["status", "--config", str(config_file)]) == 0
    assert "RUN-FAILED" in capsys.readouterr().out

    # Recovery must clear it, or the flag is worse than no flag at all.
    broken["fetch"] = False
    assert cli.main(["index", "--config", str(config_file)]) == 0
    assert _repo_row(config_file)["last_run_error"] is None


def test_unexpected_error_in_one_repo_does_not_abort_the_run(
    config_file, origin, monkeypatch, capsys
):
    """A non-GitError escaping index_repo was caught nowhere in _index.

    It aborted the entire run -- every repo after the failing one went
    unindexed -- after drain_retry_paths had already committed the queue
    deletion and before any run state was recorded.
    """
    from argus.store.db import open_db

    first = Project(gitlab_id=42, path_with_namespace="g/first",
                    default_branch="main", http_url=str(origin))
    second = Project(gitlab_id=43, path_with_namespace="g/second",
                     default_branch="main", http_url=str(origin))
    monkeypatch.setattr(cli, "list_projects", lambda cfg: [first, second])

    real_index_repo = cli.index_repo

    def flaky(conn, index_cfg, project, *args, **kwargs):
        if project.gitlab_id == first.gitlab_id:
            raise RuntimeError("simulated indexer defect")
        return real_index_repo(conn, index_cfg, project, *args, **kwargs)

    monkeypatch.setattr(cli, "index_repo", flaky)

    assert cli.main(["index", "--config", str(config_file)]) == 1

    conn = open_db(config_file.parent / "data" / "i.db")
    indexed = conn.execute(
        "SELECT COUNT(*) c FROM files f JOIN repos r ON r.id = f.repo_id"
        " WHERE r.path_with_namespace = 'g/second'"
    ).fetchone()["c"]
    assert indexed == 1, "the repo after the failing one was never indexed"
    messages = [r["message"] for r in conn.execute(
        "SELECT message FROM index_errors i JOIN repos r ON r.id = i.repo_id"
        " WHERE r.path_with_namespace = 'g/first'")]
    conn.close()
    assert any("simulated indexer defect" in m for m in messages), (
        "the unexpected failure left no record"
    )
    assert _repo_row(config_file, "g/first")["last_run_error"] is not None


def test_reset_retries_flag_clears_counters(config_file, fake_projects, capsys):
    from argus.store.db import open_db

    assert cli.main(["index", "--config", str(config_file)]) == 0

    # Insert a retry_attempts entry so there's something to clear
    conn = open_db(config_file.parent / "data" / "i.db")
    repo_id = conn.execute(
        "SELECT id FROM repos WHERE path_with_namespace = ?",
        ("g/eal",),
    ).fetchone()["id"]
    conn.execute(
        "INSERT INTO retry_attempts (repo_id, path, attempts) VALUES (?, ?, ?)",
        (repo_id, "some/path.c", 1),
    )
    conn.commit()
    conn.close()

    assert cli.main(["index", "--config", str(config_file), "--reset-retries"]) == 0
    out = capsys.readouterr().out
    # Message should indicate retry counters were reset
    assert "reset" in out.lower() and "counter" in out.lower()


def test_index_refuses_exuberant_ctags(config_file, fake_projects,
                                       monkeypatch, capsys):
    """Exuberant Ctags has no --output-format=json; it must be rejected."""
    monkeypatch.setattr(cli.shutil, "which", lambda name: "/usr/bin/ctags")
    monkeypatch.setattr(
        cli.subprocess, "run",
        lambda *a, **k: types.SimpleNamespace(stdout="Exuberant Ctags 5.9~svn\n"),
    )
    assert cli.main(["index", "--config", str(config_file)]) == 4
    assert "not Universal Ctags" in capsys.readouterr().err


def test_reset_retries_uncommitted_if_list_projects_fails(
    config_file, fake_projects, monkeypatch, capsys
):
    """If list_projects fails after reset, retry_attempts must stay unchanged."""
    from argus.gitlab import GitLabError
    from argus.store.db import open_db
    from argus.store import writes

    # Index once to establish baseline
    assert cli.main(["index", "--config", str(config_file)]) == 0
    conn = open_db(config_file.parent / "data" / "i.db")

    # Get repo_id and insert a retry_attempts entry
    repo_id = conn.execute(
        "SELECT id FROM repos WHERE path_with_namespace = ?",
        ("g/eal",),
    ).fetchone()["id"]
    conn.execute(
        "INSERT INTO retry_attempts (repo_id, path, attempts) VALUES (?, ?, ?)",
        (repo_id, "some/path.c", 2),
    )
    conn.commit()

    # Verify the entry was added
    count_before = conn.execute(
        "SELECT COUNT(*) as cnt FROM retry_attempts WHERE repo_id = ?"
        " AND path = 'some/path.c'",
        (repo_id,),
    ).fetchone()["cnt"]
    assert count_before == 1
    conn.close()

    # Monkeypatch list_projects to raise GitLabError
    def failing_list_projects(cfg):
        raise GitLabError("network error")

    monkeypatch.setattr(cli, "list_projects", failing_list_projects)

    # Run with --reset-retries; it should fail with exit code 3
    assert (
        cli.main(["index", "--config", str(config_file), "--reset-retries"])
        == 3
    )

    # Verify retry_attempts still has the entry
    conn = open_db(config_file.parent / "data" / "i.db")
    count_after = conn.execute(
        "SELECT COUNT(*) as cnt FROM retry_attempts WHERE repo_id = ?"
        " AND path = 'some/path.c'",
        (repo_id,),
    ).fetchone()["cnt"]
    assert count_after == 1, "retry_attempts should not be cleared when list_projects fails"

    # Verify success message was not printed
    out = capsys.readouterr().out
    assert (
        "reset retry counters" not in out
    ), "success message should not be printed when list_projects fails"
    conn.close()


def _seed_retry_attempt(config_file, path="some/path.c", attempts=1):
    from argus.store.db import open_db

    conn = open_db(config_file.parent / "data" / "i.db")
    repo_id = conn.execute(
        "SELECT id FROM repos WHERE path_with_namespace = ?", ("g/eal",),
    ).fetchone()["id"]
    conn.execute(
        "INSERT INTO retry_attempts (repo_id, path, attempts) VALUES (?, ?, ?)",
        (repo_id, path, attempts),
    )
    conn.commit()
    conn.close()
    return repo_id


def _surviving_attempts(config_file, path="some/path.c"):
    from argus.store.db import open_db

    conn = open_db(config_file.parent / "data" / "i.db")
    try:
        row = conn.execute(
            "SELECT attempts FROM retry_attempts WHERE path = ?", (path,)
        ).fetchone()
        return row["attempts"] if row else None
    finally:
        conn.close()


def test_reset_retries_with_mistyped_repo_clears_nothing(config_file,
                                                          fake_projects, capsys):
    """A --repo that matches nothing must name the repo AND clear nothing.

    The behaviour under test is that counters survive. The earlier version
    of this test asserted only on prose, through a
    "not found"/"no repos matched"/"did not match" disjunction that the
    pre-existing `print("no repos matched")` satisfies for ANY empty
    projects list, flag or no flag -- and it inserted a retry_attempts row
    without ever checking it was still there.
    """
    assert cli.main(["index", "--config", str(config_file)]) == 0
    _seed_retry_attempt(config_file, attempts=2)

    assert cli.main(
        ["index", "--config", str(config_file), "--reset-retries",
         "--repo", "g/nope"]
    ) == 0

    assert _surviving_attempts(config_file) == 2, (
        "a mistyped --repo cleared retry counters it was never meant to touch"
    )
    out = capsys.readouterr().out
    assert "repo 'g/nope' not found" in out, (
        f"the unmatched repo must be named, not folded into a generic"
        f" 'no repos matched'; got: {out}"
    )
    assert "reset retry counters" not in out


def test_index_without_reset_retries_preserves_counters(config_file,
                                                         fake_projects, capsys):
    """--reset-retries must be opt-in.

    This replaces a test that asserted only on the flag's help prose -- a
    four-way OR in which "manual" alone passed, satisfied by wording rather
    than behaviour. Wiping accumulated retry history on every scheduled run
    is the actual hazard that warning exists for, so assert that instead.
    """
    assert cli.main(["index", "--config", str(config_file)]) == 0
    _seed_retry_attempt(config_file, attempts=2)

    assert cli.main(["index", "--config", str(config_file)]) == 0

    assert _surviving_attempts(config_file) == 2, (
        "an ordinary index run wiped retry history without being asked to"
    )
    assert "reset" not in capsys.readouterr().out.lower()

    # ...and the flag really is what clears them.
    assert cli.main(["index", "--config", str(config_file), "--reset-retries"]) == 0
    assert _surviving_attempts(config_file) is None


# --------------------------------------------------------------- serve ------

class _FakeMcpApp:
    """Stands in for `argus.mcpsrv.create_app`'s return value.

    Records the host/port assigned to `.settings` and whether/how `.run` was
    called, without ever binding a socket or blocking -- `serve` must be
    testable without actually serving.
    """

    def __init__(self):
        self.settings = types.SimpleNamespace(host=None, port=None)
        self.run_calls: list[dict] = []

    def run(self, transport=None):
        self.run_calls.append({
            "transport": transport,
            "host": self.settings.host,
            "port": self.settings.port,
        })


@pytest.fixture
def fake_mcp_app(monkeypatch):
    fake = _FakeMcpApp()
    monkeypatch.setattr(cli, "create_app", lambda cfg, **kwargs: fake)
    return fake


def test_serve_bad_config_returns_2(capsys):
    assert cli.main(["serve", "--config", "/nonexistent/c.yaml"]) == 2
    assert "config error" in capsys.readouterr().err


def test_serve_defaults_to_localhost(config_file, fake_mcp_app):
    """The default bind must be loopback, not a wildcard address.

    Asserted explicitly (both the positive value AND that it isn't
    "0.0.0.0") so a future change to a wildcard default fails this test
    rather than silently opening the server to the LAN.
    """
    assert cli.main(["serve", "--config", str(config_file)]) == 0
    assert len(fake_mcp_app.run_calls) == 1
    call = fake_mcp_app.run_calls[0]
    assert call["host"] == "127.0.0.1"
    assert call["host"] != "0.0.0.0"
    assert call["port"] == 7700
    assert call["transport"] == "streamable-http"


def test_serve_honours_host_and_port(config_file, fake_mcp_app):
    assert cli.main([
        "serve", "--config", str(config_file),
        "--host", "10.0.0.5", "--port", "9999",
    ]) == 0
    call = fake_mcp_app.run_calls[0]
    assert call["host"] == "10.0.0.5"
    assert call["port"] == 9999


@pytest.fixture
def real_mcp_app(monkeypatch):
    """Let `_serve` build a *real* FastMCP app via the real `create_app`,
    capturing the instance it produced, while stubbing out only `.run` so
    the test never actually binds a socket or blocks.

    `fake_mcp_app` above is deliberately too shallow for the allowlist
    tests below: it replaces `create_app` outright, so it can never observe
    what `create_app` actually built `transport_security` into. Patching
    `FastMCP.run` on the class instead -- rather than replacing
    `create_app` -- is what lets `_serve` run its real construction path
    (migration, `_ArgusFastMCP.__init__`, `_build_transport_security`) and
    still return before ever calling `uvicorn`.
    """
    captured: dict = {}
    real_create_app = cli.create_app

    def spy_create_app(cfg, **kwargs):
        app = real_create_app(cfg, **kwargs)
        captured["app"] = app
        return app

    def fake_run(self, transport=None):
        captured["transport"] = transport

    monkeypatch.setattr(cli, "create_app", spy_create_app)
    monkeypatch.setattr(FastMCP, "run", fake_run)
    return captured


def test_serve_default_allowed_hosts_is_the_unchanged_localhost_set(
    config_file, real_mcp_app
):
    """No `--allowed-host` given: the DNS-rebinding allowlist that lands on
    the real app must be *exactly* the original loopback set -- not merely
    "contains 127.0.0.1", which a wildcard entry like "*" would also satisfy.
    Asserting the full, exact list (and that a wildcard/arbitrary host is
    NOT present) is what actually distinguishes "unchanged default" from
    "accidentally widened".
    """
    assert cli.main(["serve", "--config", str(config_file)]) == 0

    security = real_mcp_app["app"].settings.transport_security
    assert security.enable_dns_rebinding_protection is True
    assert security.allowed_hosts == list(DEFAULT_ALLOWED_HOSTS)
    assert "*" not in security.allowed_hosts
    assert "evil.example" not in security.allowed_hosts


def test_serve_allowed_host_reaches_transport_security_at_construction(
    config_file, real_mcp_app
):
    """This is the assertion the whole fix exists for: an operator-supplied
    `--allowed-host` must actually reach `transport_security.allowed_hosts`
    on the app FastMCP builds -- not merely get accepted by argparse.

    Before the fix, `_serve` built the app with `create_app(cfg)` (no
    `allowed_hosts` parameter existed at all) and only ever adjusted
    `app.settings.host`/`.port` afterwards; `transport_security` was fixed
    at construction from FastMCP's own loopback-only default and never
    updated. Reverting the production change makes this fail with either a
    `TypeError` (no `allowed_hosts` kwarg on `create_app`) or, if the
    signature happened to accept and silently drop it, the assertion below
    failing outright.
    """
    assert cli.main([
        "serve", "--config", str(config_file),
        "--allowed-host", "argus.internal",
    ]) == 0

    security = real_mcp_app["app"].settings.transport_security
    assert "argus.internal" in security.allowed_hosts
    # Guard against a trivially-passing wildcard implementation: a real,
    # unrelated hostname must still be excluded.
    assert "evil.example" not in security.allowed_hosts
    assert "*" not in security.allowed_hosts


def test_serve_allowed_host_replaces_default_rather_than_widening_it(
    config_file, real_mcp_app
):
    """Passing --allowed-host must not silently keep the loopback default
    alongside it -- that would let an operator believe they've scoped the
    allowlist down to just their proxy hostname while loopback access (and
    anything else in the default set) quietly still works too.
    """
    assert cli.main([
        "serve", "--config", str(config_file),
        "--allowed-host", "argus.internal",
    ]) == 0

    security = real_mcp_app["app"].settings.transport_security
    assert security.allowed_hosts == ["argus.internal"]


def test_serve_allowed_host_is_repeatable(config_file, real_mcp_app):
    assert cli.main([
        "serve", "--config", str(config_file),
        "--allowed-host", "argus.internal",
        "--allowed-host", "argus-staging.internal",
    ]) == 0

    security = real_mcp_app["app"].settings.transport_security
    assert security.allowed_hosts == ["argus.internal", "argus-staging.internal"]


# ----------------------------------------------------------- flush-acl ------

def _seed_acl_cache(config_file, entries):
    """entries: list of (token_hash, user_id, username)."""
    from argus.store.db import open_db
    from argus.store import writes

    conn = open_db(config_file.parent / "data" / "i.db")
    for token_hash, user_id, username in entries:
        writes.upsert_acl_cache(
            conn, token_hash=token_hash, user_id=user_id, username=username,
            repo_ids_json="[]", fetched_at=1,
        )
    conn.close()


def _acl_cache_usernames(config_file):
    from argus.store.db import open_db

    conn = open_db(config_file.parent / "data" / "i.db")
    try:
        return sorted(r["username"] for r in conn.execute("SELECT username FROM acl_cache"))
    finally:
        conn.close()


def test_flush_acl_clears_all_rows(config_file, capsys):
    _seed_acl_cache(config_file, [("h1", 1, "alice"), ("h2", 2, "bob")])

    assert cli.main(["flush-acl", "--config", str(config_file)]) == 0

    assert _acl_cache_usernames(config_file) == []
    assert "2" in capsys.readouterr().out


def test_flush_acl_with_user_clears_only_that_user(config_file):
    _seed_acl_cache(config_file, [("h1", 1, "alice"), ("h2", 2, "bob")])

    assert cli.main(
        ["flush-acl", "--config", str(config_file), "--user", "alice"]
    ) == 0

    assert _acl_cache_usernames(config_file) == ["bob"]


def test_flush_acl_user_matching_nobody_is_reported_distinctly(config_file, capsys):
    """A --user that matches no cached row must be named, and must leave
    every other user's cache entry untouched -- distinct from the message an
    already-empty (or fully-cleared) cache produces."""
    _seed_acl_cache(config_file, [("h1", 1, "alice")])

    assert cli.main(
        ["flush-acl", "--config", str(config_file), "--user", "nope"]
    ) == 0

    out = capsys.readouterr().out
    assert "'nope' not in acl cache" in out
    assert _acl_cache_usernames(config_file) == ["alice"]


def test_flush_acl_nothing_to_clear_message_differs_from_user_not_found(
    config_file, capsys
):
    assert cli.main(["flush-acl", "--config", str(config_file)]) == 0

    out = capsys.readouterr().out
    assert "no acl cache entries to clear" in out
    assert "not in acl cache" not in out


def _config_file(tmp_path):
    """A minimal on-disk config, independent of the `config_file` fixture
    above (which also seeds a git `origin` this test doesn't need)."""
    path = tmp_path / "config.yaml"
    path.write_text(
        "gitlab:\n  url: https://gl.test\n  token: t\n"
        f"index:\n  data_dir: {(tmp_path / 'data').as_posix()}\n"
        f"  db_path: {(tmp_path / 'index.db').as_posix()}\n",
        encoding="utf-8")
    return path


def test_resolve_subcommand_runs_both_passes_and_reports_counts(tmp_path, capsys, monkeypatch):
    """Resolution runs once over the whole database, then the graph rebuilds
    from it. Order matters: rebuilding first would materialise the previous
    pass's edges."""
    calls = []
    monkeypatch.setattr("argus.cli.resolve_includes",
                        lambda conn: (calls.append("resolve"), {"resolved": 3,
                                                                "ambiguous": 1})[1])
    monkeypatch.setattr("argus.cli.rebuild_repo_deps",
                        lambda conn: (calls.append("graph"), 2)[1])

    assert cli.main(["resolve", "--config", str(_config_file(tmp_path))]) == 0
    assert calls == ["resolve", "graph"]

    out = capsys.readouterr().out
    assert "resolved" in out and "ambiguous" in out
    assert "3" in out and "1" in out


def test_resolve_rebuild_failure_is_contained(tmp_path, capsys, monkeypatch):
    """A failure in `_resolve`'s resolve_includes/rebuild_repo_deps pair must
    not escape as an uncaught traceback, and must use the same exit code
    `_index` uses for the identical failure.

    `_resolve` had try/finally but no except, so the same
    sqlite3.IntegrityError that `test_index_resolve_rebuild_failure_is_contained`
    proves is contained inside `_index` (FK on repo_deps.to_repo_id, raised
    whenever an include still points at a repo deleted since the last pass)
    still exited `argus resolve` as a raw traceback with no exit code --
    inconsistent with `_index`'s handling of the exact same call pair.
    """
    monkeypatch.setattr(
        "argus.cli.rebuild_repo_deps",
        lambda conn: (_ for _ in ()).throw(
            __import__("sqlite3").IntegrityError("FOREIGN KEY constraint failed")))

    result = cli.main(["resolve", "--config", str(_config_file(tmp_path))])
    assert result == 4, f"expected exit code 4 (run failure), got {result}"
    err = capsys.readouterr().err
    assert "resolve/rebuild failed" in err
