import httpx
import pytest

from codeindex.config import GitLabConfig
from codeindex.gitlab import list_projects, GitLabError

CFG = GitLabConfig(url="https://gl.test", token="tok")


def _project(pid, ns, branch="main"):
    return {
        "id": pid, "path_with_namespace": ns, "default_branch": branch,
        "http_url_to_repo": f"https://gl.test/{ns}.git",
    }


def _client(handler):
    return httpx.Client(transport=httpx.MockTransport(handler))


def test_paginates_until_empty_page():
    pages = {
        "1": [_project(1, "g/a"), _project(2, "g/b")],
        "2": [_project(3, "g/c")],
        "3": [],
    }
    seen = []

    def handler(request):
        page = dict(request.url.params).get("page", "1")
        seen.append(page)
        return httpx.Response(200, json=pages[page])

    projects = list_projects(CFG, client=_client(handler))
    assert [p.gitlab_id for p in projects] == [1, 2, 3]
    assert seen == ["1", "2", "3"]


def test_sends_private_token_header():
    captured = {}

    def handler(request):
        captured.update(request.headers)
        return httpx.Response(200, json=[])

    list_projects(CFG, client=_client(handler))
    assert captured["private-token"] == "tok"


def test_skips_projects_without_default_branch():
    def handler(request):
        if dict(request.url.params).get("page", "1") == "1":
            return httpx.Response(200, json=[_project(1, "g/a", None), _project(2, "g/b")])
        return httpx.Response(200, json=[])

    projects = list_projects(CFG, client=_client(handler))
    assert [p.gitlab_id for p in projects] == [2]


def test_raises_on_auth_failure():
    def handler(request):
        return httpx.Response(401, json={"message": "401 Unauthorized"})

    with pytest.raises(GitLabError, match="401"):
        list_projects(CFG, client=_client(handler))
