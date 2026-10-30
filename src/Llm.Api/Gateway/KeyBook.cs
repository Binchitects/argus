using System.Collections.Concurrent;
using Llm.Api.Operations;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Gateway;

/// <summary>Whose an API key is, as the app keeps it: the key's hash, its person now (their email as it is today), its kind and expiry.</summary>
/// <param name="Models">The models the key may call now (<see cref="Models.KeyAccess.ListForAsync"/>): empty, every model.</param>
/// <param name="ApiOff">The person's API access is off (Admin → People, or a rule for a group of theirs).</param>
public sealed record KeyHolder(string Hash, Guid UserId, string Email, string UserName, string Kind, string Alias, DateTimeOffset? ExpiresAt, int? Parallel,
    bool Disabled, bool ApiOff, bool CacheApiAnswers, IReadOnlyList<string> Models)
{
    /// <summary>Expired at this time: refused as an unknown key would be.</summary>
    public bool ExpiredAt(DateTimeOffset now) => ExpiresAt is { } at && at <= now;
}

/// <summary>
/// One answer to "whose is this key?" for every part of the app that takes an API key (the answers' line, the answer
/// cache, Argus's key check, Arena MCP, speech, a key's own requests at once): read from gateway.api_keys and believed
/// for ten seconds (LiteLLM's own key cache's time), forgotten at once on every replica when a key is made, changed or
/// revoked (the topic keys:changed). A key the app does not know has no holder. Whether the person is disabled, or has
/// no API access, each caller asks of the person now.
/// </summary>
public sealed class KeyBook
{
    private const string Topic = "keys:changed";

    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _clock;
    private readonly Replicas _replicas;
    private readonly ConcurrentDictionary<string, (KeyHolder? Holder, DateTimeOffset At)> _known = new(StringComparer.Ordinal);

    /// <summary>How long a key's holder is believed (Gateway:KeyCacheSeconds, 10 unless said; a test sets 0).</summary>
    public TimeSpan Remember { get; }

    public KeyBook(IServiceScopeFactory scopes, TimeProvider clock, Replicas replicas, IConfiguration config)
    {
        (_scopes, _clock, _replicas) = (scopes, clock, replicas);
        Remember = TimeSpan.FromSeconds(config.GetValue("Gateway:KeyCacheSeconds", 10));
        replicas.On(Topic, hash =>
        {
            ForgetHere(hash);
            return true;
        });
    }

    /// <summary>How many times the database was asked (a test sees what was believed).</summary>
    public int Reads => _reads;

    private int _reads;

    /// <summary>The holder of a key, by the key itself.</summary>
    public Task<KeyHolder?> ForAsync(string key, CancellationToken ct) => ForHashAsync(AnswerCache.HashOf(key), ct);

    /// <summary>The holder of a key, by its hash.</summary>
    public async Task<KeyHolder?> ForHashAsync(string hash, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        if (_known.TryGetValue(hash, out var known) && now - known.At < Remember)
        {
            return known.Holder;
        }
        Interlocked.Increment(ref _reads);
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var found = await (from k in db.ApiKeys.AsNoTracking()
                           join u in db.Users.AsNoTracking() on k.UserId equals u.Id
                           where k.Hash == hash
                           select new { Key = k, User = u })
            .FirstOrDefaultAsync(ct);
        var holder = found is null ? null : new KeyHolder(found.Key.Hash.TrimEnd(), found.User.Id, found.User.Email ?? "", found.User.UserName ?? "", found.Key.Kind,
            found.Key.Alias, found.Key.ExpiresAt, found.Key.Parallel, found.User.IsDisabled,
            await scope.ServiceProvider.GetRequiredService<Access.AccessService>().ApiOffAsync(found.User, ct), found.User.CacheApiAnswers,
            await scope.ServiceProvider.GetRequiredService<Models.KeyAccess>().ListForAsync(found.User, ct));
        if (_known.Count > 10_000)
        {
            _known.Clear();
        }
        _known[hash] = (holder, now);
        return holder;
    }

    /// <summary>A key made, changed or revoked (none named: any, a person's state or rights changed): read again everywhere.</summary>
    public void Changed(string? hash = null)
    {
        ForgetHere(hash ?? "");
        _replicas.Tell(Topic, hash ?? "");
    }

    private void ForgetHere(string hash)
    {
        if (hash.Length == 0)
        {
            _known.Clear();
        }
        else
        {
            _known.TryRemove(hash, out _);
        }
    }
}
