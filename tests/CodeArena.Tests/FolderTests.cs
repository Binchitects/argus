using System.Net;
using System.Text.Json.Nodes;

namespace CodeArena.Tests;

/// <summary>Open Folder: the IDE started again on another folder, at the same address with the same key.</summary>
public sealed class FolderTests : IDisposable
{
    private readonly FakeGateway _gateway = new();

    public void Dispose() => _gateway.Dispose();

    [Fact]
    public async Task Open_folder_starts_the_ide_again_on_that_folder_at_the_same_address()
    {
        using var h = new Harness(_gateway);
        h.Write("a.txt", "a\n");
        var other = Directory.CreateDirectory(Path.Combine(h.Root, "other")).FullName;
        File.WriteAllText(Path.Combine(other, "b.txt"), "b\n");
        Directory.CreateDirectory(Path.Combine(h.Root, ".hidden"));
        await using var web = await WebRun.StartAsync(h);

        // The picker: the folder's folders by name (hidden ones last, no files), its parent, and the folder open now as recent.
        var listed = await web.GetJsonAsync($"/api/folders?path={Uri.EscapeDataString(h.Root)}");
        var names = listed["folders"]!.AsArray().Select(f => f!.GetValue<string>()).ToList();
        Assert.Equal(["config", "data", "other", "work", ".hidden"], names.Where(n => n is "config" or "data" or "other" or "work" or ".hidden"));
        Assert.Equal(h.Work, listed["current"]!.GetValue<string>());
        Assert.Equal(h.Work, listed["recent"]![0]!.GetValue<string>());
        Assert.Equal(Directory.GetParent(h.Root)!.FullName, listed["parent"]!.GetValue<string>());
        // With no path, the open folder's parent.
        Assert.Equal(h.Root, (await web.GetJsonAsync("/api/folders"))["path"]!.GetValue<string>());

        // A file, or nothing there, is not a folder to open.
        Assert.Equal(HttpStatusCode.NotFound, (await web.PostAsync("/api/folders/open", new JsonObject { ["path"] = Path.Combine(other, "b.txt") })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await web.PostAsync("/api/folders/open", new JsonObject { ["path"] = " " })).StatusCode);

        // Relative to the folder open now; answered with the full path, then the IDE starts again there.
        var opened = await web.PostAsync("/api/folders/open", new JsonObject { ["path"] = "../other" });
        Assert.Equal(HttpStatusCode.OK, opened.StatusCode);
        Assert.Equal(other, JsonNode.Parse(await opened.Content.ReadAsStringAsync())!["folder"]!.GetValue<string>());
        JsonNode? files = null;
        for (var waited = 0; waited < 20_000 && files?["entries"]?.AsArray().Any(e => e!["name"]!.GetValue<string>() == "b.txt") != true; waited += 100)
        {
            await Task.Delay(100);
            try
            {
                var response = await web.Http.GetAsync("/api/files");
                files = response.IsSuccessStatusCode ? JsonNode.Parse(await response.Content.ReadAsStringAsync()) : null;
            }
            catch (HttpRequestException)
            {
                // Between the two: not listening for a moment.
            }
        }
        Assert.Equal(["b.txt"], files!["entries"]!.AsArray().Select(e => e!["name"]!.GetValue<string>()));
        Assert.Contains($"Opening {other}.", web.Err + web.Out);
        Assert.Contains($"the IDE for {other}", web.Out);
        // The folder opened now is the first recent one.
        var recent = (await web.GetJsonAsync("/api/folders"))["recent"]!.AsArray().Select(f => f!.GetValue<string>()).ToList();
        Assert.Equal([other, h.Work], recent.Take(2));
    }

    [Fact]
    public async Task A_workspace_served_for_code_studio_does_not_open_another_folder()
    {
        using var h = new Harness(_gateway);
        await using var web = await WebRun.StartServedAsync(h, new string('k', 40));
        var refused = await web.PostAsync("/api/folders/open", new JsonObject { ["path"] = h.Root });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("Code Studio", await refused.Content.ReadAsStringAsync());
    }
}
