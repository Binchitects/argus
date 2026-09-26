using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Argus.Configuration;
using Argus.Util;

namespace Argus.Indexing;

/// <summary>An HTTP response, read fully: status and body text.</summary>
public sealed record HttpResult(int Status, string Text)
{
    public JsonNode? Json() => JsonNode.Parse(Text);
}

/// <summary>
/// TLS policy for reaching GitLab: one setting drives both the
/// API client and the git subprocess, because a call site that forgets is a
/// call site that fails in the field.
/// </summary>
public static class Tls
{
    /// <summary>Tests replace this to serve GitLab from memory.</summary>
    public static Func<GitLabConfig, HttpMessageHandler>? HandlerOverride { get; set; }

    public static HttpMessageHandler HandlerFor(GitLabConfig cfg, CookieContainer? cookies = null)
    {
        if (HandlerOverride is not null) return HandlerOverride(cfg);
        var handler = new HttpClientHandler { AllowAutoRedirect = true };
        if (cookies is not null) { handler.UseCookies = true; handler.CookieContainer = cookies; }
        else handler.UseCookies = false;
        if (!cfg.Verify)
        {
            handler.ServerCertificateCustomValidationCallback = static (_, _, _, _) => true;
        }
        else if (!string.IsNullOrEmpty(cfg.CaCert))
        {
            // Public roots stay trusted and the private CA is added on top, so a
            // certificate that verified before still does.
            var extra = new X509Certificate2Collection();
            extra.ImportFromPemFile(cfg.CaCert);
            handler.ServerCertificateCustomValidationCallback = (_, cert, _, errors) =>
            {
                if (errors == SslPolicyErrors.None) return true;
                if (cert is null || (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != 0) return false;
                using var chain = new X509Chain();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.AddRange(extra);
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                return chain.Build(cert);
            };
        }
        return handler;
    }

    public static HttpClient ClientFor(GitLabConfig cfg, double timeoutSeconds, CookieContainer? cookies = null) =>
        new(HandlerFor(cfg, cookies), disposeHandler: true) { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };

    /// <summary>Apply the same policy to a git subprocess's environment.</summary>
    public static IDictionary<string, string?> GitEnv(GitLabConfig cfg, IDictionary<string, string?> env)
    {
        if (!cfg.Verify) env["GIT_SSL_NO_VERIFY"] = "true";
        else if (!string.IsNullOrEmpty(cfg.CaCert)) env["GIT_SSL_CAINFO"] = cfg.CaCert;
        return env;
    }

    public static HttpResult Send(HttpClient client, HttpMethod method, string url,
        IReadOnlyDictionary<string, string>? headers = null, HttpContent? content = null)
    {
        using var req = new HttpRequestMessage(method, url) { Content = content };
        if (headers is not null)
            foreach (var (k, v) in headers) req.Headers.TryAddWithoutValidation(k, v);
        using var resp = client.Send(req);
        var text = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        return new HttpResult((int)resp.StatusCode, text);
    }

    public static HttpResult Get(HttpClient client, string url, IReadOnlyDictionary<string, string>? headers = null,
        IEnumerable<(string, string)>? query = null) =>
        Send(client, HttpMethod.Get, WithQuery(url, query), headers);

    public static string WithQuery(string url, IEnumerable<(string Key, string Value)>? query)
    {
        if (query is null) return url;
        var parts = query.Select(q => Uri.EscapeDataString(q.Key) + "=" + Uri.EscapeDataString(q.Value)).ToList();
        if (parts.Count == 0) return url;
        return url + (url.Contains('?') ? "&" : "?") + string.Join("&", parts);
    }
}

/// <summary>The configured GitLab credential was rejected or could not be obtained.</summary>
public sealed class CredentialError(string message) : Exception(message);

/// <summary>
/// The one place that turns a <see cref="GitLabConfig"/> into an API credential
///. Token mode sends <c>PRIVATE-TOKEN</c>; password mode
/// signs in through the web form, mints a narrowly-scoped personal access token,
/// caches it for the process, and re-mints once on a 401.
/// </summary>
public static class Credentials
{
    static readonly Dictionary<(string, string), string> Tokens = new();
    static readonly Lock Gate = new();
    public const string TokenName = "argus";
    public static readonly string[] TokenScopes = ["read_api", "read_repository"];

