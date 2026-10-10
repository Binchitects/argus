using System.Net;
using System.Text.Json.Nodes;

namespace CodeArena.Tests;

/// <summary>
/// Every chat in step both ways, at once: what the web adds while the prompt or the IDE waits comes in within seconds
/// (asked cheaply, by the branch's stamp), every chat in Arena can be listed and continued here (in the session already
/// kept with it), and the sessions Arena never had are sent to it on asking.
/// </summary>
public sealed class LiveSyncTests : IDisposable
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);
    private readonly FakeGateway _gateway = new();
    private readonly FakeMcp _mcp = new();

    public LiveSyncTests()
    {
        // Asked often here, so the tests need not wait seconds for each look.
        ChatSync.WatchEvery = TimeSpan.FromMilliseconds(100);
        _gateway.Answer = req => Reply.Say($"answer to {FakeGateway.Last(req)}");
    }

    public void Dispose()
    {
        _gateway.Dispose();
        _mcp.Dispose();
    }

    private static string Content(JsonObject m) => m["content"]!.ToString();
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
    public async Task While_the_prompt_waits_what_the_web_adds_comes_in_at_once_and_the_chat_is_not_read_again_meanwhile()
    {
        using var h = new Harness(_gateway, _mcp);
        var typed = new TypedLines();
        var output = new StringWriter();
        var screen = TextWriter.Synchronized(output);
        var env = new CliEnv
        {
            In = typed, Out = screen, Err = TextWriter.Synchronized(new StringWriter()), Env = k => h.Env.GetValueOrDefault(k),
            Cwd = h.Work, Paths = h.Paths, InTerminal = true,
        };
        string Out()
        {
            lock (screen)
            {
                return output.ToString();
            }
        }
        var run = Task.Run(() => Cli.RunAsync(["chat"], env));
        try
        {
            typed.Add("first");
            await Until(() => _mcp.Chats.All.SingleOrDefault()?.Messages.Count == 2, "the first turn is in Arena");
            var chat = _mcp.Chats.All.Single();

            // Nothing changes: the looks are answered "unchanged", the chat is not read.
            var reads = _mcp.Chats.FullReads;
            await Until(() => _mcp.Chats.UnchangedReads >= 3, "the web chat is asked while the prompt waits");
            Assert.Equal(reads, _mcp.Chats.FullReads);

            // The person writes on the web: it is in the session within moments, said above the prompt.
            _mcp.Chats.Append(chat, Said("user", "asked on the web"), Said("assistant", "answered on the web"));
            await Until(() => Out().Contains("Taken in from the web chat: 2 messages", StringComparison.Ordinal), "the web's news is said");
            var file = Directory.GetFiles(h.Paths.SessionsDir, "*.jsonl").Single();
            Assert.Contains("\"from\":\"web\"", File.ReadAllText(file));

            // The next turn hears it once, and its own messages follow on in Arena.
            typed.Add("second");
            await Until(() => chat.Messages.Count == 6, "the second turn is in Arena");
            var sent = _gateway.Requests[^1]["messages"]!.AsArray().Skip(1).Select(m => m!["content"]!.ToString()).ToList();
            Assert.Equal(["first", "answer to first", "asked on the web", "answered on the web", "second"], sent);
            Assert.Equal(["first", "answer to first", "asked on the web", "answered on the web", "second", "answer to second"], chat.Messages.Select(Content));
        }
        finally
        {
            typed.Add("/exit");
            Assert.Equal(0, await run.WaitAsync(Deadline));
        }
    }

    [Fact]
    public async Task Every_chat_in_Arena_is_listed_a_page_at_a_time_and_found_by_its_words()
    {
        using var h = new Harness(_gateway, _mcp);
        _mcp.Chats.Add("The oldest one: quarterly figures", Said("user", "figures"));
        for (var i = 0; i < 230; i++)
        {
            _mcp.Chats.Add($"Chat number {i}", Said("user", $"q{i}"));
        }
        Assert.Equal(0, await h.Run("/web\n/web all\n/web quarterly figures\n/web 1\n/exit\n", "chat"));
        // The newest 30 first, then every one (over two pages), then the one found by its words, continued by its number.
        Assert.Contains("/web all lists every one", h.Out);
        Assert.Contains("231. ", h.Out);
        Assert.Contains("The oldest one: quarterly figures", h.Out);
        Assert.Contains("Continuing the chat from Arena", h.Out);
        var file = Directory.GetFiles(h.Paths.SessionsDir, "*.jsonl").Single();
        Assert.Contains(_mcp.Chats.All[0].Id, File.ReadAllText(file));
    }

    [Fact]
    public async Task A_chat_continued_again_goes_on_in_the_session_kept_with_it_in_this_folder_only()
    {
        using var h = new Harness(_gateway, _mcp);
        var web = _mcp.Chats.Add("Plan the release", Said("user", "Plan the release"), Said("assistant", "Step one: freeze."));
        Assert.Equal(0, await h.Run("", "-p", "and then?", "--web", web.Id));
        Assert.Equal(0, await h.Run("", "-p", "and after that?", "--web", web.Id));
        Assert.Single(Directory.GetFiles(h.Paths.SessionsDir, "*.jsonl"));
        var sent = _gateway.Requests[^1]["messages"]!.AsArray().Skip(1).Select(m => m!["content"]!.ToString()).ToList();
        Assert.Equal(["Plan the release", "Step one: freeze.", "and then?", "answer to and then?", "and after that?"], sent);
        Assert.Equal(6, web.Messages.Count);

        // Another folder has its own session for it, with the chat's history.
        h.Cwd = Directory.CreateDirectory(Path.Combine(h.Root, "elsewhere")).FullName;
        Assert.Equal(0, await h.Run("", "-p", "from here too", "--web", web.Id));
        Assert.Equal(2, Directory.GetFiles(h.Paths.SessionsDir, "*.jsonl").Length);
        Assert.Equal(8, web.Messages.Count);
    }

    [Fact]
    public async Task The_sessions_Arena_never_had_are_sent_to_it_on_asking_once()
    {
        using var h = new Harness(_gateway, _mcp, c => c["syncChats"] = false);
        Assert.Equal(0, await h.Run("", "-p", "kept here only"));
        Assert.Empty(_mcp.Chats.All);

        var config = JsonNode.Parse(File.ReadAllText(h.Paths.ConfigFile))!.AsObject();
        config["syncChats"] = true;
        File.WriteAllText(h.Paths.ConfigFile, config.ToJsonString());
        Assert.Equal(0, await h.Run("/sync all\n/sync all\n/exit\n", "chat"));
        var chat = Assert.Single(_mcp.Chats.All);
        Assert.Equal(["kept here only", "answer to kept here only"], chat.Messages.Select(Content));
        Assert.Equal(h.Work, chat.Place);
        Assert.Contains("Sent to Arena: 1 session.", h.Out);
        Assert.Contains("Arena has every session of this folder already.", h.Out);
    }

    [Fact]
    public async Task The_IDE_takes_in_the_web_chats_news_while_nothing_runs_lists_Arenas_chats_and_opens_one()
    {
        using var h = new Harness(_gateway, _mcp);
        await using var web = await WebRun.StartAsync(h);
        (await web.SendAsync("first")).Dispose();
        await Until(() => _mcp.Chats.All.SingleOrDefault()?.Messages.Count == 2, "the first turn is in Arena");
        var chat = _mcp.Chats.All.Single();
        var synced = (await web.GetJsonAsync("/api/state"))["synced"]!.GetValue<int>();

        // Added on the web while the IDE waits: taken in, and the state says so (the page reads the session again).
        _mcp.Chats.Append(chat, Said("user", "asked on the web"), Said("assistant", "answered on the web"));
        var until = DateTime.UtcNow + Deadline;
        JsonNode state;
        while ((state = await web.GetJsonAsync("/api/state"))["synced"]!.GetValue<int>() == synced)
        {
            Assert.True(DateTime.UtcNow < until, "The state never counted the web's news.");
            await Task.Delay(50);
        }
        Assert.False(state["busy"]!.GetValue<bool>());
        var messages = (await web.GetJsonAsync("/api/session"))["messages"]!.AsArray();
        Assert.Contains(messages, m => m!["content"]?.ToString() == "asked on the web");

        // The session is marked as kept with its chat; Arena's other chats are listed, and one opens with its history.
        var sessions = (await web.GetJsonAsync("/api/sessions")).AsArray();
        Assert.Equal(chat.Id, sessions.Single()!["chat"]!.GetValue<string>());
        var other = _mcp.Chats.Add("From the web", Said("user", "web question"), Said("assistant", "web answer"));
        var listed = (await web.GetJsonAsync("/api/sessions/web")).AsArray();
        Assert.Equal(chat.Id, listed.Single(c => c!["session"] is not null)!["id"]!.GetValue<string>());
        Assert.Contains(listed, c => c!["id"]!.GetValue<string>() == other.Id);
        var opened = await web.PostAsync("/api/sessions/web", new JsonObject { ["id"] = other.Id });
        Assert.Equal(HttpStatusCode.OK, opened.StatusCode);
        var session = JsonNode.Parse(await opened.Content.ReadAsStringAsync())!;
        Assert.Equal(["web question", "web answer"], session["messages"]!.AsArray().Select(m => m!["content"]!.ToString()));
        Assert.Equal(2, (await web.GetJsonAsync("/api/sessions")).AsArray().Count);

        // Not an id: refused.
        Assert.Equal(HttpStatusCode.BadRequest, (await web.PostAsync("/api/sessions/web", new JsonObject { ["id"] = "../x" })).StatusCode);
    }
    [Fact]
    public async Task A_chat_Arena_does_not_have_or_a_number_past_the_list_shown_opens_nothing()
    {
        using var h = new Harness(_gateway, _mcp);
        _mcp.Chats.Add("Release plan", Said("user", "plan"));
        for (var i = 0; i < 5; i++)
        {
            _mcp.Chats.Add($"Other {i}", Said("user", $"o{i}"));
        }
        var missing = Guid.NewGuid().ToString();
        Assert.Equal(0, await h.Run($"first\n/web {missing}\n/web release\n/web 4\n/exit\n", "chat"));
        Assert.Contains($"Arena has no chat {missing} of yours", h.Out);
        Assert.Contains("Release plan", h.Out);
        Assert.Contains("There is no chat 4: the list is above.", h.Out);
        Assert.DoesNotContain("Continuing the chat from Arena", h.Out);
        // Still the first session, kept with its own chat.
        Assert.Single(Directory.GetFiles(h.Paths.SessionsDir, "*.jsonl"));
        Assert.Equal(7, _mcp.Chats.All.Count);
    }

    [Fact]
    public async Task Sync_all_sends_what_Arena_lacks_the_oldest_first_and_the_sessions_keep_their_order_here()
    {
        using var h = new Harness(_gateway, _mcp, c => c["syncChats"] = false);
        Assert.Equal(0, await h.Run("", "-p", "older"));
        Assert.Equal(0, await h.Run("", "-p", "newer"));
        var files = Directory.GetFiles(h.Paths.SessionsDir, "*.jsonl").OrderBy(f => File.ReadAllText(f).Contains("\"newer\"", StringComparison.Ordinal)).ToList();
        File.SetLastWriteTimeUtc(files[0], DateTime.UtcNow.AddHours(-2));
        File.SetLastWriteTimeUtc(files[1], DateTime.UtcNow.AddHours(-1));
        var times = files.Select(File.GetLastWriteTimeUtc).ToList();

        var config = JsonNode.Parse(File.ReadAllText(h.Paths.ConfigFile))!.AsObject();
        config["syncChats"] = true;
        File.WriteAllText(h.Paths.ConfigFile, config.ToJsonString());
        // One sent in part before: Arena has its question only.
        Assert.Equal(0, await h.Run("", "-p", "partly sent"));
        var partly = _mcp.Chats.All.Single();
        partly.Messages.RemoveAt(1);
        var partlyFile = Directory.GetFiles(h.Paths.SessionsDir, "*.jsonl").Except(files).Single();
        File.AppendAllText(partlyFile, new JsonObject { ["type"] = "sync", ["conversation"] = partly.Id, ["server"] = 1, ["pushed"] = 1 }.ToJsonString() + "\n");
        File.SetLastWriteTimeUtc(partlyFile, DateTime.UtcNow.AddHours(-3));

        Assert.Equal(0, await h.Run("/sync all\n/exit\n", "chat"));
        Assert.Contains("Sent to Arena: 3 sessions.", h.Out);
        Assert.Equal(["partly sent", "answer to partly sent"], partly.Messages.Select(Content));
        // The older made first in Arena; here each keeps its place, and --continue goes on in the newest.
        Assert.Equal(["partly sent", "older", "newer"], _mcp.Chats.All.Select(c => Content(c.Messages[0])));
        Assert.Equal(times, files.Select(File.GetLastWriteTimeUtc).ToList());
        Assert.Equal(0, await h.Run("", "-p", "and next", "--continue"));
        Assert.Equal(4, _mcp.Chats.All.Single(c => Content(c.Messages[0]) == "newer").Messages.Count);
    }

    [Fact]
    public async Task While_the_web_chat_answers_it_is_said_once_and_its_messages_come_when_the_answer_ends()
    {
        using var h = new Harness(_gateway, _mcp);
        var web = _mcp.Chats.Add("Long answer", Said("user", "write a lot"));
        _mcp.Chats.Answering.Add(web.Id);
        var typed = new TypedLines();
        var output = new StringWriter();
        var screen = TextWriter.Synchronized(output);
        var env = new CliEnv
        {
            In = typed, Out = screen, Err = TextWriter.Synchronized(new StringWriter()), Env = k => h.Env.GetValueOrDefault(k),
            Cwd = h.Work, Paths = h.Paths, InTerminal = true,
        };
        string Out()
        {
            lock (screen)
            {
                return output.ToString();
            }
        }
        var run = Task.Run(() => Cli.RunAsync(["chat", "--web", web.Id], env));
        try
        {
            // The watch asks again and again while the answer is written: it is said once all the same.
            await Task.Delay(1000);
            typed.Add("/sync");
            await Until(() => Out().Contains("Kept in step with the chat", StringComparison.Ordinal), "/sync answers");
            Assert.Equal(1, Out().Split("The web chat is answering").Length - 1);

            // The answer ends: its messages come in at once.
            _mcp.Chats.Append(web, Said("assistant", "a long answer"));
            _mcp.Chats.Answering.Remove(web.Id);
            await Until(() => Out().Contains("Taken in from the web chat: 2 messages", StringComparison.Ordinal), "the web's messages come in");
        }
        finally
        {
            typed.Add("/exit");
            Assert.Equal(0, await run.WaitAsync(Deadline));
        }
    }
}
