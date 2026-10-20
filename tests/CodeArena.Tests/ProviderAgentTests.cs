namespace CodeArena.Tests;

/// <summary>The person's own coding agents (Claude Code, Codex, Gemini CLI): found on PATH, run on a task in the folder, their answer to the model.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class ProviderAgentTests : IDisposable
{
    private readonly FakeGateway _gateway = new();

    /// <summary>A stand-in "claude" on PATH: says where it ran and what it was asked, and whether the Arena key reached it.</summary>
    private static string FakeClaude(Harness h)
    {
        var bin = Path.Combine(h.Root, "bin");
        Directory.CreateDirectory(bin);
        var script = Path.Combine(bin, "claude");
        File.WriteAllText(script, """
            #!/bin/sh
            echo "ran in $(pwd)"
            echo "args: $*"
            echo "key: ${ARENA_API_KEY:-none}"
            """);
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return bin;
    }

    [Fact]
    public async Task The_model_hands_a_task_to_the_persons_own_agent_which_runs_in_the_folder_as_its_own_program()
    {
        using var h = new Harness(_gateway);
        h.Env["PATH"] = FakeClaude(h) + ":/usr/bin:/bin";
        _gateway.Answer = req => FakeGateway.HasToolResults(req) ? Reply.Say("Claude did it.") : Reply.Call(("ask_agent", """{"agent":"claude","task":"fix the failing test"}"""));
        Assert.True(await h.Run("", "-p", "ask claude to fix the test", "--mode", "yolo") == 0, h.Err + h.Out);
        Assert.Contains(FakeGateway.ToolNames(_gateway.Requests.First()), n => n == "ask_agent");
        var result = FakeGateway.History(_gateway.Requests.Last()).Last(m => m!["role"]?.ToString() == "tool")!["content"]!.ToString();
        Assert.Contains($"ran in {h.Work}", result, StringComparison.Ordinal);
        Assert.Contains("args: -p fix the failing test", result, StringComparison.Ordinal);
        Assert.Contains("key: none", result, StringComparison.Ordinal);
    }

    [Fact]
    public void With_none_of_them_installed_the_tool_is_not_offered_and_the_config_adds_or_turns_off_one()
    {
        var env = new Dictionary<string, string?> { ["PATH"] = "/nonexistent" };
        Assert.Null(ProviderAgents.Tool(new Config(), k => env.GetValueOrDefault(k)));
        var config = new Config { Agents = new() { ["mine"] = ["/bin/echo", "hello"] } };
        Assert.Equal(["mine"], ProviderAgents.Available(config, k => env.GetValueOrDefault(k)).Keys);
        Assert.NotNull(ProviderAgents.Tool(config, k => env.GetValueOrDefault(k)));
    }

    public void Dispose() => _gateway.Dispose();
}
