using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Llm.Api.Code.Studio;
using Llm.Api.Gateway;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Llm.Tests;

/// <summary>
/// Code Studio's forwarded ports: what a workspace's dev server listens on, listed in its Ports view, and reached at its own
/// host under Arena's domain (ws-&lt;workspace&gt;-&lt;port&gt;.&lt;domain&gt;) with the person's Arena session — by its owner, or by
/// everyone signed in once shared — WebSockets included, Arena's cookies never passed to it nor set by it.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class StudioPortsTests(AppFixture app)
{
    private const string Key = "wwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwww";

    [Theory]
    [InlineData("ws-0123456789abcdef0123456789abcdef-5173.llm.test", true, 5173)]
    [InlineData("WS-0123456789ABCDEF0123456789ABCDEF-80.LLM.TEST", true, 80)]
    [InlineData("ws-0123456789abcdef0123456789abcdef-70000.llm.test", false, 0)]
    [InlineData("ws-0123456789abcdef0123456789abcdef-0.llm.test", false, 0)]
    [InlineData("ws-0123456789abcdef0123456789abcdef-5173.evil.test", false, 0)]
    [InlineData("ws-0123456789abcdef0123456789abcdef-5173.x.llm.test", false, 0)]
    [InlineData("gateway.llm.test", false, 0)]
    [InlineData("llm.test", false, 0)]
    [InlineData("ws-0123456789abcdef0123456789abcdef-+5.llm.test", false, 0)]
    public void A_forwarded_ports_host_names_its_workspace_and_port_under_the_domain_alone(string host, bool ok, int port)
    {
        Assert.Equal(ok, StudioPorts.TryParseHost(host, "llm.test", out var workspace, out var p));
        Assert.Equal(port, p);
        Assert.Equal(ok ? Guid.Parse("0123456789abcdef0123456789abcdef") : Guid.Empty, workspace);
        Assert.Equal("ws-0123456789abcdef0123456789abcdef-5173.llm.test", StudioPorts.Host(Guid.Parse("0123456789abcdef0123456789abcdef"), 5173, "llm.test"));
    }

    [Theory]
    [InlineData("own=1; Path=/; HttpOnly", true)]
    [InlineData("wide=1; Path=/; Domain=llm.test", false)]
    [InlineData("wide=1; domain = .llm.test", false)]
    [InlineData("llm_session=forged; Path=/", false)]
    [InlineData("arena_replica=1", false)]
    [InlineData(".AspNetCore.Antiforgery.x=1", false)]
    public void A_program_sets_cookies_for_its_own_host_only_and_none_of_arenas(string cookie, bool kept) =>
        Assert.Equal(kept, PortForwards.KeptCookie(cookie));

    /// <summary>The workspaces service: the workspace started, and its ports as /proc/net showed them.</summary>
    private sealed class FakeService(int devPort) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = path.EndsWith("/ports", StringComparison.Ordinal)
                ? $$"""[{"port":{{devPort}},"address":"127.0.0.1","pid":42,"process":"node node_modules/.bin/vite"}]"""
                : path.EndsWith("/stop", StringComparison.Ordinal) || request.Method == HttpMethod.Delete ? "{}" : """{"port":4100,"key":"engine-kkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkk","cloned":true}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    /// <summary>The relay to a workspace's port: to the dev server here for its port, what the service says for any other.</summary>
    private sealed class FakeRelay(int devPort, int devServerPort) : IPortRelay
    {
        public readonly ConcurrentQueue<(Guid Workspace, int Port)> Asked = new();

        public async Task<Stream> ConnectAsync(Guid workspace, int port, CancellationToken ct)
        {
            Asked.Enqueue((workspace, port));
            if (port != devPort)
            {
                throw new PortRelayException(404, $"Nothing in the workspace listens on port {port}.");
            }
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, devServerPort, ct);
            return client.GetStream();
        }
    }

    /// <summary>A dev server: says what it was asked (host, cookies, forwarded headers), sets its cookies and a forged one of Arena's, echoes WebSockets.</summary>
    private static async Task<WebApplication> DevServerAsync()
    {
        var b = WebApplication.CreateSlimBuilder();
        b.WebHost.UseUrls("http://127.0.0.1:0");
        var dev = b.Build();
        dev.UseWebSockets();
        dev.Map("/{**rest}", async (HttpContext http) =>
        {
            if (http.WebSockets.IsWebSocketRequest)
            {
                using var ws = await http.WebSockets.AcceptWebSocketAsync(http.WebSockets.WebSocketRequestedProtocols.FirstOrDefault());
                var buffer = new byte[1024];
                var got = await ws.ReceiveAsync(buffer, CancellationToken.None);
                var said = $"{Encoding.UTF8.GetString(buffer, 0, got.Count)} host={http.Request.Host} origin={http.Request.Headers.Origin} cookie={http.Request.Headers.Cookie}";
                await ws.SendAsync(Encoding.UTF8.GetBytes(said), WebSocketMessageType.Text, true, CancellationToken.None);
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                return;
            }
            http.Response.Headers.Append("Set-Cookie", "own=1; Path=/");
            http.Response.Headers.Append("Set-Cookie", "llm_session=forged; Path=/");
            http.Response.Headers.Append("Set-Cookie", "wide=1; Path=/; Domain=llm.test");
            http.Response.Headers.Append("X-Dev", "vite");
            var body = http.Request.ContentLength > 0 ? await new StreamReader(http.Request.Body).ReadToEndAsync() : "";
            await http.Response.WriteAsJsonAsync(new
            {
                method = http.Request.Method, path = $"{http.Request.Path}{http.Request.QueryString}", host = http.Request.Host.Value,
                cookie = http.Request.Headers.Cookie.ToString(), forwardedHost = http.Request.Headers["X-Forwarded-Host"].ToString(), body,
            });
        });
        await dev.StartAsync();
        return dev;
    }

    private static int PortOf(WebApplication dev) => new Uri(dev.Urls.First()).Port;

    private async Task<(WebApplicationFactory<Program> App, FakeRelay Relay)> NewAppAsync(int devPort, int devServerPort)
    {
        var relay = new FakeRelay(devPort, devServerPort);
        var f = app.Create(app.ConnectionStringFor("ports_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(), new Dictionary<string, string?>
        {
            ["Auth:DataKey"] = "a-data-key-for-studio-tests", ["GitLab:Url"] = "https://gitlab.test",
            ["Workspaces:Url"] = "http://workspaces.test:4000", ["Workspaces:Key"] = Key, ["Modules:workspaces"] = "true",
        }, s =>
        {
            s.AddHttpClient(StudioWorkspaces.Client).ConfigurePrimaryHttpMessageHandler(() => new FakeService(devPort));
            s.AddSingleton<IPortRelay>(relay);
        });
        return (f, relay);
    }

    private static async Task<TestBrowser> PersonAsync(WebApplicationFactory<Program> f)
    {
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "p" + Guid.NewGuid().ToString("N")[..10];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        return await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!);
    }

    private static async Task<Guid> RunningWorkspaceAsync(TestBrowser b)
    {
        await b.Http.PutAsJsonAsync(new Uri("/api/fixes/token", UriKind.Relative), new { token = "person-token" });
        var id = (await b.JsonAsync(await b.PostAsync("/api/studio/workspaces", new { project = "group/app" }))).GetProperty("id").GetGuid();
        Assert.Equal("running", (await b.JsonAsync(await b.PostAsync($"/api/studio/workspaces/{id}/start"))).GetProperty("state").GetString());
        return id;
    }

    private static Uri At(Guid id, int port, string path) => new($"https://{StudioPorts.Host(id, port, AppFixture.Domain)}{path}");

    [Fact]
    [Trait("Control", "IAM-05")]
    public async Task A_dev_server_is_listed_and_reached_by_its_owner_alone_at_its_own_address_without_arenas_session()
    {
        await using var dev = await DevServerAsync();
        var (f, relay) = await NewAppAsync(5173, PortOf(dev));
        await using var _ = f;
        var kim = await PersonAsync(f);
        var id = await RunningWorkspaceAsync(kim);

        var view = await kim.JsonAsync(await kim.GetAsync($"/api/studio/workspaces/{id}/ports"));
        var port = view.GetProperty("ports").EnumerateArray().Single();
        Assert.Equal((5173, "auto", "private", true, "node node_modules/.bin/vite", $"https://ws-{id:N}-5173.{AppFixture.Domain}/"),
            (port.GetProperty("port").GetInt32(), port.GetProperty("origin").GetString(), port.GetProperty("visibility").GetString(), port.GetProperty("listening").GetBoolean(),
             port.GetProperty("process").GetString(), port.GetProperty("url").GetString()));

        // Its owner, with their Arena session (its cookie covers the domain's subdomains): the dev server's answer, as it gave it.
        var page = await kim.Http.GetAsync(At(id, 5173, "/src/main.tsx?v=1"));
        await StatusAssert.Is(HttpStatusCode.OK, page);
        var seen = JsonDocument.Parse(await page.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(("GET", "/src/main.tsx?v=1", "localhost:5173", $"ws-{id:N}-5173.{AppFixture.Domain}"),
            (seen.GetProperty("method").GetString(), seen.GetProperty("path").GetString(), seen.GetProperty("host").GetString(), seen.GetProperty("forwardedHost").GetString()));
        Assert.Equal((id, 5173), relay.Asked.Last());
        // Arena's session never reaches the program; the program sets no cookie of Arena's, nor one for the whole domain.
        Assert.DoesNotContain("llm_session", seen.GetProperty("cookie").GetString(), StringComparison.Ordinal);
        var set = page.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies.ToList() : [];
        Assert.Contains("own=1; Path=/", set);
        Assert.DoesNotContain(set, c => c.StartsWith("llm_session=forged", StringComparison.Ordinal) || c.StartsWith("wide=", StringComparison.Ordinal));
        Assert.Equal("vite", page.Headers.GetValues("X-Dev").Single());
        // Not Arena's page: none of Arena's headers that would break a dev server (its inline scripts).
        Assert.False(page.Headers.Contains("Content-Security-Policy"));

        // Its own cookies come back to it; a form posted without Arena's X-Requested-With is the program's to judge.
        using var plain = f.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        using var post = new HttpRequestMessage(HttpMethod.Post, At(id, 5173, "/api/login")) { Content = new StringContent("name=kim", Encoding.UTF8, "application/x-www-form-urlencoded") };
        post.Headers.Add("Cookie", string.Join("; ", kim.Cookies().Append("own=1")));
        var posted = JsonDocument.Parse(await (await plain.SendAsync(post)).Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(("POST", "name=kim", "own=1"), (posted.GetProperty("method").GetString(), posted.GetProperty("body").GetString(), posted.GetProperty("cookie").GetString()));

        // Its live reload's WebSocket, its subprotocol kept, from its own pages only.
        var socket = f.Server.CreateWebSocketClient();
        socket.SubProtocols.Add("vite-hmr");
        socket.ConfigureRequest = r =>
        {
            r.Headers["Cookie"] = string.Join("; ", kim.Cookies());
            r.Headers["Origin"] = $"https://ws-{id:N}-5173.{AppFixture.Domain}";
        };
        using (var ws = await socket.ConnectAsync(new Uri($"wss://ws-{id:N}-5173.{AppFixture.Domain}/?token=t"), CancellationToken.None))
        {
            Assert.Equal("vite-hmr", ws.SubProtocol);
            await ws.SendAsync(Encoding.UTF8.GetBytes("ping"), WebSocketMessageType.Text, true, CancellationToken.None);
            var buffer = new byte[1024];
            var got = await ws.ReceiveAsync(buffer, CancellationToken.None);
            var said = Encoding.UTF8.GetString(buffer, 0, got.Count);
            Assert.StartsWith("ping host=localhost:5173 origin=http://localhost:5173 cookie=", said, StringComparison.Ordinal);
            Assert.DoesNotContain("llm_session", said, StringComparison.Ordinal);
        }
        var elsewhere = f.Server.CreateWebSocketClient();
        elsewhere.ConfigureRequest = r =>
        {
            r.Headers["Cookie"] = string.Join("; ", kim.Cookies());
            r.Headers["Origin"] = "https://evil.test";
        };
        await Assert.ThrowsAnyAsync<Exception>(() => elsewhere.ConnectAsync(new Uri($"wss://ws-{id:N}-5173.{AppFixture.Domain}/"), CancellationToken.None));

        // Nothing listening there: said so, plainly.
        var none = await kim.Http.GetAsync(At(id, 3000, "/"));
        await StatusAssert.Is(HttpStatusCode.BadGateway, none);
        Assert.Contains("listens on port 3000", await none.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // Someone else signed in: nothing there for them, not even that it exists; and their own Ports view of it is refused.
        var other = await PersonAsync(f);
        await StatusAssert.Is(HttpStatusCode.NotFound, await other.Http.GetAsync(At(id, 5173, "/")));
        await StatusAssert.Is(HttpStatusCode.NotFound, await other.GetAsync($"/api/studio/workspaces/{id}/ports"));
        await StatusAssert.Is(HttpStatusCode.NotFound, await other.Http.PutAsJsonAsync(new Uri($"/api/studio/workspaces/{id}/ports/5173", UriKind.Relative), new { visibility = "signed-in" }));

        // Nobody signed in: a page is sent to sign in at Arena first, and back; anything else is refused.
        var anonymous = await plain.SendAsync(new HttpRequestMessage(HttpMethod.Get, At(id, 5173, "/app?x=1")) { Headers = { { "Accept", "text/html" } } });
        await StatusAssert.Is(HttpStatusCode.Redirect, anonymous);
        Assert.Equal($"https://{AppFixture.Domain}/login?rd={Uri.EscapeDataString($"https://ws-{id:N}-5173.{AppFixture.Domain}/app?x=1")}", anonymous.Headers.Location!.ToString());
        await StatusAssert.Is(HttpStatusCode.Unauthorized, await plain.GetAsync(At(id, 5173, "/data.json")));

        // Nothing of Arena's API answers on a port's host.
        var api = await kim.Http.GetAsync(At(id, 5173, "/api/studio/workspaces"));
        Assert.Equal("/api/studio/workspaces", JsonDocument.Parse(await api.Content.ReadAsStringAsync()).RootElement.GetProperty("path").GetString());
    }

    [Fact]
    [Trait("Control", "IAM-05")]
    public async Task A_port_is_named_shared_with_everyone_signed_in_stopped_and_forwarded_by_hand()
    {
        await using var dev = await DevServerAsync();
        var (f, _) = await NewAppAsync(5173, PortOf(dev));
        await using var _ = f;
        var kim = await PersonAsync(f);
        var other = await PersonAsync(f);
        var id = await RunningWorkspaceAsync(kim);
        async Task<HttpResponseMessage> Set(int port, object body) => await kim.Http.PutAsJsonAsync(new Uri($"/api/studio/workspaces/{id}/ports/{port}", UriKind.Relative), body);

        // Shared: the person's colleague reaches it too; the change is in the audit log.
        var shared = await kim.JsonAsync(await Set(5173, new { label = "Web app", visibility = "signed-in" }));
        Assert.Equal(("Web app", "signed-in", "auto"), (shared.GetProperty("label").GetString(), shared.GetProperty("visibility").GetString(), shared.GetProperty("origin").GetString()));
        await StatusAssert.Is(HttpStatusCode.OK, await other.Http.GetAsync(At(id, 5173, "/")));
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        Assert.Contains("studio.port.share", await (await admin.GetAsync("/api/admin/audit?take=100")).Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // Stopped: nobody reaches it, its owner neither, until forwarded again.
        await Set(5173, new { stopped = true });
        await StatusAssert.Is(HttpStatusCode.NotFound, await other.Http.GetAsync(At(id, 5173, "/")));
        await StatusAssert.Is(HttpStatusCode.NotFound, await kim.Http.GetAsync(At(id, 5173, "/")));
        await Set(5173, new { stopped = false, visibility = "private" });
        await StatusAssert.Is(HttpStatusCode.OK, await kim.Http.GetAsync(At(id, 5173, "/")));
        await StatusAssert.Is(HttpStatusCode.NotFound, await other.Http.GetAsync(At(id, 5173, "/")));

        // Forwarded by hand before its server starts: listed as the person's, not listening yet.
        var added = await kim.JsonAsync(await Set(8080, new { label = "API" }));
        Assert.Equal(("user", false), (added.GetProperty("origin").GetString(), added.GetProperty("listening").GetBoolean()));
        var listed = (await kim.JsonAsync(await kim.GetAsync($"/api/studio/workspaces/{id}/ports"))).GetProperty("ports").EnumerateArray().Select(p => p.GetProperty("port").GetInt32()).ToList();
        Assert.Equal([5173, 8080], listed);
        await StatusAssert.Is(HttpStatusCode.BadRequest, await Set(70000, new { label = "x" }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await Set(8080, new { visibility = "public" }));
        await StatusAssert.Is(HttpStatusCode.NoContent, await kim.Http.DeleteAsync(new Uri($"/api/studio/workspaces/{id}/ports/8080", UriKind.Relative)));

        // Asleep: its ports are not reached, and the page says to open it.
        await kim.PostAsync($"/api/studio/workspaces/{id}/stop");
        var asleep = await kim.Http.SendAsync(new HttpRequestMessage(HttpMethod.Get, At(id, 5173, "/")) { Headers = { { "Accept", "text/html" } } });
        await StatusAssert.Is(HttpStatusCode.ServiceUnavailable, asleep);
        Assert.Contains("asleep", await asleep.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
}
