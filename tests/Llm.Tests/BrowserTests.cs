using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Llm.Api.Chat.Tools.Browser;
using Llm.Api.Gateway;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>
/// The browser agent: a real browser (here Chromium's DevTools, faked) driven by the model in the chat, with its screen
/// shown to the person and to a model that sees; sending only after the person allows it, secrets never typed by it, the
/// person taking it over; and sites only through the proxy's rules.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class BrowserTests(AppFixture app)
{
    private WebApplicationFactory<Program> NewApp(FakeBrowser browser, FakeGateway? gateway = null, string blocked = "") =>
        app.Create(app.ConnectionStringFor("browser_" + Guid.NewGuid().ToString("N")[..8]), gateway ?? new FakeGateway(),
            new Dictionary<string, string?> { ["Browser:Url"] = browser.Url, ["Browser:Sites"] = "*.test", ["Browser:BlockedSites"] = blocked, ["Browser:ProxyPort"] = "0" });

    private static async Task<(TestBrowser Browser, Guid Id)> PersonAsync(WebApplicationFactory<Program> f)
    {
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "w" + Guid.NewGuid().ToString("N")[..10];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        return (await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!), made.GetProperty("id").GetGuid());
    }

    private static async Task<List<JsonElement>> SendAsync(TestBrowser b, Guid chat, string text) =>
        [.. (await (await b.PostAsync($"/api/chat/conversations/{chat}/messages", new { content = text })).Content.ReadAsStringAsync())
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Where(l => l.StartsWith("data: ", StringComparison.Ordinal)).Select(l => JsonDocument.Parse(l[6..]).RootElement)];

    private static JsonElement Result(List<JsonElement> events) => events.First(e => e.GetProperty("type").GetString() == "tool_result");

    private static async Task<Guid> ChatAsync(TestBrowser b) =>
        (await b.JsonAsync(await b.PostAsync("/api/chat/conversations", new { useArgus = false, tools = new[] { "browser" } }))).GetProperty("id").GetGuid();

    [Fact]
    public async Task The_model_browses_shown_live_sending_only_when_allowed_and_never_typing_a_secret()
    {
        await using var browser = new FakeBrowser();
        await using var f = NewApp(browser, blocked: "evil.test");
        var (b, _) = await PersonAsync(f);
        var chat = await ChatAsync(b);

        // A page, in words and in a picture.
        var opened = Result(await SendAsync(b, chat, """[call browser_open {"url":"https://shop.test/"}]"""));
        Assert.False(opened.GetProperty("isError").GetBoolean(), opened.GetProperty("text").GetString());
        var text = opened.GetProperty("text").GetString()!;
        Assert.Contains("Page: Shop — https://shop.test/", text, StringComparison.Ordinal);
        Assert.Contains("[1] link \"Laptops\"", text, StringComparison.Ordinal);
        Assert.Contains("Welcome to the shop", text, StringComparison.Ordinal);
        var shot = opened.GetProperty("attachments")[0];
        Assert.Equal(("image", "image/jpeg"), (shot.GetProperty("kind").GetString(), shot.GetProperty("contentType").GetString()));
        Assert.Equal("jpeg of https://shop.test/", await (await b.GetAsync($"/api/chat/attachments/{shot.GetProperty("id").GetGuid()}/content")).Content.ReadAsStringAsync());
        // Each chat its own context.
        Assert.Single(browser.Commands, c => c.Method == "Target.createBrowserContext");

        // Sites: the stack's own network never, a blocked one never, an address outside the list never.
        Assert.Contains("inside your network", Result(await SendAsync(b, chat, """[call browser_open {"url":"https://internal.test/"}]""")).GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.Contains("never opens", Result(await SendAsync(b, chat, """[call browser_open {"url":"https://evil.test/"}]""")).GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.Contains("not among the sites", Result(await SendAsync(b, chat, """[call browser_open {"url":"https://example.com/"}]""")).GetProperty("text").GetString(), StringComparison.Ordinal);

        // A search box: typed and searched.
        var searched = Result(await SendAsync(b, chat, """[call browser_type {"element":2,"text":"phone","search":true}]"""));
        Assert.Contains("Page: Results", searched.GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.Contains("phone", browser.Typed);

        // A link is a click; Buy now is not: it sends, and asks first.
        await SendAsync(b, chat, """[call browser_back {}]""");
        Assert.Contains("Page: Laptop X1", Result(await SendAsync(b, chat, """[call browser_click {"element":1}]""")).GetProperty("text").GetString(), StringComparison.Ordinal);
        var refused = Result(await SendAsync(b, chat, """[call browser_click {"element":1}]"""));
        Assert.True(refused.GetProperty("isError").GetBoolean());
        Assert.Contains("browser_submit", refused.GetProperty("text").GetString(), StringComparison.Ordinal);
        var buying = SendAsync(b, chat, """[call browser_submit {"element":1}]""");
        for (var i = 0; (await b.PostAsync($"/api/chat/conversations/{chat}/tool-calls/call_1", new { allow = true })).StatusCode != HttpStatusCode.NoContent; i++)
        {
            Assert.True(i < 100, "the purchase never waited to be allowed");
            await Task.Delay(100);
        }
        Assert.Contains("Page: Thank you", Result(await buying).GetProperty("text").GetString(), StringComparison.Ordinal);

        // A password is the person's to type.
        await SendAsync(b, chat, """[call browser_open {"url":"https://shop.test/sign-in"}]""");
        var secret = Result(await SendAsync(b, chat, """[call browser_type {"element":2,"text":"hunter2"}]"""));
        Assert.True(secret.GetProperty("isError").GetBoolean());
        Assert.Contains("take over", secret.GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", browser.Typed);
    }

    [Fact]
    public async Task The_person_watches_and_takes_over_and_nobody_else_sees_it()
    {
        await using var browser = new FakeBrowser();
        await using var f = NewApp(browser);
        var (b, _) = await PersonAsync(f);
        var chat = await ChatAsync(b);
        Assert.False((await b.JsonAsync(await b.GetAsync($"/api/chat/browser/{chat}"))).GetProperty("open").GetBoolean());
        await SendAsync(b, chat, """[call browser_open {"url":"https://shop.test/sign-in"}]""");

        var status = await b.JsonAsync(await b.GetAsync($"/api/chat/browser/{chat}"));
        Assert.Equal(("https://shop.test/sign-in", "Sign in", false), (status.GetProperty("url").GetString(), status.GetProperty("title").GetString(), status.GetProperty("takenOver").GetBoolean()));
        var screen = await b.GetAsync($"/api/chat/browser/{chat}/screen");
        Assert.Equal("image/jpeg", screen.Content.Headers.ContentType?.MediaType);

        // Only while taken over does the person's hand reach the page; their password is theirs.
        await StatusAssert.Is(HttpStatusCode.Conflict, await b.PostAsync($"/api/chat/browser/{chat}/input", new { kind = "type", text = "x" }));
        await StatusAssert.Is(HttpStatusCode.NoContent, await b.PostAsync($"/api/chat/browser/{chat}/take"));
        await StatusAssert.Is(HttpStatusCode.NoContent, await b.PostAsync($"/api/chat/browser/{chat}/input", new { kind = "click", x = 0.5, y = 0.25 }));
        await StatusAssert.Is(HttpStatusCode.NoContent, await b.PostAsync($"/api/chat/browser/{chat}/input", new { kind = "type", text = "my own secret" }));
        Assert.Contains("my own secret", browser.Typed);
        Assert.Contains(browser.Commands, c => c.Method == "Input.dispatchMouseEvent" && c.Params["x"]!.GetValue<double>() == 640 && c.Params["y"]!.GetValue<double>() == 200);

        // The agent waits while the person has it, and goes on when they hand it back.
        var waiting = SendAsync(b, chat, """[call browser_scroll {"direction":"down"}]""");
        await Task.Delay(1500);
        Assert.False(waiting.IsCompleted);
        await StatusAssert.Is(HttpStatusCode.NoContent, await b.PostAsync($"/api/chat/browser/{chat}/give"));
        Assert.Contains("handed it back", Result(await waiting).GetProperty("text").GetString(), StringComparison.Ordinal);

        // Someone else sees nothing of it.
        var (other, _) = await PersonAsync(f);
        Assert.False((await other.JsonAsync(await other.GetAsync($"/api/chat/browser/{chat}"))).GetProperty("open").GetBoolean());
        await StatusAssert.Is(HttpStatusCode.NotFound, await other.GetAsync($"/api/chat/browser/{chat}/screen"));

        // Closed, its context goes.
        await StatusAssert.Is(HttpStatusCode.NoContent, await b.Http.DeleteAsync(new Uri($"/api/chat/browser/{chat}", UriKind.Relative)));
        Assert.Contains(browser.Commands, c => c.Method == "Target.disposeBrowserContext");
    }

    [Fact]
    public async Task A_model_that_sees_gets_the_screen_after_each_step()
    {
        await using var browser = new FakeBrowser();
        var gateway = new FakeGateway();
        gateway.Models.Add(new GatewayModel("Seeing-Model", 32768, 4096, Vision: true, Tools: true, Thinking: false, null, null, null));
        await using var f = NewApp(browser, gateway);
        var (b, _) = await PersonAsync(f);
        var chat = (await b.JsonAsync(await b.PostAsync("/api/chat/conversations", new { useArgus = false, tools = new[] { "browser" }, model = "Seeing-Model" }))).GetProperty("id").GetGuid();
        var before = app.Model.Requests.Count;
        Assert.False(Result(await SendAsync(b, chat, """[call browser_open {"url":"https://shop.test/"}]""")).GetProperty("isError").GetBoolean());
        // The request after the step: the call's result, then the screen.
        var shown = app.Model.Requests.Skip(before).Select(r => r.Body["messages"]!.AsArray())
            .Where(m => m.Any(x => x!["role"]!.GetValue<string>() == "tool") && m.Last()!["content"] is System.Text.Json.Nodes.JsonArray)
            .Select(m => m.Last()!).First();
        Assert.Equal("user", shown["role"]!.GetValue<string>());
        Assert.Equal("[What the screen shows now, after the step above]", shown["content"]![0]!["text"]!.GetValue<string>());
        Assert.StartsWith("data:image/jpeg;base64,", shown["content"]![1]!["image_url"]!["url"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_proxy_opens_public_sites_on_the_lists_only_and_only_for_the_browser()
    {
        await using var browser = new FakeBrowser();
        var port = FreePort();
        await using var f = app.Create(app.ConnectionStringFor("proxy_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(), new Dictionary<string, string?>
        {
            ["Browser:Url"] = browser.Url, ["Browser:Sites"] = "*.test", ["Browser:BlockedSites"] = "evil.test", ["Browser:ProxyPort"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Browser:ProxyClients"] = "127.0.0.1",
        });
        var proxy = f.Services.GetRequiredService<BrowserProxy>();
        Assert.Null((await proxy.CheckAsync("shop.test", 443, CancellationToken.None)).Refusal);
        Assert.Contains("usual ports", (await proxy.CheckAsync("shop.test", 22, CancellationToken.None)).Refusal, StringComparison.Ordinal);
        // A name that also points inside: the public address is the one used (the proxy connects to what it checked).
        Assert.Equal(IPAddress.Parse("93.184.216.34"), (await proxy.CheckAsync("rebind.test", 443, CancellationToken.None)).Address);

        async Task<string> AskAsync(string request)
        {
            for (var i = 0; ; i++)
            {
                try
                {
                    using var client = new TcpClient();
                    await client.ConnectAsync(IPAddress.Loopback, port);
                    var stream = client.GetStream();
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(request));
                    // The refusal, whole: the proxy closes the connection after it.
                    using var answer = new MemoryStream();
                    await stream.CopyToAsync(answer);
                    return Encoding.UTF8.GetString(answer.ToArray());
                }
                catch (SocketException) when (i < 50)
                {
                    await Task.Delay(100);
                }
            }
        }
        var inside = await AskAsync("CONNECT internal.test:443 HTTP/1.1\r\nHost: internal.test:443\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 403", inside, StringComparison.Ordinal);
        Assert.Contains("inside your network", inside, StringComparison.Ordinal);
        Assert.Contains("never opens", await AskAsync("GET http://evil.test/ HTTP/1.1\r\nHost: evil.test\r\n\r\n"), StringComparison.Ordinal);
        Assert.Contains("not among the sites", await AskAsync("CONNECT example.com:443 HTTP/1.1\r\n\r\n"), StringComparison.Ordinal);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
