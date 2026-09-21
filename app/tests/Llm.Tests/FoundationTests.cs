using System.Net;
using System.Net.Http.Json;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Llm.Tests;

[Collection(nameof(AppCollection))]
public sealed class FoundationTests(AppFixture app)
{
    private readonly HttpClient _client = app.Factory.CreateClient();

    [Fact]
    public async Task Liveness_is_ok()
    {
        var res = await _client.GetAsync(new Uri("/healthz", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Readiness_checks_the_database()
    {
        var res = await _client.GetAsync(new Uri("/readyz", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("Healthy", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Startup_created_the_database_and_applied_migrations()
    {
        await using var conn = new NpgsqlConnection(app.AppConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "select count(*) from \"__EFMigrationsHistory\" where \"MigrationId\" like '%_Initial'", conn);
        Assert.Equal(1L, await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Bootstrap_is_idempotent()
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(app.AppConnectionString).Options);
        await DatabaseBootstrap.EnsureReadyAsync(db, NullLogger.Instance);
        await DatabaseBootstrap.EnsureReadyAsync(db, NullLogger.Instance);
        db.Settings.Add(new Setting { Key = "test.idempotent", Value = "yes" });
        await db.SaveChangesAsync();
        Assert.Equal("yes", (await db.Settings.SingleAsync(s => s.Key == "test.idempotent")).Value);
    }

    [Fact]
    public async Task Info_reports_name_and_version()
    {
        var info = await _client.GetFromJsonAsync<Dictionary<string, string>>(new Uri("/api/info", UriKind.Relative));
        Assert.NotNull(info);
        Assert.Equal("LLM Service", info["name"]);
        Assert.Matches(@"^\d+\.\d+\.\d+", info["version"]);
    }

    [Fact]
    public async Task Unknown_api_path_is_404_not_the_page()
    {
        var res = await _client.GetAsync(new Uri("/api/does-not-exist", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.DoesNotContain("<div id=\"root\">", await res.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/chat")]
    [InlineData("/dashboards/usage")]
    public async Task Page_routes_serve_the_app_shell_uncached(string path)
    {
        var res = await _client.GetAsync(new Uri(path, UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(AppFixture.IndexHtml, await res.Content.ReadAsStringAsync());
        Assert.Equal("no-cache", res.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Fingerprinted_assets_are_cached_forever()
    {
        var res = await _client.GetAsync(new Uri("/assets/app-abc123.js", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var cc = res.Headers.CacheControl!;
        Assert.True(cc.Public);
        Assert.Equal(TimeSpan.FromDays(365), cc.MaxAge);
        Assert.Contains(cc.Extensions, e => e.Name == "immutable");
    }

    [Fact]
    public async Task Security_headers_are_set()
    {
        var res = await _client.GetAsync(new Uri("/", UriKind.Relative));
        var csp = string.Join(";", res.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("script-src 'self'", csp, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", csp, StringComparison.Ordinal);
        Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", res.Headers.GetValues("X-Frame-Options").Single());
        Assert.False(res.Headers.Contains("Strict-Transport-Security"), "HSTS must not be sent over plain http");
    }

    [Fact]
    public async Task Behind_the_proxy_https_is_recognised()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, new Uri("/", UriKind.Relative));
        req.Headers.Add("X-Forwarded-Proto", "https");
        var res = await _client.SendAsync(req);
        Assert.StartsWith("max-age=", res.Headers.GetValues("Strict-Transport-Security").Single(), StringComparison.Ordinal);
    }
}
