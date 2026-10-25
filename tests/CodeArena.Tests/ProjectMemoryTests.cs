namespace CodeArena.Tests;

/// <summary>The project's long memory in .remember/: read at a session's start, kept with remember_project, only inside its folder.</summary>
public sealed class ProjectMemoryTests : IDisposable
{
    private readonly FakeGateway _gateway = new();
    private readonly FakeMcp _mcp = new();

    public void Dispose()
    {
        _gateway.Dispose();
        _mcp.Dispose();
    }

    [Fact]
    public void A_section_is_made_added_to_or_replaced_and_the_rest_stays_as_it_was()
    {
        var made = ProjectMemory.Keep("", "MEMORY.md", "How to build, test and run", "- `make check` runs the tests.", replace: false);
        Assert.StartsWith("# Project memory\n", made, StringComparison.Ordinal);
        Assert.EndsWith("\n## How to build, test and run\n\n- `make check` runs the tests.\n", made, StringComparison.Ordinal);

        var more = ProjectMemory.Keep(made + "\n## Decisions\n\n- Postgres, not MySQL.\n", "MEMORY.md", "how to build, test and run", "- `make run` starts it on :8080.", replace: false);
        Assert.Contains("- `make check` runs the tests.\n- `make run` starts it on :8080.\n\n## Decisions\n\n- Postgres, not MySQL.\n", more, StringComparison.Ordinal);

        var replaced = ProjectMemory.Keep(more, "MEMORY.md", "Decisions", "- SQLite for the tests, Postgres in production.", replace: true);
        Assert.Contains("## Decisions\n\n- SQLite for the tests, Postgres in production.\n", replaced, StringComparison.Ordinal);
        Assert.DoesNotContain("not MySQL", replaced, StringComparison.Ordinal);
        Assert.Contains("make run", replaced, StringComparison.Ordinal);

        Assert.Equal("# architecture\n\n## Parts\n\n- The API, the worker.\n", ProjectMemory.Keep("", "architecture.md", "Parts", "- The API, the worker.", replace: false));
        Assert.NotNull(ProjectMemory.FileOf("/repo", "../secrets.md").Problem);
        Assert.NotNull(ProjectMemory.FileOf("/repo", "notes.txt").Problem);
        Assert.EndsWith("/repo/.remember/topics/db.md", ProjectMemory.FileOf("/repo", ".remember/topics/db.md").Path!.Replace('\\', '/'), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_session_starts_from_the_project_memory_and_keeps_what_it_learns_for_the_next()
    {
        using var h = new Harness(_gateway, _mcp, c => c["projectMemory"] = true);
        Directory.CreateDirectory(Path.Combine(h.Work, ".remember"));
        File.WriteAllText(Path.Combine(h.Work, ".remember", "MEMORY.md"), "# Project memory\n\n## Decisions\n\n- Postgres, not MySQL.\n");
        File.WriteAllText(Path.Combine(h.Work, ".remember", "architecture.md"), "# How the parts fit\n\nThe API and the worker.\n");
        var step = 0;
        _gateway.Answer = _ => ++step switch
        {
            1 => Reply.Call(("remember_project", """{"section":"How to build, test and run","text":"- `make check` runs the tests."}""")),
            _ => Reply.Say("kept"),
        };
        Assert.Equal(0, await h.Run("", "-p", "the tests run with make check", "--mode", "ask"));

        var system = FakeGateway.SystemOnly(_gateway.Requests[0]);
        Assert.Contains("## .remember/MEMORY.md, as this session started\n\n# Project memory\n\n## Decisions\n\n- Postgres, not MySQL.", system, StringComparison.Ordinal);
        Assert.Contains("- .remember/architecture.md (", system, StringComparison.Ordinal);
        Assert.Contains("How the parts fit", system, StringComparison.Ordinal);
        // Kept without asking, though the mode asks before edits: it is the project's memory.
        Assert.Contains("make check", File.ReadAllText(Path.Combine(h.Work, ".remember", "MEMORY.md")), StringComparison.Ordinal);

        Assert.Equal(0, await h.Run("", "-p", "what do you know?"));
        Assert.Contains("## How to build, test and run\n\n- `make check` runs the tests.", FakeGateway.SystemOnly(_gateway.Requests[^1]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_none_yet_the_session_is_told_how_to_start_it_and_with_it_off_nothing_is_said()
    {
        using (var h = new Harness(_gateway, _mcp, c => c["projectMemory"] = true))
        {
            _gateway.Answer = _ => Reply.Say("ok");
            Assert.Equal(0, await h.Run("", "-p", "hello"));
            Assert.Contains("This project has no long memory yet.", FakeGateway.SystemOnly(_gateway.Requests[^1]), StringComparison.Ordinal);
            Assert.Contains("remember_project", FakeGateway.ToolNames(_gateway.Requests[^1]));
        }
        using (var h = new Harness(_gateway, _mcp))
        {
            Assert.Equal(0, await h.Run("", "-p", "hello"));
            Assert.DoesNotContain("Project memory", FakeGateway.SystemOnly(_gateway.Requests[^1]), StringComparison.Ordinal);
            Assert.DoesNotContain("remember_project", FakeGateway.ToolNames(_gateway.Requests[^1]));
        }
    }
}
