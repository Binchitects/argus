using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Llm.Api.Studio;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>
/// Agent Studio's GitLab events (merged, comments, pushes; filtered by branch, label and words, never its own comments)
/// and its acts in GitLab; and its other steps: JSON text, a wait that goes on later, notify, stop, a web address, a data source.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class StudioGitLabTests(AppFixture app)
{
    private WebApplicationFactory<Program> NewApp() =>
        app.Create(app.ConnectionStringFor("studiogl_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(), new Dictionary<string, string?>
        {
            ["Auth:DataKey"] = "a-data-key-for-studio-tests", ["GitLab:Url"] = "https://gitlab.test", ["GitLab:BotToken"] = "bot-token",
            ["Web:AllowedSites"] = "api.shop.test, internal.test",
        });

    private static async Task<JsonElement> EndedAsync(TestBrowser b, WebApplicationFactory<Program> f, Guid run)
    {
        await f.Services.GetRequiredService<StudioRuns>().Finished(run).WaitAsync(TimeSpan.FromSeconds(30));
        return await b.JsonAsync(await b.GetAsync($"/api/studio/runs/{run}"));
    }

    private static List<(string Path, string Status, string Output)> Steps(JsonElement run) =>
        [.. run.GetProperty("steps").EnumerateArray().Select(s => (s.GetProperty("path").GetString()!, s.GetProperty("status").GetString()!, s.GetProperty("output").GetString() ?? ""))];

    private static async Task<JsonElement> HookAsync(WebApplicationFactory<Program> f, Guid workflow, string secret, object e)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/studio/hooks/{workflow}") { Content = new StringContent(JsonSerializer.Serialize(e), Encoding.UTF8, "application/json") };
        request.Headers.Add("X-Gitlab-Token", secret);
        var res = await f.CreateClient().SendAsync(request);
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static object Project => new { id = 7, path_with_namespace = "group/app", web_url = "https://gitlab.test/group/app" };

    private static object Merged(string target, params string[] labels) => new
    {
        object_kind = "merge_request", user = new { name = "Dana" }, project = Project, labels = labels.Select(l => new { title = l }).ToArray(),
        object_attributes = new { iid = 3, action = "merge", title = "Widen DecodeFrame", source_branch = "wide", target_branch = target, url = "https://gitlab.test/group/app/-/merge_requests/3" },
    };

    private static object Comment(string note) => new
    {
        object_kind = "note", user = new { name = "Ben" }, project = Project,
        object_attributes = new { note, noteable_type = "MergeRequest", url = "https://gitlab.test/group/app/-/merge_requests/3#note_1", system = false },
        merge_request = new { iid = 3, title = "Widen DecodeFrame", target_branch = "main", description = "Takes long now." },
    };

    [Fact]
    public async Task GitLab_events_start_runs_only_when_they_match_and_the_run_acts_in_GitLab()
    {
        await using var f = NewApp();
        var ana = await KnowledgeTests.AdminAsync(f);
        var flow = new
        {
            trigger = new { kind = "gitlab", events = new[] { "merge_request_merged", "comment", "push" }, branches = "main, release/*", labels = "", keyword = "@arena" },
            steps = new object[]
            {
                new { id = "said", kind = "text", text = "{{input.gitlab.event}} on {{input.gitlab.branch}}: {{input.gitlab.title}} by {{input.gitlab.author}}" },
                new { id = "merged", kind = "condition", left = "{{input.gitlab.event}}", op = "equals", right = "merge_request_merged", then = new object[]
                {
                    new { id = "label", kind = "gitlab", action = "label", labels = "shipped, {{input.gitlab.branch}}" },
                    new { id = "note", kind = "gitlab", action = "comment", text = "Merged into {{input.gitlab.branch}}." },
                    new { id = "follow", kind = "gitlab", action = "issue", project = "group/app", subject = "Release notes for !{{input.gitlab.iid}}", text = "Write them.", labels = "docs" },
                } },
            },
        };
        var made = await ana.JsonAsync(await ana.PostAsync("/api/studio", new { name = "Ship it", flow }));
        var id = made.GetProperty("workflow").GetProperty("id").GetGuid();
        var secret = made.GetProperty("secret").GetString()!;

        // Merged into main: it runs, labels, comments (signed) and opens an issue.
        var run = await EndedAsync(ana, f, (await HookAsync(f, id, secret, Merged("main"))).GetProperty("run").GetGuid());
        Assert.Equal("done", run.GetProperty("status").GetString());
        var steps = Steps(run);
        Assert.Equal("merge_request_merged on main: Widen DecodeFrame by Dana", steps[0].Output);
        Assert.Equal(["said", "merged", "merged/label", "merged/note", "merged/follow"], steps.Select(s => s.Path));
        Assert.Equal("Opened #5: https://gitlab.test/group/app/-/issues/5", steps[4].Output);
        List<(string Method, string Path, string? Body)> calls;
        lock (app.Mcp.GitLabCalls)
        {
            calls = [.. app.Mcp.GitLabCalls.Select(c => (c.Method, c.PathAndQuery, c.Body))];
        }
        Assert.Contains(calls, c => c is ("PUT", "/api/v4/projects/7/merge_requests/3", _) && c.Body!.Contains("\"add_labels\":\"shipped,main\"", StringComparison.Ordinal));
        var note = Assert.Single(calls, c => c.Method == "POST" && c.Path == "/api/v4/projects/7/merge_requests/3/notes" && c.Body!.Contains("Merged into main.", StringComparison.Ordinal));
        Assert.Contains(Llm.Api.Schedules.Triggers.Signature, note.Body, StringComparison.Ordinal);
        Assert.Contains(calls, c => c is ("POST", "/api/v4/projects/group%2Fapp/issues", _) && c.Body!.Contains("Release notes for !3", StringComparison.Ordinal));

        // What it does not take: another branch, a comment without its words, its own comment, a deleted branch.
        Assert.Equal("ignored", (await HookAsync(f, id, secret, Merged("dev"))).GetProperty("status").GetString());
        Assert.Equal("ignored", (await HookAsync(f, id, secret, Comment("Looks fine to me."))).GetProperty("status").GetString());
        Assert.Equal("ignored", (await HookAsync(f, id, secret, Comment($"@arena said so\n\n---\n*Ship it, {Llm.Api.Schedules.Triggers.Signature} Ana*"))).GetProperty("status").GetString());
        var deleted = new { object_kind = "push", user_name = "Dana", project = Project, @ref = "refs/heads/main", before = "abc", after = new string('0', 40), commits = Array.Empty<object>() };
        Assert.Equal("ignored", (await HookAsync(f, id, secret, deleted)).GetProperty("status").GetString());

        // What it takes: a comment with its words, a push to a release branch.
        var asked = await EndedAsync(ana, f, (await HookAsync(f, id, secret, Comment("@arena what changed?"))).GetProperty("run").GetGuid());
        Assert.Equal("comment on main: Widen DecodeFrame by Ben", Steps(asked)[0].Output);
        var push = new
        {
            object_kind = "push", user_name = "Dana", project = Project, @ref = "refs/heads/release/7", before = "abc1234567", after = "def4567890", total_commits_count = 1,
            commits = new[] { new { id = "def4567890", message = "Fix the decoder\n\nLonger words.", author = new { name = "Dana" } } },
        };
        var pushed = await EndedAsync(ana, f, (await HookAsync(f, id, secret, push)).GetProperty("run").GetGuid());
        Assert.Equal(("done", "push on release/7: Fix the decoder by Dana"), (pushed.GetProperty("status").GetString(), Steps(pushed)[0].Output));
        Assert.Equal(["said", "merged"], Steps(pushed).Select(s => s.Path));

        // The trigger's events are checked.
        var wrong = await ana.PostAsync("/api/studio", new { name = "Bad", flow = new { trigger = new { kind = "gitlab", events = new[] { "everything" } }, steps = new[] { new { id = "a", kind = "text", text = "x" } } } });
        Assert.Contains("merge_request_merged", await wrong.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Steps_read_JSON_wait_and_go_on_notify_stop_call_the_web_and_ask_data()
    {
        await using var f = NewApp();
        var ana = await KnowledgeTests.AdminAsync(f);
        var runs = f.Services.GetRequiredService<StudioRuns>();
        app.Web.Pages["https://api.shop.test/orders"] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"open": 4, "late": ["A1", "B2"]}""", Encoding.UTF8, "application/json") };

        // A data source of files to ask.
        var data = (await ana.JsonAsync(await ana.PostAsync("/api/admin/data-sources", new { name = "Orders", kind = "files", reach = "company" }))).GetProperty("id").GetGuid();
        using (var form = new MultipartFormDataContent())
        {
            form.Add(new ByteArrayContent("Region,Total\nNorth,10\nSouth,32\n"u8.ToArray()) { Headers = { ContentType = new MediaTypeHeaderValue("text/csv") } }, "files", "orders.csv");
            await StatusAssert.Is(HttpStatusCode.OK, await ana.Http.PostAsync(new Uri($"/api/admin/data-sources/{data}/files", UriKind.Relative), form));
        }
        var table = $"{Llm.Api.Insights.FilesData.SchemaOf(data)}.orders";

        var flow = new
        {
            trigger = new { kind = "manual" },
            steps = new object[]
            {
                new { id = "web", kind = "http", method = "GET", url = "https://api.shop.test/orders" },
                new { id = "facts", kind = "text", json = true, text = """{"open": {{web.json.open}}, "first": "{{web.json.late.0}}"}""" },
                new { id = "sum", kind = "data", sourceId = data, sql = $"select sum(total) as \"Total\" from {table}" },
                new { id = "later", kind = "wait", minutes = 30 },
                new { id = "tell", kind = "notify", subject = "Orders", text = "{{facts.json.open}} open, first late {{facts.json.first}}, total {{sum.json.rows.0.Total}}" },
                new { id = "check", kind = "condition", left = "{{facts.json.open}}", op = "greater", right = "3", then = new object[] { new { id = "end", kind = "stop", outcome = "done", text = "Enough said." } } },
                new { id = "never", kind = "text", text = "not reached" },
            },
        };
        var id = (await ana.JsonAsync(await ana.PostAsync("/api/studio", new { name = "Orders check", flow }))).GetProperty("workflow").GetProperty("id").GetGuid();
        var started = (await ana.JsonAsync(await ana.PostAsync($"/api/studio/{id}/run", new { text = "" }))).GetProperty("id").GetGuid();
        var waiting = await EndedAsync(ana, f, started);
        Assert.True(waiting.GetProperty("status").GetString() == "waiting", waiting.ToString());
        Assert.Equal([("web", "done"), ("facts", "done"), ("sum", "done"), ("later", "waiting")], Steps(waiting).Select(s => (s.Path, s.Status)));
        Assert.Contains("| 42 |", Steps(waiting)[2].Output, StringComparison.Ordinal);

        // Not yet due: nothing goes on. Due: the run goes on from there, where it waited.
        Assert.Equal(0, await runs.WaitedAsync(CancellationToken.None));
        await using (var scope = f.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Llm.Core.Data.AppDbContext>();
            await db.WorkflowRunSteps.Where(s => s.RunId == started && s.Kind == "wait").ExecuteUpdateAsync(u => u.SetProperty(s => s.ResumeAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
        }
        var requests = app.Web.Requests.Count(u => u.Host == "api.shop.test");
        Assert.Equal(1, await runs.WaitedAsync(CancellationToken.None));
        Assert.Equal(0, await runs.WaitedAsync(CancellationToken.None));
        var ended = await EndedAsync(ana, f, started);
        Assert.Equal(("done", JsonValueKind.Null), (ended.GetProperty("status").GetString(), ended.GetProperty("error").ValueKind));
        Assert.Equal(["web", "facts", "sum", "later", "tell", "check", "check/end"], Steps(ended).Select(s => s.Path));
        Assert.Equal(requests, app.Web.Requests.Count(u => u.Host == "api.shop.test"));
        var bell = (await ana.JsonAsync(await ana.GetAsync("/api/notifications"))).GetProperty("items").EnumerateArray().First(n => n.GetProperty("title").GetString() == "Orders");
        Assert.Equal("4 open, first late A1, total 42", bell.GetProperty("body").GetString());

        // A web address that is not public is refused; a stop that fails tells why.
        var refused = (await ana.JsonAsync(await ana.PostAsync("/api/studio", new
        {
            name = "Inside", flow = new { trigger = new { kind = "manual" }, steps = new object[] { new { id = "in", kind = "http", method = "GET", url = "https://internal.test/admin" } } },
        }))).GetProperty("workflow").GetProperty("id").GetGuid();
        var inside = await EndedAsync(ana, f, (await ana.JsonAsync(await ana.PostAsync($"/api/studio/{refused}/run", new { text = "" }))).GetProperty("id").GetGuid());
        Assert.Equal("failed", inside.GetProperty("status").GetString());
        Assert.Contains("not on the public internet", inside.GetProperty("error").GetString(), StringComparison.Ordinal);
        var failing = (await ana.JsonAsync(await ana.PostAsync("/api/studio", new
        {
            name = "Fails", flow = new { trigger = new { kind = "manual" }, steps = new object[] { new { id = "no", kind = "stop", outcome = "failed", text = "Nothing to do." } } },
        }))).GetProperty("workflow").GetProperty("id").GetGuid();
        var failed = await EndedAsync(ana, f, (await ana.JsonAsync(await ana.PostAsync($"/api/studio/{failing}/run", new { text = "" }))).GetProperty("id").GetGuid());
        Assert.Equal(("failed", "Nothing to do."), (failed.GetProperty("status").GetString(), failed.GetProperty("error").GetString()));
    }
}
