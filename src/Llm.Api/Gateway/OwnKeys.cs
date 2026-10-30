using System.Collections.Concurrent;
using Llm.Api.Identity;
using Llm.Core.Data;
using Llm.Core.Gateway;
using Llm.Core.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Gateway;

/// <summary>A key as its person sees it on Account → API keys: what it is, what it spent, and its limits.</summary>
/// <param name="Id">The key's hash at the gateway (never the key): what changes and revokes it.</param>
/// <param name="Kind">"own" (made here, with a name and limits), "main" (the key Rotate replaces), "studio" (a Code Studio workspace's, which it revokes itself) or "other".</param>
/// <param name="Limits">The limits set on the key itself (own keys); null for another kind.</param>
/// <param name="Effective">What the gateway holds the key to: the tighter of the person's limits and the key's own.</param>
public sealed record KeyView(string Id, string Name, string Kind, string? Preview, decimal Spend, bool Blocked, DateTimeOffset? CreatedAt, KeyLimitsSet? Limits, KeyLimitsSet Effective);

/// <summary>A key's limits: requests and tokens a minute, requests at once, and spend in 30 days (dollars); null: none of its own.</summary>
public sealed record KeyLimitsSet(int? RequestsPerMinute, int? TokensPerMinute, int? Parallel, decimal? MonthlyBudget);

/// <summary>A key asked for, or changed: its name and its own limits (each null: as many as the person may).</summary>
public sealed record KeyRequest(string? Name, int? RequestsPerMinute, int? TokensPerMinute, int? Parallel, decimal? MonthlyBudget);

/// <summary>Why a key could not be made, changed or revoked: said to the person as it is.</summary>
public sealed class OwnKeyException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

