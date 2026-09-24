using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace Llm.Api.Identity;

/// <summary>Configuration section "Throttle": the brakes on password guessing (Settings page, applies at once).</summary>
public sealed class ThrottleOptions
{
    public int MaxFailuresPerAccount { get; set; } = 5;
    public int MaxFailuresPerAddress { get; set; } = 50;
    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(10);
    public TimeSpan AccountBan { get; set; } = TimeSpan.FromHours(12);
    public TimeSpan AddressBan { get; set; } = TimeSpan.FromHours(1);
}

/// <summary>
/// Brakes on password guessing, in two layers (defaults; see <see cref="ThrottleOptions"/>):
///   one account from one address: 5 failures in 10 minutes bans that pair for 12 hours;
///   one address across accounts:  50 failures in 10 minutes (spraying) bans the address for 1 hour.
/// Keyed per account as well as per address because an office behind one NAT
/// shares an address: one person's typos must not lock out their colleagues.
/// Per-account lockout (Identity) additionally stops guessing spread across addresses.
/// </summary>
public sealed class LoginThrottle(TimeProvider clock, IOptionsMonitor<ThrottleOptions> options)
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    private ThrottleOptions O => options.CurrentValue;

    public bool IsBanned(string? ip, string login) =>
        ip is not null && (Banned(ip) || Banned(Pair(ip, login)));

    public void Failure(string? ip, string login)
    {
        if (ip is null)
        {
            return;
        }
        var o = O;
        Record(ip, o.MaxFailuresPerAddress, o.AddressBan, o.Window);
        Record(Pair(ip, login), o.MaxFailuresPerAccount, o.AccountBan, o.Window);
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

    private void Record(string key, int max, TimeSpan ban, TimeSpan window)
    {
        var now = clock.GetUtcNow();
        _entries.AddOrUpdate(key,
            _ => new Entry([now], max <= 1 ? now + ban : null),
            (_, e) =>
            {
                var recent = e.Failures.Where(t => now - t < window).Append(now).ToArray();
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
        var window = O.Window;
        foreach (var (key, e) in _entries)
        {
            if ((e.BannedUntil ?? DateTimeOffset.MinValue) < now && e.Failures.All(t => now - t >= window))
            {
                _entries.TryRemove(key, out _);
            }
        }
    }

    private sealed record Entry(DateTimeOffset[] Failures, DateTimeOffset? BannedUntil);
}
