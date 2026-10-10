using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Llm.Api.Gateway;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>
/// Credits, one per kind (the chat's answers, API keys' text requests, pictures, video, speech), a person's own and their
/// groups' (shared or per member), each held by the app to this month's spend of its kind; the gateway holds no budget.
/// Cost centres and chargeback.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class GroupCreditTests(AppFixture app)
{
    /// <summary>March 2024: the spend these tests book is out of every other test's range.</summary>
    private static readonly DateTimeOffset Now = new(2024, 3, 15, 12, 0, 0, TimeSpan.Zero);

    private const string Picture = Llm.Api.Models.MediaModels.ImageModel;

    private WebApplicationFactory<Program> NewApp(FakeGateway gateway) =>
        app.Create(app.ConnectionStringFor("credit_" + Guid.NewGuid().ToString("N")[..8]), gateway, new Dictionary<string, string?> { ["Credit:Refresh"] = "00:00:00" },
            s => s.AddSingleton<TimeProvider>(new MovableClock(Now)));

    private static async Task<(TestBrowser Browser, Guid Id, string Email)> PersonAsync(WebApplicationFactory<Program> f, TestBrowser admin, object? credits = null)
    {
        var name = "cr" + Guid.NewGuid().ToString("N")[..8];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test", credits }));
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
    public static async Task<JsonElement> GuardrailAsync(WebApplicationFactory<Program> f, string? email, params string[] texts) =>
        await GuardrailForAsync(f, email, "Qwen3.8-Flash-Next", texts);

    /// <summary>The guardrail's verdict on an API key's request to <paramref name="model"/>.</summary>
    public static async Task<JsonElement> GuardrailForAsync(WebApplicationFactory<Program> f, string? email, string model, params string[] texts)
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
                model,
            }),
        };
        req.Headers.Add("x-api-key", "sk-master-for-tests");
        var res = await b.Http.SendAsync(req);
        await StatusAssert.Is(HttpStatusCode.OK, res);
        return await b.JsonAsync(res);
    }

    [Fact]
    public async Task A_groups_shared_chat_credit_stops_its_members_answers_and_its_API_credit_their_keys_each_on_its_own()
    {
        var gateway = new FakeGateway();
        await using var f = NewApp(gateway);
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var (ann, annId, annEmail) = await PersonAsync(f, admin);
        var (_, benId, benEmail) = await PersonAsync(f, admin);
        var group = await GroupAsync(admin, new { credits = new { chat = 5, api = 3 }, costCentre = "CC-42" }, annId, benId);

        Assert.StartsWith("Answer to:", await AskAsync(ann, "What is new?"), StringComparison.Ordinal);
        Assert.Equal("NONE", (await GuardrailAsync(f, benEmail, "hello")).GetProperty("action").GetString());

        // Ann's and Ben's chats together reach the group's chat credit; last month's spend does not count, nor the keys'.
        await app.SpendAsync(annEmail, 3m, Now.AddDays(-5));
        await app.SpendAsync(benEmail, 2.5m, Now.AddDays(-3));
        await app.SpendAsync(benEmail, 100m, Now.AddMonths(-1));
        await app.SpendAsync(benEmail, 1m, Now.AddDays(-3), apiKey: true);
        var refused = await AskAsync(ann, "And now?");
        Assert.StartsWith("refused: ", refused, StringComparison.Ordinal);
        Assert.Contains("has used its chat credit for this month ($5.00, shared by its members)", refused, StringComparison.Ordinal);
        // The keys have their own credit: under it, they go on.
        Assert.Equal("NONE", (await GuardrailAsync(f, benEmail, "hello again")).GetProperty("action").GetString());
        await app.SpendAsync(annEmail, 2.5m, Now.AddDays(-1), apiKey: true);
        var api = await GuardrailAsync(f, benEmail, "and again");
        Assert.Equal("BLOCKED", api.GetProperty("action").GetString());
        Assert.Contains("has used its API credit for this month ($3.00", api.GetProperty("blocked_reason").GetString(), StringComparison.Ordinal);

        var detail = await admin.JsonAsync(await admin.GetAsync($"/api/admin/groups/{group}"));
        Assert.Equal(9m, detail.GetProperty("spentThisMonth").GetDecimal());
        Assert.Equal(5.5m, detail.GetProperty("spentByKind").GetProperty("chat").GetDecimal());
        Assert.Equal(3.5m, detail.GetProperty("spentByKind").GetProperty("api").GetDecimal());
        Assert.Equal(5m, detail.GetProperty("policies").GetProperty("credits").GetProperty("chat").GetDecimal());
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("policies").GetProperty("credits").GetProperty("pictures").ValueKind);
        Assert.Equal("CC-42", detail.GetProperty("policies").GetProperty("costCentre").GetString());
        // The gateway holds no budget: no team for the group.
        Assert.Empty(gateway.Teams);
    }

    [Fact]
    public async Task Each_kind_holds_on_its_own_a_members_credit_and_a_persons_own()
    {
        var gateway = new FakeGateway();
        await using var f = NewApp(gateway);
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var (ann, annId, annEmail) = await PersonAsync(f, admin);
        var (ben, benId, benEmail) = await PersonAsync(f, admin);
        await GroupAsync(admin, new { credits = new { chat = 2, pictures = 1 }, creditPerMember = true }, annId, benId);

        await app.SpendAsync(annEmail, 2.5m, Now.AddDays(-1));
        await app.SpendAsync(benEmail, 0.5m, Now.AddDays(-1));
        Assert.Contains("your chat credit as a member of", await AskAsync(ann, "Am I out?"), StringComparison.Ordinal);
        Assert.StartsWith("Answer to:", await AskAsync(ben, "Am I out?"), StringComparison.Ordinal);
        // Ann's text requests by key have no credit of the group: they go on.
        Assert.Equal("NONE", (await GuardrailAsync(f, annEmail, "x")).GetProperty("action").GetString());
        // Pictures count to the picture credit, whichever way they are made.
        Assert.Equal("NONE", (await GuardrailForAsync(f, annEmail, Picture, "a cat")).GetProperty("action").GetString());
        await app.SpendAsync(annEmail, 1m, Now.AddDays(-1), apiKey: true, model: Picture, callType: "aimage_generation");
        var picture = await GuardrailForAsync(f, annEmail, Picture, "another cat");
        Assert.Equal("BLOCKED", picture.GetProperty("action").GetString());
        Assert.Contains("your picture credit as a member of", picture.GetProperty("blocked_reason").GetString(), StringComparison.Ordinal);
        Assert.Equal("NONE", (await GuardrailForAsync(f, benEmail, Picture, "a dog")).GetProperty("action").GetString());

        // A person's own credits: 2 in the chat leaves the chat under 3; 1.5 by key is over the API's 1, which leaves the chat be.
        var (cy, cyId, cyEmail) = await PersonAsync(f, admin, new { chat = 3, api = 1 });
        await app.SpendAsync(cyEmail, 2m, Now.AddDays(-2));
        await app.SpendAsync(cyEmail, 1.5m, Now.AddDays(-1), apiKey: true);
        Assert.StartsWith("Answer to:", await AskAsync(cy, "Still in?"), StringComparison.Ordinal);
        var key = await GuardrailAsync(f, cyEmail, "x");
        Assert.Equal("BLOCKED", key.GetProperty("action").GetString());
        Assert.Contains("You have used all your API credit for this month ($1.00)", key.GetProperty("blocked_reason").GetString(), StringComparison.Ordinal);
        await app.SpendAsync(cyEmail, 1m, Now.AddDays(-1));
        Assert.Contains("You have used all your chat credit for this month ($3.00)", await AskAsync(cy, "Still in?"), StringComparison.Ordinal);

        // The person's page: each kind's spend and credit, the tightest group's, and what is used up.
        var page = await admin.JsonAsync(await admin.GetAsync($"/api/admin/people/{cyId}"));
        var credits = page.GetProperty("person").GetProperty("credits");
        Assert.Equal(3m, credits.GetProperty("chat").GetProperty("spent").GetDecimal());
        Assert.Equal(3m, credits.GetProperty("chat").GetProperty("credit").GetDecimal());
        Assert.Equal(JsonValueKind.Null, credits.GetProperty("video").GetProperty("credit").ValueKind);
        Assert.Equal(["chat", "api"], page.GetProperty("person").GetProperty("overCredit").EnumerateArray().Select(k => k.GetString()));
        var annPage = await admin.JsonAsync(await admin.GetAsync($"/api/admin/people/{annId}"));
        var annPictures = annPage.GetProperty("standing").EnumerateArray().Single(s => s.GetProperty("kind").GetString() == "pictures");
        Assert.Equal(0m, annPictures.GetProperty("groupLeft").GetDecimal());

        // Credits changed by an admin hold at once; a negative one is refused.
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.Http.PutAsJsonAsync(new Uri($"/api/admin/people/{cyId}/credits", UriKind.Relative), new { chat = 10, api = (decimal?)null }));
        Assert.StartsWith("Answer to:", await AskAsync(cy, "Back?"), StringComparison.Ordinal);
        Assert.Equal("NONE", (await GuardrailAsync(f, cyEmail, "x")).GetProperty("action").GetString());
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.Http.PutAsJsonAsync(new Uri($"/api/admin/people/{cyId}/credits", UriKind.Relative), new { video = -1 }));
        Assert.Contains("chat $10.00; API no limit", await (await admin.GetAsync("/api/admin/audit?take=1000")).Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_teams_an_older_version_made_at_the_gateway_go_once_their_keys_are_out_of_them()
    {
        var gateway = new FakeGateway();
        await using var f = NewApp(gateway);
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var (_, annId, annEmail) = await PersonAsync(f, admin);
        var group = await GroupAsync(admin, new { credits = new { chat = 1 } }, annId);
        // As v5.4 left it: a team holding the group's credit, Ann's key in it.
        var team = GroupTeams.TeamId(group);
        gateway.Teams[team] = (new GatewayTeam(team, "old", 1m, null, [annEmail]), "1mo");
        await using (var scope = f.Services.CreateAsyncScope())
        {
            var key = Assert.Single(await scope.ServiceProvider.GetRequiredService<ILiteLlm>().KeysAsync(annEmail));
            await scope.ServiceProvider.GetRequiredService<ILiteLlm>().SetKeyTeamAsync(key.Token, team);
            Assert.Equal(2, await scope.ServiceProvider.GetRequiredService<GroupTeams>().SyncAsync());
        }
        // The key survives its team (the gateway deletes a team's keys with it).
        Assert.Null(gateway.KeysOf(annEmail).Single().TeamId);
        Assert.Empty(gateway.Teams);
        Assert.Contains("credit chat $1.00; API no limit; picture no limit; video no limit; speech no limit, shared", await (await admin.GetAsync("/api/admin/audit?take=1000")).Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_chargeback_report_has_each_groups_and_cost_centres_spend_per_month_as_csv()
    {
        await using var f = NewApp(new FakeGateway());
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var (_, annId, annEmail) = await PersonAsync(f, admin);
        var (_, benId, benEmail) = await PersonAsync(f, admin);
        var (_, _, cyEmail) = await PersonAsync(f, admin);
        var data = await GroupAsync(admin, new { costCentre = "CC-42", credits = new { chat = 60, api = 40 } }, annId, benId);
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
