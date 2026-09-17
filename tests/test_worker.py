import json
import sqlite3
import subprocess
import types
from pathlib import Path

import pytest

from argus.config import IndexConfig
from argus.gitlab import Project
from argus.store.db import open_db
from argus.store import writes, queries
from argus import mirror, worker


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


def test_unresolvable_old_sha_recovers_via_full_relisting(env):
    """A missing old commit must self-heal, not fail the repo forever.

    Routine causes: data_dir/mirrors deleted to reclaim disk while index.db
    survives, `git gc` pruning a force-pushed history, a repo re-created in
    GitLab. Raising here would leave last_indexed_sha stale so every later
    run failed identically.
    """
    _run(env)
    conn, cfg, project, repo_id, _, origin = env
    git(origin, "rm", "-q", "decoder.c")
    git(origin, "commit", "-m", "drop impl")

    absent = "0" * 40  # well-formed but not an object in the mirror
    result = _run(env, old_sha=absent)

    paths = {r["path"] for r in conn.execute("SELECT path FROM files")}
    assert paths == {"decoder.h"}  # the vanished file was deleted
    assert result.deleted == 1
    row = conn.execute("SELECT last_indexed_sha FROM repos WHERE id = ?",
                       (repo_id,)).fetchone()
    assert row["last_indexed_sha"] == result.sha


def test_unchanged_files_are_skipped_not_reindexed(env):
    first = _run(env, old_sha=None)
    assert first.indexed == 2
    assert first.skipped == 2

    # Simulate a repeat full-listing pass with no upstream change (e.g. a
    # prior run timed out before advancing last_indexed_sha, so old_sha is
    # still None on the next attempt). Without the blob-sha skip check,
    # decoder.h and decoder.c would be redone even though nothing changed.
    second = _run(env, old_sha=None)
    assert second.indexed == 0
    assert second.skipped == 4  # decoder.h, decoder.c unchanged + 2 filtered


def test_read_error_is_retried_on_a_later_run(env, monkeypatch):
    first = _run(env)
    conn, cfg, project, repo_id, _, origin = env

    (origin / "decoder.c").write_text(
        '#include "decoder.h"\nint DecodeFrame(const char* b, int n){return n + 1;}\n'
    )
    git(origin, "commit", "-am", "modify decoder.c")

    real = Path.read_bytes
    def flaky(self):
        if self.name == "decoder.c":
            raise OSError("simulated transient read failure")
        return real(self)
    monkeypatch.setattr(Path, "read_bytes", flaky)

    second = _run(env, old_sha=first.sha)
    assert second.errors == 1
    row = conn.execute(
        "SELECT content FROM files WHERE repo_id = ? AND path = 'decoder.c'",
        (repo_id,),
    ).fetchone()
    assert "return n;" in row["content"]  # old content retained, sha still advances

    # Restore real reads. Upstream has NOT changed again, so a normal diff
    # between second.sha and itself would be empty — only the retry queue
    # makes the third pass revisit decoder.c.
    monkeypatch.setattr(Path, "read_bytes", real)
    third = _run(env, old_sha=second.sha)
    assert third.indexed == 1
    row = conn.execute(
        "SELECT content FROM files WHERE repo_id = ? AND path = 'decoder.c'",
        (repo_id,),
    ).fetchone()
    assert "return n + 1;" in row["content"]


def test_symbols_failure_does_not_leave_stale_symbols_behind(env, monkeypatch):
    """A file whose symbol extraction failed must never be reported as fully
    indexed with symbols from an older revision.

    Pass A upserts the new content in place (same file_id, new blob_sha) but
    ctags then fails, so the SHA is held. Without clearing the now-stale
    symbol rows, pass B sees a matching blob_sha AND symbol rows, skips the
    file, finds to_parse empty, reports symbols_failed=False and advances the
    SHA -- leaving the old revision's symbols in place permanently.
    """
    from argus.parse import ctags
    first = _run(env)
    conn, cfg, project, repo_id, _, origin = env

    (origin / "decoder.c").write_text(
        '#include "decoder.h"\n'
        "\n"
        "/* renamed and moved down */\n"
        "int DecodeFrameV2(const char* b, int n){return n + 2;}\n"
    )
    git(origin, "commit", "-am", "rename DecodeFrame to DecodeFrameV2")

    # Pass A: content lands, ctags fails, SHA is held.
    monkeypatch.setattr(ctags.shutil, "which", lambda name: None)
    passa = _run(env, old_sha=first.sha)
    assert passa.symbols_failed is True
    assert conn.execute("SELECT last_indexed_sha FROM repos WHERE id = ?",
                        (repo_id,)).fetchone()["last_indexed_sha"] == first.sha

    # Pass B: ctags healthy again, same old_sha because the SHA was held.
    monkeypatch.undo()
    passb = _run(env, old_sha=first.sha)
    assert passb.symbols_failed is False

    stored = {
        (r["name"], r["line"]) for r in conn.execute(
            "SELECT s.name, s.line FROM symbols s JOIN files f ON f.id = s.file_id"
            " WHERE f.repo_id = ? AND f.path = 'decoder.c'", (repo_id,)
        )
    }
    assert stored == {("DecodeFrameV2", 4)}
    assert conn.execute("SELECT last_indexed_sha FROM repos WHERE id = ?",
                        (repo_id,)).fetchone()["last_indexed_sha"] == passb.sha


def test_permanently_failing_path_is_dropped_after_the_retry_cap(env, monkeypatch):
    """A path that can never be read must stop being re-enqueued.

    Otherwise the queue never empties and index_errors grows without bound,
    one fresh row per pass, forever.
    """
    first = _run(env)
    conn, cfg, project, repo_id, _, origin = env

    (origin / "decoder.c").write_text(
        '#include "decoder.h"\nint DecodeFrame(const char* b, int n){return n + 1;}\n'
    )
    git(origin, "commit", "-am", "modify decoder.c")

    real = Path.read_bytes
    def never_readable(self):
        if self.name == "decoder.c":
            raise OSError("simulated permanent read failure")
        return real(self)
    monkeypatch.setattr(Path, "read_bytes", never_readable)

    previous = first.sha
    for _ in range(writes.MAX_RETRY_ATTEMPTS):
        result = _run(env, old_sha=previous)
        assert result.errors == 1
        previous = result.sha

    queued = conn.execute(
        "SELECT COUNT(*) c FROM index_queue WHERE repo_id = ?", (repo_id,)
    ).fetchone()["c"]
    assert queued == 0

    final = conn.execute(
        "SELECT path, message FROM index_errors"
        " WHERE repo_id = ? AND stage = 'retry-exhausted'", (repo_id,)
    ).fetchall()
    assert len(final) == 1
    assert final[0]["path"] == "decoder.c"
    assert "giving up" in final[0]["message"]

    # A further pass must not resurrect it or append more error rows.
    before = conn.execute("SELECT COUNT(*) c FROM index_errors").fetchone()["c"]
    extra = _run(env, old_sha=previous)
    assert extra.errors == 0
    assert conn.execute(
        "SELECT COUNT(*) c FROM index_errors").fetchone()["c"] == before


