using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace CodeArena.Tests;

/// <summary>
/// Reproduction of a stall (hypothesis "sync-lock"): ChatSync.PushAsync holds its _busy semaphore across a request to
/// Arena that has no time limit (Net.Client: Timeout infinite), and TakeInAsync, which every turn's start awaits
/// (Agent.BeforeTurn; Web.MessageAsync after Begin("answer")), waits for _busy. An Arena send that never answers (a
/// stalled Arena, a proxy holding the request, a half-open connection after sleep or a VPN change) then blocks the
/// next prompt. Each test asserts what should happen and FAILS (after a deadline, never hanging) while the stall is there.
/// </summary>
public sealed class HangSyncLockTests : IDisposable
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);
    private readonly FakeGateway _gateway = new();
    private readonly HoldingSyncArena _arena = new();

    public void Dispose()
    {
        _arena.Release.TrySetResult();
        _gateway.Dispose();
        _arena.Dispose();
    }

    /// <summary>Arena (its /api/code-arena) is the holding fake; it has no /mcp (404), as an Arena without MCP.</summary>
    private Harness NewHarness() => new(_gateway, null, c => c["url"] = _arena.BaseUrl);

    private bool Asked(string text) => _gateway.Requests.Any(r => FakeGateway.Last(r) == text);

    private static async Task<bool> Within(TimeSpan most, Func<bool> condition)
    {
        var until = DateTime.UtcNow + most;
        while (DateTime.UtcNow < until)
        {
            if (condition())
            {
                return true;
            }
            await Task.Delay(50);
        }
        return condition();
    }

    /// <summary>The first turn's answer waits until its send to Arena is on the wire and held: PushAsync owns _busy before turn two.</summary>
    private void AnswerOnceTheFirstSendIsHeld() => _gateway.Answer = req =>
    {
        var last = FakeGateway.Last(req);
        if (last == "first")
        {
            _arena.Held.Task.Wait(TimeSpan.FromSeconds(20));
        }
        return Reply.Say($"answer to {last}");
    };

    private static string Tail(string text, int most = 400) => text.Length <= most ? text : text[^most..];

    private static HttpRequestMessage Message(string text) => new(HttpMethod.Post, "/api/messages")
    {
        Content = new StringContent(new JsonObject { ["text"] = text }.ToJsonString(), Encoding.UTF8, "application/json"),
    };

    [Fact]
    public async Task Terminal_the_second_prompt_reaches_the_model_while_a_send_to_Arena_hangs()
    {
        _arena.HoldSends = true;
        AnswerOnceTheFirstSendIsHeld();
        using var h = NewHarness();
        var output = new StringWriter();
        var error = new StringWriter();
        var env = new CliEnv
        {
            In = new StringReader("first\nsecond\n/exit\n"),
            Out = TextWriter.Synchronized(output),
            Err = TextWriter.Synchronized(error),
            Env = k => h.Env.GetValueOrDefault(k),
            Cwd = h.Work,
            Paths = h.Paths,
            InTerminal = true,
        };
        string Out()
        {
            lock (env.Out)
            {
                return output.ToString();
            }
        }
        var run = Task.Run(() => Cli.RunAsync(["chat"], env));
        string? failure = null;
        try
        {
            Assert.True(await Within(Deadline, () => Asked("first")), $"The first prompt never reached the model: {Tail(Out())}");
            Assert.True(await Within(Deadline, () => _arena.Held.Task.IsCompleted), "Arena never got the first turn's send.");
            Assert.True(await Within(Deadline, () => Out().Contains("answer to first", StringComparison.Ordinal)), $"The first answer was not shown: {Tail(Out())}");

            var reached = await Within(Deadline, () => Asked("second"));
            if (!reached)
            {
                var shown = Tail(Out());
                // The cause? Let the held send go: the second prompt goes on at once if it waited for it.
                var released = DateTime.UtcNow;
                _arena.Release.TrySetResult();
                var then = await Within(TimeSpan.FromSeconds(15), () => Asked("second"));
                failure =
                    $"STALL (terminal): the second prompt did not reach the model in {Deadline.TotalSeconds} s while Arena held one send " +
                    $"(ChatSync._busy owned by PushAsync, TakeInAsync waiting in Agent.BeforeTurn). Once the held send was let go, it " +
                    $"{(then ? $"reached the model after {(DateTime.UtcNow - released).TotalMilliseconds:F0} ms" : "still did not reach the model")}. " +
                    $"Terminal at the stall: «{shown}»";
            }
        }
        finally
        {
            _arena.Release.TrySetResult();
            if (await Task.WhenAny(run, Task.Delay(Deadline)) != run)
            {
                env.Cancel.Press();
            }
        }
        if (failure is not null)
        {
            Assert.Fail(failure);
        }
        Assert.Equal(0, await run.WaitAsync(Deadline));
    }

    [Fact]
    public async Task Ide_the_second_message_starts_while_a_send_to_Arena_hangs()
    {
        _arena.HoldSends = true;
        AnswerOnceTheFirstSendIsHeld();
        using var h = NewHarness();
        string? failure;
        await using (var web = await WebRun.StartAsync(h))
        {
            failure = await SecondMessageAsync(web);
        }
        if (failure is not null)
        {
            Assert.Fail(failure);
        }
    }

    /// <summary>Two messages in the IDE, the first turn's send held by Arena; what went wrong, or null.</summary>
    private async Task<string?> SecondMessageAsync(WebRun web)
    {
        try
        {
            using (var first = await web.SendAsync("first"))
            {
                Assert.Equal("done", (await first.RestAsync())[^1]["type"]!.GetValue<string>());
            }
            Assert.True(_arena.Held.Task.IsCompleted, "Arena never got the first turn's send.");

            using var second = Message("second");
            var sending = web.Http.SendAsync(second, HttpCompletionOption.ResponseHeadersRead);
            if (await Task.WhenAny(sending, Task.Delay(Deadline)) != sending)
            {
                // What the page meets meanwhile.
                var busy = (await web.GetJsonAsync("/api/state"))["busy"]?.ToJsonString();
                var third = (await web.PostAsync("/api/messages", new JsonObject { ["text"] = "third" })).StatusCode;
                var stop = (await web.PostAsync("/api/stop", new JsonObject())).StatusCode;
                var afterStop = await Task.WhenAny(sending, Task.Delay(TimeSpan.FromSeconds(3))) == sending ? "answered" : "still pending";
                var fourth = (await web.PostAsync("/api/messages", new JsonObject { ["text"] = "fourth" })).StatusCode;
                // The cause? Let the held send go.
                var released = DateTime.UtcNow;
                _arena.Release.TrySetResult();
                var then = await Task.WhenAny(sending, Task.Delay(TimeSpan.FromSeconds(15))) == sending;
                var took = (DateTime.UtcNow - released).TotalMilliseconds;
                var events = "";
                if (then)
                {
                    using var response = await sending;
                    using var stream = new EventStream(response);
                    events = string.Join(", ", (await stream.RestAsync()).Select(e => e["type"]));
                }
                return
                    $"STALL (IDE): POST /api/messages \"second\" had no answer (not even the event stream's start) in {Deadline.TotalSeconds} s " +
                    $"while Arena held one send (Web.MessageAsync awaiting ChatSync.TakeInAsync after Begin(\"answer\")). Meanwhile: " +
                    $"/api/state busy={busy}; another message: {(int)third} {third}; /api/stop: {(int)stop} {stop}, then the second " +
                    $"message {afterStop} and another message: {(int)fourth} {fourth}. Once the held send was let go, the second message " +
                    $"{(then ? $"answered after {took:F0} ms with [{events}]" : "still had no answer")}.";
            }
            using (var response = await sending)
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var stream = new EventStream(response);
                Assert.Equal("done", (await stream.RestAsync())[^1]["type"]!.GetValue<string>());
            }
            Assert.True(Asked("second"));
            return null;
        }
        finally
        {
            _arena.Release.TrySetResult();
        }
    }

    [Fact]
    public async Task Ide_a_failure_while_taking_in_the_web_chat_does_not_leave_the_IDE_busy_for_good()
    {
        _gateway.Answer = req => Reply.Say($"answer to {FakeGateway.Last(req)}");
        using var h = NewHarness();
        string? failure;
        await using (var web = await WebRun.StartAsync(h))
        {
            failure = await FailedTakeInAsync(h, web);
        }
        if (failure is not null)
        {
            Assert.Fail(failure);
        }
    }

    /// <summary>A message whose take-in throws (the session file not writable for a moment), then one more; what went wrong, or null.</summary>
    private async Task<string?> FailedTakeInAsync(Harness h, WebRun web)
    {
        if (OperatingSystem.IsWindows())
        {
            return null;
        }
        using (var first = await web.SendAsync("first"))
        {
            Assert.Equal("done", (await first.RestAsync())[^1]["type"]!.GetValue<string>());
        }
        // Arena has the first turn, and the session file has said so (nothing left to send).
        Assert.True(await Within(Deadline, () => _arena.Chats.All.SingleOrDefault()?.Messages.Count == 2), "Arena never got the first turn.");
        await Task.Delay(1000);
        var file = Directory.GetFiles(h.Paths.SessionsDir, "*.jsonl", SearchOption.AllDirectories).Single();

        // The session file cannot be written for a moment (a full disk, a lock): TakeInAsync's _store.Sync throws.
        var mode = File.GetUnixFileMode(file);
        File.SetUnixFileMode(file, UnixFileMode.UserRead);
        string second;
        try
        {
            try
            {
                using var probe = new FileStream(file, FileMode.Append, FileAccess.Write);
                // Root writes it all the same: this cannot be set up here.
                return null;
            }
            catch (UnauthorizedAccessException)
            {
            }
            try
            {
                var answer = await web.PostAsync("/api/messages", new JsonObject { ["text"] = "second" });
                second = $"{(int)answer.StatusCode} {Tail(await answer.Content.ReadAsStringAsync(), 200)}";
            }
            catch (HttpRequestException e)
            {
                second = $"no answer ({e.Message})";
            }
        }
        finally
        {
            File.SetUnixFileMode(file, mode);
        }

        // Writable again: the next message is answered.
        using var third = Message("third");
        var sending = web.Http.SendAsync(third, HttpCompletionOption.ResponseHeadersRead);
        if (await Task.WhenAny(sending, Task.Delay(Deadline)) != sending)
        {
            return $"The third message had no answer in {Deadline.TotalSeconds} s (the second: {second}).";
        }
        using var response = await sending;
        if (response.StatusCode != HttpStatusCode.OK)
        {
            var body = await response.Content.ReadAsStringAsync();
            var busy = (await web.GetJsonAsync("/api/state"))["busy"]?.ToJsonString();
            // Still so a while later: not a turn that is ending.
            await Task.Delay(3000);
            var later = (await web.PostAsync("/api/messages", new JsonObject { ["text"] = "fourth" })).StatusCode;
            return
                $"STUCK BUSY (IDE): the second message failed in TakeInAsync after Begin(\"answer\") ({second}); with the file writable " +
                $"again, the third message got {(int)response.StatusCode} {Tail(body, 200)}, /api/state busy={busy}, and 3 s later " +
                $"another message still got {(int)later} {later}: the job Begin made is never ended.";
        }
        using var stream = new EventStream(response);
        Assert.Equal("done", (await stream.RestAsync())[^1]["type"]!.GetValue<string>());
        Assert.True(Asked("third"));
        return null;
    }
}

/// <summary>
/// Arena's /api/code-arena (the shared FakeChats) whose message sends can be held open, as a stalled Arena, a proxy
/// that holds the request, or a half-open connection would: the request is read, and no answer comes until released.
/// </summary>
public sealed class HoldingSyncArena : FakeServer
{
    /// <summary>While set, each POST /api/code-arena/chats/{id}/messages waits for <see cref="Release"/>.</summary>
    public volatile bool HoldSends;
    /// <summary>Done once a send is being held.</summary>
    public TaskCompletionSource Held { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override async Task HandleAsync(HttpListenerContext ctx, string body, CancellationToken ct)
    {
        var path = ctx.Request.Url!.AbsolutePath;
        if (!path.StartsWith("/api/code-arena/", StringComparison.Ordinal) || ctx.Request.Headers["Authorization"] != "Bearer " + FakeGateway.Key)
        {
            ctx.Response.StatusCode = 404;
            return;
        }
        if (HoldSends && ctx.Request.HttpMethod == "POST" && path.EndsWith("/messages", StringComparison.Ordinal))
        {
            Held.TrySetResult();
            await Release.Task.WaitAsync(ct);
        }
        await Chats.HandleAsync(ctx, body);
    }
}
