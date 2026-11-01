using System.Text.Json;
using Llm.Api.Identity;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Chat;

public sealed partial class AnswerJobs
{
    private const string RemoveTopic = "line:remove";

    /// <summary>An admin's removal from the line, as it goes to the other replicas: the request's ticket, why, and who asked.</summary>
    private sealed record Removal(Guid Ticket, string Reason, Guid Actor);

    /// <summary>
    /// For Admin → Line: takes the request with this ticket out of the line, or stops it running, on whichever replica
    /// holds it (each keeps its own line), and audits it there. "waiting" or "running" when this replica held it,
    /// "elsewhere" when another did, null when none had it (it ended meanwhile).
    /// </summary>
    public async Task<string?> RemoveAnywhereAsync(Guid ticket, string reason, AppUser actor, CancellationToken ct)
    {
        if (gate.Remove(ticket, reason) is { } removed)
        {
            await AuditRemovalAsync(removed, reason, actor.Id, here: true);
            return removed.Running ? "running" : "waiting";
        }
        return await replicas.AskAsync(RemoveTopic, JsonSerializer.Serialize(new Removal(ticket, reason, actor.Id), Json), ct) ? "elsewhere" : null;
    }

    /// <summary>Another replica's admin asked: done here when this replica holds the request, and audited here.</summary>
    private bool RemoveHere(string payload)
    {
        Removal? removal;
        try
        {
            removal = JsonSerializer.Deserialize<Removal>(payload, Json);
        }
        catch (JsonException)
        {
            return false;
        }
        if (removal is null || gate.Remove(removal.Ticket, removal.Reason) is not { } removed)
        {
            return false;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await AuditRemovalAsync(removed, removal.Reason, removal.Actor, here: false);
            }
            catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or Npgsql.NpgsqlException)
            {
                LogRemovalAuditFailed(logger, removal.Ticket, ex);
            }
        }, CancellationToken.None);
        return true;
    }

    /// <summary>
    /// line.remove (it waited) or line.stop (it ran), with whose request it was, its model, chat or API, how long, and why;
    /// done by another replica, which one.
    /// </summary>
    private async Task AuditRemovalAsync(AnswerGate.Removed removed, string reason, Guid actorId, bool here)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var whose = await services.GetRequiredService<AppDbContext>().Users.AsNoTracking().Where(u => u.Id == removed.Person).Select(u => u.UserName).SingleOrDefaultAsync();
        var actor = await services.GetRequiredService<UserManager<AppUser>>().FindByIdAsync(actorId.ToString());
        var where = here ? "" : $", on {replicas.Id}";
        await services.GetRequiredService<Audit>().WriteAsync(removed.Running ? "line.stop" : "line.remove", whose ?? removed.Person.ToString(),
            detail: $"{removed.Model}, {removed.Kind}, {(removed.Running ? "ran" : "waited")} {removed.For.TotalSeconds:0} s{where}: {reason}", actor: actor);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Request {Ticket}: taken out of the line, but its audit entry could not be written")]
    private static partial void LogRemovalAuditFailed(ILogger logger, Guid ticket, Exception ex);
}
