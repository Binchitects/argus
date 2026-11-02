using System.Net;
using System.Text.Json.Nodes;

namespace CodeArena.Tests;

/// <summary>
/// After a compaction the IDE still shows the whole conversation (only what the model reads is compacted, marked where it
/// was), each message keeping its id, and the agent goes on: its next turn answers, its tools run and ask as before, after
/// the page is read again and after code-arena starts again.
/// </summary>
public sealed class CompactedHistoryTests : IDisposable
{
    private readonly FakeGateway _gateway = new() { Context = 20_000 };
    private readonly FakeMcp _mcp = new();

    public void Dispose()
    {
        _gateway.Dispose();
        _mcp.Dispose();
    }

    private static List<JsonObject> Messages(JsonNode session) => [.. session["messages"]!.AsArray().OfType<JsonObject>()];

    private static string Text(JsonObject m) => m["content"]!.GetValue<string>();

    /// <summary>The ids are the messages' places, one after the other.</summary>
    private static void InOrder(List<JsonObject> messages) =>
        Assert.Equal(Enumerable.Range(0, messages.Count).Select(i => $"m{i}"), messages.Select(m => m["id"]!.GetValue<string>()));

    [Fact]
    public async Task Compact_keeps_the_whole_conversation_on_the_page_and_the_next_turn_answers_after_a_reload_and_a_restart()
    {
        _gateway.Answer = req => FakeGateway.Summarizing(req) ? Reply.Say("SUMMARY: two questions answered.") : Reply.Say($"answer to {FakeGateway.Last(req)}");
        using var h = new Harness(_gateway, _mcp);
        await using (var web = await WebRun.StartAsync(h))
        {
            await (await web.SendAsync("first")).RestAsync();
            await (await web.SendAsync("second")).RestAsync();

            var compacted = await (await web.StreamAsync(HttpMethod.Post, "/api/compact", new JsonObject())).RestAsync();
            var reset = compacted.Single(e => e["type"]!.GetValue<string>() == "reset");
            var shown = Messages(reset);
            Assert.Equal(["first", "answer to first", "second", "answer to second"], shown.Select(Text));
            InOrder(shown);
            // Marked where it was compacted: the last message the summary covers.
            Assert.StartsWith("SUMMARY: two questions answered.", shown[3]["summary"]!.GetValue<string>(), StringComparison.Ordinal);
            Assert.All(shown.Take(3), m => Assert.Null(m["summary"]));

            // The next turn: its question and answer go on after the conversation, and the model reads the summary instead.
            var third = await (await web.SendAsync("third")).RestAsync();
            Assert.Equal("m4", third.First(e => e["type"]!.GetValue<string>() == "question")["id"]!.GetValue<string>());
            Assert.Equal("m5", third.First(e => e["type"]!.GetValue<string>() == "assistant")["id"]!.GetValue<string>());
            Assert.Equal("done", third[^1]["type"]!.GetValue<string>());
            var asked = FakeGateway.History(_gateway.Requests[^1]).Select(m => m["content"]!.ToString()).ToList();
            Assert.StartsWith("[The conversation so far, summarized", asked[0], StringComparison.Ordinal);
            Assert.DoesNotContain("first", asked);
            Assert.Equal("third", asked[^1]);

            // Read again (the page reloaded): the whole of it, still marked.
            var session = Messages(await web.GetJsonAsync("/api/session"));
            Assert.Equal(["first", "answer to first", "second", "answer to second", "third", "answer to third"], session.Select(Text));
            InOrder(session);
            Assert.NotNull(session[3]["summary"]);
            Assert.False((await web.GetJsonAsync("/api/session"))["busy"]!.GetValue<bool>());

            // A question after the compaction is asked again by its id; one it covers is not (the model reads its summary).
            var covered = await (await web.StreamAsync(HttpMethod.Post, "/api/again", new JsonObject { ["question"] = "m0" })).RestAsync();
            Assert.Contains("compacted", covered.Single(e => e["type"]!.GetValue<string>() == "error")["message"]!.GetValue<string>(), StringComparison.Ordinal);
            var again = await (await web.StreamAsync(HttpMethod.Post, "/api/again", new JsonObject { ["question"] = "m4", ["text"] = "third, again" })).RestAsync();
            Assert.Equal("m4", again.First(e => e["type"]!.GetValue<string>() == "question")["id"]!.GetValue<string>());
            Assert.Equal("third, again", FakeGateway.Last(_gateway.Requests[^1]));
            session = Messages(await web.GetJsonAsync("/api/session"));
            Assert.Equal(["first", "answer to first", "second", "answer to second", "third, again", "answer to third, again"], session.Select(Text));
            Assert.NotNull(session[3]["summary"]);
        }

        // code-arena again, on the same session: the same, and it answers.
        await using (var web = await WebRun.StartAsync(h, "--continue"))
        {
            var session = Messages(await web.GetJsonAsync("/api/session"));
            Assert.Equal(6, session.Count);
            Assert.NotNull(session[3]["summary"]);
            var fourth = await (await web.SendAsync("fourth")).RestAsync();
            Assert.Equal("m6", fourth.First(e => e["type"]!.GetValue<string>() == "question")["id"]!.GetValue<string>());
            Assert.Equal("answer to fourth", string.Concat(fourth.Where(e => e["type"]!.GetValue<string>() == "content").Select(e => e["text"]!.GetValue<string>())));
            Assert.Equal(8, Messages(await web.GetJsonAsync("/api/session")).Count);
        }
    }

