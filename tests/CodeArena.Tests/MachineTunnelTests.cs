using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace CodeArena.Tests;

/// <summary>
/// code-arena web --connect: this folder in Arena's Code Studio, through a tunnel dialled with the person's key. What
/// comes through is answered by this run's own web server with its own key, the IDE's WebSockets joined message by
/// message; nothing but the IDE's calls goes through.
/// </summary>
public sealed class MachineTunnelTests : IDisposable
{
    private readonly FakeGateway _gateway = new();
    private readonly FakeMcp _mcp = new();

    public void Dispose()
    {
        _gateway.Dispose();
        _mcp.Dispose();
    }

    /// <summary>Arena's connect route: takes the tunnel, says with what it was dialled, and holds it for the test to drive.</summary>
    private sealed class FakeStudio : FakeServer
    {
        public TaskCompletionSource<(WebSocket Socket, string Query, string? Key)> Dialled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task HandleAsync(HttpListenerContext ctx, string body, CancellationToken ct)
        {
            if (ctx.Request.Url!.AbsolutePath != "/api/code-arena/connect" || !ctx.Request.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = 404;
                return;
            }
            var socket = (await ctx.AcceptWebSocketAsync(null)).WebSocket;
            Dialled.TrySetResult((socket, ctx.Request.Url.Query, ctx.Request.Headers["Authorization"]));
            await Done.Task.WaitAsync(ct);
        }
    }

    private static async Task SendAsync(WebSocket socket, JsonObject frame) =>
        await socket.SendAsync(Encoding.UTF8.GetBytes(frame.ToJsonString()), WebSocketMessageType.Text, true, CancellationToken.None);

    /// <summary>The frames from the machine, each a whole message, into a channel.</summary>
    private static Channel<JsonObject> Frames(WebSocket socket)
    {
        var frames = Channel.CreateUnbounded<JsonObject>();
        _ = Task.Run(async () =>
        {
            var buffer = new byte[64 * 1024];
            using var message = new MemoryStream();
            try
            {
                while (socket.State == WebSocketState.Open)
                {
                    var got = await socket.ReceiveAsync(buffer, CancellationToken.None);
                    if (got.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }
                    message.Write(buffer, 0, got.Count);
                    if (got.EndOfMessage)
                    {
                        frames.Writer.TryWrite((JsonObject)JsonNode.Parse(message.ToArray())!);
                        message.SetLength(0);
                    }
                }
            }
            catch (WebSocketException)
            {
            }
            frames.Writer.TryComplete();
        });
        return frames;
    }

