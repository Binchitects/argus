using System.Security.Cryptography;
using Llm.Core.Data;
using Llm.Core.Gateway;
using Llm.Core.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Gateway;

/// <summary>An own key's limits as the person set them (each null: as many as the person may).</summary>
public sealed record OwnLimits(int? RequestsPerMinute, int? TokensPerMinute, int? Parallel, decimal? MonthlyBudget);

/// <summary>
/// The people's API keys, kept by the app (gateway.api_keys): each made here (the secret is shown once and kept only as its
/// SHA-256, as LiteLLM keeps it), and registered at LiteLLM under the same secret while LiteLLM still answers, with the
/// models, the limits and the budget it holds the key to. The row first, so the mirror of LiteLLM's keys never finds a key
/// the app does not know; a key LiteLLM refused is taken out again.
/// </summary>
public sealed class ApiKeys(AppDbContext db, ILiteLlm gateway, TimeProvider clock)
{
    /// <summary>A new key: the secret (shown once), and its row.</summary>
    public async Task<(string Secret, ApiKey Key)> MakeAsync(AppUser user, string kind, string alias, IReadOnlyList<string> models, int? maxParallel, KeyRate? rate,
        string? name = null, Guid? workspace = null, OwnLimits? own = null, CancellationToken ct = default)
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
        try
        {
            await gateway.GenerateKeyAsync(user.Email!, alias, secret, models, maxParallel, rate, ct);
            if (own?.MonthlyBudget is { } budget)
            {
                await gateway.SetKeyBudgetAsync(key.Hash, budget, ct);
            }
        }
        catch
        {
            db.ApiKeys.Remove(key);
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
        return (secret, key);
    }

    /// <summary>Keys revoked: LiteLLM refuses them from now on, and the app forgets them.</summary>
    public async Task RevokeAsync(IReadOnlyCollection<string> hashes, CancellationToken ct = default)
    {
        if (hashes.Count == 0)
        {
            return;
        }
        await gateway.DeleteKeysAsync(hashes, ct);
        await db.ApiKeys.Where(k => hashes.Contains(k.Hash)).ExecuteDeleteAsync(ct);
    }

    /// <summary>A person's keys, the oldest first.</summary>
    public Task<List<ApiKey>> OfAsync(Guid userId, CancellationToken ct = default) =>
        db.ApiKeys.AsNoTracking().Where(k => k.UserId == userId).OrderBy(k => k.CreatedAt).ToListAsync(ct);

    /// <summary>A new key's secret: sk- and 32 random bytes (base64url), as hard to guess as LiteLLM's own.</summary>
    public static string NewSecret() => "sk-" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>What may be shown of a key: its last four characters, as LiteLLM shows one.</summary>
    public static string PreviewOf(string secret) => "sk-..." + secret[^4..];
}
