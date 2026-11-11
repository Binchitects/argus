using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Llm.Api.Code;

namespace Llm.Tests;

/// <summary>Code review: a merge request read through code-intel as the person, findings on lines checked by a second pass, a summary, a room to discuss it.</summary>
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
        app.CodeIntel.Answers["mr_get"] = """{"iid": 7, "title": "Count faster", "author": "dev", "source_branch": "fast", "target_branch": "main", "web_url": "https://gitlab.test/g/app/-/merge_requests/7", "description": "Speeds up counting.", "diff_refs": {"base_sha": "aaa", "start_sha": "aaa", "head_sha": "bbb"}}""";
        app.CodeIntel.Answers["mr_diff"] = $$"""{"project": "g/app", "iid": 7, "files": [{"old_path": "count.c", "new_path": "count.c", "new_file": false, "deleted_file": false, "renamed_file": false, "diff": "{{diff}}"}, {"old_path": "z.c", "new_path": "z.c", "new_file": false, "deleted_file": false, "renamed_file": false, "diff": "{{other}}"}]}""";
        app.CodeIntel.Answers["ci_status"] = """{"project": "g/app", "pipeline": {"id": 1, "status": "failed"}, "failed_jobs": [{"id": 9, "name": "test"}]}""";
        app.CodeIntel.Answers["mr_list"] = """{"result": [{"iid": 7, "title": "Count faster", "state": "opened"}]}""";
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
        Assert.All(app.CodeIntel.McpCalls.Where(c => c.Method == "tools/call" && c.Params?.GetProperty("name").GetString() == "mr_diff"), c => Assert.Equal(email, c.Email));
        var asked = app.Model.Requests.Where(r => r.Body["user"]?.GetValue<string>() == email).ToList();
        Assert.Equal(5, asked.Count);
        Assert.Equal(Enumerable.Repeat<string?>("review", 5), await app.TagsAsync(email));

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

    private void Repository()
    {
        // Its product's code: two C files (the larger one read first), a test and a vendored file left out, a README read for what it is.
        app.CodeIntel.Handlers["files_at"] = args => JsonSerializer.Serialize(new
        {
            project = args.GetProperty("project").GetString(), @ref = args.TryGetProperty("ref", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : "main", sha = "c0de", total = 5, bytes = 9000,
            languages = new { c = new { files = 3, bytes = 8000 } },
            files = new object[]
            {
                new { path = "README.md", size = 300, language = "markdown" },
                new { path = "src/count.c", size = 4000, language = "c" },
                new { path = "src/util.c", size = 2500, language = "c" },
                new { path = "tests/count_test.c", size = 1500, language = "c" },
                new { path = "vendor/zlib.c", size = 90000, language = "c" },
            },
            more = 0,
        });
        app.CodeIntel.Handlers["file_at"] = args => args.GetProperty("path").GetString() switch
        {
            "README.md" => """{"start_line": 1, "end_line": 1, "total_lines": 1, "content": "A counter library.\n"}""",
            "src/count.c" => """{"start_line": 1, "end_line": 3, "total_lines": 3, "content": "int count(int *a, int n) {\n  for (i = 0; i <= n; i++) total += a[i]; // [bug]\n}\n"}""",
            _ => """{"start_line": 1, "end_line": 1, "total_lines": 1, "content": "int ok(void) { return 1; }\n"}""",
        };
        app.CodeIntel.Handlers["dependency_path"] = args => JsonSerializer.Serialize(new
        {
            from = args.GetProperty("from").GetString(), to = args.GetProperty("to").GetString(), direction = $"{args.GetProperty("from").GetString()} depends on {args.GetProperty("to").GetString()}",
            paths = new[] { new { length = 1, confidence = 0.95, steps = new[] { new { from = args.GetProperty("from").GetString(), to = args.GetProperty("to").GetString(), confidence = 0.95, tier = "strong", by = new[] { "package:nuget Lib" } } } } },
        });
    }

    [Fact]
    public async Task A_whole_repository_is_reviewed_at_a_branch_from_its_products_code_with_a_summary_of_its_health()
    {
        Repository();
        var admin = await new TestBrowser(app.Factory).SignedInAsync("admin", AppFixture.AdminPassword);
        var (_, email, me) = await PersonAsync(admin);
        await StatusAssert.Is(HttpStatusCode.BadRequest, await me.PostAsync("/api/code/reviews", new { project = "g/lib", scope = "everything" }));
        var res = await me.PostAsync("/api/code/reviews", new { project = "g/lib", scope = "repository", @ref = "develop" });
        await StatusAssert.Is(HttpStatusCode.Accepted, res);
        var done = await DoneAsync(me, (await me.JsonAsync(res)).GetProperty("id").GetGuid());
        var review = done.GetProperty("review");
        Assert.True(review.GetProperty("status").GetString() == "done", review.GetProperty("error").ToString());
        Assert.Equal(("repository", "g/lib @ develop", 2, "changes"), (review.GetProperty("scope").GetString(), review.GetProperty("title").GetString(), review.GetProperty("files").GetInt32(),
            review.GetProperty("verdict").GetString()));
        Assert.StartsWith("A counter library in C.", done.GetProperty("summary").GetString(), StringComparison.Ordinal);
        var finding = Assert.Single(done.GetProperty("findings").EnumerateArray());
        Assert.Equal(("src/count.c", 2), (finding.GetProperty("path").GetString(), finding.GetProperty("line").GetInt32()));
        // Read as the person at the branch: the test and the vendored file never.
        var read = app.CodeIntel.McpCalls.Where(c => c.Email == email && c.Method == "tools/call" && c.Params?.GetProperty("name").GetString() == "file_at")
            .Select(c => c.Params!.Value.GetProperty("arguments").GetProperty("path").GetString()).ToList();
        Assert.Equal(["README.md", "src/count.c", "src/util.c"], read);
        Assert.All(app.CodeIntel.McpCalls.Where(c => c.Email == email && c.Method == "tools/call" && c.Params?.GetProperty("name").GetString() == "file_at"),
            c => Assert.Equal("develop", c.Params!.Value.GetProperty("arguments").GetProperty("ref").GetString()));
    }

    [Fact]
    public async Task Several_whole_repositories_are_reviewed_then_looked_at_across_with_how_they_depend_on_each_other()
    {
        Repository();
        var admin = await new TestBrowser(app.Factory).SignedInAsync("admin", AppFixture.AdminPassword);
        var (_, email, me) = await PersonAsync(admin);
        await StatusAssert.Is(HttpStatusCode.BadRequest, await me.PostAsync("/api/code/reviews/batches", new { projects = new[] { "g/app" } }));
        var res = await me.PostAsync("/api/code/reviews/batches", new { projects = new[] { "g/app", "g/lib" }, title = "Counting" });
        await StatusAssert.Is(HttpStatusCode.Accepted, res);
        var id = (await me.JsonAsync(res)).GetProperty("id").GetGuid();
        JsonElement batch = default;
        for (var i = 0; i < 400; i++)
        {
            batch = await me.JsonAsync(await me.GetAsync($"/api/code/reviews/batches/{id}"));
            if (batch.GetProperty("batch").GetProperty("status").GetString() is "done" or "failed")
            {
                break;
            }
            await Task.Delay(50);
        }
        Assert.True(batch.GetProperty("batch").GetProperty("status").GetString() == "done", batch.GetProperty("batch").GetProperty("error").ToString());
        Assert.Equal(["g/app", "g/lib"], batch.GetProperty("reviews").EnumerateArray().Select(r => r.GetProperty("project").GetString()));
        Assert.All(batch.GetProperty("reviews").EnumerateArray(), r => Assert.Equal("changes", r.GetProperty("verdict").GetString()));
        Assert.Contains("g/app depends on g/lib", batch.GetProperty("links").GetString(), StringComparison.Ordinal);
        Assert.StartsWith("## How they fit together", batch.GetProperty("report").GetString(), StringComparison.Ordinal);
        Assert.Equal(2, batch.GetProperty("findings").GetArrayLength());
        var asked = app.Model.Requests.Last(r => r.Body["user"]?.GetValue<string>() == email);
        Assert.Contains("# How they depend on each other", asked.Body["messages"]![1]!["content"]!.GetValue<string>(), StringComparison.Ordinal);
        var listed = (await me.JsonAsync(await me.GetAsync("/api/code/reviews/batches"))).EnumerateArray().Single(b => b.GetProperty("batch").GetProperty("id").GetGuid() == id);
        Assert.Equal((2, 2, "Counting"), (listed.GetProperty("done").GetInt32(), listed.GetProperty("changes").GetInt32(), listed.GetProperty("batch").GetProperty("title").GetString()));
        await StatusAssert.Is(HttpStatusCode.NoContent, await me.Http.DeleteAsync(new Uri($"/api/code/reviews/batches/{id}", UriKind.Relative)));
        await StatusAssert.Is(HttpStatusCode.NotFound, await me.GetAsync($"/api/code/reviews/{batch.GetProperty("reviews")[0].GetProperty("id").GetGuid()}"));
    }

    [Fact]
    public async Task A_scheduled_task_reviews_a_whole_repository_as_its_owner_and_delivers_its_summary_with_a_link()
    {
        Repository();
        Llm.Api.Code.ReviewLauncher.Poll = TimeSpan.FromMilliseconds(50);
        var admin = await new TestBrowser(app.Factory).SignedInAsync("admin", AppFixture.AdminPassword);
        var (_, email, me) = await PersonAsync(admin);
        // A review action names its repositories; one repository for a review of one.
        await StatusAssert.Is(HttpStatusCode.BadRequest, await me.PostAsync("/api/tasks", new { name = "Weekly lib review", action = "review-repository", cron = "0 6 * * 1", timeZone = "UTC" }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await me.PostAsync("/api/tasks", new { name = "Weekly", action = "review-repositories", repositories = new[] { "g/lib" }, cron = "0 6 * * 1" }));
        var made = await me.PostAsync("/api/tasks", new { name = "Weekly lib review", action = "review-repository", repositories = new[] { "g/lib" }, @ref = "develop", cron = "0 6 * * 1", timeZone = "UTC" });
        await StatusAssert.Is(HttpStatusCode.Created, made);
        var task = await me.JsonAsync(made);
        Assert.Equal(("review-repository", "develop"), (task.GetProperty("action").GetString(), task.GetProperty("ref").GetString()));
        var id = task.GetProperty("id").GetGuid();
        await StatusAssert.Is(HttpStatusCode.Accepted, await me.PostAsync($"/api/tasks/{id}/run"));
        JsonElement run = default;
        for (var i = 0; i < 400; i++)
        {
            var runs = (await me.JsonAsync(await me.GetAsync($"/api/tasks/{id}/runs"))).EnumerateArray().ToList();
            if (runs.Count > 0 && runs[0].GetProperty("status").GetString() != "running")
            {
                run = runs[0];
                break;
            }
            await Task.Delay(50);
        }
        Assert.Equal("done", run.GetProperty("status").GetString());
        var link = run.GetProperty("link").GetString()!;
        Assert.StartsWith("/code/reviews/", link, StringComparison.Ordinal);
        // The review is the owner's, of the repository at the ref, and done; the notification has its verdict and summary.
        var review = await me.JsonAsync(await me.GetAsync($"/api/code/reviews/{link["/code/reviews/".Length..]}"));
        Assert.Equal(("done", "g/lib @ develop", "repository"), (review.GetProperty("review").GetProperty("status").GetString(), review.GetProperty("review").GetProperty("title").GetString(),
            review.GetProperty("review").GetProperty("scope").GetString()));
        var bell = await me.JsonAsync(await me.GetAsync("/api/notifications"));
        var told = bell.GetProperty("items").EnumerateArray().First(n => n.GetProperty("title").GetString() == "Weekly lib review");
        Assert.Equal(link, told.GetProperty("link").GetString());
        Assert.Contains("Verdict: changes suggested", told.GetProperty("body").GetString(), StringComparison.Ordinal);
        Assert.Contains(app.CodeIntel.McpCalls, c => c.Email == email && c.Method == "tools/call" && c.Params?.GetProperty("name").GetString() == "files_at");
    }

    [Fact]
    public async Task An_agent_studio_step_reviews_a_whole_repository_and_hands_its_summary_to_the_next_step()
    {
        Repository();
        Llm.Api.Code.ReviewLauncher.Poll = TimeSpan.FromMilliseconds(50);
        var admin = await new TestBrowser(app.Factory).SignedInAsync("admin", AppFixture.AdminPassword);
        var (_, _, me) = await PersonAsync(admin);
        // A review step names what it reviews and the repositories.
        var bad = await me.PostAsync("/api/studio", new { name = "Bad", flow = new { trigger = new { kind = "manual" }, steps = new object[] { new { id = "health", kind = "review", review = "review-repository" } } } });
        await StatusAssert.Is(HttpStatusCode.BadRequest, bad);
        var flow = new
        {
            trigger = new { kind = "manual" },
            steps = new object[]
            {
                new { id = "health", kind = "review", review = "review-repository", repositories = "g/lib", @ref = "develop" },
                new { id = "said", kind = "text", text = "Health: {{health.text}}" },
            },
        };
        var made = await me.JsonAsync(await me.PostAsync("/api/studio", new { name = "Lib health", flow }));
        var id = made.GetProperty("workflow").GetProperty("id").GetGuid();
        var started = await me.JsonAsync(await me.PostAsync($"/api/studio/{id}/run", new { text = "" }));
        var runId = started.GetProperty("id").GetGuid();
        await app.Factory.Services.GetRequiredService<Llm.Api.Studio.StudioRuns>().Finished(runId).WaitAsync(TimeSpan.FromSeconds(60));
        var run = await me.JsonAsync(await me.GetAsync($"/api/studio/runs/{runId}"));
        Assert.True(run.GetProperty("status").GetString() == "done", run.ToString());
        var steps = run.GetProperty("steps").EnumerateArray().ToList();
        Assert.StartsWith("/code/reviews/", steps[0].GetProperty("link").GetString(), StringComparison.Ordinal);
        Assert.Contains("Verdict: changes suggested", steps[0].GetProperty("output").GetString(), StringComparison.Ordinal);
        Assert.StartsWith("Health: g/lib @ develop. Verdict: changes suggested", steps[1].GetProperty("output").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_shared_review_is_read_by_whom_it_is_shared_with_while_they_may_read_its_project_and_they_change_nothing()
    {
        GitLab();
        var admin = await new TestBrowser(app.Factory).SignedInAsync("admin", AppFixture.AdminPassword);
        var (_, _, me) = await PersonAsync(admin);
        var (_, _, other) = await PersonAsync(admin);
        var (_, thirdEmail, third) = await PersonAsync(admin);
        var otherName = (await other.JsonAsync(await other.GetAsync("/api/auth/me"))).GetProperty("userName").GetString()!;
        var id = (await me.JsonAsync(await me.PostAsync("/api/code/reviews", new { project = "g/app", iid = 7 }))).GetProperty("id").GetGuid();
        var done = await DoneAsync(me, id);
        var finding = done.GetProperty("findings")[0].GetProperty("id").GetGuid();
        Assert.Equal("Private", done.GetProperty("share").GetProperty("reach").GetString());
        await StatusAssert.Is(HttpStatusCode.NotFound, await other.GetAsync($"/api/code/reviews/{id}"));
        Task<HttpResponseMessage> Share(TestBrowser b, object body) => b.Http.PutAsJsonAsync(new Uri($"/api/code/reviews/{id}/share", UriKind.Relative), body);

        // With one person by name: they are told, and read it; nobody else does.
        await StatusAssert.Is(HttpStatusCode.BadRequest, await Share(me, new { reach = "Private", people = new[] { "nobody-here" } }));
        await StatusAssert.Is(HttpStatusCode.OK, await Share(me, new { reach = "Private", people = new[] { otherName } }));
        var read = await other.JsonAsync(await other.GetAsync($"/api/code/reviews/{id}"));
        Assert.True(read.GetProperty("readOnly").GetBoolean());
        Assert.Equal(JsonValueKind.Null, read.GetProperty("share").ValueKind);
        Assert.Contains((await other.JsonAsync(await other.GetAsync("/api/code/reviews"))).EnumerateArray(), r => r.GetProperty("review").GetProperty("id").GetGuid() == id && r.GetProperty("sharedBy").GetString() == "Rae");
        Assert.Contains((await other.JsonAsync(await other.GetAsync("/api/notifications"))).GetProperty("items").EnumerateArray(), n => n.GetProperty("link").GetString() == $"/code/reviews/{id}");
        await StatusAssert.Is(HttpStatusCode.NotFound, await third.GetAsync($"/api/code/reviews/{id}"));
        // To read, not to change: no deciding findings, no room, no sharing it on.
        await StatusAssert.Is(HttpStatusCode.NotFound, await other.Http.PutAsJsonAsync(new Uri($"/api/code/reviews/{id}/findings/{finding}", UriKind.Relative), new { state = "dismissed" }));
        await StatusAssert.Is(HttpStatusCode.NotFound, await other.PostAsync($"/api/code/reviews/{id}/room"));
        await StatusAssert.Is(HttpStatusCode.NotFound, await Share(other, new { reach = "Company" }));

        // With everyone: each of them, while GitLab lets them read the project.
        await StatusAssert.Is(HttpStatusCode.OK, await Share(me, new { reach = "Company" }));
        await StatusAssert.Is(HttpStatusCode.OK, await third.GetAsync($"/api/code/reviews/{id}"));
        app.CodeIntel.Unreadable[$"{thirdEmail}|g/app"] = true;
        try
        {
            await StatusAssert.Is(HttpStatusCode.NotFound, await third.GetAsync($"/api/code/reviews/{id}"));
            Assert.DoesNotContain((await third.JsonAsync(await third.GetAsync("/api/code/reviews"))).EnumerateArray(), r => r.GetProperty("review").GetProperty("id").GetGuid() == id);
        }
        finally
        {
            app.CodeIntel.Unreadable.TryRemove($"{thirdEmail}|g/app", out _);
        }

        // Taken back: theirs alone again; audited each time.
        await StatusAssert.Is(HttpStatusCode.OK, await Share(me, new { reach = "Private" }));
        await StatusAssert.Is(HttpStatusCode.NotFound, await other.GetAsync($"/api/code/reviews/{id}"));
        var audit = (await admin.JsonAsync(await admin.GetAsync("/api/admin/audit?take=50"))).EnumerateArray().ToList();
        Assert.Contains(audit, e => e.GetProperty("action").GetString() == "review.share" && e.GetProperty("target").GetString() == id.ToString() && e.GetProperty("detail").GetString() == "not shared");
        Assert.Contains(audit, e => e.GetProperty("action").GetString() == "review.share" && e.GetProperty("target").GetString() == id.ToString() && e.GetProperty("detail").GetString() == "company");
    }

    [Fact]
    [Trait("Control", "VUL-01")]
    public async Task A_merge_request_that_adds_a_vulnerable_package_or_a_scanners_finding_says_so()
    {
        const string app_py = "@@ -1,2 +1,3 @@\\n import os\\n+os.system(\\\"ping \\\" + host)\\n def main(): pass\\n";
        const string lock_ = "@@ -5,3 +5,3 @@\\n   \\\"node_modules/qs\\\": {\\n-    \\\"version\\\": \\\"6.11.0\\\"\\n+    \\\"version\\\": \\\"6.10.1\\\"\\n   },\\n";
        app.CodeIntel.Answers["mr_get"] = """{"iid": 8, "title": "Ping the host", "author": "dev", "source_branch": "ping", "target_branch": "main", "web_url": "https://gitlab.test/g/app/-/merge_requests/8", "description": "Adds a ping.", "diff_refs": {"base_sha": "aaa", "start_sha": "aaa", "head_sha": "ccc"}}""";
        app.CodeIntel.Answers["mr_diff"] = $$"""{"project": "g/app", "iid": 8, "files": [{"old_path": "app.py", "new_path": "app.py", "diff": "{{app_py}}"}, {"old_path": "web/package-lock.json", "new_path": "web/package-lock.json", "diff": "{{lock_}}"}]}""";
        app.CodeIntel.Answers["ci_status"] = """{"project": "g/app", "note": "No pipeline."}""";
        const string old = """{"ecosystem": "npm", "package": "minimist", "version": "1.2.5", "direct": false, "via": "x", "file": "web/package-lock.json", "line": 30, "advisory": "GHSA-xvch-5gv4-984h", "aliases": [], "severity": "critical", "cwes": [], "exploited": false, "summary": "Prototype pollution in minimist", "fixed_in": "1.2.6", "upgrade_to": "1.2.6", "url": "https://github.com/advisories/GHSA-xvch-5gv4-984h"}""";
        const string added = """{"ecosystem": "npm", "package": "qs", "version": "6.10.1", "direct": true, "via": null, "file": "web/package-lock.json", "line": 6, "advisory": "GHSA-hrpp-h998-j3pp", "aliases": ["CVE-2022-24999"], "severity": "high", "cwes": ["CWE-1321"], "exploited": false, "summary": "qs vulnerable to Prototype Pollution.", "fixed_in": "6.10.3", "upgrade_to": "6.10.3", "url": "https://github.com/advisories/GHSA-hrpp-h998-j3pp"}""";
        app.CodeIntel.Handlers["deps_check"] = args => args.TryGetProperty("iid", out _)
            ? $$"""{"findings": [{{old}}, {{added}}], "advisories": [{"as_of": "2026-10-28"}]}"""
            : $$"""{"findings": [{{old}}], "advisories": [{"as_of": "2026-10-28"}]}""";
        app.CodeIntel.Handlers["scan_at"] = _ => """
            {"tools": [{"name": "opengrep", "ran": true}], "more": 0, "findings": [
              {"tools": ["opengrep", "bandit"], "rules": ["python-command-from-strings"], "kind": "code", "path": "app.py", "line": 2, "cwe": "CWE-78", "severity": "high", "title": "A shell runs a command built from data", "message": "Pass the program and its arguments as a list."},
              {"tools": ["opengrep"], "rules": ["python-weak-hash"], "kind": "code", "path": "app.py", "line": 3, "cwe": "CWE-328", "severity": "low", "title": "MD5 is broken", "message": "Use SHA-256."}]}
            """;
        try
        {
            var admin = await new TestBrowser(app.Factory).SignedInAsync("admin", AppFixture.AdminPassword);
            var (_, email, me) = await PersonAsync(admin);
            var res = await me.PostAsync("/api/code/reviews", new { project = "g/app", iid = 8 });
            var done = await DoneAsync(me, (await me.JsonAsync(res)).GetProperty("id").GetGuid());
            var security = done.GetProperty("findings").EnumerateArray().Where(f => f.GetProperty("category").GetString() == "security").ToList();
            // The package the change brings, not the one the target had; and the scanners' finding on a line it adds, not on one it kept.
            var package = security.Single(f => f.GetProperty("path").GetString() == "web/package-lock.json");
            Assert.Equal(("Adds qs 6.10.1, affected by GHSA-hrpp-h998-j3pp (high)", "major", true, "qs 6.10.3"),
                (package.GetProperty("title").GetString(), package.GetProperty("severity").GetString(), package.GetProperty("verified").GetBoolean(), package.GetProperty("suggestion").GetString()));
            var command = Assert.Single(security, f => f.GetProperty("path").GetString() == "app.py");
            Assert.Equal((2, "A shell runs a command built from data"), (command.GetProperty("line").GetInt32(), command.GetProperty("title").GetString()));
            Assert.Contains("CWE-78, opengrep, bandit", command.GetProperty("body").GetString(), StringComparison.Ordinal);
            Assert.DoesNotContain(security, f => f.GetProperty("title").GetString()!.Contains("minimist", StringComparison.Ordinal));
            // The change's files alone were scanned, as its person, at the merge request's head.
            var scan = app.CodeIntel.McpCalls.Last(c => c.Method == "tools/call" && c.Params?.GetProperty("name").GetString() == "scan_at" && c.Email == email);
            var args = scan.Params!.Value.GetProperty("arguments");
            Assert.Equal((8, false, "app.py\nweb/package-lock.json"), (args.GetProperty("iid").GetInt32(), args.GetProperty("history").GetBoolean(), args.GetProperty("paths").GetString()));
        }
        finally
        {
            app.CodeIntel.Handlers.TryRemove("deps_check", out _);
            app.CodeIntel.Handlers.TryRemove("scan_at", out _);
        }
    }

    [Fact]
    public async Task The_chat_reviews_a_merge_request_with_its_tool_and_reads_the_review_again()
    {
        GitLab();
        var admin = await new TestBrowser(app.Factory).SignedInAsync("admin", AppFixture.AdminPassword);
        var (_, email, me) = await PersonAsync(admin);
        var chat = (await me.JsonAsync(await me.PostAsync("/api/chat/conversations", new { title = "Review" }))).GetProperty("id").GetGuid();
        await me.Http.PatchAsJsonAsync(new Uri($"/api/chat/conversations/{chat}", UriKind.Relative), new { tools = new[] { "reviews" } });
        var before = app.CodeIntel.McpCalls.Count;

        var done = await ToolAsync(me, chat, """Review it [call review_merge_request {"project": "g/app", "iid": "!7"}]""");
        Assert.False(done.GetProperty("isError").GetBoolean(), done.GetProperty("text").GetString());
        var review = JsonDocument.Parse(done.GetProperty("text").GetString()!).RootElement;
        Assert.Equal("changes", review.GetProperty("verdict").GetString());
        // The finding the second pass refuted is left out, and counted.
        Assert.Equal(["count.c"], review.GetProperty("findings").EnumerateArray().Select(f => f.GetProperty("path").GetString()));
        Assert.Equal(1, review.GetProperty("doubted").GetInt32());
        Assert.Contains(app.CodeIntel.McpCalls.Skip(before), c => c.Method == "tools/call" && c.Params?.GetProperty("name").GetString() == "mr_diff" && c.Email == email);

        var again = await ToolAsync(me, chat, $$"""Again [call review_get {"id": "{{review.GetProperty("id").GetString()}}"}]""");
        Assert.Equal(review.GetProperty("summary").GetString(), JsonDocument.Parse(again.GetProperty("text").GetString()!).RootElement.GetProperty("summary").GetString());
        var (_, _, other) = await PersonAsync(admin);
        var theirs = (await other.JsonAsync(await other.PostAsync("/api/chat/conversations", new { title = "Peek" }))).GetProperty("id").GetGuid();
        await other.Http.PatchAsJsonAsync(new Uri($"/api/chat/conversations/{theirs}", UriKind.Relative), new { tools = new[] { "reviews" } });
        Assert.True((await ToolAsync(other, theirs, $$"""Peek [call review_get {"id": "{{review.GetProperty("id").GetString()}}"}]""")).GetProperty("isError").GetBoolean());
    }

    private static async Task<JsonElement> ToolAsync(TestBrowser b, Guid chat, string text)
    {
        var res = await b.PostAsync($"/api/chat/conversations/{chat}/messages", new { content = text });
        return (await res.Content.ReadAsStringAsync()).Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.StartsWith("data: ", StringComparison.Ordinal)).Select(l => JsonDocument.Parse(l[6..]).RootElement)
            .Single(e => e.GetProperty("type").GetString() == "tool_result");
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
