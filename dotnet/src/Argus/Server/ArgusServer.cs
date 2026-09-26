using System.Diagnostics;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Argus.Access;
using Argus.Configuration;
using Argus.Packs;
using Argus.Platform;
using Argus.Store;
using Argus.Util;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Argus.Server;

/// <summary>A principal that carries the resolved Argus identity through the MCP pipeline.</summary>
public sealed class ArgusPrincipal : ClaimsPrincipal
{
    public Identity ArgusIdentity { get; }

    public ArgusPrincipal(Identity identity) : base(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, identity.UserId.ToString()), new Claim(ClaimTypes.Name, identity.Username)],
        authenticationType: "argus"))
    {
        ArgusIdentity = identity;
    }
}

/// <summary>
/// The whole backend on one port: the React app, its API under /api (signed-in
/// people, chat, administration), the MCP endpoint behind per-caller bearer
/// authentication and the DNS-rebinding guard, /healthz, the code index's
/// operator surface under /admin/ (an administrator's session, or
/// ARGUS_ADMIN_TOKEN for scripts), the GitLab push webhook (gated by
/// ARGUS_WEBHOOK_TOKEN), and the in-process index scheduler.
/// </summary>
public static class ArgusServer
{
    public const string HealthzPath = "/healthz";
    public const string AdminPrefix = "/admin/";
    public const string ApiPrefix = "/api/";
    public const string AdminTokenEnv = "ARGUS_ADMIN_TOKEN";
    public const string WebhookPath = "/hook/gitlab";
    public const string WebhookTokenEnv = "ARGUS_WEBHOOK_TOKEN";
    public const string WebhookHeader = "x-gitlab-token";
    public const int WebhookQueueLimit = 25;
    public const string DeniedAtGateTool = "<auth_denied>";
    public static readonly string[] DefaultAllowedHosts = ["127.0.0.1:*", "localhost:*", "[::1]:*"];

    static string Env(string name) => (Environment.GetEnvironmentVariable(name) ?? "").Trim();
    public static string WebhookToken() => Env(WebhookTokenEnv);
    static bool WebhookEnabled() => WebhookToken().Length > 0;
    static string AdminToken() => Env(AdminTokenEnv);
    static bool AdminEnabled() => AdminToken().Length > 0;

    static readonly JsonSerializerOptions JsonOut = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    static IResult Json(JsonNode body, int status = 200) =>
        Results.Text(body.ToJsonString(JsonOut), "application/json", Encoding.UTF8, status);