def test_timed_out_pass_does_not_discard_queued_retry_paths(env):
    """A time-budget break must not be what loses the retry queue.

    index_repo drains and DELETEs the index_queue row up front. Nothing
    re-enqueues an unreached retry path -- failed_paths only collects paths
    that actually errored -- and it cannot come back via a later diff, so a
    timed-out pass silently dropped it and the original defect recurred.
    """
    first = _run(env)
    conn, cfg, project, repo_id, _, _ = env
    writes.enqueue_retry(conn, repo_id, ["decoder.c"],
                         "earlier read failure", 0)

    budget_cfg = IndexConfig(data_dir=cfg.data_dir, db_path=cfg.db_path,
                             repo_time_budget_seconds=0)
    m = mirror.ensure_mirror(budget_cfg, project, clone_url=str(env[5]))
    sha = mirror.head_sha(m, "main")
    tree = mirror.sync_worktree(budget_cfg, project.gitlab_id, m, sha)

    result = worker.index_repo(conn, budget_cfg, project, m, tree, sha,
                               first.sha)
    assert result.timed_out is True
    assert writes.drain_retry_paths(conn, repo_id) == ["decoder.c"]


def test_unexpected_error_mid_pass_does_not_discard_the_retry_queue(env, monkeypatch):
    """An exception between the drain and the re-enqueue must not lose paths.

    index_queue's row was DELETEd up front, and nothing else re-derives a
    retry path: the pass that first failed on it let the SHA advance past the
    commit that changed it, so it never reappears in a later diff. An
    unexpected failure anywhere in between therefore destroyed the queue
    silently.
    """
    from argus.parse import ctags as ctags_mod
    conn, cfg, project, repo_id, _, origin = env
    first = _run(env)

    writes.enqueue_retry(conn, repo_id, ["decoder.c"], "earlier read failure", 0)
    # Make decoder.c genuinely stale so the retry actually reprocesses it
    # rather than skipping it as already-current.
    conn.execute(
        "UPDATE files SET symbols_sha = NULL WHERE repo_id = ? AND path = 'decoder.c'",
        (repo_id,))
    conn.commit()

    def boom(*args, **kwargs):
        raise RuntimeError("simulated indexer defect")

    monkeypatch.setattr(ctags_mod, "extract_symbols", boom)
    m = mirror.ensure_mirror(cfg, project, clone_url=str(origin))
    sha = mirror.head_sha(m, "main")
    tree = mirror.sync_worktree(cfg, project.gitlab_id, m, sha)
    with pytest.raises(RuntimeError):
        worker.index_repo(conn, cfg, project, m, tree, sha, first.sha)
    monkeypatch.undo()

    assert "decoder.c" in _queued_paths(conn, repo_id), (
        "the drained retry queue was discarded by an unexpected failure"
    )


def test_file_with_zero_symbols_is_not_reprocessed(env, monkeypatch):
    """An include-only .c has no symbols; it must still count as complete."""
    conn, cfg, project, repo_id, _, origin = env
    (origin / "empty.c").write_text('#include "decoder.h"\n')
    git(origin, "add", "-A")
    git(origin, "commit", "-m", "add include-only file")

    _run(env)
    assert "empty.c" in {r["path"] for r in conn.execute("SELECT path FROM files")}

    reprocessed = []
    original_upsert_file = writes.upsert_file

    def spy_upsert_file(conn, **kwargs):
        reprocessed.append(kwargs["path"])
        return original_upsert_file(conn, **kwargs)

    monkeypatch.setattr(writes, "upsert_file", spy_upsert_file)

    # Force the full-listing path so _already_current is what decides.
    second = _run(env, old_sha=None)

    # empty.c has zero symbol rows by design (include-only header). Under the
    # old "does this file have any symbol rows" proxy, that made it look
    # incomplete forever and it would be reprocessed -- upsert_file called
    # again for it -- on every subsequent full-listing pass. Assert directly
    # on empty.c's own fate: build/gen.c and logo.bin are skipped by
    # unrelated filename filters on every pass regardless of this bug, so
    # aggregate counters like second.skipped can't tell the two behaviours
    # apart, and second.indexed would already be 0 for reasons unrelated to
    # empty.c if it were the only file in the repo.
    assert "empty.c" not in reprocessed
    assert second.indexed == 0


def test_stale_symbols_never_satisfy_the_completion_check(env, monkeypatch):
    """symbols_sha lagging blob_sha means the file is not complete."""
    conn, cfg, project, repo_id, _, _ = env
    _run(env)
    fid = conn.execute("SELECT id FROM files WHERE path = 'decoder.c'").fetchone()["id"]
    conn.execute("UPDATE files SET symbols_sha = 'stale' WHERE id = ?", (fid,))
    conn.commit()

    from argus import worker
    assert worker._already_current(conn, repo_id, "decoder.c",
                                   conn.execute("SELECT blob_sha FROM files WHERE id = ?",
                                                (fid,)).fetchone()["blob_sha"]) is False


