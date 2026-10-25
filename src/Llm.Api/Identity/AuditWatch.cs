using Llm.Core.Data;
using Microsoft.Extensions.Options;

namespace Llm.Api.Identity;

/// <summary>Configuration section "Audit": where the chain's anchors are kept apart, and how often it is walked.</summary>
public sealed class AuditOptions
{
    /// <summary>A folder outside the database (compose's audit volume) for the daily anchors; empty: they are only logged.</summary>
    public string? AnchorDir { get; set; } = "/audit";

    /// <summary>How often the whole chain is walked and, when it holds, anchored.</summary>
    public TimeSpan VerifyEvery { get; set; } = TimeSpan.FromDays(1);
}

/// <summary>The newest walk of the chain, for Admin → Compliance.</summary>
public sealed class AuditChainState
{
    public AuditChainReport? Last { get; set; }
    public AuditAnchor? LastAnchor { get; set; }
}

/// <summary>
/// Seals what was written before the chain existed, then walks the whole chain every day: when it holds, the newest entry is
/// anchored apart (a file in its own volume, and the log); when it is broken, every admin is told, once a break.
/// </summary>
public sealed partial class AuditWatch(IServiceScopeFactory scopes, IOptionsMonitor<AuditOptions> options, AuditChainState state, TimeProvider clock,
    ILogger<AuditWatch> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using (var scope = scopes.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                if (await AuditChain.SealAsync(db, stoppingToken) is > 0 and var n)
                {
                    LogSealed(logger, n);
                }
            }
            while (!stoppingToken.IsCancellationRequested)
            {
                await WalkAsync(anchor: true, stoppingToken);
                await Task.Delay(options.CurrentValue.VerifyEvery, clock, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>Walks the chain now; when it holds and <paramref name="anchor"/>, anchors its newest entry; when broken, tells the admins.</summary>
    public async Task<AuditChainReport> WalkAsync(bool anchor, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var dir = options.CurrentValue.AnchorDir;
        var anchors = AuditChain.ReadAnchors(string.IsNullOrWhiteSpace(dir) ? null : dir);
        var report = await AuditChain.VerifyAsync(db, anchors, clock.GetUtcNow(), ct);
        state.Last = report;
        state.LastAnchor ??= anchors.Count > 0 ? anchors[^1] : null;
        if (!report.Holds)
        {
            var where = report.BrokenAt is { } at ? $"at entry {at}: {report.Why}" : $"the anchors of entries {string.Join(", ", report.AnchorsWrong)} no longer match";
            LogBroken(logger, where);
            await scope.ServiceProvider.GetRequiredService<Notifications.Notifier>().ToAdminsAsync(new Notifications.News("alert",
                "The audit log was changed", $"Its hash chain is broken {where}. Someone with access to the database changed it: find out who, from the database's own logs and the backups.",
                "/admin/compliance", $"audit-chain:{report.BrokenAt}:{string.Join(',', report.AnchorsWrong)}"), ct);
            await scope.ServiceProvider.GetRequiredService<Audit>().WriteAsync("audit.chain_broken", success: false, detail: where);
            return report;
        }
        if (anchor && report.LastId is { } last && report.LastHash is { } hash && state.LastAnchor?.Id != last)
        {
            var a = new AuditAnchor(report.At, last, hash, report.Entries);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                try
                {
                    AuditChain.AppendAnchor(dir, a);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    LogAnchorNotKept(logger, dir, ex.Message);
                }
            }
            // The log is a second place apart from the database (Loki keeps it).
            LogAnchor(logger, a.Id, a.Hash, a.Entries);
            state.LastAnchor = a;
        }
        return report;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Audit chain: {Count} entries written before it existed were sealed")]
    private static partial void LogSealed(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Audit chain broken {Where}")]
    private static partial void LogBroken(ILogger logger, string where);

    [LoggerMessage(Level = LogLevel.Information, Message = "Audit chain anchor: entry {Id}, hash {Hash}, {Entries} entries")]
    private static partial void LogAnchor(ILogger logger, long id, string hash, long entries);

    [LoggerMessage(Level = LogLevel.Error, Message = "Audit chain: the anchor could not be kept in {Dir}: {Reason}")]
    private static partial void LogAnchorNotKept(ILogger logger, string dir, string reason);
}
