# Phase 1 Hardening & Measurement Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the indexer-correctness gaps found by the Phase 1 reviews, then run the first real index against GitLab and record what it actually costs.

**Architecture:** No new modules. One migration adds a `files.symbols_sha` marker that replaces the current practice of *inferring* symbol completion from the existence of symbol rows — that inference is the root cause of three separate defects. The remaining tasks close holes in the retry queue and the transaction boundary. The final task is a measurement run against the real GitLab.

**Tech Stack:** Python 3.11+, SQLite (FTS5 external-content), universal-ctags, git, pytest.

Spec: [`../specs/2026-07-28-local-code-assistant-design.md`](../specs/2026-07-28-local-code-assistant-design.md)
Phase 1 plan: [`2026-07-28-argus-phase1-indexer.md`](2026-07-28-argus-phase1-indexer.md)

## Why this runs before Phase 2

Phase 2 exposes the index to real developers through an agent. Every defect below causes the index to hold or report something untrue — missing symbols, a permanently blacklisted file, a repo that claims to be current when its last pass timed out. Shipping the retrieval surface on top of knowingly wrong data means the first thing your developers experience is the agent confidently answering from it.

## Global Constraints

Every task's requirements implicitly include this section.

- **Python `>=3.11`.** Deployment host is Linux; development is Windows. No 3.12+ syntax, no hardcoded path separators.
- **The package is `argus`.** Imports are `from argus...`; the CLI is `argus`; the token env var is `ARGUS_GITLAB_TOKEN`.
- **`allowed_repo_ids` stays the first positional parameter, with no default, on every public function in `argus/store/queries.py`.** Enforced by `test_every_public_query_takes_allowlist_first`.
- **Never edit an applied migration.** `001_initial.sql`, `002_index_errors_repo_idx.sql`, and `003_retry_attempts.sql` are already applied to real databases. New schema goes in `004_*.sql` and above.
- **One bad file must never abort a repo.**
- **`last_indexed_sha` advances only when the repo's entire changed set is complete.** Crash recovery depends on replaying the same diff into idempotent writes.
- **No network in tests.** Real local git repos in `tmp_path`, the real ctags binary, real SQLite. No mocking the tool under test.
- Conventional commit prefixes (`feat:`, `fix:`, `test:`, `chore:`, `docs:`).
- Baseline suite is **91 passed, 0 skipped**. It must never go down.
- **Every regression test must be demonstrated to fail.** Revert the production change in your working tree, run the test, capture the failing output, restore, run again, capture the pass. Report both. This is not ceremony: Task 1 shipped a test whose assertions were satisfied by unrelated fixture files, so it passed with the bug fully reintroduced. Assert on the specific behaviour under test, never on aggregate counters other fixtures can satisfy.
- Phase 1 scope only. No embeddings, no MCP server, no ACL module, no tree-sitter, no cross-repo include resolution.

## File Structure

| File | Change |
|---|---|
| `argus/store/migrations/004_symbols_sha.sql` | Create — `files.symbols_sha` column |
| `argus/store/migrations/005_repo_run_state.sql` | Create — persist last-run flags on `repos` |
| `argus/store/writes.py` | Modify — set `symbols_sha`; record run state; reset retry counter |
| `argus/store/queries.py` | Modify — `index_status` returns the run-state flags |
| `argus/worker.py` | Modify — completion check, retry re-enqueue, transaction boundary |
| `docs/phase1-measurements.md` | Create — the real numbers |

---

### Task 1: Replace inferred symbol completion with an explicit marker

**Files:**
- Create: `argus/store/migrations/004_symbols_sha.sql`
- Modify: `argus/store/writes.py` (`replace_symbols`)
- Modify: `argus/worker.py` (`_already_current`)
- Test: `tests/test_worker.py`, `tests/store/test_writes.py`

