using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using System.Threading.RateLimiting;
using Llm.Api.Endpoints;
using Llm.Api.Gateway;
using Llm.Api.Identity;
using Llm.Api.Ldap;
using Llm.Api.Oidc;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OpenIddict.Server;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Llm.Api;

public static class IdentityWiring
{
    public static void AddAppIdentity(this WebApplicationBuilder builder)
    {
        var services = builder.Services;
        var config = builder.Configuration;
        var auth = config.GetSection("Auth").Get<AuthOptions>() ?? new AuthOptions();
        var oidc = config.GetSection("Oidc").Get<OidcOptions>() ?? new OidcOptions();

        services.Configure<AuthOptions>(config.GetSection("Auth"));
        services.Configure<LdapOptions>(config.GetSection("Ldap"));
        services.Configure<OidcOptions>(config.GetSection("Oidc"));
        services.Configure<LiteLlmOptions>(config.GetSection("Gateway"));
        services.AddHttpContextAccessor();
        services.TryAddSingleton(TimeProvider.System);

        var dataProtection = services.AddDataProtection().PersistKeysToDbContext<AppDbContext>().SetApplicationName("llm-app");
        if (!string.IsNullOrEmpty(auth.DataKey))
        {
            var ringKey = KeyRingEncryptor.Derive(auth.DataKey);
            dataProtection.AddKeyManagementOptions(o => o.XmlEncryptor = new KeyRingEncryptor(ringKey));
        }

        services.AddIdentityCore<AppUser>(o =>
            {
                o.User.RequireUniqueEmail = true;
                // Strength is judged by zxcvbn (StrongPasswordValidator), not by character classes.
                o.Password.RequiredLength = 10;
                o.Password.RequireDigit = false;
                o.Password.RequireLowercase = false;
                o.Password.RequireUppercase = false;
                o.Password.RequireNonAlphanumeric = false;
                o.Password.RequiredUniqueChars = 1;
                o.Lockout.AllowedForNewUsers = true;
                o.Lockout.MaxFailedAccessAttempts = 10;
                o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            })
            .AddRoles<AppRole>()
            .AddEntityFrameworkStores<AppDbContext>()
            .AddSignInManager<AppSignInManager>()
            .AddDefaultTokenProviders()
            .AddClaimsPrincipalFactory<AppClaimsFactory>()
            .AddPasswordValidator<StrongPasswordValidator>();

        services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();
        services.ConfigureApplicationCookie(o =>
        {
            o.Cookie.Name = "llm_session";
            // The parent domain: one sign-in covers every subdomain.
            o.Cookie.Domain = auth.Domain;
            o.Cookie.HttpOnly = true;
            o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.ExpireTimeSpan = auth.SessionIdle;
            o.SlidingExpiration = true;
            o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
            o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
            o.Events.OnSigningIn = ctx =>
            {
                if (ctx.Properties.IsPersistent)
                {
                    ctx.Properties.ExpiresUtc = DateTimeOffset.UtcNow + auth.RememberMe;
                }
                return Task.CompletedTask;
            };
            o.Events.OnValidatePrincipal = async ctx =>
            {
                await SecurityStampValidator.ValidatePrincipalAsync(ctx);
                if (ctx.Principal is null)
                {
                    return;
                }
                // The absolute cap: activity keeps a session alive, but not past this.
                var limit = ctx.Properties.IsPersistent ? auth.RememberMe : auth.SessionMax;
                if (AppClaimsFactory.SignedInAt(ctx.Principal) is not { } at || DateTimeOffset.UtcNow - at > limit)
                {
                    ctx.RejectPrincipal();
                    await ctx.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
                }
            };
        });
        services.ConfigureExternalCookie(o => o.Cookie.SecurePolicy = CookieSecurePolicy.Always);
        services.Configure<CookieAuthenticationOptions>(IdentityConstants.TwoFactorUserIdScheme, o =>
        {
            o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            o.ExpireTimeSpan = TimeSpan.FromMinutes(5);
        });
        services.Configure<SecurityStampValidatorOptions>(o =>
        {
            // A disabled person, or a reset password, ends sessions within a minute.
            o.ValidationInterval = auth.SessionRecheck;
            o.OnRefreshingPrincipal = ctx =>
            {
                foreach (var type in new[] { AppClaimsFactory.AuthTime, "amr" })
                {
                    var old = ctx.CurrentPrincipal?.FindFirst(type);
                    var id = ctx.NewPrincipal?.Identity as ClaimsIdentity;
                    if (old is not null && id is not null)
                    {
                        foreach (var fresh in id.FindAll(type).ToList())
                        {
                            id.RemoveClaim(fresh);
                        }
                        id.AddClaim(new Claim(type, old.Value));
                    }
                }
                return Task.CompletedTask;
            };
        });
        services.AddAuthorizationBuilder()
            .AddPolicy(AdminEndpoints.Policy, p => p.AddAuthenticationSchemes(IdentityConstants.ApplicationScheme).RequireRole(Roles.Admin));

        services.AddOpenIddict()
            .AddCore(o => o.UseEntityFrameworkCore().UseDbContext<AppDbContext>().ReplaceDefaultEntities<Guid>())
            .AddServer(o =>
            {
                o.SetIssuer(new Uri(auth.Origin + "/"));
                o.SetAuthorizationEndpointUris("connect/authorize")
                 .SetTokenEndpointUris("connect/token")
                 .SetUserInfoEndpointUris("connect/userinfo")
                 .SetEndSessionEndpointUris("connect/logout");
                o.AllowAuthorizationCodeFlow().AllowRefreshTokenFlow().AllowClientCredentialsFlow();
                o.RegisterScopes(Scopes.OpenId, Scopes.Email, Scopes.Profile, Scopes.OfflineAccess, OidcScopes.Groups, OidcScopes.Api);
                o.RegisterClaims(Claims.Subject, Claims.Name, Claims.PreferredUsername, Claims.Email, Claims.EmailVerified, OidcScopes.Groups);
                // Plain signed JWTs, so any resource server can check them against the published keys.
                o.DisableAccessTokenEncryption();
                o.SetAccessTokenLifetime(oidc.AccessTokenLifetime);
                o.SetIdentityTokenLifetime(oidc.AccessTokenLifetime);
                o.SetRefreshTokenLifetime(oidc.RefreshTokenLifetime);
                o.UseAspNetCore()
                 .EnableAuthorizationEndpointPassthrough()
                 .EnableTokenEndpointPassthrough()
                 .EnableUserInfoEndpointPassthrough()
                 .EnableEndSessionEndpointPassthrough();
            })
            .AddValidation(o =>
            {
                o.UseLocalServer();
                o.UseAspNetCore();
            });
        services.AddSingleton<IConfigureOptions<OpenIddictServerOptions>, OidcKeys>();

        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            // A flood guard only: guessing is stopped by LoginThrottle and account lockout.
            // Generous, because a whole office signing in at 9:00 shares one NAT address.
            o.AddPolicy("sign-in", ctx => RateLimitPartition.GetFixedWindowLimiter(
                ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1) }));
            o.OnRejected = async (ctx, ct) =>
            {
                ctx.HttpContext.Response.Headers.RetryAfter = "60";
                await ctx.HttpContext.Response.WriteAsJsonAsync(
                    new { status = "rate_limited", error = "Too many requests from your address. Wait a minute and try again." }, ct);
            };
            o.AddPolicy("token", ctx => RateLimitPartition.GetFixedWindowLimiter(
                ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 300, Window = TimeSpan.FromMinutes(1) }));
        });

        services.AddExceptionHandler<GatewayExceptionHandler>();
        services.AddHttpClient<ILiteLlm, LiteLlmClient>((sp, c) =>
        {
            var o = sp.GetRequiredService<IOptions<LiteLlmOptions>>().Value;
            c.BaseAddress = new Uri(o.Url);
            // Admin calls are small; a gateway that needs longer is down for our purposes.
            c.Timeout = TimeSpan.FromSeconds(10);
            if (!string.IsNullOrEmpty(o.MasterKey))
            {
                c.DefaultRequestHeaders.Authorization = new("Bearer", o.MasterKey);
            }
        });

        services.Configure<ThrottleOptions>(config.GetSection("Throttle"));
        services.Configure<Settings.BrandingOptions>(config.GetSection("Branding"));
        services.AddSingleton<Settings.SettingsAtStart>();
        services.AddSingleton<Settings.IAppRestarter, Settings.AppRestarter>();
        services.AddScoped<Settings.SettingsService>();
        services.AddSingleton<LoginThrottle>();
        services.AddSingleton<ILdapDirectory, LdapDirectory>();
        services.AddSingleton<LdapSync>();
        services.AddHostedService(sp => sp.GetRequiredService<LdapSync>());
        services.AddScoped<Audit>();
        services.AddScoped<DirectoryFile>();
        services.AddScoped<PeopleService>();
        services.AddScoped<Access.AccessService>();
        services.AddScoped<SignInService>();
        services.AddScoped<PersonClaims>();
        services.AddScoped<IdentityBootstrap>();
        services.AddScoped<OidcClients>();

        services.Configure<Dashboards.DashboardOptions>(config.GetSection("Dashboards"));
        services.AddSingleton<Dashboards.DashboardStore>();
        services.AddSingleton<Dashboards.SqlDatasource>();
        services.AddScoped<Gateway.Ledger>();
        services.AddScoped<Notifications.Notifier>();
        services.AddScoped<Chat.DocumentPages>();
        services.AddSingleton<Notifications.NewsWatch>();
        services.AddHostedService(sp => sp.GetRequiredService<Notifications.NewsWatch>());
        services.AddHttpClient<Dashboards.PromDatasource>(c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddHttpClient<Dashboards.LokiDatasource>(c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddHttpClient<Dashboards.AlertmanagerClient>(c => c.Timeout = TimeSpan.FromSeconds(15));

        services.Configure<Operations.StackOptions>(config.GetSection("Stack"));
        services.Configure<Operations.ArgusOptions>(config.GetSection("Argus"));
        services.AddSingleton<Operations.Modules>();
        services.AddHttpClient("probe", c => c.Timeout = TimeSpan.FromSeconds(3));
        services.AddHttpClient<Operations.ArgusAdmin>(c => c.Timeout = TimeSpan.FromSeconds(15));
        services.AddScoped<Operations.ArgusWebhook>();

        services.Configure<Chat.ChatOptions>(config.GetSection("Chat"));
        services.Configure<Quality.QualityOptions>(config.GetSection("Quality"));
        services.Configure<Safeguards.SafeguardOptions>(config.GetSection("Safeguards"));
        services.AddScoped<Safeguards.Safeguards>();
        services.AddGovernance(config);
        services.PostConfigure<Chat.ChatOptions>(o =>
        {
            if (config["Gateway:Url"] is { Length: > 0 } url && config["Chat:GatewayUrl"] is null)
            {
                o.GatewayUrl = url;
            }
        });
        services.AddSingleton<Chat.AnswerGate>();
        services.AddHttpClient<Chat.GatewayChat>((sp, c) =>
        {
            var o = sp.GetRequiredService<IOptions<Chat.ChatOptions>>().Value;
            c.BaseAddress = new Uri(o.GatewayUrl);
            // A long answer, with thinking, can take minutes; the stream itself is the progress.
            c.Timeout = o.RequestTimeout;
        });
        services.AddSingleton<Chat.ChatKey>();
        services.AddSingleton<Chat.ChatModels>();
        // A tool call may run for an hour (Chat:ToolCallTimeout): each request sets its own limit.
        services.AddHttpClient<Chat.ArgusMcp>(c => c.Timeout = Timeout.InfiniteTimeSpan).ConfigurePrimaryHttpMessageHandler(Chat.Mcp.Handler);
        services.AddHttpClient(Chat.Tools.ToolRegistry.McpClient, c => c.Timeout = Timeout.InfiniteTimeSpan).ConfigurePrimaryHttpMessageHandler(Chat.Mcp.Handler);
        services.AddScoped<Chat.Tools.ArgusTool>();
        services.AddScoped<Chat.Tools.ImageTool>();
        services.AddScoped<Chat.Tools.VideoTool>();
        services.AddScoped<Chat.Tools.SpeechTool>();
        services.AddScoped<Chat.Media>();
        services.AddHttpClient(Chat.Tools.VideoTool.Client, c => c.Timeout = TimeSpan.FromMinutes(2));
        services.AddSingleton<Chat.Tools.CalculatorTool>();
        services.AddSingleton<Chat.Tools.TimeTool>();
        services.AddSingleton<Chat.Tools.AskTool>();
        services.AddSingleton<Chat.Tools.AgentsTool>();
        services.AddScoped<Chat.Tools.FilesTool>();
        services.Configure<Chat.Tools.SandboxOptions>(config.GetSection("Sandbox"));
        services.AddSingleton<Chat.Tools.SandboxClient>();
        services.AddScoped<Chat.Tools.PythonTool>();
        services.Configure<Chat.Tools.WebOptions>(config.GetSection("Web"));
        services.Configure<Plugins.PluginOptions>(config.GetSection("Plugins"));
        services.Configure<Schedules.GitLabOptions>(config.GetSection("GitLab"));
        services.AddSingleton<Schedules.GitLabBot>();
        services.AddHttpClient(Schedules.GitLabBot.Client, c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddSingleton<Plugins.PluginCatalog>();
        services.AddScoped<Plugins.PersonCredentials>();
        services.AddHttpClient(Plugins.PluginCatalog.Client, c => c.Timeout = TimeSpan.FromSeconds(60));
        services.AddSingleton<Chat.Tools.WebResolver>();
        services.AddHttpClient(Chat.Tools.WebFetcher.Client).ConfigurePrimaryHttpMessageHandler(sp => Chat.Tools.WebFetcher.Handler(sp.GetRequiredService<Chat.Tools.WebResolver>()));
        services.AddHttpClient(Chat.Tools.WebFetcher.SearchClient, c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddSingleton<Chat.Tools.WebFetcher>();
        services.AddSingleton<Chat.Tools.WebPageCache>();
        services.AddScoped<Chat.Tools.WebTool>();
        services.AddScoped<Chat.Tools.ToolRegistry>();
        Knowledge.KnowledgeWiring.AddKnowledge(services, config);
        services.AddSingleton<Chat.Tools.ToolApprovals>();
        services.AddScoped<Chat.ChatService>();
        services.AddScoped<Chat.SmallModel>();
        services.AddScoped<Chat.AutoModel>();
        services.AddSingleton<Chat.ChatTitles>();
        services.AddScoped<Settings.ISettingWarning>(sp => sp.GetRequiredService<Chat.SmallModel>());
        // What a person asked the chat to remember, and the prompt library.
        services.Configure<Chat.MemoryOptions>(config.GetSection("Memory"));
        services.AddScoped<Chat.Memories>();
        services.AddScoped<Chat.Tools.MemoryTool>();
        ArenaMcp.McpEndpoints.AddArenaMcp(services, config);
        // Answers outlive the page that asked: they run here, and a page re-attaches.
        services.AddSingleton<Chat.AnswerJobs>();
        services.AddHostedService(sp => sp.GetRequiredService<Chat.AnswerJobs>());

        // The engine's models (llama.cpp's router): Admin -> Models, and who may use which model.
        services.Configure<Models.EngineOptions>(config.GetSection("Engine"));
        services.Configure<Models.ModelHoursOptions>(config.GetSection("ModelHours"));
        services.Configure<Models.HuggingFaceOptions>(config.GetSection("HuggingFace"));
        // Downloads are hours long for a large model: the client has no timeout of its own (ModelDownloads cuts off a read that stalls).
        services.AddHttpClient<Models.HuggingFace>(c => c.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(20), PooledConnectionLifetime = TimeSpan.FromMinutes(10) });
        services.AddSingleton<Models.ModelDownloads>();
        services.AddHostedService(sp => sp.GetRequiredService<Models.ModelDownloads>());
        // A new deployment's models, fetched by the app itself (MODEL in .env, and the picture, video and speech servers').
        services.AddHttpClient(Models.Provisioning.Client, c => c.Timeout = TimeSpan.FromMinutes(30));
        services.AddHostedService<Models.Provisioning>();
        services.AddHttpClient(Models.MediaControl.Client, c => c.Timeout = TimeSpan.FromSeconds(60));
        services.AddSingleton<Models.MediaControl>();
        services.AddHostedService(sp => sp.GetRequiredService<Models.MediaControl>());
        services.Configure<Schedules.ScheduleOptions>(config.GetSection("Schedules"));
        services.Configure<Operations.ArgusIndexOptions>(config.GetSection("ArgusIndex"));
        services.AddHostedService<Operations.ArgusIndexSchedule>();
        services.Configure<Schedules.MailOptions>(config.GetSection("Mail"));
        services.AddSingleton<Schedules.Mailer>();
        services.AddSingleton<Schedules.Webhooks>();
        services.AddHttpClient(Schedules.Webhooks.Client, c => c.Timeout = TimeSpan.FromSeconds(15));
        services.AddScoped<Schedules.TaskRunner>();
        services.AddScoped<Schedules.TaskEndpoints.TaskContext>();
        services.AddSingleton<Schedules.Scheduler>();
        services.AddHostedService(sp => sp.GetRequiredService<Schedules.Scheduler>());
        services.AddSingleton<Models.ModelHoursState>();
        services.AddScoped<Models.ModelHours>();
        services.AddHttpClient<Models.EngineClient>(c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddSingleton<Models.EngineState>();
        services.AddSingleton<Models.ModelLibrary>();
        services.AddSingleton<Models.HardwareProbe>();
        // Other GPU servers: their certificates are checked against the system's roots; a server
        // an admin marked unchecked uses the other client.
        services.AddHttpClient(Models.RemoteServerClient.Client);
#pragma warning disable CA5359 // Only for a server an admin marked "do not check its certificate" (a self-signed one with no CA to trust); the page says what that gives up.
        services.AddHttpClient(Models.RemoteServerClient.Unchecked).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true },
        });
#pragma warning restore CA5359
        services.AddSingleton<Models.RemoteServerClient>();
        services.AddSingleton<Models.RemoteHealth>();
        services.AddScoped<Models.ModelCatalog>();
        services.AddScoped<Models.ModelPolicy>();
        services.AddScoped<Models.KeyAccess>();
        services.AddSingleton<Models.KeyAccessWatcher>();
        services.AddHostedService(sp => sp.GetRequiredService<Models.KeyAccessWatcher>());
        services.AddSingleton<Models.EngineWatcher>();
        services.AddHostedService(sp => sp.GetRequiredService<Models.EngineWatcher>());
    }

    /// <summary>
    /// Refuses to start when stored keys cannot be read (APP_DATA_KEY changed or
    /// lost). Data Protection would otherwise drop them and quietly make new ones:
    /// everyone signed out, and the OIDC signing key unreadable.
    /// </summary>
    private static void CheckKeyRing(IServiceProvider services)
    {
        var keys = services.GetRequiredService<Microsoft.AspNetCore.DataProtection.KeyManagement.IKeyManager>();
        foreach (var key in keys.GetAllKeys().Where(k => !k.IsRevoked && k.ExpirationDate > DateTimeOffset.UtcNow))
        {
            try
            {
                _ = key.Descriptor;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Security.Cryptography.CryptographicException)
            {
                throw new InvalidOperationException(
                    "The stored sign-in keys cannot be decrypted. APP_DATA_KEY in .env must be the value used at the first start " +
                    "(it never changes). Restore it; removing it or making a new one signs everyone out and breaks single sign-on.", ex);
            }
        }
        // Loads (or creates) the OIDC keys now rather than on the first request.
        try
        {
            _ = services.GetRequiredService<IOptions<OpenIddictServerOptions>>().Value;
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            throw new InvalidOperationException("The OIDC signing key cannot be decrypted: APP_DATA_KEY differs from the first start.", ex);
        }
    }

    /// <summary>
    /// State-changing API calls must carry X-Requested-With. A browser only sends
    /// a custom header cross-origin after a CORS preflight this app never approves,
    /// so another site (or a sibling subdomain) cannot make them on someone's behalf.
    /// </summary>
    public static IApplicationBuilder UseCsrfGuard(this IApplicationBuilder app) =>
        app.Use(async (ctx, next) =>
        {
            var m = ctx.Request.Method;
            // Events for tasks (/api/hooks) come from other systems, with no session: their secret is the guard.
            if (ctx.Request.Path.StartsWithSegments("/api") && !ctx.Request.Path.StartsWithSegments("/api/authz") && !ctx.Request.Path.StartsWithSegments("/api/hooks") &&
                !(HttpMethods.IsGet(m) || HttpMethods.IsHead(m) || HttpMethods.IsOptions(m)) &&
                !ctx.Request.Headers.ContainsKey("X-Requested-With"))
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsJsonAsync(new { status = "csrf", error = "Missing X-Requested-With header." });
                return;
            }
            await next(ctx);
        });

    public static void MapAppIdentity(this WebApplication app)
    {
        app.MapAuth();
        app.MapAccount();
        app.MapAdmin();
        Access.GroupEndpoints.MapGroups(app);
        app.MapForwardAuth();
        app.MapKeyCheck();
        app.MapOidc();
        Dashboards.DashboardEndpoints.MapDashboards(app);
        Dashboards.UsageEndpoints.MapUsage(app);
        Dashboards.LogEndpoints.MapLogs(app);
        Dashboards.AlertEndpoints.MapAlerts(app);
        Operations.OperationsEndpoints.MapOperations(app);
        Settings.SettingsEndpoints.MapSettings(app);
        Chat.ChatEndpoints.MapChat(app);
        Chat.ProjectEndpoints.MapProjects(app);
        Chat.TraceEndpoints.MapTraces(app);
        Chat.MemoryEndpoints.MapMemories(app);
        Chat.PromptEndpoints.MapPrompts(app);
        Quality.QualityEndpoints.MapQuality(app);
        Chat.Tools.ToolEndpoints.MapTools(app);
        Plugins.PluginEndpoints.MapPlugins(app);
        ArenaMcp.McpEndpoints.MapArenaMcp(app);
        Knowledge.KnowledgeEndpoints.MapKnowledge(app);
        Models.ModelEndpoints.MapModels(app);
        Models.ModelHoursEndpoints.MapModelHours(app);
        Models.HuggingFaceEndpoints.MapHuggingFace(app);
        Schedules.TaskEndpoints.MapTasks(app);
        Models.RemoteServerEndpoints.MapRemoteServers(app);
        app.MapGovernance();
    }

    public static async Task BootstrapIdentityAsync(this WebApplication app)
    {
        CheckKeyRing(app.Services);
        await using var scope = app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IdentityBootstrap>().RunAsync();
        await scope.ServiceProvider.GetRequiredService<OidcClients>().RunAsync();
    }
}
