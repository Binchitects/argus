using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Llm.Tests;

[Collection(nameof(AppCollection))]
public sealed class ChatTests(AppFixture app)
{
    private async Task<(TestBrowser Browser, string Email)> PersonAsync()
    {
        var admin = await new TestBrowser(app.Factory).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "c" + Guid.NewGuid().ToString("N")[..10];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        return (await new TestBrowser(app.Factory).SignedInAsync(name, made.GetProperty("password").GetString()!), $"{name}@example.test");
    }

    private static async Task<Guid> NewChatAsync(TestBrowser b, object? body = null)
    {
        var res = await b.PostAsync("/api/chat/conversations", body ?? new { });
        await StatusAssert.Is(HttpStatusCode.Created, res);
        return (await b.JsonAsync(res)).GetProperty("id").GetGuid();
    }

    /// <summary>Sends and reads the whole event stream.</summary>
    private static async Task<List<JsonElement>> SendAsync(TestBrowser b, Guid id, string text, Guid[]? attachments = null, string path = "messages")
    {
        var res = await b.PostAsync($"/api/chat/conversations/{id}/{path}", new { content = text, attachments });
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}");
        Assert.Equal("text/event-stream", res.Content.Headers.ContentType?.MediaType);
        return Events(await res.Content.ReadAsStringAsync());
    }

    private static List<JsonElement> Events(string sse) =>
        [.. sse.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Where(l => l.StartsWith("data: ", StringComparison.Ordinal))
            .Select(l => JsonDocument.Parse(l[6..]).RootElement)];

    private static IEnumerable<string> Types(List<JsonElement> events) => events.Select(e => e.GetProperty("type").GetString()!);

    private static async Task<JsonElement> ConversationAsync(TestBrowser b, Guid id) => await b.JsonAsync(await b.GetAsync($"/api/chat/conversations/{id}"));

    [Fact]
    public async Task Config_lists_the_model_its_presets_and_argus()
    {
        var (b, _) = await PersonAsync();
        var c = await b.JsonAsync(await b.GetAsync("/api/chat/config"));
        Assert.Equal("Qwen3.8-Flash-Next", c.GetProperty("model").GetString());
        Assert.Equal(["xhigh", "low", "off"], c.GetProperty("presets").EnumerateArray().Select(p => p.GetProperty("level").GetString()));
        Assert.True(c.GetProperty("argus").GetBoolean());
    }

    [Fact]
    public async Task An_answer_streams_is_saved_and_is_billed_to_the_person()
    {
        var (b, email) = await PersonAsync();
        var id = await NewChatAsync(b, new { thinking = "low", useArgus = false });
        var events = await SendAsync(b, id, "How do I rotate logs?\nMore detail here.");
        Assert.Equal(["question", "title", "assistant", "reasoning", "thought", "content", "content", "usage", "done"], Types(events));
        Assert.Equal("How do I rotate logs?", events[1].GetProperty("title").GetString());

        var (body, headers) = app.Model.Requests.Last();
        Assert.Equal(email, body["user"]!.GetValue<string>());
        Assert.Equal(email, headers["X-OpenWebUI-User-Email"]);
        // The chat's own key (alias "chat"), never the master key.
        Assert.StartsWith("Bearer sk-chat-", headers["Authorization"], StringComparison.Ordinal);
        Assert.Contains(headers["Authorization"][7..], app.Gateway.ServiceKeys);
        Assert.Equal("low", body["chat_template_kwargs"]!["reasoning_effort"]!.GetValue<string>());
        Assert.True(body["stream_options"]!["include_usage"]!.GetValue<bool>());
        Assert.Null(body["tools"]);
        Assert.Equal("system", body["messages"]![0]!["role"]!.GetValue<string>());

        var conv = await ConversationAsync(b, id);
        var msgs = conv.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(["user", "assistant"], msgs.Select(m => m.GetProperty("role").GetString()));
        Assert.Equal("Answer to: How do I rotate logs", msgs[1].GetProperty("content").GetString());
        Assert.Equal("Thinking about it.", msgs[1].GetProperty("reasoning").GetString());
        Assert.Equal(40, msgs[1].GetProperty("cachedTokens").GetInt32());
        Assert.Equal("complete", msgs[1].GetProperty("status").GetString());
        // The answer hangs off the question, and is what the chat shows.
        Assert.Equal(msgs[0].GetProperty("id").GetGuid(), msgs[1].GetProperty("parentId").GetGuid());
        Assert.Equal(msgs[1].GetProperty("id").GetGuid(), conv.GetProperty("currentLeafId").GetGuid());
        Assert.True(msgs[1].GetProperty("thinkingMs").GetInt32() >= 0);
        Assert.True(msgs[1].GetProperty("durationMs").GetInt32() >= 0);
    }

    [Fact]
    public async Task Thinking_off_turns_thinking_off_and_the_default_sends_nothing()
    {
        var (b, _) = await PersonAsync();
        var off = await NewChatAsync(b, new { thinking = "off", useArgus = false });
        await SendAsync(b, off, "hi");
        Assert.False(app.Model.Requests.Last().Body["chat_template_kwargs"]!["enable_thinking"]!.GetValue<bool>());
        var plain = await NewChatAsync(b, new { useArgus = false });
        await SendAsync(b, plain, "hi");
        Assert.Null(app.Model.Requests.Last().Body["chat_template_kwargs"]);
        await StatusAssert.Is(HttpStatusCode.BadRequest, await b.PostAsync("/api/chat/conversations", new { thinking = "ultra" }));
    }

    [Fact]
    public async Task Argus_tools_are_called_as_the_person_and_the_answer_uses_them()
    {
        var (b, email) = await PersonAsync();
        var id = await NewChatAsync(b);
        var events = await SendAsync(b, id, "Where is ParseHeader? [tool]");
        Assert.Equal(["question", "title", "assistant", "usage", "tool_call", "tool_result", "assistant", "content", "usage", "done"], Types(events));
        var result = events.Single(e => e.GetProperty("type").GetString() == "tool_result");
        Assert.Contains("src/parse.c:10", result.GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.True(result.GetProperty("durationMs").GetInt32() >= 0);
        Assert.False(result.GetProperty("noAccess").GetBoolean());

        // Argus saw the chat token and this person's email, in one session.
        var calls = app.Argus.McpCalls.Where(c => c.Email == email).ToList();
        Assert.Equal(["initialize", "notifications/initialized", "tools/list", "tools/call"], calls.Select(c => c.Method));
        Assert.All(calls.Skip(1), c => Assert.Equal("session-" + email, c.Session));

        // The model got the tools, Argus's instructions, and then the tool's answer.
        var requests = app.Model.Requests.Where(r => r.Body["user"]!.GetValue<string>() == email).ToList();
        Assert.Equal("find_symbol", requests[0].Body["tools"]![0]!["function"]!["name"]!.GetValue<string>());
        Assert.Contains(FakeArgus.Instructions, requests[0].Body["messages"]![0]!["content"]!.GetValue<string>(), StringComparison.Ordinal);
        var second = requests[1].Body["messages"]!.AsArray();
        Assert.Equal("tool", second.Last()!["role"]!.GetValue<string>());
        Assert.Equal("call_1", second.Last()!["tool_call_id"]!.GetValue<string>());

        var msgs = (await ConversationAsync(b, id)).GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(["user", "assistant", "tool", "assistant"], msgs.Select(m => m.GetProperty("role").GetString()));
        Assert.Equal("Found it.", msgs[3].GetProperty("content").GetString());

        // The history sent next time holds the whole exchange, in order.
        await SendAsync(b, id, "thanks");
        var history = app.Model.Requests.Last().Body["messages"]!.AsArray().Select(m => m!["role"]!.GetValue<string>());
        Assert.Equal(["system", "user", "assistant", "tool", "assistant", "user"], history);
    }

    [Fact]
    public async Task No_access_is_flagged_so_the_person_knows_whom_to_ask()
    {
        var (b, _) = await PersonAsync();
        var id = await NewChatAsync(b);
        var events = await SendAsync(b, id, "What is SecretThing? [noaccess]");
        var result = events.Single(e => e.GetProperty("type").GetString() == "tool_result");
        Assert.True(result.GetProperty("noAccess").GetBoolean());
        Assert.Contains("maintainers: alice", result.GetProperty("text").GetString(), StringComparison.Ordinal);
        var tool = (await ConversationAsync(b, id)).GetProperty("messages").EnumerateArray().Single(m => m.GetProperty("role").GetString() == "tool");
        Assert.True(tool.GetProperty("noAccess").GetBoolean());
    }

    [Fact]
    public async Task When_argus_is_down_the_chat_still_answers_and_says_so()
    {
        var (b, _) = await PersonAsync();
        var id = await NewChatAsync(b);
        app.Argus.McpDown = true;
        try
        {
            var events = await SendAsync(b, id, "hello");
            Assert.Equal("argus_unavailable", events.First(e => e.GetProperty("type").GetString() == "notice").GetProperty("kind").GetString());
            Assert.Contains("done", Types(events));
            Assert.Null(app.Model.Requests.Last().Body["tools"]);
        }
        finally
        {
            app.Argus.McpDown = false;
        }
    }

    [Fact]
    public async Task Used_up_credit_is_said_plainly_and_the_turn_is_marked_failed()
    {
        var (b, _) = await PersonAsync();
        var id = await NewChatAsync(b, new { useArgus = false });
        var events = await SendAsync(b, id, "hello [budget]");
        Assert.Equal("You have used all your credit. Ask an admin to raise it.", events.Last().GetProperty("message").GetString());
        var last = (await ConversationAsync(b, id)).GetProperty("messages").EnumerateArray().Last();
        Assert.Equal("failed", last.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Stopping_keeps_what_arrived_and_marks_it_stopped()
    {
        var (b, _) = await PersonAsync();
        var id = await NewChatAsync(b, new { useArgus = false });
        using var cts = new CancellationTokenSource();
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri($"/api/chat/conversations/{id}/messages", UriKind.Relative))
        {
            Content = JsonContent.Create(new { content = "Write a lot [slow]" }),
        };
        var res = await b.Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        var stream = await res.Content.ReadAsStreamAsync(cts.Token);
        var buffer = new byte[4096];
        var seen = new StringBuilder();
        while (!seen.ToString().Contains("w5 ", StringComparison.Ordinal))
        {
            seen.Append(Encoding.UTF8.GetString(buffer, 0, await stream.ReadAsync(buffer, cts.Token)));
        }
        await cts.CancelAsync();
        res.Dispose();

        JsonElement last = default;
        for (var i = 0; i < 50; i++)
        {
            await Task.Delay(100);
            last = (await ConversationAsync(b, id)).GetProperty("messages").EnumerateArray().Last();
            if (last.GetProperty("status").GetString() == "stopped")
            {
                break;
            }
        }
        Assert.Equal("stopped", last.GetProperty("status").GetString());
        var content = last.GetProperty("content").GetString()!;
        Assert.StartsWith("w0 w1 w2 w3 w4 w5 ", content, StringComparison.Ordinal);
        Assert.DoesNotContain("w399", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Regenerate_adds_an_answer_beside_the_old_one()
    {
        var (b, _) = await PersonAsync();
        var id = await NewChatAsync(b, new { useArgus = false });
        await SendAsync(b, id, "first question");
        var before = (await ConversationAsync(b, id)).GetProperty("messages").EnumerateArray().Last().GetProperty("id").GetGuid();
        var events = await SendAsync(b, id, "", path: "regenerate");
        Assert.Contains("done", Types(events));
        var conv = await ConversationAsync(b, id);
        var msgs = conv.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(["user", "assistant", "assistant"], msgs.Select(m => m.GetProperty("role").GetString()));
        // Both answers hang off the question; the new one is on screen.
        Assert.Equal(msgs[1].GetProperty("parentId").GetGuid(), msgs[2].GetProperty("parentId").GetGuid());
        Assert.Equal(before, msgs[1].GetProperty("id").GetGuid());
        Assert.Equal(msgs[2].GetProperty("id").GetGuid(), conv.GetProperty("currentLeafId").GetGuid());
        // The next question builds on the new answer only.
        await SendAsync(b, id, "next");
        Assert.Equal(["system", "user", "assistant", "user"], app.Model.Requests.Last().Body["messages"]!.AsArray().Select(m => m!["role"]!.GetValue<string>()));
    }

    [Fact]
    public async Task One_answer_at_a_time_per_chat()
    {
        var (b, _) = await PersonAsync();
        var id = await NewChatAsync(b, new { useArgus = false });
        using var first = new HttpRequestMessage(HttpMethod.Post, new Uri($"/api/chat/conversations/{id}/messages", UriKind.Relative)) { Content = JsonContent.Create(new { content = "long [slow]" }) };
        using var running = await b.Http.SendAsync(first, HttpCompletionOption.ResponseHeadersRead);
        var second = await b.PostAsync($"/api/chat/conversations/{id}/messages", new { content = "again" });
        await StatusAssert.Is(HttpStatusCode.Conflict, second);
    }

    [Fact]
    public async Task People_only_ever_see_their_own_chats_and_files()
    {
        var (alice, _) = await PersonAsync();
        var (bob, _) = await PersonAsync();
        var id = await NewChatAsync(alice, new { useArgus = false });
        await SendAsync(alice, id, "private question");
        await StatusAssert.Is(HttpStatusCode.NotFound, await bob.GetAsync($"/api/chat/conversations/{id}"));
        await StatusAssert.Is(HttpStatusCode.NotFound, await bob.PostAsync($"/api/chat/conversations/{id}/messages", new { content = "x" }));
        await StatusAssert.Is(HttpStatusCode.NotFound, await bob.Http.DeleteAsync(new Uri($"/api/chat/conversations/{id}", UriKind.Relative)));
        Assert.DoesNotContain(id.ToString(), await (await bob.GetAsync("/api/chat/conversations")).Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var file = await UploadAsync(alice, "notes.txt", "alice's notes"u8.ToArray());
        var bobChat = await NewChatAsync(bob, new { useArgus = false });
        var res = await bob.PostAsync($"/api/chat/conversations/{bobChat}/messages", new { content = "read this", attachments = new[] { file.GetProperty("id").GetGuid() } });
        await StatusAssert.Is(HttpStatusCode.BadRequest, res);
    }

    private static async Task<JsonElement> UploadAsync(TestBrowser b, string name, byte[] bytes, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue(name.EndsWith(".pdf", StringComparison.Ordinal) ? "application/pdf" : "text/plain");
        form.Add(part, "file", name);
        var res = await b.Http.PostAsync(new Uri("/api/chat/attachments", UriKind.Relative), form);
        await StatusAssert.Is(expected, res);
        return await b.JsonAsync(res);
    }

    [Fact]
    public async Task Text_and_pdf_attachments_reach_the_model_and_binaries_do_not()
    {
        var (b, _) = await PersonAsync();
        var txt = await UploadAsync(b, "config.yaml", Encoding.UTF8.GetBytes("retries: 3\ntimeout: 30s\n"));
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(595, 842);
        page.AddText("Quarterly report: revenue grew 12 percent", 12, new UglyToad.PdfPig.Core.PdfPoint(50, 700), builder.AddStandard14Font(Standard14Font.Helvetica));
        var pdf = await UploadAsync(b, "report.pdf", builder.Build());
        Assert.True(pdf.GetProperty("chars").GetInt32() > 20);
        await UploadAsync(b, "image.png", [0x89, 0x50, 0x4E, 0x47, 0, 0, 0, 0], HttpStatusCode.BadRequest);

        var id = await NewChatAsync(b, new { useArgus = false });
        await SendAsync(b, id, "Summarise these", [txt.GetProperty("id").GetGuid(), pdf.GetProperty("id").GetGuid()]);
        var user = app.Model.Requests.Last().Body["messages"]!.AsArray().Last(m => m!["role"]!.GetValue<string>() == "user")!["content"]!.GetValue<string>();
        Assert.Contains("<attachment name=\"config.yaml\">\nretries: 3", user, StringComparison.Ordinal);
        Assert.Contains("revenue grew 12 percent", user, StringComparison.Ordinal);
        var shown = (await ConversationAsync(b, id)).GetProperty("messages")[0].GetProperty("attachments").EnumerateArray().Select(a => a.GetProperty("fileName").GetString());
        Assert.Equal(["config.yaml", "report.pdf"], shown);
    }

    [Fact]
    public async Task A_long_history_is_trimmed_from_the_oldest_end()
    {
        var (b, email) = await PersonAsync();
        var id = await NewChatAsync(b, new { useArgus = false });
        // 60 old exchanges of ~4000 characters: far past a 32K-token context.
        using (var scope = app.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            for (var i = 0; i < 60; i++)
            {
                db.ChatMessages.Add(new() { ConversationId = id, Role = "user", Sequence = 2 * i + 1, Content = $"old question {i} " + new string('x', 4000) });
                db.ChatMessages.Add(new() { ConversationId = id, Role = "assistant", Sequence = 2 * i + 2, Content = new string('y', 100) });
            }
            await db.SaveChangesAsync();
        }
        await SendAsync(b, id, "the newest question");
        var sent = app.Model.Requests.Last(r => r.Body["user"]!.GetValue<string>() == email).Body["messages"]!.AsArray();
        var text = sent.ToJsonString();
        Assert.Contains("the newest question", text, StringComparison.Ordinal);
        Assert.DoesNotContain("old question 0 ", text, StringComparison.Ordinal);
        Assert.Equal("user", sent[1]!["role"]!.GetValue<string>());
        Assert.True(text.Length < (32768 - 8192) * 4);
    }

    [Fact]
    public async Task Chats_can_be_renamed_listed_searched_and_deleted()
    {
        var (b, _) = await PersonAsync();
        var id = await NewChatAsync(b, new { useArgus = false });
        await StatusAssert.Is(HttpStatusCode.NoContent, await b.Http.PatchAsJsonAsync(new Uri($"/api/chat/conversations/{id}", UriKind.Relative), new { title = "Deploy notes", thinking = "xhigh" }));
        var c = await ConversationAsync(b, id);
        Assert.Equal(("Deploy notes", "xhigh"), (c.GetProperty("title").GetString(), c.GetProperty("thinking").GetString()));
        Assert.Single((await b.JsonAsync(await b.GetAsync("/api/chat/conversations?q=deploy"))).EnumerateArray());
        Assert.Empty((await b.JsonAsync(await b.GetAsync("/api/chat/conversations?q=nothing-like-it"))).EnumerateArray());
        await StatusAssert.Is(HttpStatusCode.NoContent, await b.Http.DeleteAsync(new Uri($"/api/chat/conversations/{id}", UriKind.Relative)));
        await StatusAssert.Is(HttpStatusCode.NotFound, await b.GetAsync($"/api/chat/conversations/{id}"));
    }

    [Fact]
    public async Task A_chat_key_deleted_at_the_gateway_is_replaced_on_the_fly()
    {
        var (b, _) = await PersonAsync();
        var id = await NewChatAsync(b, new { useArgus = false });
        await SendAsync(b, id, "first");
        var oldKey = app.Model.Requests.Last().Headers["Authorization"][7..];
        app.Model.RevokedKeys[oldKey] = true;
        var events = await SendAsync(b, id, "second");
        Assert.Contains("done", Types(events));
        var newKey = app.Model.Requests.Last().Headers["Authorization"][7..];
        Assert.NotEqual(oldKey, newKey);
        Assert.StartsWith("sk-chat-", newKey, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Short question", "Short question")]
    [InlineData("  Line one\nline two", "Line one")]
    [InlineData("A very long first question that keeps going well past the sixty character limit here", "A very long first question that keeps going well past the…")]
    public void Titles_come_from_the_first_line(string text, string expected) =>
        Assert.Equal(expected, Llm.Api.Chat.ChatService.TitleFrom(text));
}
