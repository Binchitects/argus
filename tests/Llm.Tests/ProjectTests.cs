using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Llm.Tests;

/// <summary>Projects: chats together, with instructions and files every answer in them reads; only their owner's.</summary>
[Collection(nameof(AppCollection))]
public sealed class ProjectTests(AppFixture app)
{
    private async Task<TestBrowser> PersonAsync()
    {
        var admin = await new TestBrowser(app.Factory).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "pj" + Guid.NewGuid().ToString("N")[..8];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        return await new TestBrowser(app.Factory).SignedInAsync(name, made.GetProperty("password").GetString()!);
    }

    private static async Task<Guid> UploadAsync(TestBrowser b, string name, string text)
    {
        using var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(text));
        part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        form.Add(part, "file", name);
        return (await b.JsonAsync(await b.Http.PostAsync(new Uri("/api/chat/attachments", UriKind.Relative), form))).GetProperty("id").GetGuid();
    }

    private static async Task SendAsync(TestBrowser b, Guid chat, string text)
    {
        var res = await b.PostAsync($"/api/chat/conversations/{chat}/messages", new { content = text });
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        await res.Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task A_project_gives_its_chats_its_instructions_and_files_and_only_its_owner_sees_it()
    {
        var b = await PersonAsync();
        var other = await PersonAsync();
        await StatusAssert.Is(HttpStatusCode.BadRequest, await b.PostAsync("/api/projects", new { name = " " }));
        var created = await b.PostAsync("/api/projects", new { name = "Codec rewrite", description = "The new decoder", instructions = "Answer as a codec engineer." });
        await StatusAssert.Is(HttpStatusCode.Created, created);
        var id = (await b.JsonAsync(created)).GetProperty("id").GetGuid();
        var facts = await UploadAsync(b, "facts.txt", "The frame header is 12 bytes.\n");
        await StatusAssert.Is(HttpStatusCode.NoContent, await b.PostAsync($"/api/projects/{id}/files", new { attachmentId = facts }));

        // A chat in the project: the answer reads the project's instructions and its file.
        var chat = (await b.JsonAsync(await b.PostAsync("/api/chat/conversations", new { projectId = id, tools = new[] { "files" } }))).GetProperty("id").GetGuid();
        await SendAsync(b, chat, "How big is a frame header?");
        var system = app.Model.Requests.Last().Body["messages"]![0]!["content"]!.GetValue<string>();
        Assert.Contains("project \"Codec rewrite\". The project's instructions:\nAnswer as a codec engineer.", system, StringComparison.Ordinal);
        Assert.Contains("<project_file name=\"facts.txt\">\nThe frame header is 12 bytes.", system, StringComparison.Ordinal);
        Assert.Equal("Codec rewrite", (await b.JsonAsync(await b.GetAsync($"/api/chat/conversations/{chat}"))).GetProperty("project").GetProperty("name").GetString());

        // Its tools have the file too.
        await SendAsync(b, chat, "Read it [call read_file {\"file\":\"facts.txt\"}]");
        var tool = app.Model.Requests.Last().Body["messages"]!.AsArray().Last(m => m!["role"]!.GetValue<string>() == "tool")!["content"]!.GetValue<string>();
        Assert.Contains("12 bytes", tool, StringComparison.Ordinal);

        var listed = (await b.JsonAsync(await b.GetAsync("/api/projects"))).EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == id);
        Assert.Equal(1, listed.GetProperty("chats").GetInt32());
        Assert.Equal(1, listed.GetProperty("files").GetInt32());
        var full = await b.JsonAsync(await b.GetAsync($"/api/projects/{id}"));
        Assert.Equal("facts.txt", full.GetProperty("files")[0].GetProperty("fileName").GetString());
        Assert.Equal(chat, full.GetProperty("chats")[0].GetProperty("id").GetGuid());
        Assert.Equal([chat], (await b.JsonAsync(await b.GetAsync($"/api/chat/conversations?project={id}"))).EnumerateArray().Select(c => c.GetProperty("id").GetGuid()));

        // Someone else: nothing to see, change or use.
        await StatusAssert.Is(HttpStatusCode.NotFound, await other.GetAsync($"/api/projects/{id}"));
        await StatusAssert.Is(HttpStatusCode.NotFound, await other.Http.PatchAsJsonAsync(new Uri($"/api/projects/{id}", UriKind.Relative), new { name = "mine" }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await other.PostAsync("/api/chat/conversations", new { projectId = id }));
        Assert.Empty((await other.JsonAsync(await other.GetAsync("/api/projects"))).EnumerateArray());
        var theirs = await UploadAsync(other, "x.txt", "x");
        await StatusAssert.Is(HttpStatusCode.BadRequest, await b.PostAsync($"/api/projects/{id}/files", new { attachmentId = theirs }));

        // Out of the project: the next answer reads neither.
        await StatusAssert.Is(HttpStatusCode.NoContent, await b.Http.PatchAsJsonAsync(new Uri($"/api/chat/conversations/{chat}", UriKind.Relative), new { projectId = Guid.Empty }));
        await SendAsync(b, chat, "And now?");
        Assert.DoesNotContain("Codec rewrite", app.Model.Requests.Last().Body["messages"]![0]!["content"]!.GetValue<string>(), StringComparison.Ordinal);

        // Removed: its chats stay, out of any project, unless they go too.
        await b.Http.PatchAsJsonAsync(new Uri($"/api/chat/conversations/{chat}", UriKind.Relative), new { projectId = id });
        await StatusAssert.Is(HttpStatusCode.NoContent, await b.Http.DeleteAsync(new Uri($"/api/projects/{id}", UriKind.Relative)));
        Assert.Equal(JsonValueKind.Null, (await b.JsonAsync(await b.GetAsync($"/api/chat/conversations/{chat}"))).GetProperty("project").ValueKind);
        var second = (await b.JsonAsync(await b.PostAsync("/api/projects", new { name = "Short lived" }))).GetProperty("id").GetGuid();
        var doomed = (await b.JsonAsync(await b.PostAsync("/api/chat/conversations", new { projectId = second }))).GetProperty("id").GetGuid();
        await StatusAssert.Is(HttpStatusCode.NoContent, await b.Http.DeleteAsync(new Uri($"/api/projects/{second}?chats=delete", UriKind.Relative)));
        await StatusAssert.Is(HttpStatusCode.NotFound, await b.GetAsync($"/api/chat/conversations/{doomed}"));
    }
}
