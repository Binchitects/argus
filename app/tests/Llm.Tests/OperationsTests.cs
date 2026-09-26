using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Llm.Tests;

[Collection(nameof(AppCollection))]
public sealed class OperationsTests(AppFixture app)
{
    private async Task<TestBrowser> Admin() => await new TestBrowser(app.Factory).SignedInAsync("admin", AppFixture.AdminPassword);

    [Fact]
    public async Task Overview_counts_people_spend_and_who_is_over_credit()
    {
        var admin = await Admin();
        var name = "ov" + Guid.NewGuid().ToString("N")[..8];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test", budget = 1 }));
        var o = await admin.JsonAsync(await admin.GetAsync("/api/admin/overview"));
        Assert.True(o.GetProperty("people").GetInt32() >= 2);
        Assert.True(o.GetProperty("admins").GetInt32() >= 1);
        Assert.Contains(name, o.GetProperty("overCredit").EnumerateArray().Select(x => x.GetString())); // the fake gateway reports 1.50 spent
        Assert.True(o.GetProperty("index").GetProperty("configured").GetBoolean());
        Assert.Equal(1, o.GetProperty("index").GetProperty("summary").GetProperty("repos").GetInt32());
        Assert.Equal("Qwen3.8-Flash-Next", o.GetProperty("model").GetString());
        Assert.NotNull(made.GetProperty("id").GetString());
    }

    [Fact]
    public async Task Services_are_probed_and_a_dead_one_says_so()
    {
        var admin = await Admin();
        var services = (await admin.JsonAsync(await admin.GetAsync("/api/admin/services"))).EnumerateArray().ToList();
        Assert.Contains(services, s => s.GetProperty("name").GetString() == "Model gateway" && !s.GetProperty("ok").GetBoolean());
        Assert.Contains(services, s => s.GetProperty("name").GetString() == "Argus");
    }

    [Fact]
    public async Task The_model_page_lists_every_shipped_sample_with_its_block()
    {
        var admin = await Admin();
        var m = await admin.JsonAsync(await admin.GetAsync("/api/admin/model"));
        Assert.Equal("Qwen3.8-Flash-Next", m.GetProperty("running").GetProperty("name").GetString());
        var samples = m.GetProperty("samples").EnumerateArray().ToList();
        var shipped = Directory.GetFiles(Path.Combine(AppFixture.DashboardsPath, "..", "..", "env-samples"), "*.env").Length;
        Assert.Equal(shipped, samples.Count);
        Assert.All(samples, s =>
        {
            Assert.StartsWith("# >>> MODEL", s.GetProperty("block").GetString(), StringComparison.Ordinal);
            Assert.EndsWith("# <<< MODEL", s.GetProperty("block").GetString(), StringComparison.Ordinal);
            Assert.DoesNotContain("PASSWORD", s.GetProperty("block").GetString(), StringComparison.Ordinal);
            Assert.False(string.IsNullOrEmpty(s.GetProperty("title").GetString()));
        });
    }

