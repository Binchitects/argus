using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Llm.Api.Chat;
using Llm.Api.Chat.Tools;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Llm.Tests;

/// <summary>The Web tool: what it may reach (allowed sites, public addresses only), pages as text, and search.</summary>
[Collection(nameof(AppCollection))]
public sealed class WebTests(AppFixture app)
{
    [Theory]
    [InlineData("93.184.216.34", true)]
    [InlineData("8.8.8.8", true)]
    [InlineData("2606:4700:4700::1111", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.20.0.4", false)] // a Docker network: the stack's own services
    [InlineData("192.168.1.10", false)]
    [InlineData("169.254.169.254", false)] // cloud metadata
    [InlineData("100.64.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("fd00::5", false)]
    [InlineData("fe80::1", false)]
    [InlineData("::ffff:10.0.0.1", false)] // an IPv4 LAN address written as IPv6
    [InlineData("64:ff9b::a00:1", false)] // NAT64 into 10.0.0.1
    public void Only_public_addresses_are_reached(string address, bool open) => Assert.Equal(open, WebGuard.IsPublic(IPAddress.Parse(address)));

    [Theory]
    [InlineData("docs.python.org", "docs.python.org", true)]
    [InlineData("DOCS.python.org.", "docs.python.org", true)]
    [InlineData("evil-docs.python.org", "docs.python.org", false)]
    [InlineData("learn.microsoft.com", "*.microsoft.com", true)]
    [InlineData("microsoft.com", "*.microsoft.com", true)]
    [InlineData("microsoft.com.evil.net", "*.microsoft.com", false)]
    [InlineData("notmicrosoft.com", "*.microsoft.com", false)]
    [InlineData("anything.org", "*", true)]
    [InlineData("anything.org", "", false)]
    [InlineData("b.org", "a.org, b.org", true)]
    public void Sites_are_allowed_by_name(string host, string allowed, bool open) => Assert.Equal(open, WebGuard.Allowed(host, allowed));

    [Fact]
    public async Task The_connection_itself_refuses_a_private_address()
    {
        // Past the first check, a name that now points inside (DNS rebinding) fails where the socket is made.
        using var client = new HttpClient(WebFetcher.Handler(app.Web.Resolver));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(new Uri("http://internal.test/")));
        Assert.IsType<WebAccessException>(ex.InnerException);
        ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(new Uri("http://rebind.test/")));
        Assert.IsType<WebAccessException>(ex.InnerException);
    }

    [Fact]
    public void A_page_is_read_as_its_main_text()
    {
        var filler = new string('x', 600);
        var (title, text) = Html.Read($$"""
            <html><head><title>Release notes &amp; more</title><style>.x{}</style><script>alert(1)</script></head>
            <body><nav>Home | About | Login</nav>
            <main><h1>Version 2</h1><p>Faster <b>startup</b>.</p><ul><li>One</li><li>Two</li></ul>
            <table><tr><th>A</th><th>B</th></tr><tr><td>1</td><td>2</td></tr></table>
            <!-- hidden --> <p>{{filler}}</p></main>
            <footer>© Example</footer></body></html>
            """);
        Assert.Equal("Release notes & more", title);
        Assert.Contains("# Version 2", text, StringComparison.Ordinal);
        Assert.Contains("Faster startup.", text, StringComparison.Ordinal);
        Assert.Contains("- One\n- Two", text, StringComparison.Ordinal);
        Assert.Contains("A | B |", text, StringComparison.Ordinal);
        Assert.DoesNotContain("alert", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Login", text, StringComparison.Ordinal);
        Assert.DoesNotContain("hidden", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Example", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task In_deep_research_the_parts_read_the_web_and_the_answer_only_delegates_and_writes()
    {
        await using var f = NewApp("docs.example.test");
        var (b, chat) = await PersonWithWebAsync(f);
        var marker = "research-" + Guid.NewGuid().ToString("N")[..8];
        // The answer's own model tries to read a page itself.
        var res = await b.PostAsync($"/api/chat/conversations/{chat}/messages",
            new { content = $"{marker} [call fetch_page {{\"url\":\"https://docs.example.test/a\"}}]", research = true });
        var events = await EventsAsync(res);
        var refused = events.Single(e => e.GetProperty("type").GetString() == "tool_result");
        Assert.Contains("In deep research the parts read the web", refused.GetProperty("text").GetString(), StringComparison.Ordinal);
        // It is offered delegate, and not the web's functions (the parts get those), nor told it can read the web.
        var first = AnswerRequests(marker).First();
        var offered = first["tools"]!.AsArray().Select(t => t!["function"]!["name"]!.GetValue<string>()).ToList();
        Assert.Contains("delegate", offered);
        Assert.DoesNotContain("fetch_page", offered);
        Assert.DoesNotContain("web_search", offered);
        var system = first["messages"]![0]!["content"]!.GetValue<string>();
        Assert.Contains("twice in all at most", system, StringComparison.Ordinal);
        Assert.DoesNotContain(WebNotes, system, StringComparison.Ordinal);
    }

    [Fact]
    public async Task In_deep_research_a_web_that_asks_first_stays_with_the_answer_which_asks_the_person()
    {
        await using var f = NewApp("docs.example.test");
        var (b, chat) = await PersonWithWebAsync(f, askFirst: true);
        app.Web.Html("https://docs.example.test/asked", "<html><head><title>Asked</title></head><body><p>Read once allowed.</p></body></html>");
        var marker = "research-" + Guid.NewGuid().ToString("N")[..8];
        var answering = b.PostAsync($"/api/chat/conversations/{chat}/messages",
            new { content = $"{marker} [call fetch_page {{\"url\":\"https://docs.example.test/asked\"}}]", research = true });
        // A part could not ask the person, so the answer reads the web itself: its call waits to be allowed, not refused.
        for (var i = 0; (await b.PostAsync($"/api/chat/conversations/{chat}/tool-calls/call_1", new { allow = true })).StatusCode != HttpStatusCode.NoContent; i++)
        {
            Assert.True(i < 100, "the call never waited to be allowed");
            await Task.Delay(100);
        }
        var events = await EventsAsync(await answering);
        Assert.Equal("web", events.Single(e => e.GetProperty("type").GetString() == "approval").GetProperty("tool").GetString());
        var read = events.Single(e => e.GetProperty("type").GetString() == "tool_result");
        Assert.False(read.GetProperty("isError").GetBoolean());
        Assert.Contains("Read once allowed.", read.GetProperty("text").GetString(), StringComparison.Ordinal);
        // It is offered the web and told to research with it.
        var first = AnswerRequests(marker).First();
        Assert.Contains("fetch_page", first["tools"]!.AsArray().Select(t => t!["function"]!["name"]!.GetValue<string>()));
        var system = first["messages"]![0]!["content"]!.GetValue<string>();
        Assert.Contains(WebNotes, system, StringComparison.Ordinal);
        Assert.Contains("research that too", system, StringComparison.Ordinal);
        Assert.DoesNotContain("The parts read the web", system, StringComparison.Ordinal);
        Assert.Contains("research each with your tools", first["messages"]!.AsArray().Last(m => m!["role"]!.GetValue<string>() == "user")!["content"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deep_research_delegates_in_two_rounds_at_most_and_only_delegations_whose_parts_ran_count()
    {
        await using var f = NewApp("docs.example.test", searchUrl: "http://searxng.test:8080");
        var (b, chat) = await PersonWithWebAsync(f);
        var marker = "research-" + Guid.NewGuid().ToString("N")[..8];
        object Delegate(params string[] titles) =>
            new { name = "delegate", arguments = new { tasks = titles.Select(t => new { title = t, instructions = $"{marker}: look up {t}." }).ToArray() } };
        // One part is refused (no delegation), two calls in one round are one delegation, the gaps the second; then calling is off.
        var script = JsonSerializer.Serialize(new[]
        {
            new[] { Delegate("Only") },
            new[] { Delegate("A", "B"), Delegate("C", "D") },
            new[] { Delegate("Gap 1", "Gap 2") },
            new[] { Delegate("More", "Again") },
        });
        var events = await EventsAsync(await b.PostAsync($"/api/chat/conversations/{chat}/messages", new { content = $"{marker} [script {script}]", research = true }));

        var results = events.Where(e => e.GetProperty("type").GetString() == "tool_result").ToList();
        Assert.Equal(4, results.Count);
        Assert.Contains("2 to 10 tasks", results[0].GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.All(results.Skip(1), r => Assert.False(r.GetProperty("isError").GetBoolean()));
        var answer = AnswerRequests(marker);
        Assert.Equal(4, answer.Count);
        Assert.All(answer.Take(3), r => Assert.False(r.ContainsKey("tool_choice")));
        Assert.Equal("none", answer[3]["tool_choice"]!.GetValue<string>());
        Assert.EndsWith(ChatService.LastRoundNote, answer[3]["messages"]!.AsArray().Last()!["content"]!.GetValue<string>(), StringComparison.Ordinal);

        // The parts (six: the third delegation never ran) get the web's functions and notes, and not the answer's steps.
        var parts = app.Model.Requests.Select(r => r.Body)
            .Where(r => r["messages"]![0]!["content"]!.GetValue<string>().Contains("You are a sub-agent", StringComparison.Ordinal)
                && r["messages"]![1]!["content"]!.GetValue<string>().StartsWith(marker, StringComparison.Ordinal))
            .ToList();
        Assert.Equal(6, parts.Count);
        Assert.All(parts, r =>
        {
            var functions = r["tools"]!.AsArray().Select(t => t!["function"]!["name"]!.GetValue<string>()).ToList();
            Assert.Contains("web_search", functions);
            Assert.Contains("fetch_page", functions);
            var system = r["messages"]![0]!["content"]!.GetValue<string>();
            Assert.Contains(WebNotes, system, StringComparison.Ordinal);
            Assert.DoesNotContain("Deep research:", system, StringComparison.Ordinal);
        });
    }

    /// <summary>The start of the web tool's notes in a system prompt.</summary>
    private const string WebNotes = "You can read the web (";

    /// <summary>The answer's own requests for a question with this marker (not its title's, nor its sub-agents').</summary>
    private List<JsonObject> AnswerRequests(string marker) =>
        [.. app.Model.Requests.Select(r => r.Body).Where(r => r["tools"] is not null && r["messages"]!.ToJsonString().Contains(marker, StringComparison.Ordinal)
            && !r["messages"]![0]!["content"]!.GetValue<string>().Contains("You are a sub-agent", StringComparison.Ordinal))];

    private static async Task<List<JsonElement>> EventsAsync(HttpResponseMessage res) =>
        [.. (await res.Content.ReadAsStringAsync()).Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.StartsWith("data: ", StringComparison.Ordinal)).Select(l => JsonDocument.Parse(l[6..]).RootElement)];

    private WebApplicationFactory<Program> NewApp(string? sites, string? searchUrl = null) =>
        app.Create(app.ConnectionStringFor("web_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(), new Dictionary<string, string?>
        {
            ["Web:AllowedSites"] = sites, ["Web:SearchUrl"] = searchUrl,
        });

    private static async Task<(TestBrowser B, Guid Chat)> PersonWithWebAsync(WebApplicationFactory<Program> f, bool turnOn = true, bool askFirst = false)
    {
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        if (turnOn)
        {
            await StatusAssert.Is(HttpStatusCode.NoContent, await admin.Http.PutAsJsonAsync(new Uri("/api/admin/tools/web", UriKind.Relative),
                new { enabled = true, audience = "Everyone", groups = Array.Empty<Guid>(), onByDefault = true, askFirst }));
        }
        var name = "w" + Guid.NewGuid().ToString("N")[..10];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        var b = await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!);
        var chat = (await b.JsonAsync(await b.PostAsync("/api/chat/conversations", new { }))).GetProperty("id").GetGuid();
        return (b, chat);
    }

    private static async Task<JsonElement> CallAsync(TestBrowser b, Guid chat, string call)
    {
        var res = await b.PostAsync($"/api/chat/conversations/{chat}/messages", new { content = call });
        return (await res.Content.ReadAsStringAsync()).Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.StartsWith("data: ", StringComparison.Ordinal)).Select(l => JsonDocument.Parse(l[6..]).RootElement)
            .Single(e => e.GetProperty("type").GetString() == "tool_result");
    }

    private static async Task<List<string>> ToolsAsync(TestBrowser b) =>
        [.. (await b.JsonAsync(await b.GetAsync("/api/chat/config"))).GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("id").GetString()!)];

    [Fact]
    public async Task The_web_is_off_until_an_admin_turns_it_on_and_allows_sites()
    {
        await using (var none = NewApp(sites: null))
        {
            var (b, _) = await PersonWithWebAsync(none);
            Assert.DoesNotContain("web", await ToolsAsync(b));
        }
        await using var f = NewApp(sites: "docs.example.test");
        var (off, _) = await PersonWithWebAsync(f, turnOn: false);
        Assert.DoesNotContain("web", await ToolsAsync(off));
        var (on, _) = await PersonWithWebAsync(f);
        Assert.Contains("web", await ToolsAsync(on));
    }

    [Fact]
    public async Task Pages_of_allowed_sites_are_read_in_parts_and_nothing_else_is_reached()
    {
        await using var f = NewApp(sites: "docs.example.test, *.cdn.example.test");
        var (b, chat) = await PersonWithWebAsync(f);
        var body = string.Join("", Enumerable.Range(1, 3000).Select(i => $"<p>Paragraph {i} of the guide.</p>"));
        app.Web.Html("https://docs.example.test/guide", $"<html><head><title>The guide</title></head><body><nav>Menu</nav>{body}</body></html>");
        app.Web.Redirect("https://docs.example.test/old", "https://docs.example.test/guide");
        app.Web.Redirect("https://docs.example.test/out", "https://elsewhere.test/");
        app.Web.Redirect("https://docs.example.test/in", "http://internal.test/admin");

        var page = JsonDocument.Parse((await CallAsync(b, chat, """[call fetch_page {"url":"https://docs.example.test/old"}]""")).GetProperty("text").GetString()!).RootElement;
        Assert.Equal("https://docs.example.test/guide", page.GetProperty("url").GetString());
        Assert.Equal("The guide", page.GetProperty("title").GetString());
        Assert.StartsWith("Paragraph 1 of the guide.", page.GetProperty("text").GetString(), StringComparison.Ordinal);
        var next = page.GetProperty("next_start").GetInt32();
        var more = JsonDocument.Parse((await CallAsync(b, chat, $$"""[call fetch_page {"url":"https://docs.example.test/guide","start":{{next}}}]""")).GetProperty("text").GetString()!).RootElement;
        Assert.Equal(next, more.GetProperty("start").GetInt32());

        async Task RefusedAsync(string url, string why)
        {
            var result = await CallAsync(b, chat, $$"""[call fetch_page {"url":"{{url}}"}]""");
            Assert.True(result.GetProperty("isError").GetBoolean(), url);
            Assert.Contains(why, result.GetProperty("text").GetString(), StringComparison.Ordinal);
        }
        await RefusedAsync("https://elsewhere.test/", "not one of the sites the chat may open");
        await RefusedAsync("https://docs.example.test/out", "elsewhere.test is not one of the sites");
        await RefusedAsync("https://docs.example.test/in", "not one of the sites");
        await RefusedAsync("file:///etc/passwd", "Only http and https");
        await RefusedAsync("https://user:pw@docs.example.test/guide", "user name or password");
        Assert.DoesNotContain(app.Web.Requests, u => u.Host is "elsewhere.test" or "internal.test");
    }

    [Fact]
    public async Task A_long_page_is_read_once_and_comes_back_as_its_passages_about_the_focus()
    {
        await using var f = NewApp(sites: "manual.example.test");
        var (b, chat) = await PersonWithWebAsync(f);
        var url = "https://manual.example.test/" + Guid.NewGuid().ToString("N")[..8];
        var filler = string.Join("", Enumerable.Range(1, 400).Select(i => $"<p>Section {i} talks about installing and upgrading the product on servers.</p>"));
        app.Web.Html(url, $"<html><head><title>Manual</title></head><body>{filler}<h2>Licensing</h2><p>A license key is renewed every year from the portal.</p>{filler}</body></html>");

        var found = JsonDocument.Parse((await CallAsync(b, chat, $$"""[call fetch_page {"url":"{{url}}","focus":"license key renewal"}]""")).GetProperty("text").GetString()!).RootElement;
        var passages = found.GetProperty("passages").EnumerateArray().ToList();
        Assert.Contains(passages, p => p.GetProperty("text").GetString() == "A license key is renewed every year from the portal.");
        Assert.Contains(passages, p => p.GetProperty("text").GetString() == "## Licensing");
        Assert.True(found.GetProperty("total_characters").GetInt32() > 20_000);

        // Read on in parts: from the cache, not downloaded again.
        var start = passages.First(p => p.GetProperty("text").GetString()!.StartsWith("A license", StringComparison.Ordinal)).GetProperty("start").GetInt32();
        var around = JsonDocument.Parse((await CallAsync(b, chat, $$"""[call fetch_page {"url":"{{url}}","start":{{start}}}]""")).GetProperty("text").GetString()!).RootElement;
        Assert.Contains("A license key is renewed", around.GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.Single(app.Web.Requests, u => u.AbsoluteUri == url);
    }

    [Fact]
    public async Task Even_an_allowed_name_is_not_opened_when_it_points_inside()
    {
        await using var f = NewApp(sites: "*");
        var (b, chat) = await PersonWithWebAsync(f);
        foreach (var url in new[] { "http://internal.test/", "http://rebind.test/", "http://127.0.0.1:8080/", "http://[::1]/", "http://169.254.169.254/latest/meta-data/" })
        {
            var result = await CallAsync(b, chat, $$"""[call fetch_page {"url":"{{url}}"}]""");
            Assert.True(result.GetProperty("isError").GetBoolean(), url);
            Assert.Contains("not on the public internet", result.GetProperty("text").GetString(), StringComparison.Ordinal);
        }
        Assert.DoesNotContain(app.Web.Requests, u => u.Host is "internal.test" or "rebind.test" or "127.0.0.1" or "[::1]" or "169.254.169.254");
    }

    [Fact]
    public async Task Search_finds_pages_and_says_which_may_be_opened()
    {
        await using var f = NewApp(sites: "docs.example.test", searchUrl: "http://searxng.test:8080");
        var (b, chat) = await PersonWithWebAsync(f);
        app.Web.Pages["http://searxng.test:8080/search"] = () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"results":[{"title":"Guide","url":"https://docs.example.test/guide","content":"How to start"},{"title":"Forum","url":"https://forum.test/t/1","content":"A thread"}]}""", Encoding.UTF8, "application/json"),
        };
        var result = JsonDocument.Parse((await CallAsync(b, chat, """[call web_search {"query":"how to start"}]""")).GetProperty("text").GetString()!).RootElement;
        var hits = result.GetProperty("results").EnumerateArray().ToDictionary(h => h.GetProperty("url").GetString()!, h => h.GetProperty("can_open").GetBoolean());
        Assert.True(hits["https://docs.example.test/guide"]);
        Assert.False(hits["https://forum.test/t/1"]);
        Assert.Contains(app.Web.Requests, u => u.Host == "searxng.test" && u.Query.Contains("q=how%20to%20start", StringComparison.Ordinal) && u.Query.Contains("format=json", StringComparison.Ordinal));
    }
}
