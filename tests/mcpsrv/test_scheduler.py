"""The automatic reindex timer.

WHY THIS FILE EXISTS

The stack shipped with nineteen alert rules, a stale gauge and an admin
console that could start a pass -- and nothing anywhere that ran one on a
timer. `argus index --interval 900` existed, was documented as the fallback
the design specifies, and no service ever called it. So the index only
advanced when somebody noticed and pressed a button, which means the
ArgusIndexStale alert would have been a permanent warning on every healthy
deployment -- and a permanent warning is how people learn to ignore all of
them.

These pin the two decisions that make the fix correct rather than merely
present:

  * the schedule runs in the SERVE process, so the one `_index_lock` that
    already serialises manual runs also serialises scheduled ones. Two
    processes writing one SQLite index, with no `busy_timeout` anywhere in
    `store.connect`, would fail each other with "database is locked" and
    neither failure would say why.

  * a deployment that does not want the timer gets a console that says so out
    loud, rather than a page that looks identical to one that does.
"""
from __future__ import annotations

import threading
import time

import httpx
import pytest
from starlette.testclient import TestClient

import argus.mcpsrv.server as server_mod
from argus.config import Config, GitLabConfig, IndexConfig
from argus.mcpsrv import metrics
from argus.mcpsrv.server import create_app
from argus.store import writes
from argus.store.db import open_db


@pytest.fixture(autouse=True)
def clean_job_state():
    """`_index_job` is module state shared by every test in the process. One
    test leaving it `running` would make the next one's start silently refuse,
    which is a confusing way to fail."""
    with server_mod._index_lock:
        server_mod._index_job.update(state="idle", branches=[], started=None,
                                     finished=None, returncode=None, tail=[],
                                     trigger=None)
    yield
    with server_mod._index_lock:
        server_mod._index_job.update(state="idle", branches=[], started=None,
                                     finished=None, returncode=None, tail=[],
                                     trigger=None)


def _cfg(tmp_path, *, last_run_at=None, repos=True):
    db_path = tmp_path / "argus.db"
    conn = open_db(db_path)
    if repos:
        rid = writes.upsert_repo(conn, gitlab_id=101, path_with_namespace="g/a",
                                 default_branch="main", http_url="x")
        if last_run_at is not None:
            conn.execute("UPDATE repos SET last_run_at = ? WHERE id = ?",
                         (last_run_at, rid))
        conn.commit()
    conn.close()
    return Config(
        gitlab=GitLabConfig(url="https://gl.test", token="service-token"),
        index=IndexConfig(data_dir=tmp_path / "data", db_path=db_path),
    )


@pytest.fixture
def cfg(tmp_path):
    """The same shape as test_server.cfg, kept local because the two files are
    deliberately independent: a fixture shared between them would let one
    file's change silently alter what the other is testing."""
    return _cfg(tmp_path)


@pytest.fixture
def admin_cfg(tmp_path, monkeypatch):
    monkeypatch.setenv("ARGUS_ADMIN_TOKEN", "admin-secret")
    return _cfg(tmp_path, last_run_at=int(time.time()) - 7200)


class FakeProc:
    """A child that produces no output and exits 0, so a test can start a
    scheduled pass without running a real indexer."""

    stdout = iter([])

    def wait(self):
        return 0


@pytest.fixture
def no_subprocess(monkeypatch):
    """Start runs without running anything, and WITHOUT racing the tests.

    `_run_index` is what a real worker thread calls, and it ends by setting
    `state` back to "idle". A test that starts a run and then asserts the slot
    is still claimed would therefore pass or fail depending on how fast the
    thread got there. Stubbing both means the state a test sets stays set."""
    started: list[list[str]] = []

    def fake_popen(argv, **kw):
        started.append(argv)
        return FakeProc()

    monkeypatch.setattr(server_mod.subprocess, "Popen", fake_popen)
    monkeypatch.setattr(server_mod, "_run_index", lambda *a, **k: None)
    return started


# --- the setting ------------------------------------------------------------

