using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Argus.CodeHosts;
using Argus.Configuration;
using Argus.Server;
using Argus.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;

namespace Argus.Tests;

/// <summary>
/// Pull requests on GitHub and Gitea, read through the same tools as GitLab's merge requests, with the person's own
/// token (which the app passes): their rights there are all Argus reads, and nothing is written.
/// </summary>
[Collection("process-state")]
public sealed class ForeignHostTests : IAsyncLifetime
{
    const string Chat = "chat-secret";
    const string Alpha = "dev_alpha@argus.test";
    const string GitHubToken = "gh-alpha-read";
    const string GiteaToken = "gt-alpha-read";

    const string GiteaDiff = """
        diff --git a/src/a.c b/src/a.c
        index 1111111..2222222 100644
        --- a/src/a.c
        +++ b/src/a.c
        @@ -1,2 +1,2 @@
         int a;
        -int b;
        +long b;
        diff --git a/docs/new.md b/docs/new.md
        new file mode 100644
        index 0000000..3333333
        --- /dev/null
        +++ b/docs/new.md
        @@ -0,0 +1 @@
        +# New
        diff --git a/old name.txt b/new name.txt
        similarity index 90%
        rename from old name.txt
        rename to new name.txt
        index 4444444..5555555 100644
        --- a/old name.txt
        +++ b/new name.txt
        @@ -1 +1 @@
        -x
        +y
        diff --git a/logo.png b/logo.png
        index 6666666..7777777 100644
        Binary files a/logo.png and b/logo.png differ

        """;

    readonly TestIndex _ix = new();
    FakeGitLabMr _gitlab = null!;
    WebApplication _hosts = null!;
    EnvScope _env = null!;
    WebApplication _app = null!;
    HttpClient _http = null!;
    readonly ConcurrentQueue<(string Method, string Path, string Auth)> _requests = new();

    static IResult Json(string json, int status = 200) => Results.Text(json, "application/json", Encoding.UTF8, status);

    /// <summary>GitHub under /gh, Gitea under /gt: each answers only its own token.</summary>
    WebApplication FakeHosts()
    {
        var b = WebApplication.CreateSlimBuilder();
        b.Logging.ClearProviders();
        b.WebHost.UseUrls("http://127.0.0.1:0");
        var app = b.Build();
        app.Use(async (ctx, next) =>
        {
            var auth = ctx.Request.Headers.Authorization.ToString();
            _requests.Enqueue((ctx.Request.Method, ctx.Request.Path + ctx.Request.QueryString, auth));
            var gh = ctx.Request.Path.StartsWithSegments("/gh");
            if (auth != (gh ? $"Bearer {GitHubToken}" : $"token {GiteaToken}"))
            {
                ctx.Response.StatusCode = 401;
                await ctx.Response.WriteAsJsonAsync(new { message = "Bad credentials" });
                return;
            }
            await next();
        });
        const string pr3 = """{"number": 3, "title": "Count faster", "state": "open", "draft": false, "user": {"login": "dev"}, "body": "Speeds it up.", "html_url": "https://github.test/octo/app/pull/3", "head": {"ref": "fast", "sha": "h3"}, "base": {"ref": "main", "sha": "b3"}, "changed_files": 1, "mergeable": true, "mergeable_state": "clean", "labels": [{"name": "perf"}], "requested_reviewers": [{"login": "rae"}], "assignees": [], "created_at": "2026-10-20T08:00:00Z", "updated_at": "2026-10-21T08:00:00Z"}""";
        const string pr2 = """{"number": 2, "title": "Old", "state": "closed", "draft": false, "user": {"login": "dev"}, "head": {"ref": "old", "sha": "h2"}, "base": {"ref": "main", "sha": "b2"}, "merged_at": "2026-10-01T00:00:00Z", "updated_at": "2026-10-01T00:00:00Z"}""";
        const string files = """[{"filename": "count.c", "status": "modified", "changes": 2, "patch": "@@ -1,2 +1,2 @@\n int n;\n-int i;\n+long i;"}]""";
        app.MapGet("/gh/user", () => Json("""{"login": "alpha"}"""));
        app.MapGet("/gh/repos/octo/app", () => Json("""{"id": 501, "full_name": "octo/app", "default_branch": "main", "clone_url": "https://github.test/octo/app.git"}"""));
        app.MapGet("/gh/repos/octo/app/pulls", (string state) => Json(state == "open" ? $"[{pr3}]" : $"[{pr3}, {pr2}]"));
        app.MapGet("/gh/repos/octo/app/pulls/3", () => Json(pr3));
        app.MapGet("/gh/repos/octo/app/pulls/3/files", () => Json(files));
        app.MapGet("/gh/repos/octo/app/compare/{range}", (string range) =>
            Json("""{"commits": [{"sha": "h3", "commit": {"message": "Count faster\n\nWith a long.", "author": {"name": "Dev", "date": "2026-10-21T08:00:00Z"}}}], "files": """ + files + ", \"range\": \"" + range + "\"}"));
        app.MapGet("/gh/repos/octo/app/actions/runs", (string head_sha) =>
            Json(head_sha == "h3" ? """{"workflow_runs": [{"id": 77, "status": "completed", "conclusion": "failure", "head_sha": "h3", "head_branch": "fast", "html_url": "https://github.test/r/77"}]}""" : """{"workflow_runs": []}"""));
        app.MapGet("/gh/repos/octo/app/actions/runs/77/jobs", () => Json("""{"jobs": [{"id": 9, "name": "test", "workflow_name": "CI", "status": "completed", "conclusion": "failure"}, {"id": 10, "name": "lint", "status": "completed", "conclusion": "success"}]}"""));

        app.MapGet("/gt/api/v1/user", () => Json("""{"login": "alpha"}"""));
        app.MapGet("/gt/api/v1/repos/team/lib", () => Json("""{"id": 61, "full_name": "team/lib", "default_branch": "main", "clone_url": "https://gitea.test/team/lib.git"}"""));
        app.MapGet("/gt/api/v1/repos/team/lib/pulls/4", () => Json("""{"number": 4, "title": "WIP: tidy", "state": "open", "user": {"login": "bo"}, "body": "", "html_url": "https://gitea.test/team/lib/pulls/4", "head": {"ref": "tidy", "sha": "t4"}, "base": {"ref": "main", "sha": "m4"}, "merge_base": "m3", "merged": false, "mergeable": true}"""));
        app.MapGet("/gt/api/v1/repos/team/lib/pulls/4.diff", () => Results.Text(GiteaDiff.Replace("\r\n", "\n"), "text/plain"));
        app.MapGet("/gt/api/v1/repos/team/lib/commits/t4/status", () => Json("""{"state": "failure", "statuses": [{"id": 1, "context": "ci/build", "status": "failure", "description": "2 tests failed", "target_url": "https://ci.test/1"}]}"""));
        app.StartAsync().GetAwaiter().GetResult();
        return app;
    }

