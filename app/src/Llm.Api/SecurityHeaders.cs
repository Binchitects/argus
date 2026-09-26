namespace Llm.Api;

public static class SecurityHeaders
{
    // Styles allow 'unsafe-inline' because chart and UI libraries set style
    // attributes; scripts do not -- the React build has no inline script.
    private const string Csp =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; " +
        "font-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'; object-src 'none'";

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                var h = context.Response.Headers;
                h.ContentSecurityPolicy = Csp;
                h.XContentTypeOptions = "nosniff";
                h.XFrameOptions = "DENY";
                h["Referrer-Policy"] = "no-referrer";
                h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
                h["Cross-Origin-Opener-Policy"] = "same-origin";
                if (context.Request.IsHttps)
                {
                    h.StrictTransportSecurity = "max-age=31536000";
                }
                return Task.CompletedTask;
            });
            await next(context);
        });
}
