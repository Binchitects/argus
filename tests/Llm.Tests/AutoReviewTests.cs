using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Llm.Api.Code;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>A project's automatic reviews: merge requests reviewed as GitLab says they open and change, a look at the open ones, a scheduled branch review, and a room told.</summary>
[Collection(nameof(AppCollection))]
public sealed class AutoReviewTests(AppFixture app)
{
    private const string Diff = "@@ -1,3 +1,4 @@\\n int count(int *a, int n) {\\n-  for (i = 0; i < n; i++)\\n+  for (i = 0; i <= n; i++) // [bug]\\n+  total += a[i];\\n }\\n";

    private void GitLab(string head)
    {
        app.Argus.Answers["mr_get"] = $$$"""{"iid": 7, "title": "Count faster", "author": "dev", "source_branch": "fast", "target_branch": "main", "web_url": "https://gitlab.test/g/app/-/merge_requests/7", "description": "Speeds up counting.", "diff_refs": {"base_sha": "aaa", "start_sha": "aaa", "head_sha": "{{{head}}}"}}""";
        app.Argus.Answers["mr_diff"] = $$$"""{"project": "g/app", "iid": 7, "files": [{"old_path": "count.c", "new_path": "count.c", "new_file": false, "deleted_file": false, "renamed_file": false, "diff": "{{{Diff}}}"}]}""";
        app.Argus.Answers["ci_status"] = """{"project": "g/app", "pipeline": {"id": 1, "status": "success"}}""";
        app.Argus.Answers["mr_list"] = $$$"""{"result": [{"iid": 7, "title": "Count faster", "state": "opened", "draft": false, "target_branch": "main", "sha": "{{{head}}}"}, {"iid": 8, "title": "WIP", "state": "opened", "draft": true, "target_branch": "main", "sha": "ddd"}]}""";
        app.Argus.Answers["compare_refs"] = $$$"""{"project": "g/app", "from": "main", "to": "develop", "commits": [{"sha": "c0", "title": "Start"}, {"sha": "c1", "title": "Count faster"}], "files": [{"old_path": "count.c", "new_path": "count.c", "diff": "{{{Diff}}}"}]}""";
    }

    private static object Event(string action, string sha, bool draft = false) => new
    {
        object_kind = "merge_request",
        project = new { id = 3, path_with_namespace = "g/app" },
        object_attributes = new { iid = 7, action, target_branch = "main", draft, oldrev = action == "update" ? "bbb" : null, last_commit = new { id = sha } },
    };