    public async Task InitializeAsync()
    {
        _gitlab = ReviewFixture.Build();
        _hosts = FakeHosts();
        var url = _hosts.Urls.First();
        _env = new EnvScope(("ARGUS_KEY_CHECK_URL", null), ("ARGUS_CHAT_CLIENT_TOKEN", Chat), ("ARGUS_APP", "off"), ("ARGUS_ACCESS_NOTICES", "0"),
            ("ARGUS_INDEX_INTERVAL", "0"), ("ARGUS_USERS_FILE", null), (ForeignHosts.GitHubApiEnv, url + "/gh"), (ForeignHosts.GiteaUrlEnv, url + "/gt"));
        var cfg = _ix.Config() with { GitLab = GitLabConfig.Create(_gitlab.Url, FakeGitLabMr.ServiceToken) };
        _app = ArgusServer.Create(cfg, configure: b => b.WebHost.UseTestServer());
        await _app.StartAsync();
        _http = _app.GetTestClient();
        _http.BaseAddress = new Uri("http://localhost:7700");
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        await _hosts.StopAsync();
        await _hosts.DisposeAsync();
        _env.Dispose();
        _ix.Dispose();
        await _gitlab.DisposeAsync();
    }

    async Task<(HttpStatusCode Status, JsonNode Body)> Review(string pathAndQuery, string? github = GitHubToken, string? gitea = GiteaToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, pathAndQuery);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Chat);
        req.Headers.Add(ArgusServer.ChatEmailHeader, Alpha);
        if (github is not null) req.Headers.Add(ForeignHosts.TokenHeader("github"), github);
        if (gitea is not null) req.Headers.Add(ForeignHosts.TokenHeader("gitea"), gitea);
        using var resp = await _http.SendAsync(req);
        return (resp.StatusCode, JsonNode.Parse(await resp.Content.ReadAsStringAsync())!);
    }

    [Fact]
    public async Task A_GitHub_pull_request_is_read_with_the_persons_own_token()
    {
        var (status, list) = await Review("/review/mr_list?project=github:octo/app");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal([3L], list.AsArray().Select(m => m!["iid"]!.GetValue<long>()));
        var merged = (await Review("/review/mr_list?project=github:octo/app&state=merged")).Body.AsArray();
        Assert.Equal([2L], merged.Select(m => m!["iid"]!.GetValue<long>()));

        var mr = (await Review("/review/mr_get?project=github:octo/app&iid=3")).Body;
        Assert.Equal(("github:octo/app", "Count faster", "h3", "fast", "main"),
            (mr["project"]!.GetValue<string>(), mr["title"]!.GetValue<string>(), mr["diff_refs"]!["head_sha"]!.GetValue<string>(), mr["source_branch"]!.GetValue<string>(), mr["target_branch"]!.GetValue<string>()));
        Assert.Equal(["rae"], mr["reviewers"]!.AsArray().Select(r => r!.GetValue<string>()));

        var diff = (await Review("/review/mr_diff?project=github:octo/app&iid=3")).Body;
        var file = diff["files"]![0]!;
        Assert.Equal("count.c", file["new_path"]!.GetValue<string>());
        Assert.StartsWith("@@ -1,2 +1,2 @@", file["diff"]!.GetValue<string>(), StringComparison.Ordinal);
        // A review again reads only what came after: a straight comparison.
        var since = (await Review("/review/mr_diff?project=github:octo/app&iid=3&since_sha=h1")).Body;
        Assert.Equal("h1", since["since_sha"]!.GetValue<string>());
        Assert.Contains(_requests, r => r.Path.StartsWith("/gh/repos/octo/app/compare/h1..h3", StringComparison.Ordinal));

        var ci = (await Review("/review/ci_status?project=github:octo/app&iid=3")).Body;
        Assert.Equal("failed", ci["pipeline"]!["status"]!.GetValue<string>());
        Assert.Equal(["test"], ci["failed_jobs"]!.AsArray().Select(j => j!["name"]!.GetValue<string>()));
        var compared = (await Review("/review/compare_refs?project=github:octo/app&from=main&to=fast")).Body;
        Assert.Equal("h3", compared["commits"]![0]!["sha"]!.GetValue<string>());
        Assert.Contains(_requests, r => r.Path.StartsWith("/gh/repos/octo/app/compare/main...fast", StringComparison.Ordinal));

        // No token, or a repository the token cannot read: refused, and said why.
        var (noToken, why) = await Review("/review/mr_get?project=github:octo/app&iid=3", github: null);
        Assert.Equal(HttpStatusCode.Forbidden, noToken);
        Assert.Contains("Account → Code hosts", why["error"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, (await Review("/review/mr_get?project=github:octo/secret&iid=1")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Review("/review/mr_get?project=github:octo/app&iid=3", github: "wrong")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Review("/review/grep_at?project=github:octo/app&pattern=count")).Status);

        // Who the token is, as the app checks one being linked.
        Assert.Equal("alpha", (await Review("/review/host_user?kind=github")).Body["login"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Forbidden, (await Review("/review/host_user?kind=github", github: "wrong")).Status);
        Assert.All(_requests, r => Assert.Equal("GET", r.Method));
    }

    [Fact]
    public async Task A_Gitea_pull_request_is_read_from_its_diff_with_statuses_as_CI()
    {
        var mr = (await Review("/review/mr_get?project=gitea:team/lib&iid=4")).Body;
        Assert.True(mr["draft"]!.GetValue<bool>());
        Assert.Equal("m3", mr["diff_refs"]!["base_sha"]!.GetValue<string>());

        var files = (await Review("/review/mr_diff?project=gitea:team/lib&iid=4")).Body["files"]!.AsArray();
        Assert.Equal(["src/a.c", "docs/new.md", "new name.txt", "logo.png"], files.Select(f => f!["new_path"]!.GetValue<string>()));
        Assert.True(files[1]!["new_file"]!.GetValue<bool>());
        Assert.Equal(("old name.txt", true), (files[2]!["old_path"]!.GetValue<string>(), files[2]!["renamed_file"]!.GetValue<bool>()));
        Assert.True(files[3]!["binary"]!.GetValue<bool>());
        Assert.Equal(" int a;\n-int b;\n+long b;\n", files[0]!["diff"]!.GetValue<string>()[16..]);

        // Gitea cannot compare two commits: a review again reads the whole diff, and says so.
        var since = (await Review("/review/mr_diff?project=gitea:team/lib&iid=4&since_sha=t3")).Body;
        Assert.Contains("whole diff", since["since_sha_ignored"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, (await Review("/review/compare_refs?project=gitea:team/lib&from=main&to=tidy")).Status);

        var ci = (await Review("/review/ci_status?project=gitea:team/lib&iid=4")).Body;
        Assert.Equal("failed", ci["pipeline"]!["status"]!.GetValue<string>());
        Assert.Equal(["ci/build"], ci["failed_jobs"]!.AsArray().Select(j => j!["name"]!.GetValue<string>()));
        Assert.All(_requests, r => Assert.Equal("GET", r.Method));
    }
}
