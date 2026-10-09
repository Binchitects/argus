using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Llm.Api.Dashboards;
using Llm.Api.Operations;
using Microsoft.Extensions.Options;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

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
        // Spent in the chat: LiteLLM books it to the end user, not the internal user its keys count for.
        await app.SpendAsync($"{name}@example.test", 1.5m);
        var o = await admin.JsonAsync(await admin.GetAsync("/api/admin/overview"));
        Assert.True(o.GetProperty("people").GetInt32() >= 2);
        Assert.True(o.GetProperty("admins").GetInt32() >= 1);
        Assert.Contains(name, o.GetProperty("overCredit").EnumerateArray().Select(x => x.GetString()));
        // Everything in the gateway's log: the seeded requests (1.0012) and this one at least.
        Assert.True(o.GetProperty("spend").GetDecimal() >= 2.5012m, o.GetProperty("spend").ToString());
        var people = (await admin.JsonAsync(await admin.GetAsync("/api/admin/people"))).GetProperty("people").EnumerateArray();
        Assert.Equal(1.5m, people.Single(p => p.GetProperty("userName").GetString() == name).GetProperty("spend").GetDecimal());
        Assert.Contains($"{name}@example.test", await (await admin.GetAsync("/api/admin/people.csv")).Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.True(o.GetProperty("index").GetProperty("configured").GetBoolean());
        Assert.Equal(1, o.GetProperty("index").GetProperty("summary").GetProperty("repos").GetInt32());
        Assert.Equal("Qwen3.8-Flash-Next", o.GetProperty("model").GetString());
        Assert.NotNull(made.GetProperty("id").GetString());
    }

    /// <summary>Prometheus's answer to traefik_tls_certs_not_after: each certificate's name and when it expires.</summary>
    private static string Certs(params (string Cn, DateTimeOffset At)[] certs) =>
        JsonSerializer.Serialize(new
        {
            status = "success",
            data = new
            {
                resultType = "vector",
                result = certs.Select(c => new { metric = new { cn = c.Cn }, value = new object[] { 1_790_000_000, c.At.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture) } }),
            },
        });

    [Fact]
    public async Task Overview_says_when_the_certificate_expires_and_whether_it_is_traefiks_own()
    {
        var admin = await Admin();
        var now = DateTimeOffset.UtcNow;
        var served = Certs(("llm.test", now.AddDays(10).AddHours(2)), ("TRAEFIK DEFAULT CERT", now.AddDays(300)));
        app.Observe.Answers["/api/v1/query"] = q => q["query"]!.Contains("traefik_tls_certs_not_after", StringComparison.Ordinal) ? served : Certs();
        try
        {
            // The one that expires first, Traefik's own left out while another is served; one of your own (no Let's Encrypt here).
            var cert = (await admin.JsonAsync(await admin.GetAsync("/api/admin/overview"))).GetProperty("certificate");
            Assert.Equal("llm.test", cert.GetProperty("name").GetString());
            Assert.Equal(10, cert.GetProperty("days").GetInt32());
            Assert.Equal("own", cert.GetProperty("issuer").GetString());
            Assert.Contains(app.Observe.To("/api/v1/query"), q => q.Args["query"] == "max by (cn) (traefik_tls_certs_not_after)");

            // Only Traefik's own: Overview says so.
            served = Certs(("TRAEFIK DEFAULT CERT", now.AddDays(300)));
            cert = (await admin.JsonAsync(await admin.GetAsync("/api/admin/overview"))).GetProperty("certificate");
            Assert.Equal("traefik", cert.GetProperty("issuer").GetString());

            // Prometheus knows none: nothing is said.
            served = Certs();
            Assert.Equal(JsonValueKind.Null, (await admin.JsonAsync(await admin.GetAsync("/api/admin/overview"))).GetProperty("certificate").ValueKind);
        }
        finally
        {
            app.Observe.Answers.TryRemove("/api/v1/query", out _);
        }
    }

    [Fact]
    public async Task With_lets_encrypt_the_certificate_is_said_to_be_its_and_prometheus_down_says_nothing()
    {
        using var http = new HttpClient(app.Observe, disposeHandler: false);
        var prom = new PromDatasource(http, Options.Create(new StackOptions { PrometheusUrl = "http://prometheus.test" }));
        var now = DateTimeOffset.UtcNow;
        app.Observe.Answers["/api/v1/query"] = _ => Certs(("llm.test", now.AddDays(80)), ("gateway.llm.test", now.AddDays(5).AddHours(1)));
        try
        {
            var cert = await Certificates.ReadAsync(prom, new StackOptions { Acme = "letsencrypt" }, now, CancellationToken.None);
            Assert.Equal(new CertificateStatus("gateway.llm.test", DateTimeOffset.FromUnixTimeSeconds(now.AddDays(5).AddHours(1).ToUnixTimeSeconds()), 5, "letsencrypt"), cert);
            app.Observe.Down["/api/v1/query"] = true;
            Assert.Null(await Certificates.ReadAsync(prom, new StackOptions(), now, CancellationToken.None));
        }
        finally
        {
            app.Observe.Answers.TryRemove("/api/v1/query", out _);
            app.Observe.Down.TryRemove("/api/v1/query", out _);
        }
    }

    private sealed class RuleFile
    {
        public List<RuleGroup> Groups { get; set; } = [];
    }

    private sealed class RuleGroup
    {
        public List<Rule> Rules { get; set; } = [];
    }

    private sealed class Rule
    {
        public string? Alert { get; set; }
        public string? Expr { get; set; }
        public string? For { get; set; }
        public Dictionary<string, string> Labels { get; set; } = [];
    }

    [Fact]
    public void The_certificate_alerts_warn_thirty_days_before_and_are_critical_seven_days_before()
    {
        var path = Path.Combine(AppFixture.DashboardsPath, "..", "..", "..", "..", "deploy", "config", "prometheus", "rules", "stack.yml");
        var file = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).IgnoreUnmatchedProperties().Build()
            .Deserialize<RuleFile>(File.ReadAllText(path));
        var rules = file.Groups.SelectMany(g => g.Rules).Where(r => r.Alert is not null).ToDictionary(r => r.Alert!);
        var soon = rules["CertificateExpiresSoon"];
        Assert.Equal("warning", soon.Labels["severity"]);
        Assert.Contains("traefik_tls_certs_not_after{cn!=\"TRAEFIK DEFAULT CERT\"}", soon.Expr, StringComparison.Ordinal);
        Assert.EndsWith("< 30 * 86400", soon.Expr!.Trim(), StringComparison.Ordinal);
        var verySoon = rules["CertificateExpiresVerySoon"];
        Assert.Equal("critical", verySoon.Labels["severity"]);
        Assert.EndsWith("< 7 * 86400", verySoon.Expr!.Trim(), StringComparison.Ordinal);
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
    public async Task The_gitlab_webhook_secret_is_shown_once_and_argus_keeps_only_its_hash()
    {
        var admin = await Admin();
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/argus/webhook", new { }));
        var token = made.GetProperty("token").GetString()!;
        Assert.Equal(48, token.Length);
        Assert.True(made.GetProperty("enabled").GetBoolean());
        Assert.Equal($"https://argus.{AppFixture.Domain}/hook/gitlab", made.GetProperty("url").GetString());
        Assert.Equal("X-Gitlab-Token", made.GetProperty("header").GetString());
        Assert.Equal(Llm.Api.Operations.ArgusWebhook.Hash(token), app.Argus.WebhookHash);
        Assert.DoesNotContain(app.Argus.Calls, c => c.Body?.ToString().Contains(token, StringComparison.Ordinal) == true);

        // Afterwards: on, where, what came in; the secret never again.
        var view = await admin.JsonAsync(await admin.GetAsync("/api/admin/argus/webhook"));
        Assert.True(view.GetProperty("enabled").GetBoolean());
        Assert.False(view.TryGetProperty("token", out _));
        Assert.Equal("merge", view.GetProperty("deliveries")[0].GetProperty("event").GetString());

        // A new one replaces it.
        var again = (await admin.JsonAsync(await admin.PostAsync("/api/admin/argus/webhook", new { }))).GetProperty("token").GetString()!;
        Assert.NotEqual(token, again);
        Assert.Equal(Llm.Api.Operations.ArgusWebhook.Hash(again), app.Argus.WebhookHash);

        var off = await admin.JsonAsync(await admin.Http.DeleteAsync(new Uri("/api/admin/argus/webhook", UriKind.Relative)));
        Assert.False(off.GetProperty("enabled").GetBoolean());
        Assert.Equal("", app.Argus.WebhookHash);
        var audit = (await admin.JsonAsync(await admin.GetAsync("/api/admin/audit?take=20"))).EnumerateArray().ToList();
        Assert.Equal(2, audit.Count(e => e.GetProperty("action").GetString() == "argus.webhook_secret"));
        Assert.Contains(audit, e => e.GetProperty("action").GetString() == "argus.webhook_off");
    }

    [Fact]
    public async Task Repositories_are_chosen_updated_one_by_one_and_audited()
    {
        var admin = await Admin();
        var repos = await admin.JsonAsync(await admin.GetAsync("/api/admin/argus/repos"));
        var app0 = repos.GetProperty("repos")[0];
        Assert.Equal("Fix the decoder", app0.GetProperty("indexed")[0].GetProperty("message").GetString());

        var res = await admin.Http.PatchAsJsonAsync(new Uri("/api/admin/argus/repos/7", UriKind.Relative), new { included = false, branches = new[] { " develop ", "release/*" } });
        await StatusAssert.Is(HttpStatusCode.OK, res);
        var call = app.Argus.Calls.Last(c => c.PathAndQuery == "/admin/repos/7");
        Assert.False(call.Body!.Value.GetProperty("included").GetBoolean());
        Assert.Equal(["develop", "release/*"], call.Body!.Value.GetProperty("branches").EnumerateArray().Select(b => b.GetString()));
        await StatusAssert.Is(HttpStatusCode.NotFound, await admin.Http.PatchAsJsonAsync(new Uri("/api/admin/argus/repos/8", UriKind.Relative), new { included = true }));

        Assert.Equal("main", (await admin.JsonAsync(await admin.GetAsync("/api/admin/argus/repos/7/branches")))[0].GetProperty("name").GetString());
        await StatusAssert.Is(HttpStatusCode.OK, await admin.Http.PutAsJsonAsync(new Uri("/api/admin/argus/repos/settings", UriKind.Relative), new { newRepos = "exclude" }));
        Assert.Equal("exclude", app.Argus.Calls.Last(c => c.PathAndQuery == "/admin/repos/settings").Body!.Value.GetProperty("new_repos").GetString());

        // Update one repository: only it, now or after the pass running.
        var one = await admin.PostAsync("/api/admin/argus/index", new { repo = "group/app" });
        await StatusAssert.Is(HttpStatusCode.OK, one);
        Assert.Equal("queued", (await admin.JsonAsync(one)).GetProperty("status").GetString());
        Assert.Equal("group/app", app.Argus.Calls.Last(c => c.PathAndQuery == "/admin/index").Body!.Value.GetProperty("repo").GetString());

        var audit = (await admin.JsonAsync(await admin.GetAsync("/api/admin/audit?take=20"))).EnumerateArray().ToList();
        Assert.Contains(audit, e => e.GetProperty("action").GetString() == "argus.repo_choice" && e.GetProperty("target").GetString() == "group/app");
        Assert.Contains(audit, e => e.GetProperty("action").GetString() == "argus.index_repo" && e.GetProperty("target").GetString() == "group/app");
        Assert.Contains(audit, e => e.GetProperty("action").GetString() == "argus.repo_policy");
    }

    [Fact]
    public async Task Repositories_are_changed_many_at_once_given_schedules_and_their_log_is_read()
    {
        var admin = await Admin();
        var res = await admin.PostAsync("/api/admin/argus/repos/batch", new { ids = new[] { 7, 8 }, action = "schedule", schedule = " daily:02:30 " });
        await StatusAssert.Is(HttpStatusCode.OK, res);
        Assert.False((await admin.JsonAsync(res)).GetProperty("results")[1].GetProperty("ok").GetBoolean());
        var call = app.Argus.Calls.Last(c => c.PathAndQuery == "/admin/repos/batch").Body!.Value;
        Assert.Equal("schedule", call.GetProperty("action").GetString());
        Assert.Equal([7L, 8L], call.GetProperty("ids").EnumerateArray().Select(i => i.GetInt64()));
        Assert.Equal("daily:02:30", call.GetProperty("schedule").GetString());

        // Back to the default for all; the default itself, in a time zone.
        await StatusAssert.Is(HttpStatusCode.OK, await admin.Http.PatchAsJsonAsync(new Uri("/api/admin/argus/repos/7", UriKind.Relative), new { schedule = "" }));
        Assert.Equal("", app.Argus.Calls.Last(c => c.PathAndQuery == "/admin/repos/7").Body!.Value.GetProperty("schedule").GetString());
        await StatusAssert.Is(HttpStatusCode.OK, await admin.Http.PutAsJsonAsync(new Uri("/api/admin/argus/repos/settings", UriKind.Relative), new { schedule = "hours:6", timeZone = "Europe/Berlin" }));
        var settings = app.Argus.Calls.Last(c => c.PathAndQuery == "/admin/repos/settings").Body!.Value;
        Assert.Equal("hours:6", settings.GetProperty("schedule").GetString());
        Assert.Equal("Europe/Berlin", settings.GetProperty("schedule_tz").GetString());
        Assert.False(settings.TryGetProperty("new_repos", out _));

        var log = await admin.JsonAsync(await admin.GetAsync("/api/admin/argus/repos/7/log?runs=50"));
        Assert.Equal("Run started by an admin.", log.GetProperty("lines")[0].GetProperty("text").GetString());
        Assert.Contains(app.Argus.Calls, c => c.PathAndQuery == "/admin/repos/7/log?runs=20");

        var audit = (await admin.JsonAsync(await admin.GetAsync("/api/admin/audit?take=20"))).EnumerateArray().ToList();
        Assert.Contains(audit, e => e.GetProperty("action").GetString() == "argus.repos_batch" && e.GetProperty("target").GetString() == "schedule"
            && e.GetProperty("detail").GetString() == "1 of 2 repositories; schedule: daily:02:30");
        Assert.Contains(audit, e => e.GetProperty("action").GetString() == "argus.repo_choice" && e.GetProperty("detail").GetString() == "schedule: default");
    }

    [Fact]
    public async Task The_index_schedule_is_set_in_words_checked_and_shows_its_next_passes()
    {
        var admin = await Admin();
        var first = await admin.JsonAsync(await admin.GetAsync("/api/admin/argus/schedule"));
        Assert.Equal("*/15 * * * *", first.GetProperty("schedule").GetString());
        Assert.Equal(3, first.GetProperty("nextRuns").GetArrayLength());

        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.Http.PutAsJsonAsync(new Uri("/api/admin/argus/schedule", UriKind.Relative), new { schedule = "* * * * *", timeZone = "UTC" }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.Http.PutAsJsonAsync(new Uri("/api/admin/argus/schedule", UriKind.Relative), new { schedule = "0 2 * *", timeZone = "UTC" }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.Http.PutAsJsonAsync(new Uri("/api/admin/argus/schedule", UriKind.Relative), new { schedule = "0 2 * * *", timeZone = "Mars/Base" }));
        try
        {
            await StatusAssert.Is(HttpStatusCode.NoContent, await admin.Http.PutAsJsonAsync(new Uri("/api/admin/argus/schedule", UriKind.Relative), new { schedule = "30 2 * * 1-5", timeZone = "Europe/Berlin" }));
            var set = await admin.JsonAsync(await admin.GetAsync("/api/admin/argus/schedule"));
            Assert.Equal("30 2 * * 1-5", set.GetProperty("schedule").GetString());
            Assert.Equal("Europe/Berlin", set.GetProperty("timeZone").GetString());
            var next = set.GetProperty("nextRuns")[0].GetDateTimeOffset();
            Assert.Equal(new TimeSpan(2, 30, 0), TimeZoneInfo.ConvertTime(next, TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin")).TimeOfDay);

            // Off: only pushes and Index now.
            await StatusAssert.Is(HttpStatusCode.NoContent, await admin.Http.PutAsJsonAsync(new Uri("/api/admin/argus/schedule", UriKind.Relative), new { schedule = "", timeZone = "UTC" }));
            Assert.Equal(0, (await admin.JsonAsync(await admin.GetAsync("/api/admin/argus/schedule"))).GetProperty("nextRuns").GetArrayLength());
        }
        finally
        {
            await admin.Http.PutAsJsonAsync(new Uri("/api/admin/argus/schedule", UriKind.Relative), new { schedule = "*/15 * * * *", timeZone = "UTC" });
        }
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
        foreach (var path in new[] { "/api/admin/overview", "/api/admin/services", "/api/admin/people.csv", "/api/admin/argus/status", "/api/admin/argus/packs", "/api/admin/argus/explore?q=x" })
        {
            await StatusAssert.Is(HttpStatusCode.Forbidden, await member.GetAsync(path));
        }
        await StatusAssert.Is(HttpStatusCode.Forbidden, await member.PostAsync("/api/admin/argus/index", new { branches = Array.Empty<string>() }));
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
