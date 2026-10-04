using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Llm.Api.Endpoints;
using Llm.Api.Identity;
using Llm.Api.Scim;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Company;

/// <summary>
/// Company sign-in: the button's label for the sign-in page, the redirect to the
/// identity provider and its answer, and the admin's view (status, a test, the SCIM token).
/// </summary>
public static partial class CompanyEndpoints
{
    public const string FlowCookie = "llm_company_sign_in";
    private const string CookiePath = "/api/auth/company";
    private static readonly TimeSpan FlowLifetime = TimeSpan.FromMinutes(10);

    /// <summary>What this browser's sign-in is waiting for, in a protected cookie until the provider answers.</summary>
    private sealed record Flow(string State, string Nonce, string Verifier, string Redirect, bool Remember);

    public static void MapCompany(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/auth/company").RequireRateLimiting("sign-in");
        // Public: the sign-in page shows the button when company sign-in is on.
        auth.MapGet("", (IOptionsMonitor<CompanySignInOptions> o) =>
            Results.Ok(new { label = o.CurrentValue.Enabled ? Label(o.CurrentValue) : null }));
        auth.MapGet("/start", StartAsync);
        auth.MapGet("/callback", CallbackAsync);

        var admin = app.MapGroup("/api/admin/company-sign-in").RequireAuthorization(AdminEndpoints.Policy);
        admin.MapGet("", StatusAsync);
        // Tries the issuer before it is saved: the form's values over the current ones.
        admin.MapPost("/test", (Dictionary<string, string?> form, IOptionsMonitor<CompanySignInOptions> current, CompanyIdp idp, CancellationToken ct) =>
        {
            var c = current.CurrentValue;
            string? F(string name, string? fallback) => form.TryGetValue(name, out var v) ? v : fallback;
            return idp.TestAsync(new CompanySignInOptions { Issuer = F("CompanySignIn:Issuer", c.Issuer)?.Trim(), ClientId = F("CompanySignIn:ClientId", c.ClientId)?.Trim() }, ct);
        });
        admin.MapPost("/scim-token", async (ScimTokens tokens, Audit audit, CancellationToken ct) =>
        {
            var token = await tokens.CreateAsync(ct);
            await audit.WriteAsync("scim.token", detail: "a new token; any earlier one stopped working");
            return Results.Ok(new { token });
        });
        admin.MapDelete("/scim-token", async (ScimTokens tokens, Audit audit, CancellationToken ct) =>
        {
            if (await tokens.RevokeAsync(ct))
            {
                await audit.WriteAsync("scim.token_revoke", detail: "SCIM is off");
            }
            return Results.NoContent();
        });
    }

    public static string Label(CompanySignInOptions o) => string.IsNullOrWhiteSpace(o.ButtonLabel) ? "your company account" : o.ButtonLabel.Trim();

    /// <summary>The redirect URI to register at the identity provider.</summary>
    public static string Callback(AuthOptions auth) => auth.Origin + CookiePath + "/callback";

    private static ITimeLimitedDataProtector Protector(IDataProtectionProvider dp) => dp.CreateProtector("company-sign-in").ToTimeLimitedDataProtector();

    private static async Task<IResult> StartAsync(HttpContext ctx, string? rd, bool? remember, CompanyIdp idp, IOptionsMonitor<CompanySignInOptions> options,
        IOptions<AuthOptions> auth, IDataProtectionProvider dp, Audit audit, ILogger<CompanyIdp> logger)
    {
        var o = options.CurrentValue;
        var redirect = Redirects.Safe(rd, auth.Value.Domain);
        if (!o.Enabled)
        {
            return Results.Redirect(Login("company_off", redirect));
        }
        var flow = new Flow(CompanyIdp.Random(), CompanyIdp.Random(), CompanyIdp.Random(), redirect, remember == true);
        string url;
        try
        {
            url = await idp.AuthorizeUrlAsync(o, Callback(auth.Value), flow.State, flow.Nonce, flow.Verifier, ctx.RequestAborted);
        }
        catch (CompanyIdpException ex)
        {
            LogUnavailable(logger, ex.Message);
            await audit.WriteAsync("sign_in", success: false, detail: "company sign-in: " + ex.Message);
            return Results.Redirect(Login("company_unavailable", redirect));
        }
        ctx.Response.Cookies.Append(FlowCookie, Protector(dp).Protect(JsonSerializer.Serialize(flow), FlowLifetime), CookieOptions(FlowLifetime));
        return Results.Redirect(url);
    }

