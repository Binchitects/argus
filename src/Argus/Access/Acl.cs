using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Argus.Configuration;
using Argus.Indexing;
using Argus.Store;
using Argus.Util;
using Microsoft.Data.Sqlite;
using YamlDotNet.RepresentationModel;

namespace Argus.Access;

/// <summary>Access could not be established. The message is read by an agent.</summary>
public class AclDenied(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Who is asking, and which index repo ids they may read.</summary>
public sealed record Identity(long UserId, string Username, IReadOnlyList<long> AllowedRepoIds)
{
    /// <summary>How the person was identified, for the audit lines: api_key, gitlab_token, chat, argus_key.</summary>
    public string? Via { get; init; }
}

/// <summary>
/// A developer's GitLab token resolved to the repositories they may read
///. Cached by SHA-256 of the token -- never the token -- for ten
/// minutes, and served stale for up to an hour when GitLab itself is unwell.
/// </summary>
public static class Acl
{
    public const long TtlSeconds = 600;
    public const long StaleGraceSeconds = 3600;
    public const int MinAccessLevel = 20;
    public const int PerPage = 100;
    public const int MaxPages = 1000;

    sealed class GitLabUnwell(string message) : Exception(message);

    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    static List<long> MapToRepoIds(SqliteConnection conn, List<long> gitlabIds)
    {
        if (gitlabIds.Count == 0) return [];
        return Sql.QueryList(conn, $"SELECT id FROM repos WHERE gitlab_id IN ({Sql.Marks(gitlabIds.Count)})",
                gitlabIds.Cast<object?>().ToArray())
            .Select(r => r.Long("id")).Order().ToList();
    }

    static (long, string, List<long>) Fetch(GitLabConfig cfg, string token, HttpClient client)
    {
        var headers = new Dictionary<string, string> { ["PRIVATE-TOKEN"] = token };
        var me = Tls.Get(client, $"{cfg.Url}/api/v4/user", headers);
        if (me.Status is 401 or 403)
            throw new AclDenied(
                "Your GitLab token was rejected. Refresh it and re-run " +
                "`hermes mcp add argus --url <url> --auth header`.");
        if (me.Status >= 500) throw new GitLabUnwell($"GitLab returned {me.Status} for /user.");
        if (me.Status != 200) throw new AclDenied($"GitLab returned {me.Status} for /user.");
        var user = me.Json()!;

        var gitlabIds = new List<long>();
        for (int page = 1; page <= MaxPages; page++)
        {
            var resp = Tls.Get(client, $"{cfg.Url}/api/v4/projects", headers,
            [
                ("membership", "true"), ("min_access_level", MinAccessLevel.ToString()),
                ("simple", "true"), ("per_page", PerPage.ToString()), ("page", page.ToString()),
            ]);
            if (resp.Status >= 500) throw new GitLabUnwell($"GitLab returned {resp.Status} listing your projects.");
            if (resp.Status != 200) throw new AclDenied($"GitLab returned {resp.Status} listing your projects.");
            if (resp.Json() is not JsonArray batch || batch.Count == 0) break;
            gitlabIds.AddRange(batch.Select(p => Convert.ToInt64(p!["id"]!.ToString())));
        }
        return (Convert.ToInt64(user["id"]!.ToString()), user["username"]!.GetValue<string>(), gitlabIds);
    }

    static Identity FromCache(Row cached) =>
        new(cached.Long("user_id"), cached.Str("username"),
            (JsonNode.Parse(cached.Str("repo_ids_json")) as JsonArray ?? []).Select(n => n!.GetValue<long>()).ToList());

    public static Identity Resolve(SqliteConnection conn, GitLabConfig cfg, string token, HttpClient? client = null, Func<double>? now = null)
    {
        now ??= () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        if (string.IsNullOrEmpty(token))
            throw new AclDenied("No credential was sent. Configure the server with --auth header.");

        var key = Hash(token);
        var cached = Writes.GetAclCache(conn, key);
        double? age = cached is null ? null : now() - cached.Long("fetched_at");
        if (cached is not null && age >= 0 && age < TtlSeconds) return FromCache(cached);

        var owns = client is null;
        client ??= Tls.ClientFor(cfg, 15.0);
        long userId;
        string username;
        List<long> gitlabIds;
        try
        {
            (userId, username, gitlabIds) = Fetch(cfg, token, client);
        }
        catch (AclDenied)
        {
            throw;
        }
        catch (Exception exc) when (exc is HttpRequestException or TaskCanceledException or JsonException
                                        or GitLabUnwell or InvalidOperationException or FormatException or NullReferenceException)
        {
            if (cached is not null && age < StaleGraceSeconds)
            {
                Console.Error.WriteLine($"GitLab is unwell ({exc.Message}); serving ACL cached {age:0}s ago");
                return FromCache(cached);
            }
            throw new AclDenied(
                "Cannot verify your GitLab access right now and no recent cached " +
                "permission exists, so access is denied. Retry shortly.", exc);
        }
        finally
        {
            if (owns) client.Dispose();
        }

        var repoIds = MapToRepoIds(conn, gitlabIds);
        Writes.UpsertAclCache(conn, key, userId, username, "[" + string.Join(", ", repoIds) + "]", (long)now());
        return new Identity(userId, username, repoIds);
    }
}

/// <summary>GitLab could not be asked, and nothing recent enough is cached.</summary>
public sealed class GitLabUnavailable(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Project member lists and user lookups via the READ-ONLY service credential:
/// how a signed-in person (identified by their email, or the GitLab username an
/// administrator linked) gets exactly the repositories their GitLab membership
/// grants, and how a refusal names whom to ask. One connection pool for every
/// call; each answer is fetched once for everyone asking at the same moment.
/// </summary>
public sealed class MemberDirectory(GitLabConfig cfg, HttpClient? client = null, double ttl = Acl.TtlSeconds, Func<double>? now = null)
{
    public const int MaintainerLevel = 40;
    const int MaxMemberPages = 50;
    /// <summary>Member lists fetched at once when a person's repositories are resolved: GitLab is asked in parallel, not one project after another.</summary>
    public const int Parallel = 8;

    public GitLabConfig Config { get; } = cfg;
    readonly Func<double> _now = now ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0);
    readonly Lock _gate = new();
    readonly Dictionary<long, (double At, List<Member> Value)> _members = new();
    readonly Dictionary<string, (double At, JsonObject? Value)> _users = new(StringComparer.Ordinal);
    readonly System.Collections.Concurrent.ConcurrentDictionary<object, object> _fetching = new();
    readonly Lazy<HttpClient> _shared = new(() => client ?? Tls.ClientFor(cfg, 15.0));

    public sealed record Member(long Id, string Username, string Name, int AccessLevel, string State);

    HttpResult Get(string path, IEnumerable<(string, string)> query) =>
        Tls.Get(_shared.Value, $"{Config.Url}/api/v4{path}", Credentials.Headers(Config), query);

    T Cached<TKey, T>(Dictionary<TKey, (double At, T Value)> cache, TKey key, Func<T> fetch) where TKey : notnull
    {
        if (Fresh(cache, key, out var value)) return value;
        // One fetch per key at a time: who asks meanwhile waits for it and takes its answer.
        lock (_fetching.GetOrAdd((cache, key), _ => new object()))
        {
            if (Fresh(cache, key, out value)) return value;
            (double At, T Value) hit;
            bool found;
            lock (_gate) found = cache.TryGetValue(key, out hit);
            double? age = found ? _now() - hit.At : null;
            try { value = fetch(); }
            catch (Exception exc) when (exc is HttpRequestException or TaskCanceledException or JsonException or GitLabUnavailable
                                            or InvalidOperationException or FormatException)
            {
                if (found && age < Acl.StaleGraceSeconds)
                {
                    Console.Error.WriteLine($"GitLab is unwell ({exc.Message}); serving a cached answer {age:0}s old");
                    return hit.Value;
                }
                throw new GitLabUnavailable(exc.Message, exc);
            }
            lock (_gate) cache[key] = (_now(), value);
            return value;
        }
    }

    bool Fresh<TKey, T>(Dictionary<TKey, (double At, T Value)> cache, TKey key, out T value) where TKey : notnull
    {
        (double At, T Value) hit;
        bool found;
        lock (_gate) found = cache.TryGetValue(key, out hit);
        var age = found ? _now() - hit.At : -1;
        value = hit.Value;
        return found && age >= 0 && age < ttl;
    }

    /// <summary>
    /// The member lists of these projects, fetched <see cref="Parallel"/> at a time where they
    /// are not known or are older than the TTL. A project GitLab cannot answer for (and nothing
    /// recent is kept of) fails the whole: <see cref="GitLabUnavailable"/>, once every one was tried.
    /// </summary>
    public void Prefetch(IReadOnlyCollection<long> gitlabIds)
    {
        var missing = gitlabIds.Distinct().Where(id => !Fresh(_members, id, out _)).ToList();
        if (missing.Count == 0) return;
        GitLabUnavailable? failed = null;
        System.Threading.Tasks.Parallel.ForEach(missing, new ParallelOptions { MaxDegreeOfParallelism = Parallel }, id =>
        {
            try { Members(id); }
            catch (GitLabUnavailable exc) { Interlocked.CompareExchange(ref failed, exc, null); }
        });
        if (failed is not null) throw failed;
    }

    /// <summary>Everyone with access to a project, inherited memberships included.</summary>
    public List<Member> Members(long gitlabId) => Cached(_members, gitlabId, () =>
    {
        var output = new List<Member>();
        for (int page = 1; page <= MaxMemberPages; page++)
        {
            var resp = Get($"/projects/{gitlabId}/members/all", [("per_page", Acl.PerPage.ToString()), ("page", page.ToString())]);
            if (resp.Status is 403 or 404) return [];
            if (resp.Status >= 500) throw new GitLabUnavailable($"GitLab returned {resp.Status} for project {gitlabId} members");
            if (resp.Status != 200) return [];
            if (resp.Json() is not JsonArray batch || batch.Count == 0) break;
            foreach (var m in batch)
                output.Add(new Member(
                    Convert.ToInt64(m!["id"]!.ToString()),
                    m["username"]?.GetValue<string>() ?? "",
                    m["name"]?.GetValue<string>() ?? "",
                    Convert.ToInt32(m["access_level"]?.ToString() ?? "0"),
                    m["state"]?.GetValue<string>() ?? "active"));
            if (batch.Count < Acl.PerPage) break;
        }
        return output;
    });

    /// <summary>A GitLab account by exact email (public or, for an admin token, private), else by exact username.</summary>
    public JsonObject? User(string? username = null, string? email = null)
    {
        JsonObject? Lookup(IEnumerable<(string, string)> query, Func<JsonObject, bool> match)
        {
            var resp = Get("/users", query);
            if (resp.Status >= 500) throw new GitLabUnavailable($"GitLab returned {resp.Status} looking up a user");
            if (resp.Status != 200) return null;
            var hits = (resp.Json() as JsonArray ?? []).OfType<JsonObject>().Where(match).ToList();
            return hits.Count == 1 ? hits[0] : null;
        }

        if (!string.IsNullOrEmpty(email))
        {
            var addr = PyStr.Strip(email).ToLowerInvariant();
            bool Matches(JsonObject u) =>
                new[] { "public_email", "email" }.Any(f => PyStr.Strip(u[f]?.ToString() ?? "").ToLowerInvariant() == addr);
            var found = Cached(_users, $"e:{addr}", () => Lookup([("search", addr)], Matches));
            if (found is not null) return found;
        }
        if (!string.IsNullOrEmpty(username))
        {
            var name = PyStr.Strip(username).ToLowerInvariant();
            var found = Cached(_users, $"u:{name}", () => Lookup([("username", name)],
                u => (u["username"]?.ToString() ?? "").ToLowerInvariant() == name));
            if (found is not null) return found;
        }
        return null;
    }

    /// <summary>"@username (Name)" for active Maintainers and Owners, best first.</summary>
    public List<string> Maintainers(long gitlabId, int limit = 5) =>
        Members(gitlabId)
            .Where(m => m.AccessLevel >= MaintainerLevel && m.State == "active")
            .OrderBy(m => -m.AccessLevel).ThenBy(m => m.Name.ToLowerInvariant(), StringComparer.Ordinal)
            .Take(limit)
            .Select(m => m.Name.Length > 0 ? $"@{m.Username} ({m.Name})" : $"@{m.Username}")
            .ToList();
}

public static class People
{
    /// <summary>
    /// The sign-in username a users file gives this email
    /// (<c>users: {name: {email: ...}}</c>), if the file is readable. The platform
    /// writes one without password hashes for Argus: its people's usernames are
    /// their GitLab usernames, so a private GitLab email still finds the account.
    /// </summary>
    public static string? UsernameForEmail(string? usersFile, string email)
    {
        if (string.IsNullOrEmpty(usersFile)) return null;
        YamlMappingNode? root;
        try
        {
            var stream = new YamlStream();
            using var reader = new StringReader(File.ReadAllText(usersFile));
            stream.Load(reader);
            root = stream.Documents.Count > 0 ? stream.Documents[0].RootNode as YamlMappingNode : null;
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException or YamlDotNet.Core.YamlException)
        {
            return null;
        }
        if (root is null) return null;
        var addr = PyStr.Strip(email).ToLowerInvariant();
        foreach (var (k, v) in root.Children)
        {
            if (k is not YamlScalarNode { Value: "users" } || v is not YamlMappingNode users) continue;
            foreach (var (uk, uv) in users.Children)
            {
                if (uv is not YamlMappingNode entry) continue;
                foreach (var (ek, ev) in entry.Children)
                    if (ek is YamlScalarNode { Value: "email" } && ev is YamlScalarNode es
                        && PyStr.Strip(es.Value ?? "").ToLowerInvariant() == addr)
                        return (uk as YamlScalarNode)?.Value;
            }
        }
        return null;
    }

    const string CannotVerify = "Cannot verify your GitLab access right now and no recent cached " +
                                "permission exists, so access is denied. Retry shortly.";

    /// <summary>
    /// The Identity of a signed-in person, from GitLab membership read with the
    /// service credential. Matched by email, then by the GitLab username the
    /// administrator linked to the account, if any.
    /// </summary>
    public static Identity ResolvePerson(SqliteConnection conn, MemberDirectory directory, string? email, string? gitlabUsername = null)
    {
        email = PyStr.Strip(email ?? "");
        if (email.Length == 0) throw new AclDenied("No email is known for this person, so access is denied.");
        var username = string.IsNullOrWhiteSpace(gitlabUsername) ? null : gitlabUsername.Trim();
        JsonObject? user;
        try { user = directory.User(username, email); }
        catch (GitLabUnavailable exc) { throw new AclDenied(CannotVerify, exc); }
        if (user is null)
        {
            var also = username is not null ? $", then by username {PyStr.Repr(username)}" : "";
            throw new AclDenied(
                $"No GitLab account matches {email} (looked up by email{also}). " +
                "If that address is PRIVATE on the GitLab profile, only an " +
                "administrator service token can match it -- otherwise set it as " +
                "the profile's public email, or ask an administrator to link your " +
                "GitLab username to your account.");
        }
        if ((user["state"]?.ToString() ?? "active") != "active")
            throw new AclDenied($"The GitLab account {user["username"]} is not active.");

        var uid = Convert.ToInt64(user["id"]!.ToString());
        var byProject = new Dictionary<long, List<long>>();
        var order = new List<long>();
        foreach (var r in Sql.Query(conn, "SELECT id, gitlab_id FROM repos"))
        {
            var gid = r.Long("gitlab_id");
            if (!byProject.TryGetValue(gid, out var list)) { byProject[gid] = list = []; order.Add(gid); }
            list.Add(r.Long("id"));
        }
        var allowed = new List<long>();
        try
        {
            directory.Prefetch(order);
            foreach (var gid in order)
                if (directory.Members(gid).Any(m => m.Id == uid && m.AccessLevel >= Acl.MinAccessLevel && m.State == "active"))
                    allowed.AddRange(byProject[gid]);
        }
        catch (GitLabUnavailable exc) { throw new AclDenied(CannotVerify, exc); }
        return new Identity(uid, user["username"]?.ToString() ?? "", allowed.Order().ToList());
    }

    /// <summary>An access check that came to a definite no (not GitLab being unwell).</summary>
    public static bool Definite(AclDenied exc) => exc.InnerException is not GitLabUnavailable;

    /// <summary>Explain matches the person cannot read: which repositories, and whom to ask.</summary>
    public static string NoAccessMessage(IReadOnlyList<(string Path, long GitlabId, int Count)> repos, MemberDirectory? directory)
    {
        var lines = new List<string>();
        foreach (var (path, gitlabId, count) in repos)
        {
            var who = "";
            if (directory is not null)
            {
                try
                {
                    var people = directory.Maintainers(gitlabId);
                    who = people.Count > 0 ? "maintainers: " + string.Join(", ", people) : "no maintainer listed";
                }
                catch (GitLabUnavailable) { who = "maintainers unavailable right now"; }
            }
            var noun = count == 1 ? "match" : "matches";
            lines.Add($"- {path} ({count} {noun}){(who.Length > 0 ? " -- " + who : "")}");
        }
        bool many = repos.Count != 1;
        return "Nothing you have access to matches this, but it does exist in " +
               $"{repos.Count} {(many ? "repositories" : "repository")} you cannot read:\n" +
               string.Join("\n", lines) +
               "\nTell the person asking that they do not have access, and that they can ask a " +
               "maintainer listed above to add them in GitLab with at least Reporter access. " +
               "Argus picks the change up within 10 minutes.";
    }
}

/// <summary>
/// The repositories each person may read, resolved from their GitLab membership apart from the
/// requests that need them. Connecting never waits for it: a person known to the gate is
/// connected at once, the resolution starts then (<see cref="Warm"/>), and only a tool that reads
/// code waits, for the first answer only. An answer is kept ten minutes, then served while a fresh
/// one is fetched in the background (for an hour at most while GitLab cannot answer), and is
/// fetched once for everyone asking at the same moment.
/// </summary>
public sealed class PersonAccess(Func<string, string?, Identity> resolve, Func<double>? now = null, double ttl = Acl.TtlSeconds)
{
    const int MaxKept = 5000;
    readonly Func<double> _now = now ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0);
    readonly Lock _gate = new();
    readonly Dictionary<string, (double At, Identity Value)> _known = new(StringComparer.Ordinal);
    readonly Dictionary<string, Task<Identity>> _resolving = new(StringComparer.Ordinal);

    static string Key(string email, string? username) => PyStr.Strip(email).ToLowerInvariant() + "\n" + (username ?? "").Trim().ToLowerInvariant();

    /// <summary>
    /// The person's repositories: as kept, else resolved now, on this thread (or by joining the
    /// resolution already under way: no thread waits for another that is not running yet).
    /// </summary>
    public Identity Resolve(string email, string? username)
    {
        var key = Key(email, username);
        Task<Identity>? running;
        TaskCompletionSource<Identity>? mine = null;
        lock (_gate)
        {
            if (_known.TryGetValue(key, out var hit))
            {
                var age = _now() - hit.At;
                if (age >= 0 && age < ttl) return hit.Value;
                if (age >= 0 && age < Acl.StaleGraceSeconds)
                {
                    Background(key, email, username);
                    return hit.Value;
                }
            }
            if (!_resolving.TryGetValue(key, out running))
            {
                mine = new TaskCompletionSource<Identity>(TaskCreationOptions.RunContinuationsAsynchronously);
                _resolving[key] = running = mine.Task;
            }
        }
        if (mine is not null) Run(key, email, username, mine);
        return running.GetAwaiter().GetResult();
    }

    /// <summary>Starts resolving when nothing fresh is kept and nothing is under way, so the first tool call finds it ready.</summary>
    public void Warm(string email, string? username)
    {
        var key = Key(email, username);
        lock (_gate)
        {
            if (_known.TryGetValue(key, out var hit) && _now() - hit.At is var age && age >= 0 && age < ttl) return;
            Background(key, email, username);
        }
    }

    /// <summary>Under the gate: a resolution on a thread of its own, unless one is under way.</summary>
    void Background(string key, string email, string? username)
    {
        if (_resolving.ContainsKey(key)) return;
        var mine = new TaskCompletionSource<Identity>(TaskCreationOptions.RunContinuationsAsynchronously);
        _resolving[key] = mine.Task;
        _ = Task.Run(() => Run(key, email, username, mine));
    }

    /// <summary>One resolution: kept when it worked; a definite no forgets what was kept; GitLab unwell leaves it.</summary>
    void Run(string key, string email, string? username, TaskCompletionSource<Identity> mine)
    {
        Identity identity;
        try
        {
            identity = resolve(email, username);
        }
        catch (Exception exc)
        {
            lock (_gate)
            {
                _resolving.Remove(key);
                // A definite no (no account, not active): what was kept is not served again.
                if (exc is AclDenied denied && People.Definite(denied)) _known.Remove(key);
                else Console.Error.WriteLine($"could not resolve a person's repositories: {exc.Message}");
            }
            mine.SetException(exc);
            // Seen here: nobody may be waiting for a resolution started in the background.
            _ = mine.Task.Exception;
            return;
        }
        lock (_gate)
        {
            _resolving.Remove(key);
            if (_known.Count >= MaxKept)
            {
                var now = _now();
                foreach (var old in _known.Where(e => now - e.Value.At >= Acl.StaleGraceSeconds).Select(e => e.Key).ToList()) _known.Remove(old);
                if (_known.Count >= MaxKept) _known.Clear();
            }
            _known[key] = (_now(), identity);
        }
        mine.SetResult(identity);
    }
}
