using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Llm.Api.Gateway;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>Groups' credit (shared or per member) and one credit over the chat and API keys, held at the gateway and by the app; cost centres and chargeback.</summary>
[Collection(nameof(AppCollection))]
public sealed class GroupCreditTests(AppFixture app)
{
    /// <summary>March 2024: the spend these tests book is out of every other test's range.</summary>
    private static readonly DateTimeOffset Now = new(2024, 3, 15, 12, 0, 0, TimeSpan.Zero);

    private WebApplicationFactory<Program> NewApp(FakeGateway gateway) =>
        app.Create(app.ConnectionStringFor("credit_" + Guid.NewGuid().ToString("N")[..8]), gateway, new Dictionary<string, string?> { ["Credit:Refresh"] = "00:00:00" },
            s => s.AddSingleton<TimeProvider>(new MovableClock(Now)));

    private static async Task<(TestBrowser Browser, Guid Id, string Email)> PersonAsync(WebApplicationFactory<Program> f, TestBrowser admin, decimal? budget = null)
    {
        var name = "cr" + Guid.NewGuid().ToString("N")[..8];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test", budget }));
        return (await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!), made.GetProperty("id").GetGuid(), $"{name}@example.test");
    }

    private static async Task<Guid> GroupAsync(TestBrowser admin, object policies, params Guid[] members)
    {
        var id = (await admin.JsonAsync(await admin.PostAsync("/api/admin/groups", new { name = "g" + Guid.NewGuid().ToString("N")[..8] }))).GetProperty("id").GetGuid();
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.Http.PutAsJsonAsync(new Uri($"/api/admin/groups/{id}/policies", UriKind.Relative), policies));
        if (members.Length > 0)
        {
            await StatusAssert.Is(HttpStatusCode.NoContent, await admin.PostAsync($"/api/admin/groups/{id}/members", new { userIds = members }));
        }
        return id;
    }

    /// <summary>A chat answer: its text, or the refusal it ended with.</summary>
    private static async Task<string> AskAsync(TestBrowser b, string text)
    {
        var id = (await b.JsonAsync(await b.PostAsync("/api/chat/conversations", new { useArgus = false }))).GetProperty("id").GetGuid();
        var res = await b.PostAsync($"/api/chat/conversations/{id}/messages", new { content = text });
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        await res.Content.ReadAsStringAsync();
        var last = (await b.JsonAsync(await b.GetAsync($"/api/chat/conversations/{id}"))).GetProperty("messages").EnumerateArray().Last();
        return last.GetProperty("status").GetString() == "failed" ? "refused: " + last.GetProperty("error").GetString() : last.GetProperty("content").GetString()!;
    }

    /// <summary>What the gateway's guardrail is told for an API key's request, as LiteLLM posts it.</summary>
    public static async Task<JsonElement> GuardrailAsync(WebApplicationFactory<Program> f, string? email, params string[] texts)
    {
        var b = new TestBrowser(f);
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(Llm.Api.Safeguards.GuardrailEndpoints.Path, UriKind.Relative))
        {
            Content = JsonContent.Create(new
            {
                input_type = "request",
                texts,
                structured_messages = texts.Select(t => new { role = "user", content = t }),
                request_data = new { user_api_key_user_id = email, user_api_key_alias = "app-person", user_api_key_hash = "hash" },
                model = "Qwen3.8-Flash-Next",
            }),
        };
        req.Headers.Add("x-api-key", "sk-master-for-tests");
        var res = await b.Http.SendAsync(req);
        await StatusAssert.Is(HttpStatusCode.OK, res);
        return await b.JsonAsync(res);
    }

    private static async Task SyncTeamsAsync(WebApplicationFactory<Program> f)
    {
        await using var scope = f.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<GroupTeams>().SyncAsync();
    }

    [Fact]
    public async Task A_groups_shared_credit_stops_its_members_in_the_chat_and_through_the_API()
    {
        var gateway = new FakeGateway();
        await using var f = NewApp(gateway);
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var (ann, annId, annEmail) = await PersonAsync(f, admin);
        var (_, benId, benEmail) = await PersonAsync(f, admin);
        var group = await GroupAsync(admin, new { credit = 5, costCentre = "CC-42" }, annId, benId);

        // At the gateway: a team holding the credit a month, its members, and their keys in it.
        await SyncTeamsAsync(f);
        var (team, duration) = gateway.Teams[GroupTeams.TeamId(group)];
        Assert.Equal(5m, team.Budget);
        Assert.Null(team.MemberBudget);
        Assert.Equal("1mo", duration);
        Assert.Equal(new[] { annEmail, benEmail }.Order(), team.Members.Order());
        Assert.All(gateway.KeysOf(annEmail).Concat(gateway.KeysOf(benEmail)), k => Assert.Equal(team.Id, k.TeamId));

        Assert.StartsWith("Answer to:", await AskAsync(ann, "What is new?"), StringComparison.Ordinal);
        Assert.Equal("NONE", (await GuardrailAsync(f, benEmail, "hello")).GetProperty("action").GetString());

        // Ann's chat and Ben's key together reach the group's credit; last month's spend does not count.
        await app.SpendAsync(annEmail, 3m, Now.AddDays(-5));
        await app.SpendAsync(benEmail, 2.5m, Now.AddDays(-3), apiKey: true);
        await app.SpendAsync(benEmail, 100m, Now.AddMonths(-1), apiKey: true);
        var refused = await AskAsync(ann, "And now?");
        Assert.StartsWith("refused: ", refused, StringComparison.Ordinal);
        Assert.Contains("has used its credit for this month ($5.00", refused, StringComparison.Ordinal);
        var api = await GuardrailAsync(f, benEmail, "hello again");
        Assert.Equal("BLOCKED", api.GetProperty("action").GetString());
        Assert.Contains("has used its credit for this month", api.GetProperty("blocked_reason").GetString(), StringComparison.Ordinal);

        var detail = await admin.JsonAsync(await admin.GetAsync($"/api/admin/groups/{group}"));
        Assert.Equal(5.5m, detail.GetProperty("spentThisMonth").GetDecimal());
        Assert.Equal("CC-42", detail.GetProperty("policies").GetProperty("costCentre").GetString());
    }

    [Fact]
    public async Task Credit_per_member_holds_each_member_and_a_persons_own_credit_counts_the_chat_and_keys_together()
    {
        var gateway = new FakeGateway();
        await using var f = NewApp(gateway);
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var (ann, annId, annEmail) = await PersonAsync(f, admin);
        var (ben, benId, benEmail) = await PersonAsync(f, admin);
        var group = await GroupAsync(admin, new { credit = 2, creditPerMember = true }, annId, benId);
        await SyncTeamsAsync(f);
        Assert.Equal(2m, gateway.Teams[GroupTeams.TeamId(group)].Team.MemberBudget);
        Assert.Null(gateway.Teams[GroupTeams.TeamId(group)].Team.Budget);

        await app.SpendAsync(annEmail, 1.5m, Now.AddDays(-1));
        await app.SpendAsync(annEmail, 1m, Now.AddDays(-1), apiKey: true);
        await app.SpendAsync(benEmail, 0.5m, Now.AddDays(-1));
        Assert.Contains("your credit as a member of", await AskAsync(ann, "Am I out?"), StringComparison.Ordinal);
        Assert.Equal("BLOCKED", (await GuardrailAsync(f, annEmail, "x")).GetProperty("action").GetString());
        Assert.StartsWith("Answer to:", await AskAsync(ben, "Am I out?"), StringComparison.Ordinal);
        Assert.Equal("NONE", (await GuardrailAsync(f, benEmail, "x")).GetProperty("action").GetString());

        // A person's own credit: 2 in the chat and 1.5 by key are over 3, though each path alone is under it.
        var (cy, _, cyEmail) = await PersonAsync(f, admin, budget: 3);
        await app.SpendAsync(cyEmail, 2m, Now.AddDays(-2));
        Assert.StartsWith("Answer to:", await AskAsync(cy, "Still in?"), StringComparison.Ordinal);
        await app.SpendAsync(cyEmail, 1.5m, Now.AddDays(-1), apiKey: true);
        var refused = await AskAsync(cy, "Still in?");
        Assert.Contains("You have used all your credit for this month ($3.00, the chat and your API keys together)", refused, StringComparison.Ordinal);
        Assert.Equal("BLOCKED", (await GuardrailAsync(f, cyEmail, "x")).GetProperty("action").GetString());
    }

    [Fact]
    public async Task Removing_a_groups_credit_takes_its_keys_out_of_its_team_before_the_team_goes()
    {
        var gateway = new FakeGateway();
        await using var f = NewApp(gateway);
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var (_, annId, annEmail) = await PersonAsync(f, admin);
        var tight = await GroupAsync(admin, new { credit = 1 }, annId);
        var loose = await GroupAsync(admin, new { credit = 50 }, annId);
        await SyncTeamsAsync(f);
        // The least credit has the key.
        Assert.Equal(GroupTeams.TeamId(tight), gateway.KeysOf(annEmail).Single().TeamId);

        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.Http.PutAsJsonAsync(new Uri($"/api/admin/groups/{tight}/policies", UriKind.Relative), new { credit = (decimal?)null }));
        await SyncTeamsAsync(f);
        Assert.Equal(GroupTeams.TeamId(loose), gateway.KeysOf(annEmail).Single().TeamId);
        Assert.False(gateway.Teams.ContainsKey(GroupTeams.TeamId(tight)));
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.Http.DeleteAsync(new Uri($"/api/admin/groups/{loose}", UriKind.Relative)));
        await SyncTeamsAsync(f);
        // The key survives its team (the gateway deletes a team's keys with it).
        Assert.Null(gateway.KeysOf(annEmail).Single().TeamId);
        Assert.Empty(gateway.Teams);
        Assert.Contains("credit $1.00 shared", await (await admin.GetAsync("/api/admin/audit?take=1000")).Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_chargeback_report_has_each_groups_and_cost_centres_spend_per_month_as_csv()
    {
        await using var f = NewApp(new FakeGateway());
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var (_, annId, annEmail) = await PersonAsync(f, admin);
        var (_, benId, benEmail) = await PersonAsync(f, admin);
        var (_, _, cyEmail) = await PersonAsync(f, admin);
        var data = await GroupAsync(admin, new { costCentre = "CC-42", credit = 100 }, annId, benId);
        // Ann is in two groups of one cost centre: the cost centre counts her once.
        await GroupAsync(admin, new { costCentre = "CC-42" }, annId);
        await app.SpendAsync(annEmail, 1m, new DateTimeOffset(2024, 2, 10, 0, 0, 0, TimeSpan.Zero));
        await app.SpendAsync(annEmail, 2m, new DateTimeOffset(2024, 3, 10, 0, 0, 0, TimeSpan.Zero), apiKey: true);
        await app.SpendAsync(benEmail, 4m, new DateTimeOffset(2024, 3, 11, 0, 0, 0, TimeSpan.Zero));
        await app.SpendAsync(cyEmail, 8m, new DateTimeOffset(2024, 3, 12, 0, 0, 0, TimeSpan.Zero));

        var report = await admin.JsonAsync(await admin.GetAsync("/api/admin/groups/chargeback?from=2024-02&to=2024-03"));
        var rows = report.GetProperty("rows").EnumerateArray().ToList();
        var groupName = (await admin.JsonAsync(await admin.GetAsync($"/api/admin/groups/{data}"))).GetProperty("name").GetString();
        JsonElement Row(string month, string kind, string name) =>
            rows.Single(r => r.GetProperty("month").GetString() == month && r.GetProperty("kind").GetString() == kind && r.GetProperty("name").GetString() == name);
        Assert.Equal(1m, Row("2024-02", "group", groupName!).GetProperty("spend").GetDecimal());
        Assert.Equal(6m, Row("2024-03", "group", groupName!).GetProperty("spend").GetDecimal());
        Assert.Equal(6m, Row("2024-03", "cost centre", "CC-42").GetProperty("spend").GetDecimal());
        Assert.Equal(2, Row("2024-03", "cost centre", "CC-42").GetProperty("members").GetInt32());
        Assert.True(Row("2024-03", "none", "(in no group)").GetProperty("spend").GetDecimal() >= 8m);

        var csv = await admin.GetAsync("/api/admin/groups/chargeback?from=2024-03&to=2024-03&format=csv");
        Assert.Equal("text/csv", csv.Content.Headers.ContentType?.MediaType);
        var text = await csv.Content.ReadAsStringAsync();
        Assert.StartsWith("month,kind,name,cost_centre,members,spend,credit\n", text, StringComparison.Ordinal);
        Assert.Contains($"2024-03,group,\"{groupName}\",\"CC-42\",2,6.0000,100", text, StringComparison.Ordinal);
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.GetAsync("/api/admin/groups/chargeback?from=2024-04&to=2024-03"));
    }
}
