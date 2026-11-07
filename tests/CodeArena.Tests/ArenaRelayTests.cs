using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace CodeArena.Tests;

/// <summary>The IDE's page reaching the person's files in Arena through code-arena, which adds the key: only those routes, only after its own checks.</summary>
public sealed class ArenaRelayTests : IDisposable
{
    private readonly FakeGateway _gateway = new();
    private readonly FakeMcp _mcp = new();

    public void Dispose()
    {
        _gateway.Dispose();
        _mcp.Dispose();
    }

    private static MultipartFormDataContent Form(string name, string text)
    {
        var form = new MultipartFormDataContent();
        var part = new StringContent(text);
        part.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        form.Add(part, "file", name);
        return form;
    }

    [Fact]
    public async Task A_file_goes_to_Arena_with_the_key_and_comes_back_as_Arena_gives_it()
    {
        using var h = new Harness(_gateway, _mcp);
        await using var web = await WebRun.StartAsync(h);

        using var form = Form("notes.txt", "remember the milk");
        var made = await web.Http.PostAsync("/api/chat/attachments", form);
        Assert.Equal(HttpStatusCode.OK, made.StatusCode);
        var file = JsonNode.Parse(await made.Content.ReadAsStringAsync())!;
        Assert.Equal("notes.txt", file["fileName"]!.GetValue<string>());
        var sent = Assert.Single(_mcp.Chats.Relayed);
        // With the person's key, never this run's token the page holds.
        Assert.Equal("Bearer " + FakeGateway.Key, sent.Authorization);
        Assert.StartsWith("multipart/form-data", sent.Type);
        Assert.Contains("remember the milk", sent.Body);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/chat/attachments/{file["id"]}/content?download=1&secret=no");
        request.Headers.Range = new RangeHeaderValue(0, 3);
        var content = await web.Http.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, content.StatusCode);
        Assert.Contains("remember the milk", await content.Content.ReadAsStringAsync());
        Assert.Equal("inline", content.Content.Headers.ContentDisposition!.ToString());
        Assert.False(content.Headers.Contains("X-Not-Passed"));
        // Only the query Arena's route reads, and the range a player asks for.
        Assert.Equal(($"/api/chat/attachments/{file["id"]}/content?download=1", "bytes=0-3"), (_mcp.Chats.Relayed[^1].Path, _mcp.Chats.Relayed[^1].Range));
        Assert.Equal(HttpStatusCode.NotFound, (await web.Http.GetAsync($"/api/chat/attachments/{Guid.NewGuid()}/content")).StatusCode);
    }

    [Fact]
    public async Task Only_the_files_routes_are_relayed_a_form_only_to_the_upload_and_nothing_from_another_site()
    {
        using var h = new Harness(_gateway, _mcp);
        await using var web = await WebRun.StartAsync(h);

        // A form anywhere else is refused, as before.
        using (var form = Form("a.txt", "x"))
        {
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await web.Http.PostAsync("/api/messages", form)).StatusCode);
        }
        // The upload takes a form only.
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await web.Http.PostAsync("/api/chat/attachments", JsonContent("{}"))).StatusCode);
        // The chat's other routes are not Arena's here.
        Assert.NotEqual(HttpStatusCode.OK, (await web.Http.GetAsync("/api/chat/conversations")).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await web.Http.GetAsync("/api/chat/attachments/../conversations")).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await web.Http.GetAsync("/api/chat/attachments/not-an-id/content")).StatusCode);
        // Another site's page, or a request without this run's token, gets nowhere near Arena.
        using (var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat/attachments") { Content = Form("a.txt", "x") })
        {
            request.Headers.Add("Sec-Fetch-Site", "cross-site");
            Assert.Equal(HttpStatusCode.Forbidden, (await web.Http.SendAsync(request)).StatusCode);
        }
        using var anonymous = WebRun.Client(web.Port, token: null);
        using (var form = Form("a.txt", "x"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/chat/attachments", form)).StatusCode);
        }
        Assert.Empty(_mcp.Chats.Relayed);
    }

    [Fact]
    public async Task The_IDE_reaches_Arenas_projects_with_the_key_and_only_their_routes()
    {
        _mcp.Chats.AddProject("8f1c2d3e-0000-4000-8000-000000000001", "Payments", "owner", ("Card payments", "doing"));
        _mcp.Chats.AddProject("8f1c2d3e-0000-4000-8000-000000000002", "Handbook", "viewer");
        using var h = new Harness(_gateway, _mcp);
        await using var web = await WebRun.StartAsync(h);

        // The web's own routes and shapes, as Arena gives them: the list, a project, its report in a window, what may be made.
        var list = JsonNode.Parse(await (await web.Http.GetAsync("/api/projects")).Content.ReadAsStringAsync())!.AsArray();
        Assert.Equal(["Payments", "Handbook"], list.Select(p => p!["name"]!.GetValue<string>()));
        var one = await web.GetJsonAsync("/api/projects/8f1c2d3e-0000-4000-8000-000000000001");
        Assert.Equal("Card payments", one["goals"]![0]!["title"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.OK, (await web.Http.GetAsync("/api/projects/8f1c2d3e-0000-4000-8000-000000000001/report?from=2026-11-01T00:00:00Z&to=2026-11-07T00:00:00Z&secret=no")).StatusCode);
        Assert.Equal("/api/projects/8f1c2d3e-0000-4000-8000-000000000001/report?from=2026-11-01T00%3A00%3A00Z&to=2026-11-07T00%3A00%3A00Z", _mcp.Chats.Relayed[^1].Path);
        Assert.True((await web.GetJsonAsync("/api/projects/may"))["create"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.OK, (await web.Http.GetAsync("/api/sharing/groups")).StatusCode);

        // A change goes as JSON with the key; Arena's answer comes back as it is, a refusal too (Arena decides, not code-arena).
        var added = await web.Http.PostAsync("/api/projects/8f1c2d3e-0000-4000-8000-000000000001/goals", JsonContent("""{"title":"Refunds"}"""));
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        Assert.Equal(("POST", "Bearer " + FakeGateway.Key, """{"title":"Refunds"}"""), (_mcp.Chats.Relayed[^1].Method, _mcp.Chats.Relayed[^1].Authorization, _mcp.Chats.Relayed[^1].Body));
        var refused = await web.Http.PostAsync("/api/projects/8f1c2d3e-0000-4000-8000-000000000002/goals", JsonContent("""{"title":"Mine"}"""));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("Viewers see the project", await refused.Content.ReadAsStringAsync());

        // Only JSON, and only the projects' routes: a form, another of Arena's routes, a path that only looks like one.
        var relayed = _mcp.Chats.Relayed.Count;
        using (var form = Form("a.txt", "x"))
        {
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await web.Http.PostAsync("/api/projects/8f1c2d3e-0000-4000-8000-000000000001/goals", form)).StatusCode);
        }
        Assert.NotEqual(HttpStatusCode.OK, (await web.Http.GetAsync("/api/projects/8f1c2d3e-0000-4000-8000-000000000001/../../admin/people")).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await web.Http.GetAsync("/api/projects/not-an-id")).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await web.Http.PostAsync("/api/sharing/groups", JsonContent("{}"))).StatusCode);
        using (var request = new HttpRequestMessage(HttpMethod.Get, "/api/projects"))
        {
            request.Headers.Add("Sec-Fetch-Site", "cross-site");
            Assert.Equal(HttpStatusCode.Forbidden, (await web.Http.SendAsync(request)).StatusCode);
        }
        using var anonymous = WebRun.Client(web.Port, token: null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/projects")).StatusCode);
        Assert.Equal(relayed, _mcp.Chats.Relayed.Count);
    }

    private static StringContent JsonContent(string json) => new(json, System.Text.Encoding.UTF8, "application/json");
}
