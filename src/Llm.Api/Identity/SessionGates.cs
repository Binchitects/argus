using Microsoft.AspNetCore.Http.Extensions;

namespace Llm.Api.Identity;

/// <summary>
/// What a session held back by a gate (the sign-in notice, two-factor enrolment) gets when its browser is sent to another app's
/// sign-in through this one (OIDC's /connect/authorize, as code-intel's and Code Arena's): the web's page for that step, which goes
/// on to the same authorize request once it is done, not a JSON refusal with no page around it.
/// </summary>
public static class SessionGates
{
    /// <summary>Where the web takes over: it shows the step, then opens <c>rd</c>.</summary>
    public const string ContinuePath = "/continue";

    /// <summary>
    /// For a browser on its way through /connect/authorize (GET, or POST as form_post sends it): a redirect to the web's page
    /// for the step, carrying the request to go back to; true when it was sent there.
    /// </summary>
    public static async Task<bool> SendBrowserOnAsync(HttpContext ctx)
    {
        if (!ctx.Request.Path.StartsWithSegments("/connect/authorize") || !(HttpMethods.IsGet(ctx.Request.Method) || HttpMethods.IsPost(ctx.Request.Method)))
        {
            return false;
        }
        // The same authorize request, as a GET: a form's fields become its query.
        var fields = ctx.Request.HasFormContentType ? (await ctx.Request.ReadFormAsync(ctx.RequestAborted)).ToList() : ctx.Request.Query.ToList();
        var back = ctx.Request.PathBase + ctx.Request.Path + new QueryBuilder(fields.SelectMany(f => f.Value.Select(v => new KeyValuePair<string, string>(f.Key, v ?? "")))).ToQueryString();
        ctx.Response.Redirect(ContinuePath + "?rd=" + Uri.EscapeDataString(back));
        return true;
    }

    /// <summary>
    /// What a gate never holds back: signing out, here or through OIDC, and Traefik's check of the caller's address before
    /// every request (Settings → Network access), which carries the person's cookie but asks about their network, not them:
    /// held back, its refusal would be every page's, the one that shows the step too.
    /// </summary>
    public static bool Passes(PathString path) =>
        path.StartsWithSegments("/connect/logout") || path.StartsWithSegments(Oidc.AddressFilter.Path);
}
