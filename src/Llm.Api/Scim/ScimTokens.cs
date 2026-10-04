using System.Security.Cryptography;
using System.Text;
using Llm.Core.Data;
using Microsoft.IdentityModel.Tokens;

namespace Llm.Api.Scim;

/// <summary>
/// The one bearer token the identity provider's SCIM client uses. Shown once when
/// made; only its SHA-256 is stored (a settings row), so a database dump does not
/// reveal it. A new token replaces the old one at once.
/// </summary>
public sealed class ScimTokens(AppDbContext db)
{
    private const string Row = "scim.token_sha256";

    public async Task<string> CreateAsync(CancellationToken ct = default)
    {
        var token = "scim_" + Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        var row = await db.Settings.FindAsync([Row], ct);
        if (row is null)
        {
            db.Settings.Add(new Setting { Key = Row, Value = Hash(token) });
        }
        else
        {
            row.Value = Hash(token);
            row.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(ct);
        return token;
    }

    public async Task<bool> RevokeAsync(CancellationToken ct = default)
    {
        if (await db.Settings.FindAsync([Row], ct) is not { } row)
        {
            return false;
        }
        db.Settings.Remove(row);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>When the current token was made; null when there is none (SCIM is off).</summary>
    public async Task<DateTimeOffset?> MadeAtAsync(CancellationToken ct = default) =>
        (await db.Settings.FindAsync([Row], ct))?.UpdatedAt;

    public async Task<bool> VerifyAsync(string presented, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(presented) || await db.Settings.FindAsync([Row], ct) is not { } row)
        {
            return false;
        }
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(presented)), Encoding.ASCII.GetBytes(row.Value));
    }

    private static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