**Interfaces:**
- Consumes: existing `writes.replace_symbols(conn, repo_id, file_id, symbols)`.
- Produces: `replace_symbols(conn, repo_id, file_id, symbols, blob_sha)` — one extra **required** positional argument, the blob sha the symbols were extracted from. `files.symbols_sha` is set to it. `_already_current` returns True only when the stored `blob_sha` matches the tree AND `symbols_sha == blob_sha`.

**Why this task exists.** `_already_current` currently asks "does this file have any symbol rows?" as a proxy for "were symbols extracted successfully?". That proxy is wrong in both directions: a file that legitimately contains zero symbols (an include-only `.c`, a pure macro header) never satisfies it and is re-read, re-upserted and re-FTS-indexed on every full-listing pass; and a file whose fresh extraction failed can still satisfy it using symbol rows from an *older* revision. Making completion explicit fixes both, and it is the precondition for Task 2.

- [ ] **Step 1: Write the failing tests**

Add to `tests/test_worker.py`:

```python
def test_file_with_zero_symbols_is_not_reprocessed(env, monkeypatch):
    """An include-only .c has no symbols; it must still count as complete."""
    conn, cfg, project, repo_id, _, origin = env
    (origin / "empty.c").write_text('#include "decoder.h"\n')
    git(origin, "add", "-A")
    git(origin, "commit", "-m", "add include-only file")

    first = _run(env)
    assert "empty.c" in {r["path"] for r in conn.execute("SELECT path FROM files")}

    # Force the full-listing path so _already_current is what decides.
    # Spy on the specific file: aggregate counters are satisfied by unrelated
    # fixtures (build/gen.c, logo.bin are skipped by filters on every pass),
    # so asserting `second.skipped >= 1` would pass with the bug reintroduced.
    reprocessed = []
    real_upsert = writes.upsert_file
    def spy(conn_, *, repo_id, path, **kw):
        reprocessed.append(path)
        return real_upsert(conn_, repo_id=repo_id, path=path, **kw)
    monkeypatch.setattr(worker.writes, "upsert_file", spy)

    _run(env, old_sha=None)
    assert "empty.c" not in reprocessed


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
```

Add to `tests/store/test_writes.py`:

```python
def test_replace_symbols_records_the_blob_sha(conn, repo_id):
    fid = writes.upsert_file(conn, repo_id=repo_id, path="a.c", lang="c",
                             size=1, blob_sha="aaa", content="x")
    writes.replace_symbols(conn, repo_id, fid, [
        {"name": "F", "kind": "function", "line": 1, "end_line": 2,
         "signature": None, "scope": None, "is_public": 1},
    ], "aaa")
    assert conn.execute("SELECT symbols_sha FROM files WHERE id = ?",
                        (fid,)).fetchone()["symbols_sha"] == "aaa"


def test_replace_symbols_records_the_sha_even_when_empty(conn, repo_id):
    """Zero symbols is a valid successful result, not an incomplete one."""
    fid = writes.upsert_file(conn, repo_id=repo_id, path="b.c", lang="c",
                             size=1, blob_sha="bbb", content="x")
    writes.replace_symbols(conn, repo_id, fid, [], "bbb")
    assert conn.execute("SELECT symbols_sha FROM files WHERE id = ?",
                        (fid,)).fetchone()["symbols_sha"] == "bbb"
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `python -m pytest tests/test_worker.py -k "zero_symbols or stale_symbols" tests/store/test_writes.py -k "blob_sha or when_empty" -v`
Expected: FAIL — `sqlite3.OperationalError: no such column: symbols_sha`, and `replace_symbols()` takes 4 positional arguments.

- [ ] **Step 3: Write the migration**

`argus/store/migrations/004_symbols_sha.sql`:

```sql
-- Explicit marker for "symbols were successfully extracted from this blob".
-- Replaces inferring completion from the existence of symbol rows, which is
-- wrong for files that legitimately contain zero symbols and for files whose
-- fresh extraction failed while older symbol rows survived.
ALTER TABLE files ADD COLUMN symbols_sha TEXT;
```

- [ ] **Step 4: Update `replace_symbols` and `_already_current`**

In `argus/store/writes.py`, change the signature and add the marker write:

```python
def replace_symbols(conn: sqlite3.Connection, repo_id: int, file_id: int,
                    symbols: list[dict], blob_sha: str) -> None:
    conn.execute("DELETE FROM symbols WHERE file_id = ?", (file_id,))
    conn.executemany(
        "INSERT INTO symbols"
        " (repo_id, file_id, name, kind, line, end_line, signature, scope, is_public)"
        " VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)",
        [
            (repo_id, file_id, s["name"], s["kind"], s["line"], s.get("end_line"),
             s.get("signature"), s.get("scope"), int(s.get("is_public", 0)))
            for s in symbols
        ],
    )
    # An empty symbol list is a successful extraction, not an incomplete one.
    conn.execute("UPDATE files SET symbols_sha = ? WHERE id = ?", (blob_sha, file_id))
    conn.commit()
