"""The GitLab push webhook.

WHY THIS EXISTS

Index freshness was interval-polled. `ARGUS_INDEX_INTERVAL` defaults to 900s,
so a push sat unindexed for up to fifteen minutes and every answer in that
window came from the previous commit while nothing said so. That is the exact
failure this project exists to prevent -- a confident answer from data that is
no longer true -- and a webhook is what removes it.

These pin the four things that make it safe rather than merely present:

  * FAIL CLOSED. No `ARGUS_WEBHOOK_TOKEN`, no route. Unset must mean the
    surface does not exist, not that it exists and accepts anything.

  * CONSTANT-TIME comparison, and a 401 whose body describes nothing about
    the secret. An attacker who learns "the secret is right, the shape is wrong"
    has still not learned the secret.

  * NEVER LOSE A DELIVERY. A push arriving during a pass is the normal case on
    a busy estate. Dropping it would mean the change waits for the next poll --
    precisely the latency the webhook exists to remove -- and nobody could tell
    it had happened.

  * ALWAYS ANSWER 2xx FOR A DELIVERY IT ACCEPTS, including one it does nothing
    with. GitLab treats a non-2xx as a failed delivery, retries with backoff
    and eventually disables the webhook, so answering 400 to a tag push costs
    the operator their webhook over a non-problem.
"""
from __future__ import annotations

import time

import httpx
import pytest
from starlette.testclient import TestClient

import argus.mcpsrv.server as server_mod
from argus.config import Config, GitLabConfig, IndexConfig
from argus.mcpsrv.server import create_app
from argus.store import writes
from argus.store.db import open_db

SECRET = "hook-secret-value"


@pytest.fixture(autouse=True)
def clean_job_state():
    with server_mod._index_lock:
        server_mod._index_job.update(
            state="idle", branches=[], started=None, finished=None,
            returncode=None, tail=[], trigger=None, pending=[],
            pending_full=False)
    yield
    with server_mod._index_lock:
        server_mod._index_job.update(
            state="idle", branches=[], started=None, finished=None,
            returncode=None, tail=[], trigger=None, pending=[],
            pending_full=False)


@pytest.fixture
def cfg(tmp_path):
    db_path = tmp_path / "argus.db"
    conn = open_db(db_path)
    writes.upsert_repo(conn, gitlab_id=101, path_with_namespace="g/a",
                       default_branch="main", http_url="x")
    conn.close()
    return Config(
        gitlab=GitLabConfig(url="https://gl.test", token="service-token"),
        index=IndexConfig(data_dir=tmp_path / "data", db_path=db_path),
    )


@pytest.fixture
def hooks(monkeypatch):
    """Every `argus index` the server would have started, without starting one."""
    started: list[list[str]] = []
    monkeypatch.setattr(server_mod, "_run_index",
                        lambda *a, **k: None)
    real_popen = server_mod.subprocess.Popen

    def fake_popen(argv, **kw):
        started.append(argv)
        raise AssertionError("no child should be started in these tests")
    monkeypatch.setattr(server_mod.subprocess, "Popen", fake_popen)
    return started


@pytest.fixture
def client(cfg, monkeypatch):
    monkeypatch.setenv(server_mod.WEBHOOK_TOKEN_ENV, SECRET)
    app = create_app(cfg, client=httpx.Client(
        transport=httpx.MockTransport(lambda r: httpx.Response(404))))
    return TestClient(app.streamable_http_app(), raise_server_exceptions=False)


def push(repo="g/a", **extra):
    body = {"object_kind": "push",
            "project": {"path_with_namespace": repo},
            "ref": "refs/heads/main",
            "after": "abc123"}
    body.update(extra)
    return body


def post(client, body=None, token=SECRET):
    headers = {} if token is None else {server_mod.WEBHOOK_HEADER: token}
    return client.post(server_mod.WEBHOOK_PATH,
                       json=push() if body is None else body, headers=headers)


# --- fail closed ---------------------------------------------------------------

def test_the_route_does_not_exist_without_a_secret(cfg, monkeypatch):
    """Unset must mean the surface is absent, not that it is open. A webhook
    that anyone who can reach the port may fire is a way to make the indexer
    run continuously, which on a shared machine is a denial of service."""
    monkeypatch.delenv(server_mod.WEBHOOK_TOKEN_ENV, raising=False)
    app = create_app(cfg, client=httpx.Client(
        transport=httpx.MockTransport(lambda r: httpx.Response(404))))
    client = TestClient(app.streamable_http_app(), raise_server_exceptions=False)
    # Not 202: with no token configured the middleware no longer exempts the
    # path, so it is gated like every other route and the handler is absent.
    assert post(client).status_code != 202


def test_a_wrong_secret_is_refused(client, hooks):
    assert post(client, token="not-the-secret").status_code == 401
    assert post(client, token=None).status_code == 401
    assert post(client, token="").status_code == 401
    assert not hooks


def test_a_refusal_says_nothing_about_the_secret(client, hooks):
    """The 401 body is one generic word, and that is the whole point.

    An earlier version of this test asserted that a bad secret and a malformed
    delivery were indistinguishable. They are not, and should not be: a
    delivery that carries the right secret but no project is a CONFIGURATION
    mistake by the operator who set the webhook up, and answering it with a
    useless "forbidden" would send them looking in the wrong place. The
    distinction only helps an attacker who can already guess a 32-byte random
    token, and if they can do that the endpoint's error messages are not the
    problem.

    What must hold is that nothing in the refusal describes the secret -- no
    length, no prefix, no "close but not quite" -- so the assertion is about
    the body being a single generic key, not about it matching something else.
    """
    wrong = post(client, token="nope")
    assert wrong.status_code == 401
    assert wrong.json() == {"error": "forbidden"}

    # ...and the operator's own mistake IS diagnosable, with the right secret.
    malformed = post(client, body={"object_kind": "push"}, token=SECRET)
    assert malformed.status_code == 400
    assert "path_with_namespace" in malformed.json()["error"]


