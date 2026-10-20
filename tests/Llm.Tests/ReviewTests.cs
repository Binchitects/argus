using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Llm.Api.Code;

namespace Llm.Tests;

/// <summary>Code review: a merge request read through Argus as the person, findings on lines checked by a second pass, a summary, a room to discuss it.</summary>
[Collection(nameof(AppCollection))]
public sealed class ReviewTests(AppFixture app)
{
    private async Task<(Guid Id, string Email, TestBrowser Browser)> PersonAsync(TestBrowser admin)
    {
        var user = "rv" + Guid.NewGuid().ToString("N")[..8];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = user, email = $"{user}@example.test", displayName = "Rae" }));
        return (made.GetProperty("id").GetGuid(), $"{user}@example.test", await new TestBrowser(app.Factory).SignedInAsync(user, made.GetProperty("password").GetString()!));
    }

    private void GitLab()
    {
        const string diff = "@@ -1,3 +1,4 @@\\n int count(int *a, int n) {\\n-  for (i = 0; i < n; i++)\\n+  for (i = 0; i <= n; i++) // [bug]\\n+  total += a[i];\\n }\\n";
        const string other = "@@ -10,2 +10,3 @@\\n x = 1;\\n+y = z[k + 1]; // [bug] [harmless]\\n }\\n";
        app.Argus.Answers["mr_get"] = """{"iid": 7, "title": "Count faster", "author": "dev", "source_branch": "fast", "target_branch": "main", "web_url": "https://gitlab.test/g/app/-/merge_requests/7", "description": "Speeds up counting.", "diff_refs": {"base_sha": "aaa", "start_sha": "aaa", "head_sha": "bbb"}}""";
        app.Argus.Answers["mr_diff"] = $$"""{"project": "g/app", "iid": 7, "files": [{"old_path": "count.c", "new_path": "count.c", "new_file": false, "deleted_file": false, "renamed_file": false, "diff": "{{diff}}"}, {"old_path": "z.c", "new_path": "z.c", "new_file": false, "deleted_file": false, "renamed_file": false, "diff": "{{other}}"}]}""";
        app.Argus.Answers["ci_status"] = """{"project": "g/app", "pipeline": {"id": 1, "status": "failed"}, "failed_jobs": [{"id": 9, "name": "test"}]}""";
        app.Argus.Answers["mr_list"] = """{"result": [{"iid": 7, "title": "Count faster", "state": "opened"}]}""";
    }

    private static async Task<JsonElement> DoneAsync(TestBrowser b, Guid id)
    {
        for (var i = 0; i < 200; i++)
        {
            var r = await b.JsonAsync(await b.GetAsync($"/api/code/reviews/{id}"));
            if (r.GetProperty("review").GetProperty("status").GetString() is "done" or "failed")
            {
                return r;
            }
            await Task.Delay(50);
        }
        throw new TimeoutException("the review did not end");
    }

    [Fact]
    public async Task A_merge_request_is_reviewed_as_its_person_with_findings_checked_and_a_room_to_discuss_them()
    {
        GitLab();
        var admin = await new TestBrowser(app.Factory).SignedInAsync("admin", AppFixture.AdminPassword);
        var (_, email, me) = await PersonAsync(admin);
        var (_, _, other) = await PersonAsync(admin);

        var list = await me.JsonAsync(await me.GetAsync("/api/code/mrs?project=g/app"));
        Assert.True(list.ValueKind == JsonValueKind.Array, list.ToString());
        Assert.Equal(7, list[0].GetProperty("iid").GetInt32());

        var res = await me.PostAsync("/api/code/reviews", new { project = "g/app", iid = 7 });
        await StatusAssert.Is(HttpStatusCode.Accepted, res);
        var id = (await me.JsonAsync(res)).GetProperty("id").GetGuid();
        var done = await DoneAsync(me, id);
        Assert.Equal("done", done.GetProperty("review").GetProperty("status").GetString());
        Assert.Equal("!7 Count faster", done.GetProperty("review").GetProperty("title").GetString());
        Assert.Equal("changes", done.GetProperty("review").GetProperty("verdict").GetString());
        Assert.Contains("failed: test", done.GetProperty("ci").GetString(), StringComparison.Ordinal);
        var findings = done.GetProperty("findings").EnumerateArray().ToList();
        Assert.Equal(2, findings.Count);
        // The real one, on its new line; the refuted one kept apart, with why.
        Assert.Equal(("count.c", 2, true), (findings[0].GetProperty("path").GetString(), findings[0].GetProperty("line").GetInt32(), findings[0].GetProperty("verified").GetBoolean()));
        Assert.Equal(("z.c", 11, false), (findings[1].GetProperty("path").GetString(), findings[1].GetProperty("line").GetInt32(), findings[1].GetProperty("verified").GetBoolean()));
        Assert.Equal("The caller checks the bound.", findings[1].GetProperty("verifyNote").GetString());
        // Read as the person, and asked as them, tagged review.
        Assert.All(app.Argus.McpCalls.Where(c => c.Method == "tools/call" && c.Params?.GetProperty("name").GetString() == "mr_diff"), c => Assert.Equal(email, c.Email));
        var asked = app.Model.Requests.Where(r => r.Body["user"]!.GetValue<string>() == email).ToList();
        Assert.Equal(5, asked.Count);
        Assert.All(asked, r => Assert.Equal("review", r.Body["metadata"]!["tags"]![0]!.GetValue<string>()));

        // Someone else does not see it; a room shares it.
        await StatusAssert.Is(HttpStatusCode.NotFound, await other.GetAsync($"/api/code/reviews/{id}"));
        var finding = findings[0].GetProperty("id").GetGuid();
        await StatusAssert.Is(HttpStatusCode.NoContent, await me.Http.PutAsJsonAsync(new Uri($"/api/code/reviews/{id}/findings/{finding}", UriKind.Relative), new { state = "resolved" }));
        var room = (await me.JsonAsync(await me.PostAsync($"/api/code/reviews/{id}/room"))).GetProperty("id").GetGuid();
        var chat = await me.JsonAsync(await me.GetAsync($"/api/chat/conversations/{room}"));
        Assert.Contains("Review of g/app !7 Count faster", chat.GetProperty("messages")[0].GetProperty("content").GetString(), StringComparison.Ordinal);

        await me.PostAsync($"/api/rooms/{room}/members", new { userIds = new[] { (await other.JsonAsync(await other.GetAsync("/api/auth/me"))).GetProperty("id").GetGuid() } });
        Assert.Equal("resolved", (await other.JsonAsync(await other.GetAsync($"/api/code/reviews/{id}"))).GetProperty("findings")[0].GetProperty("state").GetString());

        // Again, with nothing new: nothing to review.
        var again = (await me.JsonAsync(await me.PostAsync($"/api/code/reviews/{id}/again"))).GetProperty("id").GetGuid();
        var second = await DoneAsync(me, again);
        Assert.Equal("Nothing changed since the last review.", second.GetProperty("summary").GetString());
    }

    [Fact]
    public void A_diff_is_numbered_as_the_new_file_and_cut_into_parts_at_its_hunks()
    {
        var numbered = ReviewEngine.Numbered("@@ -4,2 +10,3 @@\n a\n-b\n+c\n+d\n");
        Assert.Equal("@@ -4,2 +10,3 @@\n   10 a\n     -b\n   11+c\n   12+d\n", numbered);
        Assert.Equal([10, 11, 12], ReviewEngine.NumberedLines(numbered).Order());
        var big = string.Concat(Enumerable.Range(0, 400).Select(i => $"@@ -{i * 100},1 +{i * 100},2 @@\n x\n+{new string('y', 100)}\n"));
        var units = ReviewEngine.Units([("big.c", big)]);
        Assert.True(units.Count > 1);
        Assert.All(units, u => Assert.True(u.Text.Length <= ReviewEngine.UnitChars + 200));
    }
}
