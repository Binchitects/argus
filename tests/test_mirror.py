import subprocess
from pathlib import Path

import pytest

from codeindex.config import IndexConfig
from codeindex import mirror
from codeindex.gitlab import Project


def git(cwd, *args):
    subprocess.run(["git", *args], cwd=cwd, check=True,
                   capture_output=True, text=True)


@pytest.fixture
def origin(tmp_path):
    """A real git repo standing in for GitLab."""
    repo = tmp_path / "origin"
    repo.mkdir()
    git(repo, "init", "-b", "main")
    git(repo, "config", "user.email", "t@t.test")
    git(repo, "config", "user.name", "Test")
    (repo / "a.c").write_text("int a(void){return 1;}\n")
    git(repo, "add", "-A")
    git(repo, "commit", "-m", "first")
    return repo


@pytest.fixture
def cfg(tmp_path):
    return IndexConfig(data_dir=tmp_path / "data", db_path=tmp_path / "data" / "i.db")


@pytest.fixture
def project():
    return Project(gitlab_id=42, path_with_namespace="g/a",
                   default_branch="main", http_url="https://unused")


def test_ensure_mirror_clones_then_fetches(cfg, project, origin):
    m1 = mirror.ensure_mirror(cfg, project, clone_url=str(origin))
    assert (m1 / "HEAD").exists()
    first = mirror.head_sha(m1, "main")

    (origin / "b.c").write_text("int b(void){return 2;}\n")
    git(origin, "add", "-A")
    git(origin, "commit", "-m", "second")

    m2 = mirror.ensure_mirror(cfg, project, clone_url=str(origin))
    assert m2 == m1
    assert mirror.head_sha(m2, "main") != first


def test_changed_files_first_index_lists_everything(cfg, project, origin):
    m = mirror.ensure_mirror(cfg, project, clone_url=str(origin))
    changes = mirror.changed_files(m, None, mirror.head_sha(m, "main"))
    assert changes == [mirror.Change(status="A", path="a.c")]


def test_changed_files_reports_add_modify_delete(cfg, project, origin):
    m = mirror.ensure_mirror(cfg, project, clone_url=str(origin))
    old = mirror.head_sha(m, "main")

    (origin / "a.c").write_text("int a(void){return 99;}\n")
    (origin / "c.c").write_text("int c(void){return 3;}\n")
    git(origin, "add", "-A")
    git(origin, "commit", "-m", "edit+add")
    git(origin, "rm", "-q", "a.c")
    (origin / "d.c").write_text("int d(void){return 4;}\n")
    git(origin, "add", "-A")
    git(origin, "commit", "-m", "delete+add")

    m = mirror.ensure_mirror(cfg, project, clone_url=str(origin))
    new = mirror.head_sha(m, "main")
    got = {(c.status, c.path) for c in mirror.changed_files(m, old, new)}
    assert got == {("D", "a.c"), ("A", "c.c"), ("A", "d.c")}


def test_force_push_falls_back_to_full_listing(cfg, project, origin):
    m = mirror.ensure_mirror(cfg, project, clone_url=str(origin))
    orphan = mirror.head_sha(m, "main")

    git(origin, "checkout", "-q", "--orphan", "fresh")
    git(origin, "rm", "-q", "-rf", ".")
    (origin / "z.c").write_text("int z(void){return 0;}\n")
    git(origin, "add", "-A")
    git(origin, "commit", "-m", "rewritten")
    git(origin, "branch", "-M", "fresh", "main")

    m = mirror.ensure_mirror(cfg, project, clone_url=str(origin))
    new = mirror.head_sha(m, "main")
    assert mirror.is_ancestor(m, orphan, new) is False
    assert mirror.changed_files(m, orphan, new) == [mirror.Change(status="A", path="z.c")]


def test_sync_worktree_materializes_files(cfg, project, origin):
    m = mirror.ensure_mirror(cfg, project, clone_url=str(origin))
    tree = mirror.sync_worktree(cfg, project.gitlab_id, m, mirror.head_sha(m, "main"))
    assert (tree / "a.c").read_text().startswith("int a")


def test_sync_worktree_updates_existing(cfg, project, origin):
    m = mirror.ensure_mirror(cfg, project, clone_url=str(origin))
    mirror.sync_worktree(cfg, project.gitlab_id, m, mirror.head_sha(m, "main"))

    (origin / "e.c").write_text("int e(void){return 5;}\n")
    git(origin, "add", "-A")
    git(origin, "commit", "-m", "third")

    m = mirror.ensure_mirror(cfg, project, clone_url=str(origin))
    tree = mirror.sync_worktree(cfg, project.gitlab_id, m, mirror.head_sha(m, "main"))
    assert (tree / "e.c").exists()


def test_blob_shas_maps_every_path(cfg, project, origin):
    m = mirror.ensure_mirror(cfg, project, clone_url=str(origin))
    shas = mirror.blob_shas(m, mirror.head_sha(m, "main"))
    assert set(shas) == {"a.c"}
    assert len(shas["a.c"]) == 40


def test_changed_files_reports_non_ascii_path_verbatim(cfg, project, origin):
    m = mirror.ensure_mirror(cfg, project, clone_url=str(origin))
    old = mirror.head_sha(m, "main")

    (origin / "файл.c").write_text("int f(void){return 9;}\n")
    git(origin, "add", "-A")
    git(origin, "commit", "-m", "add non-ascii file")

    m = mirror.ensure_mirror(cfg, project, clone_url=str(origin))
    new = mirror.head_sha(m, "main")
    changes = mirror.changed_files(m, old, new)
    assert changes == [mirror.Change(status="A", path="файл.c")]


def test_head_sha_missing_branch_raises_git_error(cfg, project, origin):
    m = mirror.ensure_mirror(cfg, project, clone_url=str(origin))
    with pytest.raises(mirror.GitError):
        mirror.head_sha(m, "does-not-exist")


def test_is_ancestor_raises_git_error_on_invalid_sha(cfg, project, origin):
    m = mirror.ensure_mirror(cfg, project, clone_url=str(origin))
    head = mirror.head_sha(m, "main")
    # Exit 1 ("not an ancestor") is a legitimate answer and must return
    # False; exit 128 (bad/unknown object) is a broken ref and must raise
    # rather than be silently treated as "not an ancestor".
    with pytest.raises(mirror.GitError):
        mirror.is_ancestor(m, "not-a-real-sha", head)


def test_changed_files_treats_typechange_as_modify(cfg, project, origin):
    m = mirror.ensure_mirror(cfg, project, clone_url=str(origin))
    old = mirror.head_sha(m, "main")

    # Force a typechange (file -> symlink) at the git object-model level, in
    # the working repo (which has an index), without needing real OS
    # symlink support so this runs the same on Windows and Linux: hash a
    # blob and re-stage a.c at symlink mode.
    link_sha = subprocess.run(
        ["git", "hash-object", "-w", "--stdin"],
        cwd=origin, input="a.c\n", check=True, capture_output=True, text=True,
    ).stdout.strip()
    git(origin, "update-index", "--cacheinfo", f"120000,{link_sha},a.c")
    git(origin, "commit", "-m", "typechange a.c to symlink")

    m = mirror.ensure_mirror(cfg, project, clone_url=str(origin))
    new = mirror.head_sha(m, "main")
    changes = mirror.changed_files(m, old, new)
    assert changes == [mirror.Change(status="M", path="a.c")]
