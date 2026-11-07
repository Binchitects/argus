using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Llm.Api.Gateway;
using Llm.Api.Notifications;
using Llm.Core.Data;
using Llm.Core.Gateway;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Llm.Tests;

/// <summary>
/// Projects: their people in roles (a project someone is not in is not there for them), global projects everyone adds to,
/// chats and Code Arena sessions put in them with their requests, the accounting from the usage log tagged with them, budgets
/// that alert and stop, and the exports.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class ProjectTests(AppFixture app)
{
    private Task<TestBrowser> AdminAsync() => new TestBrowser(app.Factory).SignedInAsync("admin", AppFixture.AdminPassword);

    private async Task<(Guid Id, TestBrowser Browser, string Email, string Key)> PersonAsync(TestBrowser admin)
    {
        var name = "pj" + Guid.NewGuid().ToString("N")[..8];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test", displayName = $"Person {name}" }));
        return (made.GetProperty("id").GetGuid(), await new TestBrowser(app.Factory).SignedInAsync(name, made.GetProperty("password").GetString()!), $"{name}@example.test",
            made.GetProperty("apiKey").GetString()!);
    }

    private static async Task<Guid> ProjectAsync(TestBrowser b, object body)
    {
        var res = await b.PostAsync("/api/projects", body);
        await StatusAssert.Is(HttpStatusCode.Created, res);
        return (await b.JsonAsync(res)).GetProperty("id").GetGuid();
    }

    private static Task<HttpResponseMessage> PatchAsync(TestBrowser b, string path, object body) => b.Http.PatchAsJsonAsync(new Uri(path, UriKind.Relative), body);

    private static Task<HttpResponseMessage> PutAsync(TestBrowser b, string path, object body) => b.Http.PutAsJsonAsync(new Uri(path, UriKind.Relative), body);

    private static async Task<Guid> ChatAsync(TestBrowser b)
    {
        var res = await b.PostAsync("/api/chat/conversations", new { useArgus = false });
        await StatusAssert.Is(HttpStatusCode.Created, res);
        return (await b.JsonAsync(res)).GetProperty("id").GetGuid();
    }

    private async Task SetFeatureAsync(Guid person, string feature, bool on)
    {
        await using var scope = app.Factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Llm.Core.Identity.AppUser>>();
        await scope.ServiceProvider.GetRequiredService<Llm.Api.Access.AccessService>().SetFeatureAsync(null, (await users.FindByIdAsync(person.ToString()))!, feature, on);
    }

    private static string Window(DateTimeOffset from, DateTimeOffset to) =>
        $"from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(to.ToString("O"))}";

    [Fact]
    [Trait("Control", "DAT-09")]
    public async Task A_project_s_people_hold_their_roles_and_it_is_not_there_for_anyone_else()
    {
        var admin = await AdminAsync();
        var (annId, ann, _, _) = await PersonAsync(admin);
        var (benId, ben, _, _) = await PersonAsync(admin);
        var (cyId, cy, _, _) = await PersonAsync(admin);
        var (_, dee, _, _) = await PersonAsync(admin);
        var id = await ProjectAsync(ann, new { name = "Payments revamp", description = "The new checkout.", dueOn = "2026-12-31" });

        // Not in it: not there.
        await StatusAssert.Is(HttpStatusCode.NotFound, await ben.GetAsync($"/api/projects/{id}"));
        Assert.DoesNotContain((await ben.JsonAsync(await ben.GetAsync("/api/projects"))).EnumerateArray(), p => p.GetProperty("id").GetGuid() == id);

        // Ben as a viewer sees it and changes nothing; Cy, through a group, adds to it.
        await StatusAssert.Is(HttpStatusCode.NoContent, await ann.PostAsync($"/api/projects/{id}/members", new { userIds = new[] { benId }, role = "viewer" }));
        var group = (await admin.JsonAsync(await admin.PostAsync("/api/admin/groups", new { name = "Checkout " + Guid.NewGuid().ToString("N")[..6] }))).GetProperty("id").GetGuid();
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.PostAsync($"/api/admin/groups/{group}/members", new { userIds = new[] { cyId } }));
        // Adding a group is sharing with it: Ann adds only a group she is in (any group needs sharing.groups).
        await StatusAssert.Is(HttpStatusCode.Forbidden, await ann.PostAsync($"/api/projects/{id}/members", new { groupIds = new[] { group }, role = "contributor" }));
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.PostAsync($"/api/admin/groups/{group}/members", new { userIds = new[] { annId } }));
        await StatusAssert.Is(HttpStatusCode.NoContent, await ann.PostAsync($"/api/projects/{id}/members", new { groupIds = new[] { group }, role = "contributor" }));

        var seen = await ben.JsonAsync(await ben.GetAsync($"/api/projects/{id}"));
        Assert.Equal(("Payments revamp", "viewer", "2026-12-31"), (seen.GetProperty("name").GetString(), seen.GetProperty("role").GetString(), seen.GetProperty("dueOn").GetString()));
        Assert.Equal(2, seen.GetProperty("members").GetArrayLength());
        await StatusAssert.Is(HttpStatusCode.Forbidden, await PatchAsync(ben, $"/api/projects/{id}", new { name = "Mine now" }));
        await StatusAssert.Is(HttpStatusCode.Forbidden, await ben.PostAsync($"/api/projects/{id}/goals", new { title = "Ship it" }));

        var goal = (await cy.JsonAsync(await cy.PostAsync($"/api/projects/{id}/goals", new { title = "Card payments", dueOn = "2026-11-30" }))).GetProperty("id").GetGuid();
        await StatusAssert.Is(HttpStatusCode.Created, await cy.PostAsync($"/api/projects/{id}/tasks", new { title = "Wire the gateway", goalId = goal }));
        await StatusAssert.Is(HttpStatusCode.NoContent, await PatchAsync(cy, $"/api/projects/{id}/goals/{goal}", new { status = "done" }));
        await StatusAssert.Is(HttpStatusCode.OK, await cy.PostAsync($"/api/projects/{id}/repositories", new { path = "https://gitlab.corp/payments/api.git" }));
        // A contributor is not an owner: the people and the budget are the owners'.
        await StatusAssert.Is(HttpStatusCode.Forbidden, await PatchAsync(cy, $"/api/projects/{id}", new { monthlyBudget = 5 }));
        await StatusAssert.Is(HttpStatusCode.Forbidden, await cy.PostAsync($"/api/projects/{id}/members", new { userIds = new[] { benId }, role = "owner" }));

        var full = await ann.JsonAsync(await ann.GetAsync($"/api/projects/{id}"));
        Assert.Equal("payments/api", full.GetProperty("repositories")[0].GetProperty("path").GetString());
        Assert.Equal("gitlab.corp", full.GetProperty("repositories")[0].GetProperty("host").GetString());
        Assert.Equal("done", full.GetProperty("goals")[0].GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, full.GetProperty("goals")[0].GetProperty("doneAt").ValueKind);
        Assert.Equal("Wire the gateway", full.GetProperty("tasks")[0].GetProperty("title").GetString());

        // Dee is in nothing: every route says so. An admin with projects.manage sees it all.
        await StatusAssert.Is(HttpStatusCode.NotFound, await dee.PostAsync($"/api/projects/{id}/goals", new { title = "x" }));
        await StatusAssert.Is(HttpStatusCode.NotFound, await dee.GetAsync($"/api/projects/{id}/report?{Window(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow)}"));
        Assert.Equal("owner", (await admin.JsonAsync(await admin.GetAsync($"/api/projects/{id}"))).GetProperty("role").GetString());

        // Archived: read only.
        await StatusAssert.Is(HttpStatusCode.NoContent, await PatchAsync(ann, $"/api/projects/{id}", new { status = "archived" }));
        await StatusAssert.Is(HttpStatusCode.Conflict, await cy.PostAsync($"/api/projects/{id}/tasks", new { title = "Late" }));
        await StatusAssert.Is(HttpStatusCode.Forbidden, await cy.Http.DeleteAsync(new Uri($"/api/projects/{id}", UriKind.Relative)));
        await StatusAssert.Is(HttpStatusCode.NoContent, await ann.Http.DeleteAsync(new Uri($"/api/projects/{id}", UriKind.Relative)));
        await StatusAssert.Is(HttpStatusCode.NotFound, await ann.GetAsync($"/api/projects/{id}"));
    }

    [Fact]
    [Trait("Control", "DAT-09")]
    public async Task A_global_project_is_everyones_to_add_to_and_making_one_is_a_permission()
    {
        var admin = await AdminAsync();
        var (annId, ann, _, _) = await PersonAsync(admin);
        var (_, ben, _, _) = await PersonAsync(admin);
        await SetFeatureAsync(annId, Llm.Core.Access.Permissions.ProjectsGlobal, on: false);
        await StatusAssert.Is(HttpStatusCode.Forbidden, await ann.PostAsync("/api/projects", new { name = "Everyone's", global = true }));
        var own = await ProjectAsync(ann, new { name = "Ann's own" });
        await StatusAssert.Is(HttpStatusCode.Forbidden, await PatchAsync(ann, $"/api/projects/{own}", new { global = true }));
        await SetFeatureAsync(annId, Llm.Core.Access.Permissions.ProjectsGlobal, on: true);
        var id = await ProjectAsync(ann, new { name = "Company handbook", global = true });

        var listed = (await ben.JsonAsync(await ben.GetAsync("/api/projects"))).EnumerateArray().Single(p => p.GetProperty("id").GetGuid() == id);
        Assert.Equal(("contributor", true, false), (listed.GetProperty("role").GetString(), listed.GetProperty("global").GetBoolean(), listed.GetProperty("mine").GetBoolean()));
        // Ben puts his chat in it, and sees it there; Ann sees that Ben has a chat in it, not its title.
        var chat = await ChatAsync(ben);
        await StatusAssert.Is(HttpStatusCode.OK, await PutAsync(ben, $"/api/chat/conversations/{chat}/project", new { projectId = id }));
        Assert.Equal(id, (await ben.JsonAsync(await ben.GetAsync($"/api/chat/conversations/{chat}"))).GetProperty("projectId").GetGuid());
        Assert.Equal(chat, (await ben.JsonAsync(await ben.GetAsync($"/api/projects/{id}"))).GetProperty("chats")[0].GetProperty("id").GetGuid());
        var annView = await ann.JsonAsync(await ann.GetAsync($"/api/projects/{id}"));
        Assert.Equal(0, annView.GetProperty("chats").GetArrayLength());
        Assert.Equal(1, annView.GetProperty("otherChats")[0].GetProperty("count").GetInt32());
        // Ann may not move Ben's chat; nobody puts a chat in a project they cannot add to.
        await StatusAssert.Is(HttpStatusCode.NotFound, await PutAsync(ann, $"/api/chat/conversations/{chat}/project", new { projectId = (Guid?)null }));
        await StatusAssert.Is(HttpStatusCode.NotFound, await PutAsync(ben, $"/api/chat/conversations/{chat}/project", new { projectId = own }));

        // Without feature.projects, no project of one's own.
        await SetFeatureAsync(annId, Llm.Core.Access.Permissions.ProjectsCreate, on: false);
        await StatusAssert.Is(HttpStatusCode.Forbidden, await ann.PostAsync("/api/projects", new { name = "Another" }));
    }

    private async Task LogAsync(string id, string email, bool apiKey, decimal spend, int prompt, int completion, DateTimeOffset at, params string[] tags)
    {
        await using var conn = new NpgsqlConnection(app.LitellmConnectionString);
        await conn.OpenAsync();
        await using var insert = new NpgsqlCommand("""
            insert into "LiteLLM_SpendLogs" (request_id, call_type, api_key, spend, total_tokens, prompt_tokens, completion_tokens, "startTime", "endTime", model, "user",
              metadata, end_user, request_tags, request_duration_ms)
            values (@id, 'acompletion', @key, @spend, @total, @prompt, @completion, @at, @end, 'qwen', '', @metadata::jsonb, @endUser, @tags::jsonb, 4000)
            """, conn);
        insert.Parameters.AddWithValue("id", id);
        insert.Parameters.AddWithValue("key", apiKey ? "hash-key-" + email : "hash-chat");
        insert.Parameters.AddWithValue("spend", (double)spend);
        insert.Parameters.AddWithValue("total", prompt + completion);
        insert.Parameters.AddWithValue("prompt", prompt);
        insert.Parameters.AddWithValue("completion", completion);
        insert.Parameters.AddWithValue("at", DateTime.SpecifyKind(at.UtcDateTime, DateTimeKind.Unspecified));
        insert.Parameters.AddWithValue("end", DateTime.SpecifyKind(at.UtcDateTime.AddSeconds(4), DateTimeKind.Unspecified));
        insert.Parameters.AddWithValue("metadata", apiKey ? $$"""{"user_api_key_user_id":"{{email}}","user_api_key_alias":"app-key"}""" : """{"user_api_key_alias":"chat"}""");
        insert.Parameters.AddWithValue("endUser", apiKey ? "" : email);
        insert.Parameters.AddWithValue("tags", JsonSerializer.Serialize(tags));
        await insert.ExecuteNonQueryAsync();
    }

    private async Task<List<GatewayRequest>> LedgerAsync(params string[] ids)
    {
        var import = app.Factory.Services.GetRequiredService<LedgerImport>();
        await using var scope = app.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        for (var i = 0; i < 40 && await db.GatewayRequests.CountAsync(r => ids.Contains(r.SourceId!)) < ids.Length; i++)
        {
            await import.ImportAsync(CancellationToken.None);
        }
        return await db.GatewayRequests.AsNoTracking().Where(r => ids.Contains(r.SourceId!)).OrderBy(r => r.SourceId).ToListAsync();
    }

    [Fact]
    [Trait("Control", "DAT-09")]
    public async Task A_projects_usage_is_its_chats_requests_and_those_tagged_with_it_and_a_moved_chat_takes_its_own()
    {
        var admin = await AdminAsync();
        var (_, ann, annEmail, _) = await PersonAsync(admin);
        var (_, ben, benEmail, _) = await PersonAsync(admin);
        var id = await ProjectAsync(ann, new { name = "Search" });
        var other = await ProjectAsync(ben, new { name = "Ben's" });
        var chat = await ChatAsync(ann);

        // The chat's requests carry its tag (the first tag still says what part of the app asked).
        var sent = await ann.PostAsync($"/api/chat/conversations/{chat}/messages", new { content = "Where is the index built?" });
        await sent.Content.ReadAsStringAsync();
        Assert.Contains("chat:" + chat, app.Model.Requests.Last(r => r.Body["user"]?.GetValue<string>() == annEmail).Body["metadata"]!["tags"]!.AsArray().Select(t => t!.GetValue<string>()));

        var now = DateTimeOffset.UtcNow.AddMinutes(-30);
        var tag = Guid.NewGuid().ToString("N")[..6];
        string[] ids = [$"pa-{tag}-1", $"pa-{tag}-2", $"pa-{tag}-3", $"pa-{tag}-4", $"pa-{tag}-5"];
        await LogAsync(ids[0], annEmail, false, 0.01m, 1000, 100, now, "chat:" + chat);
        await LogAsync(ids[1], annEmail, false, 0.002m, 50, 10, now, "title", "chat:" + chat);
        // Code Arena with Ann's key, naming the project; Ben naming Ann's project, which he is not in; Ann naming Ben's.
        await LogAsync(ids[2], annEmail, true, 0.03m, 3000, 300, now.AddMinutes(5), "project:" + id);
        await LogAsync(ids[3], benEmail, true, 0.5m, 9000, 900, now.AddMinutes(5), "project:" + id);
        await LogAsync(ids[4], annEmail, true, 0.7m, 9000, 900, now.AddMinutes(5), "project:" + other);
        var rows = await LedgerAsync(ids);
        Assert.Equal((chat, (Guid?)null, "chat", (string?)null), (rows[0].ConversationId!.Value, rows[0].ProjectId, rows[0].Surface, rows[0].Tag));
        Assert.Equal(("app", "title"), (rows[1].Surface, rows[1].Tag));
        Assert.Equal(id, rows[2].ProjectId);
        Assert.Null(rows[3].ProjectId);
        Assert.Null(rows[4].ProjectId);

        // Put in the project, the chat's requests go with it; the report counts them and the tagged one.
        await StatusAssert.Is(HttpStatusCode.OK, await PutAsync(ann, $"/api/chat/conversations/{chat}/project", new { projectId = id }));
        var report = await ann.JsonAsync(await ann.GetAsync($"/api/projects/{id}/report?{Window(now.AddHours(-1), DateTimeOffset.UtcNow.AddMinutes(1))}"));
        var totals = report.GetProperty("totals");
        Assert.Equal(3, totals.GetProperty("requests").GetInt32());
        Assert.Equal(0.042m, totals.GetProperty("cost").GetDecimal());
        Assert.Equal(1000 + 50 + 3000, totals.GetProperty("input").GetInt64() + totals.GetProperty("cached").GetInt64());
        Assert.Equal(410, totals.GetProperty("output").GetInt64());
        Assert.Equal(12, totals.GetProperty("agentSeconds").GetDouble());
        // Her question in the chat is a stretch of her own time.
        Assert.True(totals.GetProperty("activeSeconds").GetDouble() >= 60);
        var person = report.GetProperty("people").EnumerateArray().Single();
        Assert.Equal((annEmail, 3), (person.GetProperty("email").GetString(), person.GetProperty("requests").GetInt32()));
        Assert.Equal(1, report.GetProperty("progress").GetProperty("chats").GetInt32());
        Assert.Contains(report.GetProperty("surfaces").EnumerateArray(), s => s.GetProperty("name").GetString() == "API keys and Code Arena");

        // Out of it again: its requests are no longer the project's.
        await StatusAssert.Is(HttpStatusCode.OK, await PutAsync(ann, $"/api/chat/conversations/{chat}/project", new { projectId = (Guid?)null }));
        report = await ann.JsonAsync(await ann.GetAsync($"/api/projects/{id}/report?{Window(now.AddHours(-1), DateTimeOffset.UtcNow.AddMinutes(1))}"));
        Assert.Equal(1, report.GetProperty("totals").GetProperty("requests").GetInt32());
        await StatusAssert.Is(HttpStatusCode.BadRequest, await ann.GetAsync($"/api/projects/{id}/report?{Window(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(-1))}"));
    }

    [Fact]
    [Trait("Control", "DAT-09")]
    public async Task Each_persons_share_is_for_the_owners_and_a_viewer_sees_their_own()
    {
        var admin = await AdminAsync();
        var (_, ann, _, _) = await PersonAsync(admin);
        var (benId, ben, benEmail, _) = await PersonAsync(admin);
        var id = await ProjectAsync(ann, new { name = "Shares" });
        await StatusAssert.Is(HttpStatusCode.NoContent, await ann.PostAsync($"/api/projects/{id}/members", new { userIds = new[] { benId }, role = "viewer" }));
        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.GatewayRequests.AddRange(
                new GatewayRequest { StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5), EndedAt = DateTimeOffset.UtcNow, Surface = "api", Call = "chat", CreditKind = "api", Model = "qwen",
                    Status = GatewayRequest.Done, Cost = 0.2m, PromptTokens = 10, CompletionTokens = 1, UserId = benId, Person = benEmail, ProjectId = id },
                new GatewayRequest { StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5), EndedAt = DateTimeOffset.UtcNow, Surface = "api", Call = "chat", CreditKind = "api", Model = "qwen",
                    Status = GatewayRequest.Done, Cost = 0.3m, PromptTokens = 10, CompletionTokens = 1, Person = "someone@example.test", ProjectId = id });
            await db.SaveChangesAsync();
        }
        var window = Window(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddMinutes(1));
        var asOwner = await ann.JsonAsync(await ann.GetAsync($"/api/projects/{id}/report?{window}"));
        Assert.Equal(2, asOwner.GetProperty("people").GetArrayLength());
        var asViewer = await ben.JsonAsync(await ben.GetAsync($"/api/projects/{id}/report?{window}"));
        Assert.True(asViewer.GetProperty("peopleHidden").GetBoolean());
        Assert.Equal(benEmail, asViewer.GetProperty("people").EnumerateArray().Single().GetProperty("email").GetString());
        Assert.Equal(0.5m, asViewer.GetProperty("totals").GetProperty("cost").GetDecimal());
    }

    [Fact]
    [Trait("Control", "DAT-09")]
    public async Task A_budget_tells_the_owners_and_one_that_stops_refuses_its_chats_answers()
    {
        var admin = await AdminAsync();
        var (annId, ann, _, _) = await PersonAsync(admin);
        var id = await ProjectAsync(ann, new { name = "Tight " + Guid.NewGuid().ToString("N")[..4] });
        await StatusAssert.Is(HttpStatusCode.NoContent, await PatchAsync(ann, $"/api/projects/{id}", new { monthlyBudget = 1.0m, alertPercent = 50, stopAtBudget = true }));
        var chat = await ChatAsync(ann);
        await StatusAssert.Is(HttpStatusCode.OK, await PutAsync(ann, $"/api/chat/conversations/{chat}/project", new { projectId = id }));

        async Task SpendAsync(decimal cost)
        {
            await using var scope = app.Factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.GatewayRequests.Add(new GatewayRequest
            {
                StartedAt = DateTimeOffset.UtcNow, EndedAt = DateTimeOffset.UtcNow, Surface = "api", Call = "chat", CreditKind = "api", Model = "qwen", Status = GatewayRequest.Done,
                Cost = cost, UserId = annId, ProjectId = id,
            });
            await db.SaveChangesAsync();
        }
        async Task<List<string>> NewsAsync() =>
            [.. (await ann.JsonAsync(await ann.GetAsync("/api/notifications"))).GetProperty("items").EnumerateArray().Where(n => n.GetProperty("link").GetString() == $"/projects/{id}")
                .Select(n => n.GetProperty("title").GetString()!)];
        var watch = app.Factory.Services.GetRequiredService<NewsWatch>();

        await SpendAsync(0.6m);
        await watch.CheckProjectsAsync(CancellationToken.None);
        await watch.CheckProjectsAsync(CancellationToken.None);
        Assert.Equal([$"60% of {(await ann.JsonAsync(await ann.GetAsync($"/api/projects/{id}"))).GetProperty("name").GetString()}'s budget is spent"], await NewsAsync());
        var answer = await ann.PostAsync($"/api/chat/conversations/{chat}/messages", new { content = "Still fine?" });
        await answer.Content.ReadAsStringAsync();
        Assert.Equal("complete", (await ann.JsonAsync(await ann.GetAsync($"/api/chat/conversations/{chat}"))).GetProperty("messages").EnumerateArray().Last().GetProperty("status").GetString());

        await SpendAsync(0.5m);
        await watch.CheckProjectsAsync(CancellationToken.None);
        Assert.Equal(2, (await NewsAsync()).Count);
        answer = await ann.PostAsync($"/api/chat/conversations/{chat}/messages", new { content = "And now?" });
        await answer.Content.ReadAsStringAsync();
        var last = (await ann.JsonAsync(await ann.GetAsync($"/api/chat/conversations/{chat}"))).GetProperty("messages").EnumerateArray().Last();
        Assert.Equal("failed", last.GetProperty("status").GetString());
        Assert.Contains("has spent its budget for this month ($1.10 of $1.00)", last.GetProperty("error").GetString(), StringComparison.Ordinal);
        // A chat out of the project answers again.
        await StatusAssert.Is(HttpStatusCode.OK, await PutAsync(ann, $"/api/chat/conversations/{chat}/project", new { projectId = (Guid?)null }));
        answer = await ann.PostAsync($"/api/chat/conversations/{chat}/messages", new { content = "Out of it?" });
        await answer.Content.ReadAsStringAsync();
        Assert.Equal("complete", (await ann.JsonAsync(await ann.GetAsync($"/api/chat/conversations/{chat}"))).GetProperty("messages").EnumerateArray().Last().GetProperty("status").GetString());
    }

    /// <summary>Code Arena: no cookies, the person's key.</summary>
    private HttpClient CodeArena(string key)
    {
        var http = app.Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"https://{AppFixture.Domain}"), AllowAutoRedirect = false, HandleCookies = false });
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        http.DefaultRequestHeaders.Add("X-Requested-With", "code-arena");
        return http;
    }

    [Fact]
    [Trait("Control", "DAT-09")]
    public async Task A_Code_Arena_session_goes_in_its_repositorys_project_or_the_one_chosen()
    {
        var admin = await AdminAsync();
        var (_, ann, _, key) = await PersonAsync(admin);
        var (_, ben, _, _) = await PersonAsync(admin);
        var id = await ProjectAsync(ann, new { name = "API" });
        var chosen = await ProjectAsync(ann, new { name = "Chosen" });
        var bens = await ProjectAsync(ben, new { name = "Not Ann's" });
        await StatusAssert.Is(HttpStatusCode.OK, await ann.PostAsync($"/api/projects/{id}/repositories", new { path = "payments/api" }));
        var arena = CodeArena(key);

        var listed = JsonDocument.Parse(await arena.GetStringAsync(new Uri("/api/code-arena/projects?repo=gitlab.corp/Payments/API", UriKind.Relative))).RootElement;
        Assert.Equal(id, listed.GetProperty("suggested").GetGuid());
        Assert.DoesNotContain(listed.GetProperty("projects").EnumerateArray(), p => p.GetProperty("id").GetGuid() == bens);

        var made = JsonDocument.Parse(await (await arena.PostAsJsonAsync("/api/code-arena/chats", new { @ref = "s-" + Guid.NewGuid().ToString("N")[..6], place = "/w/api", project = "gitlab.corp/payments/api" }))
            .Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(id, made.GetProperty("projectId").GetGuid());
        // Named, so Code Arena says which project the session went in.
        Assert.Equal("API", made.GetProperty("projectName").GetString());
        var picked = JsonDocument.Parse(await (await arena.PostAsJsonAsync("/api/code-arena/chats", new { @ref = "s-" + Guid.NewGuid().ToString("N")[..6], place = "/w/api", project = "gitlab.corp/payments/api", projectId = chosen }))
            .Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(chosen, picked.GetProperty("projectId").GetGuid());
        // One Ann may not add to is not taken: the repository's is.
        var refused = JsonDocument.Parse(await (await arena.PostAsJsonAsync("/api/code-arena/chats", new { @ref = "s-" + Guid.NewGuid().ToString("N")[..6], place = "/w/x", project = "local/abc", projectId = bens }))
            .Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(JsonValueKind.Null, refused.GetProperty("projectId").ValueKind);
        Assert.Equal(JsonValueKind.Null, refused.GetProperty("projectName").ValueKind);

        // /project in Code Arena: moved, and out.
        var session = made.GetProperty("id").GetGuid();
        await StatusAssert.Is(HttpStatusCode.OK, await arena.PutAsJsonAsync($"/api/code-arena/chats/{session}/project", new { projectId = chosen }));
        Assert.Equal("session", (await ann.JsonAsync(await ann.GetAsync($"/api/projects/{chosen}"))).GetProperty("chats").EnumerateArray().First(c => c.GetProperty("id").GetGuid() == session)
            .GetProperty("kind").GetString());
        await StatusAssert.Is(HttpStatusCode.NotFound, await arena.PutAsJsonAsync($"/api/code-arena/chats/{session}/project", new { projectId = bens }));
    }

    [Fact]
    [Trait("Control", "DAT-09")]
    public async Task Code_Arena_reaches_a_project_with_the_persons_key_and_Arena_checks_it_as_for_the_page()
    {
        var admin = await AdminAsync();
        var (annId, ann, _, annKey) = await PersonAsync(admin);
        var (benId, _, _, benKey) = await PersonAsync(admin);
        var id = await ProjectAsync(ann, new { name = "From the IDE" });
        await StatusAssert.Is(HttpStatusCode.NoContent, await ann.PostAsync($"/api/projects/{id}/members", new { userIds = new[] { benId }, role = "viewer" }));
        var arena = CodeArena(annKey);

        // The page's routes, with the key: the list, the project, its report, a goal, a task ticked off, its export.
        Assert.Contains((await arena.GetFromJsonAsync<JsonElement>("/api/projects")).EnumerateArray(), p => p.GetProperty("id").GetGuid() == id);
        Assert.Equal("owner", (await arena.GetFromJsonAsync<JsonElement>($"/api/projects/{id}")).GetProperty("role").GetString());
        var window = Window(DateTimeOffset.UtcNow.AddDays(-7), DateTimeOffset.UtcNow.AddMinutes(1));
        await StatusAssert.Is(HttpStatusCode.OK, await arena.GetAsync($"/api/projects/{id}/report?{window}"));
        await StatusAssert.Is(HttpStatusCode.Created, await arena.PostAsJsonAsync($"/api/projects/{id}/goals", new { title = "Typed in the IDE" }));
        var task = (await (await arena.PostAsJsonAsync($"/api/projects/{id}/tasks", new { title = "Tick me" })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await StatusAssert.Is(HttpStatusCode.NoContent, await arena.PatchAsJsonAsync($"/api/projects/{id}/tasks/{task}", new { done = true }));
        Assert.Equal("text/csv", (await arena.GetAsync($"/api/projects/{id}/report/export?format=csv&{window}")).Content.Headers.ContentType!.MediaType);
        Assert.True((await arena.GetFromJsonAsync<JsonElement>("/api/projects/may")).GetProperty("create").GetBoolean());
        await StatusAssert.Is(HttpStatusCode.OK, await arena.GetAsync("/api/sharing/groups"));

        // Making one is the same permission as on the page, and a global one too.
        await SetFeatureAsync(annId, Llm.Core.Access.Permissions.ProjectsGlobal, on: false);
        Assert.False((await arena.GetFromJsonAsync<JsonElement>("/api/projects/may")).GetProperty("global").GetBoolean());
        await StatusAssert.Is(HttpStatusCode.Forbidden, await arena.PostAsJsonAsync("/api/projects", new { name = "Everyone's", global = true }));
        await StatusAssert.Is(HttpStatusCode.Created, await arena.PostAsJsonAsync("/api/projects", new { name = "Made in the terminal" }));
        await SetFeatureAsync(annId, Llm.Core.Access.Permissions.ProjectsCreate, on: false);
        await StatusAssert.Is(HttpStatusCode.Forbidden, await arena.PostAsJsonAsync("/api/projects", new { name = "Not now" }));
        Assert.False((await arena.GetFromJsonAsync<JsonElement>("/api/projects/may")).GetProperty("create").GetBoolean());

        // Ben, a viewer, sees it and changes nothing with his key either; without Code Arena his key reaches nothing here.
        var bens = CodeArena(benKey);
        Assert.Equal("viewer", (await bens.GetFromJsonAsync<JsonElement>($"/api/projects/{id}")).GetProperty("role").GetString());
        await StatusAssert.Is(HttpStatusCode.Forbidden, await bens.PostAsJsonAsync($"/api/projects/{id}/goals", new { title = "Mine" }));
        await StatusAssert.Is(HttpStatusCode.Forbidden, await bens.PatchAsJsonAsync($"/api/projects/{id}/tasks/{task}", new { done = false }));
        await SetFeatureAsync(benId, Llm.Core.Access.Permissions.CodeArena, on: false);
        await StatusAssert.Is(HttpStatusCode.Forbidden, await bens.GetAsync($"/api/projects/{id}"));
        await StatusAssert.Is(HttpStatusCode.Forbidden, await bens.GetAsync("/api/sharing/groups"));
        // A key that is no one's: refused before any route.
        await StatusAssert.Is(HttpStatusCode.Unauthorized, await CodeArena("sk-not-a-key").GetAsync("/api/projects"));
        // Moving a chat on the page's route stays the page's (Code Arena has its own, by the session's chat).
        await StatusAssert.Is(HttpStatusCode.Unauthorized, await arena.PutAsJsonAsync($"/api/chat/conversations/{Guid.NewGuid()}/project", new { projectId = id }));
    }

    [Fact]
    [Trait("Control", "DAT-09")]
    public async Task A_projects_report_and_the_usage_reports_export_as_pdf_excel_and_csv()
    {
        var admin = await AdminAsync();
        var (annId, ann, annEmail, _) = await PersonAsync(admin);
        var (benId, ben, _, _) = await PersonAsync(admin);
        var id = await ProjectAsync(ann, new { name = "Exports Ünïcode test", description = "Numbers to pass on." });
        await StatusAssert.Is(HttpStatusCode.NoContent, await ann.PostAsync($"/api/projects/{id}/members", new { userIds = new[] { benId }, role = "viewer" }));
        await StatusAssert.Is(HttpStatusCode.Created, await ann.PostAsync($"/api/projects/{id}/goals", new { title = "First release" }));
        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            for (var i = 0; i < 6; i++)
            {
                db.GatewayRequests.Add(new GatewayRequest
                {
                    StartedAt = DateTimeOffset.UtcNow.AddDays(-i).AddMinutes(-5), EndedAt = DateTimeOffset.UtcNow.AddDays(-i), Surface = "api", Call = "chat", CreditKind = "api",
                    Model = "qwen", Status = GatewayRequest.Done, Cost = 0.25m, PromptTokens = 1000, CompletionTokens = 200, UserId = annId, Person = annEmail, ProjectId = id,
                });
            }
            await db.SaveChangesAsync();
        }
        var window = Window(DateTimeOffset.UtcNow.AddDays(-7), DateTimeOffset.UtcNow.AddMinutes(1));

        var pdf = await ann.GetAsync($"/api/projects/{id}/report/export?format=pdf&{window}");
        await StatusAssert.Is(HttpStatusCode.OK, pdf);
        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType!.MediaType);
        Assert.StartsWith("exports-ünïcode-test-", pdf.Content.Headers.ContentDisposition!.FileNameStar, StringComparison.Ordinal);
        using (var doc = UglyToad.PdfPig.PdfDocument.Open(await pdf.Content.ReadAsByteArrayAsync()))
        {
            var text = string.Join("\n", doc.GetPages().Select(p => p.Text));
            Assert.Contains("Exports Ünïcode test", text, StringComparison.Ordinal);
            Assert.Contains("$1.50", text, StringComparison.Ordinal);
            Assert.Contains("First release", text, StringComparison.Ordinal);
            Assert.Contains("Page 1 of", text, StringComparison.Ordinal);
            Assert.Contains("Person pj", text, StringComparison.Ordinal);
        }

        var xlsx = await ann.GetAsync($"/api/projects/{id}/report/export?format=xlsx&{window}");
        await StatusAssert.Is(HttpStatusCode.OK, xlsx);
        var sheets = Llm.Api.Insights.TabularFiles.Read("report.xlsx", await xlsx.Content.ReadAsByteArrayAsync());
        var people = sheets.Single(s => s.Name == "report People");
        Assert.Equal(["Person", "Requests", "Tokens", "Cost", "Agent time (hours)", "Active time (hours)", "Messages"], people.Header);
        Assert.Equal(6.0, Convert.ToDouble(people.Rows[0][1], System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(1.5, Convert.ToDouble(people.Rows[0][3], System.Globalization.CultureInfo.InvariantCulture), 6);
        Assert.Contains(sheets, s => s.Name == "report Summary");

        var csv = await ben.GetAsync($"/api/projects/{id}/report/export?format=csv&{window}");
        var bytes = await csv.Content.ReadAsByteArrayAsync();
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        var lines = System.Text.Encoding.UTF8.GetString(bytes[3..]).Split("\r\n");
        Assert.Equal("Exports Ünïcode test", lines[0]);
        // A viewer's export has their own row only, and says so.
        var at = Array.IndexOf(lines, "People");
        Assert.StartsWith("Only your own row", lines[at + 1], StringComparison.Ordinal);
        Assert.Equal("Person,Requests,Tokens,Cost ($),Agent time (hours),Active time (hours),Messages", lines[at + 2]);
        Assert.Equal("", lines[at + 3]);
        await StatusAssert.Is(HttpStatusCode.BadRequest, await ann.GetAsync($"/api/projects/{id}/report/export?format=docx&{window}"));

        // The usage reports: one's own for everyone, everyone's and the groups' with usage.read.
        var mine = await ann.GetAsync($"/api/usage/me/export?format=csv&{window}");
        await StatusAssert.Is(HttpStatusCode.OK, mine);
        Assert.Contains("Cost,$1.50", System.Text.Encoding.UTF8.GetString(await mine.Content.ReadAsByteArrayAsync()), StringComparison.Ordinal);
        await StatusAssert.Is(HttpStatusCode.Forbidden, await ann.GetAsync($"/api/admin/usage/export?format=pdf&{window}"));
        var everyone = await admin.GetAsync($"/api/admin/usage/export?format=xlsx&{window}");
        await StatusAssert.Is(HttpStatusCode.OK, everyone);
        Assert.Contains(Llm.Api.Insights.TabularFiles.Read("u.xlsx", await everyone.Content.ReadAsByteArrayAsync()).Single(s => s.Name == "u People").Rows,
            r => (string?)r[1] == annEmail);
        var groups = await admin.GetAsync($"/api/admin/usage/groups/export?format=pdf&{window}");
        await StatusAssert.Is(HttpStatusCode.OK, groups);
        using (var doc = UglyToad.PdfPig.PdfDocument.Open(await groups.Content.ReadAsByteArrayAsync()))
        {
            Assert.Contains("(in no group)", string.Join("\n", doc.GetPages().Select(p => p.Text)), StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("payments/api", null, "payments/api", null)]
    [InlineData("https://gitlab.corp/payments/api.git", null, "payments/api", "gitlab.corp")]
    [InlineData("git@gitlab.corp:group/sub/repo.git", null, "group/sub/repo", "gitlab.corp")]
    [InlineData("https://gitlab.corp/payments/api/-/tree/main", null, "payments/api", "gitlab.corp")]
    [InlineData("payments/api", "GitLab.Corp", "payments/api", "gitlab.corp")]
    public void A_repository_is_known_by_its_path_from_any_address(string text, string? host, string path, string? expectedHost) =>
        Assert.Equal((path, expectedHost), Llm.Api.Projects.ProjectEndpoints.RepositoryOf(text, host));

    [Theory]
    [InlineData("api")]
    [InlineData("has space/x")]
    [InlineData("")]
    public void A_repository_without_a_group_or_with_spaces_is_refused(string text) => Assert.Null(Llm.Api.Projects.ProjectEndpoints.RepositoryOf(text, null));
}