    private static async Task<IResult> CallbackAsync(HttpContext ctx, CompanyIdp idp, CompanySignIn signIn, IOptionsMonitor<CompanySignInOptions> options,
        IOptions<AuthOptions> auth, IDataProtectionProvider dp, Audit audit, ILogger<CompanyIdp> logger)
    {
        var q = ctx.Request.Query;
        var flow = ReadFlow(ctx, dp);
        ctx.Response.Cookies.Delete(FlowCookie, CookieOptions(TimeSpan.Zero));
        // The state ties the answer to the browser that asked: nobody can slip their own sign-in into someone else's.
        if (flow is null || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(q["state"].ToString()), Encoding.UTF8.GetBytes(flow.State)))
        {
            await audit.WriteAsync("sign_in", success: false, detail: "company sign-in: the answer is not for a sign-in this browser started (or it took over 10 minutes)");
            return Results.Redirect(Login("company_failed", flow?.Redirect ?? "/"));
        }
        if (q["error"].ToString() is { Length: > 0 } error)
        {
            await audit.WriteAsync("sign_in", success: false, detail: $"company sign-in: the identity provider answered {error} {q["error_description"]}".TrimEnd());
            return Results.Redirect(Login(error == "access_denied" ? "company_cancelled" : "company_failed", flow.Redirect));
        }
        var o = options.CurrentValue;
        if (!o.Enabled)
        {
            return Results.Redirect(Login("company_off", flow.Redirect));
        }
        CompanyPerson person;
        try
        {
            person = await idp.RedeemAsync(o, q["code"].ToString(), Callback(auth.Value), flow.Verifier, flow.Nonce, ctx.RequestAborted);
        }
        catch (CompanyIdpException ex)
        {
            if (ex.Unavailable)
            {
                LogUnavailable(logger, ex.Message);
            }
            await audit.WriteAsync("sign_in", success: false, detail: "company sign-in: " + ex.Message);
            return Results.Redirect(Login(ex.Unavailable ? "company_unavailable" : "company_failed", flow.Redirect));
        }
        return await signIn.SignInAsync(person, flow.Remember) switch
        {
            CompanyOutcome.Success => Results.Redirect(flow.Redirect),
            CompanyOutcome.NotAllowed => Results.Redirect(Login("company_not_allowed", flow.Redirect)),
            CompanyOutcome.Disabled => Results.Redirect(Login("company_disabled", flow.Redirect)),
            _ => Results.Redirect(Login("company_refused", flow.Redirect)),
        };
    }

    private static Flow? ReadFlow(HttpContext ctx, IDataProtectionProvider dp)
    {
        if (ctx.Request.Cookies[FlowCookie] is not { Length: > 0 } cookie)
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<Flow>(Protector(dp).Unprotect(cookie));
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException)
        {
            return null; // tampered with, or expired
        }
    }

    private static CookieOptions CookieOptions(TimeSpan life) => new()
    {
        HttpOnly = true,
        Secure = true,
        // Lax: the provider's answer is a top-level navigation from another site, and must carry it.
        SameSite = SameSiteMode.Lax,
        Path = CookiePath,
        MaxAge = life,
        IsEssential = true,
    };

    /// <summary>Back to the sign-in page, which words the error; where the person was headed is kept for the next try.</summary>
    private static string Login(string error, string redirect) =>
        QueryHelpers.AddQueryString("/login", redirect == "/" ? new Dictionary<string, string?> { ["error"] = error } : new() { ["error"] = error, ["rd"] = redirect });

    private static async Task<IResult> StatusAsync(IOptionsMonitor<CompanySignInOptions> options, IOptions<AuthOptions> auth, ScimTokens tokens, AppDbContext db, CancellationToken ct)
    {
        var o = options.CurrentValue;
        return Results.Ok(new
        {
            enabled = o.Enabled,
            issuer = string.IsNullOrWhiteSpace(o.Issuer) ? null : o.Issuer.Trim(),
            label = Label(o),
            adminGroup = string.IsNullOrWhiteSpace(o.AdminGroup) ? null : o.AdminGroup.Trim(),
            requiredGroup = string.IsNullOrWhiteSpace(o.RequiredGroup) ? null : o.RequiredGroup.Trim(),
            redirectUri = Callback(auth.Value),
            people = await db.Users.CountAsync(u => u.Source == UserSource.Oidc, ct),
            scim = new
            {
                url = auth.Value.Origin + ScimEndpoints.Base,
                tokenMadeAt = await tokens.MadeAtAsync(ct),
                groups = await db.Groups.CountAsync(g => g.Scim, ct),
            },
        });
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Company sign-in: {Reason}")]
    private static partial void LogUnavailable(ILogger logger, string reason);
}
