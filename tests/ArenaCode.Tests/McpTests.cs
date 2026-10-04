using System.Text.Json.Nodes;

namespace ArenaCode.Tests;

public sealed class McpTests : IDisposable
{
    private readonly FakeMcp _mcp = new();
    private readonly HttpClient _http = new();

    private static Dictionary<string, string> Auth => new() { ["Authorization"] = "Bearer " + FakeGateway.Key };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_client_handshakes_lists_and_calls_and_sends_the_session_back_over_JSON_or_an_event_stream(bool sse)
    {
        _mcp.Sse = sse;
        await using var client = await McpClient.ConnectAsync("arena", new HttpMcpTransport(_http, _mcp.Url, Auth), CancellationToken.None);
        Assert.Equal("argus-arena", client.ServerName);
        Assert.StartsWith("Arena: search the web", client.Instructions);
        Assert.Equal(["web_search", "read_file", "create_issue"], client.Tools.Select(t => t.Str("name")));
        var result = await client.CallAsync("web_search", new JsonObject { ["query"] = "llama" }, CancellationToken.None);
        Assert.Equal(new McpResult("Results for llama: https://arena.example/answer", false), result);
        var calls = _mcp.Calls;
        Assert.Equal(["initialize", "notifications/initialized", "tools/list", "tools/call"], calls.Select(c => c.Method));
        Assert.All(calls.Skip(1), c => Assert.Equal("session-42", c.Session));
    }

    [Fact]
    public async Task A_missing_endpoint_and_a_refused_key_are_told_apart()
    {
        _mcp.Missing = true;
        await Assert.ThrowsAsync<McpUnavailableException>(() => McpClient.ConnectAsync("arena", new HttpMcpTransport(_http, _mcp.Url, Auth), CancellationToken.None));
        _mcp.Missing = false;
        var refused = await Assert.ThrowsAsync<McpException>(() => McpClient.ConnectAsync("arena", new HttpMcpTransport(_http, _mcp.Url, new Dictionary<string, string>()), CancellationToken.None));
        Assert.IsNotType<McpUnavailableException>(refused);
        Assert.Contains("401", refused.Message);
    }

    [Fact]
    public void Tool_names_of_the_persons_own_servers_are_prefixed_and_kept_to_what_the_API_allows()
    {
        Assert.Equal("mcp__git_hub__create_issue", Runtime.OwnToolName("git hub", "create_issue"));
        Assert.Equal(64, Runtime.OwnToolName("server", new string('t', 100)).Length);
    }

    [Fact]
    public async Task A_stdio_server_of_the_persons_own_is_started_and_its_tools_ask_before_running()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using var gateway = new FakeGateway();
        var script = """
            while IFS= read -r line; do
              id=$(printf '%s' "$line" | sed -n 's/.*"id":\([0-9]*\).*/\1/p')
              case "$line" in
                *'"initialize"'*) printf '{"jsonrpc":"2.0","id":%s,"result":{"protocolVersion":"2025-06-18","capabilities":{},"serverInfo":{"name":"echo"},"instructions":"Echo repeats."}}\n' "$id" ;;
                *'"tools/list"'*) printf '{"jsonrpc":"2.0","id":%s,"result":{"tools":[{"name":"echo","description":"Repeat a word.","inputSchema":{"type":"object","properties":{"word":{"type":"string"}}}}]}}\n' "$id" ;;
                *'"tools/call"'*) printf '{"jsonrpc":"2.0","id":%s,"result":{"content":[{"type":"text","text":"echo: %s"}]}}\n' "$id" "$GREETING" ;;
              esac
            done
            """;
        using var h = new Harness(gateway, mcp: null, c => c["mcpServers"] = new JsonObject
        {
            ["echo"] = new JsonObject
            {
                ["command"] = "/bin/sh",
                ["args"] = new JsonArray("-c", script),
                ["env"] = new JsonObject { ["GREETING"] = "hello" },
            },
        });
        gateway.Answer = req => FakeGateway.HasToolResults(req) ? Reply.Say("Got: " + FakeGateway.Last(req)) : Reply.Call(("mcp__echo__echo", """{"word":"hi"}"""));

        // Asked first (the server is not trusted), and allowed.
        Assert.Equal(0, await h.Run("ask me\ny\n/exit\n"));
        Assert.Contains("Allow mcp__echo__echo?", h.Out);
        Assert.Contains("Got: echo: hello", h.Out);
        Assert.Contains("1 from echo", h.Out);
        Assert.Contains("Echo repeats.", FakeGateway.System(gateway.Requests[0]));
    }

    public void Dispose()
    {
        _mcp.Dispose();
        _http.Dispose();
    }
}