```

In `argus/worker.py`, replace the "has symbol rows" clause in `_already_current` with the marker comparison:

```python
def _already_current(conn, repo_id: int, path: str, blob_sha: str) -> bool:
    """True when this exact blob is stored AND its symbols were extracted from it.

    Keep the existing parameter name — this replaces the body only, so every
    existing call site stays valid.
    """
    if not blob_sha:
        return False
    row = conn.execute(
        "SELECT blob_sha, symbols_sha FROM files WHERE repo_id = ? AND path = ?",
        (repo_id, path),
    ).fetchone()
    if row is None:
        return False
    return row["blob_sha"] == blob_sha and row["symbols_sha"] == blob_sha
```

Update the `replace_symbols` call site in `_apply_symbols` to pass the blob sha for that path. It is already available in the `shas` mapping computed by `blob_shas`; thread it through rather than re-deriving it.

- [ ] **Step 5: Run the full suite**

Run: `python -m pytest -q`
Expected: all pass, total ≥ 95, 0 skipped.

- [ ] **Step 6: Commit**

```bash
git add argus/store/migrations/004_symbols_sha.sql argus/store/writes.py argus/worker.py tests/
git commit -m "fix: track symbol extraction completion explicitly via symbols_sha"
```

---

### Task 2: Re-enqueue retry-origin paths when symbol extraction fails

**Files:**
- Modify: `argus/worker.py` (`index_repo`)
- Test: `tests/test_worker.py`

**Interfaces:**
- Consumes: Task 1's `symbols_sha`; existing `writes.enqueue_retry`.
- Produces: no signature change. Behavioural guarantee: a path that entered a pass **from the retry queue** and whose symbol extraction then failed is re-enqueued, so it is not lost.

**The hole.** A path fails to read in pass N, so it is queued and the SHA advances. In pass N+1 it arrives only via the queue, reads fine, and is upserted — then ctags fails. `symbols_failed` holds the SHA, but nothing re-enqueues it, because `failed_paths` only collects paths that *errored during the file loop*. In pass N+2 the diff no longer contains it (it changed before the current `old_sha`, which is exactly why it was queued), and the queue is empty. It is never revisited: stored with current content and no symbols, permanently, with `errors=0`.

- [ ] **Step 1: Write the failing test**

```python
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
    assert conn.execute("SELECT COUNT(*) c FROM index_queue").fetchone()["c"] == 1

    # Pass N+1: it reads fine but ctags fails. It must stay queued.
    from argus.parse import ctags as ctags_mod
    monkeypatch.setattr(ctags_mod.shutil, "which", lambda name: None)
    second = _run(env, old_sha=first.sha)
    monkeypatch.undo()
    assert second.symbols_failed is True
    assert conn.execute("SELECT COUNT(*) c FROM index_queue").fetchone()["c"] == 1, \
        "retry-origin path was dropped after its symbol extraction failed"

    # Pass N+2: ctags healthy. The file must get correct symbols.
    _run(env, old_sha=first.sha)
    names = {r["name"] for r in conn.execute(
        "SELECT s.name FROM symbols s JOIN files f ON f.id = s.file_id"
        " WHERE f.path = 'decoder.c'")}
    assert "DecodeFrameV2" in names
