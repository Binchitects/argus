using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;

namespace Llm.Tests;

/// <summary>
/// One real Postgres (the production image) and one app per test run. The app's
/// database does not exist beforehand, so every run exercises the create path.
/// </summary>
public sealed class AppFixture : IAsyncLifetime
{
    public const string IndexHtml = "<!doctype html><title>test shell</title><div id=\"root\"></div>";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("pgvector/pgvector:0.8.0-pg16").Build();

    private readonly string _webRoot = Directory.CreateTempSubdirectory("llm-webroot-").FullName;

    public WebApplicationFactory<Program> Factory { get; private set; } = null!;

    public string AppConnectionString { get; private set; } = "";

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        AppConnectionString = new Npgsql.NpgsqlConnectionStringBuilder(_postgres.GetConnectionString()) { Database = "llmapp_test" }.ConnectionString;
        Directory.CreateDirectory(Path.Combine(_webRoot, "assets"));
        await File.WriteAllTextAsync(Path.Combine(_webRoot, "index.html"), IndexHtml);
        await File.WriteAllTextAsync(Path.Combine(_webRoot, "assets", "app-abc123.js"), "console.log(1)");

        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:App", AppConnectionString);
            b.UseSetting(WebHostDefaults.WebRootKey, _webRoot);
            b.UseEnvironment("Production");
        });
        _ = Factory.Server; // start the app now, so migration failures surface here
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        await _postgres.DisposeAsync();
        Directory.Delete(_webRoot, recursive: true);
    }
}

[CollectionDefinition(nameof(AppCollection))]
public sealed class AppCollection : ICollectionFixture<AppFixture>;
