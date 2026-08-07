"""The guard against a silently partial index.

`list_projects` uses `membership=false`, which for a NON-ADMIN token returns
only public projects. Indexing then succeeds, reports zero errors, and covers
a fraction of the estate -- with every later answer confidently incomplete and
nothing downstream able to tell.
"""

from __future__ import annotations

import httpx
import pytest

from argus.config import GitLabConfig
from argus.gitlab import EnumerationHealth, GitLabError, enumeration_health

CFG = GitLabConfig(url="https://gl.test", token="t")


def _client(user: dict, by_membership: dict[str, list]) -> httpx.Client:
    def handler(request: httpx.Request) -> httpx.Response:
        if request.url.path.endswith("/user"):
            return httpx.Response(200, json=user)
        membership = request.url.params.get("membership")
        page = int(request.url.params.get("page", 1))
        rows = by_membership.get(membership, [])
        return httpx.Response(200, json=rows if page == 1 else [])
    return httpx.Client(transport=httpx.MockTransport(handler))


def test_an_admin_token_is_healthy_however_the_counts_land():
    health = enumeration_health(CFG, client=_client(
        {"is_admin": True}, {"false": [{"id": 1}], "true": [{"id": 1}, {"id": 2}]}))
    assert health.ok
    assert health.problem is None


def test_a_non_admin_missing_repositories_is_reported_with_the_numbers():
    """The real failure: membership=false saw 1 project, but the token is a
    member of 3. Two repositories would be silently absent."""
    health = enumeration_health(CFG, client=_client(
        {"is_admin": False},
        {"false": [{"id": 1}], "true": [{"id": 1}, {"id": 2}, {"id": 3}]}))
    assert not health.ok
    problem = health.problem
    assert "1 project" in problem and "3" in problem
    assert "At least 2" in problem
    assert "admin" in problem


def test_a_non_admin_that_sees_everything_it_is_a_member_of_is_healthy():
    """A non-admin token is not automatically broken -- only one that provably
    cannot reach repositories it belongs to."""
    health = enumeration_health(CFG, client=_client(
        {"is_admin": False}, {"false": [{"id": 1}, {"id": 2}], "true": [{"id": 1}]}))
    assert health.ok


def test_an_error_response_raises_rather_than_reporting_health():
    def handler(request):
        return httpx.Response(500, text="boom")
    with pytest.raises(GitLabError, match="500"):
        enumeration_health(CFG, client=httpx.Client(
            transport=httpx.MockTransport(handler)))


def test_a_malformed_user_body_raises():
    def handler(request):
        if request.url.path.endswith("/user"):
            return httpx.Response(200, text="<html>")
        return httpx.Response(200, json=[])
    with pytest.raises(GitLabError, match="JSON"):
        enumeration_health(CFG, client=httpx.Client(
            transport=httpx.MockTransport(handler)))


def test_health_is_a_pure_value_with_no_hidden_state():
    h = EnumerationHealth(is_admin=False, visible_count=2, member_count=5)
    assert not h.ok
    assert "At least 3" in h.problem