def _partial_ctags_module(tagged_path: str, blamed_path: str):
    """A stand-in for ctags' `subprocess` that fakes one partial batch.

    Shape of a real partial batch: ctags emits what it managed to parse,
    names the file it could not open on stderr, and exits non-zero.

    A whole stand-in module is substituted rather than patching
    ``subprocess.run`` in place, because ``ctags.subprocess`` IS the stdlib
    module -- patching its ``run`` would also intercept mirror's git calls
    and make index_repo fail for an unrelated reason.
    """
    def run(cmd, **kwargs):
        listed = [p for p in kwargs.get("input", "").splitlines() if p]
        # A pseudo-tag line keeps stdout non-empty even when every listed
        # path is the blamed one. Without it the batch would look like a
        # total failure (non-zero exit, no output) and raise CtagsUnavailable
        # instead of exercising the partial-batch path.
        stdout = '{"_type":"ptag","name":"JSON_OUTPUT_VERSION"}\n'
        stdout += "".join(
            json.dumps({"_type": "tag", "name": "DecodeFrame", "path": path,
                        "kind": "prototype", "line": 1}) + "\n"
            for path in listed if path == tagged_path
        )
        return types.SimpleNamespace(
            returncode=1, stdout=stdout,
            stderr=f'ctags: Warning: cannot open input file "{blamed_path}"'
                   " : Permission denied\n",
        )
    return types.SimpleNamespace(
        run=run, TimeoutExpired=subprocess.TimeoutExpired)


def _blaming_ctags_module(blamed_path: str, *, stderr: str | None = None):
    """A stand-in `subprocess` that tags every listed path except `blamed_path`.

    Models the realistic partial batch: ctags opened and parsed everything it
    could, named the one file it could not open, and exited non-zero. Pass
    `stderr` to model a non-zero exit that names nothing at all.
    """
    def run(cmd, **kwargs):
        listed = [p for p in kwargs.get("input", "").splitlines() if p]
        # A pseudo-tag keeps stdout non-empty so the batch is treated as
        # partial rather than as a total failure (which raises).
        stdout = '{"_type":"ptag","name":"JSON_OUTPUT_VERSION"}\n'
        stdout += "".join(
            json.dumps({"_type": "tag",
                        "name": "Sym_" + path.replace("/", "_").replace(".", "_"),
                        "path": path, "kind": "function", "line": 1}) + "\n"
            for path in listed if path != blamed_path
        )
        return types.SimpleNamespace(
            returncode=1, stdout=stdout,
            stderr=stderr if stderr is not None else
            f'ctags: Warning: cannot open input file "{blamed_path}"'
            " : Permission denied\n",
        )
    return types.SimpleNamespace(
        run=run, TimeoutExpired=subprocess.TimeoutExpired)


def _symbols_sha_row(conn, repo_id, path):
    return conn.execute(
        "SELECT blob_sha, symbols_sha FROM files WHERE repo_id = ? AND path = ?",
        (repo_id, path),
    ).fetchone()


def test_partial_ctags_batch_leaves_the_uncovered_path_incomplete(env, monkeypatch):
    """A path ctags never covered must not be stamped symbols_sha = blob_sha.

    Stamping it marks it complete forever: the SHA advances, the next pass
    skips it as already-current, and only a content edit can ever rescue it.
    """
    from argus.parse import ctags as ctags_mod
    conn, cfg, project, repo_id, _, origin = env
    # Resolve the mirror BEFORE patching: ctags.subprocess is the stdlib
    # subprocess module itself, so patching its `run` also intercepts mirror's
    # git invocations.
    m = mirror.ensure_mirror(cfg, project, clone_url=str(origin))
    sha = mirror.head_sha(m, "main")
    tree = mirror.sync_worktree(cfg, project.gitlab_id, m, sha)

    monkeypatch.setattr(ctags_mod, "subprocess",
                        _partial_ctags_module("decoder.h", "decoder.c"))
    first = worker.index_repo(conn, cfg, project, m, tree, sha, None)
    monkeypatch.undo()

    row = _symbols_sha_row(conn, repo_id, "decoder.c")
    assert row["symbols_sha"] != row["blob_sha"], (
        "decoder.c was stamped complete even though ctags never covered it"
    )
    assert ("decoder.c", "symbols") in {
        (r["path"], r["stage"]) for r in conn.execute(
            "SELECT path, stage FROM index_errors WHERE repo_id = ?", (repo_id,))
    }, "an uncovered path must surface in index_errors, not vanish silently"
    assert "decoder.c" in _queued_paths(conn, repo_id)

    # decoder.h was covered by the same batch and must still be complete.
    hdr = _symbols_sha_row(conn, repo_id, "decoder.h")
    assert hdr["symbols_sha"] == worker._symbols_stamp(hdr["blob_sha"])

    # Upstream has NOT changed, so only the retry queue can bring decoder.c
    # back on the next pass.
    worker.index_repo(conn, cfg, project, m, tree, sha, first.sha)
    names = {r["name"] for r in conn.execute(
        "SELECT s.name FROM symbols s JOIN files f ON f.id = s.file_id"
        " WHERE f.repo_id = ? AND f.path = 'decoder.c'", (repo_id,))}
    assert "DecodeFrame" in names
    row = _symbols_sha_row(conn, repo_id, "decoder.c")
    assert row["symbols_sha"] == worker._symbols_stamp(row["blob_sha"])


def test_healthy_root_file_survives_a_subdirectory_namesake_failing(env, monkeypatch):
    """The end-to-end cost of blaming by substring.

    `main.c` at the root and `sub/main.c` (ACL-denied) is an ordinary C repo
    layout. ctags parses the root file fine, but a substring blame test drags
    it into `uncovered`, which deletes the symbols just extracted, NULLs its
    symbols_sha, writes an index_errors row and charges a retry attempt --
    three passes and a healthy file is retry-exhausted and, since the SHA has
    advanced, permanently symbol-less.
    """
    from argus.parse import ctags as ctags_mod
    conn, cfg, project, repo_id, _, origin = env
    (origin / "main.c").write_text("int MainRoot(void){return 0;}\n")
    (origin / "sub").mkdir()
    (origin / "sub" / "main.c").write_text("int MainSub(void){return 1;}\n")
    git(origin, "add", "-A")
    git(origin, "commit", "-m", "add colliding file names")

    m = mirror.ensure_mirror(cfg, project, clone_url=str(origin))
    sha = mirror.head_sha(m, "main")
    tree = mirror.sync_worktree(cfg, project.gitlab_id, m, sha)

    monkeypatch.setattr(ctags_mod, "subprocess",
                        _blaming_ctags_module("sub/main.c"))
    worker.index_repo(conn, cfg, project, m, tree, sha, None)
    monkeypatch.undo()

    row = _symbols_sha_row(conn, repo_id, "main.c")
    assert row["symbols_sha"] == worker._symbols_stamp(row["blob_sha"]), (
        "the root main.c was blamed for sub/main.c's diagnostic and lost its"
        " completion marker"
    )
    assert {r["name"] for r in conn.execute(
        "SELECT s.name FROM symbols s JOIN files f ON f.id = s.file_id"
        " WHERE f.repo_id = ? AND f.path = 'main.c'", (repo_id,))} == {"Sym_main_c"}
    assert "main.c" not in {r["path"] for r in conn.execute(
        "SELECT path FROM index_errors WHERE repo_id = ?", (repo_id,))}
    assert _attempts(conn, repo_id, "main.c") == 0

    # The file ctags actually named is still handled as a real failure.
    sub = _symbols_sha_row(conn, repo_id, "sub/main.c")
    # Not `!= blob_sha`: since the stamp gained a contract-version prefix, a
    # SUCCESSFUL row also differs from the bare blob sha, so that assertion
    # would now pass for exactly the case this test exists to rule out.
    assert sub["symbols_sha"] != worker._symbols_stamp(sub["blob_sha"])
    assert "sub/main.c" in _queued_paths(conn, repo_id)
    assert _attempts(conn, repo_id, "sub/main.c") == 1


