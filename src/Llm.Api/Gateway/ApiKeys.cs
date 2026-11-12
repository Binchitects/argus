using System.Security.Cryptography;
using Llm.Core.Data;
using Llm.Core.Gateway;
using Llm.Core.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Gateway;

/// <summary>An own key's limits as the person set them (each null: as many as the person may).</summary>
public sealed record OwnLimits(int? RequestsPerMinute, int? TokensPerMinute, int? Parallel, decimal? MonthlyBudget);

/// <summary>
/// The people's API keys, kept by the app (gateway.api_keys): each made here, the secret shown once and kept only as its
/// SHA-256. What a key may do (its models, limits, budget) is decided at each request (<see cref="Admission"/>); its spend
/// is the ledger's.
/// </summary>
public sealed class ApiKeys(AppDbContext db, KeyBook book, TimeProvider clock, Access.AccessService access)
{
    /// <summary>A new key: the secret (shown once), and its row.</summary>
    public async Task<(string Secret, ApiKey Key)> MakeAsync(AppUser user, string kind, string alias, string? name = null, Guid? workspace = null, OwnLimits? own = null,
        CancellationToken ct = default)
    {
        var secret = NewSecret();
        var key = new ApiKey
        {
            Hash = AnswerCache.HashOf(secret), UserId = user.Id, Kind = kind, Alias = alias, Name = name, Preview = PreviewOf(secret), WorkspaceId = workspace,
            RequestsPerMinute = own?.RequestsPerMinute, TokensPerMinute = own?.TokensPerMinute, Parallel = own?.Parallel, MonthlyBudget = own?.MonthlyBudget,
            CreatedAt = clock.GetUtcNow(),
        };
        db.ApiKeys.Add(key);
        await db.SaveChangesAsync(ct);
        book.Changed(key.Hash);
        return (secret, key);
    }

    /// <summary>Keys revoked: refused from now on, and forgotten.</summary>
    public async Task RevokeAsync(IReadOnlyCollection<string> hashes, CancellationToken ct = default)
    {
        if (hashes.Count == 0)
        {
            return;
        }
        await db.ApiKeys.Where(k => hashes.Contains(k.Hash)).ExecuteDeleteAsync(ct);
        foreach (var hash in hashes)
        {
            book.Changed(hash);
        }
    }

    /// <summary>
    /// A person's keys as their pages show them, the oldest first: each one's spend in the ledger (all of it, LiteLLM's
    /// imported too), blocked while the person is turned off or has no API access, its own models and limits.
    /// </summary>
    public async Task<IReadOnlyList<GatewayKey>> KeysAsync(AppUser user, CancellationToken ct = default)
    {
        var rows = await OfAsync(user.Id, ct);
        if (rows.Count == 0)
        {
            return [];
        }
        var hashes = rows.Select(r => r.Hash).ToList();
        var spent = await db.GatewayRequests.AsNoTracking().Where(r => r.KeyHash != null && hashes.Contains(r.KeyHash))
            .GroupBy(r => r.KeyHash!).Select(g => new { g.Key, Cost = g.Sum(r => r.Cost) }).ToDictionaryAsync(g => g.Key, g => g.Cost, ct);
        var blocked = user.IsDisabled || (await access.MembershipAsync(user, ct)).ApiOff;
        return [.. rows.Select(r => new GatewayKey(r.Hash, r.Alias, r.Preview, spent.GetValueOrDefault(r.Hash), blocked, r.CreatedAt, null, r.Parallel, null,
            r.RequestsPerMinute is null && r.TokensPerMinute is null ? null : new KeyRate(r.RequestsPerMinute, r.TokensPerMinute), r.MonthlyBudget))];
    }

    /// <summary>The same, for the person with this email (none: no keys).</summary>
    public async Task<IReadOnlyList<GatewayKey>> KeysAsync(string email, CancellationToken ct = default)
    {
        var normalized = email.ToUpperInvariant();
        return await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.NormalizedEmail == normalized, ct) is { } user ? await KeysAsync(user, ct) : [];
    }

    /// <summary>A person's keys, the oldest first.</summary>
    public Task<List<ApiKey>> OfAsync(Guid userId, CancellationToken ct = default) =>
        db.ApiKeys.AsNoTracking().Where(k => k.UserId == userId).OrderBy(k => k.CreatedAt).ToListAsync(ct);

    /// <summary>A new key's secret: sk- and 32 random bytes (base64url).</summary>
    public static string NewSecret() => "sk-" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>What may be shown of a key: its last four characters.</summary>
    public static string PreviewOf(string secret) => "sk-..." + secret[^4..];
}
