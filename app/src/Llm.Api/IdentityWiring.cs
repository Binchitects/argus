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

        services.AddDataProtection().PersistKeysToDbContext<AppDbContext>().SetApplicationName("llm-app");

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
            o.AddPolicy("sign-in", ctx => RateLimitPartition.GetFixedWindowLimiter(
                ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1) }));
            o.AddPolicy("token", ctx => RateLimitPartition.GetFixedWindowLimiter(
                ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 300, Window = TimeSpan.FromMinutes(1) }));
        });

        services.AddHttpClient<ILiteLlm, LiteLlmClient>((sp, c) =>
        {
            var o = sp.GetRequiredService<IOptions<LiteLlmOptions>>().Value;
            c.BaseAddress = new Uri(o.Url);
            c.Timeout = TimeSpan.FromSeconds(30);
            if (!string.IsNullOrEmpty(o.MasterKey))
            {
                c.DefaultRequestHeaders.Authorization = new("Bearer", o.MasterKey);
            }
        });

        services.AddSingleton<LoginThrottle>();
        services.AddSingleton<ILdapDirectory, LdapDirectory>();
        services.AddSingleton<LdapSync>();
        services.AddHostedService(sp => sp.GetRequiredService<LdapSync>());
        services.AddScoped<Audit>();
        services.AddScoped<DirectoryFile>();
        services.AddScoped<PeopleService>();
        services.AddScoped<SignInService>();
        services.AddScoped<PersonClaims>();
        services.AddScoped<IdentityBootstrap>();
        services.AddScoped<OidcClients>();
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
        app.MapForwardAuth();
        app.MapOidc();
    }

    public static async Task BootstrapIdentityAsync(this WebApplication app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IdentityBootstrap>().RunAsync();
        await scope.ServiceProvider.GetRequiredService<OidcClients>().RunAsync();
    }
}