    /// <summary>Constant-time comparison, like hmac.compare_digest.</summary>
    public static bool SecretEquals(string supplied, string expected) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(expected));

    public static string? ExtractBearer(string? header)
    {
        if (string.IsNullOrEmpty(header)) return null;
        int space = header.IndexOf(' ');
        if (space < 0) return null;
        if (!header[..space].Equals("bearer", StringComparison.OrdinalIgnoreCase)) return null;
        var token = PyStr.Strip(header[(space + 1)..]);
        return token.Length > 0 ? token : null;
    }

    /// <summary>Build the web application; <paramref name="configure"/> lets tests swap in a TestServer.</summary>
    public static WebApplication Create(ArgusConfig cfg, IReadOnlyList<string>? allowedHosts = null, string[]? args = null,
        Action<WebApplicationBuilder>? configure = null)
    {
        using (var conn = Db.Connect(cfg.Index.DbPath)) Db.Migrate(conn);
        var appDbPath = AppDb.PathFor(cfg.Index.DataDir);
        using (var conn = AppDb.Open(appDbPath))
            if (Users.Bootstrap(conn) is { } admin) Console.WriteLine($"created the first administrator, {admin.Username}");

        var tools = new Tools(cfg);
        var jobs = new Jobs(cfg);
        var gateway = Gateway.FromEnvironment();
        var directory = new Lazy<MemberDirectory>(() => new MemberDirectory(cfg.GitLab));
        Identity IdentityFor(AppUser user)
        {
            using var conn = Db.Connect(cfg.Index.DbPath);
            return People.ResolvePerson(conn, directory.Value, user.Email, user.GitlabUsername);
        }
        var chat = new ChatService(tools, gateway, IdentityFor, appDbPath);
        var builder = WebApplication.CreateSlimBuilder(args ?? []);
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(o => o.SingleLine = true);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton(tools);
        builder.Services.AddSingleton(jobs);
        PlatformApi.AddServices(builder.Services);
        builder.Services
            .AddMcpServer(o =>
            {
                o.ServerInfo = new Implementation { Name = "argus", Version = Metrics.Version };
                o.ServerInstructions = ToolCatalog.ServerInstructions;
            })
            .WithHttpTransport(o => o.SessionMode = HttpServerSessionMode.StatefulForInitializeClients)
            .WithListToolsHandler(McpHandlers.ListTools)
            .WithCallToolHandler(McpHandlers.CallTool);
        configure?.Invoke(builder);

        var app = builder.Build();
        McpHandlers.Configure(tools, app.Services.GetRequiredService<IHttpContextAccessor>());

        var hosts = allowedHosts is { Count: > 0 } ? allowedHosts.ToList() : DefaultAllowedHosts.ToList();
        var origins = allowedHosts is { Count: > 0 }
            ? hosts.SelectMany(h => new[] { $"http://{h}", $"https://{h}" }).ToList()
            : hosts.Select(h => $"http://{h}").ToList();

        PlatformApi.MapWeb(app, WebRoot());
        app.Use(PlatformApi.Errors);
        app.Use(async (ctx, next) => await Authenticate(ctx, next, cfg, directory, appDbPath));
        app.UseRateLimiter();
        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path.StartsWithSegments("/mcp") && !TransportSecurity(ctx, hosts, origins, out var status, out var message))
            {
                ctx.Response.StatusCode = status;
                await ctx.Response.WriteAsync(message);
                return;
            }
            await next();
        });

        app.MapGet(HealthzPath, () => Json(new JsonObject { ["status"] = "ok" }));
        MapAdmin(app, cfg, jobs);
        if (WebhookEnabled()) MapWebhook(app, jobs);
        PlatformApi.Map(app, cfg, appDbPath, gateway, chat);
        app.MapMcp("/mcp");

        if (Jobs.IndexInterval() > 0) jobs.StartScheduler();
        return app;
    }

    static bool HostMatches(string value, IEnumerable<string> allowed)
    {
        foreach (var a in allowed)
        {
            if (value == a) return true;
            if (a.EndsWith(":*", StringComparison.Ordinal) && value.StartsWith(a[..^2] + ":", StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>The MCP SDK's transport security in Python: Content-Type, then Host, then Origin.</summary>
    static bool TransportSecurity(HttpContext ctx, List<string> hosts, List<string> origins, out int status, out string message)
    {
        status = 200;
        message = "";
        if (HttpMethods.IsPost(ctx.Request.Method))
        {
            var ct = ctx.Request.ContentType ?? "";
            if (!ct.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
            {
                (status, message) = (400, "Invalid Content-Type header");
                return false;
            }
        }
        var host = ctx.Request.Headers.Host.ToString();
        if (host.Length == 0 || !HostMatches(host, hosts))
        {
            (status, message) = (421, "Invalid Host header");
            return false;
        }
        var origin = ctx.Request.Headers.Origin.ToString();
        if (origin.Length > 0 && !HostMatches(origin, origins))
        {
            (status, message) = (403, "Invalid Origin header");
            return false;
        }
        return true;
    }

    static async Task Unauthorized(HttpContext ctx, string message)
    {
        ctx.Response.StatusCode = 401;
        ctx.Response.Headers.WWWAuthenticate = "Bearer";
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(new JsonObject { ["error"] = message }.ToJsonString(JsonOut));
    }

    static void AuditDenied(ArgusConfig cfg, string reason, string path, string? detail = null)
    {
        try
        {
            using var conn = Db.ConnectAudit(cfg.Index.DbPath);
            Writes.RecordAudit(conn, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), null, null, DeniedAtGateTool, "{}", null);
        }
        catch (Exception exc)
        {
            Console.Error.WriteLine($"failed to record audit row for a denied request: {exc.Message}");
        }
        AuditLog.Denied(reason, path, detail);
    }

    /// <summary><c>ARGUS_WEB_ROOT</c>, or the <c>wwwroot</c> published beside the binary.</summary>
    static string WebRoot() =>
        Environment.GetEnvironmentVariable("ARGUS_WEB_ROOT") is { Length: > 0 } w ? w : Path.Combine(AppContext.BaseDirectory, "wwwroot");

    static async Task Authenticate(HttpContext ctx, Func<Task> next, ArgusConfig cfg, Lazy<MemberDirectory> directory, string appDbPath)
    {
        var path = ctx.Request.Path.Value ?? "";
        PlatformApi.Identify(ctx, appDbPath);
        var user = PlatformApi.CurrentUser(ctx);

        if (path.StartsWith(ApiPrefix, StringComparison.Ordinal) || path.StartsWith(AdminPrefix, StringComparison.Ordinal))
        {
            if (!PlatformApi.CsrfOk(ctx))
            {
                ctx.Response.StatusCode = 403;
                await ctx.Response.WriteAsJsonAsync(new { error = $"A change made from the browser must carry the {PlatformApi.CsrfHeader} header." });
                return;
            }
            // Signing in needs no account; the operator surface authorises itself (a session or its token).
            if (path.StartsWith(ApiPrefix, StringComparison.Ordinal) && !path.StartsWith("/api/auth/", StringComparison.Ordinal) && user is null)
            {
                ctx.Response.StatusCode = 401;
                await ctx.Response.WriteAsJsonAsync(new { error = "Sign in first." });
                return;
            }
            await next();
            return;
        }
        if (!ctx.Request.Path.StartsWithSegments("/mcp"))
        {
            // The app, /healthz and the webhook: public, or authorised by their own handler.
            await next();
            return;
        }

        Identity identity;
        try
        {
            if (user is not null && ctx.Items[PlatformApi.ViaItem] as string == "key")
            {
                identity = await Task.Run(() =>
                {
                    using var conn = Db.Connect(cfg.Index.DbPath);
                    return People.ResolvePerson(conn, directory.Value, user.Email, user.GitlabUsername);
                });
            }
            else
            {
                var token = ExtractBearer(ctx.Request.Headers.Authorization.ToString());
                if (token is null)
                {
                    AuditDenied(cfg, "missing_token", path);
                    await Unauthorized(ctx, "Missing or malformed Authorization header. Expected 'Authorization: Bearer <token>'.");
                    return;
                }
                if (token.StartsWith(Users.ApiKeyPrefix, StringComparison.Ordinal))
                    throw new AclDenied("That key is not valid, or its account is disabled.");
                identity = await Task.Run(() =>
                {
                    using var conn = Db.Connect(cfg.Index.DbPath);
                    return Acl.Resolve(conn, cfg.GitLab, token);
                });
            }
        }
        catch (AclDenied exc)
        {
            AuditDenied(cfg, "token_rejected", path, exc.Message);
            await Unauthorized(ctx, exc.Message);
            return;
        }
        ctx.Items["argus.identity"] = identity;
        ctx.User = new ArgusPrincipal(identity);
        await next();
    }

    // --- webhook ------------------------------------------------------------------------

    static void MapWebhook(WebApplication app, Jobs jobs)
    {
        app.MapPost(WebhookPath, async (HttpContext ctx) =>
        {
            var supplied = ctx.Request.Headers[WebhookHeader].ToString();
            if (supplied.Length == 0 || !SecretEquals(supplied, WebhookToken()))
            {
                AuditLog.Denied("webhook_token_rejected", WebhookPath, $"header {WebhookHeader} missing or wrong");
                return Json(new JsonObject { ["error"] = "forbidden" }, 401);
            }
            JsonNode? body;
            try { body = await JsonNode.ParseAsync(ctx.Request.Body); }
            catch (JsonException) { return Json(new JsonObject { ["error"] = "body is not JSON" }, 400); }
            if (body is not JsonObject obj) return Json(new JsonObject { ["error"] = "body is not an object" }, 400);
            var kind = obj["object_kind"]?.ToString();
            if (string.IsNullOrEmpty(kind)) kind = ctx.Request.Headers["x-gitlab-event"].ToString();
            if (kind.Length > 0 && kind.ToLowerInvariant() is not ("push" or "push hook"))
                return Json(new JsonObject { ["status"] = "ignored", ["event"] = kind });
            var repo = PyStr.Strip((obj["project"] as JsonObject)?["path_with_namespace"]?.ToString() ?? "");
            if (repo.Length == 0) return Json(new JsonObject { ["error"] = "no project.path_with_namespace" }, 400);
            var after = obj["after"]?.ToString() ?? "";
            if (after.Length > 0 && after.All(c => c == '0'))
                return Json(new JsonObject { ["status"] = "ignored", ["reason"] = "ref deleted", ["repo"] = repo });
            return Json(jobs.EnqueueWebhook(repo), 202);
        });
    }

    // --- admin --------------------------------------------------------------------------

    static bool Authorised(HttpRequest request)
    {
        if (PlatformApi.CurrentUser(request.HttpContext) is { IsAdmin: true }) return true;
        if (!AdminEnabled()) return false;
        var supplied = request.Headers["x-argus-admin-token"].ToString();
        if (supplied.Length == 0) supplied = ExtractBearer(request.Headers.Authorization.ToString()) ?? "";
        return supplied.Length > 0 && SecretEquals(supplied, AdminToken());
    }

    static IResult Forbidden() => Json(new JsonObject { ["error"] = "forbidden" }, 403);

    static async Task<JsonObject> BodyOrEmpty(HttpRequest request)
    {
        try { return await JsonNode.ParseAsync(request.Body) as JsonObject ?? []; }
        catch (JsonException) { return []; }
    }

    static string Short(Exception exc) => PyStr.Prefix($"{exc.GetType().Name}: {exc.Message}", 200);

    static void MapAdmin(WebApplication app, ArgusConfig cfg, Jobs jobs)
    {
        app.MapPost(AdminPrefix + "index", async (HttpRequest request) =>
        {
            if (!Authorised(request)) return Forbidden();
            var body = await BodyOrEmpty(request);
            var branches = (body["branches"] as JsonArray ?? []).Select(b => b is JsonValue v && v.TryGetValue<string>(out var s) ? s : null)
                .Where(s => s is not null && PyStr.Strip(s).Length > 0).Cast<string>().ToList();
            var allowPartial = body["allow_partial"] is JsonValue ap && ap.TryGetValue<bool>(out var b) && b;
            if (!jobs.StartIndex(branches, allowPartial, "manual"))
                return Json(new JsonObject { ["error"] = "an index run is already in progress", ["started"] = jobs.IndexStarted() }, 409);
            return Json(new JsonObject
            {
                ["status"] = "started",
                ["branches"] = new JsonArray(branches.Select(x => (JsonNode?)x).ToArray()),
                ["allow_partial"] = allowPartial,
            });
        });

        app.MapGet(AdminPrefix + "metrics", (HttpRequest request) =>
        {
            if (!Authorised(request)) return Forbidden();
            string body;
            try { body = Metrics.Render(cfg.Index.DbPath); }
            catch (Exception exc) { body = Metrics.RenderError(exc); }
            return Results.Text(body, "text/plain; version=0.0.4; charset=utf-8");
        });

        app.MapGet(AdminPrefix + "explore", (HttpRequest request) =>
        {
            if (!Authorised(request)) return Forbidden();
            var q = PyStr.Strip(request.Query["q"].ToString());
            var repo = PyStr.Strip(request.Query["repo"].ToString());
            var limit = int.TryParse(request.Query["limit"].ToString(), out var l) ? l : Explore.DefaultLimit;
            if (request.Query["limit"].ToString().Length == 0) limit = Explore.DefaultLimit;
            try
            {
                using var conn = Db.ConnectReadonly(cfg.Index.DbPath);
                return Json(new JsonObject
                {
                    ["repos"] = Explore.Repos(conn),
                    ["symbols"] = Explore.Symbols(conn, q, repo, limit),
                    ["files"] = Explore.Files(conn, q, repo, limit),
                    ["query"] = q,
                    ["repo"] = repo,
                });
            }
            catch (Exception exc)
            {
                return Json(new JsonObject
                {
                    ["error"] = Short(exc), ["repos"] = new JsonArray(),
                    ["symbols"] = new JsonObject { ["rows"] = new JsonArray(), ["capped"] = false },
                    ["files"] = new JsonObject { ["rows"] = new JsonArray(), ["capped"] = false },
                });
            }
        });

        app.MapGet(AdminPrefix + "packs", (HttpRequest request) =>
        {
            if (!Authorised(request)) return Forbidden();
            var job = jobs.PackJobSnapshot();
            try
            {
                return Json(new JsonObject
                {
                    ["packs"] = Jobs.PackRows(cfg.PacksDir), ["job"] = job,
                    ["index_url"] = Jobs.PackIndexUrl(), ["packs_dir"] = cfg.PacksDir,
                });
            }
            catch (Exception exc)
            {
                return Json(new JsonObject { ["error"] = Short(exc), ["packs"] = new JsonArray(), ["job"] = job });
            }
        });

        app.MapPost(AdminPrefix + "packs/install", async (HttpRequest request) =>
        {
            if (!Authorised(request)) return Forbidden();
            var body = await BodyOrEmpty(request);
            var source = PyStr.Strip(body["source"]?.ToString() ?? "");
            if (source.Length == 0) return Json(new JsonObject { ["error"] = "a pack URL or path is required" }, 400);
            if (!jobs.StartPackJob("install", source: source, sha256: body["sha256"]?.ToString()))
                return Json(new JsonObject { ["error"] = "another pack operation is running" }, 409);
            return Json(new JsonObject { ["status"] = "started", ["action"] = "install", ["source"] = source });
        });

        app.MapPost(AdminPrefix + "packs/update", async (HttpRequest request) =>
        {
            if (!Authorised(request)) return Forbidden();
            var body = await BodyOrEmpty(request);
            var indexUrl = PyStr.Strip(body["index_url"]?.ToString() ?? "");
            if (indexUrl.Length == 0 && Jobs.PackIndexUrl().Length == 0)
                return Json(new JsonObject { ["error"] = "no pack index configured; set ARGUS_PACK_INDEX_URL on the argus service, or give one with the request" }, 400);
            var name = body["name"]?.ToString();
            if (!jobs.StartPackJob("update", name: name, indexUrl: indexUrl))
                return Json(new JsonObject { ["error"] = "another pack operation is running" }, 409);
            return Json(new JsonObject { ["status"] = "started", ["action"] = "update", ["name"] = name ?? "" });
        });

        app.MapPost(AdminPrefix + "packs/remove", async (HttpRequest request) =>
        {
            if (!Authorised(request)) return Forbidden();
            var body = await BodyOrEmpty(request);
            var name = PyStr.Strip(body["name"]?.ToString() ?? "");
            if (name.Length == 0) return Json(new JsonObject { ["error"] = "a pack name is required" }, 400);
            if (jobs.PackJobRunning()) return Json(new JsonObject { ["error"] = "a pack operation is running; wait for it to finish" }, 409);
            try
            {
                if (!Registry.Remove(name, cfg.PacksDir))
                    return Json(new JsonObject { ["error"] = $"no installed pack named {PyStr.Repr(name)}" }, 404);
            }
            catch (Exception exc) { return Json(new JsonObject { ["error"] = Short(exc) }, 500); }
            return Json(new JsonObject { ["status"] = "removed", ["name"] = name });
        });

        app.MapGet(AdminPrefix + "index/status", (HttpRequest request) =>
        {
            if (!Authorised(request)) return Forbidden();
            var job = jobs.IndexJobSnapshot();
            var rows = new JsonArray();
            try
            {
                using var conn = Db.ConnectReadonly(cfg.Index.DbPath);
                foreach (var r in Sql.Query(conn,
                             "SELECT path_with_namespace, branch, default_branch,       last_run_at, last_run_timed_out, last_run_symbols_failed" +
                             "  FROM repos ORDER BY last_run_at DESC NULLS LAST, path_with_namespace"))
                    rows.Add(new JsonObject
                    {
                        ["repo"] = PyJson.From(r[0]), ["branch"] = PyJson.From(r[1]), ["default_branch"] = PyJson.From(r[2]),
                        ["last_run_at"] = PyJson.From(r[3]), ["timed_out"] = Convert.ToInt64(r[4] ?? 0L) != 0,
                        ["symbols_failed"] = PyJson.From(r[5]),
                    });
            }
            catch (Exception exc) { job["repos_error"] = PyStr.Prefix(exc.ToString(), 200); }
            JsonObject summary;
            try
            {
                var snap = Metrics.Take(cfg.Index.DbPath);
                summary = new JsonObject
                {
                    ["repos"] = snap.Repos.Count, ["stale"] = snap.StaleRepos, ["errored"] = snap.ErroredRepos,
                    ["stale_after"] = snap.StaleAfterSeconds, ["version"] = snap.Version,
                    ["never_run"] = snap.Repos.Count(r => r.LastRunAt is null or 0),
                    ["files"] = snap.Repos.Sum(r => r.Files), ["symbols"] = snap.Repos.Sum(r => r.Symbols),
                    ["stale_names"] = new JsonArray(snap.Repos.Where(r => r.Stale).Select(r => (JsonNode?)$"{r.Repo}@{r.Branch}").Take(8).ToArray()),
                };
            }
            catch (Exception exc) { summary = new JsonObject { ["error"] = Short(exc) }; }
            return Json(new JsonObject
            {
                ["job"] = job, ["repos"] = rows, ["index"] = summary, ["interval"] = Jobs.IndexInterval(),
                ["webhook"] = WebhookEnabled(), ["pending"] = job["pending"]?.DeepClone() ?? new JsonArray(),
            });
        });
    }
}

/// <summary>MCP request handlers: tools/list and tools/call over the Python catalog.</summary>
public static class McpHandlers
{
    static Tools? _tools;
    static IHttpContextAccessor? _http;
    static string? _docsFindDescription;

    /// <summary>The identity for a stdio session, which has no request headers.</summary>
    public static Identity? StdioIdentity { get; set; }

    public static void Configure(Tools tools, IHttpContextAccessor? http)
    {
        _tools = tools;
        _http = http;
        _docsFindDescription = null;
    }

    static Tools T => _tools ?? throw new InvalidOperationException("tools are not configured");

    public static ValueTask<ListToolsResult> ListTools(RequestContext<ListToolsRequestParams> request, CancellationToken ct)
    {
        _docsFindDescription ??= T.DocsFindDescription(ToolCatalog.Specs.First(s => s.Name == "docs_find").Description);
        var list = ToolCatalog.Specs.Select(s => ToolRuntime.ToProtocol(s, s.Name == "docs_find" ? _docsFindDescription : null)).ToList();
        return ValueTask.FromResult(new ListToolsResult { Tools = list });
    }

    static Identity CurrentIdentity(RequestContext<CallToolRequestParams> request)
    {
        if (_http?.HttpContext?.Items["argus.identity"] is Identity fromHttp) return fromHttp;
        if (request.JsonRpcRequest.Context?.User is ArgusPrincipal p) return p.ArgusIdentity;
        if (_http?.HttpContext is null && StdioIdentity is not null) return StdioIdentity;
        throw new ToolError(_http?.HttpContext is null
            ? "No authenticated identity is available; refusing to proceed."
            : "No authenticated identity is attached to this request; refusing to proceed.");
    }

    public static async ValueTask<CallToolResult> CallTool(RequestContext<CallToolRequestParams> request, CancellationToken ct)
    {
        var name = request.Params?.Name ?? "";
        var spec = ToolCatalog.Specs.FirstOrDefault(s => s.Name == name);
        if (spec is null) return new CallToolResult { Content = [new TextContentBlock { Text = $"Unknown tool: {name}" }], IsError = true };
        try
        {
            var args = ToolRuntime.Validate(spec, request.Params?.Arguments);
            var result = await Task.Run(() => T.Dispatch(name, args, () => CurrentIdentity(request)), ct);
            return ToolRuntime.Success(spec, result);
        }
        catch (ToolError exc) { return ToolRuntime.Failure(name, exc.Message); }
        catch (QueryError exc) { return ToolRuntime.Failure(name, exc.Message); }
        catch (Exception exc)
        {
            Console.Error.WriteLine($"tool {name} failed: {exc}");
            return ToolRuntime.Failure(name, exc.Message);
        }
    }
}
