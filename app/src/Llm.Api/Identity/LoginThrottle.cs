using System.Collections.Concurrent;

namespace Llm.Api.Identity;

/// <summary>
/// Brakes on password guessing, in two layers:
///   one account from one address: 5 failures in 10 minutes bans that pair for 12 hours;
///   one address across accounts:  50 failures in 10 minutes (spraying) bans the address for 1 hour.
/// Keyed per account as well as per address because an office behind one NAT
/// shares an address: one person's typos must not lock out their colleagues.
/// Per-account lockout (Identity) additionally stops guessing spread across addresses.
/// </summary>
public sealed class LoginThrottle(TimeProvider clock)
{
    public const int MaxFailuresPerAccount = 5;
    public const int MaxFailuresPerAddress = 50;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan AccountBan = TimeSpan.FromHours(12);
    public static readonly TimeSpan AddressBan = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    public bool IsBanned(string? ip, string login) =>
        ip is not null && (Banned(ip) || Banned(Pair(ip, login)));

    public void Failure(string? ip, string login)
    {
        if (ip is null)
        {
            return;
        }
        Record(ip, MaxFailuresPerAddress, AddressBan);
        Record(Pair(ip, login), MaxFailuresPerAccount, AccountBan);
        Prune();
    }

    public void Success(string? ip, string login)
    {
        if (ip is not null)
        {
            _entries.TryRemove(Pair(ip, login), out _);
        }
    }

    private static string Pair(string ip, string login) => ip + "|" + login.Trim().ToLowerInvariant();

    private bool Banned(string key) => _entries.TryGetValue(key, out var e) && e.BannedUntil > clock.GetUtcNow();

    private void Record(string key, int max, TimeSpan ban)
    {
        var now = clock.GetUtcNow();
        _entries.AddOrUpdate(key,
            _ => new Entry([now], max <= 1 ? now + ban : null),
            (_, e) =>
            {
                var recent = e.Failures.Where(t => now - t < Window).Append(now).ToArray();
                return new Entry(recent, recent.Length >= max ? now + ban : e.BannedUntil);
            });
    }

    private void Prune()
    {
        if (_entries.Count <= 10_000)
        {
            return;
        }
        var now = clock.GetUtcNow();
        foreach (var (key, e) in _entries)
        {
            if ((e.BannedUntil ?? DateTimeOffset.MinValue) < now && e.Failures.All(t => now - t >= Window))
            {
                _entries.TryRemove(key, out _);
            }
        }
    }

    private sealed record Entry(DateTimeOffset[] Failures, DateTimeOffset? BannedUntil);
}
