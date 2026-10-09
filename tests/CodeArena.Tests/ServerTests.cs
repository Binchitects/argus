using System.Diagnostics;
using System.Text.Json.Nodes;

namespace CodeArena.Tests;

/// <summary>Arena's and Argus's MCP servers, connected in the background: slow, down, lost, back.</summary>
public sealed class ServerTests : IDisposable
{
    private readonly FakeGateway _gateway = new();
    private readonly FakeMcp _mcp = new();
    private readonly StringWriter _output = new();

    private async Task<Runtime> StartAsync(Harness h, Options? o = null, Func<string, string?>? variables = null)
    {
        var env = new CliEnv { In = new StringReader(""), Out = _output, Err = _output, Env = variables ?? (_ => null), Cwd = h.Work, Paths = h.Paths };
        return await Runtime.StartAsync(o ?? new Options(), env, new Ui(env.In, _output, _output, false, false), CancellationToken.None);
    }

    private static async Task Until(Func<bool> condition, string what, int seconds = 15)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(seconds), $"Not within {seconds} s: {what}");
            await Task.Delay(50);
        }
    }

    private static JsonObject Tool(string name) => new()
    {
        ["name"] = name,
        ["description"] = $"Argus's {name}.",
        ["inputSchema"] = new JsonObject { ["type"] = "object" },
        ["annotations"] = new JsonObject { ["readOnlyHint"] = true },
    };

    [Fact]
    public async Task A_slow_Arena_does_not_hold_the_session_and_its_tools_join_when_it_answers()
    {
        _mcp.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var h = new Harness(_gateway, _mcp);
        var clock = Stopwatch.StartNew();
        await using var rt = await StartAsync(h);

        // Started without it: a few seconds at most, not the handshake's 20.
        Assert.True(clock.Elapsed < Runtime.StartWait + TimeSpan.FromSeconds(5), $"started in {clock.Elapsed}");
        var arena = Assert.Single(rt.Links);
        Assert.Equal(LinkState.Connecting, arena.State);
        Assert.Equal("Arena: connecting", rt.Describe(arena));
        Assert.DoesNotContain(rt.Tools.All, t => t.Server == Runtime.ArenaName);
        Assert.Contains("Arena's tools are still connecting: they join the session when they answer.", _output.ToString());

        _mcp.Hold.SetResult();
        await arena.FirstTry.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(rt.ArenaConnected);
        Assert.Equal("Arena: 3 tools", rt.Describe(arena));
        Assert.Contains("Arena's tools connected: 3.", _output.ToString());

        // The next request has them, and Arena's instructions.
        _gateway.Answer = _ => Reply.Say("ok");
        Assert.Equal("ok", await rt.Agent.RunAsync("hi", new Spend(), CancellationToken.None));
        Assert.Contains("web_search", FakeGateway.ToolNames(_gateway.Requests[0]));
        Assert.Contains("arena_read_file", FakeGateway.ToolNames(_gateway.Requests[0]));
        Assert.Contains("Arena: search the web with web_search", FakeGateway.System(_gateway.Requests[0]));
    }

    [Fact]
    public async Task An_Arena_that_is_down_is_said_once_the_session_carries_on_and_it_is_tried_again_until_it_answers()
    {
        _mcp.Status = 503;
        using var h = new Harness(_gateway, _mcp);
        await using var rt = await StartAsync(h);
        var arena = Assert.Single(rt.Links);
        await arena.FirstTry.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(LinkState.Failed, arena.State);
        Assert.Contains("503", arena.Error);
        Assert.NotNull(arena.NextTry);
        Assert.StartsWith("Arena: not connected, tried again at ", rt.Describe(arena));

        // The session works with the local tools meanwhile; the failure is said once, not at each try.
        _gateway.Answer = _ => Reply.Say("ok");
        Assert.Equal("ok", await rt.Agent.RunAsync("hi", new Spend(), CancellationToken.None));
        Assert.DoesNotContain("web_search", FakeGateway.ToolNames(_gateway.Requests[0]));
        Assert.Contains("read_file", FakeGateway.ToolNames(_gateway.Requests[0]));
        Assert.Contains("Arena's tools are not available in this session", FakeGateway.System(_gateway.Requests[0]));

        // Back: the next try (5 seconds after the failure) connects it, and its tools join.
        _mcp.Status = 0;
        await Until(() => rt.ArenaConnected, "Arena connected again");
        Assert.Contains(rt.Tools.All, t => t.Name == "web_search");
        var said = _output.ToString();
        Assert.Equal(1, said.Split("Arena's tools did not connect").Length - 1);
        Assert.Contains("Arena's tools connected: 3.", said);
    }

    [Fact]
    public async Task A_call_that_finds_the_server_gone_connects_again_and_retry_tries_at_once()
    {
        using var h = new Harness(_gateway, _mcp);
        await using var rt = await StartAsync(h);
        var arena = Assert.Single(rt.Links);
        await arena.FirstTry.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(rt.ArenaConnected);

        // Its proxy says it is gone (502): the call fails, said to the model, and the link connects again (and fails).
        _mcp.Status = 502;
        _gateway.Answer = req => FakeGateway.HasToolResults(req) ? Reply.Say("Saw: " + FakeGateway.Last(req)) : Reply.Call(("web_search", """{"query":"x"}"""));
        var answer = await rt.Agent.RunAsync("search", new Spend(), CancellationToken.None);
        Assert.Contains("Error from the arena tools", answer);
        Assert.Contains("502", answer);
        await Until(() => arena.State == LinkState.Failed, "the link failed");
        Assert.DoesNotContain(rt.Tools.All, t => t.Server == Runtime.ArenaName);

        // /mcp retry (or the IDE's Try again): at once, not after the wait.
        _mcp.Status = 0;
        arena.Retry();
        await Until(() => rt.ArenaConnected, "Arena connected on retry", seconds: 4);
        Assert.Contains(rt.Tools.All, t => t.Name == "web_search");
    }

    [Fact]
    public async Task Argus_connects_beside_Arena_and_its_tools_Arena_serves_are_offered_once()
    {
        using var argus = new FakeMcp { ToolList = [Tool("find_symbol"), Tool("web_search")] };
        _mcp.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var h = new Harness(_gateway, _mcp, c => c["argusUrl"] = argus.Url);
        await using var rt = await StartAsync(h);
        Assert.Equal([Runtime.ArenaName, Runtime.ArgusName], rt.Links.Select(l => l.Name));
        var argusLink = rt.Links[1];
        await argusLink.FirstTry.WaitAsync(TimeSpan.FromSeconds(10));

        // Arena still connecting: Argus's own tools, both.
        Assert.Equal(["find_symbol", "web_search"], rt.Tools.All.Where(t => t.Server == Runtime.ArgusName).Select(t => t.Name));

        // Arena connects: its web_search is the one kept; Argus keeps what Arena does not serve.
        _mcp.Hold.SetResult();
        await rt.Links[0].FirstTry.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Single(rt.Tools.All, t => t.Name == "web_search");
        Assert.Equal(Runtime.ArenaName, rt.Tools.Find("web_search")!.Server);
        Assert.Equal(Runtime.ArgusName, rt.Tools.Find("find_symbol")!.Server);
        Assert.True(rt.Tools.Find("find_symbol")!.ReadOnly);

        // Arena lost: Argus's copy comes back.
        _mcp.Status = 503;
        rt.Links[0].Retry();
        await Until(() => rt.Links[0].State == LinkState.Failed, "Arena lost");
        Assert.Equal(Runtime.ArgusName, rt.Tools.Find("web_search")!.Server);
        Assert.Contains(argus.Calls, c => c.Method == "tools/list");
    }

    [Fact]
    public async Task Turned_off_in_the_config_neither_Arena_nor_Argus_is_asked()
    {
        using var argus = new FakeMcp();
        using var h = new Harness(_gateway, _mcp, c =>
        {
            c["argusUrl"] = argus.Url;
            c["arenaTools"] = false;
            c["argusTools"] = false;
        });
        Assert.Equal(0, await h.Run("/mcp\n/exit\n", "chat"));
        Assert.Contains("No MCP servers: Arena's and Argus's are off in config.json (arenaTools, argusTools)", h.Out);
        Assert.Empty(_mcp.Calls);
        Assert.Empty(argus.Calls);
    }

    [Fact]
    public async Task Mcp_lists_the_servers_and_retry_tries_those_not_connected()
    {
        _mcp.Status = 503;
        using var h = new Harness(_gateway, _mcp);
        Assert.Equal(0, await h.Run("/mcp\n/mcp retry\n/exit\n", "chat"));
        Assert.Contains("Arena not connected", h.Out); // the banner
        Assert.Contains("Arena: not connected, tried again at", h.Out);
        Assert.Contains("answered 503", h.Out);
        Assert.Contains("/mcp retry tries those not connected now", h.Out);
        Assert.Contains("Trying Arena again: it says when it is connected.", h.Out);
    }

    [Fact]
    public async Task An_Arena_without_Argus_beside_it_is_said_once_quietly_not_as_a_failure()
    {
        // argus.DOMAIN does not resolve (.invalid never does): this Arena has no Argus.
        using var h = new Harness(_gateway, _mcp, c =>
        {
            c["url"] = "http://arena.invalid";
            c["mcpUrl"] = _mcp.Url;
        });
        await using var rt = await StartAsync(h);
        var argus = rt.Links.Single(l => l.Name == Runtime.ArgusName);
        Assert.Equal("http://argus.arena.invalid/mcp", argus.Url);
        await argus.FirstTry.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(LinkState.Unavailable, argus.State);
        Assert.Equal("Argus: not available here", argus.Describe());
        Assert.Contains("Argus's tools are not available here: http://argus.arena.invalid/mcp does not resolve: this Arena has no Argus at argus.DOMAIN", _output.ToString());
        Assert.DoesNotContain("Argus's tools did not connect", _output.ToString());
        Assert.True(rt.ArenaConnected);
    }

    [Fact]
    public void Argus_is_found_beside_the_Arena_unless_the_Arena_has_no_name_for_it()
    {
        Assert.Equal("https://argus.arena.example.com/mcp", Config.DeriveArgus("https://arena.example.com"));
        Assert.Equal("https://argus.arena.example.com:8443/mcp", Config.DeriveArgus("https://arena.example.com:8443/"));
        Assert.Null(Config.DeriveArgus("http://127.0.0.1:8080"));
        Assert.Null(Config.DeriveArgus("http://localhost:8080"));
        Assert.Null(Config.DeriveArgus(null));
        Assert.Equal("https://code.example/mcp", new Config { Url = "https://arena.example.com", ArgusUrl = "https://code.example/mcp" }.ArgusMcpUrl);
    }

    [Fact]
    public async Task A_refusal_says_the_servers_reason()
    {
        using var refusing = new RefusingMcp();
        using var http = new HttpClient();
        var e = await Assert.ThrowsAsync<McpException>(() => McpClient.ConnectAsync("argus", new HttpMcpTransport(http, refusing.BaseUrl + "/mcp", new Dictionary<string, string>()), CancellationToken.None));
        Assert.Equal($"{refusing.BaseUrl}/mcp refused the credentials (401): Cannot verify your GitLab access right now.", e.Message);
        Assert.False(e.Lost);
    }

    [Fact]
    public async Task A_failure_of_any_kind_is_said_and_tried_again_and_the_first_try_always_ends()
    {
        // What HttpClient throws for a scheme it does not speak (or Uri for a port out of range): neither an MCP nor a network error.
        var tries = 0;
        await using var link = new ServerLink("mine", "Mine", "ftp://files.example/mcp", _ =>
        {
            Interlocked.Increment(ref tries);
            throw new NotSupportedException("The 'ftp' scheme is not supported.");
        });
        link.Start();
        await link.FirstTry.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(LinkState.Failed, link.State);
        Assert.Equal("ftp://files.example/mcp: The 'ftp' scheme is not supported.", link.Error);
        Assert.NotNull(link.NextTry);
        Assert.StartsWith("Mine: not connected, tried again at ", link.Describe());

        // Still trying, not stuck: a retry tries at once.
        link.Retry();
        await Until(() => Volatile.Read(ref tries) >= 2, "tried again", seconds: 5);
    }

    [Fact]
    public async Task A_fault_in_what_hears_of_a_change_is_a_failure_like_any_not_the_end_of_the_link()
    {
        using var http = new HttpClient();
        await using var link = new ServerLink(Runtime.ArenaName, "Arena", _mcp.Url,
            ct => McpClient.ConnectAsync(Runtime.ArenaName, new HttpMcpTransport(http, _mcp.Url, new Dictionary<string, string> { ["Authorization"] = "Bearer " + FakeGateway.Key }), ct));
        var heard = 0;
        link.Changed = (_, client, _) =>
        {
            if (Interlocked.Increment(ref heard) == 1)
            {
                throw new InvalidCastException("a fault in the handler");
            }
        };
        link.Start();
        await link.FirstTry.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(LinkState.Failed, link.State);
        Assert.Contains("a fault in the handler", link.Error);
        Assert.Null(link.Client);

        link.Retry();
        await Until(() => link.State == LinkState.Connected, "connected on retry");
        Assert.Equal(3, link.Client!.Tools.Count);
    }

    [Fact]
    public async Task A_command_waits_for_Arenas_first_answer_no_longer_than_its_handshake()
    {
        var shell = LocalTools.All().Single(t => t.Name == "run_shell");
        var ui = new Ui(new StringReader(""), _output, _output, false, false);
        Assert.Equal(McpClient.Handshake, new Permissions(ui, Mode.Yolo).GuardWait);
        // A first try that never ends (as a link whose loop had died did): the command runs after the wait all the same.
        var permissions = new Permissions(ui, Mode.Yolo) { GuardReady = new TaskCompletionSource().Task, GuardWait = TimeSpan.FromMilliseconds(300) };
        var clock = Stopwatch.StartNew();
        Assert.Null(await permissions.CheckAsync(shell, new JsonObject { ["command"] = "ls" }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.InRange(clock.Elapsed, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(4));

        // Stopped meanwhile (Ctrl+C): stopped, not waited out.
        permissions.GuardWait = TimeSpan.FromMinutes(1);
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => permissions.CheckAsync(shell, new JsonObject { ["command"] = "ls" }, stop.Token).WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Theory]
    [InlineData("mcpUrl", "https://arena.corp:99999/mcp", "ARENA_MCP_URL")]
    [InlineData("mcpUrl", "https://arena corp/mcp", "ARENA_MCP_URL")]
    [InlineData("argusUrl", "ftp://argus.corp/mcp", "ARENA_ARGUS_URL")]
    public async Task An_address_that_is_not_one_stops_the_session_as_it_starts_with_where_to_fix_it(string key, string address, string variable)
    {
        using var h = new Harness(_gateway, _mcp, c => c[key] = address);
        var e = await Assert.ThrowsAsync<Runtime.StartException>(() => StartAsync(h));
        Assert.Equal($"{address} is not an http or https address: fix \"{key}\" in {h.Paths.ConfigFile} (or {variable}).", e.Message);

        // From the environment too.
        using var fine = new Harness(_gateway, _mcp);
        e = await Assert.ThrowsAsync<Runtime.StartException>(() => StartAsync(fine, variables: name => name == variable ? address : null));
        Assert.StartsWith($"{address} is not an http or https address", e.Message);
    }

    [Fact]
    public async Task An_mcp_server_of_ones_own_at_an_address_that_is_not_one_is_skipped_and_said()
    {
        using var h = new Harness(_gateway, _mcp, c => c["mcpServers"] = new JsonObject { ["files"] = new JsonObject { ["url"] = "ftp://files.example/mcp" } });
        await using var rt = await StartAsync(h);
        Assert.Equal([Runtime.ArenaName], rt.Links.Select(l => l.Name));
        Assert.Contains("MCP server files: ftp://files.example/mcp is not an http or https address: skipped.", _output.ToString());
    }

    /// <summary>Argus refusing as it does when it cannot say what the person may read.</summary>
    private sealed class RefusingMcp : FakeServer
    {
        protected override Task HandleAsync(System.Net.HttpListenerContext ctx, string body, CancellationToken ct) =>
            WriteJson(ctx, new JsonObject { ["error"] = "Cannot verify your GitLab access right now." }, 401);
    }

    public void Dispose()
    {
        _gateway.Dispose();
        _mcp.Dispose();
        _output.Dispose();
    }
}
