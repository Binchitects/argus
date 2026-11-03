using System.Text.Json.Nodes;

namespace CodeArena.Tests;

/// <summary>Alone: it shortens how often a turn renews its hold on the chat, for every sync in this process.</summary>
[CollectionDefinition(nameof(SyncWakeCollection), DisableParallelization = true)]
public sealed class SyncWakeCollection;

/// <summary>
/// The sends wake for each message after a quiet stretch too: a turn whose model said nothing past a renewal of its hold,
/// or Arena out of reach a while. What the session adds next reaches Arena at once, and the chat is let go.
/// </summary>
[Collection(nameof(SyncWakeCollection))]
public sealed class SyncWakeTests : IDisposable
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);
    /// <summary>At once: far less than a renewal or a retry would wait for.</summary>
    private static readonly TimeSpan Soon = TimeSpan.FromSeconds(5);
    private readonly TimeSpan _renew = ChatSync.TurnRenew;
    private readonly FakeGateway _gateway = new();
    private readonly FakeMcp _mcp = new();
    private readonly string _dir = Directory.CreateTempSubdirectory("sync-wake-").FullName;

    public SyncWakeTests() => _gateway.Answer = req => Reply.Say($"answer to {FakeGateway.Last(req)}");

    public void Dispose()
    {
        ChatSync.TurnRenew = _renew;
        _gateway.Dispose();
        _mcp.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static string Content(JsonObject m) => m["content"]!.ToString();
    private static JsonObject Said(string role, string content) => new() { ["role"] = role, ["content"] = content };

    private static async Task Until(Func<bool> condition, string what, TimeSpan? most = null)
    {
        var limit = most ?? Deadline;
        var until = DateTime.UtcNow + limit;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, $"Not within {limit.TotalSeconds:0} s: {what}.");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task After_a_turn_quiet_past_its_hold_s_renewals_the_next_turn_reaches_Arena_at_once_and_lets_the_chat_go()
    {
        ChatSync.TurnRenew = TimeSpan.FromMilliseconds(150);
        // The model thinks a while before it answers: the turn's hold is renewed several times meanwhile.
        _gateway.Answer = req =>
        {
            if (FakeGateway.Last(req) == "slow")
            {
                Thread.Sleep(ChatSync.TurnRenew * 8);
            }
            return Reply.Say($"answer to {FakeGateway.Last(req)}");
        };
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
            await Until(() => _mcp.Chats.All.SingleOrDefault()?.Messages.Count == 2, "Arena has the first turn");
            var chat = _mcp.Chats.All.Single();
            typed.Add("slow");
            await Until(() => chat.Messages.Count == 4 && chat.Turn is null, "Arena has the slow turn and the chat is let go");
            Assert.True(_mcp.Chats.Turns.Count(t => t == "hold") > 2);

            // The prompt waits, then the next turn: its question and answer reach Arena at once, and the web may write again.
            typed.Add("next");
            await Until(() => chat.Messages.Count == 6 && chat.Turn is null, "Arena has the next turn and the chat is let go", Soon);
            Assert.Equal(["first", "answer to first", "slow", "answer to slow", "next", "answer to next"], chat.Messages.Select(Content));
        }
        finally
        {
            typed.Add("/exit");
            Assert.Equal(0, await run.WaitAsync(Deadline));
        }
    }

    /// <summary>Arena out of reach while <see cref="Down"/> (connection refused), then back.</summary>
    private sealed class Unreachable(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public volatile bool Down;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Down ? throw new HttpRequestException("Connection refused") : base.SendAsync(request, ct);
    }

    [Fact]
    public async Task After_Arena_was_out_of_reach_a_while_what_the_session_adds_next_reaches_it_at_once()
    {
        var config = new Config { Url = _mcp.BaseUrl, ApiKey = FakeGateway.Key, SyncChats = true };
        var store = SessionStore.Create(_dir, "/work/shop", "model-a");
        var arena = new Unreachable(new HttpClientHandler());
        await using var sync = ChatSync.Start(config, new HttpClient(arena), store, "/work/shop", () => "model-a", (_, _) => { })!;
        store.Message(Said("user", "q1"));
        store.Message(Said("assistant", "a1"), "model-a");
        await Until(() => sync.InStep, "Arena has the first turn");
        var chat = _mcp.Chats.All.Single();

        // Arena goes away (restarting) as the session adds; it is back before the send is tried again.
        arena.Down = true;
        store.Message(Said("user", "q2"));
        await Until(() => sync.Trouble is not null, "the send fails");
        arena.Down = false;
        await Until(() => sync.InStep, "Arena has what it missed, once it is back");
        Assert.Null(sync.Trouble);

        // The session goes on: what it adds next goes at once.
        store.Message(Said("assistant", "a2"), "model-a");
        await Until(() => chat.Messages.Count == 4, "Arena has the next message", Soon);
        Assert.Equal(["q1", "a1", "q2", "a2"], chat.Messages.Select(Content));
    }
}
