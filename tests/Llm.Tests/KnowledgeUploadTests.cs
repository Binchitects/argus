using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Llm.Api.Knowledge;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Llm.Tests;

/// <summary>
/// Files uploaded as company knowledge: many kinds read (Markdown, HTML, e-mail, EPUB, an archive's files each on its own),
/// pictures and older Office files read in the sandbox (OCR, LibreOffice), their text found by search; a file replaced or
/// removed; names that would reach outside kept inside.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class KnowledgeUploadTests(AppFixture app)
{
    private static byte[] Zip(params (string Name, string Text)[] files)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in files)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open());
                w.Write(text);
            }
        }
        return ms.ToArray();
    }

    private static async Task<HttpResponseMessage> UploadAsync(TestBrowser admin, Guid source, params (string Name, byte[] Bytes)[] files)
    {
        using var form = new MultipartFormDataContent();
        foreach (var (name, bytes) in files)
        {
            var part = new ByteArrayContent(bytes);
            part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(part, "files", name);
        }
        return await admin.Http.PostAsync(new Uri($"/api/admin/knowledge/{source}/files", UriKind.Relative), form);
    }

    [Fact]
    public async Task Uploaded_files_of_many_kinds_become_knowledge_pictures_and_old_office_files_read_in_the_sandbox()
    {
        await using var sandbox = new FakeSandbox();
        await using var f = app.Create(app.ConnectionStringFor("kupload_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(), new Dictionary<string, string?>
        {
            ["Auth:DataKey"] = "a-data-key-for-knowledge-tests", ["Modules:embed"] = "true", ["Modules:sandbox"] = "true", ["Sandbox:Dir"] = sandbox.Dir,
        });
        var admin = await KnowledgeTests.AdminAsync(f);
        var (id, _) = await KnowledgeTests.AddAsync(admin, new { name = "Company files", kind = "upload" });

        var mail = "Subject: =?UTF-8?B?UmVmdW5kIHBvbGljeQ==?=\nFrom: ops@example.test\nContent-Type: text/plain; charset=utf-8\nContent-Transfer-Encoding: quoted-printable\n\nRefunds are paid within 14 d=\nays.\n";
        var res = await UploadAsync(admin, id,
            ("handbook/notes.md", "# Notes\n\nThe office opens at eight."u8.ToArray()),
            ("scans/receipt.png", [0x89, 0x50, 0x4E, 0x47, 1, 2, 3]),
            ("mail/refunds.eml", Encoding.UTF8.GetBytes(mail)),
            ("archive.zip", Zip(("hr/leave.md", "Everyone gets 30 days of paid leave."), (".hidden/x.md", "hidden"), ("__MACOSX/._leave.md", "junk"))),
            ("legacy/contract.doc", [0xD0, 0xCF, 0x11, 0xE0]),
            ("../../etc/passwd.txt", "root:x:0:0"u8.ToArray()));
        await StatusAssert.Is(HttpStatusCode.OK, res);
        var added = (await admin.JsonAsync(res)).GetProperty("added").EnumerateArray().Select(a => a.GetString()).ToList();
        // A name that would reach outside is kept inside.
        Assert.Contains("etc/passwd.txt", added);

        var synced = await KnowledgeTests.SyncAgainAsync(admin, id);
        Assert.Equal("synced", synced.GetProperty("state").GetString());
        var docs = (await admin.JsonAsync(await admin.GetAsync($"/api/admin/knowledge/{id}/documents"))).EnumerateArray()
            .Where(d => d.GetProperty("passages").GetInt32() > 0).Select(d => d.GetProperty("title").GetString()).Order().ToList();
        Assert.Equal(["archive.zip/hr/leave.md", "etc/passwd.txt", "handbook/notes.md", "legacy/contract.doc", "mail/refunds.eml", "scans/receipt.png"], docs);

        // The picture went to the sandbox's OCR, in the languages set; its text is found.
        Assert.Contains(sandbox.Jobs, j => j["code"]!.GetValue<string>().Contains("pytesseract", StringComparison.Ordinal));
        var found = await KnowledgeTests.SearchAsync(app, admin, "Invoice 4711");
        Assert.Equal("scans/receipt.png", found.GetProperty("results")[0].GetProperty("title").GetString());

        // The files, with what each became.
        var files = (await admin.JsonAsync(await admin.GetAsync($"/api/admin/knowledge/{id}/files"))).EnumerateArray().ToList();
        Assert.Equal(1, files.Single(x => x.GetProperty("path").GetString() == "archive.zip").GetProperty("documents").GetInt32());
        Assert.Equal("Administrator", files[0].GetProperty("uploadedBy").GetString());

        // Removed: its documents go at the sync that follows.
        var scan = files.Single(x => x.GetProperty("path").GetString() == "scans/receipt.png").GetProperty("id").GetGuid();
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.Http.DeleteAsync(new Uri($"/api/admin/knowledge/{id}/files/{scan}", UriKind.Relative)));
        await KnowledgeTests.SyncAgainAsync(admin, id);
        Assert.DoesNotContain((await admin.JsonAsync(await admin.GetAsync($"/api/admin/knowledge/{id}/documents"))).EnumerateArray(),
            d => d.GetProperty("title").GetString() == "scans/receipt.png");
    }

    [Fact]
    public async Task Without_the_sandbox_a_picture_is_said_to_need_it_and_the_rest_is_read()
    {
        await using var f = app.Create(app.ConnectionStringFor("kupload_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(), new Dictionary<string, string?>
        {
            ["Auth:DataKey"] = "a-data-key-for-knowledge-tests", ["Modules:embed"] = "true", ["Modules:sandbox"] = "false",
        });
        var admin = await KnowledgeTests.AdminAsync(f);
        var (id, _) = await KnowledgeTests.AddAsync(admin, new { name = "Scans", kind = "upload" });
        await StatusAssert.Is(HttpStatusCode.OK, await UploadAsync(admin, id, ("scan.jpg", [0xFF, 0xD8, 0xFF, 0]), ("notes.txt", "Plain words."u8.ToArray())));
        // Said at every sync, and read once the sandbox runs: it is not kept as an empty document.
        var synced = await KnowledgeTests.SyncAgainAsync(admin, id);
        Assert.Contains("needs the sandbox feature", synced.GetProperty("error").GetString(), StringComparison.Ordinal);
        var docs = (await admin.JsonAsync(await admin.GetAsync($"/api/admin/knowledge/{id}/documents"))).EnumerateArray().Select(d => d.GetProperty("title").GetString()).ToList();
        Assert.Equal(["notes.txt"], docs);
    }

    [Fact]
    public void An_email_is_read_with_its_headers_and_its_text_part()
    {
        var mail = "Subject: Hello\r\nFrom: a@b.test\r\nContent-Type: multipart/alternative; boundary=\"xx\"\r\n\r\n--xx\r\nContent-Type: text/html\r\n\r\n<p>Hi <b>there</b></p>\r\n--xx\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: base64\r\n\r\n"
            + Convert.ToBase64String("Hi there, plainly."u8.ToArray()) + "\r\n--xx--\r\n";
        var text = DocumentReader.Email(mail);
        Assert.StartsWith("Subject: Hello\nFrom: a@b.test", text, StringComparison.Ordinal);
        Assert.EndsWith("Hi there, plainly.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_epub_is_read_chapter_by_chapter()
    {
        var book = Zip(("OEBPS/ch1.xhtml", "<html><head><title>One</title></head><body><p>First chapter.</p></body></html>"),
            ("OEBPS/ch2.xhtml", "<html><head><title>Two</title></head><body><p>Second chapter.</p></body></html>"), ("mimetype", "application/epub+zip"));
        var text = DocumentReader.Epub(book);
        Assert.True(text.IndexOf("First chapter.", StringComparison.Ordinal) < text.IndexOf("Second chapter.", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("../../etc/passwd", "etc/passwd")]
    [InlineData("C:\\Users\\a\\report.pdf", "C:/Users/a/report.pdf")]
    [InlineData("docs/.git/config", "docs/config")]
    [InlineData("./a/./b.md", "a/b.md")]
    [InlineData("..", null)]
    public void An_uploaded_name_is_kept_inside(string name, string? kept) => Assert.Equal(kept, KnowledgeEndpoints.CleanPath(name));
}