```

- [ ] **Step 2: Run test to verify it fails**

Run: `python -m pytest tests/test_worker.py::test_retry_path_whose_symbols_fail_is_requeued -v`
Expected: FAIL at the `index_queue` count assertion — the path was dropped.

- [ ] **Step 3: Implement**

In `index_repo`, where the retry queue is re-populated, include retry-origin paths that were in `to_parse` when symbol extraction failed:

```python
    if result.symbols_failed:
        # A retry-origin path is not in any future diff, so if its symbols failed
        # it must stay queued or it is lost permanently.
        failed_paths.extend(p for p in to_parse if p in retry_set)
```

Place this before the existing `enqueue_retry` call, and make sure `enqueue_retry` de-duplicates (it already sorts and de-duplicates via `sorted(set(...))`).

- [ ] **Step 4: Run the full suite**

Run: `python -m pytest -q`
Expected: all pass, 0 skipped.

- [ ] **Step 5: Commit**

```bash
git add argus/worker.py tests/test_worker.py
git commit -m "fix: keep retry-origin paths queued when symbol extraction fails"
```

---

### Task 3: Reset the retry counter on success, and give operators an escape hatch

**Files:**
- Modify: `argus/worker.py`
- Modify: `argus/cli.py` (add `--reset-retries`)
- Test: `tests/test_worker.py`, `tests/test_cli.py`

**Interfaces:**
- Produces: `argus index --config PATH --reset-retries` clears `retry_attempts` for all repos (or one, with `--repo`) before indexing.

**The hole.** `clear_retry_attempts` is keyed off the set of *queued* paths, so once a path exhausts the cap it is never queued again and its counter stays at the cap forever. An operator who fixes the underlying cause (an ACL, a too-long path, an antivirus quarantine) gets no recovery: the next transient error on that file gives up immediately with zero retries.

- [ ] **Step 1: Write the failing tests**

```python
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
```

```python
def test_reset_retries_flag_clears_counters(config_file, fake_projects, capsys):
    assert cli.main(["index", "--config", str(config_file)]) == 0
    assert cli.main(["index", "--config", str(config_file), "--reset-retries"]) == 0
    assert "reset retry counters" in capsys.readouterr().out
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `python -m pytest tests/test_worker.py -k retry_counter tests/test_cli.py -k reset_retries -v`
Expected: FAIL — the counter survives, and `--reset-retries` is an unrecognised argument.

- [ ] **Step 3: Implement**

In `worker.index_repo`, clear the counter for every path that indexed successfully this pass, not only those that arrived via the queue:

```python
    # Clear on success regardless of how the path entered this pass, so a fixed
    # underlying cause (permissions, path length, AV) actually recovers.
    if indexed_paths:
        writes.clear_retry_attempts(conn, repo_id, indexed_paths)
```

In `argus/cli.py`, add the flag to the `index` subparser and act on it before the repo loop:

```python
    p_index.add_argument("--reset-retries", action="store_true",
                         help="Clear retry counters before indexing (recovery escape hatch)")
```

```python
    if reset_retries:
        conn.execute("DELETE FROM retry_attempts")
        conn.commit()
        print("reset retry counters")
```

- [ ] **Step 4: Run the full suite**

Run: `python -m pytest -q`
Expected: all pass, 0 skipped.

- [ ] **Step 5: Commit**

```bash
git add argus/worker.py argus/cli.py tests/
git commit -m "fix: clear retry counters on success and add --reset-retries"
```

---

