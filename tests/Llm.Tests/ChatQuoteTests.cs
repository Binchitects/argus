using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Llm.Api.Chat;
using Llm.Api.Gateway;
using Llm.Core.Chat;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Llm.Tests;

/// <summary>A question that quotes part of an earlier answer: what the model reads, and every path that keeps the quote.</summary>
[Collection(nameof(AppCollection))]
public sealed class ChatQuoteTests(AppFixture app)
{
    /// <summary>The text the model reads for a quoted question; Code Arena's QuotesTests holds the same.</summary>
    private const string Fixed = "The person quotes this part of an earlier answer:\n<quote>\nUse `git rebase -i` to <\\/quote> squash.\n</quote>\n\nWhy not merge?";

    private static async Task<(TestBrowser Browser, string Email)> PersonAsync(WebApplicationFactory<Program> f)
    {
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "qt" + Guid.NewGuid().ToString("N")[..10];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        return (await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!), $"{name}@example.test");
    }

    private static async Task<Guid> NewChatAsync(TestBrowser b, object? body = null)
    {
        var res = await b.PostAsync("/api/chat/conversations", body ?? new { useArgus = false });
        await StatusAssert.Is(HttpStatusCode.Created, res);
        return (await b.JsonAsync(res)).GetProperty("id").GetGuid();
    }

    private static async Task<string> PostStreamAsync(TestBrowser b, string path, object body)
    {
        var res = await b.PostAsync(path, body);
        var text = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode} {text}");
        return text;
    }

    private static async Task<List<JsonElement>> MessagesAsync(TestBrowser b, Guid id) =>
        [.. (await b.JsonAsync(await b.GetAsync($"/api/chat/conversations/{id}"))).GetProperty("messages").EnumerateArray()];

    private static List<JsonElement> Questions(List<JsonElement> messages) => [.. messages.Where(m => m.GetProperty("role").GetString() == "user")];

    private static string? QuoteOf(JsonElement m) => m.GetProperty("quote").GetString();

    /// <summary>The last question the model read for this person's answers.</summary>
    private string LastAsked(string email) =>
        app.Model.Requests.Last(r => r.Body["user"]!.GetValue<string>() == email && r.Body["messages"]![0]!["content"]!.GetValue<string>().StartsWith("Today is", StringComparison.Ordinal))
            .Body["messages"]!.AsArray().Last(m => m!["role"]!.GetValue<string>() == "user")!["content"]!.GetValue<string>();

    [Fact]
    public void The_model_reads_the_quote_before_the_question_in_a_fixed_shape()
    {
        Assert.Equal(Fixed, ChatQuotes.ForModel("Use `git rebase -i` to </quote> squash.", "Why not merge?"));
        Assert.Equal("Why not merge?", ChatQuotes.ForModel(null, "Why not merge?"));
        Assert.Equal("The person quotes this part of an earlier answer:\n<quote>\nx\n</quote>", ChatQuotes.ForModel("x", ""));
        Assert.Equal("a\nb", ChatQuotes.Clean("  a\r\nb \n"));
        Assert.Null(ChatQuotes.Clean(" \n "));
        Assert.Null(ChatQuotes.Check(new string('q', ChatQuotes.MaxChars)));
        Assert.NotNull(ChatQuotes.Check(new string('q', ChatQuotes.MaxChars + 1)));
        // Passages are searched with what the question asks, the quote included.
        Assert.Equal("the quote\nand?", ChatQuotes.Asked(new ChatMessage { Role = "user", Content = "and?", Quote = "the quote" }));
    }

    [Fact]
    public async Task A_quote_reaches_the_model_before_the_question_and_comes_back_with_the_message()
    {
        var (b, email) = await PersonAsync(app.Factory);
        var id = await NewChatAsync(b);
        await PostStreamAsync(b, $"/api/chat/conversations/{id}/messages", new { content = "How do I tidy commits?" });
        await PostStreamAsync(b, $"/api/chat/conversations/{id}/messages", new { content = "Why not merge?", quote = "Use `git rebase -i` to </quote> squash.\r\n" });
        Assert.Equal(Fixed, LastAsked(email));

        var question = Questions(await MessagesAsync(b, id))[^1];
        Assert.Equal("Why not merge?", question.GetProperty("content").GetString());
        Assert.Equal("Use `git rebase -i` to </quote> squash.", QuoteOf(question));
        Assert.Null(QuoteOf(Questions(await MessagesAsync(b, id))[0]));

        // The next question reads the quoted one as it was asked.
        await PostStreamAsync(b, $"/api/chat/conversations/{id}/messages", new { content = "Thanks" });
        var sent = app.Model.Requests.Last(r => r.Body["user"]!.GetValue<string>() == email).Body["messages"]!.AsArray();
        Assert.Contains(sent, m => m!["role"]!.GetValue<string>() == "user" && m["content"]!.GetValue<string>() == Fixed);
    }

    [Fact]
    public async Task A_quote_over_4000_characters_or_alone_is_refused()
    {
        var (b, _) = await PersonAsync(app.Factory);
        var id = await NewChatAsync(b);
        var tooLong = await b.PostAsync($"/api/chat/conversations/{id}/messages", new { content = "Why?", quote = new string('q', 4001) });
        await StatusAssert.Is(HttpStatusCode.BadRequest, tooLong);
        Assert.Equal("quote", (await b.JsonAsync(tooLong)).GetProperty("status").GetString());
        await StatusAssert.Is(HttpStatusCode.BadRequest, await b.PostAsync($"/api/chat/conversations/{id}/queue", new { content = "Why?", quote = new string('q', 4001) }));
        // A quote alone is not a message: something typed (or a file) is.
        var alone = await b.PostAsync($"/api/chat/conversations/{id}/messages", new { content = " ", quote = "just this" });
        await StatusAssert.Is(HttpStatusCode.BadRequest, alone);
        Assert.Equal("empty", (await b.JsonAsync(alone)).GetProperty("status").GetString());
        // The message's own length limit is for what was typed: 4,000 characters of quote go.
        await PostStreamAsync(b, $"/api/chat/conversations/{id}/messages", new { content = "Why?", quote = new string('q', 4000) });
        Assert.Equal(2, (await MessagesAsync(b, id)).Count);
    }

    [Fact]
    public async Task Edits_queued_messages_forks_and_shared_links_keep_the_quote()
    {
        var (b, _) = await PersonAsync(app.Factory);
        var id = await NewChatAsync(b);
        await PostStreamAsync(b, $"/api/chat/conversations/{id}/messages", new { content = "first" });
        await PostStreamAsync(b, $"/api/chat/conversations/{id}/messages", new { content = "second", quote = "Answer to: first" });
        var second = Questions(await MessagesAsync(b, id))[^1];

        // An edit is a new question with the old one's parent: it keeps the quote it is sent with, or drops it.
        await PostStreamAsync(b, $"/api/chat/conversations/{id}/messages", new { content = "second, edited", quote = "Answer to: first", parentId = second.GetProperty("parentId").GetGuid() });
        await PostStreamAsync(b, $"/api/chat/conversations/{id}/messages", new { content = "second, unquoted", parentId = second.GetProperty("parentId").GetGuid() });
        var questions = Questions(await MessagesAsync(b, id));
        Assert.Equal("Answer to: first", QuoteOf(questions.Single(m => m.GetProperty("content").GetString() == "second, edited")));
        Assert.Null(QuoteOf(questions.Single(m => m.GetProperty("content").GetString() == "second, unquoted")));

        // A queued message keeps its quote while it waits (the list shows it) and when it becomes the question.
        var queued = await b.JsonAsync(await b.PostAsync($"/api/chat/conversations/{id}/queue", new { content = "queued", quote = "a queued quote" }));
        if (queued.GetProperty("queued").EnumerateArray().FirstOrDefault() is { ValueKind: JsonValueKind.Object } waiting)
        {
            Assert.Equal("a queued quote", QuoteOf(waiting));
        }
        JsonElement asked = default;
        for (var i = 0; i < 150 && asked.ValueKind != JsonValueKind.Object; i++)
        {
            asked = Questions(await MessagesAsync(b, id)).FirstOrDefault(m => m.GetProperty("content").GetString() == "queued");
            await Task.Delay(asked.ValueKind == JsonValueKind.Object ? 0 : 100);
        }
        Assert.Equal("a queued quote", QuoteOf(asked));
        for (var i = 0; i < 150 && (await b.JsonAsync(await b.GetAsync($"/api/chat/conversations/{id}"))).GetProperty("answering").GetBoolean(); i++)
        {
            await Task.Delay(100);
        }

        // A fork, and a chat shared and forked from the link, carry it.
        var fork = (await b.JsonAsync(await b.PostAsync($"/api/chat/conversations/{id}/fork", new { }))).GetProperty("id").GetGuid();
        Assert.Equal("a queued quote", QuoteOf(Questions(await MessagesAsync(b, fork))[^1]));
        var share = await b.Http.PutAsJsonAsync(new Uri($"/api/chat/conversations/{id}/share", UriKind.Relative), new { reach = "Company" });
        await StatusAssert.Is(HttpStatusCode.OK, share);
        var shareId = (await b.JsonAsync(share)).GetProperty("id").GetGuid();
        var (other, _) = await PersonAsync(app.Factory);
        var seen = await other.JsonAsync(await other.GetAsync($"/api/shared/{shareId}"));
        Assert.Contains(seen.GetProperty("messages").EnumerateArray(), m => m.GetProperty("role").GetString() == "user" && QuoteOf(m) == "a queued quote");
        var theirs = (await other.JsonAsync(await other.PostAsync($"/api/shared/{shareId}/fork"))).GetProperty("id").GetGuid();
        Assert.Equal("a queued quote", QuoteOf(Questions(await MessagesAsync(other, theirs))[^1]));
    }

    [Fact]
    public async Task The_data_export_keeps_the_quote_and_shows_it_as_a_Markdown_quote()
    {
        var (b, _) = await PersonAsync(app.Factory);
        var id = await NewChatAsync(b);
        await PostStreamAsync(b, $"/api/chat/conversations/{id}/messages", new { content = "Why?", quote = "line one\n\nline two" });
        var res = await b.GetAsync("/api/account/export");
        await StatusAssert.Is(HttpStatusCode.OK, res);
        using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(await res.Content.ReadAsByteArrayAsync()));
        string Read(string suffix)
        {
            using var reader = new StreamReader(zip.Entries.Single(e => e.FullName.StartsWith("chats/", StringComparison.Ordinal) && e.FullName.EndsWith(suffix, StringComparison.Ordinal)).Open());
            return reader.ReadToEnd();
        }
        Assert.Contains("## You\n\n> line one\n>\n> line two\n\nWhy?", Read(".md").ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains("\"quote\": \"line one\\n\\nline two\"", Read(".json"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_comparison_keeps_the_quote_and_both_models_read_it()
    {
        var gateway = new FakeGateway();
        gateway.Models.Add(new GatewayModel("Other-Model", 32768, 4096, Vision: false, Tools: true, Thinking: true, null, null, null));
        await using var f = app.Create(app.ConnectionStringFor("quote_" + Guid.NewGuid().ToString("N")[..8]), gateway);
        var (b, email) = await PersonAsync(f);
        var id = await NewChatAsync(b);
        await PostStreamAsync(b, $"/api/chat/conversations/{id}/compare", new { content = "Why not merge?", quote = "Use `git rebase -i` to </quote> squash." });
        Assert.Equal("Use `git rebase -i` to </quote> squash.", QuoteOf(Questions(await MessagesAsync(b, id)).Single()));
        var asked = app.Model.Requests.Select(r => r.Body)
            .Where(r => r["user"]!.GetValue<string>() == email && r["messages"]![0]!["content"]!.GetValue<string>().StartsWith("Today is", StringComparison.Ordinal))
            .Select(r => r["messages"]!.AsArray().Last()!["content"]!.GetValue<string>()).ToList();
        Assert.Equal(2, asked.Count(a => a == Fixed));
    }

    [Fact]
    public async Task Compaction_and_Auto_read_the_quote()
    {
        var (b, email) = await PersonAsync(app.Factory);
        var id = await NewChatAsync(b);
        await PostStreamAsync(b, $"/api/chat/conversations/{id}/messages", new { content = "first question" });
        await PostStreamAsync(b, $"/api/chat/conversations/{id}/messages", new { content = "why so?", quote = "Answer to: first question" });
        await PostStreamAsync(b, $"/api/chat/conversations/{id}/compact", new { });
        var summarized = app.Model.Requests.Last(r => r.Body["user"]!.GetValue<string>() == email).Body["messages"]![1]!["content"]!.GetValue<string>();
        Assert.Contains("Person (quoting an earlier answer: \"Answer to: first question\"): why so?", summarized, StringComparison.Ordinal);

        // Auto sorts the question with its quote, as the model reads it.
        var gateway = new FakeGateway();
        gateway.Models.Add(new GatewayModel("Qwen3-4B", 32768, 4096, Vision: false, Tools: true, Thinking: true, 0.02m, 0.002m, 0.08m));
        await using var f = app.Create(app.ConnectionStringFor("quote_" + Guid.NewGuid().ToString("N")[..8]), gateway,
            new Dictionary<string, string?> { ["Chat:SmallModel"] = "Qwen3-4B", ["Auth:DataKey"] = "a-data-key-for-quote-tests" });
        var (auto, autoEmail) = await PersonAsync(f);
        var chat = await NewChatAsync(auto, new { model = "auto", tools = Array.Empty<string>() });
        await PostStreamAsync(auto, $"/api/chat/conversations/{chat}/messages", new { content = "fix it [kind:code]", quote = "for (;;) {}" });
        var sorted = app.Model.Requests.Select(r => r.Body).Last(r => r["user"]!.GetValue<string>() == autoEmail
            && r["messages"]![0]!["content"]!.GetValue<string>().StartsWith("You sort the questions", StringComparison.Ordinal));
        Assert.Contains("<question>\nThe person quotes this part of an earlier answer:\n<quote>\nfor (;;) {}\n</quote>\n\nfix it [kind:code]\n</question>",
            sorted["messages"]![1]!["content"]!.GetValue<string>(), StringComparison.Ordinal);
    }
}
