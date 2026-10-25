using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Llm.Api.Chat;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>
/// Memory by relevance: the memories that matter to a question go with it (by meaning with the embedder, by words
/// without it), the answer says which it read, a memory said again in other words is the same one, and the company's
/// memories are kept by those who keep the company's knowledge and read by everyone's answers.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class MemoryRecallTests(AppFixture app)
{
    private WebApplicationFactory<Program> NewApp(bool embedder = true) =>
        app.Create(app.ConnectionStringFor("recall_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(),
            new Dictionary<string, string?> { ["Modules:embed"] = embedder ? "true" : "false" });

    private static async Task<(TestBrowser Browser, string Email)> PersonAsync(WebApplicationFactory<Program> f, TestBrowser admin)
    {
        var name = "r" + Guid.NewGuid().ToString("N")[..10];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        return (await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!), $"{name}@example.test");
    }

    private static async Task<List<JsonElement>> AskAsync(TestBrowser b, string text, object? chat = null)
    {
        var made = await b.PostAsync("/api/chat/conversations", chat ?? new { });
        var id = (await b.JsonAsync(made)).GetProperty("id").GetGuid();
        var res = await b.PostAsync($"/api/chat/conversations/{id}/messages", new { content = text });
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}");
        return [.. (await res.Content.ReadAsStringAsync()).Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.StartsWith("data: ", StringComparison.Ordinal)).Select(l => JsonDocument.Parse(l[6..]).RootElement)];
    }

    private static async Task<JsonElement> KeepAsync(TestBrowser b, object body)
    {
        var res = await b.PostAsync("/api/account/memories", body);
        await StatusAssert.Is(HttpStatusCode.Created, res);
        return await b.JsonAsync(res);
    }

    private static async Task<List<JsonElement>> MemoriesAsync(TestBrowser b) =>
        [.. (await b.JsonAsync(await b.GetAsync("/api/account/memories"))).GetProperty("memories").EnumerateArray()];

    /// <summary>The system prompt and the last question of the person's newest request to the model.</summary>
    private (string System, string Question) SentFor(string email)
    {
        var messages = app.Model.Requests.Last(r => r.Body["user"]!.GetValue<string>() == email).Body["messages"]!.AsArray();
        var question = messages.Last(m => m!["role"]!.GetValue<string>() == "user")!["content"]!;
        return (messages[0]!["content"]!.GetValue<string>(), question is System.Text.Json.Nodes.JsonValue v ? v.GetValue<string>() : question.ToJsonString());
    }

    /// <summary>Forty memories of nothing in particular, kept after the one that matters: the prompt's block has no room for it.</summary>
    private static async Task FillAsync(TestBrowser b)
    {
        for (var i = 0; i < 40; i++)
        {
            // Words of their own each: alike in meaning, they would be one memory.
            await KeepAsync(b, new { text = $"Filler memory {i}: k{i}alpha k{i}beta k{i}gamma k{i}delta, about gardening and weather." });
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_memory_that_matters_goes_with_the_question_and_the_answer_says_it_read_it(bool embedder)
    {
        await using var f = NewApp(embedder);
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var (b, email) = await PersonAsync(f, admin);
        var podman = await KeepAsync(b, new { text = "Deploys the payment service with Podman on the build server.", kind = "howto" });
        await KeepAsync(b, new { text = "Prefers answers in Persian." });
        await FillAsync(b);

        var events = await AskAsync(b, "How do I deploy the payment service?");
        var (system, question) = SentFor(email);
        Assert.DoesNotContain("Podman", system, StringComparison.Ordinal);
        Assert.Contains(Memories.RecallHeading, question, StringComparison.Ordinal);
        Assert.Contains("Deploys the payment service with Podman on the build server. (how to)", question, StringComparison.Ordinal);
        Assert.DoesNotContain("Persian", question, StringComparison.Ordinal);
        var read = events.Single(e => e.GetProperty("type").GetString() == "memories").GetProperty("memories");
        Assert.Equal(podman.GetProperty("id").GetString(), Assert.Single(read.EnumerateArray()).GetProperty("id").GetString());
        // Kept with the answer, and counted.
        var chat = (await b.JsonAsync(await b.GetAsync("/api/chat/conversations"))).EnumerateArray().First().GetProperty("id").GetGuid();
        var answer = (await b.JsonAsync(await b.GetAsync($"/api/chat/conversations/{chat}"))).GetProperty("messages").EnumerateArray().Last(m => m.GetProperty("role").GetString() == "assistant");
        Assert.Equal("howto", answer.GetProperty("details").GetProperty("memories")[0].GetProperty("kind").GetString());
        var kept = (await MemoriesAsync(b)).Single(m => m.GetProperty("id").GetString() == podman.GetProperty("id").GetString());
        Assert.Equal(1, kept.GetProperty("uses").GetInt32());
    }

    [Fact]
    public async Task A_memory_said_again_in_other_words_is_the_same_one_in_the_newer_words()
    {
        await using var f = NewApp();
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var (b, _) = await PersonAsync(f, admin);
        var first = await KeepAsync(b, new { text = "Deploys with Podman." });
        var again = await KeepAsync(b, new { text = "I deploy with podman", kind = "howto" });
        Assert.Equal(first.GetProperty("id").GetString(), again.GetProperty("id").GetString());
        var only = Assert.Single(await MemoriesAsync(b));
        Assert.Equal(("I deploy with podman", "howto"), (only.GetProperty("text").GetString(), only.GetProperty("kind").GetString()));
        // Something else is another memory.
        await KeepAsync(b, new { text = "Deploys the docs with Netlify." });
        Assert.Equal(2, (await MemoriesAsync(b)).Count);
    }

    [Fact]
    public async Task The_companys_memories_are_kept_by_its_knowledge_keepers_and_read_by_everyones_answers()
    {
        await using var f = NewApp();
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var (ana, anaEmail) = await PersonAsync(f, admin);
        await StatusAssert.Is(HttpStatusCode.Forbidden, await ana.PostAsync("/api/account/memories", new { text = "Our fiscal year starts in April.", scope = "company" }));
        var company = await KeepAsync(admin, new { text = "Our fiscal year starts in April.", scope = "company", pinned = true });
        Assert.Equal("company", company.GetProperty("scope").GetString());

        await AskAsync(ana, "When does the quarter end?");
        Assert.Contains("- Our fiscal year starts in April. (the company's)", SentFor(anaEmail).System, StringComparison.Ordinal);
        var listed = Assert.Single(await MemoriesAsync(ana));
        Assert.Equal((false, false), (listed.GetProperty("mine").GetBoolean(), listed.GetProperty("canEdit").GetBoolean()));
        var id = company.GetProperty("id").GetString();
        await StatusAssert.Is(HttpStatusCode.NotFound, await ana.Http.PutAsJsonAsync(new Uri($"/api/account/memories/{id}", UriKind.Relative), new { text = "Ours starts in May." }));
        await StatusAssert.Is(HttpStatusCode.NotFound, await ana.Http.DeleteAsync(new Uri($"/api/account/memories/{id}", UriKind.Relative)));
        // Ana's "delete all" leaves the company's.
        await StatusAssert.Is(HttpStatusCode.NoContent, await ana.Http.DeleteAsync(new Uri("/api/account/memories", UriKind.Relative)));
        Assert.Single(await MemoriesAsync(ana));
        // A scope or kind that is none of them is refused; a project's says its project.
        await StatusAssert.Is(HttpStatusCode.BadRequest, await ana.PostAsync("/api/account/memories", new { text = "x", scope = "galaxy" }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await ana.PostAsync("/api/account/memories", new { text = "x", kind = "rumour" }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await ana.PostAsync("/api/account/memories", new { text = "x", scope = "project" }));
    }

    [Fact]
    public async Task The_model_remembers_with_a_kind_and_recalls_what_is_not_given()
    {
        await using var f = NewApp();
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var (b, email) = await PersonAsync(f, admin);
        var events = await AskAsync(b, """Remember: run the tests with tools/dn before a commit. [call remember {"memory":"Runs the tests with tools/dn before a commit.","kind":"howto"}]""",
            new { tools = new[] { "memory" } });
        var card = events.Single(e => e.GetProperty("type").GetString() == "tool_result").GetProperty("details").GetProperty("memory");
        Assert.Equal(("kept", "howto"), (card.GetProperty("state").GetString(), card.GetProperty("kind").GetString()));
        Assert.Equal("howto", Assert.Single(await MemoriesAsync(b)).GetProperty("kind").GetString());

        await FillAsync(b);
        events = await AskAsync(b, """What do I do before a commit? [call recall {"query":"tests before a commit"}]""", new { tools = new[] { "memory" } });
        var found = events.Single(e => e.GetProperty("type").GetString() == "tool_result").GetProperty("text").GetString();
        Assert.Equal("- Runs the tests with tools/dn before a commit. (how to)", found);
        Assert.Contains("recall", app.Model.Requests.Last(r => r.Body["user"]!.GetValue<string>() == email).Body["tools"]!.ToJsonString(), StringComparison.Ordinal);
    }


    [Fact]
    public async Task Memories_kept_while_the_embedder_was_away_are_given_their_meaning_later()
    {
        await using var f = NewApp(embedder: false);
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var (b, _) = await PersonAsync(f, admin);
        var kept = await KeepAsync(b, new { text = "Deploys with Podman." });
        await using (var on = f.WithWebHostBuilder(w => w.UseSetting("Modules:embed", "true")))
        {
            var upkeep = on.Services.GetRequiredService<MemoryUpkeep>();
            Assert.True(await upkeep.RunAsync(CancellationToken.None) >= 1);
            using var scope = on.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<Llm.Core.Data.AppDbContext>();
            var memory = await db.Memories.FindAsync(Guid.Parse(kept.GetProperty("id").GetString()!));
            Assert.Equal(FakeEmbedder.Dimensions, memory!.Embedding!.Length);
            Assert.Equal(0, await upkeep.RunAsync(CancellationToken.None));
        }
    }
}
