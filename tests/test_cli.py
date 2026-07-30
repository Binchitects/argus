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