def test_path_ctags_cannot_open_is_not_stamped_complete(env, monkeypatch):
    """git can check out a name the filesystem will not hand back.

    A Windows reserved name (aux.c), a >260-char path or a trailing-dot name
    fails is_file(), so ctags is never given it. It must be reported as a
    failure, not stamped complete with zero symbols.
    """
    conn, cfg, project, repo_id, _, origin = env
    m = mirror.ensure_mirror(cfg, project, clone_url=str(origin))
    sha = mirror.head_sha(m, "main")
    tree = mirror.sync_worktree(cfg, project.gitlab_id, m, sha)

    real_is_file = Path.is_file

    def unopenable(self):
        if self.name == "decoder.c":
            return False
        return real_is_file(self)

    monkeypatch.setattr(Path, "is_file", unopenable)
    worker.index_repo(conn, cfg, project, m, tree, sha, None)
    monkeypatch.undo()

    row = _symbols_sha_row(conn, repo_id, "decoder.c")
    assert row["symbols_sha"] != row["blob_sha"], (
        "a path ctags could not open was stamped complete with zero symbols"
    )
    assert ("decoder.c", "symbols") in {
        (r["path"], r["stage"]) for r in conn.execute(
            "SELECT path, stage FROM index_errors WHERE repo_id = ?", (repo_id,))
    }
    assert "decoder.c" in _queued_paths(conn, repo_id)


def test_ctags_unavailable_stops_work_without_advancing_sha(env, monkeypatch):
    from argus.parse import ctags
    conn, cfg, project, repo_id, _, _ = env
    monkeypatch.setattr(ctags.shutil, "which", lambda name: None)

    result = _run(env)
    assert result.symbols_failed is True
    assert result.errors == 1
    row = conn.execute("SELECT last_indexed_sha FROM repos WHERE id = ?",
                       (repo_id,)).fetchone()
    assert row["last_indexed_sha"] is None
    err = conn.execute(
        "SELECT stage FROM index_errors WHERE repo_id = ?", (repo_id,)
    ).fetchone()
    assert err["stage"] == "ctags"


def test_retry_path_whose_symbols_fail_is_requeued(env, monkeypatch):
    conn, cfg, project, repo_id, _, origin = env
    _run(env)  # establish a baseline so later diffs are narrow

    # Pass N: decoder.c is modified but fails to read, so it lands in the queue.
    (origin / "decoder.c").write_text(
        '#include "decoder.h"\nint DecodeFrameV2(const char* b, int n){return n;}\n'
    )
    git(origin, "add", "-A")
    git(origin, "commit", "-m", "modify decoder")

    real = Path.read_bytes
    def failing(self):
        if self.name == "decoder.c":
            raise OSError("simulated")
        return real(self)
    monkeypatch.setattr(Path, "read_bytes", failing)
    first = _run(env)
    monkeypatch.undo()
    assert "decoder.c" in _queued_paths(conn, repo_id)

    # Pass N+1: it reads fine but ctags fails. It must stay queued.
    from argus.parse import ctags as ctags_mod
    monkeypatch.setattr(ctags_mod.shutil, "which", lambda name: None)
    second = _run(env, old_sha=first.sha)
    monkeypatch.undo()
    assert second.symbols_failed is True
    assert "decoder.c" in _queued_paths(conn, repo_id), \
        "retry-origin path was dropped after its symbol extraction failed"

    # Pass N+2: ctags healthy. The file must get correct symbols.
    _run(env, old_sha=first.sha)
    names = {r["name"] for r in conn.execute(
        "SELECT s.name FROM symbols s JOIN files f ON f.id = s.file_id"
        " WHERE f.path = 'decoder.c'")}
    assert "DecodeFrameV2" in names


def test_successful_index_clears_the_retry_counter(env, monkeypatch):
    conn, cfg, project, repo_id, _, _ = env
    conn.execute(
        "INSERT INTO retry_attempts (repo_id, path, attempts) VALUES (?, 'decoder.c', 2)",
        (repo_id,),
    )
    conn.commit()
    _run(env)
    row = conn.execute(
        "SELECT attempts FROM retry_attempts WHERE repo_id = ? AND path = 'decoder.c'",
        (repo_id,),
    ).fetchone()
    assert row is None, "counter should be cleared once the path indexes successfully"


def test_symbols_failed_pass_does_not_clear_the_retry_counter(env, monkeypatch):
    """Task 2's guard: ctags is all-or-nothing, so when it fails NO path in
    to_parse actually completed even though its read/store succeeded.
    Clearing retry_attempts here would let bump_retry_attempts reinsert a
    fresh attempts=1 row every such pass, and attempts could never reach
    MAX_RETRY_ATTEMPTS -- the exact bug Task 2 fixed. decoder.c's read/store
    succeed this pass (it lands in to_parse), but ctags fails, so its stale
    counter must survive untouched.
    """
    from argus.parse import ctags
    conn, cfg, project, repo_id, _, _ = env
    conn.execute(
        "INSERT INTO retry_attempts (repo_id, path, attempts) VALUES (?, 'decoder.c', 2)",
        (repo_id,),
    )
    conn.commit()
    monkeypatch.setattr(ctags.shutil, "which", lambda name: None)

    result = _run(env)
    assert result.symbols_failed is True
    row = conn.execute(
        "SELECT attempts FROM retry_attempts WHERE repo_id = ? AND path = 'decoder.c'",
        (repo_id,),
    ).fetchone()
    assert row is not None and row["attempts"] == 2, \
        "counter must survive a pass whose symbol extraction failed"


