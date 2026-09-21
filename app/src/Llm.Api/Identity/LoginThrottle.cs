using System.Collections.Concurrent;

namespace Llm.Api.Identity;

/// <summary>
/// Per-address brake on password guessing, matching what Authelia enforced:
/// 5 failures within 10 minutes bans the address for 12 hours. Per-account
/// lockout (Identity) additionally stops guessing spread across addresses.
/// </summary>
public sealed class LoginThrottle(TimeProvider clock)
{
    public const int MaxFailures = 5;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan Ban = TimeSpan.FromHours(12);

    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    public bool IsBanned(string? ip) =>
        ip is not null && _entries.TryGetValue(ip, out var e) && e.BannedUntil > clock.GetUtcNow();

    public void Failure(string? ip)
    {
        if (ip is null)
        {
            return;
        }
        var now = clock.GetUtcNow();
        _entries.AddOrUpdate(ip,
            _ => new Entry([now], null),
            (_, e) =>
            {
                var recent = e.Failures.Where(t => now - t < Window).Append(now).ToArray();
                return new Entry(recent, recent.Length >= MaxFailures ? now + Ban : e.BannedUntil);
            });
        if (_entries.Count > 10_000)
        {
            foreach (var (key, e) in _entries)
            {
                if ((e.BannedUntil ?? DateTimeOffset.MinValue) < now && e.Failures.All(t => now - t >= Window))
                {
                    _entries.TryRemove(key, out _);
                }
            }
        }
    }

    public void Success(string? ip)
    {
        if (ip is not null && !IsBanned(ip))
        {
            _entries.TryRemove(ip, out _);
        }
    }

    private sealed record Entry(DateTimeOffset[] Failures, DateTimeOffset? BannedUntil);
}
