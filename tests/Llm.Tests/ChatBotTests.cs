using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Llm.Api.Bots;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>
/// The chat bots: a question in Slack, Mattermost or Teams answered in its thread as the
/// person who asked (matched by email), each platform's own check of who is calling; and
/// email in, answered by email.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class ChatBotTests(AppFixture app) : IDisposable
{
    private const string SlackSecret = "slack-signing-secret-for-tests";
    private const string TeamsApp = "00000000-aaaa-bbbb-cccc-000000000001";

    private readonly FakeChatPlatforms _platforms = new();

    private WebApplicationFactory<Program> NewApp(Dictionary<string, string?>? more = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Auth:DataKey"] = "a-data-key-for-bot-tests",
            ["Bots:Slack:SigningSecret"] = SlackSecret, ["Bots:Slack:BotToken"] = "xoxb-test",
            ["Bots:Mattermost:Url"] = "https://mattermost.test", ["Bots:Mattermost:BotToken"] = "mm-bot-token", ["Bots:Mattermost:Tokens"] = "hook-token, command-token",
            ["Bots:Teams:AppId"] = TeamsApp, ["Bots:Teams:AppPassword"] = "teams-secret",
        };
        foreach (var (k, v) in more ?? [])
        {
            settings[k] = v;
        }
        return app.Create(app.ConnectionStringFor("bots_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(), settings)
            .WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddHttpClient(BotEndpoints.Client).ConfigurePrimaryHttpMessageHandler(() => _platforms)));
    }

    private static async Task<(TestBrowser Browser, string Email)> PersonAsync(WebApplicationFactory<Program> f)
    {
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "b" + Guid.NewGuid().ToString("N")[..10];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        return (await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!), $"{name}@example.test");
    }

    private static Task<HttpResponseMessage> SlackAsync(WebApplicationFactory<Program> f, object body, string secret = SlackSecret, long? at = null)
    {
        var json = JsonSerializer.Serialize(body);
        var ts = (at ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()).ToString(CultureInfo.InvariantCulture);
        var signature = "v0=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"v0:{ts}:{json}")));
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/bots/slack") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        request.Headers.Add("X-Slack-Request-Timestamp", ts);
        request.Headers.Add("X-Slack-Signature", signature);
        return f.CreateClient().SendAsync(request);
    }

    private static object Mention(string user, string text, string channel = "C1", string ts = "1700000000.000100", string? thread = null, string? id = null, string type = "app_mention",
        string? channelType = null) => new
    {
        type = "event_callback", team_id = "T1", event_id = id ?? Guid.NewGuid().ToString("N"),
        @event = new { type, user, text, channel, ts, thread_ts = thread, team = "T1", channel_type = channelType },
    };

    public void Dispose() => _platforms.Dispose();

    private static async Task<List<JsonElement>> ChatsAsync(TestBrowser b) => [.. (await b.JsonAsync(await b.GetAsync("/api/chat/conversations"))).EnumerateArray()];

    [Fact]
    public async Task A_Slack_mention_is_answered_in_its_thread_as_the_person_who_asked()
    {
        await using var f = NewApp();
        var (b, email) = await PersonAsync(f);
        _platforms.SlackEmails["U1"] = email;

        // Slack checks the address with a challenge; a wrong or stale signature is refused.
        var challenge = await SlackAsync(f, new { type = "url_verification", challenge = "abc123" });
        Assert.Equal("abc123", JsonDocument.Parse(await challenge.Content.ReadAsStringAsync()).RootElement.GetProperty("challenge").GetString());
        await StatusAssert.Is(HttpStatusCode.Unauthorized, await SlackAsync(f, Mention("U1", "hi"), secret: "wrong"));
        await StatusAssert.Is(HttpStatusCode.Unauthorized, await SlackAsync(f, Mention("U1", "hi"), at: DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds()));

        await StatusAssert.Is(HttpStatusCode.OK, await SlackAsync(f, Mention("U1", "<@UBOT> What does the parser do?", id: "Ev1")));
        // Slack sends an event again when it thinks it was missed: it is answered once.
        await SlackAsync(f, Mention("U1", "<@UBOT> What does the parser do?", id: "Ev1"));
        var post = await _platforms.WaitAsync("slack.com", "/api/chat.postMessage");
        var sent = JsonNode.Parse(post.Body)!;
        Assert.Equal("C1", sent["channel"]!.GetValue<string>());
        Assert.Equal("1700000000.000100", sent["thread_ts"]!.GetValue<string>());
        Assert.Equal("Answer to: What does the parser", sent["text"]!.GetValue<string>());
        Assert.Equal("Bearer xoxb-test", post.Headers["Authorization"]);

        // The chat is the person's: their model and tools, in their list.
        var chat = Assert.Single(await ChatsAsync(b));
        Assert.Equal("Slack · What does the parser do?", chat.GetProperty("title").GetString());
        var request = app.Model.Requests.Last(r => r.Body["messages"]!.AsArray().Last()!["content"]!.ToString() == "What does the parser do?");
        Assert.Contains("answer is read as a message", request.Body["messages"]![0]!["content"]!.ToString(), StringComparison.Ordinal);

        // A reply in the thread carries the same chat on.
        await SlackAsync(f, Mention("U1", "<@UBOT> And the lexer?", ts: "1700000000.000200", thread: "1700000000.000100"));
        await _platforms.WaitAsync("slack.com", "/api/chat.postMessage", p => p.Body.Contains("Answer to: And the lexer?", StringComparison.Ordinal));
        Assert.Single(await ChatsAsync(b));
        var messages = (await b.JsonAsync(await b.GetAsync($"/api/chat/conversations/{chat.GetProperty("id").GetString()}"))).GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(2, messages.Count(m => m.GetProperty("role").GetString() == "user"));
        Assert.Equal(2, _platforms.To("slack.com", "/api/chat.postMessage").Count());

        // Admins see it set up, with the address to give Slack.
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var platforms = (await admin.JsonAsync(await admin.GetAsync("/api/admin/bots"))).GetProperty("platforms").EnumerateArray().ToList();
        var slack = platforms.Single(p => p.GetProperty("name").GetString() == "slack");
        Assert.True(slack.GetProperty("ready").GetBoolean());
        Assert.Equal("https://llm.test/api/bots/slack", slack.GetProperty("url").GetString());
        Assert.Equal(1, slack.GetProperty("threads").GetInt32());
        Assert.False(platforms.Single(p => p.GetProperty("name").GetString() == "email").GetProperty("ready").GetBoolean());
        await StatusAssert.Is(HttpStatusCode.Forbidden, await b.GetAsync("/api/admin/bots"));
    }

    [Fact]
    public async Task A_bot_answer_is_not_offered_deep_research_while_it_asks_first()
    {
        await using var f = NewApp();
        var (_, email) = await PersonAsync(f);
        _platforms.SlackEmails["U3"] = email;
        var marker = "bot-" + Guid.NewGuid().ToString("N")[..8];
        async Task<List<string>> AskAsync(string ts)
        {
            await StatusAssert.Is(HttpStatusCode.OK, await SlackAsync(f, Mention("U3", $"<@UBOT> {marker}-{ts} What changed in vector databases?", ts: ts)));
            // Answered in its thread at once: nothing waited for an Allow nobody in the thread can press.
            await _platforms.WaitAsync("slack.com", "/api/chat.postMessage", p => p.Body.Contains($"\"{ts}\"", StringComparison.Ordinal));
            var request = app.Model.Requests.Select(r => r.Body).Last(r => r["tools"] is not null && r["messages"]!.ToJsonString().Contains($"{marker}-{ts}", StringComparison.Ordinal));
            return [.. request["tools"]!.AsArray().Select(t => t!["function"]!["name"]!.GetValue<string>())];
        }

        // As installed, deep research asks first: the bot's answer has the person's other tools, not it.
        var asked = await AskAsync("4.1");
        Assert.NotEmpty(asked);
        Assert.DoesNotContain("deep_research", asked);

        // An admin lets the model start one without asking: then it is offered, as in the person's chat.
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.Http.PutAsJsonAsync(new Uri("/api/admin/tools/research", UriKind.Relative),
            new { enabled = true, audience = "Everyone", groups = Array.Empty<Guid>(), onByDefault = true, askFirst = false }));
        Assert.Contains("deep_research", await AskAsync("4.2"));
    }

    [Fact]
    public async Task Someone_without_an_account_is_refused_politely_and_channels_not_allowed_are_declined()
    {
        await using var f = NewApp(new() { ["Bots:Slack:Channels"] = "C1, C7" });
        var (_, email) = await PersonAsync(f);
        _platforms.SlackEmails["U9"] = "stranger@elsewhere.test";
        _platforms.SlackEmails["U2"] = email;
        var asked = app.Model.Requests.Count;

        await SlackAsync(f, Mention("U9", "<@UBOT> Tell me the secrets", channel: "C7", ts: "1.1"));
        var refusal = await _platforms.WaitAsync("slack.com", "/api/chat.postMessage", p => p.Body.Contains("\"1.1\"", StringComparison.Ordinal));
        Assert.Contains("only answer people who have an account", JsonNode.Parse(refusal.Body)!["text"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(asked, app.Model.Requests.Count);
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var audit = (await admin.JsonAsync(await admin.GetAsync("/api/admin/audit"))).EnumerateArray().ToList();
        Assert.Contains(audit, e => e.GetProperty("action").GetString() == "bot.refused" && e.GetProperty("target").GetString() == "slack:U9");

        // A channel not on the list: declined there; a direct message: always answered.
        await SlackAsync(f, Mention("U2", "<@UBOT> Here?", channel: "C5", ts: "2.1"));
        var declined = await _platforms.WaitAsync("slack.com", "/api/chat.postMessage", p => p.Body.Contains("\"2.1\"", StringComparison.Ordinal));
        Assert.Contains("do not answer in this channel", declined.Body, StringComparison.Ordinal);
        await SlackAsync(f, Mention("U2", "Privately then", channel: "D5", ts: "3.1", type: "message", channelType: "im"));
        var direct = await _platforms.WaitAsync("slack.com", "/api/chat.postMessage", p => p.Body.Contains("\"3.1\"", StringComparison.Ordinal));
        Assert.Contains("Answer to: Privately then", direct.Body, StringComparison.Ordinal);
        // The bot's own posts are not questions.
        var bot = await SlackAsync(f, new { type = "event_callback", event_id = "Ev-bot", @event = new { type = "message", channel_type = "im", bot_id = "B1", user = "UBOT", text = "hello", channel = "D5", ts = "4.1" } });
        Assert.Contains("ignored", await bot.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Mattermost_post_is_answered_in_its_thread_and_a_slash_command_in_the_channel()
    {
        await using var f = NewApp();
        var (b, email) = await PersonAsync(f);
        _platforms.MattermostEmails["mm-u1"] = email;
        _platforms.MattermostRoots["reply-post"] = "root-post";
        var client = f.CreateClient();
        FormUrlEncodedContent Form(params (string, string)[] fields) => new(fields.ToDictionary(x => x.Item1, x => x.Item2));

        await StatusAssert.Is(HttpStatusCode.Unauthorized, await client.PostAsync(new Uri("/api/bots/mattermost", UriKind.Relative),
            Form(("token", "nope"), ("channel_id", "ch1"), ("user_id", "mm-u1"), ("text", "@argus hi"))));

        // An outgoing webhook: a reply in a thread, answered in that thread (its first post).
        await StatusAssert.Is(HttpStatusCode.OK, await client.PostAsync(new Uri("/api/bots/mattermost", UriKind.Relative), Form(("token", "hook-token"), ("channel_id", "ch1"),
            ("channel_name", "dev"), ("user_id", "mm-u1"), ("user_name", "dana"), ("post_id", "reply-post"), ("trigger_word", "@argus"), ("text", "@argus how is the build doing?"))));
        var post = await _platforms.WaitAsync("mattermost.test", "/api/v4/posts");
        var sent = JsonNode.Parse(post.Body)!;
        Assert.Equal("ch1", sent["channel_id"]!.GetValue<string>());
        Assert.Equal("root-post", sent["root_id"]!.GetValue<string>());
        Assert.Equal("Answer to: how is the build doi", sent["message"]!.GetValue<string>());
        Assert.Equal("Bearer mm-bot-token", post.Headers["Authorization"]);
        Assert.Equal("Mattermost · how is the build doing?", Assert.Single(await ChatsAsync(b)).GetProperty("title").GetString());

        // A slash command: told at once, to the asker only; the answer comes in the channel, quoting the question.
        var command = await client.PostAsync(new Uri("/api/bots/mattermost", UriKind.Relative), Form(("token", "command-token"), ("channel_id", "ch1"), ("user_id", "mm-u1"),
            ("user_name", "dana"), ("command", "/ask"), ("text", "what changed today?"), ("trigger_id", "t1")));
        var told = JsonDocument.Parse(await command.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("ephemeral", told.GetProperty("response_type").GetString());
        var answer = await _platforms.WaitAsync("mattermost.test", "/api/v4/posts", p => p.Body.Contains("what changed today", StringComparison.Ordinal));
        var message = JsonNode.Parse(answer.Body)!["message"]!.GetValue<string>();
        Assert.StartsWith("> @dana: what changed today?", message, StringComparison.Ordinal);
        Assert.Contains("Answer to: what changed today?", message, StringComparison.Ordinal);
        Assert.Equal(2, (await ChatsAsync(b)).Count);
    }

    [Fact]
    public async Task A_Teams_message_with_the_Bot_Connectors_token_is_answered_and_forged_tokens_are_refused()
    {
        await using var f = NewApp();
        var (b, email) = await PersonAsync(f);
        _platforms.TeamsEmails["29:dana"] = email;
        const string serviceUrl = "https://smba.test/emea/";
        const string conversation = "19:abc@thread.tacv2;messageid=1700";
        JsonObject Activity(string text) => new()
        {
            ["type"] = "message", ["id"] = "1700", ["channelId"] = "msteams", ["serviceUrl"] = serviceUrl, ["text"] = text,
            ["from"] = new JsonObject { ["id"] = "29:dana", ["name"] = "Dana", ["aadObjectId"] = "aad-1" },
            ["recipient"] = new JsonObject { ["id"] = "28:" + TeamsApp, ["name"] = "Argus" },
            ["conversation"] = new JsonObject { ["id"] = conversation, ["conversationType"] = "channel", ["isGroup"] = true },
            ["channelData"] = new JsonObject { ["channel"] = new JsonObject { ["id"] = "19:abc@thread.tacv2" }, ["team"] = new JsonObject { ["id"] = "19:team@thread.tacv2" } },
        };
        Task<HttpResponseMessage> Send(JsonObject activity, string? token)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/bots/teams") { Content = new StringContent(activity.ToJsonString(), Encoding.UTF8, "application/json") };
            if (token is not null)
            {
                request.Headers.Authorization = new("Bearer", token);
            }
            return f.CreateClient().SendAsync(request);
        }

        using var other = RSA.Create(2048);
        await StatusAssert.Is(HttpStatusCode.Unauthorized, await Send(Activity("hi"), null));
        await StatusAssert.Is(HttpStatusCode.Unauthorized, await Send(Activity("hi"), _platforms.BotConnectorToken("another-bot", serviceUrl)));
        await StatusAssert.Is(HttpStatusCode.Unauthorized, await Send(Activity("hi"), _platforms.BotConnectorToken(TeamsApp, serviceUrl, key: other)));
        await StatusAssert.Is(HttpStatusCode.Unauthorized, await Send(Activity("hi"), _platforms.BotConnectorToken(TeamsApp, serviceUrl, issuer: "https://evil.test")));
        await StatusAssert.Is(HttpStatusCode.Unauthorized, await Send(Activity("hi"), _platforms.BotConnectorToken(TeamsApp, "https://evil.test/")));
        var unendorsed = Activity("hi");
        unendorsed["channelId"] = "slack";
        await StatusAssert.Is(HttpStatusCode.Unauthorized, await Send(unendorsed, _platforms.BotConnectorToken(TeamsApp, serviceUrl)));
        Assert.Empty(_platforms.To("smba.test", $"/emea/v3/conversations/{conversation}/activities/1700"));

        await StatusAssert.Is(HttpStatusCode.OK, await Send(Activity("<at>Argus</at> Summarize the incident"), _platforms.BotConnectorToken(TeamsApp, serviceUrl)));
        var reply = await _platforms.WaitAsync("smba.test", "/emea/v3/conversations/19:abc@thread.tacv2;messageid=1700/activities/1700");
        var sent = JsonNode.Parse(reply.Body)!;
        Assert.Equal("Answer to: Summarize the incide", sent["text"]!.GetValue<string>());
        Assert.Equal("1700", sent["replyToId"]!.GetValue<string>());
        Assert.Equal("Bearer bot-access-token", reply.Headers["Authorization"]);
        Assert.Equal("Microsoft Teams · Summarize the incident", Assert.Single(await ChatsAsync(b)).GetProperty("title").GetString());
        var signIn = _platforms.To("login.microsoftonline.com", "/botframework.com/oauth2/v2.0/token").First();
        Assert.Contains("client_id=" + TeamsApp, signIn.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_email_is_answered_by_email_and_in_a_chat_and_strangers_get_nothing()
    {
        await using var smtp = new FakeSmtp();
        await using var f = NewApp(new()
        {
            ["Bots:Email:Secret"] = "mail-secret",
            ["Mail:Host"] = "127.0.0.1", ["Mail:Port"] = smtp.Port.ToString(CultureInfo.InvariantCulture), ["Mail:StartTls"] = "false", ["Mail:From"] = "Argus Arena <llm@example.test>",
        });
        var (b, email) = await PersonAsync(f);
        var client = f.CreateClient();
        Task<HttpResponseMessage> Mail(object body, string? secret = "mail-secret")
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/mail/inbound") { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
            if (secret is not null)
            {
                request.Headers.Add("X-Mail-Secret", secret);
            }
            return client.SendAsync(request);
        }

        await StatusAssert.Is(HttpStatusCode.Unauthorized, await Mail(new { from = email, subject = "x", text = "y" }, secret: null));
        await StatusAssert.Is(HttpStatusCode.Unauthorized, await Mail(new { from = email, subject = "x", text = "y" }, secret: "wrong"));
        await StatusAssert.Is(HttpStatusCode.Accepted, await Mail(new { from = $"Dana <{email}>", to = "ask@example.test", subject = "Quarterly numbers", text = "Which team grew most?" }));
        FakeSmtp.Mail? answer = null;
        for (var i = 0; i < 200 && answer is null; i++)
        {
            await Task.Delay(50);
            lock (smtp.Received)
            {
                answer = smtp.Received.FirstOrDefault(m => m.To.Contains($"<{email}>"));
            }
        }
        Assert.NotNull(answer);
        Assert.Contains("Subject: Re: Quarterly numbers", answer.Data, StringComparison.Ordinal);
        var chat = Assert.Single(await ChatsAsync(b));
        Assert.Equal("Email · Quarterly numbers", chat.GetProperty("title").GetString());
        Assert.Contains("Answer to: Quarterly numbers", BodyOf(answer.Data), StringComparison.Ordinal);
        Assert.Contains($"https://llm.test/chat/{chat.GetProperty("id").GetString()}", BodyOf(answer.Data), StringComparison.Ordinal);

        // A reply carries the chat on.
        await StatusAssert.Is(HttpStatusCode.Accepted, await Mail(new { from = email, subject = "RE: Quarterly numbers", text = "And the smallest?" }));
        for (var i = 0; i < 200 && smtp.Received.Count < 2; i++)
        {
            await Task.Delay(50);
        }
        Assert.Equal(2, smtp.Received.Count);
        Assert.Single(await ChatsAsync(b));

        // No account: nothing is sent back (the sender may be forged); a sender the gateway found forged is dropped at once.
        await StatusAssert.Is(HttpStatusCode.Accepted, await Mail(new { from = "someone@elsewhere.test", subject = "Hello", text = "Who are you?" }));
        var dropped = await Mail(new { from = email, subject = "Forged", text = "x", spf = "fail" });
        Assert.Contains("spf did not pass", await dropped.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        for (var i = 0; i < 100; i++)
        {
            var audit = (await admin.JsonAsync(await admin.GetAsync("/api/admin/audit"))).EnumerateArray().ToList();
            if (audit.Any(e => e.GetProperty("action").GetString() == "bot.refused" && e.GetProperty("target").GetString() == "email:someone@elsewhere.test"))
            {
                break;
            }
            await Task.Delay(50);
        }
        Assert.Equal(2, smtp.Received.Count);
    }

    /// <summary>An email's text, its transfer encoding undone.</summary>
    private static string BodyOf(string data)
    {
        var split = data.IndexOf("\n\n", StringComparison.Ordinal);
        var (headers, body) = (data[..split], data[(split + 2)..]);
        return headers.Contains("base64", StringComparison.OrdinalIgnoreCase)
            ? Encoding.UTF8.GetString(Convert.FromBase64String(string.Concat(body.Split('\n', StringSplitOptions.TrimEntries))))
            : body;
    }
}

/// <summary>Text for the platforms: answers cut to fit, Slack's Markdown, mentions and subjects.</summary>
public sealed class BotTextTests
{
    [Fact]
    public void A_long_answer_is_cut_at_a_line_with_a_link_and_an_open_code_block_closed()
    {
        Assert.Equal("short", BotText.Fit("short", 100, "[more](u)"));
        var text = "Intro line\n```cs\n" + string.Join('\n', Enumerable.Range(0, 200).Select(i => $"var x{i} = {i};")) + "\n```\nEnd";
        var cut = BotText.Fit(text, 500, "[The whole answer](https://x/chat/1)");
        Assert.True(cut.Length <= 500, $"{cut.Length} characters");
        Assert.EndsWith("```\n…\n\n[The whole answer](https://x/chat/1)", cut, StringComparison.Ordinal);
        Assert.StartsWith("Intro line\n```cs\nvar x0 = 0;", cut, StringComparison.Ordinal);
    }

    [Fact]
    public void Markdown_is_written_as_Slack_writes_it()
    {
        Assert.Equal("*Title*\n*bold* and _italic_, ~gone~, <https://example.com/a?b=1|a link>\n• one\n• two",
            BotText.SlackMarkdown("## Title\n**bold** and *italic*, ~~gone~~, [a link](https://example.com/a?b=1)\n- one\n- two"));
        // Slack reads &, < and > as its own everywhere, code included: they are escaped there too.
        Assert.Equal("```\nif (a &lt; b) **x**\n``` and `a&lt;b`", BotText.SlackMarkdown("```cs\nif (a < b) **x**\n``` and `a<b`"));
        Assert.Equal("1 &lt; 2 &amp; 3\n> quoted", BotText.SlackMarkdown("1 < 2 & 3\n> quoted"));
    }

    [Fact]
    public void Mentions_and_reply_prefixes_are_taken_out()
    {
        Assert.Equal("What is <this> & that?", BotText.SlackQuestion("<@U0123ABC> What is &lt;this&gt; &amp; that?"));
        Assert.Equal("Summarize it", BotText.TeamsQuestion("<at>Argus Arena</at> Summarize it"));
        Assert.Equal("quarterly numbers", BotText.Subject("RE: Fwd: AW:  Quarterly   Numbers"));
        Assert.Equal("", BotText.Subject(null));
    }

    [Fact]
    public void A_platforms_channel_list_and_tools_read_as_meant()
    {
        var o = new SlackOptions { Channels = "C1, #dev", Tools = "argus, web" };
        Assert.True(o.Allows("C1"));
        Assert.True(o.Allows("C9", "dev"));
        Assert.False(o.Allows("C2"));
        Assert.True(new SlackOptions().Allows("anything"));
        Assert.Equal(["argus", "web"], o.ToolList());
        Assert.Null(new SlackOptions().ToolList());
        Assert.Empty(new MailInOptions().ToolList()!);
    }
}
