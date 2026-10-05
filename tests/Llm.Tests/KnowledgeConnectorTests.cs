using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Llm.Api.Knowledge;
using Llm.Core.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Llm.Tests.KnowledgeTests;

namespace Llm.Tests;

/// <summary>
/// Confluence and SharePoint as company knowledge, against a fake Confluence (Data Center and Cloud) and a fake Microsoft Graph:
/// each person finds only what Confluence or SharePoint lets them read (restrictions on a page, a space whose permissions are
/// hidden, a SharePoint group falling back to whom the admin chose), only what changed is read again (versions, Graph's delta
/// links), what is gone goes, a sync that fails half way keeps what it had, and the secrets are never sent back.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class KnowledgeConnectorTests(AppFixture app)
{
    private WebApplicationFactory<Program> NewApp(FakeConfluence? confluence = null, FakeGraph? graph = null) =>
        app.Create(app.ConnectionStringFor("knowledge_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(),
            new Dictionary<string, string?> { ["Auth:DataKey"] = "a-data-key-for-knowledge-tests", ["Modules:embed"] = "true" },
            s =>
            {
                s.AddHttpClient(ConfluenceConnector.Client).ConfigurePrimaryHttpMessageHandler(() => confluence ?? new FakeConfluence());
                s.AddHttpClient(SharePointConnector.Client).ConfigurePrimaryHttpMessageHandler(() => graph ?? new FakeGraph());
            });

    private Task<JsonElement> SearchAsync(TestBrowser b, string query) => KnowledgeTests.SearchAsync(app, b, query);

    private async Task<List<string>> FindsAsync(TestBrowser b, string query) => Links(await SearchAsync(b, query));

    private static async Task<Guid> GroupAsync(TestBrowser admin, string name, params Guid[] members)
    {
        var id = (await admin.JsonAsync(await admin.PostAsync("/api/admin/groups", new { name }))).GetProperty("id").GetGuid();
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.PostAsync($"/api/admin/groups/{id}/members", new { userIds = members }));
        return id;
    }

    private static Task<HttpResponseMessage> PatchAsync(TestBrowser admin, Guid id, object body) =>
        admin.Http.PatchAsJsonAsync(new Uri($"/api/admin/knowledge/{id}", UriKind.Relative), body);

    [Fact]
    public async Task A_Confluence_space_answers_only_whom_Confluence_lets_view_it_with_the_passage_and_its_link()
    {
        var confluence = new FakeConfluence();
        await using var f = NewApp(confluence);
        var admin = await AdminAsync(f);
        var (alice, _) = await PersonAsync(f, admin, "alice");
        var (dave, _) = await PersonAsync(f, admin, "dave");
        var (carol, _) = await PersonAsync(f, admin, "carol");
        var (bob, _) = await PersonAsync(f, admin, "bob");

        // Data Center, every space the account may read, with a personal access token.
        var (id, source) = await AddAsync(admin, new { name = "Wiki", kind = "confluence", location = "https://confluence.test", secret = FakeConfluence.Token });
        Assert.Equal("synced", source.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, source.GetProperty("error").ValueKind);
        // The pages of both spaces; no blog post unless asked for.
        Assert.Equal(5, source.GetProperty("documents").GetInt32());
        var viewers = source.GetProperty("viewers").EnumerateArray().ToDictionary(v => v.GetProperty("name").GetString()!, v => v.GetProperty("people").GetInt32());
        Assert.Equal(new Dictionary<string, int> { ["ENG · Engineering"] = 3, ["HR · People team"] = 2 }, viewers);
        Assert.Contains("ENG: mirrored from Confluence, 3 people.", source.GetProperty("mirror").GetString(), StringComparison.Ordinal);
        Assert.All(confluence.Calls, c => Assert.Equal("Bearer " + FakeConfluence.Token, c.Authorization));

        // A viewer gets the passage with its link and section, its code block kept.
        var found = await SearchAsync(alice, "how do I roll back a deploy");
        var first = found.GetProperty("results")[0];
        Assert.Equal("https://confluence.test/spaces/ENG/pages/101/Deploy", first.GetProperty("link").GetString());
        Assert.Equal("Deploy (Engineering)", first.GetProperty("title").GetString());
        Assert.Equal("Deploy › Rollback", first.GetProperty("section").GetString());
        Assert.Equal("Wiki", first.GetProperty("source").GetString());
        Assert.Contains("make rollback TAG=v1 && echo <done>", first.GetProperty("passage").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("bash", first.GetProperty("passage").GetString(), StringComparison.Ordinal);
        // A member of a group that may view the space too.
        Assert.Contains("https://confluence.test/spaces/ENG/pages/101/Deploy", await FindsAsync(dave, "roll back a deploy"));

        // A page restricted to the group leads, and the page under it: only alice, though dave may view the space.
        Assert.Contains("https://confluence.test/spaces/ENG/pages/103/Leads", await FindsAsync(alice, "reorganisation of the codec team"));
        Assert.Contains("https://confluence.test/spaces/ENG/pages/104/Leads%20budget", await FindsAsync(alice, "hiring budget codec team"));
        Assert.DoesNotContain(await FindsAsync(dave, "reorganisation of the codec team"), l => l.Contains("/103/", StringComparison.Ordinal));
        Assert.DoesNotContain(await FindsAsync(dave, "hiring budget codec team"), l => l.Contains("/104/", StringComparison.Ordinal));

        // Someone who may not view the space, and an admin who is not in Confluence: nothing of it.
        foreach (var outsider in new[] { carol, admin })
        {
            var none = await SearchAsync(outsider, "how do I roll back a deploy");
            Assert.Empty(none.GetProperty("results").EnumerateArray());
        }
        // bob views HR, and only HR.
        Assert.Equal(["https://confluence.test/spaces/HR/pages/201/Leave%20policy"], await FindsAsync(bob, "how many days of paid leave"));
        Assert.DoesNotContain(await FindsAsync(bob, "roll back a deploy"), l => l.Contains("/ENG/", StringComparison.Ordinal));

        // Blog posts, when asked for.
        await StatusAssert.Is(HttpStatusCode.NoContent, await PatchAsync(admin, id, new { blogPosts = true }));
        var more = await SyncedAsync(admin, id, source.GetProperty("syncedAt").GetDateTimeOffset());
        Assert.Equal(6, more.GetProperty("documents").GetInt32());
        var blog = await SearchAsync(dave, "version nine faster decoder");
        Assert.Equal("Release notes (Engineering blog)", blog.GetProperty("results")[0].GetProperty("title").GetString());
    }

    [Fact]
    public async Task A_Confluence_sync_reads_only_what_changed_drops_what_is_gone_and_keeps_all_when_it_fails_half_way()
    {
        var confluence = new FakeConfluence();
        await using var f = NewApp(confluence);
        var admin = await AdminAsync(f);
        var (alice, _) = await PersonAsync(f, admin, "alice");
        var (dave, _) = await PersonAsync(f, admin, "dave");
        var (id, first) = await AddAsync(admin, new { name = "Engineering", kind = "confluence", location = "https://confluence.test/", spaces = "ENG", secret = FakeConfluence.Token });
        Assert.Equal(4, first.GetProperty("documents").GetInt32());

        // Nothing changed: nothing is embedded again, and nothing goes.
        var embedded = app.Embedder.Texts;
        var again = await SyncAgainAsync(admin, id);
        Assert.Equal(4, again.GetProperty("documents").GetInt32());
        Assert.Equal(embedded, app.Embedder.Texts);

        // One page edited (a new version), one deleted: only the edited one is read and embedded again, the deleted one goes.
        var deploy = confluence.Contents.Single(c => c.Id == "101");
        (deploy.Body, deploy.Version) = ("<h2>Rollback</h2><p>To roll back a deploy, run make undo-release with the release tag.</p>", 2);
        confluence.Contents.RemoveAll(c => c.Id == "102");
        var reads = confluence.Calls.Count(c => c.PathAndQuery.Contains("expand=body.storage", StringComparison.Ordinal));
        var changed = await SyncAgainAsync(admin, id);
        Assert.Equal(3, changed.GetProperty("documents").GetInt32());
        Assert.Equal(reads + 1, confluence.Calls.Count(c => c.PathAndQuery.Contains("expand=body.storage", StringComparison.Ordinal)));
        Assert.True(app.Embedder.Texts - embedded is > 0 and <= 2, $"embedded {app.Embedder.Texts - embedded} passages again");
        Assert.Contains("undo-release", (await SearchAsync(alice, "roll back a deploy")).GetProperty("results")[0].GetProperty("passage").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain(await FindsAsync(alice, "laptop and accounts on the first day"), l => l.Contains("/102/", StringComparison.Ordinal));

        // A restriction put on one page (its version unchanged): at the next sync only whom it names read it, with nothing embedded again.
        deploy.RestrictUsers.Add("alice");
        embedded = app.Embedder.Texts;
        await SyncAgainAsync(admin, id);
        Assert.Equal(embedded, app.Embedder.Texts);
        Assert.Contains("https://confluence.test/spaces/ENG/pages/101/Deploy", await FindsAsync(alice, "roll back a deploy"));
        Assert.DoesNotContain("https://confluence.test/spaces/ENG/pages/101/Deploy", await FindsAsync(dave, "roll back a deploy"));

        // The listing fails on its second page: the sync says so and keeps every document as it was.
        confluence.FailSecondPage = true;
        var half = await SyncAgainAsync(admin, id);
        Assert.Equal("synced", half.GetProperty("state").GetString());
        Assert.Contains("ENG: its pages could not be listed", half.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Equal(3, half.GetProperty("documents").GetInt32());
        Assert.Contains("https://confluence.test/spaces/ENG/pages/101/Deploy", await FindsAsync(alice, "roll back a deploy"));
        confluence.FailSecondPage = false;

        // A token Confluence refuses: the sync fails, says why, and keeps what it had.
        await StatusAssert.Is(HttpStatusCode.NoContent, await PatchAsync(admin, id, new { secret = "not-the-token" }));
        var failed = await SyncedAsync(admin, id, half.GetProperty("syncedAt").GetDateTimeOffset());
        Assert.Equal("failed", failed.GetProperty("state").GetString());
        Assert.Contains("401", failed.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Equal(3, failed.GetProperty("documents").GetInt32());
    }

    [Fact]
    public async Task Confluence_Cloud_is_read_with_an_email_and_token_and_what_it_cannot_tell_goes_to_the_groups_chosen()
    {
        var confluence = new FakeConfluence();
        // In ENG, someone whose email Cloud hides; HR's permissions are not shown to the account.
        confluence.Spaces.Single(s => s.Key == "ENG").Users.Add("hidden");
        confluence.Spaces.Single(s => s.Key == "HR").PermissionsShown = false;
        await using var f = NewApp(confluence);
        var admin = await AdminAsync(f);
        var (alice, _) = await PersonAsync(f, admin, "alice");
        var (bob, _) = await PersonAsync(f, admin, "bob");
        var (hanna, hannaId) = await PersonAsync(f, admin, "hanna");
        var hr = await GroupAsync(admin, "HR", hannaId);

        var (id, source) = await AddAsync(admin, new
        {
            name = "Cloud wiki", kind = "confluence", location = "https://acme.atlassian.net", account = FakeConfluence.CloudEmail, secret = FakeConfluence.CloudToken,
            spaces = "ENG, HR", audience = "Groups", groups = new[] { hr },
        });
        Assert.Equal("synced", source.GetProperty("state").GetString());
        var mirror = source.GetProperty("mirror").GetString()!;
        Assert.Contains("ENG: mirrored from Confluence, 3 people; 1 person could not be matched", mirror, StringComparison.Ordinal);
        Assert.Contains("HR: Confluence does not show the account who may view this space, so the people you chose read it.", mirror, StringComparison.Ordinal);
        Assert.All(confluence.Calls, c => Assert.StartsWith("/wiki/rest/api/", c.PathAndQuery, StringComparison.Ordinal));
        Assert.All(confluence.Calls, c => Assert.StartsWith("Basic ", c.Authorization, StringComparison.Ordinal));

        // Matched by email: alice reads ENG, with Cloud's link.
        Assert.Contains("https://acme.atlassian.net/wiki/spaces/ENG/pages/101/Deploy", await FindsAsync(alice, "roll back a deploy"));
        // The groups chosen read HR (whose viewers cannot be told) and ENG (someone in it cannot be matched), never a restricted page.
        Assert.Equal("https://acme.atlassian.net/wiki/spaces/HR/pages/201/Leave%20policy", (await FindsAsync(hanna, "how many days of paid leave"))[0]);
        Assert.Contains("https://acme.atlassian.net/wiki/spaces/ENG/pages/101/Deploy", await FindsAsync(hanna, "roll back a deploy"));
        Assert.DoesNotContain(await FindsAsync(hanna, "reorganisation of the codec team"), l => l.Contains("/103/", StringComparison.Ordinal));
        // bob may view HR in Confluence, but the account cannot see that: he is not in the groups chosen.
        Assert.Empty((await SearchAsync(bob, "how many days of paid leave")).GetProperty("results").EnumerateArray());

        // The groups chosen changed: at once, without a sync.
        await StatusAssert.Is(HttpStatusCode.NoContent, await PatchAsync(admin, id, new { audience = "Admins" }));
        Assert.Empty((await SearchAsync(hanna, "how many days of paid leave")).GetProperty("results").EnumerateArray());
        Assert.Contains("https://acme.atlassian.net/wiki/spaces/HR/pages/201/Leave%20policy", await FindsAsync(admin, "how many days of paid leave"));
    }

    [Fact]
    public async Task A_SharePoint_site_answers_only_whom_Graph_says_may_read_each_file()
    {
        var graph = new FakeGraph();
        await using var f = NewApp(graph: graph);
        var admin = await AdminAsync(f);
        var (alice, _) = await PersonAsync(f, admin, "alice");
        var (bob, _) = await PersonAsync(f, admin, "bob");
        var (erin, erinId) = await PersonAsync(f, admin, "erin");
        var (ivan, _) = await PersonAsync(f, admin, "ivan");
        var (hanna, hannaId) = await PersonAsync(f, admin, "hanna");
        var hr = await GroupAsync(admin, "HR", hannaId);
        // erin is in the directory group Engineering (as the company sign-in or LDAP put her there).
        await using (var scope = f.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var user = await db.Users.SingleAsync(u => u.Id == erinId);
            user.DirectoryGroups = ["CN=Engineering,OU=Groups,DC=contoso,DC=com"];
            await db.SaveChangesAsync();
        }

        var (_, source) = await AddAsync(admin, new
        {
            name = "Engineering site", kind = "sharepoint", tenant = FakeGraph.Tenant, account = FakeGraph.ClientId, secret = FakeGraph.Secret,
            location = "https://contoso.sharepoint.com/sites/eng", audience = "Groups", groups = new[] { hr },
        });
        Assert.Equal("synced", source.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, source.GetProperty("error").ValueKind);
        // Five files of the two libraries (not the picture) and the site page.
        Assert.Equal(6, source.GetProperty("documents").GetInt32());
        var mirror = source.GetProperty("mirror").GetString()!;
        Assert.Contains("Engineering · Documents, Engineering · Specs: who may read each file is mirrored from SharePoint.", mirror, StringComparison.Ordinal);
        Assert.Contains("1 file is shared with SharePoint groups", mirror, StringComparison.Ordinal);
        Assert.Contains("Engineering: Graph does not give its pages' permissions, so the people you chose read them.", mirror, StringComparison.Ordinal);

        // Shared with alice by email: the passage and its link.
        var found = await SearchAsync(alice, "roll back a SharePoint deploy");
        var first = found.GetProperty("results")[0];
        Assert.Equal("https://contoso.sharepoint.com/sites/eng/Shared%20Documents/deploy.md", first.GetProperty("link").GetString());
        Assert.Equal("deploy.md (Engineering)", first.GetProperty("title").GetString());
        Assert.Contains("undo pipeline", first.GetProperty("passage").GetString(), StringComparison.Ordinal);
        // Shared with the Entra group Engineering: erin, by her directory group's name.
        Assert.Contains("https://contoso.sharepoint.com/sites/eng/Shared%20Documents/deploy.md", await FindsAsync(erin, "roll back a SharePoint deploy"));
        Assert.Contains("https://contoso.sharepoint.com/sites/eng/Specs/api.md", await FindsAsync(erin, "ingest API protobuf frames port"));
        // Not shared with ivan: nothing of it; but the handbook, which everyone in the company may read, he finds.
        Assert.DoesNotContain("https://contoso.sharepoint.com/sites/eng/Shared%20Documents/deploy.md", await FindsAsync(ivan, "roll back a SharePoint deploy"));
        Assert.Equal(["https://contoso.sharepoint.com/sites/eng/Shared%20Documents/handbook.md"], await FindsAsync(ivan, "office closes Fridays"));
        // Shared with bob by his Entra ID: his email from Graph.
        Assert.Contains("https://contoso.sharepoint.com/sites/eng/Shared%20Documents/salaries.txt", await FindsAsync(bob, "salaries reviewed by the board"));
        Assert.DoesNotContain("https://contoso.sharepoint.com/sites/eng/Shared%20Documents/salaries.txt", await FindsAsync(alice, "salaries reviewed by the board"));
        // The site's Members group and the site page: the groups chosen.
        Assert.Contains("https://contoso.sharepoint.com/sites/eng/Shared%20Documents/team.md", await FindsAsync(hanna, "codec team meets Thursday blue room"));
        Assert.Contains("https://contoso.sharepoint.com/sites/eng/SitePages/Welcome.aspx", await FindsAsync(hanna, "front page on-call rota"));
        Assert.DoesNotContain("https://contoso.sharepoint.com/sites/eng/Shared%20Documents/team.md", await FindsAsync(ivan, "codec team meets Thursday blue room"));

        // A library by its address, and a site the app may not read: that library only, and the sync says why the other is not read.
        var (_, specs) = await AddAsync(admin, new
        {
            name = "Specs", kind = "sharepoint", tenant = FakeGraph.Tenant, account = FakeGraph.ClientId, secret = FakeGraph.Secret,
            location = "https://contoso.sharepoint.com/sites/eng/Specs/Forms/AllItems.aspx\nhttps://contoso.sharepoint.com/sites/hr",
        });
        Assert.Equal(1, specs.GetProperty("documents").GetInt32());
        Assert.Contains("https://contoso.sharepoint.com/sites/hr: the app may not read it (403)", specs.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_SharePoint_library_is_read_again_by_Graphs_delta_links()
    {
        var graph = new FakeGraph();
        await using var f = NewApp(graph: graph);
        var admin = await AdminAsync(f);
        var (bob, _) = await PersonAsync(f, admin, "bob");
        var (ivan, _) = await PersonAsync(f, admin, "ivan");
        var (alice, _) = await PersonAsync(f, admin, "alice");
        var (id, first) = await AddAsync(admin, new
        {
            name = "Documents", kind = "sharepoint", tenant = FakeGraph.Tenant, account = FakeGraph.ClientId, secret = FakeGraph.Secret,
            location = "https://contoso.sharepoint.com/sites/eng/Shared%20Documents", sitePages = false,
        });
        Assert.Equal(4, first.GetProperty("documents").GetInt32());
        // Read whole at first, asking for sharing changes too.
        List<(string PathAndQuery, string? Prefer)> Deltas() => [.. graph.Calls.Where(c => c.PathAndQuery.StartsWith("/v1.0/drives/docs/root/delta", StringComparison.Ordinal)).Select(c => (c.PathAndQuery, c.Prefer))];
        Assert.All(Deltas(), d => Assert.Equal("deltashowsharingchanges", d.Prefer));
        Assert.DoesNotContain(Deltas(), d => d.PathAndQuery.Contains("token=", StringComparison.Ordinal));

        // Nothing changed: the delta link lists nothing, nothing is read or embedded again.
        var embedded = app.Embedder.Texts;
        var calls = Deltas().Count;
        var again = await SyncAgainAsync(admin, id);
        Assert.Equal(4, again.GetProperty("documents").GetInt32());
        Assert.Equal(embedded, app.Embedder.Texts);
        Assert.Contains("token=", Deltas()[calls].PathAndQuery, StringComparison.Ordinal);
        var downloads = graph.Calls.Count(c => c.PathAndQuery.EndsWith("/content", StringComparison.Ordinal));

        // A file edited: only it is read and embedded again. A file deleted: it goes.
        graph.Edit("f1", "# Deploy\n\n## Rollback\n\nTo roll back a SharePoint deploy, run revert-all with the release tag.");
        graph.Delete("f3");
        var changed = await SyncAgainAsync(admin, id);
        Assert.Equal(3, changed.GetProperty("documents").GetInt32());
        Assert.Equal(downloads + 1, graph.Calls.Count(c => c.PathAndQuery.EndsWith("/content", StringComparison.Ordinal)));
        Assert.True(app.Embedder.Texts - embedded is > 0 and <= 2, $"embedded {app.Embedder.Texts - embedded} passages again");
        Assert.Contains("revert-all", (await SearchAsync(alice, "roll back a SharePoint deploy")).GetProperty("results")[0].GetProperty("passage").GetString(), StringComparison.Ordinal);

        // A file's sharing changed (not its text): who may read it follows, with nothing embedded again.
        embedded = app.Embedder.Texts;
        graph.Share("f2", FakeGraph.User("ivan@example.test"));
        await SyncAgainAsync(admin, id);
        Assert.Equal(embedded, app.Embedder.Texts);
        Assert.Contains("https://contoso.sharepoint.com/sites/eng/Shared%20Documents/salaries.txt", await FindsAsync(ivan, "salaries reviewed by the board"));
        Assert.DoesNotContain("https://contoso.sharepoint.com/sites/eng/Shared%20Documents/salaries.txt", await FindsAsync(bob, "salaries reviewed by the board"));

        // The changes fail half way: what was read stays, nothing goes, and the next sync reads the same changes again from where it was.
        graph.Edit("f1", "# Deploy\n\n## Rollback\n\nTo roll back a SharePoint deploy, run rewind-now with the release tag.");
        graph.Edit("f2", "Salaries of the codec team are reviewed every June by the board.");
        graph.Edit("f4", "# Handbook\n\nThe office closes at six on Fridays.");
        graph.FailSecondPage = true;
        calls = Deltas().Count;
        var half = await SyncAgainAsync(admin, id);
        Assert.Equal("synced", half.GetProperty("state").GetString());
        Assert.Contains("Engineering · Documents: its files could not be listed", half.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Equal(3, half.GetProperty("documents").GetInt32());
        var token = Deltas()[calls].PathAndQuery;
        graph.FailSecondPage = false;
        calls = Deltas().Count;
        var whole = await SyncAgainAsync(admin, id);
        Assert.Equal(JsonValueKind.Null, whole.GetProperty("error").ValueKind);
        Assert.Equal(token, Deltas()[calls].PathAndQuery);
        var office = (await SearchAsync(ivan, "office closes Fridays")).GetProperty("results")[0];
        Assert.Equal("https://contoso.sharepoint.com/sites/eng/Shared%20Documents/handbook.md", office.GetProperty("link").GetString());
        Assert.Contains("six", office.GetProperty("passage").GetString(), StringComparison.Ordinal);
        Assert.Contains("rewind-now", (await SearchAsync(alice, "roll back a SharePoint deploy")).GetProperty("results")[0].GetProperty("passage").GetString(), StringComparison.Ordinal);

        // A delta link Graph no longer knows (410): the library is read whole again.
        graph.Expired = true;
        calls = Deltas().Count;
        var resynced = await SyncAgainAsync(admin, id);
        Assert.Equal("synced", resynced.GetProperty("state").GetString());
        Assert.Equal(3, resynced.GetProperty("documents").GetInt32());
        Assert.Contains(Deltas().Skip(calls), d => !d.PathAndQuery.Contains("token=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Test_connection_says_what_it_reached_and_secrets_are_stored_encrypted_and_never_sent_back()
    {
        await using var f = NewApp();
        var admin = await AdminAsync(f);

        // Confluence: who the account is and what it may read; a wrong token says why, never the token.
        var ok = await admin.PostAsync("/api/admin/knowledge/test", new { kind = "confluence", location = "https://confluence.test", secret = FakeConfluence.Token });
        await StatusAssert.Is(HttpStatusCode.OK, ok);
        var said = (await admin.JsonAsync(ok)).GetProperty("message").GetString()!;
        Assert.Contains("Signed in to Confluence Data Center as svc-bot", said, StringComparison.Ordinal);
        Assert.Contains("It may read 2 spaces: ENG, HR", said, StringComparison.Ordinal);
        Assert.Contains("Who may view ENG is mirrored: 3 people", said, StringComparison.Ordinal);
        var refused = await admin.PostAsync("/api/admin/knowledge/test", new { kind = "confluence", location = "https://confluence.test", secret = "wrong-token-value" });
        await StatusAssert.Is(HttpStatusCode.BadRequest, refused);
        var why = await refused.Content.ReadAsStringAsync();
        Assert.Contains("401", why, StringComparison.Ordinal);
        Assert.DoesNotContain("wrong-token-value", why, StringComparison.Ordinal);
        // Missing the token: the form says which field.
        var missing = await admin.PostAsync("/api/admin/knowledge", new { name = "No token", kind = "confluence", location = "https://confluence.test" });
        await StatusAssert.Is(HttpStatusCode.BadRequest, missing);
        Assert.Equal("secret", (await admin.JsonAsync(missing)).GetProperty("status").GetString());

        // SharePoint: the site and its libraries; a wrong secret gets Entra's reason, never the secret.
        var graph = await admin.PostAsync("/api/admin/knowledge/test", new
        {
            kind = "sharepoint", tenant = FakeGraph.Tenant, account = FakeGraph.ClientId, secret = FakeGraph.Secret, location = "https://contoso.sharepoint.com/sites/eng",
        });
        await StatusAssert.Is(HttpStatusCode.OK, graph);
        Assert.Contains("Engineering: 2 libraries (Documents, Specs)", (await admin.JsonAsync(graph)).GetProperty("message").GetString(), StringComparison.Ordinal);
        var entra = await admin.PostAsync("/api/admin/knowledge/test", new
        {
            kind = "sharepoint", tenant = FakeGraph.Tenant, account = FakeGraph.ClientId, secret = "not-the-graph-secret", location = "https://contoso.sharepoint.com/sites/eng",
        });
        await StatusAssert.Is(HttpStatusCode.BadRequest, entra);
        var entraWhy = await entra.Content.ReadAsStringAsync();
        Assert.Contains("AADSTS7000215", entraWhy, StringComparison.Ordinal);
        Assert.DoesNotContain("not-the-graph-secret", entraWhy, StringComparison.Ordinal);
        Assert.DoesNotContain("Trace ID", entraWhy, StringComparison.Ordinal);

        // Saved: encrypted in the database, never in the page or the audit log.
        var (wiki, _) = await AddAsync(admin, new { name = "Wiki", kind = "confluence", location = "https://confluence.test", secret = FakeConfluence.Token });
        var (site, _) = await AddAsync(admin, new
        {
            name = "Site", kind = "sharepoint", tenant = FakeGraph.Tenant, account = FakeGraph.ClientId, secret = FakeGraph.Secret, location = "https://contoso.sharepoint.com/sites/eng",
        });
        var page = await (await admin.GetAsync("/api/admin/knowledge")).Content.ReadAsStringAsync();
        var audit = await (await admin.GetAsync("/api/admin/audit")).Content.ReadAsStringAsync();
        foreach (var secret in new[] { FakeConfluence.Token, FakeGraph.Secret })
        {
            Assert.DoesNotContain(secret, page, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, audit, StringComparison.Ordinal);
        }
        Assert.All(JsonNode.Parse(page)!["sources"]!.AsArray(), s => Assert.True(s!["secretSet"]!.GetValue<bool>()));
        await using (var scope = f.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            foreach (var stored in await db.KnowledgeSources.Select(s => s.SecretEncrypted!).ToListAsync())
            {
                Assert.StartsWith("enc:", stored, StringComparison.Ordinal);
                Assert.DoesNotContain(FakeConfluence.Token, stored, StringComparison.Ordinal);
                Assert.DoesNotContain(FakeGraph.Secret, stored, StringComparison.Ordinal);
            }
        }

        // A saved source is tested with its own secret; a change without one keeps it.
        await StatusAssert.Is(HttpStatusCode.OK, await admin.PostAsync($"/api/admin/knowledge/{wiki}/test", new { }));
        await StatusAssert.Is(HttpStatusCode.OK, await admin.PostAsync($"/api/admin/knowledge/{site}/test", new { secret = "" }));
        await StatusAssert.Is(HttpStatusCode.NoContent, await PatchAsync(admin, wiki, new { name = "Company wiki", secret = "" }));
        var resynced = await SyncAgainAsync(admin, wiki);
        Assert.Equal("synced", resynced.GetProperty("state").GetString());
        Assert.Equal("Company wiki", resynced.GetProperty("name").GetString());
    }
}
