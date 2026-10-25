using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Llm.Tests;

/// <summary>Code Arena's memory is Arena's: a project's memories by the repository's key, the person's for every project, recall by meaning, and its files brought in once.</summary>
[Collection(nameof(AppCollection))]
public sealed class CodeArenaMemoryTests(AppFixture app)
{
    private const string Repo = "gitlab.corp/payments/api";

    private WebApplicationFactory<Program> NewApp() =>
        app.Create(app.ConnectionStringFor("camem_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(),
            new Dictionary<string, string?> { ["Auth:DataKey"] = "a-data-key-for-memory-tests", ["Modules:embed"] = "true" });

    private static async Task<(TestBrowser Browser, string Key)> PersonAsync(WebApplicationFactory<Program> f)
    {
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "c" + Guid.NewGuid().ToString("N")[..10];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        return (await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!), made.GetProperty("apiKey").GetString()!);
    }

    private static HttpClient CodeArena(WebApplicationFactory<Program> f, string? key)
    {
        var http = f.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"https://{AppFixture.Domain}"), AllowAutoRedirect = false, HandleCookies = false });
        if (key is not null)
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }
        http.DefaultRequestHeaders.Add("X-Requested-With", "code-arena");
        return http;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage res)
    {
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}");
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    [Fact]
    public async Task A_projects_memories_are_its_own_the_persons_go_everywhere_and_recall_finds_what_matters()
    {
        await using var f = NewApp();
        var (web, key) = await PersonAsync(f);
        var arena = CodeArena(f, key);
        await StatusAssert.Is(HttpStatusCode.Unauthorized, await CodeArena(f, null).GetAsync(new Uri("/api/code-arena/memories", UriKind.Relative)));

        var build = await Json(await arena.PostAsJsonAsync("/api/code-arena/memories", new { text = "Build and test with tools/dn, never dotnet directly.", scope = "project", kind = "howto", project = Repo }));
        Assert.Equal(("project", Repo, "howto"), (build.GetProperty("scope").GetString(), build.GetProperty("project").GetString(), build.GetProperty("kind").GetString()));
        await Json(await arena.PostAsJsonAsync("/api/code-arena/memories", new { text = "Prefers small commits with clear messages.", kind = "preference" }));
        await Json(await arena.PostAsJsonAsync("/api/code-arena/memories", new { text = "The docs site deploys with Netlify.", scope = "project", project = "gitlab.corp/docs/site" }));
        // A project's without its project, or the company's from Code Arena: refused / the person's.
        await StatusAssert.Is(HttpStatusCode.BadRequest, await arena.PostAsJsonAsync("/api/code-arena/memories", new { text = "x", scope = "project" }));

        var here = await Json(await arena.GetAsync(new Uri($"/api/code-arena/memories?project={Uri.EscapeDataString(Repo)}", UriKind.Relative)));
        var texts = here.GetProperty("memories").EnumerateArray().Select(m => m.GetProperty("text").GetString()).ToList();
        Assert.Contains("Build and test with tools/dn, never dotnet directly.", texts);
        Assert.Contains("Prefers small commits with clear messages.", texts);
        Assert.DoesNotContain("The docs site deploys with Netlify.", texts);
        Assert.Contains("Build and test with tools/dn, never dotnet directly. (how to, this project's)", here.GetProperty("note").GetString(), StringComparison.Ordinal);
        Assert.Equal(2, here.GetProperty("inNote").GetArrayLength());

        // Recall: what matters to the question, and not what was given already.
        var found = await Json(await arena.PostAsJsonAsync("/api/code-arena/memories/recall", new { query = "how do I build and test this?", project = Repo }));
        Assert.Equal("Build and test with tools/dn, never dotnet directly.", found.GetProperty("memories")[0].GetProperty("text").GetString());
        Assert.StartsWith(Llm.Api.Chat.Memories.RecallHeading, found.GetProperty("note").GetString(), StringComparison.Ordinal);
        var again = await Json(await arena.PostAsJsonAsync("/api/code-arena/memories/recall", new { query = "how do I build and test this?", project = Repo, besides = new[] { build.GetProperty("id").GetGuid() } }));
        Assert.DoesNotContain(again.GetProperty("memories").EnumerateArray(), m => m.GetProperty("id").GetGuid() == build.GetProperty("id").GetGuid());

        // The web shows them, the project's with its project.
        var listed = (await web.JsonAsync(await web.GetAsync("/api/account/memories"))).GetProperty("memories").EnumerateArray().ToList();
        Assert.Contains(listed, m => m.GetProperty("project").GetString() == Repo && m.GetProperty("source").GetString() == "code-arena");

        await StatusAssert.Is(HttpStatusCode.NoContent, await arena.DeleteAsync(new Uri($"/api/code-arena/memories/{build.GetProperty("id").GetString()}", UriKind.Relative)));
        await StatusAssert.Is(HttpStatusCode.NotFound, await arena.DeleteAsync(new Uri($"/api/code-arena/memories/{build.GetProperty("id").GetString()}", UriKind.Relative)));
    }

    [Fact]
    public async Task Code_Arenas_memory_files_are_brought_in_once_and_what_is_there_already_is_not_doubled()
    {
        await using var f = NewApp();
        var (_, key) = await PersonAsync(f);
        var arena = CodeArena(f, key);
        var memories = new object[]
        {
            new { text = "Uses fish as the shell." },
            new { text = "Migrations need an upgrade test.", scope = "project", project = Repo },
            new { text = "", scope = "project", project = Repo },
        };
        var first = await Json(await arena.PostAsJsonAsync("/api/code-arena/memories/import", new { memories }));
        Assert.Equal((2, 1), (first.GetProperty("kept").GetInt32(), first.GetProperty("skipped").GetInt32()));
        await Json(await arena.PostAsJsonAsync("/api/code-arena/memories/import", new { memories }));
        var all = await Json(await arena.GetAsync(new Uri($"/api/code-arena/memories?project={Uri.EscapeDataString(Repo)}", UriKind.Relative)));
        Assert.Equal(2, all.GetProperty("memories").GetArrayLength());
    }
}
