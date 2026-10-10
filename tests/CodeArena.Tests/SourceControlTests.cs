using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;

namespace CodeArena.Tests;

/// <summary>The IDE's Source Control: the folder's git as the person works it.</summary>
public sealed class SourceControlTests : IDisposable
{
    private readonly FakeGateway _gateway = new();
    private readonly FakeMcp _mcp = new();

    public void Dispose()
    {
        _gateway.Dispose();
        _mcp.Dispose();
    }

    private static void Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"git {string.Join(' ', args)}: {p.StandardError.ReadToEnd()}");
    }

    private static Dictionary<string, (string? Staged, string? Changed)> Files(JsonNode status) =>
        status["files"]!.AsArray().ToDictionary(f => f!["path"]!.GetValue<string>(), f => (f!["staged"]?.GetValue<string>(), f["changed"]?.GetValue<string>()));

    [Fact]
    public async Task What_changed_since_the_last_commit_is_staged_unstaged_discarded_and_committed()
    {
        using var h = new Harness(_gateway, _mcp, c => c["autoCommit"] = false);
        Git(h.Work, "init", "-q", "-b", "main");
        Git(h.Work, "config", "user.name", "Pat Person");
        Git(h.Work, "config", "user.email", "pat@example.test");
        h.Write("src/app.ts", "const total = 1\n");
        h.Write("README.md", "# Shop\n");
        Git(h.Work, "add", "-A");
        Git(h.Work, "commit", "-q", "-m", "First");
        h.Write("src/app.ts", "const total = 2\n");
        h.Write("notes.txt", "new\n");
        await using var web = await WebRun.StartAsync(h);

        var status = await web.GetJsonAsync("/api/git/status");
        Assert.Equal("main", status["branch"]!.GetValue<string>());
        Assert.Equal(new Dictionary<string, (string?, string?)> { ["src/app.ts"] = (null, "M"), ["notes.txt"] = (null, "?") }, Files(status));

        // Before and now, for the diff tab.
        var texts = await web.GetJsonAsync("/api/git/diff?path=src%2Fapp.ts");
        Assert.Equal("const total = 1\n", texts["original"]!.GetValue<string>());
        Assert.Equal("const total = 2\n", texts["modified"]!.GetValue<string>());
        Assert.Null((await web.GetJsonAsync("/api/git/diff?path=notes.txt"))["original"]);

        // Staged, then not.
        var staged = JsonNode.Parse(await (await web.PostAsync("/api/git/stage", new JsonObject { ["paths"] = new JsonArray("src/app.ts") })).Content.ReadAsStringAsync())!;
        Assert.Equal(("M", (string?)null), Files(staged)["src/app.ts"]);
        var unstaged = JsonNode.Parse(await (await web.PostAsync("/api/git/unstage", new JsonObject { ["paths"] = new JsonArray("src/app.ts") })).Content.ReadAsStringAsync())!;
        Assert.Equal(((string?)null, "M"), Files(unstaged)["src/app.ts"]);

        // Discarded: a file git does not know is deleted.
        await web.PostAsync("/api/git/discard", new JsonObject { ["paths"] = new JsonArray("notes.txt") });
        Assert.False(File.Exists(Path.Combine(h.Work, "notes.txt")));

        // Nothing staged: said so; all of it: committed, by the person.
        var refused = await web.PostAsync("/api/git/commit", new JsonObject { ["message"] = "Two" });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var committed = await web.PostAsync("/api/git/commit", new JsonObject { ["message"] = "Total is two", ["all"] = true });
        Assert.Equal(HttpStatusCode.OK, committed.StatusCode);
        Assert.Empty(JsonNode.Parse(await committed.Content.ReadAsStringAsync())!["status"]!["files"]!.AsArray());
        var log = (await web.GetJsonAsync("/api/git/log")).AsArray();
        Assert.Equal(["Total is two", "First"], log.Select(c => c!["subject"]!.GetValue<string>()));
        Assert.Equal("Pat Person", log[0]!["author"]!.GetValue<string>());

        // Branches, and switching.
        Git(h.Work, "branch", "feature");
        Assert.Equal(["main", "feature"], (await web.GetJsonAsync("/api/git/branches")).AsArray().Select(b => b!["name"]!.GetValue<string>()));
        var switched = JsonNode.Parse(await (await web.PostAsync("/api/git/switch", new JsonObject { ["branch"] = "feature" })).Content.ReadAsStringAsync())!;
        Assert.Equal("feature", switched["branch"]!.GetValue<string>());

        // Outside the folder: refused.
        Assert.Equal(HttpStatusCode.Forbidden, (await web.PostAsync("/api/git/stage", new JsonObject { ["paths"] = new JsonArray("../x") })).StatusCode);
    }

    [Fact]
    public async Task A_folder_in_no_repository_says_so()
    {
        using var h = new Harness(_gateway, _mcp);
        await using var web = await WebRun.StartAsync(h);
        Assert.False((await web.GetJsonAsync("/api/git/status"))["repository"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.NotFound, (await web.PostAsync("/api/git/commit", new JsonObject { ["message"] = "x" })).StatusCode);
    }
}
