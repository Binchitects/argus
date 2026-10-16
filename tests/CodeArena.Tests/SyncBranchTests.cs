using System.Text.Json.Nodes;

namespace CodeArena.Tests;

/// <summary>
/// A session and its chat in Arena kept in step by Arena's message ids, not by counts: what the web edits, answers again
/// or shows instead is followed here; what this end goes back from (/rewind) goes on a branch of its own there; summaries
/// go both ways; the two never answer at once; a long chat comes a page at a time.
/// </summary>
public sealed class SyncBranchTests : IDisposable
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);
    private readonly FakeGateway _gateway = new();
    private readonly FakeMcp _mcp = new();

    public SyncBranchTests() => _gateway.Answer = req => Reply.Say($"answer to {FakeGateway.Last(req)}");

    public void Dispose()
    {
        _gateway.Dispose();
        _mcp.Dispose();
    }

    private static string Content(JsonObject m) => m["content"]!.ToString();
    private static JsonObject Said(string role, string content) => new() { ["role"] = role, ["content"] = content };

    /// <summary>What the model was asked last, after the system prompt.</summary>
    private List<string> Asked() => [.. FakeGateway.History(_gateway.Requests[^1]).Select(m => m["content"]?.ToString() ?? "")];

    private static async Task Until(Func<bool> condition, string what)
    {
        var until = DateTime.UtcNow + Deadline;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, $"Not within {Deadline.TotalSeconds:0} s: {what}.");
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task A_question_edited_on_the_web_is_followed_here_from_where_the_two_part()
    {
        using var h = new Harness(_gateway, _mcp);
        Assert.Equal(0, await h.Run("", "-p", "first"));
        Assert.Equal(0, await h.Run("", "-p", "second", "--continue"));
        var chat = Assert.Single(_mcp.Chats.All);

        // On the web, the second question is asked again otherwise: the same length, another conversation.
        _mcp.Chats.Edit(chat, 2, Said("user", "second, better put"), Said("assistant", "the web's answer"));
        Assert.Equal(0, await h.Run("", "-p", "third", "--continue"));
        Assert.Equal(["first", "answer to first", "second, better put", "the web's answer", "third"], Asked());
        Assert.Equal(["first", "answer to first", "second, better put", "the web's answer", "third", "answer to third"], chat.Messages.Select(Content));
        Assert.Contains("The web chat's conversation changed", h.Out + h.Err);

        // Read back later, the session is the web's conversation: nothing of the old branch comes back.
        Assert.Equal(0, await h.Run("", "-p", "fourth", "--continue"));
        Assert.DoesNotContain("answer to second", Asked());
        Assert.Equal(8, chat.Messages.Count);
    }

    [Fact]
    public async Task An_answer_written_again_on_the_web_replaces_the_one_here()
    {
        using var h = new Harness(_gateway, _mcp);
        Assert.Equal(0, await h.Run("", "-p", "first"));
        var chat = Assert.Single(_mcp.Chats.All);
        // Regenerated on the web: the last message is another one, the count the same.
        _mcp.Chats.Edit(chat, 1, Said("assistant", "a better answer"));
        Assert.Equal(0, await h.Run("", "-p", "second", "--continue"));
        Assert.Equal(["first", "a better answer", "second"], Asked());
        Assert.Equal(["first", "a better answer", "second", "answer to second"], chat.Messages.Select(Content));
    }

    [Fact]
    public async Task Another_branch_shown_on_the_web_is_followed_and_shown_back_again_too()
    {
        using var h = new Harness(_gateway, _mcp);
        Assert.Equal(0, await h.Run("", "-p", "first"));
        Assert.Equal(0, await h.Run("", "-p", "second", "--continue"));
        var chat = Assert.Single(_mcp.Chats.All);
        var original = chat.Leaf!;
        _mcp.Chats.Edit(chat, 2, Said("user", "other second"), Said("assistant", "other answer"));
        Assert.Equal(0, await h.Run("", "-p", "on the other", "--continue"));
        Assert.Equal(["first", "answer to first", "other second", "other answer", "on the other"], Asked());

        // The person goes back to the first branch on the web: that is the conversation here again.
        _mcp.Chats.Show(chat, original);
        Assert.Equal(0, await h.Run("", "-p", "back on the first", "--continue"));
        Assert.Equal(["first", "answer to first", "second", "answer to second", "back on the first"], Asked());
        Assert.Equal(["first", "answer to first", "second", "answer to second", "back on the first", "answer to back on the first"], chat.Messages.Select(Content));
    }

    [Fact]
    public async Task Rewind_here_goes_on_a_branch_of_its_own_in_Arena()
    {
        using var h = new Harness(_gateway, _mcp);
        var typed = new TypedLines();
        var env = new CliEnv
        {
            In = typed, Out = TextWriter.Synchronized(new StringWriter()), Err = TextWriter.Synchronized(new StringWriter()), Env = k => h.Env.GetValueOrDefault(k),
            Cwd = h.Work, Paths = h.Paths, InTerminal = true,
        };
        var run = Task.Run(() => Cli.RunAsync(["chat"], env));
        try
        {
            typed.Add("first");
            typed.Add("second");
            await Until(() => _mcp.Chats.All.SingleOrDefault()?.Messages.Count == 4, "Arena has both turns");
            typed.Add("/rewind 2 chat");
            typed.Add("instead");
            var chat = _mcp.Chats.All.Single();
            await Until(() => chat.Messages.Count == 4 && Content(chat.Messages[^1]) == "answer to instead", "the turn after the rewind reaches Arena");
        }
        finally
        {
            typed.Add("/exit");
            Assert.Equal(0, await run.WaitAsync(Deadline));
        }
        var c = _mcp.Chats.All.Single();
        Assert.Equal(["first", "answer to first", "instead"], Asked());
        // On screen there, as here; the turn gone back from stays on the web as the other branch.
        Assert.Equal(["first", "answer to first", "instead", "answer to instead"], c.Messages.Select(Content));
        Assert.Equal(6, c.Nodes.Count);
        Assert.Equal(c.Nodes[1].Id, c.Nodes.Single(n => Content(n.Message) == "second").Parent);
    }

    [Fact]
    public async Task A_compaction_here_is_the_web_chat_s_summary_and_the_web_s_summary_is_read_here()
    {
        using var h = new Harness(_gateway, _mcp);
        _gateway.Answer = req => FakeGateway.Summarizing(req)
            ? Reply.Say("SUMMARY-OF-THE-SESSION")
            : Reply.Say($"answer to {FakeGateway.Last(req)}");
        Assert.Equal(0, await h.Run("first\nsecond\n/compact\n/exit\n", "chat"));
        var chat = Assert.Single(_mcp.Chats.All);
        // Where the web keeps its own: on the last message it covers, so the web's next answer reads it instead.
        Assert.Equal("SUMMARY-OF-THE-SESSION", chat.Branch[^1].Summary);
        Assert.Equal(4, chat.Messages.Count);

        // The other way: a long web chat compacted there is read here as its summary and what follows it.
        var web = _mcp.Chats.Add("Long one", Said("user", "q1"), Said("assistant", "a1"), Said("user", "q2"), Said("assistant", "a2"), Said("user", "q3"), Said("assistant", "a3"));
        _mcp.Chats.Summarize(web, 3, "THE-WEB-SUMMARY");
        Assert.Equal(0, await h.Run("", "-p", "next", "--web", web.Id));
        var asked = Asked();
        Assert.StartsWith(Agent.SummaryHead, asked[0]);
        Assert.Contains("THE-WEB-SUMMARY", asked[0]);
        Assert.Equal([Agent.Understood, "q3", "a3", "next"], asked[1..]);
        Assert.Equal(8, web.Messages.Count);
    }

    [Fact]
    public async Task An_empty_answer_is_not_kept_and_the_counts_agree()
    {
        using var h = new Harness(_gateway, _mcp);
        _gateway.Answer = req => FakeGateway.Last(req) == "say nothing" ? Reply.Say("") : Reply.Say($"answer to {FakeGateway.Last(req)}");
        Assert.Equal(0, await h.Run("", "-p", "say nothing"));
        Assert.Contains("The model said nothing", h.Out + h.Err);
        var chat = Assert.Single(_mcp.Chats.All);
        Assert.Equal(["say nothing"], chat.Messages.Select(Content));
        Assert.Equal(0, await h.Run("", "-p", "now say something", "--continue"));
        Assert.Equal(["say nothing", "now say something", "answer to now say something"], chat.Messages.Select(Content));
        Assert.DoesNotContain("new messages", h.Out + h.Err);
        // The list counts what a read gives.
        Assert.Equal(3, (await ChatSync.ListAsync(Config.Load(h.Paths.ConfigFile), new HttpClient(), CancellationToken.None)).Single().Messages);
    }

    [Fact]
    public async Task While_a_turn_runs_here_the_chat_is_held_and_a_turn_waits_for_the_web_s_answer()
    {
        using var h = new Harness(_gateway, _mcp);
        FakeChats.Chat? chat = null;
        var heldMeanwhile = false;
        _gateway.Answer = req =>
        {
            heldMeanwhile |= chat?.Turn is not null;
            return Reply.Say($"answer to {FakeGateway.Last(req)}");
        };
        Assert.Equal(0, await h.Run("", "-p", "first"));
        chat = Assert.Single(_mcp.Chats.All);
        var before = _mcp.Chats.Turns.Count;
        Assert.Equal(0, await h.Run("", "-p", "second", "--continue"));
        // Held while the model answered, and let go once Arena had the turn's messages.
        Assert.True(heldMeanwhile);
        Assert.Equal(["hold", "end"], _mcp.Chats.Turns.Skip(before));
        Assert.Null(chat.Turn);

        // The web is answering as a turn starts here: it waits for that answer, then hears it.
        _mcp.Chats.Append(chat, Said("user", "asked on the web"));
        _mcp.Chats.Answering.Add(chat.Id);
        var webAnswer = Task.Run(async () =>
        {
            await Task.Delay(2500);
            _mcp.Chats.Append(chat, Said("assistant", "answered on the web"));
            _mcp.Chats.Answering.Remove(chat.Id);
        });
        Assert.Equal(0, await h.Run("", "-p", "third", "--continue"));
        await webAnswer;
        Assert.Contains("this turn starts when its answer ends", h.Out + h.Err);
        Assert.Equal(["first", "answer to first", "second", "answer to second", "asked on the web", "answered on the web", "third"], Asked());
        Assert.Equal(8, chat.Messages.Count);
    }

    [Fact]
    public async Task What_the_web_added_while_this_end_s_messages_were_on_their_way_comes_before_them_here_as_there()
    {
        using var h = new Harness(_gateway, _mcp);
        Assert.Equal(0, await h.Run("", "-p", "first"));
        var chat = Assert.Single(_mcp.Chats.All);
        _mcp.Chats.Status = 503;
        Assert.Equal(0, await h.Run("", "-p", "while Arena is down", "--continue"));
        _mcp.Chats.Status = 0;
        _mcp.Chats.Append(chat, Said("user", "asked on the web"), Said("assistant", "answered on the web"));

        Assert.Equal(0, await h.Run("", "-p", "now", "--continue"));
        string[] conversation = ["first", "answer to first", "asked on the web", "answered on the web", "while Arena is down", "answer to while Arena is down", "now"];
        Assert.Equal(conversation, Asked());
        Assert.Equal([.. conversation, "answer to now"], chat.Messages.Select(Content));
        Assert.Equal(8, chat.Nodes.Count);
    }

    [Fact]
    public async Task A_long_web_chat_comes_a_page_at_a_time()
    {
        using var h = new Harness(_gateway, _mcp);
        _mcp.Chats.Page = 200;
        var messages = Enumerable.Range(0, 450).Select(i => Said(i % 2 == 0 ? "user" : "assistant", $"m{i}")).ToArray();
        var web = _mcp.Chats.Add("Long", messages);
        Assert.Equal(0, await h.Run("", "-p", "next", "--web", web.Id));
        Assert.Equal([.. messages.Select(Content), "next"], Asked());
        Assert.Equal(452, web.Messages.Count);
        var pages = _mcp.Chats.Reads.Where(r => r.Given > 0).ToList();
        Assert.Equal([200, 200, 50], pages.Select(r => r.Given));
        Assert.Null(pages[0].Since);
        Assert.Equal(web.Messages[199]["id"]!.GetValue<string>(), pages[1].Since);
    }

    [Fact]
    public async Task A_session_from_before_ids_is_read_again_from_the_web_and_goes_on()
    {
        using var h = new Harness(_gateway, _mcp);
        var web = _mcp.Chats.Add("Old", Said("user", "old question"), Said("assistant", "old answer"));
        // As an older Code Arena wrote it: messages without numbers, its place in the chat by count.
        Directory.CreateDirectory(h.Paths.SessionsDir);
        var file = Path.Combine(h.Paths.SessionsDir, "20261001-000000-abcdef.jsonl");
        File.WriteAllLines(file,
        [
            new JsonObject { ["type"] = "session", ["id"] = "20261001-000000-abcdef", ["cwd"] = h.Work, ["model"] = "model-a" }.ToJsonString(),
            new JsonObject { ["type"] = "message", ["message"] = Said("user", "old question") }.ToJsonString(),
            new JsonObject { ["type"] = "message", ["message"] = Said("assistant", "old answer"), ["from"] = "web" }.ToJsonString(),
            new JsonObject { ["type"] = "sync", ["conversation"] = web.Id, ["server"] = 2, ["pushed"] = 1 }.ToJsonString(),
            new JsonObject { ["type"] = "message", ["message"] = Said("user", "not sent yet") }.ToJsonString(),
        ]);
        Assert.Equal(0, await h.Run("", "-p", "carry on", "--continue"));
        Assert.Equal(["old question", "old answer", "not sent yet", "carry on"], Asked());
        Assert.Equal(["old question", "old answer", "not sent yet", "carry on", "answer to carry on"], web.Messages.Select(Content));
    }
}
