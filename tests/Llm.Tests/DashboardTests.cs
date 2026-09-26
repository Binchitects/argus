using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Llm.Api.Dashboards;

namespace Llm.Tests;

[Collection(nameof(AppCollection))]
public sealed class DashboardTests(AppFixture app)
{
    private async Task<TestBrowser> Admin() => await new TestBrowser(app.Factory).SignedInAsync("admin", AppFixture.AdminPassword);

    private static async Task<JsonElement> Query(TestBrowser b, string uid, int key, long? intervalMs = null)
    {
        var res = await b.Http.PostAsJsonAsync(new Uri($"/api/dashboards/{uid}/panels/{key}/query", UriKind.Relative),
            new { from = LitellmSeed.From, to = LitellmSeed.To, intervalMs });
        await StatusAssert.Is(HttpStatusCode.OK, res);
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
    }

    private static async Task<(JsonElement Dashboard, Dictionary<string, int> Keys)> Load(TestBrowser b, string uid)
    {
        var d = await b.JsonAsync(await b.GetAsync($"/api/dashboards/{uid}"));
        var keys = d.GetProperty("panels").EnumerateArray()
            .Where(p => p.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String)
            .GroupBy(p => p.GetProperty("title").GetString()!)
            .ToDictionary(g => g.Key, g => g.First().GetProperty("key").GetInt32());
        return (d, keys);
    }

    private static double Stat(JsonElement result) =>
        result.GetProperty("results")[0].GetProperty("table").GetProperty("rows")[0][0].GetDouble();

    [Fact]
    public async Task Every_sql_panel_of_the_real_dashboards_runs_without_error()
    {
        var b = await Admin();
        foreach (var uid in new[] { "usage-by-user", "llm-overview" })
        {
            var d = await b.JsonAsync(await b.GetAsync($"/api/dashboards/{uid}"));
            var sqlPanels = d.GetProperty("panels").EnumerateArray()
                .Where(p => p.GetProperty("supported").GetBoolean() && p.GetProperty("type").GetString() is not ("text" or "row")).ToList();
            Assert.NotEmpty(sqlPanels);
            foreach (var p in sqlPanels)
            {
                var r = await Query(b, uid, p.GetProperty("key").GetInt32(), 3_600_000);
                foreach (var t in r.GetProperty("results").EnumerateArray())
                {
                    Assert.True(t.GetProperty("error").ValueKind == JsonValueKind.Null,
                        $"{uid} / {p.GetProperty("title")}: {t.GetProperty("error")}");
                }
            }
        }
    }

    [Fact]
    public async Task Usage_numbers_match_the_seeded_requests_and_ignore_those_outside_the_range()
    {
        var b = await Admin();
        var (_, k) = await Load(b, "usage-by-user");
        Assert.Equal(4, Stat(await Query(b, "usage-by-user", k["Requests"])));
        Assert.Equal(180, Stat(await Query(b, "usage-by-user", k["Input tokens, cache miss"])));  // (100-40) + 60 + 50 + 10
        Assert.Equal(40, Stat(await Query(b, "usage-by-user", k["Input tokens, cache hit"])));
        Assert.Equal(22, Stat(await Query(b, "usage-by-user", k["Output tokens"])));
        Assert.Equal(0.0022, Stat(await Query(b, "usage-by-user", k["Cost"])), 9);
        Assert.Equal(1, Stat(await Query(b, "usage-by-user", k["Master-key requests"])));
    }

