using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Llm.Api.Gateway;
using Llm.Api.Quality;
using Llm.Core.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>Feedback on answers, the quality page, arena mode (two models, blind, a vote) and its leaderboard.</summary>
[Collection(nameof(AppCollection))]
public sealed class QualityTests(AppFixture app)
{
    private const string Main = "Qwen3.8-Flash-Next";
    private const string Other = "Other-Model";

    /// <summary>An app of its own (its numbers are only this test's), serving two chat models.</summary>
    private WebApplicationFactory<Program> TwoModels(IDictionary<string, string?>? settings = null)
    {
        var gateway = new FakeGateway();
        gateway.Models.Add(new GatewayModel(Other, 32768, 4096, Vision: false, Tools: true, Thinking: true, null, null, null));
        return app.Create(app.ConnectionStringFor("quality_" + Guid.NewGuid().ToString("N")[..8]), gateway, settings);
    }

    private static async Task<(TestBrowser Browser, string Email, string Name)> PersonAsync(WebApplicationFactory<Program> f)
    {
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "q" + Guid.NewGuid().ToString("N")[..10];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test", displayName = "Quinn " + name }));
        return (await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!), $"{name}@example.test", name);
    }

    private static async Task<Guid> NewChatAsync(TestBrowser b)
    {
        var res = await b.PostAsync("/api/chat/conversations", new { useArgus = false });
        await StatusAssert.Is(HttpStatusCode.Created, res);
        return (await b.JsonAsync(res)).GetProperty("id").GetGuid();
    }

    /// <summary>Posts and reads the whole stream: the events, and the raw text as it reached the page.</summary>
    private static async Task<(List<JsonElement> Events, string Raw)> StreamAsync(TestBrowser b, string path, object body)
    {
        var res = await b.PostAsync(path, body);
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}");
        var raw = await res.Content.ReadAsStringAsync();
        return ([.. raw.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Where(l => l.StartsWith("data: ", StringComparison.Ordinal))
            .Select(l => JsonDocument.Parse(l[6..]).RootElement)], raw);
    }

    private static async Task<(JsonElement Chat, string Raw)> ChatAsync(TestBrowser b, Guid id)
    {
        var raw = await (await b.GetAsync($"/api/chat/conversations/{id}")).Content.ReadAsStringAsync();
        return (JsonDocument.Parse(raw).RootElement, raw);
    }

    private static List<JsonElement> Messages(JsonElement chat) => [.. chat.GetProperty("messages").EnumerateArray()];

    private static string Type(JsonElement e) => e.GetProperty("type").GetString()!;

    [Fact]
    public async Task A_thumb_down_with_a_reason_is_kept_changed_and_shown_per_model_on_the_quality_page()
    {
        await using var f = TwoModels();
        var (b, _, name) = await PersonAsync(f);
        var id = await NewChatAsync(b);
        await StreamAsync(b, $"/api/chat/conversations/{id}/messages", new { content = "How do I rotate logs?" });
        await StreamAsync(b, $"/api/chat/conversations/{id}/messages", new { content = "And compress them?" });
        var msgs = Messages((await ChatAsync(b, id)).Chat);
        var answers = msgs.Where(m => m.GetProperty("role").GetString() == "assistant").Select(m => m.GetProperty("id").GetGuid()).ToList();
        var question = msgs.First(m => m.GetProperty("role").GetString() == "user").GetProperty("id").GetGuid();
        var rate = (Guid message) => $"/api/chat/conversations/{id}/messages/{message}/feedback";

        // Down with a reason and words; then up (the reason goes); then down again, shared.
        var down = await b.JsonAsync(await b.Http.PutAsJsonAsync(new Uri(rate(answers[0]), UriKind.Relative), new { up = false, reason = "wrong", comment = "It made up a flag." }));
        Assert.False(down.GetProperty("up").GetBoolean());
        Assert.Equal("wrong", down.GetProperty("reason").GetString());
        var up = await b.JsonAsync(await b.Http.PutAsJsonAsync(new Uri(rate(answers[0]), UriKind.Relative), new { up = true, reason = "wrong" }));
        Assert.True(up.GetProperty("up").GetBoolean());
        Assert.Equal(JsonValueKind.Null, up.GetProperty("reason").ValueKind);
        await b.Http.PutAsJsonAsync(new Uri(rate(answers[0]), UriKind.Relative), new { up = false, reason = "too_long", comment = "Half would do.", share = true });
        await b.Http.PutAsJsonAsync(new Uri(rate(answers[1]), UriKind.Relative), new { up = true });

        // One per person per answer, shown with the chat.
        var shown = Messages((await ChatAsync(b, id)).Chat).Single(m => m.GetProperty("id").GetGuid() == answers[0]).GetProperty("feedback");
        Assert.Equal("too_long", shown.GetProperty("reason").GetString());
        Assert.True(shown.GetProperty("shared").GetBoolean());
        await using (var scope = f.Services.CreateAsyncScope())
        {
            Assert.Equal(2, scope.ServiceProvider.GetRequiredService<AppDbContext>().AnswerFeedback.Count());
        }

        // Only answers, only known reasons, only one's own chats.
        await StatusAssert.Is(HttpStatusCode.BadRequest, await b.Http.PutAsJsonAsync(new Uri(rate(question), UriKind.Relative), new { up = true }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await b.Http.PutAsJsonAsync(new Uri(rate(answers[0]), UriKind.Relative), new { up = false, reason = "boring" }));
        var (stranger, _, _) = await PersonAsync(f);
        await StatusAssert.Is(HttpStatusCode.NotFound, await stranger.Http.PutAsJsonAsync(new Uri(rate(answers[0]), UriKind.Relative), new { up = true }));

        // The quality page, per model: answers, rated, the share up, the reasons; the latest down-rated with no content.
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        await StatusAssert.Is(HttpStatusCode.Forbidden, await b.GetAsync("/api/admin/quality"));
        var q = await admin.JsonAsync(await admin.GetAsync("/api/admin/quality"));
        var model = q.GetProperty("models").EnumerateArray().Single(m => m.GetProperty("model").GetString() == Main).GetProperty("counts");
        Assert.Equal(2, model.GetProperty("answers").GetInt32());
        Assert.Equal(2, model.GetProperty("rated").GetInt32());
        Assert.Equal(0.5, model.GetProperty("upRate").GetDouble());
        Assert.Equal(1, model.GetProperty("reasons").GetProperty("too_long").GetInt32());
        var noProject = q.GetProperty("projects").EnumerateArray().Single();
        Assert.Equal(JsonValueKind.Null, noProject.GetProperty("name").ValueKind);
        Assert.Equal(2, noProject.GetProperty("counts").GetProperty("answers").GetInt32());
        var latest = q.GetProperty("latest").EnumerateArray().Single();
        Assert.Equal("How do I rotate logs?", latest.GetProperty("title").GetString());
        Assert.Equal(Main, latest.GetProperty("model").GetString());
        Assert.Equal("too_long", latest.GetProperty("reason").GetString());
        Assert.Equal("Quinn " + name, latest.GetProperty("person").GetString());
        Assert.DoesNotContain("Answer to:", latest.ToString(), StringComparison.Ordinal);

        // Shared: the admin reads the chat down to the rated answer, and the read is audited.
        var chat = await admin.JsonAsync(await admin.GetAsync($"/api/admin/quality/feedback/{latest.GetProperty("id").GetGuid()}"));
        var read = chat.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(["user", "assistant"], read.Select(m => m.GetProperty("role").GetString()));
        Assert.True(read[1].GetProperty("rated").GetBoolean());
        Assert.StartsWith("Answer to:", read[1].GetProperty("content").GetString(), StringComparison.Ordinal);
        var audit = await admin.JsonAsync(await admin.GetAsync("/api/admin/audit?take=20"));
        Assert.Contains(audit.EnumerateArray(), e => e.GetProperty("action").GetString() == "quality.read_shared" && e.GetProperty("target").GetString() == name);

        // Not shared any more: closed again. Taken back: gone.
        await b.Http.PutAsJsonAsync(new Uri(rate(answers[0]), UriKind.Relative), new { up = false, reason = "too_long" });
        await StatusAssert.Is(HttpStatusCode.NotFound, await admin.GetAsync($"/api/admin/quality/feedback/{latest.GetProperty("id").GetGuid()}"));
        Assert.Equal(JsonValueKind.Null, (await admin.JsonAsync(await admin.GetAsync("/api/admin/quality"))).GetProperty("latest")[0].GetProperty("person").ValueKind);
        await StatusAssert.Is(HttpStatusCode.NoContent, await b.Http.DeleteAsync(new Uri(rate(answers[0]), UriKind.Relative)));
        Assert.Equal(JsonValueKind.Null, Messages((await ChatAsync(b, id)).Chat).Single(m => m.GetProperty("id").GetGuid() == answers[0]).GetProperty("feedback").ValueKind);
    }

    [Fact]
    public async Task Compare_answers_with_two_models_one_after_the_other_and_the_names_stay_out_until_the_vote()
    {
        await using var f = TwoModels();
        var (b, email, _) = await PersonAsync(f);
        var id = await NewChatAsync(b);
        var (events, raw) = await StreamAsync(b, $"/api/chat/conversations/{id}/compare", new { content = "Which sort is stable?" });

        // Each model in turn, with progress, then one end.
        var types = events.Select(Type).ToList();
        Assert.Equal(["question", "title", "arena"], types.Take(3));
        var steps = events.Where(e => Type(e) == "arena").ToList();
        Assert.Equal([("a", 1), ("b", 2)], steps.Select(e => (e.GetProperty("side").GetString(), e.GetProperty("step").GetInt32())));
        Assert.Equal(types.IndexOf("done"), types.Count - 1);
        Assert.Single(types, t => t == "done");
        var assistants = events.Where(e => Type(e) == "assistant").ToList();
        Assert.Equal(["Model A", "Model B"], assistants.Select(e => e.GetProperty("model").GetString()));
        Assert.Equal(["a", "b"], assistants.Select(e => e.GetProperty("side").GetString()));
        // Blind: neither name anywhere in what reached the page.
        Assert.DoesNotContain(Main, raw, StringComparison.Ordinal);
        Assert.DoesNotContain(Other, raw, StringComparison.Ordinal);

        // Both models answered, one after the other, each the question alone.
        var asked = app.Model.Requests.Where(r => r.Body["user"]!.GetValue<string>() == email).Select(r => r.Body).ToList();
        Assert.Equal(2, asked.Count);
        var order = asked.Select(r => r["model"]!.GetValue<string>()).ToList();
        Assert.Equal([Other, Main], order.Order());
        Assert.All(asked, r => Assert.Equal(["system", "user"], r["messages"]!.AsArray().Select(m => m!["role"]!.GetValue<string>())));

        // The chat, reloaded, is blind too; it shows A's answer, and the comparison waits for a vote.
        var (chat, chatRaw) = await ChatAsync(b, id);
        Assert.DoesNotContain(Main, chatRaw, StringComparison.Ordinal);
        Assert.DoesNotContain(Other, chatRaw, StringComparison.Ordinal);
        var arena = chat.GetProperty("arenas").EnumerateArray().Single();
        Assert.Equal(JsonValueKind.Null, arena.GetProperty("vote").ValueKind);
        Assert.Equal(JsonValueKind.Null, arena.GetProperty("models").ValueKind);
        var (a, bAnswer) = (arena.GetProperty("a").GetGuid(), arena.GetProperty("b").GetGuid());
        Assert.Equal(a, chat.GetProperty("currentLeafId").GetGuid());
        Assert.Equal(["Model A", "Model B"], Messages(chat).Where(m => m.GetProperty("role").GetString() == "assistant").Select(m => m.GetProperty("model").GetString()));

        // Search too: the answers say Model A and Model B, and are not found by their model.
        var found = await b.JsonAsync(await b.GetAsync("/api/chat/search?q=Answer%20to&in=answer"));
        Assert.Equal(["Model A", "Model B"], found.EnumerateArray().Select(h => h.GetProperty("model").GetString()).Order());
        Assert.Empty((await b.JsonAsync(await b.GetAsync($"/api/chat/search?q=Answer%20to&in=answer&model={Other}"))).EnumerateArray());

        // Not someone else's, and only a known vote.
        var (stranger, _, _) = await PersonAsync(f);
        var vote = $"/api/chat/arena/{arena.GetProperty("id").GetGuid()}/vote";
        await StatusAssert.Is(HttpStatusCode.NotFound, await stranger.PostAsync(vote, new { vote = "a" }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await b.PostAsync(vote, new { vote = "maybe" }));

        // B was better: the names, and the chat goes on from B's answer.
        var voted = await b.JsonAsync(await b.PostAsync(vote, new { vote = "b" }));
        Assert.Equal(order[0], voted.GetProperty("models").GetProperty("a").GetString());
        Assert.Equal(order[1], voted.GetProperty("models").GetProperty("b").GetString());
        Assert.Equal(bAnswer, voted.GetProperty("currentLeafId").GetGuid());
        await StatusAssert.Is(HttpStatusCode.Conflict, await b.PostAsync(vote, new { vote = "a" }));
        var after = (await ChatAsync(b, id)).Chat;
        Assert.Equal(bAnswer, after.GetProperty("currentLeafId").GetGuid());
        Assert.Equal(order, Messages(after).Where(m => m.GetProperty("role").GetString() == "assistant").Select(m => m.GetProperty("model").GetString()));
        Assert.Equal(order[1], after.GetProperty("arenas")[0].GetProperty("models").GetProperty("b").GetString());

        // The vote lands on the leaderboard: B's model up 16, A's down 16.
        var board = (await b.JsonAsync(await b.GetAsync("/api/arena/leaderboard"))).GetProperty("board");
        Assert.Equal(1, board.GetProperty("votes").GetInt32());
        var rows = board.GetProperty("models").EnumerateArray().ToList();
        Assert.Equal([(order[1], 1016, 1, 0), (order[0], 984, 0, 1)],
            rows.Select(r => (r.GetProperty("model").GetString(), r.GetProperty("rating").GetInt32(), r.GetProperty("wins").GetInt32(), r.GetProperty("losses").GetInt32())));

        // The next question follows the chosen answer, with the chat's own model.
        await StreamAsync(b, $"/api/chat/conversations/{id}/messages", new { content = "Thanks" });
        var last = Messages((await ChatAsync(b, id)).Chat).Last(m => m.GetProperty("role").GetString() == "user");
        Assert.Equal(bAnswer, last.GetProperty("parentId").GetGuid());
    }

    [Fact]
    public async Task Compare_takes_two_models_the_person_may_use_and_draws_which_is_a()
    {
        await using var f = TwoModels();
        var (b, _, _) = await PersonAsync(f);
        var id = await NewChatAsync(b);
        var compare = $"/api/chat/conversations/{id}/compare";
        await StatusAssert.Is(HttpStatusCode.BadRequest, await b.PostAsync(compare, new { content = "x", models = new[] { Main, Main } }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await b.PostAsync(compare, new { content = "x", models = new[] { Main } }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await b.PostAsync(compare, new { content = "x", models = new[] { Main, "Missing" } }));

        // Chosen: those two, in either order.
        var sides = new HashSet<string>();
        for (var i = 0; i < 20 && sides.Count < 2; i++)
        {
            await StreamAsync(b, compare, new { content = "Pick one", models = new[] { Main, Other } });
            var arena = (await ChatAsync(b, id)).Chat.GetProperty("arenas").EnumerateArray().Last();
            var voted = await b.JsonAsync(await b.PostAsync($"/api/chat/arena/{arena.GetProperty("id").GetGuid()}/vote", new { vote = "tie" }));
            sides.Add(voted.GetProperty("models").GetProperty("a").GetString()!);
        }
        Assert.Equal([Other, Main], sides.Order());

        // A model kept to the admins is not the person's to compare; one left is not enough for two at random.
        await using (var scope = f.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.ModelAccess.Add(new Llm.Core.Models.ModelAccess { Model = Other, Audience = Llm.Core.Access.Audience.Admins });
            await db.SaveChangesAsync();
        }
        await StatusAssert.Is(HttpStatusCode.Forbidden, await b.PostAsync(compare, new { content = "x", models = new[] { Main, Other } }));
        var alone = await b.PostAsync(compare, new { content = "x" });
        await StatusAssert.Is(HttpStatusCode.BadRequest, alone);
        Assert.Contains($"only {Main} can", await alone.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_comparison_stopped_during_the_first_answer_never_starts_the_second_and_has_no_vote()
    {
        await using var f = TwoModels();
        var (b, email, _) = await PersonAsync(f);
        var id = await NewChatAsync(b);
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri($"/api/chat/conversations/{id}/compare", UriKind.Relative))
        {
            Content = JsonContent.Create(new { content = "Write a lot [slow]" }),
        };
        using var res = await b.Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        var stream = await res.Content.ReadAsStreamAsync();
        var buffer = new byte[4096];
        var seen = new System.Text.StringBuilder();
        while (!seen.ToString().Contains("w3 ", StringComparison.Ordinal))
        {
            seen.Append(System.Text.Encoding.UTF8.GetString(buffer, 0, await stream.ReadAsync(buffer)));
        }
        await StatusAssert.Is(HttpStatusCode.Accepted, await b.PostAsync($"/api/chat/conversations/{id}/stop", new { }));
        using var reader = new StreamReader(stream);
        var rest = seen.ToString() + await reader.ReadToEndAsync();
        Assert.Contains("\"type\":\"stopped\"", rest, StringComparison.Ordinal);
        Assert.DoesNotContain("\"step\":2", rest, StringComparison.Ordinal);

        JsonElement arena = default;
        for (var i = 0; i < 50; i++)
        {
            var (chat, _) = await ChatAsync(b, id);
            arena = chat.GetProperty("arenas")[0];
            if (!chat.GetProperty("answering").GetBoolean())
            {
                break;
            }
            await Task.Delay(100);
        }
        Assert.Equal(JsonValueKind.Null, arena.GetProperty("b").ValueKind);
        Assert.Single(app.Model.Requests, r => r.Body["user"]!.GetValue<string>() == email);
        await StatusAssert.Is(HttpStatusCode.Conflict, await b.PostAsync($"/api/chat/arena/{arena.GetProperty("id").GetGuid()}/vote", new { vote = "a" }));
    }

    [Fact]
    public async Task The_leaderboard_can_be_kept_to_the_admins()
    {
        await using var f = TwoModels(new Dictionary<string, string?> { ["Quality:PublicLeaderboard"] = "false" });
        var (b, _, _) = await PersonAsync(f);
        await StatusAssert.Is(HttpStatusCode.Forbidden, await b.GetAsync("/api/arena/leaderboard"));
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var board = await admin.JsonAsync(await admin.GetAsync("/api/arena/leaderboard"));
        Assert.False(board.GetProperty("public").GetBoolean());
        Assert.Equal(0, board.GetProperty("board").GetProperty("votes").GetInt32());
    }

    [Fact]
    public void Elo_moves_both_ratings_by_how_surprising_the_vote_was()
    {
        // Even: the winner takes 16. Then the favourite winning again takes less, and losing costs it more.
        var one = Arena.Leaderboard([("x", "y", "a")]);
        Assert.Equal([("x", 1016), ("y", 984)], one.Select(s => (s.Model, s.Rating)));
        var two = Arena.Leaderboard([("x", "y", "a"), ("y", "x", "b")]);
        Assert.Equal(1031, two[0].Rating);
        var upset = Arena.Leaderboard([("x", "y", "a"), ("x", "y", "b")]);
        Assert.Equal([("y", 1001), ("x", 999)], upset.Select(s => (s.Model, s.Rating)));
        // A tie and "both bad" are half a win each, counted apart; the win rate is wins over matches.
        var mixed = Arena.Leaderboard([("x", "y", "tie"), ("x", "z", "bad"), ("z", "y", "a")]);
        var x = mixed.Single(s => s.Model == "x");
        Assert.Equal((2, 0, 0, 2, 1, 0.0), (x.Matches, x.Wins, x.Losses, x.Ties, x.Bad, x.WinRate));
        Assert.Equal(0.5, mixed.Single(s => s.Model == "z").WinRate);
    }

    [Fact]
    public void An_arena_event_is_blind_but_the_answer_streams_as_written()
    {
        var notice = JsonNode.Parse("""{"type":"notice","text":"Qwen3.8-Flash-Next cannot see images","more":{"list":["Qwen3.8-Flash"]}}""");
        Assert.Equal("""{"type":"notice","text":"Model A cannot see images","more":{"list":["Model B"]}}""",
            Arena.Blind(notice, "Qwen3.8-Flash-Next", "Qwen3.8-Flash")!.ToJsonString());
        var content = JsonNode.Parse("""{"type":"content","text":"I am Qwen3.8-Flash-Next"}""");
        Assert.Equal("""{"type":"content","text":"I am Qwen3.8-Flash-Next"}""", Arena.Blind(content, "Qwen3.8-Flash-Next", "Qwen3.8-Flash")!.ToJsonString());
    }
}
