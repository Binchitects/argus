using System.Text.Json.Nodes;
using Llm.Api.Models;
using Llm.Api.Schedules;
using Microsoft.Extensions.Options;

using Branding;
namespace Llm.Api.Operations;

/// <summary>Configuration section "CodeIntelIndex": when code-intel reindexes by itself.</summary>
public sealed class CodeIntelIndexOptions
{
    /// <summary>Five-field cron in <see cref="TimeZone"/>; empty: never by itself (pushes and Index now still do).</summary>
    public string Schedule { get; set; } = "*/15 * * * *";
    public string TimeZone { get; set; } = "UTC";
}

/// <summary>
/// Starts an code-intel index pass on the schedule admins set under Indexing (checked every
/// 30 seconds). A pass already running when one is due is left to finish: the next time
/// comes round. Each pass is incremental (only what changed since the last commit indexed),
/// and covers the repositories that go with the passes: code-intel's own scheduler runs one with a
/// schedule of its own. With several replicas, only the one that leads starts it.
/// </summary>
public sealed partial class CodeIntelIndexSchedule(IServiceScopeFactory scopes, IOptionsMonitor<CodeIntelIndexOptions> options, TimeProvider clock,
    Replicas replicas, ILogger<CodeIntelIndexSchedule> logger) : BackgroundService
{
    /// <summary>The shortest gap between two scheduled passes.</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(5);

    /// <summary>The schedule as set, read: the cron (null when off), its zone, and why it cannot be used, if so.</summary>
    public static (Cron? Cron, TimeZoneInfo Zone, string? Problem) Read(CodeIntelIndexOptions o)
    {
        var (zone, zoneProblem) = Hours.Zone(o.TimeZone);
        if (string.IsNullOrWhiteSpace(o.Schedule))
        {
            return (null, zone, zoneProblem);
        }
        var (cron, problem) = Cron.Parse(o.Schedule);
        return (cron, zone, problem ?? zoneProblem);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DateTimeOffset? next = null;
        string? set = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), clock, stoppingToken);
                var o = options.CurrentValue;
                var (cron, zone, _) = Read(o);
                var now = clock.GetUtcNow();
                if (cron is null)
                {
                    (next, set) = (null, null);
                    continue;
                }
                // A new schedule counts from now: no pass for times that went by while it was another.
                if (set != $"{o.Schedule}|{o.TimeZone}")
                {
                    (next, set) = (cron.Next(now, zone), $"{o.Schedule}|{o.TimeZone}");
                    continue;
                }
                if (next is not { } due || now < due)
                {
                    continue;
                }
                next = cron.Next(now, zone);
                if (!replicas.IsLeader)
                {
                    continue;
                }
                await using var scope = scopes.CreateAsyncScope();
                var codeIntel = scope.ServiceProvider.GetRequiredService<CodeIntelAdmin>();
                if (!codeIntel.Enabled)
                {
                    continue;
                }
                try
                {
                    await codeIntel.PostAsync("index", new JsonObject { ["trigger"] = "schedule" }, stoppingToken);
                    LogStarted(logger, next);
                }
                catch (CodeIntelException ex)
                {
                    LogSkipped(logger, ex.Message);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Whatever went wrong, the app keeps running: a background failure must never stop it.
                LogFailed(logger, ex);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "code-intel index pass started on schedule; the next is due {Next}")]
    private static partial void LogStarted(ILogger logger, DateTimeOffset? next);

    [LoggerMessage(Level = LogLevel.Information, Message = "code-intel index pass on schedule not started: {Reason}")]
    private static partial void LogSkipped(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "The code-intel index schedule failed")]
    private static partial void LogFailed(ILogger logger, Exception ex);
}
