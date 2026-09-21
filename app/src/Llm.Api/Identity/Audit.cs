using System.Security.Claims;
using Llm.Core.Data;
using Llm.Core.Identity;

namespace Llm.Api.Identity;

public sealed class Audit(AppDbContext db, IHttpContextAccessor http)
{
    public async Task WriteAsync(string action, string? target = null, bool success = true, string? detail = null, AppUser? actor = null)
    {
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
