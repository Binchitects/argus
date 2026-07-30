import subprocess
import types
from pathlib import Path

import pytest

from argus import cli
from argus.gitlab import Project


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


def test_reset_retries_with_mistyped_repo_reports_no_match(
    config_file, fake_projects, capsys
):
    """When --repo does not match any known repo, report distinctly."""
    from argus.store.db import open_db

    # Index once to establish baseline
    assert cli.main(["index", "--config", str(config_file)]) == 0

    # Insert a retry_attempts entry for the real repo
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

    # Run with --reset-retries --repo with mistyped name
    assert (
        cli.main(
            ["index", "--config", str(config_file), "--reset-retries", "--repo", "g/nope"]
        )
        == 0
    )

    out = capsys.readouterr().out
    # Should indicate the repo was not found, not silently succeed
    assert "not found" in out or "no repos matched" in out or "did not match" in out, (
        f"should report distinctly when --repo does not match; got: {out}"
    )
    assert (
        "reset retry counters" not in out
    ), "should not report success when no repos matched"


def test_reset_retries_help_warns_against_scheduling(capsys):
    """Help text for --reset-retries should warn against using on schedule."""
    # Invoke the help output to check the text
    try:
        cli.main(["index", "--help"])
    except SystemExit:
        # argparse calls sys.exit() after printing help, which is expected
        pass

    help_text = capsys.readouterr().out

    # The help should mention this is manual/not for scheduling
    assert (
        "manual" in help_text.lower()
        or "schedule" in help_text.lower()
        or "do not use on a schedule" in help_text.lower()
        or "should not be used on a schedule" in help_text.lower()
    ), (
        f"help text should warn against scheduling; got: {help_text}"
    )
