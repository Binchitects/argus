using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Llm.Api.Chat;
using Llm.Api.Chat.Tools;
using Llm.Api.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Llm.Tests;

/// <summary>
/// A test against a real Laya server (deploy/services/laya), run only when LAYA_URL names one, e.g. a
/// throwaway container: <c>tools/dn test tests/Llm.Tests --filter RealLaya -e LAYA_URL=http://127.0.0.1:18000</c>.
/// </summary>
public sealed class RealLayaFactAttribute : FactAttribute
{
    public static readonly string? Url = Environment.GetEnvironmentVariable("LAYA_URL") is { Length: > 0 } url ? url.TrimEnd('/') : null;

    public RealLayaFactAttribute()
    {
        if (Url is null)
        {
            Skip = "No Laya to ask: set LAYA_URL to a running laya server to run it.";
        }
    }
}

/// <summary>Decide (Laya): typed questions about a text answered with probabilities, in the chat and over Arena MCP, and Laya's checkpoints fetched into the library.</summary>
[Collection(nameof(AppCollection))]
public sealed class LayaTests(AppFixture app, ITestOutputHelper output) : IDisposable
{
    private readonly FakeLaya _laya = new();
    private readonly string _library = Directory.CreateTempSubdirectory("llm-laya-").FullName;

    public void Dispose() => Directory.Delete(_library, recursive: true);

    /// <summary>
    /// Its own app and database, the laya module running (or not), and the fake Laya behind it, or the
    /// real one at <paramref name="real"/>; with <paramref name="leads"/>, one replica that leads at once.
    /// </summary>
    private WebApplicationFactory<Program> NewApp(bool module = true, bool leads = false, string? real = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Auth:DataKey"] = "a-data-key-for-laya-tests", ["Modules:laya"] = module ? "true" : "false", ["Engine:LibraryDir"] = _library,
        };
        if (leads)
        {
            settings["Replicas:Enabled"] = "false";
        }
        if (real is not null)
        {
            settings["Laya:Url"] = real;
        }
        return app.Create(app.ConnectionStringFor("laya_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(), settings,
            s =>
            {
                if (real is null)
                {
                    s.AddHttpClient(LayaClient.Client).ConfigurePrimaryHttpMessageHandler(() => _laya);
                }
            });
    }

