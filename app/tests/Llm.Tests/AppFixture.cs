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
    public const string Domain = "llm.test";
    public const string AdminPassword = "correct horse battery staple admin";
    public const string GrafanaSecret = "grafana-secret-for-tests";
    public const string ApiSecret = "api-secret-for-tests";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("pgvector/pgvector:0.8.0-pg16").Build();
    private readonly string _webRoot = Directory.CreateTempSubdirectory("llm-webroot-").FullName;

    public WebApplicationFactory<Program> Factory { get; private set; } = null!;
    public FakeGateway Gateway { get; } = new();
    public FakeArgus Argus { get; } = new();
    public FakeModel Model { get; } = new();
    public FakeMcp Mcp { get; } = new();
    public FakeEngine Engine { get; } = new();
    public FakeWeb Web { get; } = new();
    public string AppConnectionString { get; private set; } = "";
    /// <summary>The real dashboard files, found by walking up to the repository.</summary>
    public static string DashboardsPath { get; } = FindDashboards();

    private static string FindDashboards()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "stack", "config", "grafana", "dashboards");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }
        throw new DirectoryNotFoundException("stack/config/grafana/dashboards not found above the test binaries.");
    }

    public string DirectoryPath => Path.Combine(_webRoot, "..", Path.GetFileName(_webRoot) + "-directory", "users.yml");

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        AppConnectionString = ConnectionStringFor("llmapp_test");
        await LitellmSeed.CreateAsync(_postgres.GetConnectionString(), "litellm_test");
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
            b.UseSetting("Auth:Domain", Domain);
            b.UseSetting("Auth:AdminPassword", AdminPassword);
            b.UseSetting("Auth:AdminEmail", "admin@llm.test");
            b.UseSetting("Auth:SessionRecheck", "00:00:00");
            b.UseSetting("Auth:DirectoryFile", DirectoryPath);
            b.UseSetting("Oidc:GrafanaSecret", GrafanaSecret);
            b.UseSetting("Oidc:ApiSecret", ApiSecret);
            b.UseSetting("Dashboards:Path", DashboardsPath);
            b.UseSetting("Dashboards:SqlDatabase", "litellm_test");
            b.UseSetting("Dashboards:StatementTimeout", "00:00:03");
            b.UseSetting("Argus:Url", "http://argus:7700");
            b.UseSetting("Argus:AdminToken", FakeArgus.Token);
            b.UseSetting("Stack:EnvSamplesDir", Path.Combine(DashboardsPath, "..", "..", "..", "env-samples"));
            b.UseSetting("Stack:ModelName", "Qwen3.8-Flash-Next");
            b.UseSetting("Stack:ThinkingPresets", "xhigh:Deep think,low:Quick,off:No thinking");
            b.UseSetting("Stack:ModelContext", "32768");
            b.UseSetting("Stack:ModelMaxOutput", "8192");
            b.UseSetting("Chat:ArgusChatToken", FakeArgus.ChatToken);
            b.UseSetting("Gateway:MasterKey", "sk-master-for-tests");
            b.UseSetting("Stack:PriceInputPerMtok", "0.20");
            // Nothing listens here: probes are refused at once instead of waiting on DNS.
            b.UseSetting("Stack:LiteLlmProbeUrl", "http://127.0.0.1:9");
            b.UseSetting("Stack:PrometheusUrl", "http://127.0.0.1:9");
            b.UseSetting("Stack:GrafanaProbeUrl", "http://127.0.0.1:9");
            foreach (var (k, v) in settings ?? new Dictionary<string, string?>())
            {
                b.UseSetting(k, v);
            }
            b.UseEnvironment("Production");
            b.ConfigureTestServices(s =>
            {
                s.AddSingleton(gateway);
                s.AddHttpClient<Llm.Api.Operations.ArgusAdmin>().ConfigurePrimaryHttpMessageHandler(() => Argus);
                s.AddHttpClient<Llm.Api.Chat.ArgusMcp>().ConfigurePrimaryHttpMessageHandler(() => Argus);
                s.AddHttpClient<Llm.Api.Chat.GatewayChat>().ConfigurePrimaryHttpMessageHandler(() => Model);
                s.AddHttpClient(Llm.Api.Chat.Tools.ToolRegistry.McpClient).ConfigurePrimaryHttpMessageHandler(() => Mcp);
                s.AddHttpClient<Llm.Api.Models.EngineClient>().ConfigurePrimaryHttpMessageHandler(() => Engine);
                s.AddSingleton<Llm.Api.Chat.Tools.WebResolver>(Web.Resolver);
                s.AddHttpClient(Llm.Api.Chat.Tools.WebFetcher.Client).ConfigurePrimaryHttpMessageHandler(() => Web);
                s.AddHttpClient(Llm.Api.Chat.Tools.WebFetcher.SearchClient).ConfigurePrimaryHttpMessageHandler(() => Web);
            });
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
