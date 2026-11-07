using System.Net;
using System.Text.Json.Nodes;

namespace CodeArena.Tests;

/// <summary>The project's .vscode/*.code-snippets and the person's own snippets files, as the IDE reads and writes them.</summary>
public sealed class SnippetsTests : IDisposable
{
    private readonly FakeGateway _gateway = new();

    public void Dispose() => _gateway.Dispose();

    [Fact]
    public async Task The_projects_and_the_persons_snippets_files_are_listed_whole_with_their_version()
    {
        using var h = new Harness(_gateway);
        h.Write(".vscode/team.code-snippets", "{ \"Header\": { \"prefix\": \"hdr\", \"body\": \"// x\" } }");
        h.Write(".vscode/settings.json", "{}");
        h.Write(".vscode/big.code-snippets", new string(' ', IdeSnippets.MaxBytes + 1));
        var lib = Directory.CreateDirectory(Path.Combine(h.Root, "lib")).FullName;
        Directory.CreateDirectory(Path.Combine(lib, ".vscode"));
        File.WriteAllText(Path.Combine(lib, ".vscode", "lib.code-snippets"), "{}");
        await using var web = await WebRun.StartAsync(h);
        Assert.Equal(HttpStatusCode.OK, (await web.PostAsync("/api/folders/add", new JsonObject { ["path"] = lib })).StatusCode);

        // The person's own: one per language, and any .code-snippets; made as their settings.json is.
        var saved = await web.PostAsync("/api/user-files", new JsonObject { ["name"] = "snippets/python.json", ["text"] = "{ \"Main\": { \"prefix\": \"main\", \"body\": \"x\" } }", ["version"] = "none" });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await web.PostAsync("/api/user-files", new JsonObject { ["name"] = "snippets/mine.code-snippets", ["text"] = "{}", ["version"] = "none" })).StatusCode);
        // Made already: "none" is refused, as a stale version is.
        Assert.Equal(HttpStatusCode.Conflict, (await web.PostAsync("/api/user-files", new JsonObject { ["name"] = "snippets/python.json", ["text"] = "{}", ["version"] = "none" })).StatusCode);
        // Nothing but a snippets file's name, in that folder.
        foreach (var name in new[] { "snippets/../settings.json", "snippets/a/b.json", "snippets/x.txt", "snippets/.json" })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await web.PostAsync("/api/user-files", new JsonObject { ["name"] = name, ["text"] = "{}" })).StatusCode);
        }
        Assert.True(File.Exists(Path.Combine(h.Paths.ConfigDir, "snippets", "python.json")));

        var list = await web.GetJsonAsync("/api/snippets");
        var files = list["files"]!.AsArray();
        Assert.Equal(
            [
                ("project", ".vscode/team.code-snippets", (string?)null),
                ("project", Path.Combine(lib, ".vscode", "lib.code-snippets"), null),
                ("user", "user:snippets/python.json", "python"),
                ("user", "user:snippets/mine.code-snippets", null),
            ],
            files.Select(f => (f!["source"]!.GetValue<string>(), f["path"]!.GetValue<string>(), f["language"]?.GetValue<string>())));
        Assert.Contains("\"hdr\"", files[0]!["text"]!.GetValue<string>());
        Assert.NotEqual("none", files[0]!["version"]!.GetValue<string>());
        // Too large: left out, and said so.
        Assert.Contains(list["problems"]!.AsArray(), p => p!.GetValue<string>().Contains("big.code-snippets", StringComparison.Ordinal));
    }
}
