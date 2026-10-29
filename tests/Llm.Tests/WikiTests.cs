using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Llm.Api.Wiki;

namespace Llm.Tests;

/// <summary>
/// The wiki: people search the company's documents by their words with no model, list a source's documents and read one
/// whole; each only what they may read.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class WikiTests(AppFixture app)
{
    [Fact]
    [Trait("Control", "IAM-05")]
    public async Task People_find_and_read_the_documents_they_may_read_and_nothing_else()
    {
        await using var f = app.Create(app.ConnectionStringFor("wiki_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(), new Dictionary<string, string?>
        {
            ["Auth:DataKey"] = "a-data-key-for-wiki-tests", ["Modules:embed"] = "true",
        });
        var admin = await KnowledgeTests.AdminAsync(f);
        var (hanna, hannaId) = await KnowledgeTests.PersonAsync(f, admin, "wikihanna" + Guid.NewGuid().ToString("N")[..4]);
        var (ivan, _) = await KnowledgeTests.PersonAsync(f, admin, "wikiivan" + Guid.NewGuid().ToString("N")[..4]);
        var hr = (await admin.JsonAsync(await admin.PostAsync("/api/admin/groups", new { name = "HR " + Guid.NewGuid().ToString("N")[..4] }))).GetProperty("id").GetGuid();
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.PostAsync($"/api/admin/groups/{hr}/members", new { userIds = new[] { hannaId } }));
        var (id, _) = await KnowledgeTests.AddAsync(admin, new { name = "HR files", kind = "upload", audience = "Groups", groups = new[] { hr } });
        using (var form = new MultipartFormDataContent())
        {
            var part = new ByteArrayContent("# Leave\n\n## Paid leave\n\nEveryone gets 30 days of paid leave a year, booked in the HR portal.\n\n## Sick leave\n\nTell your manager by nine."u8.ToArray());
            part.Headers.ContentType = new MediaTypeHeaderValue("text/markdown");
            form.Add(part, "files", "policies/leave.md");
            await StatusAssert.Is(HttpStatusCode.OK, await admin.Http.PostAsync(new Uri($"/api/admin/knowledge/{id}/files", UriKind.Relative), form));
        }
        await KnowledgeTests.SyncAgainAsync(admin, id);

        // Hanna, in HR: the source, its document, found by its words, and read whole.
        var home = await hanna.JsonAsync(await hanna.GetAsync("/api/wiki"));
        Assert.Equal(1, Assert.Single(home.GetProperty("sources").EnumerateArray(), s => s.GetProperty("id").GetGuid() == id).GetProperty("documents").GetInt32());
        var found = await hanna.JsonAsync(await hanna.GetAsync("/api/wiki/search?q=paid%20leave&in=knowledge"));
        var hit = found.GetProperty("knowledge")[0];
        Assert.Equal("policies/leave.md", hit.GetProperty("title").GetString());
        Assert.Contains("30 days of paid leave", hit.GetProperty("snippet").GetString(), StringComparison.Ordinal);
        var doc = hit.GetProperty("documentId").GetGuid();
        var listed = (await hanna.JsonAsync(await hanna.GetAsync($"/api/wiki/documents?source={id}"))).GetProperty("documents").EnumerateArray().ToList();
        Assert.Equal(doc, Assert.Single(listed).GetProperty("id").GetGuid());
        var whole = await hanna.JsonAsync(await hanna.GetAsync($"/api/wiki/documents/{doc}"));
        Assert.Equal("HR files", whole.GetProperty("source").GetString());
        Assert.Contains(whole.GetProperty("passages").EnumerateArray(), p => p.GetProperty("text").GetString()!.Contains("Tell your manager by nine.", StringComparison.Ordinal));

        // Ivan, not in HR: none of it, and the document is not there for him.
        Assert.DoesNotContain((await ivan.JsonAsync(await ivan.GetAsync("/api/wiki"))).GetProperty("sources").EnumerateArray(), s => s.GetProperty("id").GetGuid() == id);
        Assert.Empty((await ivan.JsonAsync(await ivan.GetAsync("/api/wiki/search?q=paid%20leave&in=knowledge"))).GetProperty("knowledge").EnumerateArray());
        Assert.Empty((await ivan.JsonAsync(await ivan.GetAsync($"/api/wiki/documents?source={id}"))).GetProperty("documents").EnumerateArray());
        await StatusAssert.Is(HttpStatusCode.NotFound, await ivan.GetAsync($"/api/wiki/documents/{doc}"));

        // Nothing to search for is refused.
        await StatusAssert.Is(HttpStatusCode.BadRequest, await hanna.GetAsync("/api/wiki/search?q=%20"));
    }

    [Theory]
    [InlineData("The office opens at eight and closes at six.", "closes", "The office opens at eight and closes at six.")]
    [InlineData("word", "missing", "word")]
    public void A_snippet_is_the_passage_around_the_words(string text, string words, string snippet) => Assert.Equal(snippet, WikiEndpoints.Snippet(text, words));
}
