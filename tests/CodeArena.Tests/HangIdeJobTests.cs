using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace CodeArena.Tests;

/// <summary>
/// The IDE (Web.cs) is busy while it has a job: MessageAsync begins the job, then takes in what the web chat
/// added (ChatSync.TakeInAsync, with the server's token, not the job's Stop, and through an HttpClient with no
/// time limit), and only then starts the turn, whose try/finally ends the job. Whatever stops or fails between
/// the two leaves the job in place for good: the page's Stop finds it and cancels nothing, and every next
/// message is refused as busy (409). Each test fails, rather than hangs, when that happens.
/// </summary>
public sealed class HangIdeJobTests : IDisposable
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);
    private readonly FakeGateway _gateway = new();
    private readonly HoldingArena _arena = new();

    public void Dispose()
    {
        _arena.Release();
        _gateway.Dispose();
        _arena.Dispose();
    }

    /// <summary>The gateway answers; Arena (the chats kept in step) is the holding fake, with no MCP endpoint.</summary>
    private Harness NewHarness() => new(_gateway, mcp: null, config: c => c["url"] = _arena.BaseUrl);

    [Fact]
    public async Task Hang_when_Arena_does_not_answer_the_read_of_the_web_chat_the_IDE_stays_busy_and_Stop_does_not_free_it()
    {
        using var h = NewHarness();
        _gateway.Answer = req => Reply.Say("answer to " + FakeGateway.Last(req));
        await using var web = await WebRun.StartAsync(h);
        try
        {
            await FirstTurnSyncedAsync(web);

            // Arena stops answering reads of the chat: a connection that stalls (a VPN dropping, a proxy holding it),
            // or Arena stuck behind its proxy. Nothing ever ends the read: the HttpClient has no time limit.
            _arena.Holds = (method, path) => method == "GET" && path.StartsWith("/api/code-arena/chats/", StringComparison.Ordinal);
            var second = await TurnWithinAsync(web, "second", Deadline);
            var after = await StopAndSendAgainAsync(web);

            Assert.True(_arena.Held > 0, "Arena was never asked for the web chat's messages: the hold did not engage.");
            Assert.True(second.Answered, $"The second message was not answered in {Deadline.TotalSeconds:0} s: {second}. Arena holds {_arena.Held} read(s). {after.Report}");
            Assert.True(after.Freed, after.Report);
        }
        finally
        {
            _arena.Release();
        }
    }

    [Fact]
    public async Task Hang_when_a_push_to_Arena_never_comes_back_the_next_IDE_message_waits_for_ever_and_the_IDE_stays_busy()
    {
        using var h = NewHarness();
        _gateway.Answer = req => Reply.Say("answer to " + FakeGateway.Last(req));
        await using var web = await WebRun.StartAsync(h);
        try
        {
            // The chat is made in Arena; sending its messages never comes back. The background push holds the sync's
            // one-at-a-time gate (ChatSync._busy) while it waits, and the next message's take-in waits on that gate.
            _arena.Holds = (method, path) => method == "POST" && path.EndsWith("/messages", StringComparison.Ordinal);
            var first = await TurnWithinAsync(web, "first", Deadline);
            Assert.True(first.Answered, $"The first message was not answered: {first}.");
            await UntilAsync(() => _arena.Held > 0, "the first turn's push to reach Arena");

            var second = await TurnWithinAsync(web, "second", Deadline);
            var after = await StopAndSendAgainAsync(web);

            Assert.True(second.Answered, $"The second message was not answered in {Deadline.TotalSeconds:0} s: {second}. Arena holds {_arena.Held} push(es). {after.Report}");
            Assert.True(after.Freed, after.Report);
        }
        finally
        {
            _arena.Release();
        }
    }

    [Fact]
    public async Task Hang_when_taking_in_the_web_chat_fails_before_the_turn_the_IDE_stays_busy_for_good()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using var h = NewHarness();
        _gateway.Answer = req => Reply.Say("answer to " + FakeGateway.Last(req));
        await using var web = await WebRun.StartAsync(h);
        await FirstTurnSyncedAsync(web);

        // The person carries on in the web chat; here, the session file cannot be written for a moment (a full disk,
        // a file made read-only): writing the web's messages into it throws before the turn's try/finally.
        var chat = Assert.Single(_arena.Chats.All);
        chat.Messages.Add(new JsonObject { ["role"] = "user", ["content"] = "asked on the web" });
        chat.Messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = "answered on the web" });
        var id = (await web.GetJsonAsync("/api/session"))["id"]!.GetValue<string>();
        var file = Path.Combine(h.Paths.SessionsDir, id + ".jsonl");
        Assert.True(File.Exists(file), $"No session file at {file}.");
        File.SetUnixFileMode(file, UnixFileMode.UserRead);
        if (Writable(file))
        {
            // Run as root, who writes it all the same: nothing to show here.
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return;
        }
        var second = await TurnWithinAsync(web, "second", Deadline);
        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        // The file can be written again; the person tries again.
        var after = await StopAndSendAgainAsync(web);
        Assert.True(after.Freed, $"The second message failed ({second}), and the IDE never recovered. {after.Report}");
    }

    [Fact]
    public async Task A_question_asked_while_the_page_was_away_is_replayed_to_the_page_that_comes_back_and_answered_there()
    {
        using var h = NewHarness();
        var file = h.Write("a.txt", "one\ntwo\nthree\n");
        _gateway.Answer = req => FakeGateway.Last(req) switch
        {
            "edit it" => Reply.Call(("edit_file", """{"path":"a.txt","old_string":"two","new_string":"2"}""")),
            _ => Reply.Say("Result: " + FakeGateway.Last(req)),
        };
        await using var web = await WebRun.StartAsync(h);

        string asked;
        using (var page = await web.SendAsync("edit it"))
        {
            asked = (await page.UntilAsync("approval"))["id"]!.GetValue<string>();
        }
        // The page is reloaded: its stream is cut while the question waits.
        await Task.Delay(500);
        Assert.True((await web.GetJsonAsync("/api/state"))["busy"]!.GetValue<bool>());

        using var back = await web.StreamAsync(HttpMethod.Get, "/api/turn", null);
        var again = await back.UntilAsync("approval");
        Assert.Equal(asked, again["id"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NoContent, (await web.PostAsync("/api/approvals", new JsonObject { ["id"] = asked, ["answer"] = "allow" })).StatusCode);
        var events = await back.RestAsync().WaitAsync(Deadline);
        Assert.Contains(events, e => e["type"]!.GetValue<string>() == "done");
        Assert.Equal("one\n2\nthree\n", File.ReadAllText(file));
        Assert.False((await web.GetJsonAsync("/api/state"))["busy"]!.GetValue<bool>());
    }

    // ------------------------------------------------------------------ helpers

    private async Task FirstTurnSyncedAsync(WebRun web)
    {
        var first = await TurnWithinAsync(web, "first", Deadline);
        Assert.True(first.Answered, $"The first message was not answered: {first}.");
        await UntilAsync(() => _arena.Chats.All.Count == 1 && _arena.Chats.All[0].Messages.Count == 2, "the first turn to reach Arena");
    }

    private static async Task UntilAsync(Func<bool> condition, string what)
    {
        for (var waited = 0; !condition(); waited += 50)
        {
            if (waited > Deadline.TotalMilliseconds)
            {
                throw new Xunit.Sdk.XunitException($"Timed out waiting for {what}.");
            }
            await Task.Delay(50);
        }
    }

    private static bool Writable(string file)
    {
        try
        {
            using var _ = new FileStream(file, FileMode.Append, FileAccess.Write);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>A message's answer as the page gets it, for at most <paramref name="time"/>: its status, its events' types, or what refused it.</summary>
    private sealed record Turn(HttpStatusCode? Status, List<string> Events, string? Refusal, bool TimedOut)
    {
        public bool Answered => Status == HttpStatusCode.OK && Events.Contains("content") && Events.Contains("done");

        public override string ToString() =>
            Status is null ? "no answer at all, not even the event stream's start"
            : Status != HttpStatusCode.OK ? $"HTTP {(int)Status.Value} {Refusal}"
            : $"events [{string.Join(", ", Events)}]{(TimedOut ? " and then nothing" : "")}";
    }

    private static async Task<Turn> TurnWithinAsync(WebRun web, string text, TimeSpan time)
    {
        using var timer = new CancellationTokenSource(time);
        var events = new List<string>();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/messages")
        {
            Content = new StringContent(new JsonObject { ["text"] = text }.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        HttpResponseMessage response;
        try
        {
            response = await web.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timer.Token);
        }
        catch (OperationCanceledException)
        {
            return new Turn(null, events, null, TimedOut: true);
        }
        using (response)
        {
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return new Turn(response.StatusCode, events, await response.Content.ReadAsStringAsync(), TimedOut: false);
            }
            try
            {
                using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timer.Token));
                while (await reader.ReadLineAsync(timer.Token) is { } line)
                {
                    if (line.StartsWith("data: ", StringComparison.Ordinal))
                    {
                        events.Add(JsonNode.Parse(line[6..])!["type"]!.GetValue<string>());
                    }
                }
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or HttpRequestException)
            {
                return new Turn(response.StatusCode, events, null, TimedOut: true);
            }
            return new Turn(response.StatusCode, events, null, TimedOut: false);
        }
    }

    /// <summary>What a person does next: Stop, a moment, then another message; whether the IDE took it.</summary>
    private static async Task<(bool Freed, string Report)> StopAndSendAgainAsync(WebRun web)
    {
        var stop = await web.PostAsync("/api/stop", new JsonObject());
        await Task.Delay(TimeSpan.FromSeconds(3));
        var busy = (await web.GetJsonAsync("/api/state"))["busy"]!.GetValue<bool>();
        var next = await TurnWithinAsync(web, "after stop", Deadline);
        var report = $"After Stop (HTTP {(int)stop.StatusCode}) and 3 s, /api/state says busy={busy.ToString().ToLowerInvariant()}; the next message gets {next}.";
        return (!busy && next.Answered, report);
    }
}

/// <summary>Arena's /api/code-arena (FakeChats), holding the requests chosen unanswered until released: a connection that stalls.</summary>
internal sealed class HoldingArena : FakeServer
{
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _held;

    /// <summary>Which requests (method, path) are held.</summary>
    public Func<string, string, bool> Holds { get; set; } = (_, _) => false;

    /// <summary>How many requests were held.</summary>
    public int Held => Volatile.Read(ref _held);

    /// <summary>Answers the held requests, and holds no more.</summary>
    public void Release() => _release.TrySetResult();

    protected override async Task HandleAsync(HttpListenerContext ctx, string body, CancellationToken ct)
    {
        var path = ctx.Request.Url!.AbsolutePath;
        if (!path.StartsWith("/api/code-arena/", StringComparison.Ordinal) || ctx.Request.Headers["Authorization"] != "Bearer " + FakeGateway.Key)
        {
            ctx.Response.StatusCode = 404;
            return;
        }
        if (!_release.Task.IsCompleted && Holds(ctx.Request.HttpMethod, path))
        {
            Interlocked.Increment(ref _held);
            await _release.Task.WaitAsync(ct);
        }
        await Chats.HandleAsync(ctx, body);
    }
}
