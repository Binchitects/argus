using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Xml.Linq;
using Llm.Api.Chat;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Llm.Tests;

/// <summary>The canvas: written and changed by the model part by part, edited and restored by the person, and exported.</summary>
[Collection(nameof(AppCollection))]
public sealed class CanvasTests(AppFixture app)
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private const string Plan = """
        # Launch plan

        ## Goals
        Ship the canvas to every team by March.

        ## Risks
        The model may rewrite too much.

        ## Budget
        Two people for a quarter.
        """;

    private static async Task<(TestBrowser Browser, string Email)> PersonAsync(WebApplicationFactory<Program> f)
    {
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "c" + Guid.NewGuid().ToString("N")[..10];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        return (await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!), $"{name}@example.test");
    }

    private static async Task<Guid> NewChatAsync(TestBrowser b, object? body = null)
    {
        var res = await b.PostAsync("/api/chat/conversations", body ?? new { });
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

    /// <summary>The model calls one canvas function (FakeModel's [call …]); its result as the page got it.</summary>
    private static async Task<JsonElement> CallAsync(TestBrowser b, Guid chat, string function, object args)
    {
        var events = await SendAsync(b, chat, $"Do it [call {function} {JsonSerializer.Serialize(args)}]");
        var results = events.Where(e => e.GetProperty("type").GetString() == "tool_result").ToList();
        Assert.True(results.Count == 1, string.Join("\n", events));
        return results[0];
    }

    private static async Task<JsonElement> CanvasAsync(TestBrowser b, Guid id) => await b.JsonAsync(await b.GetAsync($"/api/chat/canvases/{id}"));

    private static async Task<Guid> MadeAsync(TestBrowser b, Guid chat, object body)
    {
        var res = await b.PostAsync($"/api/chat/conversations/{chat}/canvases", body);
        await StatusAssert.Is(HttpStatusCode.Created, res);
        return (await b.JsonAsync(res)).GetProperty("id").GetGuid();
    }

    private static Task<HttpResponseMessage> SaveAsync(TestBrowser b, Guid id, object body) =>
        b.Http.PutAsJsonAsync(new Uri($"/api/chat/canvases/{id}", UriKind.Relative), body);

    [Fact]
    public async Task The_model_writes_a_canvas_and_its_change_to_one_section_leaves_the_rest_untouched()
    {
        var (b, email) = await PersonAsync(app.Factory);
        var chat = await NewChatAsync(b);

        var made = await CallAsync(b, chat, "canvas_create", new { title = "Launch plan", kind = "document", content = Plan });
        Assert.False(made.GetProperty("isError").GetBoolean(), made.GetProperty("text").GetString());
        var id = made.GetProperty("details").GetProperty("canvas").GetProperty("id").GetGuid();
        Assert.Equal(1, made.GetProperty("details").GetProperty("canvas").GetProperty("version").GetInt32());

        var edited = await CallAsync(b, chat, "canvas_edit", new
        {
            id, summary = "Named the real risk", edits = new[] { new { find = "The model may rewrite too much.", replace = "A rewrite could undo the person's edits." } },
        });
        Assert.False(edited.GetProperty("isError").GetBoolean(), edited.GetProperty("text").GetString());
        Assert.Equal(2, edited.GetProperty("details").GetProperty("canvas").GetProperty("version").GetInt32());

        var canvas = await CanvasAsync(b, id);
        Assert.Equal(Plan.Replace("The model may rewrite too much.", "A rewrite could undo the person's edits.", StringComparison.Ordinal), canvas.GetProperty("content").GetString());
        var before = Canvases.Split(Plan);
        var after = Canvases.Split(canvas.GetProperty("content").GetString()!);
        Assert.Equal(before.Length, after.Length);
        Assert.Equal([6], Enumerable.Range(0, before.Length).Where(i => before[i] != after[i]));

        var versions = (await b.JsonAsync(await b.GetAsync($"/api/chat/canvases/{id}/versions"))).EnumerateArray().ToList();
        Assert.Equal([2, 1], versions.Select(v => v.GetProperty("number").GetInt32()));
        Assert.All(versions, v => Assert.Equal("model", v.GetProperty("author").GetString()));
        Assert.Equal(["Named the real risk", "Created"], versions.Select(v => v.GetProperty("summary").GetString()));

        // The tool is on in new chats, and from now on the chat's canvases are named in the model's notes.
        var tools = (await b.JsonAsync(await b.GetAsync("/api/chat/config"))).GetProperty("tools").EnumerateArray().ToList();
        Assert.True(tools.Single(t => t.GetProperty("id").GetString() == "canvas").GetProperty("onByDefault").GetBoolean());
        await SendAsync(b, chat, "Thanks");
        var system = app.Model.Requests.Last(r => r.Body["user"]!.GetValue<string>() == email).Body["messages"]![0]!["content"]!.GetValue<string>();
        Assert.Contains($"\"Launch plan\" (id {id}, document)", system, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_find_that_is_missing_or_not_unique_is_refused_with_the_reason_and_nothing_changes()
    {
        var (b, _) = await PersonAsync(app.Factory);
        var chat = await NewChatAsync(b);
        var id = await MadeAsync(b, chat, new { title = "Notes", content = "alpha\nTODO\nbeta\nTODO\n" });

        var missing = await CallAsync(b, chat, "canvas_edit", new { id, edits = new[] { new { find = "gamma", replace = "delta" } } });
        Assert.True(missing.GetProperty("isError").GetBoolean());
        Assert.Contains("is not in the canvas", missing.GetProperty("text").GetString(), StringComparison.Ordinal);

        var twice = await CallAsync(b, chat, "canvas_edit", new { id, edits = new[] { new { find = "TODO", replace = "done" } } });
        Assert.True(twice.GetProperty("isError").GetBoolean());
        Assert.Contains("more than once (lines 2, 4)", twice.GetProperty("text").GetString(), StringComparison.Ordinal);

        // All or none: the first edit would apply, the second cannot, so neither does.
        var half = await CallAsync(b, chat, "canvas_edit", new { id, edits = new[] { new { find = "alpha", replace = "ALPHA" }, new { find = "zeta", replace = "" } } });
        Assert.Contains("Edit 2", half.GetProperty("text").GetString(), StringComparison.Ordinal);

        var canvas = await CanvasAsync(b, id);
        Assert.Equal("alpha\nTODO\nbeta\nTODO\n", canvas.GetProperty("content").GetString());
        Assert.Equal(1, canvas.GetProperty("version").GetInt32());

        // A canvas of another chat is not this chat's.
        var other = await NewChatAsync(b);
        var elsewhere = await CallAsync(b, other, "canvas_read", new { id });
        Assert.True(elsewhere.GetProperty("isError").GetBoolean());
        Assert.Contains("no canvas yet", elsewhere.GetProperty("text").GetString(), StringComparison.Ordinal);

        // Enough of the text around it makes it unique.
        var fixedUp = await CallAsync(b, chat, "canvas_edit", new { id, edits = new[] { new { find = "beta\nTODO", replace = "beta\ndone" } } });
        Assert.False(fixedUp.GetProperty("isError").GetBoolean(), fixedUp.GetProperty("text").GetString());
        Assert.Equal("alpha\nTODO\nbeta\ndone\n", (await CanvasAsync(b, id)).GetProperty("content").GetString());
    }

    [Fact]
    public async Task The_model_reads_a_canvas_by_lines_and_lists_the_chats_canvases()
    {
        var (b, _) = await PersonAsync(app.Factory);
        var chat = await NewChatAsync(b);
        var id = await MadeAsync(b, chat, new { title = "parser.py", kind = "code", language = "Python", content = "import re\n\ndef parse(s):\n    return s\n" });

        var read = JsonDocument.Parse((await CallAsync(b, chat, "canvas_read", new { id, from_line = 3, to_line = 4 })).GetProperty("text").GetString()!).RootElement;
        Assert.Equal("def parse(s):\n    return s\n", read.GetProperty("content").GetString());
        Assert.Equal(4, read.GetProperty("total_lines").GetInt32());
        Assert.Equal("python", read.GetProperty("language").GetString());

        var list = JsonDocument.Parse((await CallAsync(b, chat, "canvas_read", new { })).GetProperty("text").GetString()!).RootElement;
        Assert.Equal(["parser.py"], list.GetProperty("canvases").EnumerateArray().Select(c => c.GetProperty("title").GetString()));

        // A rewrite starts over, as a version of its own.
        var rewritten = await CallAsync(b, chat, "canvas_rewrite", new { id, content = "print('hi')\n" });
        Assert.False(rewritten.GetProperty("isError").GetBoolean(), rewritten.GetProperty("text").GetString());
        Assert.Equal("print('hi')\n", (await CanvasAsync(b, id)).GetProperty("content").GetString());
        var versions = (await b.JsonAsync(await b.GetAsync($"/api/chat/canvases/{id}/versions"))).EnumerateArray().ToList();
        Assert.Equal(("model", "Rewritten"), (versions[0].GetProperty("author").GetString(), versions[0].GetProperty("summary").GetString()));
        Assert.Equal("person", versions[1].GetProperty("author").GetString());
    }

    [Fact]
    public async Task Every_version_can_be_restored_and_a_restore_is_a_version_too()
    {
        var (b, _) = await PersonAsync(app.Factory);
        var chat = await NewChatAsync(b);
        var id = await MadeAsync(b, chat, new { title = "Letter", content = "Dear team,\n" });
        string[] texts = ["Dear team,\n", "Dear team,\nWe ship Friday.\n", "Dear all,\nWe ship Friday.\n", "Dear all,\nWe ship Monday.\nThanks.\n"];
        for (var v = 1; v < texts.Length; v++)
        {
            var saved = await SaveAsync(b, id, new { baseVersion = v, content = texts[v] });
            await StatusAssert.Is(HttpStatusCode.OK, saved);
            Assert.Equal(v + 1, (await b.JsonAsync(saved)).GetProperty("version").GetInt32());
        }
        var versions = (await b.JsonAsync(await b.GetAsync($"/api/chat/canvases/{id}/versions"))).EnumerateArray().ToList();
        Assert.Equal("Changed lines 2–3", versions[0].GetProperty("summary").GetString());
        Assert.Equal("Changed line 1", versions[1].GetProperty("summary").GetString());
        Assert.Equal("Added line 2", versions[2].GetProperty("summary").GetString());
        Assert.All(versions, v => Assert.Equal("person", v.GetProperty("author").GetString()));

        // Each version, restored, is the canvas again, as a new version that says what it restored.
        var next = texts.Length + 1; // after the four versions saved
        for (var v = 1; v <= texts.Length; v++)
        {
            var old = await b.JsonAsync(await b.GetAsync($"/api/chat/canvases/{id}/versions/{v}"));
            Assert.Equal(texts[v - 1], old.GetProperty("content").GetString());
            var restored = await b.PostAsync($"/api/chat/canvases/{id}/versions/{v}/restore");
            await StatusAssert.Is(HttpStatusCode.OK, restored);
            var now = await b.JsonAsync(restored);
            Assert.Equal(texts[v - 1], now.GetProperty("content").GetString());
            Assert.Equal(next++, now.GetProperty("version").GetInt32());
            var latest = (await b.JsonAsync(await b.GetAsync($"/api/chat/canvases/{id}/versions")))[0];
            Assert.Equal($"Restored version {v}", latest.GetProperty("summary").GetString());
        }
        await StatusAssert.Is(HttpStatusCode.NotFound, await b.PostAsync($"/api/chat/canvases/{id}/versions/99/restore"));

        // A save made on an older version is refused: the model's change in between is not lost.
        var stale = await SaveAsync(b, id, new { baseVersion = 2, content = "Mine" });
        await StatusAssert.Is(HttpStatusCode.Conflict, stale);
        Assert.Equal("changed", (await b.JsonAsync(stale)).GetProperty("status").GetString());
        Assert.Equal(texts[^1], (await CanvasAsync(b, id)).GetProperty("content").GetString());

        // Saving the same text again makes no version.
        var version = (await CanvasAsync(b, id)).GetProperty("version").GetInt32();
        Assert.Equal(version, (await b.JsonAsync(await SaveAsync(b, id, new { baseVersion = version, content = texts[^1] }))).GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task A_canvas_is_its_owners_only_and_goes_with_its_chat()
    {
        var (b, _) = await PersonAsync(app.Factory);
        var (other, _) = await PersonAsync(app.Factory);
        var chat = await NewChatAsync(b);
        var id = await MadeAsync(b, chat, new { title = "Private", content = "mine" });

        await StatusAssert.Is(HttpStatusCode.NotFound, await other.GetAsync($"/api/chat/canvases/{id}"));
        await StatusAssert.Is(HttpStatusCode.NotFound, await other.GetAsync($"/api/chat/conversations/{chat}/canvases"));
        await StatusAssert.Is(HttpStatusCode.NotFound, await other.GetAsync($"/api/chat/canvases/{id}/versions"));
        await StatusAssert.Is(HttpStatusCode.NotFound, await other.GetAsync($"/api/chat/canvases/{id}/export?format=md"));
        await StatusAssert.Is(HttpStatusCode.NotFound, await SaveAsync(other, id, new { baseVersion = 1, content = "theirs" }));
        await StatusAssert.Is(HttpStatusCode.NotFound, await other.PostAsync($"/api/chat/conversations/{chat}/canvases", new { title = "Sneaky" }));
        Assert.Equal("mine", (await CanvasAsync(b, id)).GetProperty("content").GetString());

        // Wrong shapes are refused with the reason.
        var wrong = await b.PostAsync($"/api/chat/conversations/{chat}/canvases", new { title = "X", kind = "spreadsheet" });
        await StatusAssert.Is(HttpStatusCode.BadRequest, wrong);
        Assert.Contains("document", (await b.JsonAsync(wrong)).GetProperty("error").GetString(), StringComparison.Ordinal);

        await StatusAssert.Is(HttpStatusCode.NoContent, await b.Http.DeleteAsync(new Uri($"/api/chat/conversations/{chat}", UriKind.Relative)));
        await StatusAssert.Is(HttpStatusCode.NotFound, await b.GetAsync($"/api/chat/canvases/{id}"));
    }

    [Fact]
    public async Task A_document_exports_as_markdown_and_as_a_word_file_with_the_right_parts()
    {
        var (b, _) = await PersonAsync(app.Factory);
        var chat = await NewChatAsync(b);
        const string text = """
            # Report

            Some **bold** and *italic* text with `code` and a [link](https://example.com/a?b=1&c=2).

            - first
            - second
              - nested

            1. one
            2. two

            > A quote.

            ```python
            print("hi")
            ```

            | Name | Value |
            |------|-------|
            | a    | 1     |

            گزارش فارسی
            """;
        var id = await MadeAsync(b, chat, new { title = "Q3: report", content = text });

        var md = await b.GetAsync($"/api/chat/canvases/{id}/export?format=md");
        await StatusAssert.Is(HttpStatusCode.OK, md);
        Assert.Equal(text, await md.Content.ReadAsStringAsync());
        Assert.Equal("Q3 report.md", md.Content.Headers.ContentDisposition?.FileNameStar ?? md.Content.Headers.ContentDisposition?.FileName);

        var docx = await b.GetAsync($"/api/chat/canvases/{id}/export?format=docx");
        await StatusAssert.Is(HttpStatusCode.OK, docx);
        Assert.Equal("application/vnd.openxmlformats-officedocument.wordprocessingml.document", docx.Content.Headers.ContentType?.MediaType);
        using var zip = new ZipArchive(new MemoryStream(await docx.Content.ReadAsByteArrayAsync()), ZipArchiveMode.Read);
        Assert.Superset(new HashSet<string> { "[Content_Types].xml", "_rels/.rels", "word/document.xml", "word/styles.xml", "word/numbering.xml", "word/_rels/document.xml.rels", "docProps/core.xml" },
            zip.Entries.Select(e => e.FullName).ToHashSet());
        XDocument Part(string name)
        {
            using var s = zip.GetEntry(name)!.Open();
            return XDocument.Load(s);
        }
        // Every part is well-formed XML.
        foreach (var e in zip.Entries)
        {
            Part(e.FullName);
        }
        var doc = Part("word/document.xml");
        var paragraphs = doc.Descendants(W + "p").ToList();
        string Style(XElement p) => p.Element(W + "pPr")?.Element(W + "pStyle")?.Attribute(W + "val")?.Value ?? "";
        string Text(XElement p) => string.Concat(p.Descendants(W + "t").Select(t => t.Value));
        Assert.Equal("Report", Text(paragraphs.Single(p => Style(p) == "Heading1")));
        var lists = paragraphs.Where(p => Style(p) == "ListParagraph").ToList();
        Assert.Equal(["first", "second", "nested", "one", "two"], lists.Select(Text));
        Assert.Equal("1", lists[2].Descendants(W + "ilvl").Single().Attribute(W + "val")!.Value);
        Assert.NotEqual(lists[0].Descendants(W + "numId").Single().Attribute(W + "val")!.Value, lists[3].Descendants(W + "numId").Single().Attribute(W + "val")!.Value);
        Assert.Equal("print(\"hi\")", Text(paragraphs.Single(p => Style(p) == "Code")));
        Assert.Equal("A quote.", Text(paragraphs.Single(p => Style(p) == "Quote")));
        var body = paragraphs.First(p => Text(p).StartsWith("Some", StringComparison.Ordinal));
        Assert.Equal("Some bold and italic text with code and a link.", Text(body));
        Assert.Contains(body.Descendants(W + "r"), r => r.Element(W + "rPr")?.Element(W + "b") is not null && r.Value == "bold");
        Assert.Contains(body.Descendants(W + "r"), r => r.Element(W + "rPr")?.Element(W + "i") is not null && r.Value == "italic");
        var link = body.Element(W + "hyperlink")!;
        var rels = Part("word/_rels/document.xml.rels");
        Assert.Equal("https://example.com/a?b=1&c=2", rels.Root!.Elements().Single(r => r.Attribute("Id")!.Value == link.Attribute(XName.Get("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships"))!.Value).Attribute("Target")!.Value);
        var table = doc.Descendants(W + "tbl").Single();
        Assert.Equal([["Name", "Value"], ["a", "1"]], table.Elements(W + "tr").Select(r => r.Elements(W + "tc").Select(c => Text(c.Element(W + "p")!)).ToArray()).ToArray());
        // A Persian paragraph reads right to left.
        Assert.NotNull(paragraphs.Single(p => Text(p) == "گزارش فارسی").Element(W + "pPr")!.Element(W + "bidi"));
        Assert.Equal("Q3: report", Part("docProps/core.xml").Descendants(XName.Get("title", "http://purl.org/dc/elements/1.1/")).Single().Value);
        // The styles the paragraphs name are defined.
        var styles = Part("word/styles.xml").Descendants(W + "style").Select(s => s.Attribute(W + "styleId")!.Value).ToHashSet();
        Assert.Superset(paragraphs.Select(Style).Where(s => s.Length > 0).ToHashSet(), styles);

        await StatusAssert.Is(HttpStatusCode.BadRequest, await b.GetAsync($"/api/chat/canvases/{id}/export?format=odt"));
    }

    [Fact]
    public async Task A_code_canvas_downloads_as_its_file()
    {
        var (b, _) = await PersonAsync(app.Factory);
        var chat = await NewChatAsync(b);
        var id = await MadeAsync(b, chat, new { title = "Rename photos", kind = "code", language = "python", content = "print(1)\n" });
        var file = await b.GetAsync($"/api/chat/canvases/{id}/export?format=file");
        await StatusAssert.Is(HttpStatusCode.OK, file);
        Assert.Equal("print(1)\n", await file.Content.ReadAsStringAsync());
        Assert.Equal("Rename photos.py", file.Content.Headers.ContentDisposition?.FileNameStar ?? file.Content.Headers.ContentDisposition?.FileName);

        var named = await MadeAsync(b, chat, new { title = "main.go", kind = "code", language = "go", content = "package main\n" });
        var go = await b.GetAsync($"/api/chat/canvases/{named}/export?format=file");
        Assert.Equal("main.go", go.Content.Headers.ContentDisposition?.FileNameStar ?? go.Content.Headers.ContentDisposition?.FileName);
    }

    [Fact]
    public async Task A_PDF_is_made_from_the_Word_file_in_the_sandbox_and_without_it_the_page_is_told_why()
    {
        await using var sandbox = new FakeSandbox();
        await using var f = app.Create(app.ConnectionStringFor("canvas_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(),
            new Dictionary<string, string?> { ["Sandbox:Dir"] = sandbox.Dir });
        var (b, _) = await PersonAsync(f);
        var chat = await NewChatAsync(b);
        var id = await MadeAsync(b, chat, new { title = "Plan", content = Plan });

        var pdf = await b.GetAsync($"/api/chat/canvases/{id}/export?format=pdf");
        await StatusAssert.Is(HttpStatusCode.OK, pdf);
        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType?.MediaType);
        Assert.StartsWith("%PDF-", await pdf.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var job = Assert.Single(sandbox.Jobs);
        Assert.Contains("--convert-to", job["code"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(["canvas.docx"], job["files"]!.AsArray().Select(n => n!.GetValue<string>()));

        // No sandbox here: the page is told, and prints the document itself instead.
        var (c, _) = await PersonAsync(app.Factory);
        var other = await MadeAsync(c, await NewChatAsync(c), new { title = "Plan", content = Plan });
        var refused = await c.GetAsync($"/api/chat/canvases/{other}/export?format=pdf");
        await StatusAssert.Is(HttpStatusCode.ServiceUnavailable, refused);
        Assert.Equal("no_sandbox", (await c.JsonAsync(refused)).GetProperty("status").GetString());
    }

    [Fact]
    public void Edits_apply_in_turn_and_a_summary_says_where_the_text_changed()
    {
        Assert.Equal("a\nB\nC\n", Canvases.Apply("a\nb\nc\n", [new CanvasEdit("b", "B"), new CanvasEdit("B\nc", "B\nC")]));
        // Line breaks as Windows writes them match too.
        Assert.Equal("one\ntwo!\n", Canvases.Apply("one\ntwo\n", [new CanvasEdit("one\r\ntwo", "one\r\ntwo!")]));
        var empty = Assert.Throws<CanvasException>(() => Canvases.Apply("x", [new CanvasEdit("", "y")]));
        Assert.Contains("nothing in \"find\"", empty.Message, StringComparison.Ordinal);
        var near = Assert.Throws<CanvasException>(() => Canvases.Apply("## Risks\nThe model may rewrite.\n", [new CanvasEdit("## Risks\nThe model might rewrite.", "x")]));
        Assert.Contains("line 1", near.Message, StringComparison.Ordinal);

        Assert.Equal("Changed line 2", Canvases.Changed("a\nb\nc", "a\nB\nc"));
        Assert.Equal("Added lines 3–4", Canvases.Changed("a\nb", "a\nb\nc\nd"));
        Assert.Equal("Removed line 1", Canvases.Changed("a\nb", "b"));
        Assert.Equal("Renamed", Canvases.Changed("a", "a", renamed: true));
    }
}
