import subprocess
import types
from pathlib import Path

import pytest

from codeindex import cli
from codeindex.gitlab import Project


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
