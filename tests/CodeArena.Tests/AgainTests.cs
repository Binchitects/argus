using System.Net;
using System.Text.Json.Nodes;

namespace CodeArena.Tests;

/// <summary>A question asked again in the IDE, as the web chat's Answer again and Edit: back to before it, the earlier answer kept in Arena on a branch.</summary>
public sealed class AgainTests : IDisposable
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);
    private readonly FakeGateway _gateway = new();
    private readonly FakeMcp _mcp = new();

    public AgainTests() => _gateway.Answer = req => Reply.Say($"answer to {FakeGateway.Last(req)} one two three");

    public void Dispose()
    {
        _gateway.Dispose();
        _mcp.Dispose();
    }

    private static async Task Until(Func<bool> condition, string what)
    {
        var until = DateTime.UtcNow + Deadline;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, $"Not within {Deadline.TotalSeconds:0} s: {what}.");
            await Task.Delay(50);
        }
    }

    private static List<string> Said(JsonNode session) => [.. session["messages"]!.AsArray().Select(m => m!["content"]!.GetValue<string>())];

    private static List<string> Asked(JsonObject request) =>
        [.. FakeGateway.History(request).Select(m => m!["content"] is JsonValue v ? v.GetValue<string>() : "")];

    [Fact]
    public async Task Answer_again_goes_back_to_before_the_question_and_the_earlier_answer_stays_in_Arena_on_its_own_branch()
    {
        using var h = new Harness(_gateway, _mcp);
        await using var web = await WebRun.StartAsync(h);
        await (await web.SendAsync("first")).RestAsync();
        await (await web.SendAsync("second")).RestAsync();
        await Until(() => _mcp.Chats.All.SingleOrDefault()?.Messages.Count == 4, "both turns are in Arena");

        var events = await (await web.StreamAsync(HttpMethod.Post, "/api/again", new JsonObject { ["question"] = "m0" })).RestAsync();
        Assert.Contains(events, e => e["type"]!.GetValue<string>() == "question" && e["text"]!.GetValue<string>() == "first" && e["id"]!.GetValue<string>() == "m0");
        Assert.Equal(["first"], Asked(_gateway.Requests[^1]));
        Assert.Equal(["first", "answer to first one two three"], Said(await web.GetJsonAsync("/api/session")));

        // Arena keeps both: the new turn on screen, the earlier one on a branch of its own.
        await Until(() => _mcp.Chats.All.Single().Nodes.Count == 6, "the new turn is in Arena");
        Assert.Equal(["first", "answer to first one two three"], _mcp.Chats.All.Single().Messages.Select(m => m["content"]!.ToString()));
    }

    [Fact]
    public async Task An_edited_question_is_asked_with_its_new_words_and_quote_and_shorter_says_how_long_the_last_answer_was()
    {
        using var h = new Harness(_gateway, _mcp);
        await using var web = await WebRun.StartAsync(h);
        await (await web.SendAsync("first")).RestAsync();
        await (await web.SendAsync("second")).RestAsync();

        await (await web.StreamAsync(HttpMethod.Post, "/api/again", new JsonObject { ["question"] = "m2", ["text"] = "second, better", ["quote"] = "one two" })).RestAsync();
        Assert.Equal("The person quotes this part of an earlier answer:\n<quote>\none two\n</quote>\n\nsecond, better", Asked(_gateway.Requests[^1])[^1]);
        var session = await web.GetJsonAsync("/api/session");
        Assert.Equal(["first", "answer to first one two three", "second, better"], Said(session).Take(3));
        Assert.Equal("one two", session["messages"]![2]!["quote"]!.GetValue<string>());

        await (await web.StreamAsync(HttpMethod.Post, "/api/again", new JsonObject { ["question"] = "m2", ["length"] = "shorter" })).RestAsync();
        var shorter = _gateway.Requests[^1];
        // For this answer only, in Code Arena's note: the answer before had nine words.
        Assert.Matches(@"For this answer: Answer again, shorter: the last answer to this was about \d+ words; this time about \d+, keeping what matters most\.", FakeGateway.Notes(shorter)[^1]);
        // Asked as it was: its quote kept.
        Assert.StartsWith("The person quotes this part", Asked(shorter)[^1]);
        await (await web.SendAsync("third")).RestAsync();
        Assert.DoesNotContain("For this answer", FakeGateway.Notes(_gateway.Requests[^1])[^1]);
    }

    [Fact]
    public async Task Another_model_or_thinking_answers_this_once_and_the_session_keeps_its_own()
    {
        using var h = new Harness(_gateway, _mcp);
        await using var web = await WebRun.StartAsync(h);
        await (await web.SendAsync("first")).RestAsync();

        await (await web.StreamAsync(HttpMethod.Post, "/api/again", new JsonObject { ["question"] = "m0", ["model"] = "model-b", ["thinking"] = "off" })).RestAsync();
        Assert.Equal("model-b", _gateway.Requests[^1]["model"]!.GetValue<string>());
        Assert.False(_gateway.Requests[^1]["chat_template_kwargs"]!["enable_thinking"]!.GetValue<bool>());
        Assert.Equal("model-b", (await web.GetJsonAsync("/api/session"))["messages"]![1]!["model"]!.GetValue<string>());

        await (await web.SendAsync("next")).RestAsync();
        Assert.Equal("model-a", _gateway.Requests[^1]["model"]!.GetValue<string>());
        Assert.Null(_gateway.Requests[^1]["chat_template_kwargs"]);
        var state = await web.GetJsonAsync("/api/state");
        Assert.Equal(("model-a", (string?)null), (state["model"]!.GetValue<string>(), state["thinking"]?.GetValue<string>()));
    }

    [Fact]
    public async Task With_the_files_as_they_were_the_turn_s_writes_are_put_back_first()
    {
        using var h = new Harness(_gateway, _mcp, c => c["mode"] = "auto-edit");
        File.WriteAllText(Path.Combine(h.Work, "a.txt"), "before");
        var writes = 0;
        _gateway.Answer = req => FakeGateway.HasToolResults(req) && FakeGateway.History(req)[^1]!["role"]!.GetValue<string>() == "tool"
            ? Reply.Say("written")
            : ++writes == 1 ? Reply.Call(("write_file", """{"path":"a.txt","content":"after"}""")) : Reply.Say("not this time");
        await using var web = await WebRun.StartAsync(h);
        await (await web.SendAsync("change it")).RestAsync();
        Assert.Equal("after", File.ReadAllText(Path.Combine(h.Work, "a.txt")));

        var events = await (await web.StreamAsync(HttpMethod.Post, "/api/again", new JsonObject { ["question"] = "m0", ["files"] = true })).RestAsync();
        Assert.Contains(events, e => e["type"]!.GetValue<string>() == "notice" && e["text"]!.GetValue<string>().Contains("1 file put back", StringComparison.Ordinal));
        Assert.Equal("before", File.ReadAllText(Path.Combine(h.Work, "a.txt")));
        Assert.Equal(["change it", "not this time"], Said(await web.GetJsonAsync("/api/session")));
    }

    [Fact]
    public async Task Only_a_question_of_the_session_is_asked_again_and_not_while_an_answer_is_written()
    {
        using var h = new Harness(_gateway, _mcp);
        await using var web = await WebRun.StartAsync(h);
        await (await web.SendAsync("first")).RestAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await web.PostAsync("/api/again", new JsonObject { ["question"] = "first" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await web.PostAsync("/api/again", new JsonObject { ["question"] = "m0", ["text"] = " " })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await web.PostAsync("/api/again", new JsonObject { ["question"] = "m0", ["model"] = "no-such" })).StatusCode);
        var answer = await (await web.StreamAsync(HttpMethod.Post, "/api/again", new JsonObject { ["question"] = "m1" })).RestAsync();
        Assert.Contains(answer, e => e["type"]!.GetValue<string>() == "error" && e["message"]!.GetValue<string>().Contains("not in the session", StringComparison.Ordinal));
        Assert.Equal(["first", "answer to first one two three"], Said(await web.GetJsonAsync("/api/session")));

        var held = new TaskCompletionSource();
        _gateway.Answer = req =>
        {
            held.Task.Wait(TimeSpan.FromSeconds(10));
            return Reply.Say("late");
        };
        var running = await web.SendAsync("slow");
        Assert.Equal(HttpStatusCode.Conflict, (await web.PostAsync("/api/again", new JsonObject { ["question"] = "m0" })).StatusCode);
        held.SetResult();
        await running.RestAsync();
    }
}