def _queued_paths(conn, repo_id):
    row = conn.execute(
        "SELECT reason FROM index_queue WHERE repo_id = ?", (repo_id,)
    ).fetchone()
    if row is None:
        return []
    return json.loads(row["reason"]).get("paths", [])


def _attempts(conn, repo_id, path):
    row = conn.execute(
        "SELECT attempts FROM retry_attempts WHERE repo_id = ? AND path = ?",
        (repo_id, path),
    ).fetchone()
    return row["attempts"] if row else 0


def _exhausted_paths(conn, repo_id):
    return [r["path"] for r in conn.execute(
        "SELECT path FROM index_errors WHERE repo_id = ? AND stage = 'retry-exhausted'",
        (repo_id,))]


def _queue_a_retry_origin_path(env, monkeypatch):
    """Get decoder.c into the retry queue via a read failure; return the sha."""
    conn, cfg, project, repo_id, _, origin = env
    _run(env)  # baseline, so later diffs are narrow

    (origin / "decoder.c").write_text(
        '#include "decoder.h"\nint DecodeFrameV2(const char* b, int n){return n;}\n'
    )
    git(origin, "add", "-A")
    git(origin, "commit", "-m", "modify decoder")

    real = Path.read_bytes

    def failing(self):
        if self.name == "decoder.c":
            raise OSError("simulated")
        return real(self)

    monkeypatch.setattr(Path, "read_bytes", failing)
    first = _run(env)
    monkeypatch.undo()
    assert "decoder.c" in _queued_paths(conn, repo_id)
    return first.sha


def test_repo_wide_ctags_outage_does_not_burn_the_queued_paths_retry_budget(
    env, monkeypatch
):
    """A ctags outage is repo-global and all-or-nothing, not a per-path fault.

    Charging every retry-origin path in to_parse one attempt per broken pass
    exhausts the entire queue at once after MAX_RETRY_ATTEMPTS passes: every
    path gets a retry-exhausted row and is dropped forever, ending up
    permanently symbol-less, while the very next clean pass reports the repo
    healthy. Nothing was wrong with any of those files. They are re-enqueued
    without bumping attempts -- exactly as unreached_retry already is -- and
    the outage stays visible through last_run_symbols_failed.
    """
    from argus.parse import ctags as ctags_mod
    conn, cfg, project, repo_id, _, origin = env

    first_sha = _queue_a_retry_origin_path(env, monkeypatch)
    before = _attempts(conn, repo_id, "decoder.c")
    assert before == 1, "the genuine read failure should have counted once"

    # ctags stays broken for more passes than the cap allows. decoder.c's
    # read and store succeed every time; only symbol extraction fails.
    monkeypatch.setattr(ctags_mod.shutil, "which", lambda name: None)
    previous = first_sha
    outages = []
    for _ in range(writes.MAX_RETRY_ATTEMPTS + 2):
        result = _run(env, old_sha=previous)
        outages.append(result.symbols_failed)
        previous = result.sha
    monkeypatch.undo()
    assert outages[0] is True, "sanity check: the outage was actually simulated"

    assert "decoder.c" in _queued_paths(conn, repo_id), (
        "a repo-wide ctags outage burned the queued path's retry budget and"
        " dropped it permanently"
    )
    assert _attempts(conn, repo_id, "decoder.c") == before, (
        "a tool outage must not be charged to the path's attempt counter"
    )
    assert _exhausted_paths(conn, repo_id) == []
    assert conn.execute(
        "SELECT last_run_symbols_failed FROM repos WHERE id = ?", (repo_id,)
    ).fetchone()["last_run_symbols_failed"] == 1, (
        "the outage must stay visible -- that is what makes not counting it safe"
    )

    # Once ctags recovers the path must actually index, and leave the queue.
    _run(env, old_sha=previous)
    names = {r["name"] for r in conn.execute(
        "SELECT s.name FROM symbols s JOIN files f ON f.id = s.file_id"
        " WHERE f.repo_id = ? AND f.path = 'decoder.c'", (repo_id,))}
    assert "DecodeFrameV2" in names
    assert "decoder.c" not in _queued_paths(conn, repo_id)
    assert _attempts(conn, repo_id, "decoder.c") == 0


def test_per_path_symbol_failure_still_exhausts_the_retry_cap(env, monkeypatch):
    """The other half of the distinction: one file ctags cannot process IS a
    per-path failure. It must still bump attempts and still be given up on,
    or a permanently unreadable file is re-enqueued forever and index_errors
    grows without bound -- the defect the cap exists to prevent.
    """
    from argus.parse import ctags as ctags_mod
    conn, cfg, project, repo_id, _, origin = env
    m = mirror.ensure_mirror(cfg, project, clone_url=str(origin))
    sha = mirror.head_sha(m, "main")
    tree = mirror.sync_worktree(cfg, project.gitlab_id, m, sha)

    monkeypatch.setattr(ctags_mod, "subprocess",
                        _partial_ctags_module("decoder.h", "decoder.c"))
    previous = None
    for _ in range(writes.MAX_RETRY_ATTEMPTS):
        result = worker.index_repo(conn, cfg, project, m, tree, sha, previous)
        assert result.symbols_failed is False, (
            "ctags itself ran; only this one file was not processed"
        )
        previous = result.sha
    monkeypatch.undo()

    assert _attempts(conn, repo_id, "decoder.c") == writes.MAX_RETRY_ATTEMPTS
    assert _exhausted_paths(conn, repo_id) == ["decoder.c"]
    assert "decoder.c" not in _queued_paths(conn, repo_id)


