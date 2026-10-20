using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Llm.Api.Plugins;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Llm.Tests;

/// <summary>
/// Plugins that bring more than tools: what they ask for is granted at install; their assistants, rules and toolsets follow
/// their versions; versions are kept to go back to, or pinned; their health; the starter plugin and the check for authors.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class PluginContentTests(AppFixture app)
{
    private WebApplicationFactory<Program> NewApp() =>
        app.Create(app.ConnectionStringFor("pluginx_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(),
            new Dictionary<string, string?> { ["Auth:DataKey"] = "a-data-key-for-plugin-tests" });

    /// <summary>The starter plugin, its files changed: the version, and the rules file's text.</summary>
    private static string Zip(string version, string? rules = null)
    {
        using var source = new ZipArchive(new MemoryStream(PluginTemplate.Zip()));
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var e in source.Entries)
            {
                var text = new StreamReader(e.Open()).ReadToEnd();
                if (e.Name == PluginManifest.FileName)
                {
                    text = text.Replace("version: 0.1.0", $"version: {version}", StringComparison.Ordinal);
                }
                if (e.Name == "rules.yaml" && rules is not null)
                {
                    text = rules;
                }
                using var w = new StreamWriter(zip.CreateEntry(e.FullName).Open(), new UTF8Encoding(false));
                w.Write(text);
            }
        }
        return Convert.ToBase64String(ms.ToArray());
    }

    private static readonly object Settings = new { api_url = "https://pets.test/v1", token = "weather-token" };

    [Fact]
    public async Task A_plugin_asks_before_it_brings_assistants_rules_and_toolsets_and_they_follow_its_versions()
    {
        await using var f = NewApp();
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);

        // The starter plugin downloads and checks clean, saying what it would ask for.
        var template = await admin.GetAsync("/api/admin/plugins/template");
        Assert.Equal("application/zip", template.Content.Headers.ContentType?.MediaType);
        var report = await admin.JsonAsync(await admin.PostAsync("/api/admin/plugins/check", new { zip = Zip("0.1.0") }));
        Assert.True(report.GetProperty("ok").GetBoolean(), report.ToString());
        Assert.Contains(report.GetProperty("permissions").EnumerateArray(), p => p.GetProperty("key").GetString() == "assistants");
        var broken = await admin.JsonAsync(await admin.PostAsync("/api/admin/plugins/check", new { zip = Zip("0.1.0", "rules: [{name: Bad, detector: nothing, stages: [question], action: block}]") }));
        Assert.False(broken.GetProperty("ok").GetBoolean());

        // Without the admin's yes it does not install; with it, everything it brings is there.
        var asked = await admin.PostAsync("/api/admin/plugins/install", new { zip = Zip("0.1.0"), settings = Settings });
        await StatusAssert.Is(HttpStatusCode.Conflict, asked);
        Assert.Equal("accept", (await admin.JsonAsync(asked)).GetProperty("status").GetString());
        var res = await admin.PostAsync("/api/admin/plugins/install", new { accept = true, zip = Zip("0.1.0"), settings = Settings });
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        var made = await admin.JsonAsync(res);
        var id = made.GetProperty("id").GetGuid();
        var toolId = made.GetProperty("toolId").GetString()!;
        var assistants = await admin.JsonAsync(await admin.GetAsync("/api/assistants"));
        Assert.Contains("Forecaster", assistants.ToString(), StringComparison.Ordinal);
        var rules = (await admin.JsonAsync(await admin.GetAsync("/api/admin/safeguards/rules"))).GetProperty("rules").EnumerateArray().Select(r => r.GetProperty("rule")).ToList();
        var storm = rules.Single(r => r.GetProperty("name").GetString() == "Weather desk: Storm chasing");
        Assert.True(storm.GetProperty("monitor").GetBoolean());
        var sets = (await admin.JsonAsync(await admin.GetAsync("/api/admin/tools/toolsets"))).EnumerateArray().ToList();
        Assert.Equal([toolId, "web"], sets.Single(s => s.GetProperty("name").GetString() == "Weather").GetProperty("tools").EnumerateArray().Select(t => t.GetString()));

        // A new version with a rule more: its content follows; the version before is kept to go back to.
        var two = await admin.JsonAsync(await admin.PostAsync($"/api/admin/plugins/{id}/update", new
        {
            zip = Zip("0.2.0", "rules:\n  - {name: Storm chasing, detector: words, stages: [question], action: warn, patterns: storm chasing}\n  - {name: Hail, detector: words, stages: [question], action: log, patterns: hail}\n"),
        }));
        Assert.Equal("0.2.0", two.GetProperty("version").GetString());
        var names = (await admin.JsonAsync(await admin.GetAsync("/api/admin/safeguards/rules"))).GetProperty("rules").EnumerateArray().Select(r => r.GetProperty("rule").GetProperty("name").GetString()).ToList();
        Assert.Contains("Weather desk: Hail", names);
        var installed = (await admin.JsonAsync(await admin.GetAsync("/api/admin/plugins"))).GetProperty("installed").EnumerateArray().Single();
        Assert.Equal(["0.2.0", "0.1.0"], installed.GetProperty("releases").EnumerateArray().Select(r => r.GetProperty("version").GetString()));

        // Pinned, it is not updated; back to the version before, its rule goes again.
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.PostAsync($"/api/admin/plugins/{id}/pin", new { pinned = true }));
        await StatusAssert.Is(HttpStatusCode.Conflict, await admin.PostAsync($"/api/admin/plugins/{id}/update", new { zip = Zip("0.3.0") }));
        var back = await admin.JsonAsync(await admin.PostAsync($"/api/admin/plugins/{id}/rollback", new { }));
        Assert.Equal("0.1.0", back.GetProperty("version").GetString());
        names = [.. (await admin.JsonAsync(await admin.GetAsync("/api/admin/safeguards/rules"))).GetProperty("rules").EnumerateArray().Select(r => r.GetProperty("rule").GetProperty("name").GetString())];
        Assert.DoesNotContain("Weather desk: Hail", names);

        // Its health; and removed, everything it brought goes with it.
        var health = await admin.JsonAsync(await admin.GetAsync($"/api/admin/plugins/{id}/health"));
        Assert.True(health.GetProperty("on").GetBoolean());
        Assert.Equal(0, health.GetProperty("failuresInARow").GetInt32());
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.Http.DeleteAsync(new Uri($"/api/admin/plugins/{id}", UriKind.Relative)));
        Assert.DoesNotContain("Forecaster", (await admin.JsonAsync(await admin.GetAsync("/api/assistants"))).ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain((await admin.JsonAsync(await admin.GetAsync("/api/admin/tools/toolsets"))).EnumerateArray(), s => s.GetProperty("name").GetString() == "Weather");
    }

    [Fact]
    public void Versions_are_compared_part_by_part()
    {
        Assert.True(PluginManifest.CompareVersions("1.10.0", "1.9.2") > 0);
        Assert.Equal(0, PluginManifest.CompareVersions("v2.0", "2.0.0"));
        Assert.True(PluginManifest.CompareVersions("0.1.0", "0.2.0") < 0);
    }
}
