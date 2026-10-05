using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Llm.Api.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace Llm.Api.Company;

/// <summary>
/// Company sign-in by SAML 2.0: this app's metadata for the identity provider, the
/// sign-in request (HTTP-Redirect) and the provider's answer at the ACS (HTTP-POST).
/// </summary>
public static partial class CompanyEndpoints
{
    public const string SamlFlowCookie = "llm_company_saml";
    private const string SamlPath = CookiePath + "/saml";

    /// <summary>What this browser's SAML sign-in is waiting for: the request's ID (the answer's InResponseTo) and the RelayState sent with it.</summary>
    private sealed record SamlFlow(string RequestId, string RelayState, string Redirect, bool Remember);

    /// <summary>Where the identity provider posts its answers (the Reply URL, Assertion Consumer Service).</summary>
    public static string AcsUrl(AuthOptions auth) => auth.Origin + SamlPath + "/acs";

    /// <summary>This app's SAML metadata, for the identity provider to read.</summary>
    public static string SamlMetadataUrl(AuthOptions auth) => auth.Origin + SamlPath + "/metadata";

    private static void MapSaml(RouteGroupBuilder auth)
    {
        // Public: an admin gives this address (or the file) to the identity provider.
        auth.MapGet("/saml/metadata", (IOptionsMonitor<CompanySignInOptions> o, IOptions<AuthOptions> a) =>
            Results.Text(SamlIdp.Metadata(SamlIdp.EntityId(o.CurrentValue, a.Value.Origin), AcsUrl(a.Value)), "application/samlmetadata+xml", Encoding.UTF8));
        // The browser posts the provider's answer from the provider's page, so it has no X-Requested-With: the signature,
        // the request ID and the RelayState in this browser's cookie are the guard (IdentityWiring.UseCsrfGuard lets it through).
        auth.MapPost("/saml/acs", AcsAsync).DisableAntiforgery();
    }

    private static ITimeLimitedDataProtector SamlProtector(IDataProtectionProvider dp) => dp.CreateProtector("company-sign-in-saml").ToTimeLimitedDataProtector();

    private static async Task<IResult> SamlStartAsync(HttpContext ctx, CompanySignInOptions o, string redirect, bool remember, SamlIdp saml, AuthOptions auth,
        IDataProtectionProvider dp, Audit audit, ILogger logger)
    {
        SamlProvider p;
        try
        {
            p = await saml.ProviderAsync(o, ct: ctx.RequestAborted);
        }
        catch (CompanyIdpException ex)
        {
            LogUnavailable(logger, ex.Message);
            await audit.WriteAsync("sign_in", success: false, detail: CompanySignIn.Detail(saml: true) + ": " + ex.Message);
            return Results.Redirect(Login("company_unavailable", redirect));
        }
        var relayState = CompanyIdp.Random();
        var (id, url) = SamlIdp.SignInRequest(p, SamlIdp.EntityId(o, auth.Origin), AcsUrl(auth), relayState, DateTimeOffset.UtcNow);
        var flow = new SamlFlow(id, relayState, redirect, remember);
        ctx.Response.Cookies.Append(SamlFlowCookie, SamlProtector(dp).Protect(JsonSerializer.Serialize(flow), FlowLifetime), SamlCookieOptions(FlowLifetime));
        return Results.Redirect(url);
    }

    private static async Task<IResult> AcsAsync(HttpContext ctx, SamlIdp saml, SamlReplay replay, CompanySignIn signIn, IOptionsMonitor<CompanySignInOptions> options,
        IOptions<AuthOptions> auth, IDataProtectionProvider dp, Audit audit, ILogger<SamlIdp> logger)
    {
        var detail = CompanySignIn.Detail(saml: true) + ": ";
        var flow = ReadSamlFlow(ctx, dp);
        ctx.Response.Cookies.Delete(SamlFlowCookie, SamlCookieOptions(TimeSpan.Zero));
        var form = ctx.Request.HasFormContentType ? await ctx.Request.ReadFormAsync(ctx.RequestAborted) : null;
        var relayState = form?["RelayState"].ToString() ?? "";
        var o = options.CurrentValue;
        var a = auth.Value;
        // The RelayState ties the answer to the browser that asked, as the state does for OIDC. Without it the
        // answer is one nobody here asked for: taken only when an admin allows sign-in started at the provider.
        var mine = flow is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(relayState), Encoding.UTF8.GetBytes(flow.RelayState));
        var redirect = mine ? flow!.Redirect : o.SamlAllowIdpInitiated ? Redirects.Safe(relayState, a.Domain) : "/";
        if (!o.SamlEnabled)
        {
            return Results.Redirect(Login("company_off", redirect));
        }
        if (form?["SAMLResponse"].ToString() is not { Length: > 0 } posted)
        {
            await audit.WriteAsync("sign_in", success: false, detail: detail + "the identity provider posted no SAMLResponse");
            return Results.Redirect(Login("company_failed", redirect));
        }
        SamlAssertion assertion;
        try
        {
            assertion = await saml.ReadAsync(o, posted, SamlIdp.EntityId(o, a.Origin), AcsUrl(a), mine ? flow!.RequestId : null, ctx.RequestAborted);
        }
        catch (Exception ex) when (ex is CompanyIdpException or SamlKeyMismatchException)
        {
            var unavailable = ex is CompanyIdpException { Unavailable: true };
            if (unavailable)
            {
                LogUnavailable(logger, ex.Message);
            }
            await audit.WriteAsync("sign_in", success: false, detail: detail + ex.Message);
            return Results.Redirect(Login(unavailable ? "company_unavailable" : "company_failed", redirect));
        }
        if (!await replay.FirstUseAsync(assertion.Id, assertion.KeepUntil, ctx.RequestAborted))
        {
            await audit.WriteAsync("sign_in", CompanyPeople.UserName(assertion.Person.UserName), success: false, detail: detail + "this assertion has signed someone in before (a replay)");
            return Results.Redirect(Login("company_failed", redirect));
        }
        return Finish(await signIn.SignInAsync(assertion.Person, mine && flow!.Remember), redirect);
    }

    private static SamlFlow? ReadSamlFlow(HttpContext ctx, IDataProtectionProvider dp)
    {
        if (ctx.Request.Cookies[SamlFlowCookie] is not { Length: > 0 } cookie)
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<SamlFlow>(SamlProtector(dp).Unprotect(cookie));
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException)
        {
            return null; // tampered with, or expired
        }
    }

    private static CookieOptions SamlCookieOptions(TimeSpan life) => new()
    {
        HttpOnly = true,
        Secure = true,
        // None: the answer comes back as a form posted from the provider's site, and Lax cookies stay behind on a cross-site POST.
        SameSite = SameSiteMode.None,
        Path = CookiePath,
        MaxAge = life,
        IsEssential = true,
    };

    /// <summary>Whether a SAML provider of these settings is known: its entity ID, for the admin's view.</summary>
    private static async Task<string?> SamlIssuerAsync(CompanySignInOptions o, SamlIdp saml, CancellationToken ct)
    {
        if (!o.SamlEnabled)
        {
            return null;
        }
        try
        {
            return (await saml.ProviderAsync(o, ct: ct)).EntityId;
        }
        catch (CompanyIdpException)
        {
            return string.IsNullOrWhiteSpace(o.SamlIdpEntityId) ? o.SamlMetadataUrl?.Trim() : o.SamlIdpEntityId.Trim();
        }
    }
}