def _unattributable_ctags_module(silent_paths: set):
    """A stand-in `subprocess` for a non-zero exit that names no path at all.

    Models ctags hitting an internal error rather than an ACL/open failure
    on a specific file: it still tags everything it could (all listed paths
    except `silent_paths`), but its stderr contains no path at all, so
    nothing in the batch can be individually blamed for the failure.
    """
    def run(cmd, **kwargs):
        listed = [p for p in kwargs.get("input", "").splitlines() if p]
        stdout = '{"_type":"ptag","name":"JSON_OUTPUT_VERSION"}\n'
        stdout += "".join(
            json.dumps({"_type": "tag",
                        "name": "Sym_" + path.replace("/", "_").replace(".", "_"),
                        "path": path, "kind": "function", "line": 1}) + "\n"
            for path in listed if path not in silent_paths
        )
        return types.SimpleNamespace(
            returncode=1, stdout=stdout,
            stderr="ctags: internal error: unexpected state\n",
        )
    return types.SimpleNamespace(
        run=run, TimeoutExpired=subprocess.TimeoutExpired)


def test_unattributable_batch_failure_does_not_burn_the_retry_budget(env, monkeypatch):
    """A non-zero exit that names no path is a tool hiccup, not proof that
    any one file is broken. quiet.c produces no tags (there is nothing in
    it to tag), so it looks identical to a file ctags never opened -- but
    unlike test_per_path_symbol_failure_still_exhausts_the_retry_cap, ctags
    never names it. Charging that against its retry budget would exhaust
    and permanently drop a file nothing was ever actually wrong with, after
    exactly MAX_RETRY_ATTEMPTS unlucky passes.
    """
    from argus.parse import ctags as ctags_mod
    conn, cfg, project, repo_id, _, origin = env
    (origin / "quiet.c").write_text("/* no symbols here */\n")
    git(origin, "add", "-A")
    git(origin, "commit", "-m", "add symbol-free file")

    m = mirror.ensure_mirror(cfg, project, clone_url=str(origin))
    sha = mirror.head_sha(m, "main")
    tree = mirror.sync_worktree(cfg, project.gitlab_id, m, sha)

    monkeypatch.setattr(ctags_mod, "subprocess",
                        _unattributable_ctags_module({"quiet.c"}))
    previous = None
    for _ in range(writes.MAX_RETRY_ATTEMPTS + 2):
        result = worker.index_repo(conn, cfg, project, m, tree, sha, previous)
        assert result.symbols_failed is False, (
            "ctags itself ran and tagged everything it could -- this is a"
            " per-batch attribution gap, not a repo-wide outage"
        )
        previous = result.sha
    monkeypatch.undo()

    assert _attempts(conn, repo_id, "quiet.c") == 0, (
        "an unattributable miss must not be charged against the retry budget"
    )
    assert "quiet.c" in _queued_paths(conn, repo_id), (
        "an unattributable miss must still come back on a later pass, or it"
        " is lost even though it was never given a real chance to fail"
    )
    assert _exhausted_paths(conn, repo_id) == []
    row = _symbols_sha_row(conn, repo_id, "quiet.c")
    assert row["symbols_sha"] != row["blob_sha"], (
        "an unattributable miss must not be stamped complete either"
    )

    # decoder.c produced tags every pass and must still be complete --
    # this fix must not make genuinely covered files worse off.
    dec = _symbols_sha_row(conn, repo_id, "decoder.c")
    assert dec["symbols_sha"] == worker._symbols_stamp(dec["blob_sha"])


def test_failure_inside_upsert_does_not_desync_fts(env, monkeypatch):
    conn, cfg, project, repo_id, _, origin = env
    _run(env)

    (origin / "decoder.c").write_text('#include "decoder.h"\nint Marker(void){return 7;}\n')
    git(origin, "add", "-A")
    git(origin, "commit", "-m", "modify")

    # sqlite3.Connection is a static C type: CPython refuses attribute
    # assignment on it, at the class *or* the instance level ("cannot set
    # 'execute' attribute of immutable type 'sqlite3.Connection'" /
    # "object attribute 'execute' is read-only"), so it cannot be
    # monkeypatched directly. A Connection *subclass* is an ordinary heap
    # type and can be patched, so the failure is injected by opening a
    # second connection to the same on-disk database through a subclass,
    # and running the pass through that connection instead.
    class BoomConnection(sqlite3.Connection):
        def execute(self, sql, *a, **k):
            if sql.strip().startswith("INSERT INTO files_fts(rowid"):
                raise sqlite3.OperationalError("simulated mid-upsert failure")
            return super().execute(sql, *a, **k)

    boom_conn = sqlite3.connect(cfg.db_path, factory=BoomConnection)
    boom_conn.row_factory = sqlite3.Row
    boom_conn.execute("PRAGMA foreign_keys = ON")

    m = mirror.ensure_mirror(cfg, project, clone_url=str(origin))
    sha = mirror.head_sha(m, "main")
    tree = mirror.sync_worktree(cfg, project.gitlab_id, m, sha)
    worker.index_repo(boom_conn, cfg, project, m, tree, sha, None)
    boom_conn.close()

    # Assert via MATCH, never via COUNT: files_fts is external-content, so it
    # proxies COUNT(*) straight through to `files` -- the counts stay equal
    # even when the term index is desynced (verified: healthy files=1
    # fts_count=1 match=1; desynced files=1 fts_count=1 match=0).
    #
    # A literal `files_fts MATCH 'Marker'` count is NOT the discriminating
    # check here, even though the fixture writes "Marker" into decoder.c:
    # the correct fix is conn.rollback() of the *whole* implicit transaction,
    # which undoes the UPDATE files statement too, so decoder.c's content
    # reverts to the pre-edit "DecodeFrame" text and "Marker" is never
    # persisted anywhere -- MATCH 'Marker' is 0 both before and after the fix.
    # Verified empirically:
    #   unfixed:  files.content = new "Marker" text, FTS has no row for it
    #             -> needle="Marker", hit=None (desync: content untracked)
    #   fixed:    files.content reverted to old "DecodeFrame" text, FTS
    #             row also reverted -> needle="DecodeFrame", hit=found
    # So the real invariant is: whatever `files.content` currently says must
    # be exactly what `files_fts` can find for that rowid -- check MATCH
    # against a word actually present in the file's current content, not a
    # fixed literal that assumes the write went through.
    row = conn.execute(
        "SELECT id, content FROM files WHERE repo_id = ? AND path = ?",
        (repo_id, "decoder.c"),
    ).fetchone()
    needle = "Marker" if "Marker" in row["content"] else "DecodeFrame"
    hit = conn.execute(
        "SELECT rowid FROM files_fts WHERE files_fts MATCH ? AND rowid = ?",
        (needle, row["id"]),
    ).fetchone()
    assert hit is not None, (
        f"FTS index desynced: files.content for decoder.c contains {needle!r} "
        "but files_fts has no matching row for that file"
    )


