using System.Text.Json.Nodes;

namespace CodeArena.Tests;

/// <summary>
/// Everything the agent changed is its change, not only what its file tools wrote: a command's writes (in a git folder and
/// out of one), with what each file held before the turn; the person's own saves meanwhile stay theirs.
/// </summary>
public sealed class AgentChangesFoundTests : IDisposable
{
    private readonly FakeGateway _gateway = new();
    private readonly FakeMcp _mcp = new();

    public void Dispose()
    {
        _gateway.Dispose();
        _mcp.Dispose();
    }

    private static string Git(string dir, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }
        using var git = System.Diagnostics.Process.Start(psi)!;
        var output = git.StandardOutput.ReadToEndAsync();
        _ = git.StandardError.ReadToEndAsync();
        Assert.True(git.WaitForExit(30_000), $"git {string.Join(' ', args)} did not end");
        return output.Result;
    }

    private static string Listed(JsonArray changes) =>
        string.Join(", ", changes.Select(c => $"{c!["path"]}{(c["created"]!.GetValue<bool>() ? " new" : "")}{(c["deleted"]!.GetValue<bool>() ? " gone" : "")}"));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_commands_writes_are_the_agents_changes_with_what_the_files_held_before_the_turn(bool git)
    {
        using var h = new Harness(_gateway, _mcp, c => c["autoCommit"] = true);
        h.Write("a.txt", "one\n");
        h.Write("b.txt", "keep me\n");
        h.Write("old.txt", "going\n");
        if (git)
        {
            Git(h.Work, "init", "-q", "-b", "main");
            Git(h.Work, "config", "user.name", "Pat Person");
            Git(h.Work, "config", "user.email", "pat@example.test");
            Git(h.Work, "config", "commit.gpgsign", "false");
            Git(h.Work, "add", ".");
            Git(h.Work, "commit", "-q", "-m", "first");
            // Changed before the turn: what it held then is the before, not the commit's.
            h.Write("b.txt", "keep me, mine\n");
        }
        _gateway.Answer = req => FakeGateway.HasToolResults(req)
            ? Reply.Say("done")
            : Reply.Call(("run_shell", """{"command":"printf 'two\\n' > a.txt && printf 'made\\n' > made.txt && rm old.txt && printf 'more\\n' >> b.txt"}"""));
        await using var web = await WebRun.StartAsync(h, "--mode", "yolo");

        var events = await (await web.SendAsync("change them with a command")).RestAsync();
        // Told to the page with the command's result, to show at once.
        var result = events.Single(e => e["type"]?.ToString() == "tool_result");
        Assert.Equal(["a.txt", "b.txt", "made.txt", "old.txt"], result["changed"]!.AsArray().Select(p => p!.ToString()).Order(StringComparer.Ordinal));

        var changes = (await web.GetJsonAsync("/api/changes")).AsArray();
        Assert.Equal("a.txt, b.txt, made.txt new, old.txt gone", Listed(changes));
        Assert.Equal("one\n", (await web.GetJsonAsync("/api/changes/diff?path=a.txt"))["original"]!.GetValue<string>());
        Assert.Equal(git ? "keep me, mine\n" : "keep me\n", (await web.GetJsonAsync("/api/changes/diff?path=b.txt"))["original"]!.GetValue<string>());
        Assert.Equal("going\n", (await web.GetJsonAsync("/api/changes/diff?path=old.txt"))["original"]!.GetValue<string>());

        // Reverted from the list as an edit is: the file as it was.
        await web.PostAsync("/api/changes/revert", new JsonObject { ["path"] = "a.txt" });
        Assert.Equal("one\n", File.ReadAllText(Path.Combine(h.Work, "a.txt")));
    }

    [Fact]
    public async Task What_the_person_saves_in_the_ide_while_the_agent_works_stays_theirs()
    {
        using var h = new Harness(_gateway, _mcp);
        h.Write("theirs.txt", "draft\n");
        h.Write("agents.txt", "x\n");
        _gateway.Answer = req => FakeGateway.HasToolResults(req)
            ? Reply.Say("done")
            : Reply.Call(("run_shell", """{"command":"printf 'y\\n' > agents.txt"}"""));
        await using var web = await WebRun.StartAsync(h, "--mode", "yolo");
        // Saved by the person in the editor (a turn may be running): not the agent's.
        await web.PostAsync("/api/file", new JsonObject { ["path"] = "theirs.txt", ["text"] = "final\n" });
        await (await web.SendAsync("change it")).RestAsync();
        await web.PostAsync("/api/file", new JsonObject { ["path"] = "theirs.txt", ["text"] = "final, again\n" });
        Assert.Equal("agents.txt", Listed((await web.GetJsonAsync("/api/changes")).AsArray()));
    }
}
