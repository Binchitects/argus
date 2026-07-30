import subprocess
from pathlib import Path

import pytest

from codeindex.config import IndexConfig
from codeindex.gitlab import Project
from codeindex.store.db import open_db
from codeindex.store import writes, queries
from codeindex import mirror, worker


def git(cwd, *args):
    subprocess.run(["git", *args], cwd=cwd, check=True, capture_output=True, text=True)


@pytest.fixture
def origin(tmp_path):
    repo = tmp_path / "origin"
    repo.mkdir()
    git(repo, "init", "-b", "main")
    git(repo, "config", "user.email", "t@t.test")
    git(repo, "config", "user.name", "Test")
    (repo / "decoder.h").write_text("int DecodeFrame(const char* b, int n);\n")
    (repo / "decoder.c").write_text('#include "decoder.h"\nint DecodeFrame(const char* b, int n){return n;}\n')
    (repo / "build").mkdir()
    (repo / "build" / "gen.c").write_text("int gen(void){return 0;}\n")
    (repo / "logo.bin").write_bytes(b"\x00\x01\x02")
    git(repo, "add", "-A")
    git(repo, "commit", "-m", "first")
    return repo


@pytest.fixture
def env(tmp_path, origin):
    cfg = IndexConfig(data_dir=tmp_path / "data", db_path=tmp_path / "data" / "i.db")
    conn = open_db(cfg.db_path)
    project = Project(gitlab_id=42, path_with_namespace="g/eal",
                      default_branch="main", http_url="https://unused")
    repo_id = writes.upsert_repo(
        conn, gitlab_id=project.gitlab_id,
        path_with_namespace=project.path_with_namespace,
        default_branch=project.default_branch, http_url=project.http_url,
    )
    m = mirror.ensure_mirror(cfg, project, clone_url=str(origin))
    return conn, cfg, project, repo_id, m, origin


def _run(env, old_sha=None):
    conn, cfg, project, repo_id, m, _ = env
    m = mirror.ensure_mirror(cfg, project, clone_url=str(env[5]))
    sha = mirror.head_sha(m, "main")
    tree = mirror.sync_worktree(cfg, project.gitlab_id, m, sha)
    return worker.index_repo(conn, cfg, project, m, tree, sha, old_sha)


def test_indexes_source_and_skips_filtered_files(env):
    result = _run(env)
    conn, _, _, repo_id, _, _ = env
    paths = {r["path"] for r in conn.execute("SELECT path FROM files")}
    assert paths == {"decoder.h", "decoder.c"}
    assert result.indexed == 2
    assert result.skipped == 2  # build/gen.c and logo.bin


def test_symbols_are_queryable_after_index(env):
    _run(env)
    conn, _, _, repo_id, _, _ = env
    rows = queries.find_symbol([repo_id], conn, "DecodeFrame")
    assert len(rows) >= 1
    assert rows[0]["path_with_namespace"] == "g/eal"


def test_includes_are_stored(env):
    _run(env)
    conn = env[0]
    raws = {r["raw"] for r in conn.execute("SELECT raw FROM includes")}
    assert "decoder.h" in raws


def test_last_indexed_sha_advances(env):
    result = _run(env)
    conn, _, _, repo_id, _, _ = env
    row = conn.execute("SELECT last_indexed_sha FROM repos WHERE id = ?",
                       (repo_id,)).fetchone()
    assert row["last_indexed_sha"] == result.sha


def test_incremental_pass_applies_delete(env):
    first = _run(env)
    conn, cfg, project, repo_id, _, origin = env
    git(origin, "rm", "-q", "decoder.c")
    git(origin, "commit", "-m", "drop impl")

    second = _run(env, old_sha=first.sha)
    assert second.deleted == 1
    paths = {r["path"] for r in conn.execute("SELECT path FROM files")}
    assert paths == {"decoder.h"}