/// <summary>
/// The API keys a person makes for themselves (Account → API keys): as many as <see cref="MaxKeys"/>, each named, each with
/// limits of its own under those an admin set on them (the tighter of the two is what the gateway holds it to: the sync
/// keeps it so), each revoked on its own. Making them counts with Rotate's new keys an hour (<see cref="PeopleService.OwnKeysPerHour"/>).
/// </summary>
public sealed class OwnKeys(AppDbContext db, ILiteLlm gateway, ApiKeys keys, RateLimits rateLimits, Models.KeyAccess keyAccess, Access.AccessService access, PeopleService people,
    Audit audit, TimeProvider clock, OwnKeyPlaces places, KeyWriters writers)
{
    /// <summary>The most keys a person may make for themselves.</summary>
    public const int MaxKeys = 20;

    /// <summary>The audit log's word for a key a person made here.</summary>
    public const string Made = "person.key_made";

    /// <summary>The tighter of two limits a minute (null: none).</summary>
    private static int? Tighter(int? a, int? b) => a is null ? b : b is null ? a : Math.Min(a.Value, b.Value);

    /// <summary>What a key carries: the person's limits, tightened by the key's own where it set any.</summary>
    public static KeyRate Tighter(KeyRate person, ApiKey? own) =>
        own is null ? person : new KeyRate(Tighter(person.RequestsPerMinute, own.RequestsPerMinute), Tighter(person.TokensPerMinute, own.TokensPerMinute));

    /// <summary>How many requests at once a key may have at the gateway: anyone's key's, tightened by its own.</summary>
    public static int? TighterParallel(int? all, int? own) => Tighter(all, own);

    /// <summary>The person's keys at the gateway, each with its name and limits; none while their API access is off.</summary>
    public async Task<List<KeyView>> ListAsync(AppUser user, CancellationToken ct = default)
    {
        if (await access.ApiOffAsync(user, ct))
        {
            return [];
        }
        var held = await gateway.KeysAsync(user.Email!, ct);
        var rows = await db.ApiKeys.AsNoTracking().Where(k => k.UserId == user.Id).ToDictionaryAsync(k => k.Hash, ct);
        var rate = (await rateLimits.ForAsync(user, ct)).Key;
        return [.. held.OrderBy(k => k.CreatedAt).Select(k => View(user, k, rows.GetValueOrDefault(k.Token), rate))];
    }

    private KeyView View(AppUser user, GatewayKey k, ApiKey? row, KeyRate rate)
    {
        var own = row?.Kind == ApiKey.Own ? row : null;
        var kind = own is not null ? "own"
            : row?.Kind == ApiKey.Studio || k.Alias.StartsWith("studio:", StringComparison.Ordinal) ? "studio"
            : k.Alias == PeopleService.KeyAlias(user) ? "main" : "other";
        var name = own?.Name ?? kind switch { "main" => "Your key", "studio" => "Code Studio workspace", _ => k.Alias };
        var effective = Tighter(rate, own);
        return new KeyView(k.Token, name, kind, k.Preview, k.Spend, k.Blocked, k.CreatedAt,
            own is null ? null : new KeyLimitsSet(own.RequestsPerMinute, own.TokensPerMinute, own.Parallel, own.MonthlyBudget),
            new KeyLimitsSet(effective.RequestsPerMinute, effective.TokensPerMinute, TighterParallel(keyAccess.AtOnce, own?.Parallel), own?.MonthlyBudget));
    }

    /// <summary>A new key with its name and own limits: the key itself (shown once), and how it is listed.</summary>
    public async Task<(string Key, KeyView View)> MakeAsync(AppUser user, KeyRequest request, CancellationToken ct = default)
    {
        var name = Name(request.Name);
        Check(request);
        if (await access.ApiOffAsync(user, ct))
        {
            throw new OwnKeyException(403, "api_off", "Your API access is off: ask an admin to turn it on.");
        }
        if (user.IsDisabled)
        {
            throw new OwnKeyException(403, "disabled", "Your account is disabled.");
        }
        if (await db.ApiKeys.CountAsync(k => k.UserId == user.Id && k.Kind == ApiKey.Own, ct) >= MaxKeys)
        {
            throw new OwnKeyException(409, "too_many", $"You have {MaxKeys} keys of your own, the most there may be: revoke one you no longer use first.");
        }
        if (await db.ApiKeys.AnyAsync(k => k.UserId == user.Id && k.Kind == ApiKey.Own && k.Name == name, ct))
        {
            throw new OwnKeyException(409, "name_taken", $"You have a key named {name} already: choose another name.");
        }
        if (await people.OwnKeyWaitAsync(user, clock.GetUtcNow()) is { Ticks: > 0 } wait)
        {
            var minutes = (int)Math.Ceiling(wait.TotalMinutes);
            throw new OwnKeyException(429, "too_many_keys",
                $"You made {PeopleService.OwnKeysPerHour} new keys in the last hour, the most there may be. Make the next in {minutes} minute{(minutes == 1 ? "" : "s")}.");
        }
        var limits = new OwnLimits(request.RequestsPerMinute, request.TokensPerMinute, request.Parallel, request.MonthlyBudget);
        // Made and saved in the person's turn: a sync between the two would give the new key their limits, not its own.
        using var turn = await writers.EnterAsync(user.Id, ct);
        await gateway.EnsureUserAsync(user.Email!, ct);
        var person = (await rateLimits.ForAsync(user, ct)).Key;
        var rate = new KeyRate(Tighter(person.RequestsPerMinute, limits.RequestsPerMinute), Tighter(person.TokensPerMinute, limits.TokensPerMinute));
        // A unique alias at the gateway: the person's name, the key's, and a few random letters.
        var alias = $"{PeopleService.KeyAlias(user)}-own-{Guid.NewGuid().ToString("N")[..8]}";
        var (key, own) = await keys.MakeAsync(user, ApiKey.Own, alias, await keyAccess.ListForAsync(user, ct), TighterParallel(keyAccess.MaxParallel, limits.Parallel), rate, name, own: limits, ct: ct);
        turn.Dispose();
        places.Forget(own.Hash);
        // Counted with Rotate's new keys an hour (PeopleService.OwnKeyWaitAsync).
        await audit.WriteAsync(Made, user.UserName, detail: $"{name}: {Describe(own)}", actor: user);
        var made = (await gateway.KeysAsync(user.Email!, ct)).FirstOrDefault(k => k.Token == own.Hash)
            ?? new GatewayKey(own.Hash, alias, own.Preview, 0, false, own.CreatedAt, Rate: rate);
        return (key, View(user, made, own, (await rateLimits.ForAsync(user, ct)).Key));
    }

    /// <summary>A key of theirs renamed, or its limits changed (a key not made here takes a name and limits too).</summary>
    public async Task<KeyView> ChangeAsync(AppUser user, string token, KeyRequest request, CancellationToken ct = default)
    {
        Check(request);
        // Changed, saved and written in the person's turn (KeyWriters).
        using var turn = await writers.EnterAsync(user.Id, ct);
        var key = await KeyOfAsync(user, token, ct);
        var own = await db.ApiKeys.SingleOrDefaultAsync(k => k.Hash == token && k.UserId == user.Id, ct);
        if (own?.Kind == ApiKey.Studio || key.Alias.StartsWith("studio:", StringComparison.Ordinal))
        {
            throw new OwnKeyException(409, "studio", "A Code Studio workspace's key is the workspace's: it goes when the workspace does.");
        }
        if (own is null)
        {
            // A key the mirror has not brought over yet (it looks every half minute): kept here now.
            var alias = await db.ApiKeys.AnyAsync(k => k.Alias == key.Alias, ct) ? $"{key.Alias}-{token[..8]}" : key.Alias;
            own = new ApiKey { Hash = token, UserId = user.Id, Kind = ApiKey.Own, Alias = alias, Preview = key.Preview ?? "sk-...", CreatedAt = key.CreatedAt ?? clock.GetUtcNow() };
            db.ApiKeys.Add(own);
        }
        if (own.Kind != ApiKey.Own)
        {
            // A key not made here (the main key) takes a name and limits too: it is one of the person's own from now on.
            own.Kind = ApiKey.Own;
        }
        own.Name ??= key.Alias == PeopleService.KeyAlias(user) ? "Your key" : key.Alias;
        if (request.Name is not null)
        {
            var name = Name(request.Name);
            if (name != own.Name && await db.ApiKeys.AnyAsync(k => k.UserId == user.Id && k.Kind == ApiKey.Own && k.Name == name && k.Hash != token, ct))
            {
                throw new OwnKeyException(409, "name_taken", $"You have a key named {name} already: choose another name.");
            }
            own.Name = name;
        }
        (own.RequestsPerMinute, own.TokensPerMinute, own.Parallel, own.MonthlyBudget) = (request.RequestsPerMinute, request.TokensPerMinute, request.Parallel, request.MonthlyBudget);
        await db.SaveChangesAsync(ct);
        var rate = Tighter((await rateLimits.ForAsync(user, ct)).Key, own);
        if ((key.Rate ?? KeyRate.None) != rate)
        {
            await gateway.SetKeyRateAsync(token, rate, ct);
        }
        var parallel = TighterParallel(keyAccess.MaxParallel, own.Parallel);
        if (key.MaxParallel != parallel)
        {
            await gateway.SetKeyAccessAsync(token, await keyAccess.ListForAsync(user, ct), parallel, ct);
        }
        if (key.MaxBudget != own.MonthlyBudget)
        {
            await gateway.SetKeyBudgetAsync(token, own.MonthlyBudget, ct);
        }
        turn.Dispose();
        places.Forget(token);
        await audit.WriteAsync("person.key_changed", user.UserName, detail: $"{own.Name}: {Describe(own)}", actor: user);
        return View(user, (await gateway.KeysAsync(user.Email!, ct)).First(k => k.Token == token), own, (await rateLimits.ForAsync(user, ct)).Key);
    }

    /// <summary>A key of theirs revoked: the gateway refuses it from now on. A Code Studio workspace's is the workspace's to revoke.</summary>
    public async Task RevokeAsync(AppUser user, string token, CancellationToken ct = default)
    {
        var key = await KeyOfAsync(user, token, ct);
        var own = await db.ApiKeys.AsNoTracking().SingleOrDefaultAsync(k => k.Hash == token && k.UserId == user.Id, ct);
        if (own?.Kind == ApiKey.Studio || key.Alias.StartsWith("studio:", StringComparison.Ordinal))
        {
            throw new OwnKeyException(409, "studio", "A Code Studio workspace's key is the workspace's: it goes when the workspace does.");
        }
        await keys.RevokeAsync([token], ct);
        places.Forget(token);
        await audit.WriteAsync("person.key_revoked", user.UserName, detail: own?.Name ?? key.Alias, actor: user);
    }

    private async Task<GatewayKey> KeyOfAsync(AppUser user, string token, CancellationToken ct) =>
        (await gateway.KeysAsync(user.Email!, ct)).FirstOrDefault(k => k.Token == token)
        ?? throw new OwnKeyException(404, "not_found", "There is no such key of yours.");

    private static string Name(string? name) =>
        name?.Trim() is { Length: > 0 and <= 80 } n && !n.Any(char.IsControl) ? n : throw new OwnKeyException(400, "name", "Name the key (at most 80 characters): what uses it, as \"CI\" or \"laptop\".");

    private static void Check(KeyRequest r)
    {
        if (r.RequestsPerMinute is < 1 or > RateLimits.MaxRequests || r.TokensPerMinute is < 1 or > RateLimits.MaxTokens || r.Parallel is < 1 or > 1000 || r.MonthlyBudget is <= 0 or > 1_000_000)
        {
            throw new OwnKeyException(400, "limits", "A limit is a whole number above 0 (requests and tokens a minute, requests at once) or an amount above 0 (dollars in 30 days); leave it empty for none.");
        }
    }

    private static string Describe(ApiKey k) =>
        string.Join(", ", new[]
        {
            k.RequestsPerMinute is { } r ? $"{r} requests a minute" : null, k.TokensPerMinute is { } t ? $"{t} tokens a minute" : null,
            k.Parallel is { } p ? $"{p} at once" : null, k.MonthlyBudget is { } b ? $"${b} in 30 days" : null,
        }.OfType<string>()) is { Length: > 0 } said ? said : "no limits of its own";
}

/// <summary>
/// Each key's own requests at once (api_keys.parallel) for the app's line (<see cref="KeyLine"/>), read once a minute at
/// most per key, and at once after the key changes here. Null: none of its own.
/// </summary>
public sealed class OwnKeyPlaces(IServiceScopeFactory scopes, TimeProvider clock)
{
    private readonly ConcurrentDictionary<string, (int? Parallel, DateTimeOffset At)> _known = new(StringComparer.Ordinal);

    public async Task<int?> ForAsync(string token, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (_known.TryGetValue(token, out var known) && now - known.At < TimeSpan.FromMinutes(1))
        {
            return known.Parallel;
        }
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var parallel = await db.ApiKeys.AsNoTracking().Where(k => k.Hash == token).Select(k => k.Parallel).FirstOrDefaultAsync(ct);
        _known[token] = (parallel, now);
        return parallel;
    }

    /// <summary>The key changed or went: read again at its next request.</summary>
    public void Forget(string token) => _known.TryRemove(token, out _);
}
