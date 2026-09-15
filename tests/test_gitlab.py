import httpx
import pytest

from argus.config import GitLabConfig
from argus.gitlab import clone_url_for, list_projects, GitLabError

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


def test_raises_on_malformed_json():
    def handler(request):
        return httpx.Response(200, content=b"<html>proxy error</html>")

    with pytest.raises(GitLabError, match="failed to decode JSON"):
        list_projects(CFG, client=_client(handler))


# ---------------------------------------------------------------- clone URL
#
# GitLab publishes `http_url_to_repo` built from its OWN external_url, which is
# routinely not the address the indexer can reach. The failure lands on the
# clone, AFTER enumeration has already reported healthy projects, so it reads
# as a network problem rather than a URL problem: measured against a real
# GitLab CE, Argus enumerated three projects, reported them, and then failed
# every clone with "Failed to connect to localhost port 8929".

def test_clone_url_is_rebased_onto_the_configured_gitlab():
    cfg = GitLabConfig(url="http://host.docker.internal:8929", token="t")
    # ...because inside a container, `localhost` is the container itself.
    assert (clone_url_for(cfg, "http://localhost:8929/root/eal-core.git")
            == "http://host.docker.internal:8929/root/eal-core.git")


def test_clone_url_rebasing_keeps_the_whole_path():
    # The path carries the namespace; dropping or rewriting any of it would
    # clone the wrong project.
    cfg = GitLabConfig(url="https://gitlab.example.com", token="t")
    assert (clone_url_for(cfg, "http://10.0.0.5:8080/group/sub/proj.git")
            == "https://gitlab.example.com/group/sub/proj.git")


def test_clone_url_rebasing_is_a_no_op_when_it_already_matches():
    cfg = GitLabConfig(url="https://gitlab.example.com", token="t")
    advertised = "https://gitlab.example.com/g/p.git"
    assert clone_url_for(cfg, advertised) == advertised


def test_list_projects_rebases_the_clone_url():
    def handler(request):
        return httpx.Response(200, json=[_project(1, "root/eal-core")])

    cfg = GitLabConfig(url="http://host.docker.internal:8929", token="t")
    projects = list_projects(cfg, client=_client(handler))
    assert projects[0].http_url == "http://host.docker.internal:8929/root/eal-core.git"