def test_timed_out_pass_persists_last_run_timed_out_flag(env):
    conn, cfg, project, repo_id, _, _ = env
    budget_cfg = IndexConfig(data_dir=cfg.data_dir, db_path=cfg.db_path,
                             repo_time_budget_seconds=0)
    m = mirror.ensure_mirror(budget_cfg, project, clone_url=str(env[5]))
    sha = mirror.head_sha(m, "main")
    tree = mirror.sync_worktree(budget_cfg, project.gitlab_id, m, sha)

    result = worker.index_repo(conn, budget_cfg, project, m, tree, sha, None)
    assert result.timed_out is True
    row = conn.execute(
        "SELECT last_run_timed_out, last_run_symbols_failed FROM repos WHERE id = ?",
        (repo_id,),
    ).fetchone()
    assert row["last_run_timed_out"] == 1
    assert row["last_run_symbols_failed"] == 0


def test_ctags_unavailable_persists_last_run_symbols_failed_flag(env, monkeypatch):
    from argus.parse import ctags
    conn, cfg, project, repo_id, _, _ = env
    monkeypatch.setattr(ctags.shutil, "which", lambda name: None)

    result = _run(env)
    assert result.symbols_failed is True
    row = conn.execute(
        "SELECT last_run_timed_out, last_run_symbols_failed FROM repos WHERE id = ?",
        (repo_id,),
    ).fetchone()
    assert row["last_run_symbols_failed"] == 1
    assert row["last_run_timed_out"] == 0


def test_clean_pass_clears_previously_set_last_run_timed_out_flag(env):
    """A stale flag that never clears would be worse than no flag at all."""
    conn, cfg, project, repo_id, _, _ = env
    budget_cfg = IndexConfig(data_dir=cfg.data_dir, db_path=cfg.db_path,
                             repo_time_budget_seconds=0)
    m = mirror.ensure_mirror(budget_cfg, project, clone_url=str(env[5]))
    sha = mirror.head_sha(m, "main")
    tree = mirror.sync_worktree(budget_cfg, project.gitlab_id, m, sha)
    worker.index_repo(conn, budget_cfg, project, m, tree, sha, None)

    row = conn.execute("SELECT last_run_timed_out FROM repos WHERE id = ?",
                       (repo_id,)).fetchone()
    assert row["last_run_timed_out"] == 1  # sanity check the flag was actually set

    # A subsequent clean pass (no time limit) must clear it.
    _run(env)
    row = conn.execute("SELECT last_run_timed_out FROM repos WHERE id = ?",
                       (repo_id,)).fetchone()
    assert row["last_run_timed_out"] == 0


def test_an_extractor_change_re_extracts_every_file(env, monkeypatch):
    """The gate must notice a change in what a symbol ROW contains, not only a
    change in the file.

    `symbols_sha` was compared against the blob sha alone, which answers "were
    these symbols extracted from this revision of the file" -- right for a
    content change, wrong for an extractor change. Improve the extractor and
    every file still looks current, so nothing is re-parsed and the improvement
    reaches only the files somebody happens to edit afterwards.

    That is not hypothetical: adding the doc column would have reached a real
    estate over months, half-documented, with no way to tell which half. The
    contract version is prefixed into the stamp so bumping it invalidates every
    row on the next pass, with no migration and no manual step.
    """
    conn, cfg, project, repo_id, _, _ = env
    _run(env)

    from argus import worker
    from argus.parse import ctags

    blob = conn.execute("SELECT blob_sha FROM files WHERE path = 'decoder.c'"
                        ).fetchone()["blob_sha"]
    assert worker._already_current(conn, repo_id, "decoder.c", blob) is True

    # Same file, same blob, different extractor.
    monkeypatch.setattr(ctags, "SYMBOL_CONTRACT_VERSION", "999",
                        raising=False)
    monkeypatch.setattr(worker.ctags, "SYMBOL_CONTRACT_VERSION", "999")
    assert worker._already_current(conn, repo_id, "decoder.c", blob) is False, \
        "an extractor change did not invalidate the stored symbols"


def test_rows_written_before_the_stamp_existed_are_re_extracted(env):
    """A bare blob sha is what every row held before the contract version was
    introduced. It cannot equal a prefixed stamp, so an existing index
    re-extracts itself rather than staying as it was."""
    conn, cfg, project, repo_id, _, _ = env
    _run(env)
    from argus import worker

    blob = conn.execute("SELECT blob_sha FROM files WHERE path = 'decoder.c'"
                        ).fetchone()["blob_sha"]
    conn.execute("UPDATE files SET symbols_sha = ? WHERE path = 'decoder.c'", (blob,))
    conn.commit()
    assert worker._already_current(conn, repo_id, "decoder.c", blob) is False


def test_the_stamp_is_stable_within_a_version(env):
    """The common path must not re-extract. A stamp that changed between reads
    would make every pass redo every file, which is the livelock the gate
    exists to prevent."""
    conn, cfg, project, repo_id, _, _ = env
    _run(env)
    from argus import worker

    blob = conn.execute("SELECT blob_sha FROM files WHERE path = 'decoder.c'"
                        ).fetchone()["blob_sha"]
    assert worker._symbols_stamp(blob) == worker._symbols_stamp(blob)
    assert worker._already_current(conn, repo_id, "decoder.c", blob) is True
    _run(env)
    assert worker._already_current(conn, repo_id, "decoder.c", blob) is True


def test_a_stale_contract_forces_a_pass_to_look_at_unchanged_files(env):
    """The stamp alone is not enough, and this is why.

    `_already_current` makes an individual file look stale, but a pass works
    from a git DIFF -- and a file nobody has committed to is not in it. So
    nothing ever looks. Without the recorded version, bumping the extractor
    reaches only the files edited afterwards: the doc column arriving across a
    real estate over months, half-documented with no way to tell which half.

    An index with no record at all is stale by definition -- it was built before
    the version was tracked.
    """
    conn, cfg, project, repo_id, _, _ = env
    from argus import worker

    # Freshly migrated, nothing recorded yet: stale.
    assert worker.contract_is_stale(conn, repo_id) is True

    worker.record_contract(conn, repo_id)
    assert worker.contract_is_stale(conn, repo_id) is False

    # A pass that finished records it; a later commit does not make it stale.
    _run(env)
    assert worker.contract_is_stale(conn, repo_id) is False


