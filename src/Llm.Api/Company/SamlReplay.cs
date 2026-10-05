using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Company;

/// <summary>
/// Assertions that have signed someone in, kept in the database until they expire:
/// one captured and posted again signs nobody in, on any replica.
/// </summary>
public sealed class SamlReplay(AppDbContext db, TimeProvider clock)
{
    /// <summary>True the first time an assertion is seen; false when it was used before.</summary>
    public async Task<bool> FirstUseAsync(string id, DateTimeOffset keepUntil, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        await db.UsedSamlAssertions.Where(a => a.ExpiresAt < now).ExecuteDeleteAsync(ct);
        // One statement: two posts of the same assertion at once cannot both be first.
        var until = keepUntil.ToUniversalTime();
        return await db.Database.ExecuteSqlAsync(
            $"""INSERT INTO saml_assertions ("Id", "ExpiresAt") VALUES ({id}, {until}) ON CONFLICT ("Id") DO NOTHING""", ct) == 1;
    }
}
