using System.Text.Json.Nodes;

namespace CodeArena.Tests;

/// <summary>Code Arena's memory is Arena's: the files of the machine brought in once, the block at the start, what matters to each message, remember and recall.</summary>
public sealed class ArenaMemoryTests : IDisposable
{
    private readonly FakeGateway _gateway = new();
    private readonly FakeMcp _mcp = new();

    public void Dispose()
    {
        _gateway.Dispose();
        _mcp.Dispose();
    }

    private static string System(JsonObject request) => request["messages"]![0]!["content"]!.ToString();

    [Theory]
    [InlineData("git@gitlab.corp:payments/api.git", "gitlab.corp/payments/api")]
    [InlineData("https://ana@GitLab.Corp/payments/api", "gitlab.corp/payments/api")]
    [InlineData("ssh://git@gitlab.corp:2222/payments/api.git", "gitlab.corp/payments/api")]
    [InlineData("https://github.com/acme/tools.git/", "github.com/acme/tools")]
    [InlineData("not a remote", null)]
    public void A_projects_key_is_its_remote_without_who_or_how(string remote, string? key) => Assert.Equal(key, ProjectKeys.FromRemote(remote));

    [Fact]
    public async Task The_machines_memories_go_to_Arena_once_and_each_message_brings_what_matters_to_it()
    {
        using var h = new Harness(_gateway, _mcp);
        h.Write(".git/HEAD", "ref: refs/heads/main\n");
        h.Write(".git/config", "[core]\n\tbare = false\n[remote \"origin\"]\n\turl = git@gitlab.corp:payments/api.git\n");
        var store = new MemoryStore(h.Paths, h.Work);
        store.Add(person: true, "Prefers answers in British English");
        store.Add(person: false, "Build with make, test with make check");
        _gateway.Answer = _ => Reply.Say("ok");

        Assert.Equal(0, await h.Run("", "-p", "how do I build it?"));
        var memories = _mcp.Chats.Memories;
        Assert.Contains(memories, m => m["text"]!.GetValue<string>() == "Build with make, test with make check" && m["project"]!.GetValue<string>() == "gitlab.corp/payments/api");
        Assert.Contains(memories, m => m["text"]!.GetValue<string>() == "Prefers answers in British English" && m["scope"]!.GetValue<string>() == "person");
        // The files went to Arena (a one-shot run says only its answer), and are kept beside.
        Assert.False(File.Exists(store.ProjectFile));
        Assert.True(File.Exists(store.ProjectFile + ".imported"));
        var request = _gateway.Requests[^1];
        Assert.Contains("- Build with make, test with make check (this project's)", System(request));
        // What matters to the message, in Code Arena's note before it.
        var sent = request["messages"]!.AsArray().Select(m => m!["content"]!.ToString()).ToList();
        var asked = sent.FindLastIndex(t => t == "how do I build it?");
        Assert.Contains(sent.Take(asked), t => t.StartsWith(MemoryStore.RecallHeading, StringComparison.Ordinal) && t.Contains("make check", StringComparison.Ordinal));
        Assert.Equal("how do I build it?", _mcp.Chats.Recalls[^1].Query);

        // A second session: nothing to bring in, and the same memories.
        Assert.Equal(0, await h.Run("", "-p", "hello"));
        Assert.Equal(2, _mcp.Chats.Memories.Count);
    }

    [Fact]
    public async Task What_the_model_remembers_goes_to_Arena_with_its_kind_and_recall_finds_it()
    {
        using var h = new Harness(_gateway, _mcp);
        _gateway.Answer = req => FakeGateway.HasToolResults(req)
            ? Reply.Say("noted")
            : FakeGateway.Last(req) switch
            {
                "remember how to test" => Reply.Call(("remember", """{"memory":"Run the tests with make check","kind":"howto"}""")),
                _ => Reply.Call(("recall", """{"query":"tests check"}""")),
            };
        Assert.Equal(0, await h.Run("", "-p", "remember how to test"));
        var kept = Assert.Single(_mcp.Chats.Memories);
        Assert.Equal(("project", "howto"), (kept["scope"]!.GetValue<string>(), kept["kind"]!.GetValue<string>()));
        Assert.StartsWith("local/", kept["project"]!.GetValue<string>(), StringComparison.Ordinal);

        Assert.Equal(0, await h.Run("", "-p", "what do we know"));
        var result = _gateway.Requests[^1]["messages"]!.AsArray().Last(m => m!["role"]!.GetValue<string>() == "tool")!["content"]!.ToString();
        Assert.Equal("- Run the tests with make check (how to, this project's)", result);

        Assert.Equal(0, await h.Run("/memory\n/memory forget 1\n/exit\n", "chat"));
        Assert.Contains("1. Run the tests with make check", h.Out);
        Assert.Contains("Forgotten: Run the tests with make check", h.Out);
        Assert.Empty(_mcp.Chats.Memories);
    }

    [Fact]
    public async Task Without_Arenas_memory_the_machines_files_keep_it_and_go_to_Arena_later()
    {
        using var h = new Harness(_gateway, _mcp);
        _mcp.Chats.Memory = false;
        _gateway.Answer = req => FakeGateway.HasToolResults(req) ? Reply.Say("noted") : Reply.Call(("remember", """{"memory":"Run the tests with make check"}"""));
        Assert.Equal(0, await h.Run("", "-p", "remember how to test"));
        Assert.Contains("Run the tests with make check", File.ReadAllText(new MemoryStore(h.Paths, h.Work).ProjectFile));

        _mcp.Chats.Memory = true;
        _gateway.Answer = _ => Reply.Say("ok");
        Assert.Equal(0, await h.Run("", "-p", "hello"));
        Assert.Equal("Run the tests with make check", Assert.Single(_mcp.Chats.Memories)["text"]!.GetValue<string>());
    }
}
