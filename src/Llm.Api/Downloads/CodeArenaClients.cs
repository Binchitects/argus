using System.Collections.Concurrent;
using System.Security.Claims;
using Llm.Api.Gateway;
using Llm.Api.Notifications;
using Llm.Core.Code;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Downloads;

/// <summary>
/// Which Code Arena each person uses: every request whose User-Agent is code-arena/X.Y.Z (its API calls, its chats, its
/// IDE's) is put down against the person it is for (their key, or their session), at most once in ten minutes for a person
/// and version, after the request. A version older than the one this Arena serves is logged, and the person is told once in
/// their bell how to update (code-arena update); Admin → Overview lists who still uses one.
/// </summary>
public sealed partial class CodeArenaClients(IServiceScopeFactory scopes, KeyPeople people, IConfiguration config, TimeProvider clock, ILogger<CodeArenaClients> logger)
{
    /// <summary>How long a person and version seen is not written again.</summary>
    public static readonly TimeSpan Quiet = TimeSpan.FromMinutes(10);

    /// <summary>How far back Admin → Overview looks for people on an old version.</summary>
    public static readonly TimeSpan Recent = TimeSpan.FromDays(30);

    private readonly ConcurrentDictionary<(Guid, string), DateTimeOffset> _written = new();

    /// <summary>The version a code-arena/X.Y.Z User-Agent names; null for any other.</summary>
    public static string? VersionOf(string? userAgent)
    {
        if (userAgent is null || !userAgent.StartsWith("code-arena/", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        var v = userAgent["code-arena/".Length..].Split(' ', 2)[0].Trim();
        return v.Length is > 0 and <= 40 && v.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '+') ? v : null;
    }

    /// <summary>Whether version <paramref name="a"/> is newer than <paramref name="b"/> (x.y.z; a pre-release comes before its release), as Code Arena compares them.</summary>
    public static bool Newer(string a, string b)
    {
        static (Version Number, string Pre)? Parse(string v)
        {
            var dash = v.IndexOf('-', StringComparison.Ordinal);
            var number = dash < 0 ? v : v[..dash];
            return Version.TryParse(number, out var n) ? (n, dash < 0 ? "" : v[(dash + 1)..]) : null;
        }
        if (Parse(a.Trim().TrimStart('v')) is not { } x || Parse(b.Trim().TrimStart('v')) is not { } y)
        {
            return false;
        }
        var c = x.Number.CompareTo(y.Number);
        return c != 0 ? c > 0 : x.Pre.Length == 0 ? y.Pre.Length > 0 : y.Pre.Length > 0 && string.CompareOrdinal(x.Pre, y.Pre) > 0;
    }

    /// <summary>The Code Arena this Arena serves (its own version), when it serves any builds; null when it has none to offer.</summary>
    public string? Latest => DownloadEndpoints.Builds(DownloadEndpoints.CodeArenaRoot(config)).Count > 0 ? AppInfo.Current.Version : null;

    /// <summary>
    /// A request: who sent it with which Code Arena, if it came from one. What it needs is read at once; whose key it is
    /// (a lookup) and the write run behind the request.
    /// </summary>
    public void Seen(HttpContext http)
    {
        if (VersionOf(http.Request.Headers.UserAgent.ToString()) is not { } version)
        {
            return;
        }
        if (http.User.FindFirstValue(ClaimTypes.NameIdentifier) is { } id && Guid.TryParse(id, out var signedIn))
        {
            Seen(signedIn, version);
        }
        else if (KeyOf(http.Request) is { } key)
        {
            _ = Task.Run(async () =>
            {
                if (await people.ForAsync(key, CancellationToken.None) is { } who)
                {
                    Seen(who.Id, version);
                }
            });
        }
    }

    private static string? KeyOf(HttpRequest request)
    {
        var auth = request.Headers.Authorization.ToString();
        var key = auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth[7..].Trim() : request.Headers["x-api-key"].ToString().Trim();
        return key.Length > 0 ? key : null;
    }

    /// <summary>Puts down that <paramref name="person"/> used <paramref name="version"/>, and tells them when it is old.</summary>
    public void Seen(Guid person, string version)
    {
        var now = clock.GetUtcNow();
        if (_written.TryGetValue((person, version), out var at) && now - at < Quiet)
        {
            return;
        }
        _written[(person, version)] = now;
        _ = Task.Run(() => WriteAsync(person, version, now, CancellationToken.None));
    }

    private async Task WriteAsync(Guid person, string version, DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var updated = await db.CodeArenaClients.Where(c => c.UserId == person && c.Version == version).ExecuteUpdateAsync(u => u.SetProperty(c => c.LastSeenAt, now), ct);
            if (updated == 0)
            {
                db.CodeArenaClients.Add(new CodeArenaClient { UserId = person, Version = version, FirstSeenAt = now, LastSeenAt = now });
                try
                {
                    await db.SaveChangesAsync(ct);
                }
                catch (DbUpdateException)
                {
                    // Put down at the same moment by another request or replica: once is enough.
                }
            }
            if (Latest is { } latest && Newer(latest, version))
            {
                LogOld(logger, version, latest, person);
                await scope.ServiceProvider.GetRequiredService<Notifier>().SendAsync(person, new News("code-arena",
                    $"Your Code Arena is {version}; this Arena serves {latest}",
                    $"Run `code-arena update` in a terminal to install it, or download it again from Set up your tools. Older versions miss what this Arena's newer features need.",
                    "/setup", $"code-arena-old:{version}:{latest}"), ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFailed(logger, ex.Message);
        }
    }

    /// <summary>The people who used a Code Arena older than <see cref="Latest"/> within <see cref="Recent"/>: their newest version each.</summary>
    public async Task<IReadOnlyList<(Guid Person, string Name, string Version, DateTimeOffset LastSeen)>> OldAsync(AppDbContext db, CancellationToken ct)
    {
        if (Latest is not { } latest)
        {
            return [];
        }
        var since = clock.GetUtcNow() - Recent;
        var seen = await db.CodeArenaClients.AsNoTracking().Where(c => c.LastSeenAt >= since)
            .Join(db.Users.AsNoTracking(), c => c.UserId, u => u.Id, (c, u) => new { c.UserId, Name = u.DisplayName ?? u.UserName ?? "", c.Version, c.LastSeenAt })
            .ToListAsync(ct);
        return [.. seen.GroupBy(s => s.UserId)
            .Select(g => g.Aggregate((a, b) => Newer(b.Version, a.Version) ? b : a))
            .Where(s => Newer(latest, s.Version))
            .OrderByDescending(s => s.LastSeenAt)
            .Select(s => (s.UserId, s.Name, s.Version, s.LastSeenAt))];
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Code Arena {Version} is older than {Latest}, used by person {Person}")]
    private static partial void LogOld(ILogger logger, string version, string latest, Guid person);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Which Code Arena a person uses was not put down: {Reason}")]
    private static partial void LogFailed(ILogger logger, string reason);
}