    private static async Task<HttpResponseMessage> HookAsync(TestBrowser b, Guid rule, string secret, object e)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/hooks/reviews/{rule}") { Content = JsonContent.Create(e) };
        request.Headers.Add("X-Gitlab-Token", secret);
        return await b.Http.SendAsync(request);
    }

    /// <summary>The rule's reviews once none of them waits or runs.</summary>
    private static async Task<List<JsonElement>> SettledAsync(TestBrowser b, Guid rule)
    {
        for (var i = 0; i < 300; i++)
        {
            var mine = (await b.JsonAsync(await b.GetAsync("/api/code/reviews"))).EnumerateArray().Select(r => r.GetProperty("review"))
                .Where(r => r.GetProperty("ruleId").ValueKind == JsonValueKind.String && r.GetProperty("ruleId").GetGuid() == rule).ToList();
            if (mine.All(r => r.GetProperty("status").GetString() is "done" or "failed"))
            {
                return mine;
            }
            await Task.Delay(50);
        }
        throw new TimeoutException("the rule's reviews did not end");
    }

    [Fact]
    public async Task Merge_requests_are_reviewed_as_they_open_and_change_and_the_room_is_told()
    {
        GitLab("bbb");
        var admin = await new TestBrowser(app.Factory).SignedInAsync("admin", AppFixture.AdminPassword);
        var user = "ar" + Guid.NewGuid().ToString("N")[..8];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = user, email = $"{user}@example.test", displayName = "Ari" }));
        var person = made.GetProperty("id").GetGuid();
        var me = await new TestBrowser(app.Factory).SignedInAsync(user, made.GetProperty("password").GetString()!);

        // Setting automatic reviews is a feature of its own: denied, refused.
        await admin.PostAsync("/api/admin/access/bulk", new { people = new[] { person }, steps = new[] { new { kind = "feature.deny", feature = "review.policy" } }, dryRun = false });
        await StatusAssert.Is(HttpStatusCode.Forbidden, await me.PostAsync("/api/code/rules", new { project = "g/app" }));
        await admin.PostAsync("/api/admin/access/bulk", new { people = new[] { person }, steps = new[] { new { kind = "feature.default", feature = "review.policy" } }, dryRun = false });

        var room = (await me.JsonAsync(await me.PostAsync("/api/rooms", new { title = "Reviews", userIds = Array.Empty<Guid>() }))).GetProperty("id").GetGuid();
        await StatusAssert.Is(HttpStatusCode.BadRequest, await me.PostAsync("/api/code/rules", new { project = "g/app", checkMinutes = 1 }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await me.PostAsync("/api/code/rules", new { project = "g/app", cron = "0 6 * * *" }));
        var created = await me.JsonAsync(await me.PostAsync("/api/code/rules", new { project = "g/app", checkMinutes = 0, targetBranches = new[] { "main" }, roomId = room }));
        var rule = created.GetProperty("rule").GetProperty("id").GetGuid();
        var secret = created.GetProperty("secret").GetString()!;
        Assert.EndsWith($"/api/hooks/reviews/{rule}", created.GetProperty("rule").GetProperty("hookUrl").GetString(), StringComparison.Ordinal);
        await StatusAssert.Is(HttpStatusCode.Conflict, await me.PostAsync("/api/code/rules", new { project = "g/app" }));

        // GitLab says !7 opened: reviewed as the person, by the rule.
        var anyone = new TestBrowser(app.Factory);
        await StatusAssert.Is(HttpStatusCode.Unauthorized, await HookAsync(anyone, rule, "wrong", Event("open", "bbb")));
        var opened = await HookAsync(anyone, rule, secret, Event("open", "bbb"));
        await StatusAssert.Is(HttpStatusCode.Accepted, opened);
        var first = Assert.Single(await SettledAsync(me, rule));
        Assert.Equal(("done", "webhook", "!7 Count faster"), (first.GetProperty("status").GetString(), first.GetProperty("trigger").GetString(), first.GetProperty("title").GetString()));

        // The same commit again, a draft, and a look at the open ones: nothing new to review.
        Assert.Contains("reviewed already", await (await HookAsync(anyone, rule, secret, Event("open", "bbb"))).Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Contains("not one the rule takes", await (await HookAsync(anyone, rule, secret, Event("reopen", "eee", draft: true))).Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var said = (await me.JsonAsync(await me.PostAsync($"/api/code/rules/{rule}/check"))).GetProperty("said").EnumerateArray().Select(x => x.GetString()).ToList();
        Assert.Equal(["!7: reviewed already", "!8: not one the rule takes"], said);

        // New commits: reviewed again, only what came after, and posted in the room.
        GitLab("ccc");
        await StatusAssert.Is(HttpStatusCode.Accepted, await HookAsync(anyone, rule, secret, Event("update", "ccc")));
        var both = await SettledAsync(me, rule);
        Assert.Equal(2, both.Count);
        Assert.Contains(app.Argus.McpCalls, c => c.Method == "tools/call" && c.Params?.GetProperty("name").GetString() == "mr_diff"
            && c.Params?.GetProperty("arguments").TryGetProperty("since_sha", out var since) == true && since.GetString() == "bbb");
        JsonElement chat = default;
        for (var i = 0; i < 100; i++)
        {
            chat = await me.JsonAsync(await me.GetAsync($"/api/chat/conversations/{room}"));
            if (chat.GetProperty("messages").GetArrayLength() >= 2)
            {
                break;
            }
            await Task.Delay(50);
        }
        var posted = chat.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("content").GetString()!).ToList();
        Assert.Equal(2, posted.Count(m => m.StartsWith("**Automatic review of g/app !7 Count faster**", StringComparison.Ordinal)));

        // A scheduled review of develop over main: run once, and again only when develop moved.
        var scheduled = await me.Http.PutAsJsonAsync(new Uri($"/api/code/rules/{rule}", UriKind.Relative), new { branch = "develop", @base = "main", cron = "0 6 * * *", timeZone = "Europe/Berlin" });
        await StatusAssert.Is(HttpStatusCode.OK, scheduled);
        Assert.Equal(JsonValueKind.String, (await me.JsonAsync(scheduled)).GetProperty("nextRunAt").ValueKind);
        async Task RunDueAsync()
        {
            await using var scope = app.Factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.ReviewRules.Where(r => r.Id == rule).ExecuteUpdateAsync(s => s.SetProperty(r => r.NextRunAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
            await scope.ServiceProvider.GetRequiredService<AutoReviews>().TickAsync(CancellationToken.None);
        }
        await RunDueAsync();
        var withBranch = await SettledAsync(me, rule);
        var branch = Assert.Single(withBranch, r => r.GetProperty("trigger").GetString() == "schedule");
        Assert.Equal(("done", "develop over main"), (branch.GetProperty("status").GetString(), branch.GetProperty("title").GetString()));
        await RunDueAsync();
        Assert.Single(await SettledAsync(me, rule), r => r.GetProperty("trigger").GetString() == "schedule");

        // Turned off, the webhook is acknowledged and dropped; taken away, its reviews stay.
        await me.Http.PutAsJsonAsync(new Uri($"/api/code/rules/{rule}", UriKind.Relative), new { enabled = false });
        Assert.Contains("the rule is off", await (await HookAsync(anyone, rule, secret, Event("update", "fff"))).Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await StatusAssert.Is(HttpStatusCode.NoContent, await me.Http.DeleteAsync(new Uri($"/api/code/rules/{rule}", UriKind.Relative)));
        Assert.Equal(3, (await me.JsonAsync(await me.GetAsync("/api/code/reviews"))).GetArrayLength());
    }
}
