using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Argus.Access;

/// <summary>
/// A person's Arena API key (sk-...), checked with the platform's app
/// (ARGUS_KEY_CHECK_URL, with the chat client's credential), which answers
/// their email and username; Argus then resolves them like the chat. Cached by
/// SHA-256 of the key -- never the key -- five minutes when good, thirty
/// seconds when refused. An app that cannot be asked is not cached.
/// </summary>
public sealed class ArenaKeys
{
    public const string UrlEnv = "ARGUS_KEY_CHECK_URL";
    public const string Prefix = "sk-";
    public const double GoodSeconds = 300;
    public const double RefusedSeconds = 30;
    const int MaxCached = 2000;

    /// <summary>What a coding agent is told when it brings anything else (a GitLab token).</summary>
    public const string ConnectWithKey =
        "Connect with your Arena API key (Your account → API key) as 'Authorization: Bearer sk-...'. GitLab tokens are not accepted here.";

    public const string Refused =
        "That API key is not valid: unknown, blocked or expired, or its account is disabled. Make a new one under Your account → API key.";

    public const string CannotCheck = "Cannot check your API key right now, so access is denied. Retry shortly.";

    /// <summary>Whose a key is: the person's email and their username (their GitLab username).</summary>
    public sealed record Person(string Email, string Username);

    public string Url { get; }
    readonly string _credential;
    readonly HttpClient _http;
    readonly Func<double> _now;
    readonly Lock _gate = new();
    readonly Dictionary<string, (double Until, Person? Person)> _cache = new(StringComparer.Ordinal);

    public ArenaKeys(string url, string credential, HttpClient? http = null, Func<double>? now = null)
    {
        Url = url;
        _credential = credential;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _now = now ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0);
    }

    /// <summary><c>ARGUS_KEY_CHECK_URL</c>, with <c>ARGUS_CHAT_CLIENT_TOKEN</c> as the credential; null for a standalone Argus.</summary>
    public static ArenaKeys? FromEnvironment(string credentialEnv)
    {
        var url = (Environment.GetEnvironmentVariable(UrlEnv) ?? "").Trim();
        if (url.Length == 0) return null;
        var credential = (Environment.GetEnvironmentVariable(credentialEnv) ?? "").Trim();
        if (credential.Length == 0)
            Console.Error.WriteLine($"{UrlEnv} is set but {credentialEnv} is not: the app will refuse every key check.");
        return new ArenaKeys(url, credential);
    }

    /// <summary>The key's person, or <see cref="AclDenied"/> with a reason the agent can act on. Asked once for every request that brings the key at the same moment.</summary>
    public async Task<Person> CheckAsync(string key, CancellationToken ct = default)
    {
        var hash = Acl.Hash(key);
        Task<Person?> asking;
        lock (_gate)
        {
            if (_cache.TryGetValue(hash, out var hit) && hit.Until > _now())
                return hit.Person ?? throw new AclDenied(Refused);
            if (!_asking.TryGetValue(hash, out asking!))
            {
                // Not tied to the first request: one that gives up does not fail the others waiting on the answer.
                asking = AskAsync(key, hash);
                _asking[hash] = asking;
            }
        }
        return await asking.WaitAsync(ct) ?? throw new AclDenied(Refused);
    }

    readonly Dictionary<string, Task<Person?>> _asking = new(StringComparer.Ordinal);

    /// <summary>The app's answer for the key (null: refused), remembered; <see cref="AclDenied"/> when the app cannot say.</summary>
    async Task<Person?> AskAsync(string key, string hash)
    {
        try
        {
            return await FetchAsync(key, hash);
        }
        finally
        {
            lock (_gate) _asking.Remove(hash);
        }
    }

    async Task<Person?> FetchAsync(string key, string hash)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Url)
        {
            Content = new StringContent(new JsonObject { ["key"] = key }.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (_credential.Length > 0) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _credential);
        Person? person;
        try
        {
            using var response = await _http.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            // Refused keys say so ("invalid_key"); anything else (a wrong credential, the gateway down) is the app's trouble, not the key's.
            var status = response.StatusCode == HttpStatusCode.OK ? null : (JsonNode.Parse(text.Length > 0 ? text : "{}") as JsonObject)?["status"]?.ToString();
            if (response.StatusCode == HttpStatusCode.Unauthorized && status == "invalid_key")
            {
                person = null;
            }
            else if (response.StatusCode != HttpStatusCode.OK)
            {
                Console.Error.WriteLine($"the key check at {Url} answered {(int)response.StatusCode} {status}");
                throw new AclDenied(CannotCheck);
            }
            else
            {
                var body = JsonNode.Parse(text) as JsonObject;
                var email = body?["email"]?.GetValue<string>() ?? "";
                var username = body?["username"]?.GetValue<string>() ?? "";
                if (email.Length == 0) throw new AclDenied(CannotCheck);
                person = new Person(email, username);
            }
        }
        catch (Exception exc) when (exc is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            Console.Error.WriteLine($"the key check at {Url} failed: {exc.Message}");
            throw new AclDenied(CannotCheck, exc);
        }

        lock (_gate)
        {
            var now = _now();
            if (_cache.Count >= MaxCached)
            {
                foreach (var stale in _cache.Where(e => e.Value.Until <= now).Select(e => e.Key).ToList()) _cache.Remove(stale);
                if (_cache.Count >= MaxCached) _cache.Clear();
            }
            _cache[hash] = (now + (person is null ? RefusedSeconds : GoodSeconds), person);
        }
        return person;
    }
}
