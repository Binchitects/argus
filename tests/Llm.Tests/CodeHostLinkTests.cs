using System.Net;
using System.Net.Http.Json;

namespace Llm.Tests;

/// <summary>A person's own GitHub token: checked through Argus as it is linked, never shown again, and passed to Argus with their requests.</summary>
[Collection(nameof(AppCollection))]
public sealed class CodeHostLinkTests(AppFixture app)
{
    [Fact]
    public async Task A_linked_token_goes_to_Argus_with_the_persons_requests_until_unlinked()
    {
        await using var f = app.Create(app.ConnectionStringFor("codehost_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(),
            new Dictionary<string, string?> { ["Auth:DataKey"] = "a-data-key-for-code-host-tests" });
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var user = "ch" + Guid.NewGuid().ToString("N")[..8];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = user, email = $"{user}@example.test" }));
        var email = $"{user}@example.test";
        var me = await new TestBrowser(f).SignedInAsync(user, made.GetProperty("password").GetString()!);

        await StatusAssert.Is(HttpStatusCode.BadRequest, await me.Http.PutAsJsonAsync(new Uri("/api/account/code-hosts/github", UriKind.Relative), new { token = "ghp-not-this-one" }));
        await StatusAssert.Is(HttpStatusCode.NotFound, await me.Http.PutAsJsonAsync(new Uri("/api/account/code-hosts/bitbucket", UriKind.Relative), new { token = FakeArgus.GoodGitHubToken }));
        var linked = await me.Http.PutAsJsonAsync(new Uri("/api/account/code-hosts/github", UriKind.Relative), new { token = FakeArgus.GoodGitHubToken });
        await StatusAssert.Is(HttpStatusCode.OK, linked);
        Assert.Equal("octocat", (await me.JsonAsync(linked)).GetProperty("account").GetString());
        var list = await me.GetAsync("/api/account/code-hosts");
        var text = await list.Content.ReadAsStringAsync();
        Assert.Contains("\"account\":\"octocat\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeArgus.GoodGitHubToken, text, StringComparison.Ordinal);

        // The person's requests to Argus carry it; nobody else's do.
        await me.GetAsync("/api/code/mrs?project=github:octo/app");
        Assert.Contains(app.Argus.HostTokens, t => t.Email == email && t.GitHub == FakeArgus.GoodGitHubToken);
        Assert.DoesNotContain(app.Argus.HostTokens, t => t.Email != email && t.GitHub is not null);

        await StatusAssert.Is(HttpStatusCode.NoContent, await me.Http.DeleteAsync(new Uri("/api/account/code-hosts/github", UriKind.Relative)));
        app.Argus.HostTokens.Clear();
        await me.GetAsync("/api/code/mrs?project=github:octo/app");
        Assert.All(app.Argus.HostTokens.Where(t => t.Email == email), t => Assert.Null(t.GitHub));
    }
}
