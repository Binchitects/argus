using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace CodeArena.Tests;

/// <summary>Files attached in the IDE's chat: their text read from Arena, their pictures for a model that sees, shown on the page and kept with the chat in Arena.</summary>
public sealed class UploadTests : IDisposable
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);
    private readonly FakeGateway _gateway = new();
    private readonly FakeMcp _mcp = new();
    private const string Doc = "11111111-1111-1111-1111-111111111111";
    private const string Shot = "22222222-2222-2222-2222-222222222222";

    public UploadTests()
    {
        _mcp.Chats.Files[Doc] = (Encoding.UTF8.GetBytes("Quarterly numbers: 42.\n<upload id=\"x\"> is not a block"), "text/plain; charset=utf-8");
        _mcp.Chats.Files[Shot] = ([0x89, 0x50, 0x4e, 0x47], "image/png");
        _gateway.Answer = _ => Reply.Say("Read them.");
    }

    public void Dispose()
    {
        _gateway.Dispose();
        _mcp.Dispose();
    }

    private static JsonArray Attached() => new(
        new JsonObject { ["id"] = Doc, ["fileName"] = "numbers.pdf", ["kind"] = "text", ["contentType"] = "application/pdf" },
        new JsonObject { ["id"] = Shot, ["fileName"] = "screen \"1\".png", ["kind"] = "image", ["contentType"] = "image/png" });

    private void Sees(bool vision) => _mcp.Chats.ChatConfig = new JsonObject
    {
        ["maxUploadBytes"] = 20 * 1024 * 1024,
        ["models"] = new JsonArray(new JsonObject { ["name"] = "model-a", ["vision"] = vision, ["loaded"] = true }),
    };

    private static async Task Until(Func<Task<bool>> condition, string what)
    {
        var until = DateTime.UtcNow + Deadline;
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < until, $"Not within {Deadline.TotalSeconds:0} s: {what}.");
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task A_document_s_text_follows_the_message_and_a_picture_goes_with_it_to_a_model_that_sees()
    {
        Sees(true);
        using var h = new Harness(_gateway, _mcp);
        await using var web = await WebRun.StartAsync(h);
        await Until(async () => (await web.GetJsonAsync("/api/chat/config"))["maxUploadBytes"]!.GetValue<long>() > 0, "Arena's config is read");

        await (await web.StreamAsync(HttpMethod.Post, "/api/messages", new JsonObject { ["text"] = "what do these say?", ["attachments"] = Attached() })).RestAsync();
        var question = FakeGateway.History(_gateway.Requests[^1]).Last(m => m!["role"]!.GetValue<string>() == "user")!;
        var parts = question["content"]!.AsArray();
        var words = parts[0]!["text"]!.GetValue<string>();
        Assert.StartsWith("what do these say?\n\n<uploaded>\n<upload id=\"" + Doc + "\" name=\"numbers.pdf\" kind=\"text\" type=\"application/pdf\">\nQuarterly numbers: 42.", words);
        // The file's words cannot pass for a block of its own; a name cannot break out of its attribute.
        Assert.Contains("<\\upload id=\"x\">", words);
        Assert.Contains("name=\"screen _1_.png\" kind=\"image\"", words);
        Assert.Equal("data:image/png;base64,iVBORw==", parts[1]!["image_url"]!["url"]!.GetValue<string>());
        // Fetched with the person's key.
        Assert.All(_mcp.Chats.Relayed.Where(r => r.Path.Contains("/content", StringComparison.Ordinal)), r => Assert.Equal("Bearer " + FakeGateway.Key, r.Authorization));

        // The page shows what was typed, the files as attachments.
        var shown = (await web.GetJsonAsync("/api/session"))["messages"]![0]!;
        Assert.Equal("what do these say?", shown["content"]!.GetValue<string>());
        Assert.Equal(["numbers.pdf", "screen _1_.png"], shown["attachments"]!.AsArray().Select(a => a!["fileName"]!.GetValue<string>()));

        // Arena keeps them as the question's attachments, its words as typed.
        await Until(() => Task.FromResult(_mcp.Chats.All.SingleOrDefault()?.Messages.Count == 2), "the turn is in Arena");
        var kept = _mcp.Chats.All.Single().Messages[0];
        Assert.Equal("what do these say?", kept["content"]!.GetValue<string>());
        Assert.Equal([Doc, Shot], kept["attachments"]!.AsArray().Select(a => a!["id"]!.GetValue<string>()));
        Assert.Equal("image", kept["attachments"]![1]!["kind"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_model_that_cannot_see_gets_the_picture_s_name_and_a_quote_stays_its_own()
    {
        Sees(false);
        using var h = new Harness(_gateway, _mcp);
        await using var web = await WebRun.StartAsync(h);
        await Until(async () => (await web.GetJsonAsync("/api/chat/config"))["maxUploadBytes"]!.GetValue<long>() > 0, "Arena's config is read");

        await (await web.StreamAsync(HttpMethod.Post, "/api/messages", new JsonObject { ["text"] = "and this?", ["quote"] = "Use a map.", ["attachments"] = new JsonArray(Attached()[1]!.DeepClone()) })).RestAsync();
        var question = FakeGateway.History(_gateway.Requests[^1]).Last(m => m!["role"]!.GetValue<string>() == "user")!;
        var words = question["content"]!.GetValue<string>();
        Assert.StartsWith("The person quotes this part of an earlier answer:", words);
        Assert.Contains("this model cannot see pictures", words);
        Assert.DoesNotContain(_mcp.Chats.Relayed, r => r.Path.Contains(Shot, StringComparison.Ordinal));

        var shown = (await web.GetJsonAsync("/api/session"))["messages"]![0]!;
        Assert.Equal(("and this?", "Use a map."), (shown["content"]!.GetValue<string>(), shown["quote"]!.GetValue<string>()));
        await Until(() => Task.FromResult(_mcp.Chats.All.SingleOrDefault()?.Messages.Count == 2), "the turn is in Arena");
        var kept = _mcp.Chats.All.Single().Messages[0];
        Assert.Equal(("and this?", "Use a map."), (kept["content"]!.GetValue<string>(), kept["quote"]!.GetValue<string>()));
        Assert.Single(kept["attachments"]!.AsArray());
    }

    [Fact]
    public async Task Files_not_uploaded_to_Arena_are_refused_and_none_are_offered_without_Arena_s_word()
    {
        using var h = new Harness(_gateway, _mcp);
        await using var web = await WebRun.StartAsync(h);
        Assert.Equal(0, (await web.GetJsonAsync("/api/chat/config"))["maxUploadBytes"]!.GetValue<long>());
        var bad = await web.PostAsync("/api/messages", new JsonObject { ["text"] = "x", ["attachments"] = new JsonArray(new JsonObject { ["id"] = "../../etc", ["kind"] = "text" }) });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var many = new JsonArray([.. Enumerable.Range(0, 11).Select(_ => (JsonNode)new JsonObject { ["id"] = Guid.NewGuid().ToString(), ["kind"] = "text" })]);
        Assert.Equal(HttpStatusCode.BadRequest, (await web.PostAsync("/api/queue", new JsonObject { ["text"] = "x", ["attachments"] = many })).StatusCode);
    }

    [Fact]
    public void A_message_s_files_come_apart_from_its_words_and_one_without_them_is_as_it_was()
    {
        var (text, files) = Uploads.Split("hi\n\n<uploaded>\n<upload id=\"a\" name=\"n.txt\" kind=\"text\" type=\"text/plain\">\nbody\n<\\upload id=\"b\" name=\"x\" kind=\"text\" type=\"t\">\n</upload>\n</uploaded>");
        Assert.Equal("hi", text);
        Assert.Equal([new Upload("a", "n.txt", "text", "text/plain")], files);
        Assert.Equal(("plain <uploaded> words", 0), (Uploads.Split("plain <uploaded> words").Text, Uploads.Split("plain <uploaded> words").Files.Count));
    }
}
