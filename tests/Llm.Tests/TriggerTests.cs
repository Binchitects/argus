using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Llm.Tests;

/// <summary>Tasks that events run: GitLab's (a merge request, a failed pipeline), with the answer commented back; and any system's JSON.</summary>
[Collection(nameof(AppCollection))]
public sealed class TriggerTests(AppFixture app)
{
    private WebApplicationFactory<Program> NewApp() =>
        app.Create(app.ConnectionStringFor("triggers_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(), new Dictionary<string, string?>
        {
            ["Auth:DataKey"] = "a-data-key-for-trigger-tests", ["GitLab:Url"] = "https://gitlab.test", ["GitLab:BotToken"] = "bot-token",
        });

    private static async Task<TestBrowser> PersonAsync(WebApplicationFactory<Program> f)
    {
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "g" + Guid.NewGuid().ToString("N")[..10];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        return await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!);
    }

    private static Task<HttpResponseMessage> PostEvent(WebApplicationFactory<Program> f, string url, string? secret, object body, string header = "X-Gitlab-Token")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(url).PathAndQuery) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
        if (secret is not null)
        {
            request.Headers.Add(header, secret);
        }
        return f.CreateClient().SendAsync(request);
    }

    private static async Task<JsonElement> RunAsync(TestBrowser b, string id)
    {
        for (var i = 0; i < 100; i++)
        {
            var runs = (await b.JsonAsync(await b.GetAsync($"/api/tasks/{id}/runs"))).EnumerateArray().ToList();
            if (runs.Count > 0 && runs[0].GetProperty("status").GetString() != "running")
            {
                return runs[0];
            }
            await Task.Delay(100);
        }
        throw new TimeoutException("the run never ended");
    }

    private static object MergeRequest(string action) => new
    {
        object_kind = "merge_request", user = new { name = "Dana" }, project = new { id = 7, path_with_namespace = "group/app" },
        object_attributes = new { iid = 3, action, title = "Widen DecodeFrame", source_branch = "wide", target_branch = "main", url = "https://gitlab.test/group/app/-/merge_requests/3", description = "Takes long now." },
    };

    [Fact]
    public async Task A_merge_request_runs_a_review_with_its_changes_and_the_answer_is_commented_back()
    {
        await using var f = NewApp();
        var b = await PersonAsync(f);
        var made = await b.JsonAsync(await b.PostAsync("/api/tasks", new
        {
            name = "Review", prompt = "Review this merge request.", trigger = "gitlab", events = new[] { "merge_request" }, replyInGitLab = true, useArgus = false, tools = Array.Empty<string>(),
        }));
        var id = made.GetProperty("id").GetString()!;
        var url = made.GetProperty("hookUrl").GetString()!;
        var secret = made.GetProperty("hookToken").GetString()!;
        Assert.Equal($"https://llm.test/api/hooks/{id}", url);
        Assert.Empty(made.GetProperty("nextRuns").EnumerateArray());
        // The secret is shown once.
        var listed = (await b.JsonAsync(await b.GetAsync("/api/tasks"))).GetProperty("tasks").EnumerateArray().Single();
        Assert.Equal(JsonValueKind.Null, listed.GetProperty("hookToken").ValueKind);
        Assert.True((await b.JsonAsync(await b.GetAsync("/api/tasks"))).GetProperty("gitlabBot").GetBoolean());

        Assert.Equal(HttpStatusCode.Unauthorized, (await PostEvent(f, url, "wrong", MergeRequest("open"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostEvent(f, url, null, MergeRequest("open"))).StatusCode);
        var closed = await PostEvent(f, url, secret, MergeRequest("close"));
        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);
        Assert.Contains("ignored", await closed.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.Accepted, (await PostEvent(f, url, secret, MergeRequest("open"))).StatusCode);
        var run = await RunAsync(b, id);
        Assert.Equal("done", run.GetProperty("status").GetString());
        Assert.Equal("commented in GitLab", run.GetProperty("delivery").GetString());
        // The question: the task's, then the event with the changes the bot read.
        var question = app.Model.Requests.Select(r => r.Body["messages"]!.AsArray().Last()!["content"]!.ToString())
            .Last(c => c.StartsWith("Review this merge request.", StringComparison.Ordinal));
        Assert.Contains("merge request !3 was opened by Dana in group/app", question, StringComparison.Ordinal);
        Assert.Contains("+int DecodeFrame(long x);", question, StringComparison.Ordinal);
        var comment = app.Mcp.GitLabCalls.Last(c => c.Method == "POST" && c.PathAndQuery == "/api/v4/projects/7/merge_requests/3/notes");
        Assert.Contains("Answer to: Review this merge", comment.Body, StringComparison.Ordinal);
        Assert.Contains("by Argus Arena", comment.Body, StringComparison.Ordinal);

        // A new secret: the old one stops working.
        var rotated = (await b.JsonAsync(await b.PostAsync($"/api/tasks/{id}/secret", new { }))).GetProperty("hookToken").GetString();
        Assert.NotEqual(secret, rotated);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostEvent(f, url, secret, MergeRequest("open"))).StatusCode);
    }

    [Fact]
    public async Task A_failed_pipeline_brings_its_jobs_log_and_any_system_can_post_json()
    {
        await using var f = NewApp();
        var b = await PersonAsync(f);
        var pipeline = await b.JsonAsync(await b.PostAsync("/api/tasks", new { name = "Explain", prompt = "Explain why it failed.", trigger = "gitlab", events = new[] { "pipeline_failed" } }));
        var ok = await PostEvent(f, pipeline.GetProperty("hookUrl").GetString()!, pipeline.GetProperty("hookToken").GetString(), new
        {
            object_kind = "pipeline", project = new { id = 7, path_with_namespace = "group/app" },
            object_attributes = new { id = 12, status = "success", @ref = "main", sha = "abc123def" },
        });
        Assert.Contains("ignored", await ok.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Accepted, (await PostEvent(f, pipeline.GetProperty("hookUrl").GetString()!, pipeline.GetProperty("hookToken").GetString(), new
        {
            object_kind = "pipeline", project = new { id = 7, path_with_namespace = "group/app" },
            object_attributes = new { id = 12, status = "failed", @ref = "main", sha = "abc123def" },
            builds = new[] { new { id = 41, name = "build", stage = "build", status = "failed" } },
        })).StatusCode);
        Assert.Equal("done", (await RunAsync(b, pipeline.GetProperty("id").GetString()!)).GetProperty("status").GetString());
        var asked = app.Model.Requests.Select(r => r.Body["messages"]!.AsArray().Last()!["content"]!.ToString()).Last(c => c.StartsWith("Explain why", StringComparison.Ordinal));
        Assert.Contains("pipeline #12 failed on main", asked, StringComparison.Ordinal);
        Assert.Contains("undefined reference to DecodeFrame", asked, StringComparison.Ordinal);

        var hook = await b.JsonAsync(await b.PostAsync("/api/tasks", new { name = "Alert", prompt = "Say what this alert means.", trigger = "webhook" }));
        Assert.Equal(HttpStatusCode.Accepted, (await PostEvent(f, hook.GetProperty("hookUrl").GetString()!, hook.GetProperty("hookToken").GetString(), new { alert = "DiskFull", host = "db1" }, "X-Hook-Secret")).StatusCode);
        Assert.Equal("done", (await RunAsync(b, hook.GetProperty("id").GetString()!)).GetProperty("status").GetString());
        Assert.Contains(app.Model.Requests, r => r.Body["messages"]!.AsArray().Last()!["content"]!.ToString().Contains("\"alert\":\"DiskFull\"", StringComparison.Ordinal));

        // A schedule still needs its cron; events need choosing.
        await StatusAssert.Is(HttpStatusCode.BadRequest, await b.PostAsync("/api/tasks", new { name = "X", prompt = "Y", trigger = "gitlab", events = Array.Empty<string>() }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await b.PostAsync("/api/tasks", new { name = "X", prompt = "Y", trigger = "sometimes" }));
    }
}
