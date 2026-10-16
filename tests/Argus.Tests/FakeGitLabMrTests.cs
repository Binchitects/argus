using System.Text.Json.Nodes;

namespace Argus.Tests;

/// <summary>The fake GitLab the review tests read: its answers agree with its own git repositories, as GitLab's do.</summary>
public sealed class FakeGitLabMrTests : IAsyncLifetime
{
    FakeGitLabMr _gitlab = null!;
    readonly HttpClient _http = new();

    public Task InitializeAsync()
    {
        _gitlab = ReviewFixture.Build();
        _http.DefaultRequestHeaders.Add("PRIVATE-TOKEN", FakeGitLabMr.ServiceToken);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gitlab.DisposeAsync();
    }

    async Task<JsonNode> Get(string path) => JsonNode.Parse(await _http.GetStringAsync(_gitlab.Url + "/api/v4" + path))!;

    [Fact]
    public async Task A_merge_request_has_a_ref_in_the_bare_repository_and_one_version_per_push()
    {
        var eal = _gitlab[ReviewFixture.EalCore];
        var mr = await Get("/projects/11/merge_requests/1");
        var head = mr["diff_refs"]!["head_sha"]!.GetValue<string>();
        Assert.Equal(eal.Sha("refs/merge-requests/1/head"), head);
        Assert.Equal(eal.Sha("refs/heads/frame-sum"), head);
        Assert.Equal(eal.Sha("refs/heads/main"), mr["diff_refs"]!["start_sha"]!.GetValue<string>());

        var versions = (await Get("/projects/11/merge_requests/1/versions")).AsArray();
        Assert.Equal(2, versions.Count);
        Assert.Equal(head, versions[0]!["head_commit_sha"]!.GetValue<string>());
        // The first version, alone: only the decoder changed then.
        var first = await Get($"/projects/11/merge_requests/1/versions/{versions[1]!["id"]}");
        Assert.Equal(["src/decoder.c"], first["diffs"]!.AsArray().Select(d => d!["new_path"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Diffs_commits_and_compare_are_gits_own_answers()
    {
        var diffs = (await Get("/projects/11/merge_requests/1/diffs?per_page=100")).AsArray();
        Assert.Equal(["config/upload.env", "src/decoder.c"], diffs.Select(d => d!["new_path"]!.GetValue<string>()).Order());
        var upload = diffs.Single(d => d!["new_path"]!.GetValue<string>() == "config/upload.env")!;
        Assert.True(upload["new_file"]!.GetValue<bool>());
        Assert.StartsWith("@@ -0,0 +1,3 @@", upload["diff"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("+ARCHIVE_TOKEN=" + ReviewFixture.FakeSecret, upload["diff"]!.GetValue<string>(), StringComparison.Ordinal);

        var commits = (await Get("/projects/11/merge_requests/1/commits")).AsArray();
        Assert.Equal(["Upload settings", "Add SumFrame"], commits.Select(c => c!["title"]!.GetValue<string>()));

        var compared = await Get("/projects/11/repository/compare?from=main&to=frame-sum");
        Assert.Equal(2, compared["commits"]!.AsArray().Count);
        Assert.Equal(2, compared["diffs"]!.AsArray().Count);

        // Paged as GitLab pages.
        Assert.Single((await Get("/projects/11/merge_requests/1/diffs?per_page=1&page=2")).AsArray());
    }

    [Fact]
    public async Task Pipelines_jobs_logs_and_the_credential_check()
    {
        var pipelines = (await Get("/projects/11/merge_requests/1/pipelines")).AsArray();
        var pipeline = Assert.Single(pipelines)!;
        Assert.Equal("failed", pipeline["status"]!.GetValue<string>());
        var jobs = (await Get($"/projects/11/pipelines/{pipeline["id"]}/jobs")).AsArray();
        var test = jobs.Single(j => j!["name"]!.GetValue<string>() == "test")!;
        var log = await _http.GetStringAsync($"{_gitlab.Url}/api/v4/projects/11/jobs/{test["id"]}/trace");
        Assert.Contains(ReviewFixture.FakeSecret, log, StringComparison.Ordinal);

        using var anonymous = new HttpClient();
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await anonymous.GetAsync(_gitlab.Url + "/api/v4/projects/11")).StatusCode);
        Assert.Contains(_gitlab.Requests, r => r.Token is null);
        Assert.All(_gitlab.Requests, r => Assert.Equal("GET", r.Method));
    }
}