### Task 4: Roll back before recording a per-file error

**Files:**
- Modify: `argus/worker.py`
- Test: `tests/test_worker.py`

**Interfaces:** No signature change. Guarantee: a failure part-way through `upsert_file` cannot leave the FTS index desynchronised from `files`.

**The hole.** The per-file `except` arms call `writes.record_error`, which commits. `upsert_file` performs an FTS delete, then updates `files`, then re-inserts into FTS — all in one implicit transaction. An exception raised between those steps would be committed by the subsequent `record_error`, leaving `files_fts` missing an entry that `files` still has. External-content FTS has no triggers to repair this.

- [ ] **Step 1: Write the failing test**

```python
def test_failure_inside_upsert_does_not_desync_fts(env, monkeypatch):
    conn, cfg, project, repo_id, _, origin = env
    _run(env)

    (origin / "decoder.c").write_text('#include "decoder.h"\nint Marker(void){return 7;}\n')
    git(origin, "add", "-A")
    git(origin, "commit", "-m", "modify")

    # sqlite3.Connection is a static C type — CPython refuses attribute
    # assignment on it at class or instance level, so it cannot be
    # monkeypatched. A Connection *subclass* is an ordinary heap type, so
    # inject the failure by running the pass through one.
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

    # Assert via MATCH, never via COUNT. files_fts is external-content, so it
    # proxies COUNT(*) straight through to `files` — the counts stay equal even
    # when the term index is desynced. Only a MATCH reveals it. Verified:
    # healthy files=1 fts_count=1 match=1; desynced files=1 fts_count=1 match=0.
    hits = conn.execute(
        "SELECT COUNT(*) c FROM files_fts WHERE files_fts MATCH 'Marker'"
    ).fetchone()["c"]
    assert hits == 1, "FTS index desynced: content in `files` is not searchable"
```

- [ ] **Step 2: Run test to verify it fails**

Run: `python -m pytest tests/test_worker.py::test_failure_inside_upsert_does_not_desync_fts -v`
Expected: FAIL on the count mismatch.

- [ ] **Step 3: Implement**

Add `conn.rollback()` as the first statement of every per-file `except` arm in `index_repo`, before `record_error`:

```python
        except (OSError, MemoryError) as exc:
            conn.rollback()   # discard any partial FTS/files work before committing the error
            writes.record_error(conn, repo_id, change.path, "read", str(exc), int(now()))
            result.errors += 1
            failed_paths.append(change.path)
            continue
```

Apply the same to the broad `except Exception` arm around the store path.

- [ ] **Step 4: Run the full suite**

Run: `python -m pytest -q`
Expected: all pass, 0 skipped.

- [ ] **Step 5: Commit**

```bash
git add argus/worker.py tests/test_worker.py
git commit -m "fix: roll back partial work before recording a per-file error"
```

---

### Task 5: Persist run state so `index_status` can report it

**Files:**
- Create: `argus/store/migrations/005_repo_run_state.sql`
- Modify: `argus/store/writes.py`, `argus/store/queries.py`, `argus/worker.py`, `argus/cli.py`
- Test: `tests/store/test_queries.py`, `tests/test_worker.py`

**Interfaces:**
- Produces: `writes.record_run_state(conn, repo_id, *, timed_out, symbols_failed, ts)`. `queries.index_status` gains `last_run_timed_out`, `last_run_symbols_failed`, and `queued_retries` in its returned rows.

**Why.** `timed_out` and `symbols_failed` currently live only in the in-memory `IndexResult` and are printed once. Phase 2 exposes `index_status` as an MCP tool whose stated purpose is letting the agent qualify stale answers — but the two states most in need of qualification are exactly these, and they are unqueryable. A developer whose `find_symbol` returns nothing gets no signal distinguishing "no such symbol" from "symbols were never extracted for that repo".

- [ ] **Step 1: Write the failing tests**

