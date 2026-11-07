using System.Net;
using System.Text.Json.Nodes;

namespace CodeArena.Tests;

/// <summary>VS Code's multi-root workspace file (.code-workspace) in the IDE: made, read, edited, opened and closed.</summary>
public sealed class WorkspaceFileTests : IDisposable
{
    private readonly FakeGateway _gateway = new();

    public void Dispose() => _gateway.Dispose();

    private static async Task<JsonNode> Json(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {text}");
        return JsonNode.Parse(text)!;
    }

    private static List<string> Added(JsonNode folders) => [.. folders["added"]!.AsArray().Select(f => f!.GetValue<string>())];

    [Fact]
    public async Task A_workspace_file_saved_lists_the_folders_and_its_edits_are_what_is_added_searched_and_set()
    {
        using var h = new Harness(_gateway);
        h.Write("app.py", "needle = 1\n");
        var lib = Directory.CreateDirectory(Path.Combine(h.Root, "lib")).FullName;
        File.WriteAllText(Path.Combine(lib, "util.py"), "needle = 2\n");
        var other = Directory.CreateDirectory(Path.Combine(h.Root, "other")).FullName;
        File.WriteAllText(Path.Combine(other, "x.py"), "needle = 3\n");
        await using var web = await WebRun.StartAsync(h);
        Assert.Null((await web.GetJsonAsync("/api/workspace-file"))["path"]);
        await Json(await web.PostAsync("/api/folders/add", new JsonObject { ["path"] = lib }));

        // Save Workspace As: the folder open and the added one, relative to the file.
        var file = Path.Combine(h.Root, "shop.code-workspace");
        var saved = await Json(await web.PostAsync("/api/workspace-file/save-as", new JsonObject { ["path"] = Path.Combine(h.Root, "shop") }));
        Assert.Equal(file, saved["path"]!.GetValue<string>());
        Assert.Equal("shop", saved["name"]!.GetValue<string>());
        var written = JsonNode.Parse(File.ReadAllText(file))!;
        Assert.Equal(["work", "lib"], written["folders"]!.AsArray().Select(f => f!["path"]!.GetValue<string>()));
        Assert.Equal(
            [(".", "work", true), (lib, "lib", false)],
            saved["folders"]!.AsArray().Select(f => (f!["path"]!.GetValue<string>(), f["name"]!.GetValue<string>(), f["root"]!.GetValue<bool>())));
        Assert.Equal(HttpStatusCode.Conflict, (await web.PostAsync("/api/workspace-file/save-as", new JsonObject { ["path"] = file })).StatusCode);
        // Search goes through the added folder too.
        var found = await web.GetJsonAsync("/api/search?q=needle");
        Assert.Equal(["app.py", Path.Combine(lib, "util.py")], found["files"]!.AsArray().Select(f => f!["path"]!.GetValue<string>()));

        // The page's edit, comments and all: lib out, other in with a name, a setting. What it names is what is added.
        var text = $$"""
            {
              // The shop and its tools.
              "folders": [
                { "path": "work" },
                { "path": "other", "name": "Tools" },
                { "path": "gone" },
              ],
              "settings": { "editor.tabSize": 2 },
            }
            """;
        var edited = await Json(await web.PostAsync("/api/workspace-file", new JsonObject { ["text"] = text, ["version"] = saved["version"]!.GetValue<string>() }));
        Assert.Contains("// The shop", File.ReadAllText(file));
        Assert.Equal(2, edited["settings"]!["editor.tabSize"]!.GetValue<int>());
        Assert.Equal("Tools", edited["folders"]![1]!["name"]!.GetValue<string>());
        Assert.False(edited["folders"]![2]!["exists"]!.GetValue<bool>());
        Assert.Contains(edited["problems"]!.AsArray(), p => p!.GetValue<string>().Contains("gone", StringComparison.Ordinal));
        Assert.Equal([other], Added(await web.GetJsonAsync("/api/folders")));
        Assert.Equal(HttpStatusCode.Forbidden, (await web.Http.GetAsync($"/api/file?path={Uri.EscapeDataString(Path.Combine(lib, "util.py"))}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await web.Http.GetAsync($"/api/file?path={Uri.EscapeDataString(Path.Combine(other, "x.py"))}")).StatusCode);

        // A stale version, or a text that is no workspace file, is refused and the file kept.
        Assert.Equal(HttpStatusCode.Conflict, (await web.PostAsync("/api/workspace-file", new JsonObject { ["text"] = text, ["version"] = saved["version"]!.GetValue<string>() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await web.PostAsync("/api/workspace-file", new JsonObject { ["text"] = "{ \"settings\": {} }" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await web.PostAsync("/api/workspace-file", new JsonObject { ["text"] = "{ not json" })).StatusCode);
        Assert.Contains("Tools", File.ReadAllText(file));

        // Edited on disk (by hand, by git): read again, it is what is added.
        File.WriteAllText(file, """{ "folders": [{ "path": "work" }, { "path": "lib" }] }""");
        Assert.Equal(lib, (await web.GetJsonAsync("/api/workspace-file"))["folders"]![1]!["path"]!.GetValue<string>());
        Assert.Equal([lib], Added(await web.GetJsonAsync("/api/folders")));
        // Opened lately: listed among the recent ones.
        Assert.Contains(file, (await web.GetJsonAsync("/api/folders"))["recent"]!.AsArray().Select(f => f!.GetValue<string>()));

        // Close Workspace: the folder alone again.
        await Json(await web.PostAsync("/api/workspace-file/close", new JsonObject()));
        Assert.Null((await web.GetJsonAsync("/api/workspace-file"))["path"]);
        Assert.Empty(Added(await web.GetJsonAsync("/api/folders")));

        // Opened again (it names the folder open): no new start, its folders added.
        var opened = await Json(await web.PostAsync("/api/workspace-file/open", new JsonObject { ["path"] = file }));
        Assert.False(opened["restart"]!.GetValue<bool>());
        Assert.Equal([lib], Added(await web.GetJsonAsync("/api/folders")));
        Assert.Equal(HttpStatusCode.BadRequest, (await web.PostAsync("/api/workspace-file/open", new JsonObject { ["path"] = Path.Combine(h.Work, "app.py") })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await web.PostAsync("/api/workspace-file/open", new JsonObject { ["path"] = Path.Combine(h.Root, "none.code-workspace") })).StatusCode);
    }

    [Fact]
    public async Task A_workspace_file_without_the_folder_open_starts_the_ide_on_its_first_folder_with_the_others_added()
    {
        using var h = new Harness(_gateway);
        var api = Directory.CreateDirectory(Path.Combine(h.Root, "api")).FullName;
        File.WriteAllText(Path.Combine(api, "main.go"), "package main\n");
        var web2 = Directory.CreateDirectory(Path.Combine(h.Root, "site")).FullName;
        var file = Path.Combine(h.Root, "both.code-workspace");
        File.WriteAllText(file, """{ "folders": [{ "path": "api" }, { "path": "site", "name": "Web" }], "settings": {} }""");
        await using var web = await WebRun.StartAsync(h);

        var opened = await Json(await web.PostAsync("/api/workspace-file/open", new JsonObject { ["path"] = file }));
        Assert.True(opened["restart"]!.GetValue<bool>());
        Assert.Equal(api, opened["folder"]!.GetValue<string>());
        JsonNode? files = null;
        for (var waited = 0; waited < 20_000 && files?["entries"]?.AsArray().Any(e => e!["name"]!.GetValue<string>() == "main.go") != true; waited += 100)
        {
            await Task.Delay(100);
            try
            {
                var response = await web.Http.GetAsync("/api/files");
                files = response.IsSuccessStatusCode ? JsonNode.Parse(await response.Content.ReadAsStringAsync()) : null;
            }
            catch (HttpRequestException)
            {
                // Between the two runs.
            }
        }
        Assert.Equal(["main.go"], files!["entries"]!.AsArray().Select(e => e!["name"]!.GetValue<string>()));
        var workspace = await web.GetJsonAsync("/api/workspace-file");
        Assert.Equal(file, workspace["path"]!.GetValue<string>());
        Assert.Equal([".", web2], workspace["folders"]!.AsArray().Select(f => f!["path"]!.GetValue<string>()));
        Assert.Equal([web2], Added(await web.GetJsonAsync("/api/folders")));
    }
}
