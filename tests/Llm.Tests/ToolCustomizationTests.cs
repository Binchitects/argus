using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Llm.Tests;

/// <summary>
/// Tools as the admin makes them: functions off or asking first, fixed and default arguments, words to the model, calls a day,
/// per group; toolsets; tools made without code (an HTTP request, a prompt); and how each was used.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class ToolCustomizationTests(AppFixture app)
{
    private WebApplicationFactory<Program> NewApp() =>
        app.Create(app.ConnectionStringFor("tool_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(), new Dictionary<string, string?> { ["Auth:DataKey"] = "a-data-key-for-tool-tests" });

    private static async Task<(TestBrowser Admin, TestBrowser Person, Guid Id)> PeopleAsync(WebApplicationFactory<Program> f)
    {
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "tc" + Guid.NewGuid().ToString("N")[..8];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        return (admin, await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!), made.GetProperty("id").GetGuid());
    }

    private static async Task<Guid> ChatAsync(TestBrowser b, params string[] tools) =>
        (await b.JsonAsync(await b.PostAsync("/api/chat/conversations", tools.Length > 0 ? new { tools } : new { }))).GetProperty("id").GetGuid();

    private static async Task<JsonElement> ToolResultAsync(TestBrowser b, Guid chat, string text)
    {
        var res = await b.PostAsync($"/api/chat/conversations/{chat}/messages", new { content = text });
        return (await res.Content.ReadAsStringAsync()).Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.StartsWith("data: ", StringComparison.Ordinal)).Select(l => JsonDocument.Parse(l[6..]).RootElement)
            .First(e => e.GetProperty("type").GetString() == "tool_result");
    }

    [Fact]
    public async Task A_customized_tool_offers_what_the_admin_left_fixes_arguments_says_its_words_and_counts_its_calls()
    {
        await using var f = NewApp();
        var (admin, b, _) = await PeopleAsync(f);
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.Http.PutAsJsonAsync(new Uri("/api/admin/tools/time/customize", UriKind.Relative), new
        {
            title = "Clock", instructions = "Always say the time zone.", disabledFunctions = new[] { "days_between" },
        }));
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.Http.PutAsJsonAsync(new Uri("/api/admin/tools/calculator/customize", UriKind.Relative), new
        {
            functions = new { calculate = new { description = "Sums for the finance team.", @fixed = new { expression = "6*7" } } }, callsPerDay = 2,
        }));
        var shown = await admin.JsonAsync(await admin.GetAsync("/api/admin/tools/time/customize"));
        Assert.Equal(["current_time", "days_between"], shown.GetProperty("functions").EnumerateArray().Select(x => x.GetProperty("name").GetString()));

        // People see its own name; the model is offered only what is left, as the admin described it.
        var config = await b.JsonAsync(await b.GetAsync("/api/chat/config"));
        Assert.Contains(config.GetProperty("tools").EnumerateArray(), t => t.GetProperty("id").GetString() == "time" && t.GetProperty("title").GetString() == "Clock");
        var chat = await ChatAsync(b, "time", "calculator");
        var result = await ToolResultAsync(b, chat, """Sum [call calculate {"expression":"1+1"}]""");
        Assert.Contains("42", result.GetProperty("text").GetString(), StringComparison.Ordinal);
        var asked = app.Model.Requests.Last(r => r.Body["tools"] is not null).Body;
        var offered = asked["tools"]!.AsArray().Select(t => t!["function"]!["name"]!.GetValue<string>()).ToList();
        Assert.Contains("current_time", offered);
        Assert.DoesNotContain("days_between", offered);
        var calculate = asked["tools"]!.AsArray().Single(t => t!["function"]!["name"]!.GetValue<string>() == "calculate")!["function"]!;
        Assert.Equal("Sums for the finance team.", calculate["description"]!.GetValue<string>());
        Assert.Null(calculate["parameters"]!["properties"]!["expression"]);
        Assert.Contains("Always say the time zone.", asked["messages"]![0]!["content"]!.GetValue<string>(), StringComparison.Ordinal);

        // Two calls a day: the third is refused, and the model told to go on without it.
        await ToolResultAsync(b, chat, """Again [call calculate {"expression":"2+2"}]""");
        var third = await ToolResultAsync(b, chat, """Once more [call calculate {"expression":"3+3"}]""");
        Assert.Contains("2 times today", third.GetProperty("text").GetString(), StringComparison.Ordinal);

        // Its use, by function, written in the background.
        JsonElement usage = default;
        for (var i = 0; i < 50 && (usage.ValueKind != JsonValueKind.Object || usage.GetProperty("calls").GetInt32() < 2); i++)
        {
            await Task.Delay(100);
            usage = (await admin.JsonAsync(await admin.GetAsync("/api/admin/tools/usage"))).EnumerateArray().FirstOrDefault(u => u.GetProperty("toolId").GetString() == "calculator");
        }
        Assert.Equal(2, usage.GetProperty("calls").GetInt32());
        Assert.Equal("calculate", usage.GetProperty("functions")[0].GetProperty("name").GetString());
        await StatusAssert.Is(HttpStatusCode.Forbidden, await b.GetAsync("/api/admin/tools/usage"));
    }

    [Fact]
    public async Task A_groups_new_chats_start_with_its_toolset_and_its_members_see_the_toolsets_meant_for_them()
    {
        await using var f = NewApp();
        var (admin, b, id) = await PeopleAsync(f);
        var group = (await admin.JsonAsync(await admin.PostAsync("/api/admin/groups", new { name = "Finance" }))).GetProperty("id").GetGuid();
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.PostAsync($"/api/admin/groups/{group}/members", new { userIds = new[] { id } }));
        var res = await admin.PostAsync("/api/admin/tools/toolsets", new { name = "Numbers", tools = new[] { "calculator", "time" }, defaultFor = new[] { group } });
        await StatusAssert.Is(HttpStatusCode.Created, res);
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.PostAsync("/api/admin/tools/toolsets", new { name = "Nobody's", tools = new[] { "time" }, groups = new[] { Guid.NewGuid() } }));
        var others = (await admin.JsonAsync(await admin.PostAsync("/api/admin/groups", new { name = "Others" }))).GetProperty("id").GetGuid();
        await StatusAssert.Is(HttpStatusCode.Created, await admin.PostAsync("/api/admin/tools/toolsets", new { name = "Others only", tools = new[] { "time" }, groups = new[] { others } }));

        var sets = (await b.JsonAsync(await b.GetAsync("/api/chat/toolsets"))).EnumerateArray().ToList();
        Assert.Equal(["Numbers"], sets.Select(s => s.GetProperty("name").GetString()));
        Assert.True(sets[0].GetProperty("isDefault").GetBoolean());
        var chat = await b.JsonAsync(await b.PostAsync("/api/chat/conversations", new { }));
        Assert.Equal(["calculator", "time"], chat.GetProperty("tools").EnumerateArray().Select(t => t.GetString()).Order());

        // Asking first, for this group only.
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.Http.PutAsJsonAsync(new Uri("/api/admin/tools/calculator/groups", UriKind.Relative), new { groupId = group, askFirst = true }));
        var config = await b.JsonAsync(await b.GetAsync("/api/chat/config"));
        Assert.Contains(config.GetProperty("tools").EnumerateArray(), t => t.GetProperty("id").GetString() == "calculator" && t.GetProperty("askFirst").GetBoolean());
    }

    [Fact]
    public async Task Tools_made_without_code_call_an_address_the_admin_fixed_or_run_a_prompt_and_are_tried_first()
    {
        await using var f = NewApp();
        var (admin, b, _) = await PeopleAsync(f);
        var http = new
        {
            name = "list_pets", kind = "http", title = "Pets", description = "Lists the pets in the store.",
            parameters = new[] { new { name = "limit", type = "number", description = "How many", required = false } },
            method = "GET", urlTemplate = "https://pets.test/v1/pets?limit={limit}", headers = new Dictionary<string, string> { ["X-Api-Key"] = "pet-key" },
        };
        // The host is the admin's: a parameter may not choose where the request goes.
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.PostAsync("/api/admin/tools/custom", http with { urlTemplate = "https://{limit}/pets" }));
        var tried = await admin.JsonAsync(await admin.PostAsync("/api/admin/tools/custom/try", new { tool = http, arguments = new { limit = 1 } }));
        Assert.True(tried.TryGetProperty("text", out _), tried.ToString());
        Assert.Equal("""[{"name":"Rex"}]""", tried.GetProperty("text").GetString());
        var made = await admin.PostAsync("/api/admin/tools/custom", http);
        await StatusAssert.Is(HttpStatusCode.Created, made);
        var toolId = (await admin.JsonAsync(made)).GetProperty("toolId").GetString()!;
        Assert.Equal(["X-Api-Key"], (await admin.JsonAsync(await admin.GetAsync("/api/admin/tools/custom")))[0].GetProperty("headers").EnumerateArray().Select(h => h.GetString()));

        var chat = await ChatAsync(b, toolId);
        var result = await ToolResultAsync(b, chat, """Pets [call list_pets {"limit":2}]""");
        Assert.Equal("""[{"name":"Rex"},{"name":"Tom"}]""", result.GetProperty("text").GetString());
        Assert.Contains(app.Mcp.PetCalls, c => c.PathAndQuery == "/v1/pets?limit=2" && c.Key == "pet-key");

        var prompt = await admin.PostAsync("/api/admin/tools/custom", new
        {
            name = "greet", kind = "prompt", description = "Greets someone.", parameters = new[] { new { name = "who", type = "string", description = "Whom", required = true } },
            model = "Qwen3.8-Flash-Next", promptTemplate = "Greet {who}",
        });
        await StatusAssert.Is(HttpStatusCode.Created, prompt);
        var greet = await ChatAsync(b, (await admin.JsonAsync(prompt)).GetProperty("toolId").GetString()!);
        Assert.Equal("Answer to: Greet Ann", (await ToolResultAsync(b, greet, """Hi [call greet {"who":"Ann"}]""")).GetProperty("text").GetString());
    }
}