```python
def test_index_status_reports_last_run_flags(two_repos):
    conn, ids = two_repos
    rid = ids["g/alpha"]
    writes.record_run_state(conn, rid, timed_out=True, symbols_failed=False, ts=1234)
    row = [r for r in queries.index_status([rid], conn)][0]
    assert row["last_run_timed_out"] == 1
    assert row["last_run_symbols_failed"] == 0
```

```python
def test_index_status_reports_queued_retries(two_repos):
    conn, ids = two_repos
    rid = ids["g/beta"]
    writes.enqueue_retry(conn, rid, ["a.c", "b.c"], "read error")
    row = [r for r in queries.index_status([rid], conn)][0]
    assert row["queued_retries"] >= 1
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `python -m pytest tests/store/test_queries.py -k "last_run or queued_retries" -v`
Expected: FAIL — `record_run_state` does not exist; the columns are absent.

- [ ] **Step 3: Write the migration**

`argus/store/migrations/005_repo_run_state.sql`:

```sql
-- Persist the outcome of the most recent indexing pass so index_status can
-- report partial coverage. Phase 2 exposes index_status to an agent whose
-- purpose is qualifying stale answers; these are the states worth qualifying.
ALTER TABLE repos ADD COLUMN last_run_timed_out INTEGER NOT NULL DEFAULT 0;
ALTER TABLE repos ADD COLUMN last_run_symbols_failed INTEGER NOT NULL DEFAULT 0;
ALTER TABLE repos ADD COLUMN last_run_at INTEGER;
```

- [ ] **Step 4: Implement**

`argus/store/writes.py`:

```python
def record_run_state(conn: sqlite3.Connection, repo_id: int, *,
                     timed_out: bool, symbols_failed: bool, ts: int) -> None:
    conn.execute(
        "UPDATE repos SET last_run_timed_out = ?, last_run_symbols_failed = ?,"
        "                 last_run_at = ? WHERE id = ?",
        (int(timed_out), int(symbols_failed), ts, repo_id),
    )
    conn.commit()
```

In `argus/store/queries.py`, extend `index_status`'s SELECT with the three new columns plus a retry count. Keep `allowed_repo_ids` first positional:

```python
        "SELECT r.id AS repo_id, r.path_with_namespace, r.last_indexed_sha,"
        "       r.last_indexed_at, r.last_run_timed_out, r.last_run_symbols_failed,"
        "       r.last_run_at,"
        "       (SELECT COUNT(*) FROM files   WHERE repo_id = r.id) AS files,"
        "       (SELECT COUNT(*) FROM symbols WHERE repo_id = r.id) AS symbols,"
        "       (SELECT COUNT(*) FROM index_errors WHERE repo_id = r.id) AS errors,"
        "       (SELECT COUNT(*) FROM index_queue WHERE repo_id = r.id) AS queued_retries"
        "  FROM repos r"