    static string MintToken(GitLabConfig cfg)
    {
        CredentialError Fail(string what, Exception exc) =>
            new($"could not reach GitLab at {cfg.Redacted()} to {what}: {exc.GetType().Name}");

        var cookies = new CookieContainer();
        using var client = Tls.ClientFor(cfg, 15.0, cookies);

        HttpResult page;
        try { page = Tls.Get(client, $"{cfg.Url}/users/sign_in"); }
        catch (HttpRequestException exc) { throw Fail("sign in", exc); }
        catch (TaskCanceledException exc) { throw Fail("sign in", exc); }

        var found = Regex.Match(page.Text, "name=\"authenticity_token\" value=\"([^\"]+)\"");
        if (!found.Success)
            throw new CredentialError(
                $"{cfg.Url} did not serve a sign-in form, so username/password " +
                "sign-in cannot proceed. Use gitlab.auth=token with " +
                "ARGUS_GITLAB_TOKEN, or check the URL.");

        HttpResult login, me;
        try
        {
            login = Tls.Send(client, HttpMethod.Post, $"{cfg.Url}/users/sign_in", content: new FormUrlEncodedContent(
            [
                new("user[login]", cfg.Username),
                new("user[password]", cfg.Password),
                new("authenticity_token", found.Groups[1].Value),
            ]));
        }
        catch (Exception exc) when (exc is HttpRequestException or TaskCanceledException) { throw Fail("sign in", exc); }
        try { me = Tls.Get(client, $"{cfg.Url}/api/v4/user"); }
        catch (Exception exc) when (exc is HttpRequestException or TaskCanceledException) { throw Fail("verify the sign-in", exc); }
        if (me.Status != 200)
        {
            var lower = login.Text.ToLowerInvariant();
            if (lower.Contains("two-factor") || lower.Contains("otp"))
                throw new CredentialError(
                    $"GitLab refused the sign-in for {cfg.Redacted()} because the " +
                    "account uses two-factor authentication, which a scripted " +
                    "sign-in cannot satisfy. Use gitlab.auth=token with a " +
                    "personal access token instead.");
            throw new CredentialError(
                $"GitLab rejected the username/password sign-in for " +
                $"{cfg.Redacted()}. Check gitlab.username and " +
                "ARGUS_GITLAB_PASSWORD.");
        }

        HttpResult minted;
        try
        {
            var after = Tls.Get(client, $"{cfg.Url}/-/user_settings/profile");
            var csrf = Regex.Match(after.Text, "name=\"csrf-token\" content=\"([^\"]+)\"");
            var csrfToken = csrf.Success ? csrf.Groups[1].Value : found.Groups[1].Value;
            var headers = new Dictionary<string, string> { ["X-CSRF-Token"] = csrfToken };

            var listing = Tls.Get(client, $"{cfg.Url}/api/v4/personal_access_tokens");
            if (listing.Status == 200 && listing.Json() is JsonArray existing)
                foreach (var t in existing)
                    if (t?["name"]?.GetValue<string>() == TokenName && !(t["revoked"]?.GetValue<bool>() ?? false))
                        Tls.Send(client, HttpMethod.Delete, $"{cfg.Url}/api/v4/personal_access_tokens/{t["id"]}", headers);

            var form = new List<KeyValuePair<string, string>> { new("name", TokenName) };
            form.AddRange(TokenScopes.Select(s => new KeyValuePair<string, string>("scopes[]", s)));
            form.Add(new("expires_at", ""));
            minted = Tls.Send(client, HttpMethod.Post, $"{cfg.Url}/-/user_settings/personal_access_tokens", headers,
                new FormUrlEncodedContent(form));
        }
        catch (Exception exc) when (exc is HttpRequestException or TaskCanceledException) { throw Fail("create a personal access token", exc); }

        if (minted.Status is not (200 or 201))
            throw new CredentialError(
                $"GitLab signed {cfg.Redacted()} in but would not create a personal " +
                $"access token (HTTP {minted.Status}). An administrator may " +
                "have disabled token creation for this account.");
        string token;
        try { token = minted.Json()?["token"]?.ToString() ?? throw new KeyNotFoundException(); }
        catch (Exception exc) when (exc is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new CredentialError($"GitLab's token response for {cfg.Redacted()} carried no token");
        }
        if (token.Length == 0) throw new CredentialError($"GitLab returned an empty token for {cfg.Redacted()}");
        return token;
    }

