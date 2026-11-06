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

    [Fact]
    [Trait("Control", "IAM-05")]
    public async Task The_code_is_searched_as_the_person_a_page_at_a_time_with_their_filters_and_meaning_only_beside_a_short_answer()
    {
        var name = "wikicode" + Guid.NewGuid().ToString("N")[..4];
        var (me, _) = await KnowledgeTests.PersonAsync(app.Factory, await KnowledgeTests.AdminAsync(app.Factory), name);
        const string Row = """{"kind":"symbol","how":"name and scope","score":102,"repo_id":4,"repo":"team/mylib","branch":"main","path":"src/mylib.cpp","lang":"cpp","line":3,"name":"TestFunc","symbol_kind":"function","scope":"MyLib","snippet":"TestFunc(int a)"}""";
        var total = 45;
        app.Argus.SearchAnswer = query => $$$"""
            {"query":"MyLib::TestFunc","page":2,"per_page":20,"total":{{{total}}},"reach":2000,"rows":[{{{Row}}}],
             "facets":{"repos":[{"repo":"team/mylib","n":45}],"langs":[{"lang":"cpp","n":45}],"kinds":{"symbol":3,"code":40,"doc":2}},"branch":null,"branches":["main","v2"]}
            """;
        app.Argus.Handlers["semantic_search"] = _ => """{"result":[{"repo_id":4,"path_with_namespace":"team/mylib","path":"src/near.cpp","line":9,"name":"AddOne","kind":"function","score":0.81},{"repo_id":4,"path_with_namespace":"team/mylib","path":"src/far.cpp","line":1,"name":"Random","kind":"function","score":0.21}]}""";
        try
        {
            var found = await me.JsonAsync(await me.GetAsync("/api/wiki/search?q=MyLib%3A%3ATestFunc&in=code&repo=team%2Fmylib&lang=cpp&kind=symbol&path=src%2F&branch=v2&page=2"));
            var (email, query) = app.Argus.Searches.Last();
            Assert.Equal($"{name}@example.test", email);
            foreach (var part in new[] { "q=MyLib%3A%3ATestFunc", "repo=team%2Fmylib", "lang=cpp", "kind=symbol", "path=src%2F", "branch=v2", "page=2", "per_page=20" })
            {
                Assert.Contains(part, query, StringComparison.Ordinal);
            }
            var hit = Assert.Single(found.GetProperty("code").EnumerateArray());
            Assert.Equal(("symbol", "TestFunc", "MyLib", 3), (hit.GetProperty("kind").GetString(), hit.GetProperty("name").GetString(), hit.GetProperty("scope").GetString(), hit.GetProperty("line").GetInt32()));
            Assert.Equal(45, found.GetProperty("codePage").GetProperty("total").GetInt32());
            Assert.Equal(2, found.GetProperty("codePage").GetProperty("branches").GetArrayLength());
            // A long answer: nothing by meaning beside it.
            Assert.Empty(found.GetProperty("meaning").EnumerateArray());

            // A short answer on its first page: what is near in meaning beside it, never what is far.
            total = 1;
            var short_ = await me.JsonAsync(await me.GetAsync("/api/wiki/search?q=add%20one&in=code"));
            Assert.Equal("AddOne", Assert.Single(short_.GetProperty("meaning").EnumerateArray()).GetProperty("name").GetString());

            // Argus refusing the search is said, not a failure of the rest.
            app.Argus.SearchAnswer = null;
            var refused = await me.JsonAsync(await me.GetAsync("/api/wiki/search?q=x&in=code&branch=nope"));
            Assert.Contains(refused.GetProperty("problems").EnumerateArray(), p => p.GetString()!.Contains("That branch is not indexed.", StringComparison.Ordinal));
        }
        finally
        {
            app.Argus.SearchAnswer = null;
            app.Argus.Handlers.TryRemove("semantic_search", out _);
        }
    }

    [Theory]
    [InlineData("The office opens at eight and closes at six.", "closes", "The office opens at eight and closes at six.")]
    [InlineData("word", "missing", "word")]
    public void A_snippet_is_the_passage_around_the_words(string text, string words, string snippet) => Assert.Equal(snippet, WikiEndpoints.Snippet(text, words));
}
