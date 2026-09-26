using System.Security.Claims;
using Llm.Core.Data;
using Llm.Core.Identity;

namespace Llm.Api.Identity;

/// <summary>
/// Writes through its own context, so an audit entry never flushes (or trips on)
/// whatever else the request's context is tracking -- a failed sign-in leaves the
/// person's row modified, and saving that again was a concurrency error. Measured.
/// </summary>
public sealed class Audit(Microsoft.EntityFrameworkCore.DbContextOptions<AppDbContext> options, IHttpContextAccessor http)
{
    public async Task WriteAsync(string action, string? target = null, bool success = true, string? detail = null, AppUser? actor = null)
    {
        await using var db = new AppDbContext(options);
        var ctx = http.HttpContext;
        var principal = ctx?.User;
        db.AuditEvents.Add(new AuditEvent
        {
            Action = action,
            Target = target,
            Success = success,
            Detail = detail,
            ActorId = actor?.Id ?? (Guid.TryParse(principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null),
            Actor = actor?.UserName ?? principal?.Identity?.Name,
            Ip = ctx?.Connection.RemoteIpAddress?.ToString(),
        });
        await db.SaveChangesAsync();
    }
}