def test_the_interval_is_off_unless_configured(monkeypatch):
    """Off is the default on purpose. A background job that starts reindexing
    an estate because a container came up is not something a deployment should
    discover after the fact."""
    monkeypatch.delenv("ARGUS_INDEX_INTERVAL", raising=False)
    assert server_mod.index_interval() == 0


def test_the_interval_is_read_per_call(monkeypatch):
    """Not at import: this was the exact bug with WAIT_SECONDS in the seed
    script, where a value captured at import hung the test suite for half an
    hour."""
    monkeypatch.setenv("ARGUS_INDEX_INTERVAL", "900")
    assert server_mod.index_interval() == 900
    monkeypatch.setenv("ARGUS_INDEX_INTERVAL", "60")
    assert server_mod.index_interval() == 60


def test_a_nonsense_interval_disables_the_schedule(monkeypatch):
    """Rather than raising inside a daemon thread at startup, where the
    traceback goes to a log nobody is reading and the stack looks healthy."""
    monkeypatch.setenv("ARGUS_INDEX_INTERVAL", "fifteen minutes")
    assert server_mod.index_interval() == 0


# --- does the index need a pass? --------------------------------------------

def test_a_fresh_index_does_not_need_a_pass(tmp_path, monkeypatch):
    monkeypatch.setenv("ARGUS_INDEX_STALE_AFTER", "3600")
    cfg = _cfg(tmp_path, last_run_at=int(time.time()))
    assert server_mod._index_is_current(cfg.index.db_path) is True


def test_a_stale_index_needs_a_pass(tmp_path, monkeypatch):
    monkeypatch.setenv("ARGUS_INDEX_STALE_AFTER", "3600")
    cfg = _cfg(tmp_path, last_run_at=int(time.time()) - 7200)
    assert server_mod._index_is_current(cfg.index.db_path) is False


def test_an_index_with_no_repositories_needs_a_pass(tmp_path, monkeypatch):
    """The fresh-deployment case: an empty named volume. Waiting out a full
    interval here means a first-time user gets "no repositories indexed" on
    the Overview and no reason to expect it to change."""
    monkeypatch.setenv("ARGUS_INDEX_STALE_AFTER", "3600")
    cfg = _cfg(tmp_path, repos=False)
    assert server_mod._index_is_current(cfg.index.db_path) is False


def test_an_unreadable_index_needs_a_pass(tmp_path):
    """A missing database is not "current", and it is also the state the very
    first pass runs from -- `metrics.snapshot` raises, and that must read as
    "go and index", not as an exception in the startup thread."""
    missing = tmp_path / "nothing-here.db"
    with pytest.raises(Exception):
        metrics.snapshot(missing)
    assert server_mod._index_is_current(missing) is False


# --- one writer, ever -------------------------------------------------------

def test_a_second_start_is_refused_while_one_is_running(tmp_path, no_subprocess):
    """The whole reason the schedule is not a second container. Two passes on
    one SQLite index block each other, because `store.connect` sets no
    busy_timeout, and the run that loses reports "database is locked"."""
    assert server_mod._start_index("/etc/argus/config.yaml") is True
    assert server_mod._start_index("/etc/argus/config.yaml") is False
    with server_mod._index_lock:
        assert server_mod._index_job["state"] == "running"
        assert server_mod._index_job["trigger"] == "manual"


def test_a_started_run_records_who_started_it(tmp_path, no_subprocess):
    server_mod._start_index("/etc/argus/config.yaml", trigger="schedule")
    with server_mod._index_lock:
        assert server_mod._index_job["trigger"] == "schedule"


def test_the_manual_route_and_the_schedule_share_the_one_lock(admin_cfg, no_subprocess):
    """The route must go through `_start_index`, not through its own copy of
    the state update. If it did, a scheduled pass and a button press could
    both believe they had claimed the slot."""
    app = create_app(admin_cfg, client=httpx.Client(
        transport=httpx.MockTransport(lambda r: httpx.Response(404))))
    client = TestClient(app.streamable_http_app(), raise_server_exceptions=False)
    headers = {"x-argus-admin-token": "admin-secret"}

    assert client.post("/admin/index", json={"branches": []},
                       headers=headers).status_code == 200
    with server_mod._index_lock:
        assert server_mod._index_job["trigger"] == "manual"
        assert server_mod._index_job["state"] == "running"

    second = client.post("/admin/index", json={"branches": []}, headers=headers)
    assert second.status_code == 409
    assert "already in progress" in second.json()["error"]