    [Fact]
    public async Task Per_person_table_attributes_key_and_chat_usage()
    {
        var b = await Admin();
        var (_, k) = await Load(b, "usage-by-user");
        var table = (await Query(b, "usage-by-user", k["Per person — every surface combined"])).GetProperty("results")[0].GetProperty("table");
        var cols = table.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("name").GetString()).ToList();
        var rows = table.GetProperty("rows").EnumerateArray().ToDictionary(r => r[0].GetString()!, r => r);
        Assert.Equal(2, rows["alice@example.test"][cols.IndexOf("Requests")].GetDouble());
        Assert.Equal(176, rows["alice@example.test"][cols.IndexOf("Tokens")].GetDouble());
        Assert.Equal(1, rows["bob@example.test"][cols.IndexOf("Requests")].GetDouble());
        Assert.True(rows.ContainsKey("(unattributed)"));
    }

    [Fact]
    public async Task Daily_series_are_bucketed_by_day_and_named_by_person()
    {
        var b = await Admin();
        var (_, k) = await Load(b, "usage-by-user");
        var series = (await Query(b, "usage-by-user", k["Tokens per day, by person"])).GetProperty("results")[0].GetProperty("series")
            .EnumerateArray().ToDictionary(s => s.GetProperty("name").GetString()!, s => s.GetProperty("points"));
        var alice = series["alice@example.test"].EnumerateArray().Select(p => (T: p[0].GetDouble(), V: p[1].GetDouble())).ToList();
        Assert.Equal([(new DateTimeOffset(2026, 9, 5, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(), 110.0),
                      (new DateTimeOffset(2026, 9, 6, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(), 66.0)], alice);
    }

    [Fact]
    public async Task The_interval_follows_grafanas_rounding()
    {
        var b = await Admin();
        var (_, k) = await Load(b, "usage-by-user");
        var res = await b.Http.PostAsJsonAsync(new Uri($"/api/dashboards/usage-by-user/panels/{k["Tokens by kind"]}/query", UriKind.Relative),
            new { from = LitellmSeed.From, to = LitellmSeed.To, maxDataPoints = 100 });
        // 9 days / 100 points = 7776 s -> rounded to 2 h, as Grafana does.
        Assert.Equal(7_200_000, (await b.JsonAsync(res)).GetProperty("intervalMs").GetInt64());
    }

    [Fact]
    public async Task The_browser_never_receives_the_queries()
    {
        var b = await Admin();
        var text = await (await b.GetAsync("/api/dashboards/usage-by-user")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("rawSql", text, StringComparison.Ordinal);
        Assert.DoesNotContain("LiteLLM_SpendLogs", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Members_cannot_see_everyones_usage()
    {
        var admin = await Admin();
        var name = "dm" + Guid.NewGuid().ToString("N")[..8];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        var member = await new TestBrowser(app.Factory).SignedInAsync(name, made.GetProperty("password").GetString()!);
        await StatusAssert.Is(HttpStatusCode.Forbidden, await member.GetAsync("/api/dashboards/usage-by-user"));
        var res = await member.Http.PostAsJsonAsync(new Uri("/api/dashboards/usage-by-user/panels/1/query", UriKind.Relative), new { from = LitellmSeed.From, to = LitellmSeed.To });
        await StatusAssert.Is(HttpStatusCode.Forbidden, res);
    }

    [Fact]
    public async Task A_person_sees_exactly_their_own_usage()
    {
        var admin = await Admin();
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = "alice", email = "alice@example.test" }));
        var alice = await new TestBrowser(app.Factory).SignedInAsync("alice", made.GetProperty("password").GetString()!);
        var q = $"from={Uri.EscapeDataString(LitellmSeed.From.ToString("o"))}&to={Uri.EscapeDataString(LitellmSeed.To.ToString("o"))}&intervalMs=86400000";
        var me = await alice.JsonAsync(await alice.GetAsync("/api/usage/me?" + q));
        var t = me.GetProperty("totals");
        Assert.Equal(2, t.GetProperty("requests").GetDouble());
        Assert.Equal(120, t.GetProperty("inputMiss").GetDouble()); // (100-40) + 60
        Assert.Equal(40, t.GetProperty("inputHit").GetDouble());
        Assert.Equal(16, t.GetProperty("output").GetDouble());
        Assert.Equal(0.0016, t.GetProperty("cost").GetDouble(), 9);
        var names = me.GetProperty("series").EnumerateArray().Select(s => s.GetProperty("name").GetString()).ToList();
        Assert.Contains("Input, cache hit", names);
    }

    [Fact]
    public async Task Unknown_dashboards_and_panels_are_404_and_bad_ranges_400()
    {
        var b = await Admin();
        await StatusAssert.Is(HttpStatusCode.NotFound, await b.GetAsync("/api/dashboards/nope"));
        var res = await b.Http.PostAsJsonAsync(new Uri("/api/dashboards/usage-by-user/panels/999/query", UriKind.Relative), new { from = LitellmSeed.From, to = LitellmSeed.To });
        await StatusAssert.Is(HttpStatusCode.NotFound, res);
        res = await b.Http.PostAsJsonAsync(new Uri("/api/dashboards/usage-by-user/panels/1/query", UriKind.Relative), new { from = LitellmSeed.To, to = LitellmSeed.From });
        await StatusAssert.Is(HttpStatusCode.BadRequest, res);
    }

    [Theory]
    [InlineData("select 1", false)]
    [InlineData("select 1;", false)]
    [InlineData("select ';' as x", false)]
    [InlineData("select 1 -- trailing; comment", false)]
    [InlineData("select 1; reset role", true)]
    [InlineData("select 1; drop table x", true)]
    public void Only_single_statements_run(string sql, bool rejected) =>
        Assert.Equal(rejected, SqlDatasource.HasSecondStatement(sql.TrimEnd(';')));
}

[Collection(nameof(AppCollection))]
public sealed class ReadOnlyTests(AppFixture app)
{
    [Fact]
    public async Task Panel_queries_cannot_write_even_through_a_function()
    {
        var sql = app.Factory.Services.GetService(typeof(SqlDatasource)) as SqlDatasource;
        var ex = await Assert.ThrowsAnyAsync<Npgsql.PostgresException>(() =>
            sql!.QueryAsync("""delete from "LiteLLM_SpendLogs" returning 1""", CancellationToken.None));
        Assert.True(ex.SqlState is "25006" or "42501", ex.SqlState); // read-only transaction / permission denied
        var rows = await sql!.QueryAsync("""select count(*) from "LiteLLM_SpendLogs" """, CancellationToken.None);
        Assert.Equal(5.0, rows.Rows[0][0]);
    }

    [Fact]
    public async Task A_runaway_query_is_stopped()
    {
        var sql = app.Factory.Services.GetService(typeof(SqlDatasource)) as SqlDatasource;
        var ex = await Assert.ThrowsAnyAsync<Npgsql.PostgresException>(() => sql!.QueryAsync("select pg_sleep(60)", CancellationToken.None));
        Assert.Equal("57014", ex.SqlState); // statement timeout
    }
}