    /// <summary>(header name, value) for authenticating to GitLab's API.</summary>
    public static (string Name, string Value) Credential(GitLabConfig cfg)
    {
        if (cfg.Auth == "token") return ("PRIVATE-TOKEN", cfg.Token);
        var key = (cfg.Url, cfg.Username);
        lock (Gate)
            if (Tokens.TryGetValue(key, out var cached)) return ("Authorization", $"Bearer {cached}");
        var token = MintToken(cfg);
        lock (Gate) Tokens[key] = token;
        return ("Authorization", $"Bearer {token}");
    }

    public static Dictionary<string, string> Headers(GitLabConfig cfg)
    {
        var (name, value) = Credential(cfg);
        return new Dictionary<string, string> { [name] = value };
    }

    /// <summary>The secret git receives as the password for an HTTPS clone.</summary>
    public static string GitPassword(GitLabConfig cfg)
    {
        var (_, value) = Credential(cfg);
        return value.StartsWith("Bearer ", StringComparison.Ordinal) ? value["Bearer ".Length..] : value;
    }

    public static void Invalidate(GitLabConfig cfg)
    {
        lock (Gate) Tokens.Remove((cfg.Url, cfg.Username));
    }

    /// <summary>Send a GET, and re-mint the token once if GitLab rejects it (password mode only).</summary>
    public static HttpResult Authorized(GitLabConfig cfg, Func<Dictionary<string, string>, HttpResult> request)
    {
        var response = request(Headers(cfg));
        if (response.Status != 401 || cfg.Auth != "password") return response;
        Invalidate(cfg);
        return request(Headers(cfg));
    }
}

/// <summary>GitLab returned an error or unusable response.</summary>
public sealed class GitLabError(string message) : Exception(message);

public sealed record Project(long GitlabId, string PathWithNamespace, string DefaultBranch, string HttpUrl);

/// <summary>Whether the service token can enumerate every repository.</summary>
public sealed record EnumerationHealth(bool IsAdmin, int VisibleCount, int MemberCount)
{
    public bool Ok => IsAdmin || MemberCount <= VisibleCount;

    public string? Problem => Ok ? null :
        "The service token is not a GitLab admin, and enumeration is " +
        "missing repositories: `membership=false` returned " +
        $"{VisibleCount} project(s), but the token is a member of " +
        $"{MemberCount}. At least {MemberCount - VisibleCount} repository(ies) would be " +
        "silently absent from the index, and every answer drawn from it " +
        "would be confidently incomplete.\n" +
        "Use a token with admin rights, or add the service account to " +
        "every group you want indexed.";
}

public static class GitLab
{
    public const int PerPage = 100;
    public const int MaxPages = 1000;

    /// <summary>http_url_to_repo with its origin replaced by the configured, reachable GitLab.</summary>
    public static string CloneUrlFor(GitLabConfig cfg, string advertised)
    {
        var configured = SplitUrl(cfg.Url);
        var target = SplitUrl(advertised);
        var scheme = configured.Scheme.Length > 0 ? configured.Scheme : target.Scheme;
        var netloc = configured.Netloc.Length > 0 ? configured.Netloc : target.Netloc;
        var url = "";
        if (scheme.Length > 0) url += scheme + ":";
        if (netloc.Length > 0 || scheme is "http" or "https") url += "//" + netloc;
        url += target.Path;
        if (target.Query.Length > 0) url += "?" + target.Query;
        if (target.Fragment.Length > 0) url += "#" + target.Fragment;
        return url;
    }

