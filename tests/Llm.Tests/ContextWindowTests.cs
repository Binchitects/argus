using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Llm.Core.Chat;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>
/// What fills the window (the context view, as Claude Code's /context): the request split into its pieces, each tool's
/// definition, the instructions, the files and messages, counted with the engine's tokenizer where it has one and adding up
/// to what the engine counted of the request; else estimated, adding up the same. What could be freed, and leaving out a file.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class ContextWindowTests(AppFixture app)
{
    private static async Task<TestBrowser> PersonAsync(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> f)
    {
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "cw" + Guid.NewGuid().ToString("N")[..10];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        return await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!);
    }

    private static async Task SendAsync(TestBrowser b, Guid id, string text, Guid[]? attachments = null)
    {
        var res = await b.PostAsync($"/api/chat/conversations/{id}/messages", new { content = text, attachments = attachments ?? [] });
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}");
        await res.Content.ReadAsStringAsync();
    }

    private static async Task<Guid> UploadAsync(TestBrowser b, string name, string text)
    {
        using var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(text));
        part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        form.Add(part, "file", name);
        return (await b.JsonAsync(await b.Http.PostAsync(new Uri("/api/chat/attachments", UriKind.Relative), form))).GetProperty("id").GetGuid();
    }

    private static long Sum(JsonElement window) => window.GetProperty("parts").EnumerateArray().Sum(p => p.GetProperty("tokens").GetInt64());

    private static JsonElement Part(JsonElement window, string key) => window.GetProperty("parts").EnumerateArray().Single(p => p.GetProperty("key").GetString() == key);

    private static async Task<long> LastPromptAsync(TestBrowser b, Guid id) =>
        (await b.JsonAsync(await b.GetAsync($"/api/chat/conversations/{id}"))).GetProperty("messages").EnumerateArray()
            .Last(m => m.GetProperty("role").GetString() == "assistant").GetProperty("promptTokens").GetInt64();

    /// <summary>An app of its own with llama.cpp's router (FakeEngine), once it said the chat's model is loaded there.</summary>
    private async Task<Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>> EngineAppAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "window-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        app.Engine.Reset(Path.Combine(dir, "models.ini"));
        var f = app.Create(app.ConnectionStringFor("window_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(), new Dictionary<string, string?>
        {
            ["Engine:Enabled"] = "true", ["Engine:ApiKey"] = FakeEngine.Key, ["Engine:ConfigDir"] = dir, ["Engine:LibraryDir"] = Path.Combine(dir, "library"),
        });
        var state = f.Services.GetRequiredService<Llm.Api.Models.EngineState>();
        for (var i = 0; i < 100 && state.Now.Models.All(m => m.Name != "Qwen3.8-Flash-Next" || m.Status != "loaded"); i++)
        {
            await Task.Delay(100);
        }
        return f;
    }

    [Fact]
    public async Task An_answer_that_kept_no_window_is_shown_rebuilt_from_the_chat_with_its_pieces()
    {
        var f = app.Factory;
        var b = await PersonAsync(f);
        var id = (await b.JsonAsync(await b.PostAsync("/api/chat/conversations", new { }))).GetProperty("id").GetGuid();
        await SendAsync(b, id, "How many legs has a spider?");
        await SendAsync(b, id, "And an ant?");
        // As answers from before 8.0 left it: what filled the request by kind (the gauge's), no window piece by piece.
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Llm.Core.Data.AppDbContext>();
            foreach (var m in db.ChatMessages.Where(m => m.ConversationId == id && m.Role == "assistant"))
            {
                (m.WindowJson, m.ContextJson) = (null, """{"system":1200,"tools":3000,"you":40,"answers":60}""");
            }
            await db.SaveChangesAsync();
        }
        var shown = await b.JsonAsync(await b.GetAsync($"/api/chat/conversations/{id}/context"));
        Assert.True(shown.GetProperty("rebuilt").GetBoolean());
        var window = shown.GetProperty("window");
        Assert.Equal("estimate", window.GetProperty("counted").GetString());
        // Each message before the last answer, in its part; the rest at its measured size, one piece each.
        var you = Part(window, "you").GetProperty("items").EnumerateArray().Select(i => i.GetProperty("preview").GetString()).ToList();
        Assert.Equal(["And an ant?", "How many legs has a spider?"], you.Order(StringComparer.Ordinal));
        Assert.Single(Part(window, "answers").GetProperty("items").EnumerateArray());
        Assert.Equal("Tool definitions", Part(window, "tools").GetProperty("items")[0].GetProperty("name").GetString());
        Assert.Equal(await LastPromptAsync(b, id), Sum(window));
    }

    [Fact]
    public async Task Counted_with_the_engines_tokenizer_the_pieces_add_up_to_the_requests_tokens()
    {
        await using var f = await EngineAppAsync();
        // The engine counts the request as the fake tokenizer counts its chat template.
        app.Model.PromptTokens = body => FakeEngine.CountTokens(FakeEngine.Template(body));
        try
        {
            var b = await PersonAsync(f);
            var id = (await b.JsonAsync(await b.PostAsync("/api/chat/conversations", new { tools = new[] { "calculator", "time" }, systemPrompt = "Answer in one short sentence." })))
                .GetProperty("id").GetGuid();
            await SendAsync(b, id, "How many legs has a spider?");

            var shown = await b.JsonAsync(await b.GetAsync($"/api/chat/conversations/{id}/context"));
            var window = shown.GetProperty("window");
            Assert.Equal("tokenizer", window.GetProperty("counted").GetString());
            Assert.Equal("llama.cpp", window.GetProperty("by").GetString());
            // Each piece counted by the tokenizer, the chat format what the template adds: together what the engine counted.
            var prompt = await LastPromptAsync(b, id);
            Assert.Equal(prompt, window.GetProperty("total").GetInt64());
            Assert.Equal(prompt, Sum(window));
            Assert.True(Part(window, "format").GetProperty("tokens").GetInt64() > 0);
            // Each tool's definition by name, the biggest first, in the model's tokens.
            var tools = Part(window, "tools").GetProperty("items").EnumerateArray().ToList();
            Assert.Contains(tools, t => t.GetProperty("tool").GetString() == "calculator");
            Assert.Equal(tools.Select(t => t.GetProperty("tokens").GetInt64()).OrderDescending(), tools.Select(t => t.GetProperty("tokens").GetInt64()));
            var calculator = tools.First(t => t.GetProperty("tool").GetString() == "calculator");
            var definition = app.Model.Requests.Last(r => r.Body["messages"]![0]!["content"]!.GetValue<string>().Contains("one short sentence", StringComparison.Ordinal)).Body["tools"]!.AsArray()
                .First(t => t!["function"]!["name"]!.GetValue<string>() == calculator.GetProperty("name").GetString())!;
            Assert.Equal(FakeEngine.CountTokens(definition.ToJsonString()), calculator.GetProperty("tokens").GetInt64());
            Assert.Equal("This chat's instructions", Part(window, "instructions").GetProperty("items")[0].GetProperty("name").GetString());
            Assert.Equal(FakeEngine.CountTokens("How many legs has a spider?"), Part(window, "you").GetProperty("tokens").GetInt64());
            // Neither tool was called: each may be turned off for this chat.
            var freeable = window.GetProperty("freeable").EnumerateArray().ToList();
            Assert.Contains(freeable, x => x.GetProperty("action").GetString() == "tool-off" && x.GetProperty("ref").GetString() == "calculator");
        }
        finally
        {
            app.Model.PromptTokens = null;
            app.Engine.Reset(null);
        }
    }

    [Fact]
    [Trait("Control", "IAM-05")]
    public async Task Code_Arena_counts_with_the_persons_key_and_only_for_the_models_they_may_use()
    {
        await using var f = await EngineAppAsync();
        try
        {
            var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
            var name = "cw" + Guid.NewGuid().ToString("N")[..10];
            var key = (await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }))).GetProperty("apiKey").GetString()!;
            using var arena = f.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { BaseAddress = new Uri($"https://{AppFixture.Domain}"), HandleCookies = false });
            arena.DefaultRequestHeaders.Add("X-Requested-With", "code-arena");
            var texts = new[] { "Hello there, world.", "{\"type\": \"function\"}" };
            await StatusAssert.Is(HttpStatusCode.Unauthorized, await arena.PostAsJsonAsync("/api/code-arena/tokenize", new { model = "Qwen3.8-Flash-Next", texts }));
            arena.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
            await StatusAssert.Is(HttpStatusCode.Forbidden, await arena.PostAsJsonAsync("/api/code-arena/tokenize", new { model = "a-model-nobody-may-use", texts }));
            var request = JsonNode.Parse("""{"messages": [{"role": "user", "content": "Hello there, world."}]}""")!.AsObject();
            var res = await arena.PostAsJsonAsync("/api/code-arena/tokenize", new { model = "Qwen3.8-Flash-Next", texts, request });
            await StatusAssert.Is(HttpStatusCode.OK, res);
            var counted = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal(texts.Select(FakeEngine.CountTokens), counted.GetProperty("counts").EnumerateArray().Select(c => c.GetInt32()));
            Assert.Equal(FakeEngine.CountTokens(FakeEngine.Template(request)), counted.GetProperty("total").GetInt64());
            Assert.Equal("llama.cpp", counted.GetProperty("by").GetString());
        }
        finally
        {
            app.Engine.Reset(null);
        }
    }

    [Fact]
    public async Task Without_a_tokenizer_it_is_estimated_and_still_adds_up_and_a_file_left_out_is_not_read()
    {
        // The engine counts about four characters a token.
        app.Model.PromptTokens = body => body.ToJsonString().Length / 4;
        try
        {
            var b = await PersonAsync(app.Factory);
            var id = (await b.JsonAsync(await b.PostAsync("/api/chat/conversations", new { tools = Array.Empty<string>() }))).GetProperty("id").GetGuid();
            // Nothing yet: the view says so.
            Assert.Equal(JsonValueKind.Null, (await b.JsonAsync(await b.GetAsync($"/api/chat/conversations/{id}/context"))).GetProperty("window").ValueKind);
            var file = await UploadAsync(b, "notes.txt", string.Join(' ', Enumerable.Repeat("The meeting moved to Thursday at noon.", 120)));
            await SendAsync(b, id, "Summarize the notes", [file]);
            await SendAsync(b, id, "And what day is it on?");

            var window = (await b.JsonAsync(await b.GetAsync($"/api/chat/conversations/{id}/context"))).GetProperty("window");
            // No engine of this Arena's own here: estimated from characters, shared out to what the engine counted.
            Assert.Equal("estimate", window.GetProperty("counted").GetString());
            Assert.Equal(await LastPromptAsync(b, id), Sum(window));
            var notes = Part(window, "files").GetProperty("items").EnumerateArray().Single(i => i.GetProperty("name").GetString() == "notes.txt");
            Assert.True(notes.GetProperty("tokens").GetInt64() > 0);
            // A file that went with an earlier message: offered to leave out, by its id.
            var drop = window.GetProperty("freeable").EnumerateArray().Single(x => x.GetProperty("action").GetString() == "drop-file");
            Assert.Equal(file, drop.GetProperty("file").GetGuid());
            var question = drop.GetProperty("ref").GetString();

            // Someone else sees no such chat, and changes nothing in it.
            var other = await PersonAsync(app.Factory);
            await StatusAssert.Is(HttpStatusCode.NotFound, await other.GetAsync($"/api/chat/conversations/{id}/context"));
            await StatusAssert.Is(HttpStatusCode.NotFound, await other.Http.PutAsJsonAsync(new Uri($"/api/chat/conversations/{id}/messages/{question}/file-context", UriKind.Relative), new { file, leftOut = true }));

            await StatusAssert.Is(HttpStatusCode.OK, await b.Http.PutAsJsonAsync(new Uri($"/api/chat/conversations/{id}/messages/{question}/file-context", UriKind.Relative), new { file, leftOut = true }));
            // Struck in the view, no longer offered; and the next request reads its name only.
            window = (await b.JsonAsync(await b.GetAsync($"/api/chat/conversations/{id}/context"))).GetProperty("window");
            Assert.True(Part(window, "files").GetProperty("items").EnumerateArray().Single(i => i.GetProperty("name").GetString() == "notes.txt").GetProperty("leftOut").GetBoolean());
            Assert.DoesNotContain(window.GetProperty("freeable").EnumerateArray(), x => x.GetProperty("action").GetString() == "drop-file");
            await SendAsync(b, id, "Thanks");
            var sent = app.Model.Requests.Last(r => r.Body["messages"]!.AsArray().Any(m => m!["content"] is JsonValue v && v.GetValue<string>() == "Thanks")).Body["messages"]!.AsArray();
            var asked = sent.Select(m => m!["content"] is JsonValue v ? v.GetValue<string>() : "").First(c => c.StartsWith("Summarize the notes", StringComparison.Ordinal));
            Assert.DoesNotContain("Thursday at noon", asked, StringComparison.Ordinal);
            Assert.Contains("notes.txt was attached here; the person left it out", asked, StringComparison.Ordinal);
            window = (await b.JsonAsync(await b.GetAsync($"/api/chat/conversations/{id}/context"))).GetProperty("window");
            Assert.DoesNotContain(window.GetProperty("parts").EnumerateArray(), p => p.GetProperty("key").GetString() == "files");
        }
        finally
        {
            app.Model.PromptTokens = null;
        }
    }

    [Fact]
    public void A_request_splits_into_its_pieces_and_what_could_be_freed()
    {
        var request = JsonNode.Parse("""
            {"messages": [
              {"role": "system", "content": "Arena words.\n\nNotes of web.\n\nMemory text."},
              {"role": "user", "content": "Read this\n\n<attachment name=\"a.txt\">\nA long file.\n</attachment>"},
              {"role": "assistant", "content": "", "tool_calls": [{"id": "c1", "type": "function", "function": {"name": "web_search", "arguments": "{\"q\":\"x\"}"}}]},
              {"role": "tool", "tool_call_id": "c1", "content": "A big result."},
              {"role": "assistant", "content": "Done."},
              {"role": "user", "content": "The person used the skill /tidy (Tidy). Follow its instructions:\n\n<skill name=\"tidy\">\nTidy it.\n</skill>\n\nTheir words: go\n\nRecalled: likes tea"}
            ],
            "tools": [{"type": "function", "function": {"name": "web_search"}}, {"type": "function", "function": {"name": "clock_now"}}]}
            """)!.AsObject();
        var pieces = ContextWindow.Pieces(new WindowRequest(request)
        {
            System = [new("system", "Arena's own words", "Arena words.".Length), new("system", "Notes: web", "\n\nNotes of web.".Length, "web"), new("memory", "Memory", "\n\nMemory text.".Length)],
            RefOf = i => $"m{i}",
            Recalled = (5, "\n\nRecalled: likes tea".Length),
            ToolOf = n => n == "web_search" ? "web" : "time",
        });
        Assert.Equal(string.Concat(request["messages"]![0]!["content"]!.GetValue<string>()), string.Concat(pieces.Where(p => p.At == 0).Select(p => p.Text)));
        Assert.Contains(pieces, p => p is { Part: "files", Name: "a.txt", Ref: "m1" });
        Assert.Contains(pieces, p => p is { Part: "toolResults", Name: "web_search", Tool: "web", Ref: "m3" });
        Assert.Contains(pieces, p => p is { Part: "skills", Name: "tidy (used)" });
        Assert.Contains(pieces, p => p is { Part: "memory", Name: "Recalled for this question" });
        Assert.Equal(2, pieces.Count(p => p.Part == "tools"));

        // Counted: the chat format is what the whole holds beyond its pieces; estimated: the total shared out by characters.
        var counted = pieces.Select(p => p.Text.Length / 4).ToList();
        var (tokens, format) = ContextWindow.Count(pieces, counted, counted.Sum() + 30, 4);
        Assert.Equal(30, format);
        var (estimated, none) = ContextWindow.Count(pieces, null, 500, 4);
        Assert.Equal((500L, 0L), (estimated.Sum(), none));
        // Counted one by one they may hold more than together: shared out to the total, no format.
        var (over, nothing) = ContextWindow.Count(pieces, counted, counted.Sum() - 5, 4);
        Assert.Equal((counted.Sum() - 5L, 0L), (over.Sum(), nothing));

        var freeable = ContextWindow.Freeable(pieces, [.. pieces.Select(p => 5_000L)], 100_000, t => t == "time", leaveOut: true, dropFiles: true, compact: true);
        var kinds = freeable.Select(f => (f!["kind"]!.GetValue<string>(), f["action"]?.GetValue<string>(), f["ref"]?.GetValue<string>())).ToList();
        // clock_now was never called (web_search was); the result is big; the file went with an earlier message.
        Assert.Contains(("tool", "tool-off", "time"), kinds);
        Assert.DoesNotContain(kinds, k => k.Item3 == "web" && k.Item1 == "tool");
        Assert.Contains(("result", "leave-out", "m3"), kinds);
        Assert.Contains(("file", "drop-file", "m1"), kinds);
        Assert.Contains(kinds, k => k.Item1 == "compact");
        var shaped = ContextWindow.Shape(pieces, tokens, format, 100_000, 8_000, true, "llama.cpp", freeable);
        Assert.Equal(tokens.Sum() + format, shaped["total"]!.GetValue<long>());
        Assert.Equal(100_000 - 8_000 - shaped["total"]!.GetValue<long>(), shaped["free"]!.GetValue<long>());
        Assert.NotEmpty(ContextWindow.Lines(shaped));
    }
}