def test_a_stale_contract_re_extracts_without_a_commit(env, monkeypatch):
    """The end-to-end version: an unchanged tree, a bumped extractor, and the
    symbol rows come back re-extracted."""
    conn, cfg, project, repo_id, _, _ = env
    from argus import worker
    from argus.parse import ctags

    _run(env)
    assert worker.contract_is_stale(conn, repo_id) is False

    # Same files, same commits -- only the extractor is different.
    monkeypatch.setattr(worker.ctags, "SYMBOL_CONTRACT_VERSION", "999")
    assert worker.contract_is_stale(conn, repo_id) is True

    before = conn.execute("SELECT symbols_sha FROM files WHERE path = 'decoder.c'"
                          ).fetchone()["symbols_sha"]
    _run(env)                       # no new commit; nothing in the git diff
    after = conn.execute("SELECT symbols_sha FROM files WHERE path = 'decoder.c'"
                         ).fetchone()["symbols_sha"]
    assert after != before, "an unchanged file was not re-extracted"
    assert after.startswith("999:"), after


def test_a_timed_out_pass_does_not_record_the_contract(env, monkeypatch):
    """Recording it after a pass that was cut short would strand every file the
    pass did not reach -- until somebody happened to edit them, which is exactly
    the failure the whole mechanism exists to prevent.

    Driven through the pass rather than by calling the recorder, because the
    guard being tested is the condition around the call, not the call itself.
    """
    conn, cfg, project, repo_id, m, _ = env
    from argus import worker

    recorded: list[str] = []
    monkeypatch.setattr(worker, "record_contract",
                        lambda c, r: recorded.append("called"))
    # The repository is already at HEAD, so this pass has work to do only if the
    # contract is stale -- force that, then cut the pass short.
    monkeypatch.setattr(worker, "contract_is_stale", lambda c, r: True)
    # A zero time budget makes the pass stop before it reaches any file, which
    # is the same shape as a timeout on a large estate.
    cfg_short = IndexConfig(data_dir=cfg.data_dir, db_path=cfg.db_path,
                            repo_time_budget_seconds=0)
    _run(env)
    recorded.clear()
    m2 = mirror.ensure_mirror(cfg_short, project, clone_url=str(env[5]))
    sha = mirror.head_sha(m2, "main")
    tree = mirror.sync_worktree(cfg_short, project.gitlab_id, m2, sha)
    result = worker.index_repo(conn, cfg_short, project, m2, tree, sha, None)
    assert result.timed_out is True, "the pass did not time out as set up"
    assert recorded == [], "a timed-out pass recorded the contract version"


def test_one_repo_finishing_does_not_unlock_the_others(env, monkeypatch):
    """The bug a live run found, and the reason the key is per repo.

    `record_contract` fires at the end of each repository's pass. With ONE
    global key, the first repository to finish marked the whole index current
    and every repository after it took the "up to date" shortcut: one repo
    re-extracted, the rest did not, and the run reported success. Measured on
    the reference stack -- driver-shim stamped, eal-core and etl-decoder both
    "up to date", zero docs anywhere.
    """
    conn, cfg, project, repo_id, _, _ = env
    from argus import worker

    other = repo_id + 1000
    assert worker.contract_is_stale(conn, repo_id) is True
    assert worker.contract_is_stale(conn, other) is True

    worker.record_contract(conn, repo_id)
    assert worker.contract_is_stale(conn, repo_id) is False
    assert worker.contract_is_stale(conn, other) is True, \
        "recording one repository's contract cleared another's"


def test_re_indexing_does_not_leave_orphaned_vectors(env, monkeypatch):
    """The vec tables have no foreign key and cannot have one.

    `symbol_embeddings` cascades from `symbols`, so re-indexing a file deletes
    its symbol rows, writes new ones with NEW ids, and leaves the vectors behind
    in `vec_symbols_bin` and `vec_symbols_i8` for ever. Found on the reference
    stack as 10 vectors for 6 embeddings after a handful of re-indexes.

    The growth is the lesser problem. The coarse KNN stage ranks over those
    rows, so an orphan occupies one of the k slots and is then discarded by the
    ACL re-check -- the search returns fewer real results than it asked for,
    with nothing in the response explaining why.
    """
    import sqlite3
    import pytest as _pytest
    from argus import semantic

    conn, cfg, project, repo_id, _, _ = env
    try:
        semantic.ensure_vec_tables(conn)
    except Exception as exc:                          # noqa: BLE001
        _pytest.skip(f"sqlite-vec unavailable here: {exc}")

    _run(env)
    ids = [r["id"] for r in conn.execute("SELECT id FROM symbols")]
    if not ids:
        _pytest.skip("this fixture's symbols do not survive extraction")
    for sid in ids:
        conn.execute("INSERT OR REPLACE INTO symbol_embeddings"
                     " (symbol_id, repo_id, embed_text, model, dim, text_version)"
                     " VALUES (?, ?, 't', 'm', 1, '2')", (sid, repo_id))
    conn.execute("DELETE FROM symbol_embeddings")     # what a cascade leaves
    conn.commit()

    # Simulate a re-index: symbol rows replaced, embeddings cascaded away, the
    # vec rows still pointing at ids that no longer exist.
    for sid in ids[:1]:
        conn.execute("INSERT OR REPLACE INTO vec_symbols_bin"
                     " (symbol_id, embedding) VALUES (?, vec_bit(?))",
                     (sid, semantic.to_bits([0.0] * semantic.embed_module.EMBED_DIM)))
    conn.commit()
    assert conn.execute("SELECT COUNT(*) FROM vec_symbols_bin").fetchone()[0] == 1

    dropped = semantic.prune_orphans(conn)
    assert dropped >= 1, "an orphaned vector survived"
    assert conn.execute("SELECT COUNT(*) FROM vec_symbols_bin").fetchone()[0] == 0