def test_unreadable_file_is_recorded_and_does_not_abort(env, monkeypatch):
    conn, cfg, project, repo_id, _, _ = env
    real = Path.read_bytes
    def flaky(self):
        if self.name == "decoder.c":
            raise OSError("simulated read failure")
        return real(self)
    monkeypatch.setattr(Path, "read_bytes", flaky)

    result = _run(env)
    assert result.errors == 1
    assert result.indexed == 1  # decoder.h still made it
    errs = conn.execute("SELECT path, stage FROM index_errors").fetchall()
    assert errs[0]["path"] == "decoder.c"


def test_large_file_is_skipped_without_being_read(env, monkeypatch):
    conn, cfg, project, repo_id, _, origin = env
    (origin / "huge.c").write_text("x" * 200 + "\n")
    git(origin, "add", "-A")
    git(origin, "commit", "-m", "add huge file")

    tiny_cfg = IndexConfig(data_dir=cfg.data_dir, db_path=cfg.db_path,
                           max_file_bytes=10)

    real = Path.read_bytes
    def guarded(self):
        if self.name == "huge.c":
            raise AssertionError("huge.c must be size-filtered before read_bytes")
        return real(self)
    monkeypatch.setattr(Path, "read_bytes", guarded)

    m = mirror.ensure_mirror(tiny_cfg, project, clone_url=str(origin))
    sha = mirror.head_sha(m, "main")
    tree = mirror.sync_worktree(tiny_cfg, project.gitlab_id, m, sha)
    result = worker.index_repo(conn, tiny_cfg, project, m, tree, sha, None)

    paths = {r["path"] for r in conn.execute("SELECT path FROM files")}
    assert "huge.c" not in paths
    assert result.skipped >= 1


def test_time_budget_stops_work_without_advancing_sha(env, monkeypatch):
    conn, cfg, project, repo_id, _, _ = env
    budget_cfg = IndexConfig(data_dir=cfg.data_dir, db_path=cfg.db_path,
                             repo_time_budget_seconds=0)
    m = mirror.ensure_mirror(budget_cfg, project, clone_url=str(env[5]))
    sha = mirror.head_sha(m, "main")
    tree = mirror.sync_worktree(budget_cfg, project.gitlab_id, m, sha)

    result = worker.index_repo(conn, budget_cfg, project, m, tree, sha, None)
    assert result.timed_out is True
    row = conn.execute("SELECT last_indexed_sha FROM repos WHERE id = ?",
                       (repo_id,)).fetchone()
    assert row["last_indexed_sha"] is None


def test_force_push_deletes_vanished_files(env):
    first = _run(env)
    conn, cfg, project, repo_id, _, origin = env

    git(origin, "checkout", "-q", "--orphan", "fresh")
    git(origin, "rm", "-q", "-rf", ".")
    (origin / "z.c").write_text("int z(void){return 0;}\n")
    git(origin, "add", "-A")
    git(origin, "commit", "-m", "rewritten history")
    git(origin, "branch", "-M", "fresh", "main")

    second = _run(env, old_sha=first.sha)

    paths = {r["path"] for r in conn.execute("SELECT path FROM files")}
    assert paths == {"z.c"}
    assert second.deleted == 2  # decoder.h, decoder.c
    orphaned_symbols = conn.execute(
        "SELECT COUNT(*) c FROM symbols s JOIN files f ON f.id = s.file_id"
        " WHERE f.path IN ('decoder.h', 'decoder.c')"
    ).fetchone()["c"]
    assert orphaned_symbols == 0
    assert queries.search_code([repo_id], conn, "DecodeFrame") == []


def test_ctags_unavailable_stops_work_without_advancing_sha(env, monkeypatch):
    from codeindex.parse import ctags
    conn, cfg, project, repo_id, _, _ = env
    monkeypatch.setattr(ctags.shutil, "which", lambda name: None)

    result = _run(env)
    assert result.symbols_failed is True
    row = conn.execute("SELECT last_indexed_sha FROM repos WHERE id = ?",
                       (repo_id,)).fetchone()
    assert row["last_indexed_sha"] is None