# --- what it accepts -----------------------------------------------------------

def test_a_push_starts_a_pass_for_that_repository(client, hooks):
    r = post(client)
    assert r.status_code == 202
    assert r.json()["status"] == "started"
    assert r.json()["repo"] == "g/a"
    with server_mod._index_lock:
        assert server_mod._index_job["trigger"] == "webhook"


def test_a_tag_push_is_ignored_but_acknowledged(client, hooks):
    """Answering anything but 2xx makes GitLab retry and eventually disable the
    webhook, so an event this endpoint has no use for must still be a success
    from GitLab's point of view."""
    r = post(client, body=push(object_kind="tag_push"))
    assert r.status_code == 200
    assert r.json()["status"] == "ignored"
    with server_mod._index_lock:
        assert server_mod._index_job["state"] == "idle"


def test_an_issue_event_is_ignored(client, hooks):
    r = post(client, body=push(object_kind="issue"))
    assert r.status_code == 200
    assert r.json()["status"] == "ignored"


def test_a_branch_deletion_is_ignored(client, hooks):
    """A deletion is a push event with an all-zero `after`. There is nothing to
    index for a ref that no longer exists, and running a pass to discover that
    is a pass spent on nothing."""
    r = post(client, body=push(after="0" * 40))
    assert r.status_code == 200
    assert r.json()["reason"] == "ref deleted"
    with server_mod._index_lock:
        assert server_mod._index_job["state"] == "idle"


def test_a_delivery_with_no_project_is_a_bad_request(client, hooks):
    """Malformed, not "a repository we could not find" -- guessing here would
    mean indexing the whole estate because GitLab sent something unexpected."""
    r = post(client, body={"object_kind": "push"})
    assert r.status_code == 400
    assert not hooks


def test_a_body_that_is_not_json_is_a_bad_request(client, hooks):
    r = client.post(server_mod.WEBHOOK_PATH,
                    content=b"not json at all",
                    headers={server_mod.WEBHOOK_HEADER: SECRET})
    assert r.status_code == 400
    assert not hooks


# --- never lose a delivery -----------------------------------------------------

def test_a_push_during_a_pass_is_queued_not_dropped(client, hooks):
    """The normal case on a busy estate. Dropping it means the change waits for
    the next poll -- exactly the latency this endpoint exists to remove."""
    post(client)                                       # claims the slot
    r = post(client, body=push(repo="g/b"))
    assert r.status_code == 202
    assert r.json()["status"] == "queued"
    assert r.json()["queued"] == 1
    with server_mod._index_lock:
        assert server_mod._index_job["pending"] == ["g/b"]


def test_the_same_repository_twice_is_queued_once(client, hooks):
    """A branch push and a tag push to the same project arrive as two events,
    and the queue drains one pass at a time -- so a duplicate would cost a
    whole extra pass."""
    post(client)
    assert post(client, body=push(repo="g/b")).json()["status"] == "queued"
    assert post(client, body=push(repo="g/b")).json()["status"] == "already_queued"
    with server_mod._index_lock:
        assert server_mod._index_job["pending"] == ["g/b"]


def test_the_queue_drains_one_at_a_time(client, hooks):
    post(client)
    for repo in ("g/b", "g/c"):
        post(client, body=push(repo=repo))

    # Simulate the running pass finishing.
    with server_mod._index_lock:
        server_mod._index_job["state"] = "idle"
    server_mod._drain_pending("/etc/argus/config.yaml")

    with server_mod._index_lock:
        assert server_mod._index_job["state"] == "running"
        assert server_mod._index_job["pending"] == ["g/c"], \
            "the second queued repository was lost or started at the same time"


def test_an_overfull_queue_collapses_into_one_full_pass(client, hooks):
    """A force-push across an estate, or a webhook configured for every branch,
    enqueues faster than a pass drains. Past the ceiling, indexing everything
    once is cheaper and more correct than working through the list."""
    post(client)
    for i in range(server_mod.WEBHOOK_QUEUE_LIMIT):
        post(client, body=push(repo=f"g/r{i}"))
    r = post(client, body=push(repo="g/one-too-many"))
    assert r.json()["status"] == "collapsed_to_full_pass"
    with server_mod._index_lock:
        assert server_mod._index_job["pending"] == []
        assert server_mod._index_job["pending_full"] is True

    with server_mod._index_lock:
        server_mod._index_job["state"] = "idle"
    server_mod._drain_pending("/etc/argus/config.yaml")
    with server_mod._index_lock:
        assert server_mod._index_job["pending_full"] is False


def test_nothing_happens_when_the_queue_is_empty(client, hooks):
    with server_mod._index_lock:
        server_mod._index_job["state"] = "idle"
    server_mod._drain_pending("/etc/argus/config.yaml")
    with server_mod._index_lock:
        assert server_mod._index_job["state"] == "idle"


def test_a_queued_pass_still_refuses_to_start_over_a_running_one(client, hooks):
    """`_drain_pending` is called after every pass, including one that another
    drain has already followed. It must not start a second child on top."""
    with server_mod._index_lock:
        server_mod._index_job["state"] = "running"
        server_mod._index_job["pending"] = ["g/b"]
    server_mod._drain_pending("/etc/argus/config.yaml")
    with server_mod._index_lock:
        assert server_mod._index_job["pending"] == ["g/b"], \
            "a pass was started while one was already running"
