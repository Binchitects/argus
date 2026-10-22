using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Llm.Api.Code.Studio;
using Llm.Api.Gateway;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>
/// Code Studio: a person's workspaces made from their GitLab project (read with their own token), started on the
/// workspaces service with a key of their own at the gateway, and the IDE's requests passed on to the workspace's engine
/// with its key — streamed answers, WebSockets — only for its owner; asleep, stopped and deleted with its key.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class CodeStudioTests(AppFixture app)
{
    private const string Service = "http://workspaces.test:4000";

    /// <summary>The workspaces service and its workspaces' engines, as the app's "workspaces" client reaches them.</summary>
    private sealed class FakeWorkspaces(int enginePort) : HttpMessageHandler
    {
        public readonly ConcurrentQueue<(string Method, string Path, string? Auth, string? Body)> Asked = new();
        public string EngineKey { get; } = "engine-" + new string('k', 40);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Asked.Enqueue((request.Method.Method, uri.PathAndQuery, request.Headers.Authorization?.ToString(), body));
            if (uri.Port == 4000)
            {
                return uri.AbsolutePath.EndsWith("/stop", StringComparison.Ordinal) || request.Method == HttpMethod.Delete
                    ? Json(HttpStatusCode.OK, "{}")
                    : Json(HttpStatusCode.OK, $$"""{"port":{{enginePort}},"key":"{{EngineKey}}","cloned":true}""");
            }
            // The engine: only with its key.
            if (request.Headers.Authorization?.ToString() != $"Bearer {EngineKey}")
            {
                return Json(HttpStatusCode.Unauthorized, """{"status":"unauthorized"}""");
            }
            return uri.AbsolutePath switch
            {
                "/api/state" => Json(HttpStatusCode.OK, """{"folder":"/workspaces/x/repo","model":"m"}"""),
                "/api/file" when request.Method == HttpMethod.Post => Json(HttpStatusCode.OK,
                    new JsonObject { ["got"] = body, ["type"] = request.Content!.Headers.ContentType?.MediaType, ["query"] = uri.Query }.ToJsonString()),
                "/api/messages" => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("data: {\"type\":\"text\",\"text\":\"hi\"}\n\ndata: {\"type\":\"done\"}\n\n", Encoding.UTF8, "text/event-stream"),
                },
                _ => Json(HttpStatusCode.NotFound, "{}"),
            };
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private WebApplicationFactory<Program> NewApp(FakeWorkspaces? service, FakeGateway gateway) =>
        app.Create(app.ConnectionStringFor("studio_" + Guid.NewGuid().ToString("N")[..8]), gateway, new Dictionary<string, string?>
        {
            ["Auth:DataKey"] = "a-data-key-for-studio-tests", ["GitLab:Url"] = "https://gitlab.test",
            ["Workspaces:Url"] = service is null ? null : Service, ["Workspaces:Key"] = service is null ? null : new string('w', 40), ["Workspaces:EngineHost"] = "127.0.0.1",
            ["Modules:workspaces"] = service is null ? "false" : "true",
        }, s => s.AddHttpClient(StudioWorkspaces.Client).ConfigurePrimaryHttpMessageHandler(() => (HttpMessageHandler?)service ?? new HttpClientHandler()));

    private static async Task<(TestBrowser Browser, string Email)> PersonAsync(WebApplicationFactory<Program> f)
    {
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "w" + Guid.NewGuid().ToString("N")[..10];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        return (await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!), $"{name}@example.test");
    }

    [Fact]
    public async Task Without_the_workspaces_service_there_are_no_server_workspaces()
    {
        await using var f = NewApp(null, new FakeGateway());
        var (b, _) = await PersonAsync(f);
        Assert.False((await b.JsonAsync(await b.GetAsync("/api/studio/workspaces"))).GetProperty("server").GetBoolean());
        await StatusAssert.Is(HttpStatusCode.ServiceUnavailable, await b.PostAsync("/api/studio/workspaces", new { project = "group/app" }));
    }

    [Fact]
    public async Task A_workspace_of_ones_project_starts_with_ones_own_access_and_the_ide_reaches_only_its_own_engine()
    {
        using var engine = new EchoEngine();
        var service = new FakeWorkspaces(engine.Port);
        var gateway = new FakeGateway();
        await using var f = NewApp(service, gateway);
        var (kim, email) = await PersonAsync(f);

        // Cloned and pushed as the person: their own GitLab token first.
        await StatusAssert.Is(HttpStatusCode.Conflict, await kim.PostAsync("/api/studio/workspaces", new { project = "group/app" }));
        await kim.Http.PutAsJsonAsync(new Uri("/api/fixes/token", UriKind.Relative), new { token = "person-token" });
        await StatusAssert.Is(HttpStatusCode.NotFound, await kim.PostAsync("/api/studio/workspaces", new { project = "group/app", branch = "nope" }));
        var made = await kim.JsonAsync(await kim.PostAsync("/api/studio/workspaces", new { project = "group/app" }));
        var id = made.GetProperty("id").GetGuid();
        Assert.Equal(("main", "asleep"), (made.GetProperty("branch").GetString(), made.GetProperty("state").GetString()));
        // Asleep: the IDE is told to start it.
        await StatusAssert.Is(HttpStatusCode.Conflict, await kim.GetAsync($"/api/studio/w/{id}/state"));

        var started = await kim.JsonAsync(await kim.PostAsync($"/api/studio/workspaces/{id}/start"));
        Assert.Equal("running", started.GetProperty("state").GetString());
        var put = service.Asked.Single(a => a.Method == "PUT");
        Assert.Equal($"/v1/workspaces/{id:N}", put.Path);
        Assert.Equal($"Bearer {new string('w', 40)}", put.Auth);
        var spec = JsonNode.Parse(put.Body!)!;
        Assert.Equal(("https://gitlab.test/group/app.git", "main", "person-token", "kim", "http://app:8080"),
            (spec["repo"]!.GetValue<string>(), spec["branch"]!.GetValue<string>(), spec["git_token"]!.GetValue<string>(), spec["git_user"]!.GetValue<string>(), spec["arena_url"]!.GetValue<string>()));
        // Its agent's own key at the gateway, the person's.
        var key = gateway.Keys.Values.Single(k => k.Alias == $"studio:{id:N}");
        Assert.Equal((email, key.Secret), (key.Email, spec["api_key"]!.GetValue<string>()));

        // The IDE's requests, on to the engine with its key; the page never has it.
        var state = await kim.JsonAsync(await kim.GetAsync($"/api/studio/w/{id}/state"));
        Assert.Equal("/workspaces/x/repo", state.GetProperty("folder").GetString());
        var saved = await kim.JsonAsync(await kim.PostAsync($"/api/studio/w/{id}/file?x=1", new { path = "a.txt", text = "hi" }));
        Assert.Equal(("""{"path":"a.txt","text":"hi"}""", "application/json", "?x=1"), (saved.GetProperty("got").GetString(), saved.GetProperty("type").GetString(), saved.GetProperty("query").GetString()));
        var events = await kim.Http.PostAsync(new Uri($"/api/studio/w/{id}/messages", UriKind.Relative), JsonContent.Create(new { text = "hi" }));
        Assert.Equal("text/event-stream", events.Content.Headers.ContentType?.MediaType);
        Assert.Contains("\"type\":\"done\"", await events.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain(service.EngineKey, await (await kim.GetAsync("/api/studio/workspaces")).Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // A terminal's socket, joined to the engine's.
        var socket = f.Server.CreateWebSocketClient();
        socket.ConfigureRequest = r =>
        {
            r.Headers["Cookie"] = string.Join("; ", kim.Cookies());
            r.Headers["Origin"] = $"https://{AppFixture.Domain}";
        };
        using (var ws = await socket.ConnectAsync(new Uri($"wss://{AppFixture.Domain}/api/studio/w/{id}/terminals/socket?id=t1"), CancellationToken.None))
        {
            await ws.SendAsync(Encoding.UTF8.GetBytes("ls\r"), WebSocketMessageType.Text, true, CancellationToken.None);
            var buffer = new byte[256];
            var got = await ws.ReceiveAsync(buffer, CancellationToken.None);
            Assert.Equal("echo: ls\r", Encoding.UTF8.GetString(buffer, 0, got.Count));
            Assert.Equal($"Bearer {service.EngineKey}", engine.Authorization);
            Assert.Equal("/api/terminals/socket?id=t1", engine.Path);
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        }

        // Another person: not theirs, not even to know it is there.
        var (other, _) = await PersonAsync(f);
        await StatusAssert.Is(HttpStatusCode.NotFound, await other.GetAsync($"/api/studio/w/{id}/state"));
        await StatusAssert.Is(HttpStatusCode.NotFound, await other.PostAsync($"/api/studio/workspaces/{id}/start"));

        // Asleep, then gone: its files on the service, its key at the gateway.
        await kim.PostAsync($"/api/studio/workspaces/{id}/stop");
        Assert.Contains(service.Asked, a => a.Method == "POST" && a.Path == $"/v1/workspaces/{id:N}/stop");
        await StatusAssert.Is(HttpStatusCode.Conflict, await kim.GetAsync($"/api/studio/w/{id}/state"));
        await StatusAssert.Is(HttpStatusCode.NoContent, await kim.Http.DeleteAsync(new Uri($"/api/studio/workspaces/{id}", UriKind.Relative)));
        Assert.Contains(service.Asked, a => a.Method == "DELETE" && a.Path == $"/v1/workspaces/{id:N}");
        Assert.DoesNotContain(gateway.Keys.Values, k => k.Alias == $"studio:{id:N}");
        Assert.Empty((await kim.JsonAsync(await kim.GetAsync("/api/studio/workspaces"))).GetProperty("workspaces").EnumerateArray());
    }

    /// <summary>A workspace's engine's WebSocket (a terminal): what it is sent comes back as "echo: …"; it keeps the key and path it was asked with.</summary>
    private sealed class EchoEngine : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _serving;

        public EchoEngine()
        {
            Port = FreePort();
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _listener.Start();
            _serving = Task.Run(ServeAsync);
        }

        public int Port { get; }
        public string? Authorization { get; private set; }
        public string? Path { get; private set; }

        private static int FreePort()
        {
            var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        private async Task ServeAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
                {
                    return;
                }
                Authorization = context.Request.Headers["Authorization"];
                Path = context.Request.Url!.PathAndQuery;
                var ws = (await context.AcceptWebSocketAsync(null)).WebSocket;
                var buffer = new byte[256];
                while (ws.State == WebSocketState.Open)
                {
                    var got = await ws.ReceiveAsync(buffer, CancellationToken.None);
                    if (got.MessageType == WebSocketMessageType.Close)
                    {
                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                        break;
                    }
                    await ws.SendAsync(Encoding.UTF8.GetBytes("echo: " + Encoding.UTF8.GetString(buffer, 0, got.Count)), WebSocketMessageType.Text, true, CancellationToken.None);
                }
            }
        }

        public void Dispose()
        {
            _listener.Stop();
            _listener.Close();
        }
    }

    /// <summary>The app as one replica: its own address its machines' tunnels are reached at, the "workspaces" client reaching it.</summary>
    private WebApplicationFactory<Program> MachineApp() =>
        app.Create(app.ConnectionStringFor("machine_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(), new Dictionary<string, string?>
        {
            ["Auth:DataKey"] = "a-data-key-for-studio-tests", ["Workspaces:SelfUrl"] = $"https://{AppFixture.Domain}", ["Modules:workspaces"] = "false",
        }, s => s.AddHttpClient(StudioWorkspaces.Client).ConfigurePrimaryHttpMessageHandler(sp =>
            ((Microsoft.AspNetCore.TestHost.TestServer)sp.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()).CreateHandler()));

    /// <summary>A person with their API key (as code-arena login keeps it).</summary>
    private static async Task<(TestBrowser Browser, string Key)> KeyedPersonAsync(WebApplicationFactory<Program> f)
    {
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "m" + Guid.NewGuid().ToString("N")[..10];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        return (await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!), made.GetProperty("apiKey").GetString()!);
    }

    /// <summary>
    /// A machine's code-arena on its tunnel: answers /api/state with its folder, any other request with what was asked, and
    /// echoes what a WebSocket sends; the paths it was asked kept.
    /// </summary>
    private static async Task MachineAsync(WebSocket tunnel, ConcurrentQueue<string> asked)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        async Task Send(JsonObject frame) => await tunnel.SendAsync(Encoding.UTF8.GetBytes(frame.ToJsonString()), WebSocketMessageType.Text, true, CancellationToken.None);
        try
        {
            while (tunnel.State == WebSocketState.Open)
            {
                var got = await tunnel.ReceiveAsync(buffer, CancellationToken.None);
                if (got.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }
                message.Write(buffer, 0, got.Count);
                if (!got.EndOfMessage)
                {
                    continue;
                }
                var f = (JsonObject)JsonNode.Parse(message.ToArray())!;
                message.SetLength(0);
                var id = f["id"]!.GetValue<long>();
                switch (f["t"]!.GetValue<string>())
                {
                    case "req":
                        asked.Enqueue($"{f["m"]} {f["p"]}");
                        var said = f["p"]!.GetValue<string>() == "/api/state"
                            ? """{"folder":"/home/dana/shop"}"""
                            : new JsonObject { ["body"] = f["b"] is JsonValue b ? Encoding.UTF8.GetString(Convert.FromBase64String(b.GetValue<string>())) : null, ["type"] = f["h"]?["Content-Type"]?.GetValue<string>() }.ToJsonString();
                        await Send(new JsonObject { ["t"] = "res", ["id"] = id, ["s"] = 200, ["h"] = new JsonObject { ["Content-Type"] = "application/json", ["Set-Cookie"] = "kept=here" } });
                        await Send(new JsonObject { ["t"] = "data", ["id"] = id, ["b"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(said)) });
                        await Send(new JsonObject { ["t"] = "end", ["id"] = id });
                        break;
                    case "ws":
                        asked.Enqueue($"WS {f["p"]}");
                        await Send(new JsonObject { ["t"] = "wsok", ["id"] = id });
                        break;
                    case "wsd":
                        await Send(new JsonObject { ["t"] = "wsd", ["id"] = id, ["x"] = "echo: " + f["x"]!.GetValue<string>() });
                        break;
                }
            }
        }
        catch (WebSocketException)
        {
        }
    }

    [Fact]
    public async Task A_machine_connects_with_the_persons_key_and_its_folder_opens_in_Code_Studio_through_its_tunnel()
    {
        await using var f = MachineApp();
        var (dana, key) = await KeyedPersonAsync(f);
        var machine = Guid.NewGuid();
        var client = f.Server.CreateWebSocketClient();
        var connect = new Uri($"wss://{AppFixture.Domain}/api/code-arena/connect?id={machine:N}&name=shop%20on%20laptop&project=team%2Fshop&branch=main");

        // Without a key, or with a name missing: refused.
        client.ConfigureRequest = r => r.Headers["Authorization"] = "Bearer sk-not-a-key";
        await Assert.ThrowsAnyAsync<Exception>(() => client.ConnectAsync(connect, CancellationToken.None));

        client.ConfigureRequest = r => r.Headers["Authorization"] = $"Bearer {key}";
        using var tunnel = await client.ConnectAsync(connect, CancellationToken.None);
        var asked = new ConcurrentQueue<string>();
        var serving = MachineAsync(tunnel, asked);

        var listed = (await dana.JsonAsync(await dana.GetAsync("/api/studio/workspaces"))).GetProperty("workspaces").EnumerateArray().Single();
        Assert.Equal((machine, "machine", "shop on laptop", "team/shop", "main", "connected"),
            (listed.GetProperty("id").GetGuid(), listed.GetProperty("kind").GetString(), listed.GetProperty("name").GetString(), listed.GetProperty("project").GetString(),
             listed.GetProperty("branch").GetString(), listed.GetProperty("state").GetString()));

        // The IDE's requests reach the machine through its tunnel (by this replica's own address, with the tunnel's key), answered as it answers.
        Assert.Equal("/home/dana/shop", (await dana.JsonAsync(await dana.GetAsync($"/api/studio/w/{machine}/state"))).GetProperty("folder").GetString());
        var saved = await dana.JsonAsync(await dana.PostAsync($"/api/studio/w/{machine}/file?x=1", new { path = "a.txt", text = "hi" }));
        Assert.Equal(("""{"path":"a.txt","text":"hi"}""", "application/json; charset=utf-8"), (saved.GetProperty("body").GetString(), saved.GetProperty("type").GetString()));
        Assert.Equal(["GET /api/state", "POST /api/file?x=1"], asked);
        // Only the headers the page needs come back (a cookie of the machine's stays there).
        var cookies = (await dana.GetAsync($"/api/studio/w/{machine}/state")).Headers.TryGetValues("Set-Cookie", out var set) ? set : [];
        Assert.DoesNotContain(cookies, c => c.StartsWith("kept=", StringComparison.Ordinal));

        // The tunnel's relay route takes the tunnel's own key alone; its WebSockets are joined message by message.
        await using (var scope = f.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Llm.Core.Data.AppDbContext>();
            var row = await db.StudioWorkspaces.AsNoTracking().SingleAsync(w => w.Id == machine);
            var tunnelKey = Llm.Api.Settings.SettingsCrypto.Decrypt(row.EngineKeyCipher!, "a-data-key-for-studio-tests")!;
            var anyone = f.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"https://{AppFixture.Domain}"), HandleCookies = false });
            await StatusAssert.Is(HttpStatusCode.NotFound, await anyone.GetAsync(new Uri($"/api/studio/tunnel/{machine:N}/api/state", UriKind.Relative)));
            var relay = f.Server.CreateWebSocketClient();
            relay.ConfigureRequest = r => r.Headers["Authorization"] = $"Bearer {tunnelKey}";
            using var ws = await relay.ConnectAsync(new Uri($"wss://{AppFixture.Domain}/api/studio/tunnel/{machine:N}/api/terminals/socket?id=t1"), CancellationToken.None);
            await ws.SendAsync(Encoding.UTF8.GetBytes("ls\r"), WebSocketMessageType.Text, true, CancellationToken.None);
            var buffer = new byte[256];
            var got = await ws.ReceiveAsync(buffer, CancellationToken.None);
            Assert.Equal("echo: ls\r", Encoding.UTF8.GetString(buffer, 0, got.Count));
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        }
        Assert.Contains("WS /api/terminals/socket?id=t1", asked);

        // Not theirs: someone else, even with the machine's id, reaches nothing and cannot take it.
        var (other, otherKey) = await KeyedPersonAsync(f);
        await StatusAssert.Is(HttpStatusCode.NotFound, await other.GetAsync($"/api/studio/w/{machine}/state"));
        var theirs = f.Server.CreateWebSocketClient();
        theirs.ConfigureRequest = r => r.Headers["Authorization"] = $"Bearer {otherKey}";
        await Assert.ThrowsAnyAsync<Exception>(() => theirs.ConnectAsync(connect, CancellationToken.None));
        // Started from here: it runs on the machine.
        await StatusAssert.Is(HttpStatusCode.Conflict, await dana.PostAsync($"/api/studio/workspaces/{machine}/start"));

        // Disconnected: offline, and the IDE is told how to connect it.
        // (Its own receive loop reads the app's answer to the close.)
        await tunnel.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        await serving;
        JsonElement state = default;
        for (var i = 0; i < 100; i++)
        {
            state = (await dana.JsonAsync(await dana.GetAsync("/api/studio/workspaces"))).GetProperty("workspaces")[0];
            if (state.GetProperty("state").GetString() == "offline")
            {
                break;
            }
            await Task.Delay(50);
        }
        Assert.Equal("offline", state.GetProperty("state").GetString());
        var offline = await dana.GetAsync($"/api/studio/w/{machine}/state");
        await StatusAssert.Is(HttpStatusCode.Conflict, offline);
        Assert.Contains("code-arena web --connect", await offline.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await StatusAssert.Is(HttpStatusCode.NoContent, await dana.Http.DeleteAsync(new Uri($"/api/studio/workspaces/{machine}", UriKind.Relative)));
    }
}
