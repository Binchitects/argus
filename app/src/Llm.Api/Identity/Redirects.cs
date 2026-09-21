namespace Llm.Api.Identity;

public static class Redirects
{
    /// <summary>
    /// Where to send someone after sign-in: a path on the app, or an https URL on
    /// the domain or one of its subdomains. Anything else (another site, a
    /// scheme-relative "//evil", a backslash trick) becomes "/".
    /// </summary>
    public static string Safe(string? target, string domain)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return "/";
        }
        target = target.Trim();
        if (target.StartsWith('/'))
        {
            return target.Length > 1 && (target[1] == '/' || target[1] == '\\') ? "/" : target;
        }
        if (Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo) &&
            (uri.Host.Equals(domain, StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase)))
        {
            return uri.AbsoluteUri;
        }
        return "/";
    }
}
