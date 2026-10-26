using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace CodeArena.Tests;

/// <summary>A test that needs Python 3 on PATH (code mode runs it): skipped where there is none.</summary>
public sealed class PythonFactAttribute : FactAttribute
{
    public PythonFactAttribute()
    {
        if (GdbFactAttribute.Run(OperatingSystem.IsWindows() ? "python" : "python3", "--version") is null)
        {
            Skip = "No Python 3 on PATH.";
        }
    }
}

/// <summary>Code mode: a program calls the session's tools through a bridge, each call checked as a direct one is.</summary>
public sealed class CodeModeTests : IDisposable
{
    private readonly FakeGateway _gateway = new();
    private readonly FakeMcp _mcp = new();

    public void Dispose()
    {
        _gateway.Dispose();
        _mcp.Dispose();
    }

    [Fact]
    public async Task The_bridge_answers_each_line_with_its_call_and_says_what_a_call_is_when_it_is_not_one()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ca-{Guid.NewGuid():N}"[..20] + ".s");
        await using var bridge = new ToolBridge(path, (name, args, _) => Task.FromResult(new JsonObject { ["ok"] = true, ["result"] = $"{name}:{args.Str("x")}" }));
        using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await client.ConnectAsync(new UnixDomainSocketEndPoint(path));
        await using var stream = new NetworkStream(client);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        await writer.WriteLineAsync("""{"name":"read_file","args":{"x":"a.txt"}}""");
        Assert.Equal("""{"ok":true,"result":"read_file:a.txt"}""", await reader.ReadLineAsync());
        await writer.WriteLineAsync("not json");
        Assert.Contains("A call is", await reader.ReadLineAsync(), StringComparison.Ordinal);
        Assert.Equal(["read_file"], bridge.Calls);
    }

    [Fact]
    public async Task Plan_mode_refuses_run_code_itself_since_a_program_could_write_without_a_tool()
    {
        _gateway.Answer = req => FakeGateway.Last(req) == "go" && req["messages"]!.AsArray()[^1]!["role"]!.GetValue<string>() == "user"
            ? Reply.Call(("run_code", """{"code":"open('x.txt','w').write('x')"}""")) : Reply.Say("ok");
        using var h = new Harness(_gateway, _mcp);
        Assert.Equal(0, await h.Run("go\n/exit\n", "chat", "--mode", "plan"));
        Assert.Contains("Plan mode is read-only: run_code is not allowed", FakeGateway.History(_gateway.Requests[^1]).Last(m => m["role"]!.GetValue<string>() == "tool")["content"]!.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(h.Work, "x.txt")));
    }

    [PythonFact]
    public async Task A_program_reads_files_and_searches_with_the_sessions_tools_and_only_what_it_prints_comes_back()
    {
        _gateway.Answer = req => FakeGateway.Last(req) == "count" && req["messages"]!.AsArray()[^1]!["role"]!.GetValue<string>() == "user"
            ? Reply.Call(("run_code", """
                {"code":"import arena\nprint(arena.tools.search('read a file'))\ntext = arena.local.read_file(path='a.txt')\nprint('lines:', str(text).count('one'))\narena.local.write_file(path='b.txt', content='made from code')\ntry:\n    arena.local.no_such_tool()\nexcept AttributeError:\n    print('no such function')\n"}
                """))
            : Reply.Say("ok");
        using var h = new Harness(_gateway, _mcp);
        File.WriteAllText(Path.Combine(h.Work, "a.txt"), "one\ntwo\nthree\n");
        Assert.Equal(0, await h.Run("count\n/exit\n", "chat", "--mode", "yolo"));
        var result = FakeGateway.History(_gateway.Requests[^1]).Last(m => m["role"]!.GetValue<string>() == "tool")["content"]!.ToString();
        Assert.Contains("arena.local.read_file", result, StringComparison.Ordinal);
        Assert.Contains("lines: 1", result, StringComparison.Ordinal);
        Assert.Contains("no such function", result, StringComparison.Ordinal);
        Assert.Contains("calls: read_file, write_file", result, StringComparison.Ordinal);
        // Each call ran as a direct one would: the file written is there.
        Assert.Equal("made from code", File.ReadAllText(Path.Combine(h.Work, "b.txt")));
        // Its run folder goes with it.
        Assert.False(Directory.Exists(Path.Combine(h.Work, ".code-arena", "r")) && Directory.EnumerateFileSystemEntries(Path.Combine(h.Work, ".code-arena", "r")).Any());
    }
}
