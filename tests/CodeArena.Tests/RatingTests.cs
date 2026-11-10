using System.Net;
using System.Text.Json.Nodes;

namespace CodeArena.Tests;

/// <summary>Thumbs on an answer in the IDE: kept by Arena on the session's chat (as the web chat's are), and beside the answer here.</summary>
public sealed class RatingTests : IDisposable
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);
    private readonly FakeGateway _gateway = new();
    private readonly FakeMcp _mcp = new();

    public void Dispose()
    {
        _gateway.Dispose();
        _mcp.Dispose();
    }

    private static async Task Until(Func<Task<bool>> condition, string what)
    {
        var until = DateTime.UtcNow + Deadline;
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < until, $"Not within {Deadline.TotalSeconds:0} s: {what}.");
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task An_answer_is_rated_in_arena_on_its_synced_message_shown_rated_here_and_taken_back()
    {
        _gateway.Answer = _ => Reply.Say("the answer");
        using var h = new Harness(_gateway, _mcp);
        await using var web = await WebRun.StartAsync(h);

        // Not an answer: refused.
        Assert.Equal(HttpStatusCode.BadRequest, (await web.PostAsync("/api/answers/rate", new JsonObject { ["message"] = "m0", ["up"] = true })).StatusCode);
        await (await web.SendAsync("a question")).RestAsync();
        var answerId = "m1";
        // Once Arena has the answer (its id known here), it can be rated.
        await Until(async () => _mcp.Chats.All.SingleOrDefault()?.Nodes.Any(n => n.Message["role"]?.ToString() == "assistant") == true
            && (await web.PostAsync("/api/answers/rate", new JsonObject { ["message"] = answerId, ["up"] = false, ["reason"] = "wrong", ["comment"] = "It missed the test." })).StatusCode == HttpStatusCode.OK,
            "the answer is rated");
        var chat = _mcp.Chats.All.Single();
        var (to, message, rating) = _mcp.Chats.RatingsSent()[^1];
        Assert.Equal(chat.Id, to);
        Assert.Equal(chat.Nodes.Last(n => n.Message["role"]?.ToString() == "assistant").Id, message);
        Assert.Equal((false, "wrong", "It missed the test."), (rating!["up"]!.GetValue<bool>(), rating["reason"]!.GetValue<string>(), rating["comment"]!.GetValue<string>()));

        // Beside the answer here, after the page is opened again.
        var shown = (await web.GetJsonAsync("/api/session"))["messages"]![1]!;
        Assert.Equal(("assistant", false, "wrong"), (shown["role"]!.GetValue<string>(), shown["feedback"]!["up"]!.GetValue<bool>(), shown["feedback"]!["reason"]!.GetValue<string>()));

        // Taken back: there, and here.
        Assert.Equal(HttpStatusCode.NoContent, (await web.PostAsync("/api/answers/rate", new JsonObject { ["message"] = answerId, ["clear"] = true })).StatusCode);
        Assert.Null(_mcp.Chats.RatingsSent()[^1].Rating);
        Assert.Null((await web.GetJsonAsync("/api/session"))["messages"]![1]!["feedback"]);
    }
}
