using System.Net;
using System.Text.Json;
using Llm.Api.Panels;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>Panels: roles and assistants speak in turn, round by round, each reading what came before; the verdict; all into the chat.</summary>
[Collection(nameof(AppCollection))]
public sealed class PanelTests(AppFixture app)
{
    private WebApplicationFactory<Program> NewApp() =>
        app.Create(app.ConnectionStringFor("panels_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(), new Dictionary<string, string?>());

    [Fact]
    public async Task A_panel_speaks_in_turns_then_the_moderator_gives_the_verdict_and_it_goes_into_the_chat()
    {
        await using var f = NewApp();
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "p" + Guid.NewGuid().ToString("N")[..10];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test", displayName = "Pat" }));
        var pat = await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!);
        var other = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var assistant = (await pat.JsonAsync(await pat.PostAsync("/api/assistants", new { name = "Release manager", instructions = "You plan releases and care about dates." }))).GetProperty("id").GetGuid();
        var chat = (await pat.JsonAsync(await pat.PostAsync("/api/chat/conversations", new { useArgus = false }))).GetProperty("id").GetGuid();

        await StatusAssert.Is(HttpStatusCode.BadRequest, await pat.PostAsync($"/api/chat/conversations/{chat}/panel", new { question = "Ship?", panelists = new[] { new { role = "The expert" } } }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await pat.PostAsync($"/api/chat/conversations/{chat}/panel", new { question = "Ship?", panelists = new[] { new { role = "The expert" }, new { role = "The wizard" } } }));
        await StatusAssert.Is(HttpStatusCode.NotFound, await other.PostAsync($"/api/chat/conversations/{chat}/panel", new { question = "Ship?", panelists = new[] { new { role = "The expert" }, new { role = "The critic" } } }));

        var started = await pat.JsonAsync(await pat.PostAsync($"/api/chat/conversations/{chat}/panel", new
        {
            question = "Ship on Thursday?", rounds = 2, panelists = new object[] { new { role = "The expert" }, new { role = "The critic" }, new { assistantId = assistant } },
        }));
        var id = started.GetProperty("id").GetGuid();
        await f.Services.GetRequiredService<PanelRuns>().Finished(id).WaitAsync(TimeSpan.FromSeconds(30));
        var panel = await pat.JsonAsync(await pat.GetAsync($"/api/panels/{id}"));
        Assert.Equal("done", panel.GetProperty("status").GetString());
        var turns = panel.GetProperty("turns").EnumerateArray().Select(t => (t.GetProperty("round").GetInt32(), t.GetProperty("speaker").GetString())).ToList();
        Assert.Equal([(1, "The expert"), (1, "The critic"), (1, "Release manager"), (2, "The expert"), (2, "The critic"), (2, "Release manager")], turns);
        Assert.StartsWith("Answer to:", panel.GetProperty("verdict").GetString(), StringComparison.Ordinal);

        // Each heard the others: the second speaker read the first; the assistant spoke with its own instructions.
        var requests = app.Model.Requests.Where(r => r.Body["metadata"]?["tags"]?[0]?.GetValue<string>() == "panel").Select(r => r.Body["messages"]!.AsArray()).ToList();
        Assert.Contains(requests, m => m[0]!["content"]!.GetValue<string>().Contains("You are The critic", StringComparison.Ordinal) && m[1]!["content"]!.GetValue<string>().Contains("The expert (round 1):", StringComparison.Ordinal));
        Assert.Contains(requests, m => m[0]!["content"]!.GetValue<string>().Contains("You are Release manager: You plan releases", StringComparison.Ordinal));

        // In the chat: the question, then the discussion with its verdict.
        var messages = (await pat.JsonAsync(await pat.GetAsync($"/api/chat/conversations/{chat}"))).GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal("Panel: Ship on Thursday?", messages[^2].GetProperty("content").GetString());
        var answer = messages[^1].GetProperty("content").GetString()!;
        Assert.Contains("**A panel of The expert, The critic, Release manager** discussed it.", answer, StringComparison.Ordinal);
        Assert.Contains("### Round 2", answer, StringComparison.Ordinal);
        Assert.Contains("## The verdict", answer, StringComparison.Ordinal);
        // The chat's panels: this one, done, with how many turns it had; nobody else's.
        var listed = (await pat.JsonAsync(await pat.GetAsync($"/api/chat/conversations/{chat}/panels"))).EnumerateArray().Single();
        Assert.Equal((id, "done", 6), (listed.GetProperty("id").GetGuid(), listed.GetProperty("status").GetString(), listed.GetProperty("turns").GetInt32()));
        Assert.Empty((await other.JsonAsync(await other.GetAsync($"/api/chat/conversations/{chat}/panels"))).EnumerateArray());
        // A chat made for it is named after its question.
        Assert.Equal("Ship on Thursday?", (await pat.JsonAsync(await pat.GetAsync($"/api/chat/conversations/{chat}"))).GetProperty("title").GetString());
        await StatusAssert.Is(HttpStatusCode.NotFound, await other.GetAsync($"/api/panels/{id}"));
    }
}
