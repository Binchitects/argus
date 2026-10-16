using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;

namespace CodeArena.Tests;

/// <summary>
/// An MCP tool call cannot hang a turn for ever: it has a deadline (counted anew from each progress report), the model is
/// told it timed out, and the server is told it was given up. The pictures, sound and video a tool returns are saved in
/// the workspace, and the IDE shows them.
/// </summary>
public sealed class HangToolCallTests : IDisposable
{
    private readonly FakeMcp _mcp = new();
    private readonly HttpClient _http = new();
    private readonly string _work = Directory.CreateTempSubdirectory("arena-media-").FullName;

    private static Dictionary<string, string> Auth => new() { ["Authorization"] = "Bearer " + FakeGateway.Key };

    public void Dispose()
    {
        _mcp.CallHold?.TrySetResult();
        _mcp.Dispose();
        _http.Dispose();
        Directory.Delete(_work, recursive: true);
    }

    private Task<McpClient> ConnectAsync() => McpClient.ConnectAsync("arena", new HttpMcpTransport(_http, _mcp.Url, Auth), CancellationToken.None);

    [Fact]
    public async Task A_tool_that_never_answers_times_out_the_model_is_told_and_the_server_hears_it_was_given_up()
    {
        await using var client = await ConnectAsync();
        _mcp.CallHold = new TaskCompletionSource();
        var clock = Stopwatch.StartNew();
        var call = client.CallAsync("web_search", new JsonObject { ["query"] = "q" }, CancellationToken.None, limit: TimeSpan.FromSeconds(1));
        Assert.True(await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(15))) == call, "the call did not end");
        var result = await call;
        Assert.True(result.IsError);
        Assert.StartsWith("The call to web_search timed out: the arena tools did not answer within 1s", result.Text);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"{clock.ElapsedMilliseconds} ms");
        Assert.True(await Until(() => _mcp.Calls.Any(c => c.Method == "notifications/cancelled")), "the server was not told");
    }

    [Fact]
    public async Task Progress_reports_keep_a_long_call_going_and_are_told_to_the_person()
    {
        _mcp.Sse = true;
        _mcp.CallProgress = 8;
        _mcp.ProgressGap = TimeSpan.FromMilliseconds(300);
        await using var client = await ConnectAsync();
        var said = new List<string>();
        var before = McpClient.ProgressEvery;
        McpClient.ProgressEvery = TimeSpan.Zero;
        try
        {
            // 2.4 s in all, never more than 1 s without a report.
            var result = await client.CallAsync("web_search", new JsonObject { ["query"] = "slow" }, CancellationToken.None, limit: TimeSpan.FromSeconds(1),
                progress: s => { lock (said) { said.Add(s); } });
            Assert.False(result.IsError, result.Text);
            Assert.Equal("Results for slow: https://arena.example/answer", result.Text);
        }
        finally
        {
            McpClient.ProgressEvery = before;
        }
        Assert.Contains("web_search is still working: 8 of 8, sampling.", said);
    }

    [Fact]
    public void A_tool_may_say_it_takes_longer()
    {
        Assert.Equal(McpClient.CallLimit, McpClient.LimitOf(new JsonObject { ["name"] = "web_search" }));
        Assert.Equal(TimeSpan.FromMinutes(30), McpClient.LimitOf(new JsonObject { ["name"] = "generate_video", ["_meta"] = new JsonObject { ["arena/timeoutSeconds"] = 1800 } }));
        Assert.Equal(TimeSpan.FromSeconds(10), McpClient.LimitOf(new JsonObject { ["_meta"] = new JsonObject { ["timeoutSeconds"] = 1 } }));
    }

    [Fact]
    public async Task Pictures_and_sound_a_tool_returns_are_saved_in_the_workspace_and_the_model_gets_their_paths()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3 };
        var wav = new byte[] { 0x52, 0x49, 0x46, 0x46, 9, 9, 9, 9 };
        _mcp.CallContent = new JsonArray(
            new JsonObject { ["type"] = "text", ["text"] = "Made a picture and read it aloud." },
            new JsonObject { ["type"] = "image", ["data"] = Convert.ToBase64String(png), ["mimeType"] = "image/png" },
            // The same picture's file: not fetched again.
            new JsonObject { ["type"] = "resource_link", ["uri"] = "https://arena.example/api/chat/attachments/1/content", ["name"] = "picture.png", ["mimeType"] = "image/png" },
            new JsonObject { ["type"] = "resource_link", ["uri"] = "https://arena.example/api/chat/attachments/2/content", ["name"] = "speech.wav", ["mimeType"] = "audio/wav", ["size"] = wav.Length },
            new JsonObject { ["type"] = "resource_link", ["uri"] = "https://arena.example/api/chat/attachments/3/content", ["name"] = "notes.pdf", ["mimeType"] = "application/pdf" });
        _mcp.Resources["https://arena.example/api/chat/attachments/2/content"] = ("audio/wav", wav);
        await using var client = await ConnectAsync();

        var result = await client.CallAsync("generate_image", [], CancellationToken.None, media: McpMedia.For(_work));

        Assert.False(result.IsError);
        Assert.Equal(2, result.Saved.Count);
        Assert.All(result.Saved, p => Assert.Matches(@"^\.code-arena/media/generate_image-\d{8}-\d{6}(-\d+)?\.(png|wav)$", p));
        Assert.Equal(png, File.ReadAllBytes(Path.Combine(_work, result.Saved.Single(p => p.EndsWith(".png", StringComparison.Ordinal)))));
        Assert.Equal(wav, File.ReadAllBytes(Path.Combine(_work, result.Saved.Single(p => p.EndsWith(".wav", StringComparison.Ordinal)))));
        Assert.Contains($"[image saved as {result.Saved[0]} (image/png, 1 KB)]", result.Text);
        Assert.Contains($"[speech.wav saved as {result.Saved[1]} (audio/wav, 1 KB)]", result.Text);
        Assert.Contains("[notes.pdf: https://arena.example/api/chat/attachments/3/content]", result.Text);
        Assert.Contains("Give the person these paths", result.Text);
        // The link of the picture saved already was not read; the sound's was.
        Assert.Single(_mcp.Calls, c => c.Method == "resources/read");
        // Never committed with the turn's changes: the folder ignores itself.
        Assert.Equal("*", File.ReadAllLines(Path.Combine(_work, ".code-arena", "media", ".gitignore")).Last());
    }

    [Fact]
    public async Task The_IDE_serves_a_picture_as_it_is_and_nothing_that_could_run()
    {
        using var gateway = new FakeGateway();
        using var h = new Harness(gateway, _mcp);
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 7, 7, 7 };
        Directory.CreateDirectory(Path.Combine(h.Work, ".code-arena", "media"));
        File.WriteAllBytes(Path.Combine(h.Work, ".code-arena", "media", "x.png"), png);
        h.Write("logo.svg", "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>");
        h.Write("notes.txt", "text");
        await using var web = await WebRun.StartAsync(h);

        var res = await web.Http.GetAsync("/api/media?path=.code-arena/media/x.png");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("image/png", res.Content.Headers.ContentType!.MediaType);
        Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal(png, await res.Content.ReadAsByteArrayAsync());

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await web.Http.GetAsync("/api/media?path=logo.svg")).StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await web.Http.GetAsync("/api/media?path=notes.txt")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await web.Http.GetAsync("/api/media?path=../outside.png")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await web.Http.GetAsync("/api/media?path=missing.png")).StatusCode);
        // Without the run's key: refused.
        using var stranger = WebRun.Client(web.Port, null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await stranger.GetAsync("/api/media?path=.code-arena/media/x.png")).StatusCode);
    }

    [Fact]
    public void Connections_have_TCP_keep_alive()
    {
        using var socket = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
        Net.KeepAlive(socket);
        Assert.Equal(1, (int)socket.GetSocketOption(System.Net.Sockets.SocketOptionLevel.Socket, System.Net.Sockets.SocketOptionName.KeepAlive)!);
        if (OperatingSystem.IsLinux())
        {
            Assert.Equal(Net.KeepAliveIdle, (int)socket.GetSocketOption(System.Net.Sockets.SocketOptionLevel.Tcp, System.Net.Sockets.SocketOptionName.TcpKeepAliveTime)!);
        }
    }

    private static async Task<bool> Until(Func<bool> done)
    {
        var clock = Stopwatch.StartNew();
        while (!done())
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(10))
            {
                return false;
            }
            await Task.Delay(50);
        }
        return true;
    }
}
