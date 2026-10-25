using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Chat;

/// <summary>
/// Memory's upkeep, on the replica that leads: memories kept while the embedder was away (or before there was one) are
/// given their vectors, a few hundred at a time, so recall finds them by meaning.
/// </summary>
public sealed partial class MemoryUpkeep(IServiceScopeFactory scopes, Operations.Replicas replicas, TimeProvider clock, ILogger<MemoryUpkeep> logger) : BackgroundService
{
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(10);

    /// <summary>Memories given their vector in one pass at most.</summary>
    public const int MostAtOnce = 500;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Every, clock);
        do
        {
            if (!replicas.IsLeader)
            {
                continue;
            }
            try
            {
                await RunAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or Npgsql.NpgsqlException)
            {
                LogFailed(logger, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>One pass: how many memories were given their vector.</summary>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<Memories>().EmbedMissingAsync(MostAtOnce, ct);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Memory upkeep failed")]
    private static partial void LogFailed(ILogger logger, Exception ex);
}
