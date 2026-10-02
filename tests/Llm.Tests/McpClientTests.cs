using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Llm.Api.Chat;

namespace Llm.Tests;

/// <summary>
/// The chat's MCP client with calls that take long: progress on the way, a server
/// that pings, a stream that breaks and is resumed, a call past its limit.
/// </summary>
public sealed class McpClientTests
{
    private static readonly Uri Url = new("https://tools.example.test/mcp");

    [Fact]
    public async Task A_long_call_reports_its_progress_answers_pings_and_ends_with_its_answer()
    {
        var server = new ScriptedMcp((call, token) => Sse(
            Progress(token, 1, 3, "Cloning the repository"),
            """{"jsonrpc":"2.0","id":"ping-1","method":"ping"}""",
            Progress("someone-else", 9, 9, "not ours"),
            Progress(token, 2, 3, "Building"),
            Result(call, "built")));
        var session = await Mcp.ConnectAsync(new HttpClient(server), Url, new Dictionary<string, string>(), "Build Desk", CancellationToken.None);
        var seen = new List<McpProgress>();

        var (text, isError) = await session.CallAsync("build", [], p => { seen.Add(p); return Task.CompletedTask; }, CancellationToken.None);

        Assert.Equal("built", text);
        Assert.False(isError);
        Assert.Equal([new McpProgress(1, 3, "Cloning the repository"), new McpProgress(2, 3, "Building")], seen);
        var pong = Assert.Single(server.Posts, p => p["id"]?.ToString() == "ping-1");
        Assert.NotNull(pong["result"]);
    }

    [Fact]
    public async Task A_stream_that_breaks_is_resumed_from_its_last_event()
    {
        var server = new ScriptedMcp((call, token) => Broken(Sse(("7", Progress(token, 1, 2, "Halfway")))));
        server.Resume = (call, lastEvent) => lastEvent == "7" ? Sse(("8", Result(call, "done after all"))) : throw new InvalidOperationException(lastEvent);
        var session = await Mcp.ConnectAsync(new HttpClient(server), Url, new Dictionary<string, string>(), "Build Desk", CancellationToken.None);

        var (text, _) = await session.CallAsync("build", [], null, CancellationToken.None);

        Assert.Equal("done after all", text);
        Assert.Equal(["7"], server.Resumed);
    }

    [Fact]
    public async Task A_call_past_its_limit_fails_and_the_server_is_told_to_stop()
    {
        var server = new ScriptedMcp((_, _) => Endless());
        var session = await Mcp.ConnectAsync(new HttpClient(server), Url, new Dictionary<string, string>(), "Build Desk", CancellationToken.None,
            callTimeout: TimeSpan.FromSeconds(1));

        var error = await Assert.ThrowsAsync<McpException>(() => session.CallAsync("build", [], null, CancellationToken.None));

        Assert.Contains("did not finish within 1 seconds", error.Message, StringComparison.Ordinal);
        var call = server.Posts.Single(p => p["method"]?.GetValue<string>() == "tools/call");
        var cancelled = Assert.Single(server.Posts, p => p["method"]?.GetValue<string>() == "notifications/cancelled");
        Assert.Equal(call["id"]!.GetValue<int>(), cancelled["params"]!["requestId"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_call_stopped_by_the_person_is_cancelled_at_the_server_too()
    {
        var server = new ScriptedMcp((_, _) => Endless());
        var session = await Mcp.ConnectAsync(new HttpClient(server), Url, new Dictionary<string, string>(), "Build Desk", CancellationToken.None);
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.CallAsync("build", [], null, stop.Token));

        Assert.Single(server.Posts, p => p["method"]?.GetValue<string>() == "notifications/cancelled");
    }

    private static string Progress(string? token, double done, double total, string message) =>
        JsonSerializer.Serialize(new { jsonrpc = "2.0", method = "notifications/progress", @params = new { progressToken = token, progress = done, total, message } });

    private static string Result(JsonObject call, string text) =>
        JsonSerializer.Serialize(new { jsonrpc = "2.0", id = call["id"]!.GetValue<int>(), result = new { content = new[] { new { type = "text", text } }, isError = false } });

    private static MemoryStream Sse(params string[] messages) => Sse([.. messages.Select(m => ((string?)null, m))]);

    private static MemoryStream Sse(params (string? Id, string Data)[] events) =>
        new MemoryStream(Encoding.UTF8.GetBytes(string.Concat(events.Select(e => (e.Id is null ? "" : $"id: {e.Id}\n") + $"data: {e.Data}\n\n"))));

    /// <summary>Its events, then the connection drops.</summary>
    private static Scripted Broken(Stream first) => new Scripted(first, _ => throw new IOException("The connection was reset."));

    /// <summary>A call that never ends (until it is given up on).</summary>
    private static Scripted Endless() => new Scripted(new MemoryStream(Encoding.UTF8.GetBytes(": still working\n\n")), ct => Task.Delay(Timeout.Infinite, ct));

    private sealed class Scripted(Stream first, Func<CancellationToken, Task> then) : Stream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var n = await first.ReadAsync(buffer, cancellationToken);
            if (n > 0)
            {
                return n;
            }
            await then(cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>An MCP server whose tools/call answers as the test says (a stream of events), with a session to resume.</summary>
    private sealed class ScriptedMcp(Func<JsonObject, string?, Stream> call) : HttpMessageHandler
    {
        public List<JsonObject> Posts { get; } = [];
        public List<string> Resumed { get; } = [];
        public Func<JsonObject, string, Stream>? Resume { get; set; }
        private JsonObject? _call;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                var last = request.Headers.GetValues("Last-Event-ID").Single();
                lock (Resumed)
                {
                    Resumed.Add(last);
                }
                return Events(Resume!(_call!, last));
            }
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
            lock (Posts)
            {
                Posts.Add(body);
            }
            var id = body["id"]?.ToJsonString();
            return body["method"]?.GetValue<string>() switch
            {
                "initialize" => Json("""{"jsonrpc":"2.0","id":""" + id + ""","result":{"protocolVersion":"2025-06-18","capabilities":{"tools":{}}}}""", session: "s-1"),
                "tools/call" => Events(call(_call = body, body["params"]?["_meta"]?["progressToken"]?.GetValue<string>())),
                _ => new HttpResponseMessage(HttpStatusCode.Accepted),
            };
        }

        private static HttpResponseMessage Json(string json, string session)
        {
            var res = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            res.Headers.Add("mcp-session-id", session);
            return res;
        }

        private static HttpResponseMessage Events(Stream stream)
        {
            var content = new StreamContent(stream);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }
    }
}
