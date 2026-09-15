import subprocess
from pathlib import Path

import pytest

from argus.config import GitLabConfig, IndexConfig
from argus import mirror
from argus.gitlab import Project


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


SECRET = "glpat-SUPERSECRET-do-not-leak-0001"


def test_git_error_message_redacts_the_token(cfg, project, tmp_path):
    """The token must never survive into a GitError message.

    This is the highest-stakes redaction on the project: worker.py and cli.py
    persist str(exc) straight into `index_errors`, and the MCP server serves
    queries from that same database. A token reaching that table is a token
    handed to whoever can read the index.

    Asserted by putting the secret somewhere the message is built from -- the
    command echo -- and proving it comes back masked.
    """
    with pytest.raises(mirror.GitError) as excinfo:
        mirror._git(tmp_path, "rev-parse", SECRET, secrets=(SECRET,))
    text = str(excinfo.value)
    assert SECRET not in text, f"token leaked into GitError: {text}"
    assert "***" in text


def test_git_error_without_secrets_is_unredacted(cfg, project, tmp_path):
    """Guard against the redaction test passing for the wrong reason.

    If _git simply never echoed its arguments, the test above would pass with
    redaction removed entirely. This proves the argument really is in the
    message when no secret is declared -- so masking is what changes it.
    """
    with pytest.raises(mirror.GitError) as excinfo:
        mirror._git(tmp_path, "rev-parse", SECRET)
    assert SECRET in str(excinfo.value)


def test_failed_authenticated_clone_leaves_no_token_in_index_errors(cfg, project, tmp_path):
    """End of the leak path, asserted against the PERSISTED row.

    Not the exception -- the row. That is what an operator, and the MCP
    server's own database, can actually read.
    """
    from argus.store.db import open_db
    from argus.store import writes

    conn = open_db(tmp_path / "i.db")
    repo_id = writes.upsert_repo(conn, gitlab_id=project.gitlab_id,
                                 path_with_namespace=project.path_with_namespace,
                                 default_branch="main", http_url=project.http_url)
    try:
        mirror.ensure_mirror(cfg, project,
                             clone_url="http://127.0.0.1:1/nope.git", token=SECRET)
    except mirror.GitError as exc:
        writes.record_error(conn, repo_id, None, "git", str(exc), 1234)

    rows = conn.execute("SELECT message FROM index_errors").fetchall()
    assert rows, "expected the failed clone to record an error"
    for r in rows:
        assert SECRET not in r["message"], f"token persisted to index_errors: {r['message']}"


# --------------------------------------------------------------------- TLS
#
# git reads neither httpx's configuration nor Python's, so the CA and
# verify settings that make the API work have to be pushed into the
# subprocess environment separately. Without these the API enumerates every
# project and the first clone fails with "server certificate verification
# failed", which reads as a credential problem.

def test_auth_env_is_none_when_there_is_nothing_to_apply(cfg):
    # The pre-existing contract: no token and no TLS policy means git inherits
    # the ambient environment untouched.
    assert mirror._auth_env(cfg, None, None) is None


def test_auth_env_carries_verification_off(cfg):
    env = mirror._auth_env(cfg, None, GitLabConfig(url="https://g", token="t",
                                                   verify=False))
    assert env is not None
    assert env["GIT_SSL_NO_VERIFY"] == "true"


def test_auth_env_carries_the_ca_bundle_alongside_the_token(cfg):
    env = mirror._auth_env(cfg, "glpat-x",
                           GitLabConfig(url="https://g", token="t",
                                        ca_cert="/etc/ssl/private-ca.pem"))
    assert env[mirror.ARGUS_TOKEN_ENV] == "glpat-x"
    assert env["GIT_SSL_CAINFO"] == "/etc/ssl/private-ca.pem"
    assert "GIT_SSL_NO_VERIFY" not in env


def test_a_tls_policy_alone_still_builds_an_environment(cfg):
    # The bug this pins: `_auth_env` used to return None whenever there was no
    # token, so an operator who configured only `ca_cert` got a clone that
    # ignored it and behaved exactly as if nothing had been configured.
    env = mirror._auth_env(cfg, None,
                           GitLabConfig(url="https://g", token="t",
                                        ca_cert="/etc/ssl/private-ca.pem"))
    assert env is not None
    assert env["GIT_SSL_CAINFO"] == "/etc/ssl/private-ca.pem"


def test_ensure_mirror_accepts_a_tls_policy(cfg, project, origin):
    # A TLS policy must not disturb a clone that does not need one. git only
    # consults these variables for https remotes, so a local path still works.
    path = mirror.ensure_mirror(
        cfg, project, clone_url=str(origin),
        gitlab_cfg=GitLabConfig(url="https://g", token="t", verify=False))
    assert (path / "HEAD").exists()
