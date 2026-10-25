using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Llm.Core.Data;
using Llm.Core.Identity;

namespace Llm.Api.Identity;

/// <summary>
/// Writes through its own context, so an audit entry never flushes (or trips on)
/// whatever else the request's context is tracking -- a failed sign-in leaves the
/// person's row modified, and saving that again was a concurrency error. Measured.
/// Each entry is sealed onto the hash chain (AuditChain) as it is written.
/// </summary>
public sealed class Audit(Microsoft.EntityFrameworkCore.DbContextOptions<AppDbContext> options, IHttpContextAccessor http, TimeProvider clock)
{
    /// <param name="at">When it happened, if not now: a call that ran long is entered at its start.</param>
    public async Task WriteAsync(string action, string? target = null, bool success = true, string? detail = null, AppUser? actor = null, DateTimeOffset? at = null)
    {
        await using var db = new AppDbContext(options);
        var ctx = http.HttpContext;
        var principal = ctx?.User;
        // One writer at a time across replicas: the entry takes the next id and is sealed onto the chain before the lock is let go.
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_xact_lock({AuditChain.Lock})");
        db.AuditEvents.Add(new AuditEvent
        {
            At = AuditChain.Stored(at ?? clock.GetUtcNow()),
            Action = action,
            Target = target,
            Success = success,
            Detail = detail,
            ActorId = actor?.Id ?? (Guid.TryParse(principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null),
            Actor = actor?.UserName ?? principal?.Identity?.Name,
            Ip = ctx?.Connection.RemoteIpAddress?.ToString(),
        });
        await db.SaveChangesAsync();
        await AuditChain.SealAsync(db);
        await tx.CommitAsync();
    }
}