# --- the wiring -------------------------------------------------------------

def test_the_scheduler_thread_starts_only_when_configured(tmp_path, monkeypatch):
    """create_app is where the interval is honoured. A setting that is read by
    nothing is indistinguishable from a feature that does not exist."""
    monkeypatch.setenv("ARGUS_CONFIG", "/etc/argus/config.yaml")
    cfg = _cfg(tmp_path)
    spawned: list[str] = []

    class FakeThread:
        def __init__(self, target, args=(), daemon=None, **kw):
            spawned.append(getattr(target, "__name__", str(target)))

        def start(self):
            pass

    monkeypatch.setattr(server_mod.threading, "Thread", FakeThread)
    monkeypatch.setattr(server_mod, "register_tools", lambda *a, **k: None)

    monkeypatch.delenv("ARGUS_INDEX_INTERVAL", raising=False)
    create_app(cfg)
    assert "_scheduler" not in spawned, "the timer started with no interval set"

    spawned.clear()
    monkeypatch.setenv("ARGUS_INDEX_INTERVAL", "900")
    create_app(cfg)
    assert "_scheduler" in spawned, "ARGUS_INDEX_INTERVAL was set and nothing ran"


def test_the_scheduler_exits_immediately_when_disabled(monkeypatch, tmp_path):
    """It is only ever started by create_app when the interval is positive, but
    a guard that sleeps for `interval` seconds after being handed 0 would be a
    thread that never yields. Belt and braces on a daemon."""
    monkeypatch.setenv("ARGUS_INDEX_INTERVAL", "0")
    slept: list[float] = []
    monkeypatch.setattr(server_mod.time, "sleep", lambda s: slept.append(s))
    cfg = _cfg(tmp_path)
    server_mod._scheduler(cfg, "/etc/argus/config.yaml")
    assert slept == [], "a disabled scheduler went to sleep"


# --- what the console is told ------------------------------------------------

def test_the_status_payload_reports_the_cadence(admin_cfg, monkeypatch):
    """The console has no way to know the interval: it is Argus's setting, in
    Argus's container. A settings page reading its own environment would show
    a confident, wrong number."""
    monkeypatch.setenv("ARGUS_INDEX_INTERVAL", "900")
    app = create_app(admin_cfg, client=httpx.Client(
        transport=httpx.MockTransport(lambda r: httpx.Response(404))))
    client = TestClient(app.streamable_http_app(), raise_server_exceptions=False)
    body = client.get("/admin/index/status",
                      headers={"x-argus-admin-token": "admin-secret"}).json()
    assert body["interval"] == 900


def test_the_status_payload_carries_the_same_staleness_the_alert_reads(admin_cfg,
                                                                      monkeypatch):
    """One computation, two consumers. The tile on the Overview and the number
    the ArgusIndexStale rule pages on come from `metrics.snapshot`, so they
    cannot disagree after somebody changes a threshold."""
    monkeypatch.setenv("ARGUS_INDEX_STALE_AFTER", "3600")
    app = create_app(admin_cfg, client=httpx.Client(
        transport=httpx.MockTransport(lambda r: httpx.Response(404))))
    client = TestClient(app.streamable_http_app(), raise_server_exceptions=False)
    body = client.get("/admin/index/status",
                      headers={"x-argus-admin-token": "admin-secret"}).json()

    snap = metrics.snapshot(admin_cfg.index.db_path)
    assert body["index"]["repos"] == len(snap["repos"])
    assert body["index"]["stale"] == snap["stale_repos"]
    assert body["index"]["stale_after"] == snap["stale_after"]
    assert body["index"]["stale_names"] == [f'{r["repo"]}@{r["branch"]}'
                                            for r in snap["repos"] if r["stale"]]