    [Fact]
    public async Task Settings_show_named_values_and_no_secrets()
    {
        var admin = await Admin();
        var text = await (await admin.GetAsync("/api/admin/settings")).Content.ReadAsStringAsync();
        Assert.Contains("PRICE_INPUT_PER_MTOK", text, StringComparison.Ordinal);
        Assert.Contains("0.20", text, StringComparison.Ordinal);
        Assert.DoesNotContain(AppFixture.AdminPassword, text, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeArgus.Token, text, StringComparison.Ordinal);
        Assert.DoesNotContain(AppFixture.OpenWebUiSecret, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task People_export_is_csv_and_defuses_spreadsheet_formulas()
    {
        var admin = await Admin();
        var name = "cs" + Guid.NewGuid().ToString("N")[..8];
        await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test", displayName = "=HYPERLINK(\"http://evil\")" });
        var res = await admin.GetAsync("/api/admin/people.csv");
        Assert.Equal("text/csv", res.Content.Headers.ContentType?.MediaType);
        Assert.StartsWith("attachment; filename=\"people-", res.Content.Headers.ContentDisposition?.ToString() ?? res.Headers.GetValues("Content-Disposition").Single(), StringComparison.Ordinal);
        var csv = await res.Content.ReadAsStringAsync();
        Assert.StartsWith("username,display_name,email,role,source,disabled,spend,budget,credit_left", csv, StringComparison.Ordinal);
        Assert.Contains("\"'=HYPERLINK(\"\"http://evil\"\")\"", csv, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Argus_status_is_relayed_with_the_token_the_browser_never_sees()
    {
        var admin = await Admin();
        var res = await admin.GetAsync("/api/admin/argus/status");
        var text = await res.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("group/app", text, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeArgus.Token, text, StringComparison.Ordinal);
        Assert.Equal(FakeArgus.Token, app.Argus.Calls.Last(c => c.PathAndQuery == "/admin/index/status").Token);
    }

    [Fact]
    public async Task Starting_an_index_run_passes_branches_and_is_audited()
    {
        var admin = await Admin();
        var res = await admin.PostAsync("/api/admin/argus/index", new { branches = new[] { " develop ", "", "release/*" }, allowPartial = true });
        await StatusAssert.Is(HttpStatusCode.OK, res);
        var call = app.Argus.Calls.Last(c => c.PathAndQuery == "/admin/index");
        Assert.Equal(["develop", "release/*"], call.Body!.Value.GetProperty("branches").EnumerateArray().Select(b => b.GetString()));
        Assert.True(call.Body!.Value.GetProperty("allow_partial").GetBoolean());
        var audit = (await admin.JsonAsync(await admin.GetAsync("/api/admin/audit?take=20"))).EnumerateArray();
        Assert.Contains(audit, e => e.GetProperty("action").GetString() == "argus.index" && e.GetProperty("target").GetString() == "develop,release/*");
    }

    [Fact]
    public async Task A_run_already_in_progress_is_a_409_with_argus_reason()
    {
        var admin = await Admin();
        app.Argus.IndexRunning = true;
        try
        {
            var res = await admin.PostAsync("/api/admin/argus/index", new { branches = Array.Empty<string>() });
            await StatusAssert.Is(HttpStatusCode.Conflict, res);
            Assert.Contains("already in progress", (await admin.JsonAsync(res)).GetProperty("error").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            app.Argus.IndexRunning = false;
        }
    }

    [Fact]
    public async Task Packs_are_listed_and_removed_and_unknown_actions_refused()
    {
        var admin = await Admin();
        var packs = await admin.JsonAsync(await admin.GetAsync("/api/admin/argus/packs"));
        Assert.Equal("dotnet-docs", packs.GetProperty("packs")[0].GetProperty("name").GetString());
        await StatusAssert.Is(HttpStatusCode.OK, await admin.PostAsync("/api/admin/argus/packs/remove", new { name = "dotnet-docs" }));
        await StatusAssert.Is(HttpStatusCode.NotFound, await admin.PostAsync("/api/admin/argus/packs/remove", new { name = "x" }));
        await StatusAssert.Is(HttpStatusCode.NotFound, await admin.PostAsync("/api/admin/argus/packs/format-disk", new { name = "x" }));
    }

    [Fact]
    public async Task Explore_escapes_the_query()
    {
        var admin = await Admin();
        var r = await admin.JsonAsync(await admin.GetAsync("/api/admin/argus/explore?q=" + Uri.EscapeDataString("Parse&repo=evil") + "&limit=99999"));
        Assert.Equal("ParseHeader", r.GetProperty("symbols").GetProperty("rows")[0].GetProperty("name").GetString());
        var call = app.Argus.Calls.Last(c => c.PathAndQuery.StartsWith("/admin/explore", StringComparison.Ordinal));
        Assert.Contains("q=Parse%26repo%3Devil", call.PathAndQuery, StringComparison.Ordinal);
        Assert.Contains("&repo=&", call.PathAndQuery, StringComparison.Ordinal);
        Assert.Contains("limit=500", call.PathAndQuery, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Members_reach_none_of_it()
    {
        var admin = await Admin();
        var name = "om" + Guid.NewGuid().ToString("N")[..8];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        var member = await new TestBrowser(app.Factory).SignedInAsync(name, made.GetProperty("password").GetString()!);
        foreach (var path in new[] { "/api/admin/overview", "/api/admin/services", "/api/admin/model", "/api/admin/settings", "/api/admin/people.csv", "/api/admin/argus/status", "/api/admin/argus/packs", "/api/admin/argus/explore?q=x" })
        {
            await StatusAssert.Is(HttpStatusCode.Forbidden, await member.GetAsync(path));
        }
        await StatusAssert.Is(HttpStatusCode.Forbidden, await member.PostAsync("/api/admin/argus/index", new { branches = Array.Empty<string>() }));
    }

    [Fact]
    public async Task A_token_without_the_argus_profile_is_not_a_deployed_argus()
    {
        await using var noProfile = app.Create(app.ConnectionStringFor("noprof_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(),
            new Dictionary<string, string?> { ["Stack:ComposeProfiles"] = "gateway,proxy,auth,llamacpp" });
        var admin = await new TestBrowser(noProfile).SignedInAsync("admin", AppFixture.AdminPassword);
        Assert.False((await admin.JsonAsync(await admin.GetAsync("/api/admin/argus/status"))).GetProperty("configured").GetBoolean());
        var services = (await admin.JsonAsync(await admin.GetAsync("/api/admin/services"))).EnumerateArray();
        Assert.DoesNotContain(services, s => s.GetProperty("name").GetString() == "Argus");
    }

    [Fact]
    public async Task Without_an_argus_token_the_pages_say_not_configured()
    {
        await using var bare = app.Create(app.ConnectionStringFor("noargus_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(),
            new Dictionary<string, string?> { ["Argus:AdminToken"] = "" });
        var admin = await new TestBrowser(bare).SignedInAsync("admin", AppFixture.AdminPassword);
        var s = await admin.JsonAsync(await admin.GetAsync("/api/admin/argus/status"));
        Assert.False(s.GetProperty("configured").GetBoolean());
    }
}
