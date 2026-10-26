using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Llm.Api.Schedules;
using Llm.Core.Data;
using Llm.Core.Studio;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Studio;

/// <summary>
/// Agent Studio's runs: started (by hand, a form, a schedule, a webhook, GitLab or a room's message) and run in the
/// background, one task each, stoppable; a run that waits for an approval goes on when it is given. Schedules are looked at
/// every half minute (each due run claimed, so one replica runs it); a run left "running" by an app that stopped is ended.
/// </summary>
public sealed partial class StudioRuns(IServiceScopeFactory scopes, TimeProvider clock, ILogger<StudioRuns> logger) : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Abandoned = TimeSpan.FromHours(2);
    private readonly ConcurrentDictionary<Guid, (Task Task, CancellationTokenSource Stop)> _running = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _posted = new();

    /// <summary>A new run of the workflow, started in the background; its id.</summary>
    public async Task<Guid> StartAsync(Workflow w, string trigger, JsonObject input, Guid? by, GitLabTarget? reply, CancellationToken ct)
    {
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var run = new WorkflowRun
            {
                WorkflowId = w.Id, Flow = w.Flow, Trigger = trigger, ById = by, Input = input.ToJsonString(),
                Reply = reply is null ? null : JsonSerializer.Serialize(reply, FlowModel.Json), StartedAt = clock.GetUtcNow(),
            };
            db.WorkflowRuns.Add(run);
            await db.Workflows.Where(x => x.Id == w.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.LastRunAt, run.StartedAt), ct);
            await db.SaveChangesAsync(ct);
            Go(run.Id);
            return run.Id;
        }
    }

    /// <summary>Runs (or goes on with) a run, in the background.</summary>
    public void Go(Guid run)
    {
        var stop = new CancellationTokenSource();
        var task = Task.Run(async () =>
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<FlowRunner>().RunAsync(run, stop.Token);
            }
            catch (Exception ex)
            {
                RunFailed(logger, run, ex);
            }
            finally
            {
                _running.TryRemove(run, out _);
                stop.Dispose();
            }
        });
        _running[run] = (task, stop);
    }

    /// <summary>Stops a run going on here; false when it is not running here.</summary>
    public bool Stop(Guid run)
    {
        if (!_running.TryGetValue(run, out var r))
        {
            return false;
        }
        r.Stop.Cancel();
        return true;
    }

    /// <summary>The run's work here, to wait for (tests); done when it is not running here.</summary>
    public Task Finished(Guid run) => _running.TryGetValue(run, out var r) ? r.Task : Task.CompletedTask;

    /// <summary>A workflow said this in a room: a message of its own does not start a room workflow.</summary>
    public void Posted(Guid room, string text) => _posted[$"{room}:{text.Trim()}"] = clock.GetUtcNow();

    /// <summary>A room's message, heard in the background (the room does not wait for its workflows).</summary>
    public void RoomMessage(Guid room, Guid author, string text) => _ = Task.Run(async () =>
    {
        try
        {
            await RoomMessageAsync(room, author, text, CancellationToken.None);
        }
        catch (Exception ex)
        {
            RoomFailed(logger, room, ex);
        }
    });

    /// <summary>A message said in a room: the room workflows whose word it starts with run, as their owners.</summary>
    public async Task RoomMessageAsync(Guid room, Guid author, string text, CancellationToken ct)
    {
        var said = text.Trim();
        if (_posted.TryRemove($"{room}:{said}", out var at) && clock.GetUtcNow() - at < TimeSpan.FromMinutes(5))
        {
            return;
        }
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var key = room.ToString();
        var candidates = await db.Workflows.AsNoTracking().Where(w => w.Enabled && w.Flow.Contains(key)).ToListAsync(ct);
        var name = await db.Users.AsNoTracking().Where(u => u.Id == author).Select(u => u.DisplayName == "" ? u.UserName : u.DisplayName).SingleOrDefaultAsync(ct);
        foreach (var w in candidates)
        {
            var t = FlowModel.Read(w.Flow).Trigger;
            if (t.Kind != "room" || t.RoomId != room || t.Keyword is not { Length: > 0 } word || !said.StartsWith(word, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            await StartAsync(w, "room", new JsonObject { ["text"] = said[word.Length..].Trim(), ["by"] = name, ["room"] = key }, author, null, ct);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Every, clock);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await DueAsync(stoppingToken);
                await AbandonedAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                SweepFailed(logger, ex);
            }
        }
    }

    /// <summary>The workflows whose schedule has come: each claimed (its next time moved on) before it runs, so one replica runs it.</summary>
    public async Task<int> DueAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = clock.GetUtcNow();
        var due = await db.Workflows.AsNoTracking().Where(w => w.Enabled && w.NextRunAt != null && w.NextRunAt <= now).Take(20).ToListAsync(ct);
        var started = 0;
        foreach (var w in due)
        {
            var next = NextRun(FlowModel.Read(w.Flow).Trigger, now);
            var was = w.NextRunAt;
            if (await db.Workflows.Where(x => x.Id == w.Id && x.NextRunAt == was).ExecuteUpdateAsync(s => s.SetProperty(x => x.NextRunAt, next), ct) == 1)
            {
                await StartAsync(w, "schedule", new JsonObject { ["text"] = "" }, null, null, ct);
                started++;
            }
        }
        return started;
    }

    /// <summary>When a schedule runs a workflow next after this moment, or null (no schedule).</summary>
    public static DateTimeOffset? NextRun(FlowTrigger t, DateTimeOffset after)
    {
        if (t.Kind != "schedule" || Cron.Parse(t.Cron).Cron is not { } cron)
        {
            return null;
        }
        var (zone, _) = Models.Hours.Zone(t.TimeZone);
        return cron.Next(after, zone);
    }

    /// <summary>Runs left "running" long after their last step (the app that ran them stopped): ended, saying so.</summary>
    private async Task AbandonedAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var before = clock.GetUtcNow() - Abandoned;
        var stale = await db.WorkflowRuns.Where(r => r.Status == WorkflowRun.Running && r.StartedAt < before
            && !db.WorkflowRunSteps.Any(s => s.RunId == r.Id && s.StartedAt >= before)).Take(50).ToListAsync(ct);
        foreach (var r in stale.Where(r => !_running.ContainsKey(r.Id)))
        {
            r.Status = WorkflowRun.Failed;
            r.Error = "It stopped: the app restarted while it ran, or a step never ended.";
            r.FinishedAt = clock.GetUtcNow();
        }
        await db.SaveChangesAsync(ct);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Workflow run {Run} failed")]
    private static partial void RunFailed(ILogger logger, Guid run, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A message in room {Room} could not start its workflows")]
    private static partial void RoomFailed(ILogger logger, Guid room, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Agent Studio's schedules could not be looked at")]
    private static partial void SweepFailed(ILogger logger, Exception ex);
}