```

Call `record_run_state` at the end of `index_repo`, unconditionally, so a clean pass clears a previously-set flag.

Extend the `argus status` output to show the flags when set.

- [ ] **Step 5: Run the full suite**

Run: `python -m pytest -q`
Expected: all pass, 0 skipped.

- [ ] **Step 6: Commit**

```bash
git add argus/store/migrations/005_repo_run_state.sql argus/store/ argus/worker.py argus/cli.py tests/
git commit -m "feat: persist last-run state and surface it through index_status"
```

---

### Task 6: First real index run and measurement

**Files:**
- Create: `docs/phase1-measurements.md`

**Interfaces:**
- Consumes: the whole hardened indexer.
- Produces: measured figures that size Phase 2 and Phase 4 — wall-clock for a full index, database size, file/symbol/public-symbol counts, error breakdown, and **the answer to the repository-coverage question**.

This task has no tests. It runs the finished system against the real GitLab and records what happened. The spec's figures (~70–90k vectors, ~700 MB, sub-hour rebuild) are projections from a stated line count; Phase 4's cost model depends on the real public-symbol count, and Phase 2's webhook-versus-poll decision depends on the real indexing time.

**This task requires the operator's GitLab instance and service token. It cannot be completed by an agent.**

- [ ] **Step 1: Settle the repository-coverage question first**

`argus/gitlab.py` calls `GET /api/v4/projects` with `membership=false`. For a **non-admin** token that returns only *public* projects. If the internal repositories are private and the service token is not an explicit member, the index will silently cover a fraction of them and report success.

Verify before trusting any number:

```bash
curl -s -H "PRIVATE-TOKEN: $ARGUS_GITLAB_TOKEN" "https://<gitlab-host>/api/v4/projects?per_page=1&membership=false" -I | head -3
```

Compare the `X-Total` header against the project count you expect. If it is short, either grant the service token admin, or change the call to `membership=true` and add the token's user to every group. **Record which you chose in the measurements document** — it determines what the coverage number means.

- [ ] **Step 2: Verify prerequisites on the index host**

```bash
python3 --version && git --version && ctags --version | head -1
```
Expected: Python 3.11+, git 2.x, and **"Universal Ctags"**. Exuberant Ctags has no `--output-format=json`; `argus index` refuses to start on it, which is the intended behaviour.

- [ ] **Step 3: Run the full index, timed**

```bash
export ARGUS_GITLAB_TOKEN=glpat-xxxxxxxxxxxx
time argus index --config config.yaml
```
Expected: one line per repository. Per-repo failures print to stderr and do not stop the run. Note any repo reporting `TIMED-OUT` or `SYMBOLS-FAILED`.

- [ ] **Step 4: Collect the numbers**

```bash
argus status --config config.yaml
```

```bash
du -sh /var/lib/argus /var/lib/argus/index.db
```

```bash
sqlite3 /var/lib/argus/index.db "SELECT COUNT(*) AS files FROM files; SELECT COUNT(*) AS symbols FROM symbols; SELECT COUNT(*) AS public_symbols FROM symbols WHERE is_public = 1; SELECT stage, COUNT(*) n FROM index_errors GROUP BY stage ORDER BY n DESC; SELECT COUNT(*) AS repos_with_queued_retries FROM (SELECT DISTINCT repo_id FROM index_queue);"
```

- [ ] **Step 5: Confirm incrementality**

```bash
time argus index --config config.yaml
```
Expected: every unchanged repo reports `up to date` and the run finishes in seconds. If it does not, incremental detection is not working and that is a finding, not a nuisance.

- [ ] **Step 6: Write up the results**

Create `docs/phase1-measurements.md` recording: the coverage decision from Step 1 and the resulting project count; wall-clock for the full index and for the incremental re-run; repos indexed and failed; file, symbol and **public symbol** counts (the last is the Phase 4 vector estimate); database size and total data-directory size; the error breakdown by stage; and any repo that took disproportionately long or hit the time budget.

- [ ] **Step 7: Commit**

```bash
git add docs/phase1-measurements.md
git commit -m "docs: record the first full index measurements"
```

---

## Completion Criteria

- [ ] `python -m pytest -q` passes with 0 skips
- [ ] A file with zero symbols is not re-processed on repeated full-listing passes
- [ ] A retry-origin path whose symbols fail is still queued afterwards
- [ ] `argus index --reset-retries` clears counters
- [ ] `argus status` reports `TIMED-OUT` / `SYMBOLS-FAILED` from persisted state, not just from the run that produced them
- [ ] `docs/phase1-measurements.md` records real numbers, including the coverage decision

## What This Unblocks

Phase 2's plan assumes an index whose `index_status` is trustworthy — the ACL module gates access to it, and the MCP server hands it to an agent that will believe what it says. Task 5 in particular is a prerequisite: without persisted run state, the agent cannot distinguish "no such symbol" from "this repo's symbols were never extracted".
