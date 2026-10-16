using System.Text.Json.Nodes;

namespace CodeArena.Tests;

/// <summary>
/// Nothing lost on the way between a session and its chat in Arena: the thinking, model, tokens and time of each answer,
/// the tool of each result, a stop, Code Arena's own notes, the pictures and files attached on the web, and where the sync
/// stands, in the IDE too.
/// </summary>
public sealed class SyncDetailTests : IDisposable
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);
    private readonly FakeGateway _gateway = new();
    private readonly FakeMcp _mcp = new();

    public SyncDetailTests() => _gateway.Answer = req => Reply.Say($"answer to {FakeGateway.Last(req)}");

    public void Dispose()
    {
        _gateway.Dispose();
        _mcp.Dispose();
    }

    private static string? Str(JsonObject m, string key) => m[key]?.GetValue<string>();
    private static JsonObject Said(string role, string content) => new() { ["role"] = role, ["content"] = content };

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
    public async Task An_answer_goes_with_its_thinking_model_tokens_and_time_and_a_result_with_its_tool_and_the_thinking_never_goes_back_to_the_model()
    {
        using var h = new Harness(_gateway, _mcp);
        h.Write("notes.txt", "hello");
        _gateway.Answer = req => FakeGateway.HasToolResults(req)
            ? Reply.Say("It says hello.", reasoning: "The file is short.")
            : new Reply("", [("read_file", """{"path":"notes.txt"}""")], "I should read it.");
        Assert.Equal(0, await h.Run("", "-p", "what is in notes.txt?"));

        var chat = Assert.Single(_mcp.Chats.All);
        var m = chat.Messages;
        Assert.Equal(["user", "assistant", "tool", "assistant"], m.Select(x => Str(x, "role")));
        Assert.Equal("I should read it.", Str(m[1], "reasoning"));
        Assert.Equal("The file is short.", Str(m[3], "reasoning"));
        Assert.Equal("model-a", Str(m[3], "model"));
        Assert.Equal(7, m[3]["completionTokens"]!.GetValue<long>());
        Assert.True(m[3]["promptTokens"]!.GetValue<long>() > 0);
        Assert.True(DateTimeOffset.TryParse(Str(m[0], "at"), out var at) && at > DateTimeOffset.UtcNow.AddMinutes(-5));
        Assert.Equal("read_file", Str(m[2], "name"));
        // The model is never sent its thinking back: some providers refuse it.
        Assert.DoesNotContain("reasoning_content", _gateway.Requests[^1].ToJsonString(), StringComparison.Ordinal);

        // The session keeps them too, for the IDE.
        var data = SessionStore.Load(Directory.GetFiles(h.Paths.SessionsDir, "*.jsonl").Single());
        var shown = History.Messages(data).OfType<JsonObject>().ToList();
        Assert.Equal("The file is short.", Str(shown[3], "reasoning"));
        Assert.Equal("model-a", Str(shown[3], "model"));
        Assert.Equal(7, shown[3]["completionTokens"]!.GetValue<long>());
        Assert.NotEqual("", Str(shown[0], "createdAt"));
    }

    [Fact]
    public void A_stop_and_a_note_are_sent_as_such_and_what_the_web_stopped_or_failed_reads_so_here()
    {
        var stopped = new SyncEntry { Message = Said("assistant", "Half of it\n\n" + Agent.StoppedMark), Key = 3, At = DateTimeOffset.UtcNow, Model = "model-a" };
        var o = ChatSync.Out(stopped, "s1");
        Assert.Equal("Half of it", Str(o, "content"));
        Assert.Equal("stopped", Str(o, "status"));
        Assert.Equal("s1:3", Str(o, "ref"));
        var note = ChatSync.Out(new SyncEntry { Message = Said("user", "(The connection dropped…)"), Key = 4, Kind = "note" }, "s1");
        Assert.Equal("note", Str(note, "kind"));

        var web = ChatSync.Entry(new JsonObject { ["id"] = "x1", ["role"] = "assistant", ["content"] = "Half", ["status"] = "stopped", ["model"] = "m", ["promptTokens"] = 10 });
        Assert.Equal("Half\n\n" + Agent.StoppedMark, Str(web.Message, "content"));
        Assert.Equal("stopped", web.Status);
        Assert.Equal(10, web.Usage!.Prompt);
        var failed = ChatSync.Entry(new JsonObject { ["id"] = "x2", ["role"] = "assistant", ["content"] = "", ["status"] = "failed" });
        Assert.Equal("(no answer)", Str(failed.Message, "content"));
        var noted = ChatSync.Entry(new JsonObject { ["id"] = "x3", ["role"] = "user", ["content"] = "(The connection dropped…)", ["kind"] = "note" });
        Assert.Equal("note", noted.Kind);
    }

    [Fact]
    public async Task Pictures_attached_on_the_web_come_as_pictures_to_a_model_that_sees_and_by_name_to_one_that_does_not()
    {
        using var h = new Harness(_gateway, _mcp);
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
        _mcp.Chats.Files["img-1"] = (png, "image/png");
        JsonObject Asked(string text) => new()
        {
            ["role"] = "user", ["content"] = text,
            ["attachments"] = new JsonArray(new JsonObject { ["id"] = "img-1", ["name"] = "cat.png", ["kind"] = "image", ["contentType"] = "image/png" }),
        };
        _gateway.Vision = true;
        var seen = _mcp.Chats.Add("With a picture", Asked("What is this?"), Said("assistant", "A cat."));
        Assert.Equal(0, await h.Run("", "-p", "and its colour?", "--web", seen.Id));
        var question = _gateway.Requests[^1]["messages"]![1]!.AsObject();
        var parts = question["content"]!.AsArray();
        Assert.Equal("What is this?", parts[0]!["text"]!.GetValue<string>());
        Assert.Equal("data:image/png;base64," + Convert.ToBase64String(png), parts[1]!["image_url"]!["url"]!.GetValue<string>());
        // The person continued it from here: an archived chat comes back to the list there.
        Assert.Contains(seen.Id, _mcp.Chats.Continued);

        _gateway.Vision = false;
        var blind = _mcp.Chats.Add("Another picture", Asked("And this?"), Said("assistant", "A dog."));
        Assert.Equal(0, await h.Run("", "-p", "sure?", "--web", blind.Id));
        Assert.Equal("And this?\n\n[image attached: cat.png]", _gateway.Requests[^1]["messages"]![1]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_IDE_says_where_the_sync_stands_keeps_what_was_said_while_nothing_ran_and_marks_the_sessions_not_sent()
    {
        ChatSync.WatchEvery = TimeSpan.FromMilliseconds(100);
        using var h = new Harness(_gateway, _mcp, c => c["syncChats"] = false);
        Assert.Equal(0, await h.Run("", "-p", "kept here only"));
        var config = JsonNode.Parse(File.ReadAllText(h.Paths.ConfigFile))!.AsObject();
        config["syncChats"] = true;
        File.WriteAllText(h.Paths.ConfigFile, config.ToJsonString());

        await using var web = await WebRun.StartAsync(h);
        var sessions = (await web.GetJsonAsync("/api/sessions")).AsArray().OfType<JsonObject>().ToList();
        var old = Assert.Single(sessions);
        Assert.True(old["unsent"]!.GetValue<bool>());
        Assert.Equal(2, old["waiting"]!.GetValue<int>());

        var state = await web.GetJsonAsync("/api/state");
        Assert.Null(state["sync"]!["chat"]);
        await (await web.SendAsync("first")).RestAsync();
        await Until(() => _mcp.Chats.All.Count == 1 && _mcp.Chats.All[0].Messages.Count == 2, "the turn reaches Arena");
        var chat = _mcp.Chats.All.Single();
        await Until(() => web.GetJsonAsync("/api/state").Result["sync"] is JsonObject s && s["chat"]?.GetValue<string>() == chat.Id && s["waiting"]!.GetValue<int>() == 0,
            "the state says Arena has every message");
        Assert.Equal("Pat (pat@example.test)", (await web.GetJsonAsync("/api/state"))["sync"]!["account"]?.GetValue<string>());

        // The web adds while nothing runs: taken in, and said to the page (not only in the terminal).
        _mcp.Chats.Append(chat, Said("user", "asked on the web"), Said("assistant", "answered on the web"));
        await Until(() => web.GetJsonAsync("/api/state").Result["notices"]!.AsArray().Any(n => n!["text"]!.GetValue<string>().StartsWith("Taken in from the web chat: 2 messages", StringComparison.Ordinal)),
            "the notice is kept for the page");
    }
}
