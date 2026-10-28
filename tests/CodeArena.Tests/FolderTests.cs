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
    public async Task A_folder_added_to_the_workspace_is_listed_read_and_kept_and_taken_out_again()
    {
        using var h = new Harness(_gateway);
        h.Write("a.txt", "a\n");
        var lib = Directory.CreateDirectory(Path.Combine(h.Root, "lib")).FullName;
        File.WriteAllText(Path.Combine(lib, "util.py"), "def f():\n    return 1\n");
        var secret = Directory.CreateDirectory(Path.Combine(h.Root, "secret")).FullName;
        File.WriteAllText(Path.Combine(secret, "key.txt"), "no\n");
        var util = Path.Combine(lib, "util.py");

        await using (var web = await WebRun.StartAsync(h))
        {
            // Before it is added, a whole path is refused, as it always was.
            Assert.Equal(HttpStatusCode.Forbidden, (await web.Http.GetAsync($"/api/file?path={Uri.EscapeDataString(util)}")).StatusCode);
            // The folder open now, or one in it, is not added.
            Assert.Equal(HttpStatusCode.Conflict, (await web.PostAsync("/api/folders/add", new JsonObject { ["path"] = h.Work })).StatusCode);

            var added = await web.PostAsync("/api/folders/add", new JsonObject { ["path"] = "../lib" });
            Assert.Equal(HttpStatusCode.OK, added.StatusCode);
            Assert.Equal([lib], JsonNode.Parse(await added.Content.ReadAsStringAsync())!["added"]!.AsArray().Select(f => f!.GetValue<string>()));
            // Listed and read by its whole path; what is beside it is still refused.
            var listed = await web.GetJsonAsync($"/api/files?path={Uri.EscapeDataString(lib)}");
            Assert.Equal([util], listed["entries"]!.AsArray().Select(e => e!["path"]!.GetValue<string>()));
            Assert.Equal("def f():\n    return 1\n", (await web.GetJsonAsync($"/api/file?path={Uri.EscapeDataString(util)}"))["text"]!.GetValue<string>());
            Assert.Equal(HttpStatusCode.Forbidden, (await web.Http.GetAsync($"/api/file?path={Uri.EscapeDataString(Path.Combine(secret, "key.txt"))}")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await web.Http.GetAsync($"/api/file?path={Uri.EscapeDataString(lib + "/../secret/key.txt")}")).StatusCode);
            Assert.Equal([lib], (await web.GetJsonAsync("/api/folders"))["added"]!.AsArray().Select(f => f!.GetValue<string>()));
        }

        // Kept with this folder: the next run has it, and taken out, it is refused again.
        await using (var web = await WebRun.StartAsync(h))
        {
            Assert.Equal(HttpStatusCode.OK, (await web.Http.GetAsync($"/api/file?path={Uri.EscapeDataString(util)}")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await web.PostAsync("/api/folders/remove", new JsonObject { ["path"] = lib })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await web.Http.GetAsync($"/api/file?path={Uri.EscapeDataString(util)}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await web.PostAsync("/api/folders/remove", new JsonObject { ["path"] = lib })).StatusCode);
            Assert.Empty((await web.GetJsonAsync("/api/folders"))["added"]!.AsArray());
        }
    }

    [Fact]
    public async Task A_file_outside_named_whole_with_at_goes_with_the_message_and_the_tools_may_read_it_then()
    {
        using var h = new Harness(_gateway);
        var notes = Path.Combine(Directory.CreateDirectory(Path.Combine(h.Root, "elsewhere")).FullName, "notes.md");
        File.WriteAllText(notes, "# Plan\nship it\n");
        _gateway.Answer = r => FakeGateway.HasToolResults(r) ? Reply.Say("Read it.") : Reply.Call(("read_file", new JsonObject { ["path"] = notes }.ToJsonString()));
        await using var web = await WebRun.StartAsync(h, "--mode", "yolo");

        // Its listing for the @ in the chat: files too, with ?files=1.
        var listed = await web.GetJsonAsync($"/api/folders?path={Uri.EscapeDataString(Path.GetDirectoryName(notes)!)}&files=1");
        Assert.Equal(["notes.md"], listed["files"]!.AsArray().Select(f => f!.GetValue<string>()));

        await (await web.SendAsync($"Follow @{notes} please")).RestAsync();
        var first = _gateway.Requests[0];
        Assert.Contains($"<file path=\"{notes}\">", FakeGateway.Last(first));
        Assert.Contains("ship it", FakeGateway.Last(first));
        // The model's read_file of it then works: the person gave it.
        var result = _gateway.Requests[1]["messages"]!.AsArray().Last(m => m!["role"]!.GetValue<string>() == "tool")!["content"]!.GetValue<string>();
        Assert.Contains("ship it", result);
        // A path outside not named whole (relative, though there), or one not there, stays as typed.
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(notes)!, "other.md"), "other\n");
        await (await web.SendAsync("And @../elsewhere/other.md and @/no/such/file.md")).RestAsync();
        Assert.DoesNotContain("<attached>", FakeGateway.Last(_gateway.Requests[2]));
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