    /// <summary>urllib.parse.urlsplit, enough of it for http(s) URLs.</summary>
    public static (string Scheme, string Netloc, string Path, string Query, string Fragment) SplitUrl(string url)
    {
        string scheme = "", rest = url;
        var m = Regex.Match(url, @"^([A-Za-z][A-Za-z0-9+.-]*):");
        if (m.Success) { scheme = m.Groups[1].Value.ToLowerInvariant(); rest = url.Substring(m.Length); }
        string netloc = "";
        if (rest.StartsWith("//", StringComparison.Ordinal))
        {
            rest = rest.Substring(2);
            int end = rest.IndexOfAny(['/', '?', '#']);
            netloc = end < 0 ? rest : rest.Substring(0, end);
            rest = end < 0 ? "" : rest.Substring(end);
        }
        string fragment = "", query = "";
        int hash = rest.IndexOf('#');
        if (hash >= 0) { fragment = rest.Substring(hash + 1); rest = rest.Substring(0, hash); }
        int q = rest.IndexOf('?');
        if (q >= 0) { query = rest.Substring(q + 1); rest = rest.Substring(0, q); }
        return (scheme, netloc, rest, query, fragment);
    }

    static IEnumerable<(string, string)> ProjectQuery(string membership, int page) =>
    [
        ("membership", membership), ("simple", "true"), ("archived", "false"),
        ("per_page", PerPage.ToString()), ("page", page.ToString()),
    ];

    public static List<Project> ListProjects(GitLabConfig cfg, HttpClient? client = null)
    {
        var owns = client is null;
        client ??= Tls.ClientFor(cfg, 30.0);
        var projects = new List<Project>();
        try
        {
            for (int page = 1; page <= MaxPages; page++)
            {
                var p = page;
                var response = Credentials.Authorized(cfg, auth =>
                    Tls.Get(client, $"{cfg.Url}/api/v4/projects", auth, ProjectQuery("false", p)));
                if (response.Status != 200)
                    throw new GitLabError($"GET /projects returned {response.Status}: {PyStr.Prefix(response.Text, 200)}");
                JsonNode? batch;
                try { batch = response.Json(); }
                catch (JsonException)
                {
                    throw new GitLabError($"GET /projects page {page}: failed to decode JSON: {PyStr.Prefix(response.Text, 200)}");
                }
                if (batch is not JsonArray arr || arr.Count == 0) break;
                foreach (var item in arr)
                {
                    var def = item?["default_branch"]?.GetValue<string>();
                    if (string.IsNullOrEmpty(def)) continue;
                    projects.Add(new Project(
                        GitlabId: Convert.ToInt64(item!["id"]!.ToString()),
                        PathWithNamespace: item["path_with_namespace"]!.GetValue<string>(),
                        DefaultBranch: def,
                        HttpUrl: CloneUrlFor(cfg, item["http_url_to_repo"]!.GetValue<string>())));
                }
            }
        }
        finally
        {
            if (owns) client.Dispose();
        }
        return projects;
    }

    public static EnumerationHealth CheckEnumeration(GitLabConfig cfg, HttpClient? client = null)
    {
        var owns = client is null;
        client ??= Tls.ClientFor(cfg, 30.0);
        try
        {
            var user = Credentials.Authorized(cfg, auth => Tls.Get(client, $"{cfg.Url}/api/v4/user", auth));
            if (user.Status != 200)
                throw new GitLabError($"GET /user returned {user.Status}: {PyStr.Prefix(user.Text, 200)}");
            bool isAdmin;
            try { isAdmin = user.Json()?["is_admin"]?.GetValue<bool>() ?? false; }
            catch (JsonException) { throw new GitLabError($"GET /user: failed to decode JSON: {PyStr.Prefix(user.Text, 200)}"); }

            int Count(string membership)
            {
                int total = 0;
                for (int page = 1; page <= MaxPages; page++)
                {
                    var p = page;
                    var response = Credentials.Authorized(cfg, auth =>
                        Tls.Get(client, $"{cfg.Url}/api/v4/projects", auth, ProjectQuery(membership, p)));
                    if (response.Status != 200)
                        throw new GitLabError($"GET /projects returned {response.Status}: {PyStr.Prefix(response.Text, 200)}");
                    if (response.Json() is not JsonArray arr || arr.Count == 0) break;
                    total += arr.Count;
                }
                return total;
            }

            return new EnumerationHealth(isAdmin, Count("false"), Count("true"));
        }
        finally
        {
            if (owns) client.Dispose();
        }
    }
}
