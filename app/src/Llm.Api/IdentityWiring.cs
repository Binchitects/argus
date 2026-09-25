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
        services.Replace(ServiceDescriptor.Scoped<IPasswordHasher<AppUser>, LegacyAwarePasswordHasher>());

        services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();
        services.ConfigureApplicationCookie(o =>
        {
            o.Cookie.Name = "llm_session";
            // The parent domain: one sign-in covers the admin panel and the other subdomains.
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
        services.Configure<Settings.SettingsFileOptions>(config.GetSection("Settings"));
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

        services.Configure<Operations.StackOptions>(config.GetSection("Stack"));
        services.Configure<Operations.ArgusOptions>(config.GetSection("Argus"));
        services.PostConfigure<Operations.ArgusOptions>(o =>
        {
            var profiles = config["Stack:ComposeProfiles"];
            o.Deployed = string.IsNullOrWhiteSpace(profiles) ||
                profiles.Split(',', StringSplitOptions.TrimEntries).Contains("argus", StringComparer.OrdinalIgnoreCase);
        });
        services.AddHttpClient("probe", c => c.Timeout = TimeSpan.FromSeconds(3));
        services.AddHttpClient<Operations.ArgusAdmin>(c => c.Timeout = TimeSpan.FromSeconds(15));

        services.Configure<Chat.ChatOptions>(config.GetSection("Chat"));
        services.PostConfigure<Chat.ChatOptions>(o =>
        {
            if (config["Gateway:Url"] is { Length: > 0 } url && config["Chat:GatewayUrl"] is null)
            {
                o.GatewayUrl = url;
            }
        });
        services.AddHttpClient<Chat.GatewayChat>((sp, c) =>
        {
            var o = sp.GetRequiredService<IOptions<Chat.ChatOptions>>().Value;
            c.BaseAddress = new Uri(o.GatewayUrl);
            // A long answer, with thinking, can take minutes; the stream itself is the progress.
            c.Timeout = o.RequestTimeout;
        });
        services.AddSingleton<Chat.ChatKey>();
        services.AddSingleton<Chat.ChatModels>();
        services.AddHttpClient<Chat.ArgusMcp>(c => c.Timeout = TimeSpan.FromMinutes(2));
        services.AddHttpClient(Chat.Tools.ToolRegistry.McpClient, c => c.Timeout = TimeSpan.FromMinutes(2));
        services.AddScoped<Chat.Tools.ArgusTool>();
        services.AddScoped<Chat.Tools.ImageTool>();
        services.AddSingleton<Chat.Tools.CalculatorTool>();
        services.AddSingleton<Chat.Tools.TimeTool>();
        services.AddScoped<Chat.Tools.FilesTool>();
        services.Configure<Chat.Tools.SandboxOptions>(config.GetSection("Sandbox"));
        services.AddSingleton<Chat.Tools.SandboxClient>();
        services.AddScoped<Chat.Tools.PythonTool>();
        services.Configure<Chat.Tools.WebOptions>(config.GetSection("Web"));
        services.PostConfigure<Chat.Tools.WebOptions>(o =>
        {
            // The websearch profile's own SearXNG, unless an admin set another.
            var profiles = config["Stack:ComposeProfiles"] ?? "";
            if (string.IsNullOrWhiteSpace(o.SearchUrl) && profiles.Split(',', StringSplitOptions.TrimEntries).Contains("websearch", StringComparer.OrdinalIgnoreCase))
            {
                o.SearchUrl = "http://searxng:8080";
            }
        });
        services.AddSingleton<Chat.Tools.WebResolver>();
        services.AddHttpClient(Chat.Tools.WebFetcher.Client).ConfigurePrimaryHttpMessageHandler(sp => Chat.Tools.WebFetcher.Handler(sp.GetRequiredService<Chat.Tools.WebResolver>()));
        services.AddHttpClient(Chat.Tools.WebFetcher.SearchClient, c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddSingleton<Chat.Tools.WebFetcher>();
        services.AddScoped<Chat.Tools.WebTool>();
        services.AddScoped<Chat.Tools.ToolRegistry>();
        services.AddSingleton<Chat.Tools.ToolApprovals>();
        services.AddScoped<Chat.ChatService>();

        // The engine's models (llama.cpp's router): Admin -> Models, and who may use which model.
        services.Configure<Models.EngineOptions>(config.GetSection("Engine"));
        services.PostConfigure<Models.EngineOptions>(o =>
        {
            if (config["Engine:Enabled"] is null)
            {
                var profiles = config["Stack:ComposeProfiles"] ?? "";
                o.Enabled = profiles.Split(',', StringSplitOptions.TrimEntries).Contains("llamacpp", StringComparer.OrdinalIgnoreCase);
            }
            o.DefaultModel ??= config["Stack:ModelName"];
        });
        services.AddHttpClient<Models.EngineClient>(c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddSingleton<Models.EngineState>();
        services.AddSingleton<Models.ModelLibrary>();
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
            if (ctx.Request.Path.StartsWithSegments("/api") && !ctx.Request.Path.StartsWithSegments("/api/authz") &&
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
        app.MapOidc();
        Dashboards.DashboardEndpoints.MapDashboards(app);
        Dashboards.UsageEndpoints.MapUsage(app);
        Operations.OperationsEndpoints.MapOperations(app);
        Settings.SettingsEndpoints.MapSettings(app);
        Chat.ChatEndpoints.MapChat(app);
        Chat.Tools.ToolEndpoints.MapTools(app);
        Models.ModelEndpoints.MapModels(app);
    }

    public static async Task BootstrapIdentityAsync(this WebApplication app)
    {
        CheckKeyRing(app.Services);
        await using var scope = app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IdentityBootstrap>().RunAsync();
        await scope.ServiceProvider.GetRequiredService<OidcClients>().RunAsync();
    }
}
