using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Llm.Api.Code.Studio;
using Llm.Api.Gateway;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Llm.Tests;

/// <summary>
/// A test against a real workspaces image (deploy/services/workspaces), run only when STUDIO_E2E_IMAGE names one built
/// from this checkout: <c>docker build -f deploy/services/workspaces/Dockerfile -t ws-e2e .</c>, then
/// <c>tools/dn test tests/Llm.Tests --filter RealWorkspace -e STUDIO_E2E_IMAGE=ws-e2e</c>. Its containers are its own.
/// </summary>
public sealed class RealWorkspaceFactAttribute : FactAttribute
{
    public static readonly string? Image = Environment.GetEnvironmentVariable("STUDIO_E2E_IMAGE") is { Length: > 0 } image ? image : null;

    public RealWorkspaceFactAttribute()
    {
        if (Image is null)
        {
            Skip = "No workspaces image: set STUDIO_E2E_IMAGE to one built from this checkout to run it.";
        }
    }
}

/// <summary>
/// A Code Studio workspace end to end, in throwaway containers: the workspaces service as compose runs it (its security
/// options), a code host (GitLab's API and git over HTTP, with the person's token) that is also the agent's model API.
/// Started (cloned as the person), a dev server run in its terminal and opened through its forwarded address (its
/// WebSocket too), the agent's command sandboxed, a commit pushed, put to sleep and started again.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class RealWorkspaceTests(AppFixture app, ITestOutputHelper output)
{
    private const string Key = "e2e-workspaces-key-0123456789abcdef0123";
    private static readonly string Root = Path.GetFullPath(Path.Combine(AppFixture.PluginsPath, ".."));

    /// <summary>What the agent's one command does: read the person's token, write outside and inside the clone, see the engine's key.</summary>
    private const string Command = "cat ~/.config/code-arena/git-token 2>&1 | head -c 120; echo; touch /etc/agent-was-here 2>&1; " +
        "echo made > made-by-agent.txt && echo wrote-in-clone; echo engine=[$CODE_ARENA_TOKEN]; ls ~/.config/code-arena 2>&1 | head -3";

    /// <summary>A WebSocket echo server, as a dev server's live reload is (Python's own library only).</summary>
    private const string EchoSocket = """
        import base64, hashlib, socket, threading
        def serve(c):
            head = b""
            while b"\r\n\r\n" not in head:
                head += c.recv(1)
            key = [l.split(b":", 1)[1].strip() for l in head.split(b"\r\n") if l.lower().startswith(b"sec-websocket-key")][0]
            accept = base64.b64encode(hashlib.sha1(key + b"258EAFA5-E914-47DA-95CA-C5AB0DC85B11").digest())
            c.sendall(b"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + accept + b"\r\n\r\n")
            b1, b2 = c.recv(2)
            n = b2 & 127
            mask = c.recv(4)
            data = bytes(x ^ mask[i % 4] for i, x in enumerate(c.recv(n)))
            reply = b"echo " + data
            c.sendall(bytes([0x81, len(reply)]) + reply)
            c.close()
        s = socket.socket(); s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1); s.bind(("127.0.0.1", 5174)); s.listen()
        while True:
            conn, _ = s.accept(); threading.Thread(target=serve, args=(conn,)).start()
        """;

    [RealWorkspaceFact]
    public async Task A_workspace_clones_runs_a_dev_server_reached_through_its_forward_sandboxes_the_agent_pushes_sleeps_and_wakes()
    {
        await using var network = new NetworkBuilder().WithName("studio-e2e-" + Guid.NewGuid().ToString("N")[..8]).Build();
        await network.CreateAsync();
        await using var codehost = new ContainerBuilder(RealWorkspaceFactAttribute.Image!).WithNetwork(network).WithNetworkAliases("codehost")
            .WithResourceMapping(await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Resources", "real-workspace", "codehost.py")), "/e2e/codehost.py")
            .WithEnvironment("E2E_COMMAND", Command).WithEntrypoint("python3", "-u", "/e2e/codehost.py")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted("python3", "-c", "import urllib.request; urllib.request.urlopen('http://127.0.0.1:8000/e2e/asked')"))
            .Build();
        var seccomp = await File.ReadAllTextAsync(Path.Combine(Root, "deploy", "config", "workspaces-seccomp.json"));
        // As compose runs it (docker-compose.yml's workspaces).
        await using var workspaces = new ContainerBuilder(RealWorkspaceFactAttribute.Image!).WithNetwork(network).WithNetworkAliases("workspaces")
            .WithEnvironment("WORKSPACES_KEY", Key).WithEnvironment("WORKSPACES_SLOTS", "4")
            .WithCreateParameterModifier(p =>
            {
                p.HostConfig!.Init = true;
                p.HostConfig.CapDrop = ["ALL"];
                p.HostConfig.CapAdd = ["SETUID", "SETGID", "CHOWN", "DAC_OVERRIDE", "FOWNER", "KILL"];
                p.HostConfig.SecurityOpt = ["no-new-privileges:true", "seccomp=" + seccomp, "apparmor=unconfined"];
                // systempaths=unconfined, as the docker CLI and compose send it: nothing of /proc masked.
                (p.HostConfig.MaskedPaths, p.HostConfig.ReadonlyPaths) = ([], []);
            })
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("The agent's commands:"))
            .Build();
        await Task.WhenAll(codehost.StartAsync(), workspaces.StartAsync());
        var (logs, _) = await workspaces.GetLogsAsync();
        output.WriteLine(logs);
        Assert.Contains("sandboxed by bubblewrap", logs, StringComparison.Ordinal);
        var host = await IpAsync(codehost, network);
        var service = await IpAsync(workspaces, network);
        output.WriteLine($"code host [{host}], workspaces [{service}]");

        await using var f = app.Create(app.ConnectionStringFor("realws_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(), new Dictionary<string, string?>
        {
            ["Auth:DataKey"] = "a-data-key-for-studio-tests", ["GitLab:Url"] = $"http://{host}:8000",
            ["Workspaces:Url"] = $"http://{service}:4000", ["Workspaces:Key"] = Key, ["Workspaces:EngineHost"] = service,
            // The agent's model API and Arena: the code host's stand-in, by its name on the workspaces' network.
            ["Workspaces:ArenaUrl"] = "http://codehost:8000", ["Modules:workspaces"] = "true",
        }, s =>
        {
            // The real network: GitLab's API on the code host, the workspaces service and its engines.
            s.AddHttpClient(Llm.Api.Schedules.GitLabBot.Client).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler());
            s.AddHttpClient(StudioWorkspaces.Client).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler()).AddHttpMessageHandler(() => new Said(output));
        });
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "dana" + Guid.NewGuid().ToString("N")[..6];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test", displayName = "Dana Smith" }));
        var dana = await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!);
        await StatusAssert.Is(HttpStatusCode.OK, await dana.Http.PutAsJsonAsync(new Uri("/api/fixes/token", UriKind.Relative), new { token = "glpat-e2e-token" }));

        // Started: cloned once, as the person (their token through git's askpass), the engine up.
        var id = (await dana.JsonAsync(await dana.PostAsync("/api/studio/workspaces", new { project = "group/app" }))).GetProperty("id").GetGuid();
        var started = await dana.PostAsync($"/api/studio/workspaces/{id}/start");
        output.WriteLine(await started.Content.ReadAsStringAsync());
        await StatusAssert.Is(HttpStatusCode.OK, started);
        var ide = $"/api/studio/w/{id}";
        var readme = await dana.JsonAsync(await dana.GetAsync($"{ide}/file?path=README.md"));
        output.WriteLine(readme.ToString());
        Assert.Contains("# app", readme.ToString(), StringComparison.Ordinal);

        // A dev server in a terminal of its own, and a WebSocket server beside it.
        await StatusAssert.Is(HttpStatusCode.OK, await dana.PostAsync($"{ide}/file", new { path = "ws.py", text = EchoSocket }));
        var terminal = (await dana.JsonAsync(await dana.PostAsync($"{ide}/terminals", new { cols = 120, rows = 30 }))).GetProperty("id").GetString();
        var socket = f.Server.CreateWebSocketClient();
        socket.ConfigureRequest = r =>
        {
            r.Headers["Cookie"] = string.Join("; ", dana.Cookies());
            r.Headers["Origin"] = $"https://{AppFixture.Domain}";
        };
        using (var shell = await socket.ConnectAsync(new Uri($"wss://{AppFixture.Domain}{ide}/terminals/socket?id={terminal}"), CancellationToken.None))
        {
            await shell.SendAsync(Encoding.UTF8.GetBytes("python3 ws.py & python3 -m http.server 5173 --bind 127.0.0.1\r"), WebSocketMessageType.Binary, true, CancellationToken.None);
            var said = await ReadUntilAsync(shell, "Serving HTTP on 127.0.0.1 port 5173");
            output.WriteLine(said);
        }

        // Listed in the Ports view, with its process; reached through its own address by the person, with their session.
        var ports = (await dana.JsonAsync(await dana.GetAsync($"/api/studio/workspaces/{id}/ports"))).GetProperty("ports").EnumerateArray().ToList();
        output.WriteLine(string.Join('\n', ports));
        var web = ports.Single(p => p.GetProperty("port").GetInt32() == 5173);
        Assert.Contains("http.server", web.GetProperty("process").GetString(), StringComparison.Ordinal);
        Assert.Contains(ports, p => p.GetProperty("port").GetInt32() == 5174);
        var page = await dana.Http.GetAsync(new Uri($"https://{StudioPorts.Host(id, 5173, AppFixture.Domain)}/"));
        var listing = await page.Content.ReadAsStringAsync();
        await StatusAssert.Is(HttpStatusCode.OK, page);
        Assert.Contains("README.md", listing, StringComparison.Ordinal);
        Assert.Contains("ws.py", listing, StringComparison.Ordinal);
        var live = f.Server.CreateWebSocketClient();
        live.ConfigureRequest = r =>
        {
            r.Headers["Cookie"] = string.Join("; ", dana.Cookies());
            r.Headers["Origin"] = $"https://{StudioPorts.Host(id, 5174, AppFixture.Domain)}";
        };
        using (var ws = await live.ConnectAsync(new Uri($"wss://{StudioPorts.Host(id, 5174, AppFixture.Domain)}/hmr"), CancellationToken.None))
        {
            await ws.SendAsync(Encoding.UTF8.GetBytes("reload"), WebSocketMessageType.Text, true, CancellationToken.None);
            var buffer = new byte[256];
            var got = await ws.ReceiveAsync(buffer, CancellationToken.None);
            Assert.Equal("echo reload", Encoding.UTF8.GetString(buffer, 0, got.Count));
        }

        // The agent's command, in the sandbox: no token, nothing written outside the clone, no engine key.
        await StatusAssert.Is(HttpStatusCode.OK, await dana.PostAsync($"{ide}/settings", new { mode = "yolo", model = "e2e-model" }));
        var turn = await dana.Http.PostAsync(new Uri($"{ide}/messages", UriKind.Relative), JsonContent.Create(new { text = "Check the sandbox." }));
        output.WriteLine(await turn.Content.ReadAsStringAsync());
        var asked = await new HttpClient().GetFromJsonAsync<JsonArray>($"http://{host}:8000/e2e/asked");
        var result = asked!.OfType<JsonObject>().Select(a => a["tool_result"]?.GetValue<string>()).OfType<string>().Last();
        output.WriteLine(result);
        Assert.DoesNotContain("glpat-e2e-token", result, StringComparison.Ordinal);
        Assert.Contains("wrote-in-clone", result, StringComparison.Ordinal);
        Assert.Contains("Read-only file system", result, StringComparison.Ordinal);
        Assert.Contains("engine=[]", result, StringComparison.Ordinal);
        Assert.Contains("made", (await dana.JsonAsync(await dana.GetAsync($"{ide}/file?path=made-by-agent.txt"))).ToString(), StringComparison.Ordinal);

        // Committed as the person and pushed, as them, to the code host.
        var commit = await dana.PostAsync($"{ide}/git/commit", new { message = "The agent's file", all = true });
        output.WriteLine(await commit.Content.ReadAsStringAsync());
        await StatusAssert.Is(HttpStatusCode.OK, commit);
        var push = await dana.PostAsync($"{ide}/git/push", new { });
        output.WriteLine(await push.Content.ReadAsStringAsync());
        await StatusAssert.Is(HttpStatusCode.OK, push);
        var log = await codehost.ExecAsync(["git", "-C", "/srv/git/group/app.git", "log", "-1", "--format=%an <%ae>|%s", "main"]);
        output.WriteLine(log.Stdout + log.Stderr);
        Assert.Equal($"Dana Smith <{name}@example.test>|The agent's file", log.Stdout.Trim());

        // Asleep: its engine and dev servers stopped, its address closed; woken: not cloned again, its files kept.
        await StatusAssert.Is(HttpStatusCode.OK, await dana.PostAsync($"/api/studio/workspaces/{id}/stop"));
        await StatusAssert.Is(HttpStatusCode.ServiceUnavailable, await dana.Http.GetAsync(new Uri($"https://{StudioPorts.Host(id, 5173, AppFixture.Domain)}/")));
        await StatusAssert.Is(HttpStatusCode.OK, await dana.PostAsync($"/api/studio/workspaces/{id}/start"));
        Assert.Contains("made", (await dana.JsonAsync(await dana.GetAsync($"{ide}/file?path=made-by-agent.txt"))).ToString(), StringComparison.Ordinal);
        var gone = await dana.Http.GetAsync(new Uri($"https://{StudioPorts.Host(id, 5173, AppFixture.Domain)}/"));
        await StatusAssert.Is(HttpStatusCode.BadGateway, gone);
        Assert.Contains("listens on port 5173", await gone.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await dana.Http.DeleteAsync(new Uri($"/api/studio/workspaces/{id}", UriKind.Relative));
    }

    /// <summary>What the app asks the workspaces service (a start's project address), in the test's output.</summary>
    private sealed class Said(ITestOutputHelper output) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            output.WriteLine($"{request.Method} {request.RequestUri} {(body.StartsWith('{') ? JsonNode.Parse(body)?["repo"] : null)}");
            return await base.SendAsync(request, cancellationToken);
        }
    }

    private static async Task<string> IpAsync(IContainer container, INetwork network)
    {
        _ = network;
        var inspect = await container.ExecAsync(["python3", "-c", "import socket; print(socket.gethostbyname(socket.gethostname()))"]);
        return inspect.Stdout.Trim();
    }

    private static async Task<string> ReadUntilAsync(WebSocket socket, string text)
    {
        var seen = new StringBuilder();
        var buffer = new byte[4096];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!seen.ToString().Contains(text, StringComparison.Ordinal))
        {
            var got = await socket.ReceiveAsync(buffer, timeout.Token);
            seen.Append(Encoding.UTF8.GetString(buffer, 0, got.Count));
        }
        return seen.ToString();
    }
}