    /// <summary>The Arena MCP session of the person with this key.</summary>
    private static async Task<McpSession> McpAsync(WebApplicationFactory<Program> f, string key)
    {
        var http = f.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"https://{AppFixture.Domain}"), AllowAutoRedirect = false, HandleCookies = false });
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return await Mcp.ConnectAsync(http, new Uri($"https://{AppFixture.Domain}/mcp"), new Dictionary<string, string> { ["Authorization"] = "Bearer " + key }, "Arena", CancellationToken.None);
    }

    private static async Task<(TestBrowser Browser, string Email, string Key)> PersonAsync(WebApplicationFactory<Program> f)
    {
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "l" + Guid.NewGuid().ToString("N")[..10];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        return (await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!), $"{name}@example.test", made.GetProperty("apiKey").GetString()!);
    }

    private static async Task<Guid> NewChatAsync(TestBrowser b)
    {
        var res = await b.PostAsync("/api/chat/conversations", new { useArgus = false });
        await StatusAssert.Is(HttpStatusCode.Created, res);
        return (await b.JsonAsync(res)).GetProperty("id").GetGuid();
    }

    private static async Task<List<JsonElement>> SendAsync(TestBrowser b, Guid id, string text)
    {
        var res = await b.PostAsync($"/api/chat/conversations/{id}/messages", new { content = text });
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}");
        return [.. (await res.Content.ReadAsStringAsync()).Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.StartsWith("data: ", StringComparison.Ordinal)).Select(l => JsonDocument.Parse(l[6..]).RootElement)];
    }

    /// <summary>The model calls decide with these arguments: the tool's result, and whether it failed.</summary>
    private static async Task<(JsonNode? Answer, string Text, bool IsError)> DecideAsync(TestBrowser b, Guid chat, object call)
    {
        var events = await SendAsync(b, chat, $"Decide: [call decide {JsonSerializer.Serialize(call)}]");
        Assert.Equal("laya", events.Single(e => e.GetProperty("type").GetString() == "tool_call").GetProperty("tool").GetString());
        var result = events.Single(e => e.GetProperty("type").GetString() == "tool_result");
        var text = result.GetProperty("text").GetString()!;
        var isError = result.GetProperty("isError").GetBoolean();
        return (isError ? null : JsonNode.Parse(text), text, isError);
    }

    private static async Task<JsonElement> ToolRowAsync(TestBrowser admin) =>
        (await admin.JsonAsync(await admin.GetAsync("/api/admin/tools"))).EnumerateArray().Single(t => t.GetProperty("id").GetString() == "laya");

    private static async Task<List<string>> ToolsInConfigAsync(TestBrowser b) =>
        [.. (await b.JsonAsync(await b.GetAsync("/api/chat/config"))).GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("id").GetString()!)];

    private static readonly object Triage = new
    {
        state = "Subject: Charged twice for March. Please refund the duplicate today or we cancel our plan.",
        questions = new object[]
        {
            new { id = "department", type = "choice", question = "Which department should handle this request?", options = new { billing = "invoices, payments, refunds", technical = "bugs, outages" } },
            new { id = "urgency", type = "score", question = "How urgent is it?", levels = new[] { "not urgent", "soon", "blocking" } },
            new { id = "churn", type = "noul", question = "Does the customer threaten to cancel?" },
        },
    };

    [Fact]
    public async Task Decide_is_offered_only_while_the_laya_module_runs_and_has_a_checkpoint_loaded()
    {
        await using (var off = NewApp(module: false))
        {
            var admin = await new TestBrowser(off).SignedInAsync("admin", AppFixture.AdminPassword);
            var row = await ToolRowAsync(admin);
            Assert.Equal("Decide (Laya)", row.GetProperty("title").GetString());
            Assert.Equal("scale", row.GetProperty("icon").GetString());
            Assert.Contains("COMPOSE_PROFILES=laya", row.GetProperty("unavailable").GetString(), StringComparison.Ordinal);
            var (b, _, _) = await PersonAsync(off);
            Assert.DoesNotContain("laya", await ToolsInConfigAsync(b));
        }

        _laya.Loading = true;
        await using (var loading = NewApp())
        {
            var admin = await new TestBrowser(loading).SignedInAsync("admin", AppFixture.AdminPassword);
            Assert.Contains("still fetching or loading", (await ToolRowAsync(admin)).GetProperty("unavailable").GetString(), StringComparison.Ordinal);
        }

        _laya.Loading = false;
        await using var ready = NewApp();
        var person = await PersonAsync(ready);
        Assert.Contains("laya", await ToolsInConfigAsync(person.Browser));
        var on = await ToolRowAsync(await new TestBrowser(ready).SignedInAsync("admin", AppFixture.AdminPassword));
        Assert.Equal(JsonValueKind.Null, on.GetProperty("unavailable").ValueKind);
    }

    [Fact]
    public async Task The_model_asks_typed_questions_and_reads_layas_probabilities_back()
    {
        await using var f = NewApp();
        var (b, _, _) = await PersonAsync(f);
        var chat = await NewChatAsync(b);

        var (answer, _, isError) = await DecideAsync(b, chat, Triage);
        Assert.False(isError);
        Assert.Equal("billing", answer!["answers"]!["department"]!["choice"]!.GetValue<string>());
        // Laya's numbers, to three places; its act-or-escalate head is left out.
        Assert.Equal(0.972, answer["answers"]!["department"]!["probabilities"]!["billing"]!.GetValue<double>());
        Assert.Equal(1.387, answer["answers"]!["urgency"]!["score"]!.GetValue<double>());
        Assert.Equal(0.85, answer["answers"]!["churn"]!["noul"]!.GetValue<double>());
        Assert.Null(answer["answers"]!["churn"]!["action"]);
        Assert.Equal("english", answer["checkpoint"]!.GetValue<string>());
        Assert.True(answer["calibrated"]!.GetValue<bool>());
        Assert.Null(answer["note"]);
        Assert.Null(answer["truncated"]);

        // What Laya was sent: its own shape, every question in one call, the checkpoint by script, with its length.
        var sent = _laya.Requests.Single();
        Assert.True(_laya.Lengths.Single() > 0);
        Assert.Equal("auto", sent["checkpoint"]!.GetValue<string>());
        Assert.StartsWith("Subject: Charged twice", sent["state"]!.GetValue<string>(), StringComparison.Ordinal);
        var questions = sent["questions"]!.AsObject();
        Assert.Equal(["department", "urgency", "churn"], questions.Select(q => q.Key));
        Assert.Equal("Which department should handle this request?", questions["department"]!["instructions"]!.GetValue<string>());
        Assert.Equal("invoices, payments, refunds", questions["department"]!["criteria"]!["billing"]!.GetValue<string>());
        Assert.Equal(["not urgent", "soon", "blocking"], questions["urgency"]!["criteria"]!.AsArray().Select(l => l!.GetValue<string>()));
        Assert.Equal("noul", questions["churn"]!["type"]!.GetValue<string>());
        Assert.Null(questions["churn"]!["criteria"]);

        // Persian goes to the multilingual checkpoint, whose probabilities are said to be uncalibrated.
        (answer, _, _) = await DecideAsync(b, chat, new { state = "سفارش من دو هفته است که نرسیده.", questions = new[] { new { id = "late", type = "noul", question = "Is the order late?" } } });
        Assert.Equal("multilingual", answer!["checkpoint"]!.GetValue<string>());
        Assert.Contains("not calibrated", answer["note"]!.GetValue<string>(), StringComparison.Ordinal);

        // A state Laya cut short: how much it read, said (Code Arena reads the rest again in parts).
        (answer, _, _) = await DecideAsync(b, chat, new { state = "long " + new string('x', 3000), questions = new[] { new { id = "q", type = "noul", question = "Is it long?" } } });
        Assert.Equal(812, answer!["truncated"]!["tokens"]!.GetValue<int>());
        Assert.Equal(478, answer["truncated"]!["read"]!.GetValue<int>());
        Assert.Equal("Laya read only the first 478 of the state's 812 tokens: the answers say nothing of the rest. Shorten it, with what matters first, or split it.",
            answer["note"]!.GetValue<string>());

        // Questions Laya would answer badly are refused before it is asked, with what to fix.
        var asked = _laya.Requests.Count;
        var refusals = new (object Call, string Says)[]
        {
            (new { state = "x", questions = new[] { new { id = "q", type = "choice", question = "Which?", options = new { only = "one" } } } }, "2 to 20 options"),
            (new { state = "x", questions = new[] { new { id = "q", type = "score", question = "How much?", levels = new[] { "some" } } } }, "2 to 10 levels"),
            (new { state = "x", questions = new[] { new { id = "q", type = "maybe", question = "?" } } }, "choice, score or noul"),
            (new { state = "x", questions = new[] { new { id = "q", type = "noul", question = "A?" }, new { id = "q", type = "noul", question = "B?" } } }, "Two questions are named q"),
            (new { state = "x", questions = new[] { new { id = "q", type = "noul", question = "A?", options = new { maybe = "x" } } } }, "\"true\""),
            (new { state = "  ", questions = new[] { new { id = "q", type = "noul", question = "A?" } } }, "Give the text"),
            (new { state = "x", questions = Array.Empty<object>() }, "at least one question"),
            (new { state = "x", questions = new[] { new { id = "q", type = "noul", question = "A?" } }, checkpoint = "typed-decisions" }, "auto, english or multilingual"),
        };
        foreach (var (call, says) in refusals)
        {
            var (_, text, failed) = await DecideAsync(b, chat, call);
            Assert.True(failed, text);
            Assert.Contains(says, text, StringComparison.Ordinal);
        }
        Assert.Equal(asked, _laya.Requests.Count);

        // Laya's own refusal comes back in its words.
        var (_, refused, wasError) = await DecideAsync(b, chat, new { state = "refuse", questions = new[] { new { id = "q", type = "noul", question = "A?" } } });
        Assert.True(wasError);
        Assert.Contains("head_max_len", refused, StringComparison.Ordinal);
    }

    [Fact]
    public async Task When_laya_stops_answering_the_call_fails_plainly_and_the_tool_is_offered_no_more()
    {
        await using var f = NewApp();
        var (b, _, _) = await PersonAsync(f);
        var chat = await NewChatAsync(b);
        Assert.Contains("laya", await ToolsInConfigAsync(b));

        _laya.Down = true;
        var (_, text, isError) = await DecideAsync(b, chat, Triage);
        Assert.True(isError);
        Assert.Contains("Laya cannot be reached", text, StringComparison.Ordinal);
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        Assert.Contains("does not answer", (await ToolRowAsync(admin)).GetProperty("unavailable").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("laya", await ToolsInConfigAsync(b));
    }

    [Fact]
    public async Task Arena_MCP_serves_decide_to_outside_agents_as_a_tool_that_changes_nothing()
    {
        await using var f = NewApp();
        var (_, email, key) = await PersonAsync(f);
        var session = await McpAsync(f, key);

        var decide = (await session.ToolsAsync(CancellationToken.None)).Single(t => t!["name"]!.GetValue<string>() == LayaTool.Function)!;
        Assert.Equal(["state", "questions"], decide["inputSchema"]!["required"]!.AsArray().Select(r => r!.GetValue<string>()));
        // It changes nothing: no destructive hint, so Code Arena runs it without asking.
        Assert.Null(decide["annotations"]);
        Assert.Contains("noul", decide["description"]!.GetValue<string>(), StringComparison.Ordinal);

        var arguments = JsonNode.Parse(JsonSerializer.Serialize(Triage))!.AsObject();
        var (text, isError) = await session.CallAsync(LayaTool.Function, arguments, null, CancellationToken.None);
        Assert.False(isError, text);
        Assert.Equal("billing", JsonNode.Parse(text)!["answers"]!["department"]!["choice"]!.GetValue<string>());

        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var audit = (await admin.JsonAsync(await admin.GetAsync("/api/admin/audit"))).EnumerateArray().Single(e => e.GetProperty("action").GetString() == "mcp.call");
        Assert.Equal("laya", audit.GetProperty("target").GetString());
        Assert.Equal(email.Split('@')[0], audit.GetProperty("actor").GetString());
    }

    [Fact]
    public async Task A_running_laya_module_has_both_checkpoints_fetched_into_the_library_each_in_its_folder()
    {
        // One replica, so it leads at once: the setup runs as the app starts.
        await using var f = NewApp(leads: true);
        _ = f.Server;
        string English(string file) => Path.Combine(_library, "laya", "english", file);
        string Multilingual(string file) => Path.Combine(_library, "laya", "multilingual", file);
        for (var i = 0; i < 300 && !MediaModels.LayaFiles.All(n => File.Exists(English(n)) && File.Exists(Multilingual(n))); i++)
        {
            await Task.Delay(100);
        }
        foreach (var name in MediaModels.LayaFiles)
        {
            Assert.Equal(FakeHuggingFace.LayaFiles[name], await File.ReadAllBytesAsync(English(name)));
            Assert.Equal(FakeHuggingFace.LayaFiles["multilingual/" + name], await File.ReadAllBytesAsync(Multilingual(name)));
        }
        // Only the two checkpoints the module reads: not the typed-decisions one, not the README.
        Assert.False(Directory.Exists(Path.Combine(_library, "laya", "typed-decisions")));
        Assert.False(File.Exists(English("README.md")));
        Assert.DoesNotContain(app.HuggingFace.Downloads, d => d.Path.Contains("typed-decisions", StringComparison.Ordinal));

        // Admin → Models lists the setup's downloads where their files went.
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var downloads = (await admin.JsonAsync(await admin.GetAsync("/api/admin/models/hf/downloads"))).EnumerateArray().ToList();
        Assert.Equal(2, downloads.Count);
        Assert.All(downloads, d => Assert.Equal("setup", d.GetProperty("createdBy").GetString()));
        var places = downloads.SelectMany(d => d.GetProperty("files").EnumerateArray().Select(x => x.GetProperty("library").GetString())).ToList();
        Assert.Contains("laya/multilingual/tokenizer/tokenizer.json", places);
        Assert.Contains("laya/english/model.safetensors", places);
    }

    [RealLayaFact]
    public async Task A_real_Laya_answers_an_English_incident_in_the_chat_and_a_Persian_complaint_over_Arena_MCP()
    {
        await using var f = NewApp(real: RealLayaFactAttribute.Url);
        var (b, _, key) = await PersonAsync(f);
        var chat = await NewChatAsync(b);

        var incident = new
        {
            state = "Incident: since 09:10 every checkout on the web shop fails with HTTP 500 and customers cannot pay. " +
                    "The payment service logs 'connection refused' to the database. Revenue is being lost every minute.",
            questions = new object[]
            {
                new { id = "team", type = "choice", question = "Which team should handle this incident?", options = new { database = "database servers, connections, replication", frontend = "web pages, styling, browser bugs", billing = "invoices, refunds, pricing", network = "DNS, load balancers, firewalls" } },
                new { id = "severity", type = "score", question = "How severe is this incident?", levels = new[] { "minor", "moderate", "major", "critical" } },
                new { id = "customers", type = "noul", question = "Are customers affected?" },
            },
        };
        var (answer, text, isError) = await DecideAsync(b, chat, incident);
        output.WriteLine(text);
        Assert.False(isError, text);
        Assert.Equal("english", answer!["checkpoint"]!.GetValue<string>());
        Assert.True(answer["calibrated"]!.GetValue<bool>());
        var team = answer["answers"]!["team"]!;
        Assert.Equal(1, team["probabilities"]!.AsObject().Sum(p => p.Value!.GetValue<double>()), 2);
        Assert.Contains(team["choice"]!.GetValue<string>(), new[] { "database", "frontend", "billing", "network" });
        // Major or worse, and customers affected: what the text says plainly.
        Assert.InRange(answer["answers"]!["severity"]!["score"]!.GetValue<double>(), 1.5, 3);
        Assert.True(answer["answers"]!["customers"]!["noul"]!.GetValue<double>() >= 0.85);
        Assert.True(answer["ms"]!.GetValue<double>() > 0);

        var session = await McpAsync(f, key);
        var complaint = JsonNode.Parse(JsonSerializer.Serialize(new
        {
            state = "سلام، سفارش من دو هفته است که نرسیده و هیچ کس جواب تلفن را نمی‌دهد. اگر تا فردا پول من را برنگردانید دیگر از شما خرید نمی‌کنم.",
            questions = new object[]
            {
                new { id = "topic", type = "choice", question = "What is the message about?", options = new { delivery = "a late or lost order", refund = "money back", product = "a broken or wrong product", account = "sign-in, password" } },
                new { id = "late", type = "noul", question = "Is the order late?" },
            },
        }))!.AsObject();
        var (said, failed) = await session.CallAsync(LayaTool.Function, complaint, null, CancellationToken.None);
        output.WriteLine(said);
        Assert.False(failed, said);
        var persian = JsonNode.Parse(said)!;
        Assert.Equal("multilingual", persian["checkpoint"]!.GetValue<string>());
        Assert.Contains("not calibrated", persian["note"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("delivery", persian["answers"]!["topic"]!["choice"]!.GetValue<string>());
        Assert.True(persian["answers"]!["late"]!["noul"]!.GetValue<double>() >= 0.85);
    }
}