    [Fact]
    public async Task A_long_turn_compacted_on_its_way_stays_whole_on_the_page_and_its_next_tool_still_asks_and_runs()
    {
        const int Files = 8;
        var steps = 0;
        _gateway.Answer = req =>
        {
            if (FakeGateway.Summarizing(req))
            {
                return Reply.Say("SUMMARY: wrote the files so far.");
            }
            var n = Interlocked.Increment(ref steps);
            return n <= Files ? Reply.Call(("write_file", new JsonObject { ["path"] = $"f{n}.txt", ["content"] = new string('x', 8000) }.ToJsonString()))
                : n == Files + 1 ? Reply.Call(("run_shell", """{"command":"ls f1.txt"}"""))
                : Reply.Say("done");
        };
        using var h = new Harness(_gateway, _mcp);
        await using var web = await WebRun.StartAsync(h, "--mode", "auto-edit");

        using var stream = await web.SendAsync("write the files");
        var question = await stream.UntilAsync("question");
        Assert.Equal("m0", question["id"]!.GetValue<string>());
        var reset = await stream.UntilAsync("reset");
        // The page keeps the person's question as they wrote it, and every step: the summary only marks where it was compacted.
        var shown = Messages(reset);
        Assert.Equal("write the files", Text(shown[0]));
        Assert.Null(shown[0]["summary"]);
        Assert.Contains(shown, m => m["summary"]?.GetValue<string>().StartsWith("SUMMARY", StringComparison.Ordinal) == true);
        InOrder(shown);
        Assert.DoesNotContain(shown, m => Text(m).StartsWith("[The conversation so far", StringComparison.Ordinal));

        // The command after the compaction asks, as any does in auto-edit; allowed, it runs and the turn ends.
        var approval = await stream.UntilAsync("approval");
        Assert.Equal("run_shell", approval["name"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NoContent, (await web.PostAsync("/api/approvals", new JsonObject { ["id"] = approval["id"]!.DeepClone(), ["answer"] = "allow" })).StatusCode);
        var rest = await stream.RestAsync();
        Assert.Contains(rest, e => e["type"]!.GetValue<string>() == "tool_result" && e["name"]!.GetValue<string>() == "run_shell");
        Assert.Equal("done", rest[^1]["type"]!.GetValue<string>());

        var session = Messages(await web.GetJsonAsync("/api/session"));
        InOrder(session);
        Assert.Equal(1, session.Count(m => m["role"]!.GetValue<string>() == "user"));
        Assert.Equal(Files + 1, session.Count(m => m["role"]!.GetValue<string>() == "tool"));
        Assert.Equal("done", Text(session[^1]));
        // Each answer says what filled its request, for the context gauge: by kind, and its biggest messages by their ids.
        var context = session[^1]["context"]!;
        Assert.True(context["toolResults"]!.GetValue<long>() > 0);
        Assert.True(context["system"]!.GetValue<long>() > 0);
        Assert.All(context["biggest"]!.AsArray(), b => Assert.Contains(session, m => m["id"]!.GetValue<string>() == b!["id"]!.GetValue<string>()));
    }
}
