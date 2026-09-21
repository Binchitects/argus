using Llm.Api.Gateway;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace Llm.Tests;

/// <summary>
/// One real Postgres (the production image) and one app per test run, served as
/// https://llm.test so Secure cookies, the cookie domain and the OIDC issuer
/// behave as in production. The app's database does not exist beforehand, so
/// every run exercises the create path.
/// </summary>
public sealed class AppFixture : IAsyncLifetime
{
    public const string IndexHtml = "<!doctype html><title>test shell</title><div id=\"root\"></div>";
    public const string Domain = "llm.test";
    public const string AdminPassword = "correct horse battery staple admin";
    public const string GrafanaSecret = "grafana-secret-for-tests";
    public const string ApiSecret = "api-secret-for-tests";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("pgvector/pgvector:0.8.0-pg16").Build();
    private readonly string _webRoot = Directory.CreateTempSubdirectory("llm-webroot-").FullName;

    public WebApplicationFactory<Program> Factory { get; private set; } = null!;
    public FakeGateway Gateway { get; } = new();
    public string AppConnectionString { get; private set; } = "";
    public string DirectoryPath => Path.Combine(_webRoot, "..", Path.GetFileName(_webRoot) + "-directory", "users.yml");

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        AppConnectionString = ConnectionStringFor("llmapp_test");
        Directory.CreateDirectory(Path.Combine(_webRoot, "assets"));
        await File.WriteAllTextAsync(Path.Combine(_webRoot, "index.html"), IndexHtml);
        await File.WriteAllTextAsync(Path.Combine(_webRoot, "assets", "app-abc123.js"), "console.log(1)");
        Factory = Create(AppConnectionString, Gateway);
        _ = Factory.Server; // start the app now, so migration failures surface here
    }

    public string ConnectionStringFor(string database) =>
        new Npgsql.NpgsqlConnectionStringBuilder(_postgres.GetConnectionString()) { Database = database }.ConnectionString;

    /// <summary>A separate app on its own database, for tests that need a fresh start (imports, LDAP).</summary>
    public WebApplicationFactory<Program> Create(string connectionString, ILiteLlm gateway, IDictionary<string, string?>? settings = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:App", connectionString);
            b.UseSetting(WebHostDefaults.WebRootKey, _webRoot);
            b.UseSetting("Auth:Domain", Domain);
            b.UseSetting("Auth:AdminPassword", AdminPassword);
            b.UseSetting("Auth:AdminEmail", "admin@llm.test");
            b.UseSetting("Auth:SessionRecheck", "00:00:00");
            b.UseSetting("Auth:DirectoryFile", DirectoryPath);
            b.UseSetting("Oidc:GrafanaSecret", GrafanaSecret);
            b.UseSetting("Oidc:ApiSecret", ApiSecret);
            foreach (var (k, v) in settings ?? new Dictionary<string, string?>())
            {
                b.UseSetting(k, v);
            }
            b.UseEnvironment("Production");
            b.ConfigureTestServices(s => s.AddSingleton(gateway));
        });

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        await _postgres.DisposeAsync();
        Directory.Delete(_webRoot, recursive: true);
    }
}

[CollectionDefinition(nameof(AppCollection))]
public sealed class AppCollection : ICollectionFixture<AppFixture>;
