using System.Net;
using System.Text.Json;
using Llm.Api;
using Llm.Api.Downloads;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Llm.Tests;

/// <summary>Which Code Arena each person uses: put down from its User-Agent, and an old one told to its person and listed for admins.</summary>
[Collection(nameof(AppCollection))]
public sealed class CodeArenaClientTests(AppFixture app)
{
    [Theory]
    [InlineData("code-arena/5.8.0", "5.8.0")]
    [InlineData("code-arena/7.5.0-rc.1 (linux-x64)", "7.5.0-rc.1")]
    [InlineData("curl/8.5.0", null)]
    [InlineData("code-arena/<script>", null)]
    [InlineData(null, null)]
    public void The_version_is_read_from_code_arenas_user_agent_only(string? agent, string? version) => Assert.Equal(version, CodeArenaClients.VersionOf(agent));

    [Fact]
    public void Versions_compare_as_code_arena_compares_them()
    {
        Assert.True(CodeArenaClients.Newer("7.5.0", "5.8.0"));
        Assert.True(CodeArenaClients.Newer("7.10.0", "7.9.3"));
        Assert.True(CodeArenaClients.Newer("7.5.0", "7.5.0-rc.1"));
        Assert.False(CodeArenaClients.Newer("7.5.0", "7.5.0"));
        Assert.False(CodeArenaClients.Newer("7.5.0", "8.0.0"));
    }

    [Fact]
    public async Task A_person_on_an_old_code_arena_is_told_in_their_bell_and_listed_for_admins_until_they_update()
    {
        var dir = Directory.CreateTempSubdirectory("downloads-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "code-arena", "linux-x64"));
            await File.WriteAllTextAsync(Path.Combine(dir, "code-arena", "linux-x64", "code-arena"), "ELF pretend");
            await using var f = app.Create(app.ConnectionStringFor("caclients_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(),
                new Dictionary<string, string?> { ["Downloads:Directory"] = dir });
            var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
            var name = "ca" + Guid.NewGuid().ToString("N")[..8];
            var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
            var person = await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!);
            var latest = AppInfo.Current.Version;

            // Code Arena asks the gateway for its models with the person's key.
            async Task AskAsync(string version)
            {
                using var client = f.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"https://gateway.{AppFixture.Domain}"), HandleCookies = false });
                using var req = new HttpRequestMessage(HttpMethod.Get, new Uri("/v1/models", UriKind.Relative));
                req.Headers.Authorization = new("Bearer", made.GetProperty("apiKey").GetString()!);
                req.Headers.UserAgent.ParseAdd($"code-arena/{version}");
                await StatusAssert.Is(HttpStatusCode.OK, await client.SendAsync(req));
            }
            async Task<List<JsonElement>> OldAsync() =>
                [.. (await admin.JsonAsync(await admin.GetAsync("/api/admin/overview"))).GetProperty("oldCodeArena").GetProperty("people").EnumerateArray()];

            await AskAsync("5.8.0");
            List<JsonElement> news = [];
            for (var waited = 0; waited < 10_000 && news.Count == 0; waited += 100)
            {
                await Task.Delay(100);
                news = [.. (await person.JsonAsync(await person.GetAsync("/api/notifications"))).GetProperty("items").EnumerateArray()
                    .Where(n => n.GetProperty("title").GetString() == $"Your Code Arena is 5.8.0; this Arena serves {latest}")];
            }
            Assert.Contains("code-arena update", Assert.Single(news).GetProperty("body").GetString(), StringComparison.Ordinal);
            var old = Assert.Single(await OldAsync(), p => p.GetProperty("name").GetString() == name);
            Assert.Equal("5.8.0", old.GetProperty("version").GetString());

            // Once they run the one this Arena serves, they are not listed any more.
            await AskAsync(latest);
            for (var waited = 0; waited < 10_000 && (await OldAsync()).Any(p => p.GetProperty("name").GetString() == name); waited += 100)
            {
                await Task.Delay(100);
            }
            Assert.DoesNotContain(await OldAsync(), p => p.GetProperty("name").GetString() == name);
            // Told once for that version.
            await AskAsync("5.8.0");
            await Task.Delay(300);
            Assert.Single((await person.JsonAsync(await person.GetAsync("/api/notifications"))).GetProperty("items").EnumerateArray(),
                n => n.GetProperty("title").GetString()!.StartsWith("Your Code Arena is 5.8.0", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
