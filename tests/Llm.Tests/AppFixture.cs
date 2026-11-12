using Llm.Api.Gateway;
using Microsoft.EntityFrameworkCore;
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
    public const string LangfuseSecret = "langfuse-secret-for-tests";
    public const string ApiSecret = "api-secret-for-tests";

    /// <summary>
    /// A Postgres server of your own instead of a container, for a machine without
    /// Docker: LLM_TEST_POSTGRES=Host=localhost;Username=postgres;Password=... Each run
    /// makes its own databases there (named with <see cref="_run"/>).
    /// </summary>
    private static readonly string? External = Environment.GetEnvironmentVariable("LLM_TEST_POSTGRES") is { Length: > 0 } cs ? cs : null;
    private readonly string _run = External is null ? "" : "_" + Guid.NewGuid().ToString("N")[..8];
    // Many apps at once, each with its own database and connection pools: more than Postgres's usual 100 connections; and
    // as the stack runs it, 1 GB of shared memory for parallel queries (Docker's 64 MB runs out with so many at once).
    private readonly PostgreSqlContainer? _postgres = External is null
        ? new PostgreSqlBuilder("pgvector/pgvector:0.8.0-pg16").WithCommand("-c", "max_connections=1000")
            .WithCreateParameterModifier(p =>
            {
                p.HostConfig ??= new();
                p.HostConfig.ShmSize = 1L << 30;
            }).Build()
        : null;
    private string Server => External ?? _postgres!.GetConnectionString();
    private readonly string _webRoot = Directory.CreateTempSubdirectory("llm-webroot-").FullName;

    public WebApplicationFactory<Program> Factory { get; private set; } = null!;
    public FakeGateway Gateway { get; } = new();
    public FakeCodeIntel CodeIntel { get; } = new();
    public FakeModel Model { get; } = new();
    public FakeMcp Mcp { get; } = new();
    public FakeEngine Engine { get; } = new();
    public FakeRemote Remote { get; } = new();
    public FakeWeb Web { get; } = new();
    public FakeObserve Observe { get; } = new();
    public FakeWebhook Webhook { get; } = new();
    public FakeHuggingFace HuggingFace { get; } = new();
    public FakeEmbedder Embedder { get; } = new();
    public string AppConnectionString { get; private set; } = "";
    /// <summary>The real dashboard files, found by walking up to the repository.</summary>
    public static string DashboardsPath { get; } = FindDashboards();

    /// <summary>The repository's plugins/ folder: the plugins that come with the app.</summary>
    public static string PluginsPath { get; } = Path.GetFullPath(Path.Combine(DashboardsPath, "..", "..", "..", "..", "plugins"));

    private static string FindDashboards()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Llm.Api", "Dashboards", "json");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }
        throw new DirectoryNotFoundException("src/Llm.Api/Dashboards/json not found above the test binaries.");
    }

    public string DirectoryPath => Path.Combine(_webRoot, "..", Path.GetFileName(_webRoot) + "-directory", "users.yml");

    public async Task InitializeAsync()
    {
        if (_postgres is not null)
        {
            await _postgres.StartAsync();
        }
        AppConnectionString = ConnectionStringFor("llmapp_test" + _run);
        await LitellmSeed.CreateAsync(Server, "litellm_test" + _run);
        Factory = Create(AppConnectionString, Gateway);
        _ = Factory.Server; // start the app now, so migration failures surface here
        // The seed in the app's own ledger too, as the mirror would bring it.
        await MirrorAsync();
    }

    /// <summary>
    /// A chat request at the gateway that cost <paramref name="spend"/>, booked to <paramref name="email"/>
    /// as LiteLLM books the chat (the end user). Long ago, out of every dashboard test's time range.
    /// </summary>
    public async Task SpendAsync(string email, decimal spend)
    {
        await using var conn = new Npgsql.NpgsqlConnection(ConnectionStringFor("litellm_test" + _run));
        await conn.OpenAsync();
        await using var insert = new Npgsql.NpgsqlCommand("""
            insert into "LiteLLM_SpendLogs" (request_id, call_type, api_key, spend, total_tokens, prompt_tokens, completion_tokens,
              "startTime", "endTime", model, "user", metadata, end_user)
            values (@id, 'acompletion', 'hash-chat', @spend, 10, 9, 1, '2025-01-01', '2025-01-01', 'qwen', '', '{"user_api_key_alias":"chat"}', @email)
            """, conn);
        insert.Parameters.AddWithValue("id", Guid.NewGuid().ToString());
        insert.Parameters.AddWithValue("spend", (double)spend);
        insert.Parameters.AddWithValue("email", email);
        await insert.ExecuteNonQueryAsync();
        await MirrorForAsync(email);
    }

    /// <summary>
    /// A request at the gateway at a time of the test's choosing: the chat's (booked to the end user)
    /// or an API key's (booked to the key's person), as LiteLLM books them, to a model and a kind of call.
    /// </summary>
    public async Task SpendAsync(string email, decimal spend, DateTimeOffset at, bool apiKey = false, string model = "qwen", string callType = "acompletion")
    {
        await using var conn = new Npgsql.NpgsqlConnection(ConnectionStringFor("litellm_test" + _run));
        await conn.OpenAsync();
        await using var insert = new Npgsql.NpgsqlCommand("""
            insert into "LiteLLM_SpendLogs" (request_id, call_type, api_key, spend, total_tokens, prompt_tokens, completion_tokens,
              "startTime", "endTime", model, "user", metadata, end_user)
            values (@id, @callType, @key, @spend, 10, 9, 1, @at, @at, @model, '', @metadata::jsonb, @endUser)
            """, conn);
        insert.Parameters.AddWithValue("id", Guid.NewGuid().ToString());
        insert.Parameters.AddWithValue("callType", callType);
        insert.Parameters.AddWithValue("model", model);
        insert.Parameters.AddWithValue("key", apiKey ? "hash-key-" + email : "hash-chat");
        insert.Parameters.AddWithValue("spend", (double)spend);
        insert.Parameters.AddWithValue("at", DateTime.SpecifyKind(at.UtcDateTime, DateTimeKind.Unspecified));
        insert.Parameters.AddWithValue("metadata", apiKey ? $$"""{"user_api_key_user_id":"{{email}}","user_api_key_alias":"app-key"}""" : """{"user_api_key_alias":"chat"}""");
        insert.Parameters.AddWithValue("endUser", apiKey ? "" : email);
        await insert.ExecuteNonQueryAsync();
        await MirrorForAsync(email);
    }

    /// <summary>The gateway's database (its request log), for tests that seed rows of their own.</summary>
    public string LitellmConnectionString => ConnectionStringFor("litellm_test" + _run);

    public string ConnectionStringFor(string database) =>
        new Npgsql.NpgsqlConnectionStringBuilder(Server) { Database = database }.ConnectionString;

    /// <summary>A separate app on its own database, for tests that need a fresh start (imports, LDAP).</summary>
    /// <param name="services">Test services of its own (a clock), after the fakes.</param>
    public WebApplicationFactory<Program> Create(string connectionString, FakeGateway gateway, IDictionary<string, string?>? settings = null, Action<IServiceCollection>? services = null) =>
        Build(connectionString, gateway, settings, services);

    /// <summary>The services of each app whose host is running: listed as it starts, gone as it stops.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<IServiceProvider, byte> _apps = new();

    /// <summary>Lists its app's services with the fixture once the app's other hosted services have started.</summary>
    private sealed class Started(IServiceProvider services, AppFixture fixture) : Microsoft.Extensions.Hosting.IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            fixture._apps[services] = 0;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            fixture._apps.TryRemove(services, out _);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// What a test wrote into LiteLLM's request log (an upgrade's, as the tests seed it), brought into an app's ledger
    /// (gateway.requests) as the import at an upgrade does: the apps read the ledger. This app's (the shared one's when none
    /// is named): each other app reads the whole shared log at its first import.
    /// </summary>

    public Task MirrorAsync(WebApplicationFactory<Program>? into = null) => MirrorIntoAsync((into ?? Factory).Services);

    /// <summary>
    /// The tags of the model calls the app's gateway booked, oldest first: the tag is the ledger's (the gateway does not send
    /// the request's metadata on to the engine). Written lines are flushed first.
    /// </summary>
    public async Task<List<string?>> TagsAsync(string? person = null, WebApplicationFactory<Program>? into = null)
    {
        var services = (into ?? Factory).Services;
        await services.GetRequiredService<Llm.Api.Gateway.ModelGateway>().FlushAsync(CancellationToken.None);
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<Llm.Core.Data.AppDbContext>().GatewayRequests.AsNoTracking()
            .Where(r => person == null || r.Person == person).OrderBy(r => r.StartedAt).Select(r => r.Tag).ToListAsync();
    }

    /// <summary>What the ledger has this person spend in all, the app's own gateway's lines flushed first.</summary>
    public async Task<decimal> CostAsync(string person, WebApplicationFactory<Program>? into = null)
    {
        var services = (into ?? Factory).Services;
        await services.GetRequiredService<Llm.Api.Gateway.ModelGateway>().FlushAsync(CancellationToken.None);
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<Llm.Core.Data.AppDbContext>().GatewayRequests.AsNoTracking()
            .Where(r => r.Person == person).SumAsync(r => r.Cost);
    }

    /// <summary>A person's keys as the app keeps them (gateway.api_keys), the oldest first: Token is the key's hash.</summary>
    public async Task<List<TestKey>> KeysOfAsync(string email, WebApplicationFactory<Program>? into = null)
    {
        await using var scope = (into ?? Factory).Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Llm.Core.Data.AppDbContext>();
        var normalized = email.ToUpperInvariant();
        return await (from k in db.ApiKeys.AsNoTracking()
                      join u in db.Users.AsNoTracking() on k.UserId equals u.Id
                      where u.NormalizedEmail == normalized
                      orderby k.CreatedAt
                      select new TestKey(k.Hash.TrimEnd(), k.Alias, k.Kind, k.Name, k.Parallel, k.RequestsPerMinute, k.TokensPerMinute, k.MonthlyBudget, k.ExpiresAt)).ToListAsync();
    }

    /// <summary>A person's keys as their pages show them (<see cref="Llm.Api.Gateway.ApiKeys.KeysAsync(string, CancellationToken)"/>): hashed token, whether blocked.</summary>
    public static async Task<IReadOnlyList<Llm.Api.Gateway.GatewayKey>> GatewayKeysAsync(WebApplicationFactory<Program> into, string email)
    {
        await using var scope = into.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<Llm.Api.Gateway.ApiKeys>().KeysAsync(email);
    }

    /// <summary>A key's hashed token, as the app keeps it.</summary>
    public static string TokenOf(string key) => Llm.Api.Gateway.AnswerCache.HashOf(key);

    /// <summary>The routes the app builds from its own tables (not a test's): each one's name, fingerprint, what it is given and what it says of itself.</summary>
    public async Task<List<Llm.Api.Gateway.Deployment>> RoutesOfAsync(WebApplicationFactory<Program>? into = null)
    {
        await using var scope = (into ?? Factory).Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<Llm.Api.Gateway.GatewayRoutes>().BuildAsync(CancellationToken.None, others: false);
    }

    /// <summary>How many model calls the app booked with this tag (<see cref="TagsAsync"/>).</summary>
    public async Task<int> TaggedAsync(string tag, string? person = null, WebApplicationFactory<Program>? into = null) =>
        (await TagsAsync(person, into)).Count(t => t == tag);

    /// <summary>The same, into each running app that has this person: the app a test made them in.</summary>
    public async Task MirrorForAsync(string email)
    {
        var normalized = email.ToUpperInvariant();
        foreach (var services in _apps.Keys)
        {
            bool theirs;
            try
            {
                await using var scope = services.CreateAsyncScope();
                theirs = await scope.ServiceProvider.GetRequiredService<Llm.Core.Data.AppDbContext>().Users.AnyAsync(u => u.NormalizedEmail == normalized);
            }
            catch (ObjectDisposedException)
            {
                continue; // stopping as it was asked
            }
            if (theirs)
            {
                await MirrorIntoAsync(services);
            }
        }
    }

    private static async Task MirrorIntoAsync(IServiceProvider services)
    {
        var import = services.GetRequiredService<LedgerImport>();
        while (await import.ImportAsync(CancellationToken.None) == LedgerImport.Batch)
        {
            // A full batch: there is more.
        }
    }

    private WebApplicationFactory<Program> Build(string connectionString, FakeGateway gateway, IDictionary<string, string?>? settings, Action<IServiceCollection>? services) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:App", connectionString);
            b.UseSetting("Database:StrictQueries", "true");
            b.UseSetting("Auth:Domain", Domain);
            b.UseSetting("Auth:AdminPassword", AdminPassword);
            b.UseSetting("Auth:AdminEmail", "admin@llm.test");
            b.UseSetting("Auth:SessionRecheck", "00:00:00");
            b.UseSetting("Auth:DirectoryFile", DirectoryPath);
            b.UseSetting("Oidc:LangfuseSecret", LangfuseSecret);
            b.UseSetting("Oidc:ApiSecret", ApiSecret);
            b.UseSetting("Dashboards:Path", DashboardsPath);
            b.UseSetting("Plugins:Directory", PluginsPath);
            // LiteLLM's database of an upgrade, as the tests seed it: imported when a test asks (LedgerImportTests, SpendAsync), not on its own.
            b.UseSetting("Gateway:ImportDatabase", "litellm_test" + _run);
            b.UseSetting("Gateway:LedgerImport", "false");
            b.UseSetting("Dashboards:StatementTimeout", "00:00:03");
            // Many apps at once here: the bell's watcher would hold their connections (its checks are called directly).
            b.UseSetting("Notifications:Watch", "false");
            // Tests send many messages a minute; the safeguards' own tests set the limit.
            b.UseSetting("Safeguards:MessagesPerMinute", "0");
            b.UseSetting("CodeIntel:Url", "http://code-intel:7700");
            b.UseSetting("CodeIntel:AdminToken", FakeCodeIntel.Token);
            b.UseSetting("Chat:DefaultModel", "Qwen3.8-Flash-Next");
            b.UseSetting("Chat:ThinkingPresets", "xhigh:Deep think,low:Quick,off:No thinking");
            b.UseSetting("Chat:CodeIntelChatToken", FakeCodeIntel.ChatToken);
            // Every tool whole, unless a test sends tools on demand: the default set grows with each new tool.
            b.UseSetting("Chat:ToolTextChars", "0");
            // No engine and no media servers here unless a test brings them (the models tests do).
            b.UseSetting("Engine:Enabled", "false");
            b.UseSetting("Modules:imagegen", "false");
            b.UseSetting("Modules:videogen", "false");
            b.UseSetting("Modules:audio", "false");
            // No embedder unless a test brings it (company knowledge and retrieval do).
            b.UseSetting("Modules:embed", "false");
            // None of Arena's own skills unless a test brings them: the skills' tests count what is there.
            b.UseSetting("Skills:BuiltIn", "false");
            // Past chats' digests only where a test asks for them (ChatDigests.RunAsync): the others count the model's requests.
            b.UseSetting("Memory:Digests", "false");
            b.UseSetting("Memory:Garden", "false");
            // The audit chain's anchors are only logged, unless a test gives them a folder.
            b.UseSetting("Audit:AnchorDir", "");
            // Access reviews only when a test starts one.
            b.UseSetting("Compliance:ReviewEveryDays", "0");
            // Compliance's starter only where a test asks for it: the others count their rules, policies and suppliers from none.
            b.UseSetting("Compliance:Starter", "false");
            // Security alerts only when a test looks.
            b.UseSetting("Security:Watch", "false");
            // No Laya unless a test brings it (LayaTests).
            b.UseSetting("Modules:laya", "false");
            // Nothing listens here: probes are refused at once instead of waiting on DNS.
            b.UseSetting("Stack:PrometheusUrl", "http://127.0.0.1:9");
            b.UseSetting("Dashboards:AlertmanagerUrl", "http://127.0.0.1:9");
            b.UseSetting("Dashboards:LokiUrl", "http://127.0.0.1:9");
            foreach (var (k, v) in settings ?? new Dictionary<string, string?>())
            {
                b.UseSetting(k, v);
            }
            b.UseEnvironment("Production");
            b.ConfigureTestServices(s =>
            {
                // The models a test asks for, as routes beside the app's own.
                s.AddSingleton(gateway);
                s.AddSingleton<IRoutesSource>(gateway);
                s.AddHttpClient<Llm.Api.Operations.CodeIntelAdmin>().ConfigurePrimaryHttpMessageHandler(() => CodeIntel);
                s.AddHttpClient<Llm.Api.Chat.CodeIntelMcp>().ConfigurePrimaryHttpMessageHandler(() => CodeIntel);
                // The app's own gateway sends to the engines: here, the fake model behind every route.
                s.AddHttpClient(Llm.Api.Gateway.ModelGateway.Upstream).ConfigurePrimaryHttpMessageHandler(() => Model);
                s.AddHttpClient(Llm.Api.Gateway.ModelGateway.UpstreamUnchecked).ConfigurePrimaryHttpMessageHandler(() => Model);
                s.AddHttpClient(Llm.Api.Chat.Tools.ToolRegistry.McpClient).ConfigurePrimaryHttpMessageHandler(() => Mcp);
                s.AddHttpClient(Llm.Api.Plugins.PluginCatalog.Client).ConfigurePrimaryHttpMessageHandler(() => Mcp);
                s.AddHttpClient(Llm.Api.Chat.Tools.CustomTools.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => Mcp);
                s.AddHttpClient(Llm.Api.Schedules.GitLabBot.Client).ConfigurePrimaryHttpMessageHandler(() => Mcp);
                s.AddHttpClient(Llm.Api.Models.LlamaCppBackend.Client).ConfigurePrimaryHttpMessageHandler(() => Engine);
                s.AddHttpClient(Llm.Api.Models.RemoteServerClient.Client).ConfigurePrimaryHttpMessageHandler(() => Remote);
                s.AddHttpClient(Llm.Api.Models.RemoteServerClient.Unchecked).ConfigurePrimaryHttpMessageHandler(() => Remote);
                s.AddSingleton<Llm.Api.Chat.Tools.WebResolver>(Web.Resolver);
                s.AddHttpClient(Llm.Api.Chat.Tools.WebFetcher.Client).ConfigurePrimaryHttpMessageHandler(() => Web);
                s.AddHttpClient(Llm.Api.Chat.Tools.WebFetcher.SearchClient).ConfigurePrimaryHttpMessageHandler(() => Web);
                s.AddHttpClient<Llm.Api.Dashboards.PromDatasource>().ConfigurePrimaryHttpMessageHandler(() => Observe);
                s.AddHttpClient<Llm.Api.Dashboards.LokiDatasource>().ConfigurePrimaryHttpMessageHandler(() => Observe);
                s.AddHttpClient<Llm.Api.Dashboards.AlertmanagerClient>().ConfigurePrimaryHttpMessageHandler(() => Observe);
                s.AddHttpClient(Llm.Api.Schedules.Webhooks.Client).ConfigurePrimaryHttpMessageHandler(() => Webhook);
                s.AddHttpClient<Llm.Api.Models.HuggingFace>().ConfigurePrimaryHttpMessageHandler(() => HuggingFace);
                s.AddHttpClient(Llm.Api.Knowledge.Embedder.Client).ConfigurePrimaryHttpMessageHandler(() => Embedder);
                services?.Invoke(s);
                // Last, so it runs once the app's own hosted services have started.
                s.AddHostedService(sp => new Started(sp, this));
            });
        });

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        if (_postgres is not null)
        {
            try
            {
                await _postgres.DisposeAsync();
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or HttpRequestException)
            {
                // Every test has run. A Docker slow to delete the container (an hour of the suite's databases on a CI
                // runner's disk took more than its client's 100 s) leaves it to Ryuk, which removes it once the run ends.
                Console.Error.WriteLine($"The test database's container is left to Ryuk to remove: {ex.GetType().Name}: {ex.Message}");
            }
        }
        Directory.Delete(_webRoot, recursive: true);
    }
}

[CollectionDefinition(nameof(AppCollection))]
public sealed class AppCollection : ICollectionFixture<AppFixture>;

/// <summary>A key as the app keeps it, for a test: its hash (Token), alias, kind, name and own limits.</summary>
public sealed record TestKey(string Token, string Alias, string Kind, string? Name, int? Parallel, int? RequestsPerMinute, int? TokensPerMinute, decimal? MonthlyBudget,
    DateTimeOffset? ExpiresAt);
