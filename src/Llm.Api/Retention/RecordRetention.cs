using Llm.Api.Identity;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Llm.Api.Retention;

/// <summary>What one sweep of the records removed, kind by kind.</summary>
public sealed record RecordsSwept(int Audit, int GatewayLog, int ToolCalls, int Notifications, int PromptLog)
{
    public int Total => Audit + GatewayLog + ToolCalls + Notifications + PromptLog;
}

/// <summary>
/// The records kept for a set time, and no longer (GDPR's storage limitation): the audit log (six years unless set), the
/// gateway's request log, the record of tool calls, and people's bells. Removed by the hourly sweep, each removal audited with
/// counts. The audit log's chain starts again from its oldest entry kept; logs and metrics are their own services' (Loki, Prometheus).
/// </summary>
public sealed partial class RecordRetention(AppDbContext db, Gateway.SpendLog spend, Audit audit, IOptionsMonitor<RetentionOptions> options, TimeProvider clock,
    ILogger<RecordRetention> logger)
{
    public async Task<RecordsSwept> SweepAsync(CancellationToken ct)
    {
        var o = options.CurrentValue;
        var now = clock.GetUtcNow();
        var auditGone = o.AuditYears > 0 ? await AuditAsync(now.AddYears(-o.AuditYears), ct) : 0;
        var toolsGone = o.ToolCallDays > 0 ? await db.ToolCalls.Where(c => c.At < now.AddDays(-o.ToolCallDays)).ExecuteDeleteAsync(ct) : 0;
        var newsGone = o.NotificationDays > 0 ? await db.Notifications.Where(n => n.CreatedAt < now.AddDays(-o.NotificationDays)).ExecuteDeleteAsync(ct) : 0;
        var promptsGone = o.PromptLogDays > 0 ? await db.PromptLog.Where(p => p.At < now.AddDays(-o.PromptLogDays)).ExecuteDeleteAsync(ct) : 0;
        var gatewayGone = 0;
        if (o.GatewayLogDays > 0)
        {
            // The app's own ledger keeps them as long as LiteLLM's log did.
            var before = now.AddDays(-o.GatewayLogDays);
            await db.GatewayRequests.Where(r => r.StartedAt < before).ExecuteDeleteAsync(ct);
            try
            {
                gatewayGone = await spend.DeleteBeforeAsync(before, ct);
            }
            catch (NpgsqlException ex)
            {
                LogGateway(logger, ex.Message);
            }
        }
        var swept = new RecordsSwept(auditGone, gatewayGone, toolsGone, newsGone, promptsGone);
        if (swept.Total > 0)
        {
            await audit.WriteAsync("retention.records", detail:
                $"past their time: {auditGone} audit entries, {gatewayGone} gateway requests, {toolsGone} tool calls, {newsGone} notifications, {promptsGone} API requests in the prompt log");
        }
        return swept;
    }

    /// <summary>
    /// The audit log's oldest entries, up to the first one still within its time: only ever the start of the chain (an entry made
    /// late for a call that ran long may be older than one before it), so what is kept is still one unbroken chain.
    /// </summary>
    private async Task<int> AuditAsync(DateTimeOffset before, CancellationToken ct)
    {
        var firstKept = await db.AuditEvents.Where(e => e.At >= before).MinAsync(e => (long?)e.Id, ct);
        return firstKept is { } id
            ? await db.AuditEvents.Where(e => e.Id < id).ExecuteDeleteAsync(ct)
            : await db.AuditEvents.ExecuteDeleteAsync(ct);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Retention: the gateway's request log could not be swept: {Reason}")]
    private static partial void LogGateway(ILogger logger, string reason);
}
