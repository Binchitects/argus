using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Argus.Store;
using Argus.Util;

namespace Argus.Access;

/// <summary>
/// The platform app's rules on code (Admin → Access: code.read, and repository allows and denies): of the
/// repositories GitLab lets a person read, the ones the app's rules leave them. Asked at the app beside the key
/// check (ARGUS_KEY_CHECK_URL's /api/authz/code, with the chat client's credential), by repository path; an
/// answer is kept five minutes per person and set of repositories. When the app cannot answer, nothing is
/// readable (<see cref="AclDenied"/>), as when GitLab cannot.
/// </summary>
public sealed class ArenaRules
{
    public const double GoodSeconds = 300;
    const int MaxCached = 2000;
    public const string CannotCheck = "Cannot check the platform's access rules right now, so code access is denied. Retry shortly.";

    public string Url { get; }
    readonly string _credential;
    readonly HttpClient _http;
    readonly Func<double> _now;
    readonly Func<IReadOnlyDictionary<long, string>> _paths;
    readonly Lock _gate = new();
    readonly Dictionary<string, (double Until, HashSet<string> Allowed)> _cache = new(StringComparer.Ordinal);

    /// <param name="paths">Each repository row's path (group/project), as the index has it now.</param>
    public ArenaRules(string url, string credential, Func<IReadOnlyDictionary<long, string>> paths, HttpClient? http = null, Func<double>? now = null)
    {
        Url = url;
        _credential = credential;
        _paths = paths;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _now = now ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0);
    }

    /// <summary>Beside the key check (ARGUS_KEY_CHECK_URL), with the same credential; null for a standalone Argus.</summary>
    public static ArenaRules? FromEnvironment(string credentialEnv, string dbPath)
    {
        var keyUrl = (Environment.GetEnvironmentVariable(ArenaKeys.UrlEnv) ?? "").Trim();
        if (keyUrl.Length == 0 || !Uri.TryCreate(keyUrl, UriKind.Absolute, out var key)) return null;
        var credential = (Environment.GetEnvironmentVariable(credentialEnv) ?? "").Trim();
        return new ArenaRules(new Uri(key, "/api/authz/code").ToString(), credential, () =>
        {
            using var conn = Db.Connect(dbPath);
            return Sql.Query(conn, "SELECT id, path_with_namespace FROM repos").ToDictionary(r => r.Long("id"), r => r.Str("path_with_namespace"));
        });
    }

    /// <summary>The identity with only the repositories the app's rules leave the person.</summary>
    public Identity Narrow(string email, Identity identity)
    {
        if (identity.AllowedRepoIds.Count == 0) return identity;
        var paths = _paths();
        var mine = identity.AllowedRepoIds.Where(paths.ContainsKey).Select(id => (Id: id, Path: paths[id])).ToList();
        var allowed = Allowed(email.Trim().ToLowerInvariant(), [.. mine.Select(m => m.Path).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]);
        return identity with { AllowedRepoIds = [.. mine.Where(m => allowed.Contains(m.Path)).Select(m => m.Id).Order()] };
    }

    HashSet<string> Allowed(string email, IReadOnlyList<string> repos)
    {
        var key = email + "\n" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', repos))));
        lock (_gate)
            if (_cache.TryGetValue(key, out var hit) && hit.Until > _now()) return hit.Allowed;
        using var request = new HttpRequestMessage(HttpMethod.Post, Url)
        {
            Content = new StringContent(new JsonObject { ["email"] = email, ["repos"] = new JsonArray([.. repos.Select(r => (JsonNode)r)]) }.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (_credential.Length > 0) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _credential);
        HashSet<string> allowed;
        try
        {
            using var response = _http.Send(request);
            var text = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"the rules check at {Url} answered {(int)response.StatusCode}");
                throw new AclDenied(CannotCheck);
            }
            var body = JsonNode.Parse(text) as JsonObject;
            allowed = body?["read"]?.GetValue<bool>() == true
                ? [.. (body["allowed"] as JsonArray ?? []).Select(n => n?.GetValue<string>() ?? "")]
                : [];
        }
        catch (Exception exc) when (exc is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            Console.Error.WriteLine($"the rules check at {Url} failed: {exc.Message}");
            throw new AclDenied(CannotCheck, exc);
        }
        lock (_gate)
        {
            if (_cache.Count >= MaxCached) _cache.Clear();
            _cache[key] = (_now() + GoodSeconds, allowed);
        }
        return allowed;
    }
}