    /// <summary>The frames of one request until its end (or error): its status and its body.</summary>
    private static async Task<(int Status, string Body, string? Error)> AnswerAsync(Channel<JsonObject> frames, long id)
    {
        var (status, body, error) = (0, new StringBuilder(), (string?)null);
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            var f = await frames.Reader.ReadAsync(wait.Token);
            if (f["id"]!.GetValue<long>() != id)
            {
                continue;
            }
            switch (f["t"]!.GetValue<string>())
            {
                case "res":
                    status = f["s"]!.GetValue<int>();
                    break;
                case "data":
                    body.Append(Encoding.UTF8.GetString(Convert.FromBase64String(f["b"]!.GetValue<string>())));
                    break;
                case "err":
                    error = f["e"]!.GetValue<string>();
                    return (status, body.ToString(), error);
                case "end":
                    return (status, body.ToString(), error);
            }
        }
    }

    [Fact]
    public async Task Code_Studio_reaches_this_folder_through_the_tunnel_with_this_runs_own_key_and_only_the_IDEs_calls()
    {
        using var h = new Harness(_gateway, _mcp);
        await using var web = await WebRun.StartAsync(h);
        using var studio = new FakeStudio();
        var id = Guid.NewGuid().ToString("N");
        var said = new List<string>();
        var tunnel = new MachineTunnel(new Uri(studio.BaseUrl + "/"), "sk-person-key-0123456789", web.Port, web.Token, "shop on laptop", "team/shop", "main", id,
            s => { lock (said) { said.Add(s); } });
        using var stop = new CancellationTokenSource();
        var running = tunnel.RunAsync(stop.Token);
        try
        {
            var (socket, query, key) = await studio.Dialled.Task.WaitAsync(TimeSpan.FromSeconds(20));
            // Dialled with the person's key, the folder's id, its name, project and branch.
            Assert.Equal("Bearer sk-person-key-0123456789", key);
            Assert.Equal($"?id={id}&name=shop%20on%20laptop&project=team%2Fshop&branch=main", query);
            var frames = Frames(socket);

            await SendAsync(socket, new JsonObject { ["t"] = "req", ["id"] = 1, ["m"] = "GET", ["p"] = "/api/state" });
            var (status, body, _) = await AnswerAsync(frames, 1);
            Assert.Equal(200, status);
            Assert.Equal(h.Work, JsonNode.Parse(body)!["folder"]!.GetValue<string>());

            // A body goes through: a file saved, then read.
            await SendAsync(socket, new JsonObject
            {
                ["t"] = "req", ["id"] = 2, ["m"] = "POST", ["p"] = "/api/file", ["h"] = new JsonObject { ["Content-Type"] = "application/json" },
                ["b"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(new JsonObject { ["path"] = "notes.md", ["text"] = "# Notes\n", ["version"] = null }.ToJsonString())),
            });
            Assert.Equal(200, (await AnswerAsync(frames, 2)).Status);
            Assert.Equal("# Notes\n", File.ReadAllText(Path.Combine(h.Work, "notes.md")));

            // Only the IDE's calls: nothing else of the machine.
            await SendAsync(socket, new JsonObject { ["t"] = "req", ["id"] = 3, ["m"] = "GET", ["p"] = "/" });
            Assert.Equal("Only the IDE's calls go through.", (await AnswerAsync(frames, 3)).Error);

            // A terminal: opened, joined by its WebSocket, keys in and output out.
            await SendAsync(socket, new JsonObject
            {
                ["t"] = "req", ["id"] = 4, ["m"] = "POST", ["p"] = "/api/terminals", ["h"] = new JsonObject { ["Content-Type"] = "application/json" },
                ["b"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("""{"cols":80,"rows":24}""")),
            });
            var terminal = JsonNode.Parse((await AnswerAsync(frames, 4)).Body)!["id"]!.GetValue<string>();
            await SendAsync(socket, new JsonObject { ["t"] = "ws", ["id"] = 5, ["p"] = $"/api/terminals/socket?id={terminal}" });
            using (var wait = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
            {
                Assert.Equal("wsok", (await ReadAsync(frames, 5, wait.Token))["t"]!.GetValue<string>());
                await SendAsync(socket, new JsonObject { ["t"] = "wsd", ["id"] = 5, ["x"] = """{"type":"input","data":"echo tunnel-$((40+2))\r"}""" });
                var output = new StringBuilder();
                while (!output.ToString().Contains("tunnel-42", StringComparison.Ordinal))
                {
                    var f = await ReadAsync(frames, 5, wait.Token);
                    if (f["b"] is JsonValue b)
                    {
                        output.Append(Encoding.UTF8.GetString(Convert.FromBase64String(b.GetValue<string>())));
                    }
                }
            }
            await SendAsync(socket, new JsonObject { ["t"] = "wsc", ["id"] = 5 });
            lock (said)
            {
                Assert.Contains(said, s => s.StartsWith("Connected to Code Studio", StringComparison.Ordinal));
            }
        }
        finally
        {
            await stop.CancelAsync();
            studio.Done.TrySetResult();
            await running.WaitAsync(TimeSpan.FromSeconds(20));
        }
    }

    private static async Task<JsonObject> ReadAsync(Channel<JsonObject> frames, long id, CancellationToken ct)
    {
        while (true)
        {
            var f = await frames.Reader.ReadAsync(ct);
            if (f["id"]!.GetValue<long>() == id)
            {
                return f;
            }
        }
    }

    [Fact]
    public async Task A_refused_tunnel_says_why_and_stops_dialling()
    {
        using var h = new Harness(_gateway, _mcp);
        await using var web = await WebRun.StartAsync(h);
        // Nothing answers the connect route here: 404, refused.
        using var nowhere = new NoStudio();
        var said = new List<string>();
        var tunnel = new MachineTunnel(new Uri(nowhere.BaseUrl + "/"), "sk-person-key-0123456789", web.Port, web.Token, "shop", null, null, Guid.NewGuid().ToString("N"), said.Add);
        await tunnel.RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Contains(said, s => s.StartsWith("Arena refused to connect this folder to Code Studio (HTTP 403)", StringComparison.Ordinal));
    }

    private sealed class NoStudio : FakeServer
    {
        protected override Task HandleAsync(HttpListenerContext ctx, string body, CancellationToken ct)
        {
            ctx.Response.StatusCode = 403;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void The_project_is_origins_path_whether_https_or_ssh()
    {
        var dir = Directory.CreateTempSubdirectory("origin");
        try
        {
            Origin(dir.FullName);
        }
        finally
        {
            dir.Delete(true);
        }
    }

    private static void Origin(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        var config = Path.Combine(root, ".git", "config");
        File.WriteAllText(config, "[core]\n\tbare = false\n[remote \"origin\"]\n\turl = https://gitlab.example.com/team/shop.git\n");
        Assert.Equal("team/shop", Cli.OriginProject(root));
        File.WriteAllText(config, "[remote \"origin\"]\n\turl = git@gitlab.example.com:team/sub/shop.git\n");
        Assert.Equal("team/sub/shop", Cli.OriginProject(root));
        File.WriteAllText(config, "[core]\n");
        Assert.Null(Cli.OriginProject(root));
    }
}
