using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using Llm.Api.Chat;
using Llm.Api.Identity;
using Llm.Api.Operations;
using Llm.Api.Settings;
using Llm.Core.Chat;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Schedules;

/// <summary>
/// One run of a scheduled task: its question asked in a chat as its owner (a new chat,
/// or the task's own carried on), answered as any answer is (in turn, with the owner's
/// model, tools and credit), then delivered: a notification, and if asked an email and
/// a webhook post.
/// </summary>
public sealed partial class TaskRunner(AppDbContext db, AnswerJobs jobs, Mailer mailer, Webhooks webhooks, GitLabBot gitlab, Audit audit, TimeProvider clock,
    IOptions<AuthOptions> auth, ILogger<TaskRunner> logger)
{
    /// <summary>Longest an answer of a task is kept for a notification and a webhook post.</summary>
    private const int Excerpt = 3000;

    /// <param name="trigger">The event that started it (a webhook, GitLab), said after the task's question; null: its schedule, or a person.</param>
    public async Task<ScheduledRun> RunAsync(Guid taskId, bool manual, CancellationToken ct, TriggerEvent? trigger = null)
    {
        var task = await db.ScheduledTasks.SingleAsync(t => t.Id == taskId, ct);
        var run = new ScheduledRun { TaskId = task.Id, Manual = manual, StartedAt = clock.GetUtcNow() };
        db.ScheduledRuns.Add(run);
        await db.SaveChangesAsync(ct);
        var owner = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == task.UserId, ct);
        if (owner is null || owner.IsDisabled)
        {
            return await EndAsync(run, "skipped", "Its owner cannot sign in (disabled).", ct);
        }
        var (zone, _) = Models.Hours.Zone(task.TimeZone);
        var local = TimeZoneInfo.ConvertTime(run.StartedAt, zone);

        // The chat: the task's own, carried on, or a new one for this run.
        var c = task.SameChat && task.ConversationId is { } kept
            ? await db.Conversations.SingleOrDefaultAsync(x => x.Id == kept && x.UserId == owner.Id, ct)
            : null;
        if (c is null)
        {
            c = new Conversation
            {
                UserId = owner.Id, Title = task.SameChat ? task.Name : $"{task.Name} · {local.ToString("d MMM HH:mm", CultureInfo.InvariantCulture)}",
                Model = task.Model, Thinking = task.Thinking, Tools = task.Tools, ScheduledTaskId = task.Id,
            };
            db.Conversations.Add(c);
            if (task.SameChat)
            {
                task.ConversationId = c.Id;
            }
        }
        c.ArchivedAt = null;
        var sequence = (await db.ChatMessages.Where(m => m.ConversationId == c.Id).MaxAsync(m => (int?)m.Sequence, ct) ?? 0) + 1;
        var question = new ChatMessage
        {
            ConversationId = c.Id, ParentId = c.CurrentLeafId, Role = "user", Sequence = sequence, Content = trigger is null ? task.Prompt : $"{task.Prompt}\n\n{trigger.Text}",
        };
        db.ChatMessages.Add(question);
        c.CurrentLeafId = question.Id;
        c.UpdatedAt = run.StartedAt;
        run.ConversationId = c.Id;
        await db.SaveChangesAsync(ct);

        if (jobs.Reserve(c.Id, owner.Id) is not { } job)
        {
            return await EndAsync(run, "skipped", "Its chat was still answering the run before.", ct);
        }
        job.Emit(new { type = "question", id = question.Id, parentId = question.ParentId });
        // The task's own notification says how it went (and its email, webhook). Nobody watches it in the chat, to allow a call.
        job.Notify = false;
        jobs.Start(job, question.Id, new AnswerOverrides(Unattended: true));
        await job.Running.WaitAsync(ct);

        // What was answered: the last words of the answer, or why there are none.
        var messages = await db.ChatMessages.AsNoTracking().Where(m => m.ConversationId == c.Id).ToDictionaryAsync(m => m.Id, ct);
        var leaf = await db.Conversations.AsNoTracking().Where(x => x.Id == c.Id).Select(x => x.CurrentLeafId).SingleAsync(ct);
        var answer = ChatService.PathTo(messages, leaf).SkipWhile(m => m.Id != question.Id).Where(m => m.Role == "assistant").ToList();
        var last = answer.LastOrDefault(m => m.Content.Length > 0) ?? answer.LastOrDefault();
        var failed = last is null || last.Status is MessageStatus.Failed or MessageStatus.Stopped;
        var text = last?.Content.Trim() is { Length: > 0 } t ? t : null;
        var error = failed ? last?.Error ?? (last?.Status == MessageStatus.Stopped ? "The answer was stopped." : "No answer came.") : null;

        var link = $"https://{auth.Value.Domain}/chat/{c.Id}";
        var title = failed ? $"{task.Name} failed" : task.Name;
        var summary = text ?? error ?? "";
        db.Notifications.Add(new Notification
        {
            UserId = owner.Id, Kind = "task", Title = title, Body = Cut(summary, 400), Link = $"/chat/{c.Id}", CreatedAt = clock.GetUtcNow(),
        });
        var delivered = new List<string>();
        if (task.Email && owner.Email is { Length: > 0 } email)
        {
            try
            {
                await mailer.SendAsync(email, $"{title} · {local.ToString("d MMM yyyy HH:mm", CultureInfo.InvariantCulture)}",
                    $"{summary}\n\n--\nOpen the chat: {link}\nThe scheduled task \"{task.Name}\" sent this; change it under Scheduled tasks.", ct);
                delivered.Add("email sent");
            }
            catch (Exception ex) when (ex is System.Net.Mail.SmtpException or InvalidOperationException or IOException or FormatException)
            {
                LogDelivery(logger, task.Id, "email", ex.Message);
                delivered.Add($"email failed: {ex.Message}");
            }
        }
        if (task.WebhookEncrypted is { } stored)
        {
            if (SettingsCrypto.Decrypt(stored, auth.Value.DataKey) is not { } url)
            {
                delivered.Add("webhook failed: its URL cannot be read (APP_DATA_KEY changed); set it again");
            }
            else
            {
                try
                {
                    await webhooks.PostAsync(url, $"{title}\n\n{Cut(summary, Excerpt)}\n\nOpen the chat: {link}", new JsonObject
                    {
                        ["task"] = task.Name, ["status"] = failed ? "failed" : "done", ["url"] = link, ["at"] = run.StartedAt.ToString("o", CultureInfo.InvariantCulture),
                    }, ct);
                    delivered.Add("webhook posted");
                }
                catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
                {
                    LogDelivery(logger, task.Id, "webhook", ex.Message);
                    delivered.Add($"webhook failed: {ex.Message}");
                }
            }
        }
        if (trigger?.Reply is { } target && task.ReplyInGitLab && !failed && text is not null)
        {
            try
            {
                await gitlab.CommentAsync(target, $"{text}\n\n---\n*{task.Name}, by Argus Arena for {owner.DisplayName}: [the chat]({link})*", ct);
                delivered.Add("commented in GitLab");
                await audit.WriteAsync("task.gitlab_comment", task.Name, detail: $"project {target.Project}, {target.Kind} {target.Id}", actor: owner);
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException)
            {
                LogDelivery(logger, task.Id, "GitLab comment", ex.Message);
                delivered.Add($"GitLab comment failed: {ex.Message}");
            }
        }
        run.Delivery = delivered.Count > 0 ? Cut(string.Join("; ", delivered), 1000) : null;
        return await EndAsync(run, failed ? "failed" : "done", error, ct);
    }

    private async Task<ScheduledRun> EndAsync(ScheduledRun run, string status, string? error, CancellationToken ct)
    {
        run.Status = status;
        run.Error = error is null ? null : Cut(error, 2000);
        run.FinishedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        // The last fifty runs of a task are kept.
        var old = await db.ScheduledRuns.Where(r => r.TaskId == run.TaskId).OrderByDescending(r => r.StartedAt).Skip(50).Select(r => r.Id).ToListAsync(ct);
        if (old.Count > 0)
        {
            await db.ScheduledRuns.Where(r => old.Contains(r.Id)).ExecuteDeleteAsync(ct);
        }
        return run;
    }

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    [LoggerMessage(Level = LogLevel.Warning, Message = "Scheduled task {Task}: the {Channel} could not be delivered: {Reason}")]
    private static partial void LogDelivery(ILogger logger, Guid task, string channel, string reason);
}

/// <summary>
/// Runs scheduled tasks when they are due (checked every 20 seconds), each in the
/// background, one run of a task at a time. A task that was due while the app was down
/// runs once when it is back, then keeps its schedule. With several replicas on one
/// database, the one that leads watches the clock, and every run is claimed in the
/// database first (<see cref="ScheduledTask.RunningOn"/>): a task never runs twice at
/// once, whichever replica an event or a "Run now" reached. Events that come while a
/// run goes wait in the database (<see cref="TaskEvent"/>), and the replica free next
/// takes them in order.
/// </summary>
public sealed partial class Scheduler(IServiceScopeFactory scopes, Replicas replicas, TimeProvider clock, IOptionsMonitor<ScheduleOptions> options, ILogger<Scheduler> logger)
    : BackgroundService
{
    // The runs going on this replica: their claims are renewed, and shutting down waits for them.
    private readonly ConcurrentDictionary<Guid, Task> _running = new();
    private CancellationToken _stopping;

    /// <summary>Events one task keeps waiting while it answers; past them the oldest goes.</summary>
    public const int MaxWaiting = 20;

    /// <summary>A claim not renewed for this long is let go: the replica that held it stopped mid-run.</summary>
    public static readonly TimeSpan ClaimLapses = TimeSpan.FromMinutes(2);

    /// <summary>Whether a run of it is going, on any replica.</summary>
    public static bool IsRunning(ScheduledTask task, DateTimeOffset now) => task.RunningOn is not null && task.RunningSeenAt > now - ClaimLapses;

    /// <summary>Events waiting for a task's run to end.</summary>
    public async Task<int> WaitingAsync(Guid task, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().TaskEvents.CountAsync(e => e.TaskId == task, ct);
    }

    /// <summary>
    /// An event for a task: run now, or, while a run of it is going (here or on another replica), kept
    /// to run after it (a busy repository's events must not be lost). False when it has to wait.
    /// </summary>
    public async Task<bool> StartOrQueueAsync(Guid task, TriggerEvent trigger, CancellationToken ct)
    {
        if (await StartAsync(task, manual: false, trigger, ct))
        {
            return true;
        }
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.TaskEvents.Add(new TaskEvent
            {
                TaskId = task, Text = trigger.Text, ReplyProject = trigger.Reply?.Project, ReplyKind = trigger.Reply?.Kind, ReplyId = trigger.Reply?.Id, CreatedAt = clock.GetUtcNow(),
            });
            await db.SaveChangesAsync(ct);
            var past = await db.TaskEvents.Where(e => e.TaskId == task).OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id).Skip(MaxWaiting).Select(e => e.Id).ToListAsync(ct);
            if (past.Count > 0)
            {
                await db.TaskEvents.Where(e => past.Contains(e.Id)).ExecuteDeleteAsync(ct);
            }
        }
        // The run may have ended meanwhile (it looked for events before this one was in): then nobody would take it.
        await NextAsync(task, ct);
        return false;
    }

    /// <summary>Runs the task now, in the background; false when a run of it is still going (on any replica).</summary>
    public async Task<bool> StartAsync(Guid task, bool manual, TriggerEvent? trigger = null, CancellationToken ct = default)
    {
        if (await TakeAsync(task, ct) is not { } gate)
        {
            return false;
        }
        Run(task, gate, manual, trigger);
        return true;
    }

    /// <summary>
    /// The task for a run here: its place among this replica's runs, then its claim in the database
    /// (false when a run of it goes elsewhere; a lapsed claim, or one this replica left behind, is taken over).
    /// </summary>
    private async Task<TaskCompletionSource?> TakeAsync(Guid task, CancellationToken ct)
    {
        var gate = new TaskCompletionSource();
        if (!_running.TryAdd(task, gate.Task))
        {
            return null;
        }
        try
        {
            var now = clock.GetUtcNow();
            var lapsed = now - ClaimLapses;
            await using var scope = scopes.CreateAsyncScope();
            var claimed = await scope.ServiceProvider.GetRequiredService<AppDbContext>().ScheduledTasks
                .Where(t => t.Id == task && (t.RunningOn == null || t.RunningOn == replicas.Id || t.RunningSeenAt == null || t.RunningSeenAt < lapsed))
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.RunningOn, replicas.Id).SetProperty(t => t.RunningSeenAt, now), ct) == 1;
            if (claimed)
            {
                return gate;
            }
        }
        catch
        {
            Untake(task, gate);
            throw;
        }
        Untake(task, gate);
        return null;
    }

    private void Untake(Guid task, TaskCompletionSource gate)
    {
        _running.TryRemove(new KeyValuePair<Guid, Task>(task, gate.Task));
        gate.TrySetResult();
    }

    /// <summary>The oldest event waiting for the task, run now when the task is free: claimed first, so only one replica takes it.</summary>
    private async Task NextAsync(Guid task, CancellationToken ct)
    {
        if (await TakeAsync(task, ct) is not { } gate)
        {
            return;
        }
        TriggerEvent? next = null;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // Under the claim nobody else takes this task's events; one may still be dropped as too old meanwhile.
            while (next is null && await db.TaskEvents.AsNoTracking().Where(e => e.TaskId == task).OrderBy(e => e.CreatedAt).ThenBy(e => e.Id).FirstOrDefaultAsync(ct) is { } e)
            {
                if (await db.TaskEvents.Where(x => x.Id == e.Id).ExecuteDeleteAsync(ct) == 1)
                {
                    next = new TriggerEvent(e.Text, e.ReplyProject is { } project && e.ReplyKind is { } kind && e.ReplyId is { } id ? new GitLabTarget(project, kind, id) : null);
                }
            }
        }
        finally
        {
            if (next is null)
            {
                // Given back in the database first: a run taken here meanwhile keeps its claim.
                await LetGoAsync(task);
                Untake(task, gate);
            }
        }
        if (next is not null)
        {
            Run(task, gate, manual: false, next);
        }
    }

    /// <summary>Gives the task back when its run ends; never throws (a claim left behind lapses).</summary>
    private async Task LetGoAsync(Guid task)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().ScheduledTasks.Where(t => t.Id == task && t.RunningOn == replicas.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.RunningOn, (string?)null).SetProperty(t => t.RunningSeenAt, (DateTimeOffset?)null), CancellationToken.None);
        }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or InvalidOperationException)
        {
            LogFailed(logger, task, ex);
        }
    }

    /// <param name="gate">Its place among this replica's runs (<see cref="TakeAsync"/>), done when the run is.</param>
    private void Run(Guid task, TaskCompletionSource gate, bool manual, TriggerEvent? trigger) =>
        _ = Task.Run(async () =>
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<TaskRunner>().RunAsync(task, manual, _stopping, trigger);
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
                // Shutting down: the answer keeps what it had.
            }
            catch (Exception ex)
            {
                LogFailed(logger, task, ex);
            }
            finally
            {
                // Given back first, then the events looked for: one that comes in between is taken by whoever claims next.
                await LetGoAsync(task);
                Untake(task, gate);
                if (!_stopping.IsCancellationRequested)
                {
                    await NextAfterAsync(task);
                }
            }
        });

    private async Task NextAfterAsync(Guid task)
    {
        try
        {
            await NextAsync(task, _stopping);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or InvalidOperationException or DbUpdateException)
        {
            // The leader's round takes the waiting events up again.
            LogFailed(logger, task, ex);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // A pause first: the app starts up without a query of its own, and a task due meanwhile waits 20 seconds.
                await Task.Delay(TimeSpan.FromSeconds(20), clock, stoppingToken);
                await RenewAsync(stoppingToken);
                if (options.CurrentValue.Enabled && replicas.IsLeader)
                {
                    await RunDueAsync(stoppingToken);
                    await WaitingEventsAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Whatever went wrong, the app keeps running: a background failure must never stop it.
                LogFailed(logger, Guid.Empty, ex);
            }
        }
        await Task.WhenAll(_running.Values).WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    /// <summary>This replica still runs these: their claims do not lapse.</summary>
    private async Task RenewAsync(CancellationToken ct)
    {
        var mine = _running.Keys.ToList();
        if (mine.Count == 0)
        {
            return;
        }
        var now = clock.GetUtcNow();
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().ScheduledTasks.Where(t => mine.Contains(t.Id) && t.RunningOn == replicas.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RunningSeenAt, now), ct);
    }

    /// <summary>Events left waiting by a replica that stopped mid-run (its claim lapsed): taken up again.</summary>
    private async Task WaitingEventsAsync(CancellationToken ct)
    {
        List<Guid> tasks;
        await using (var scope = scopes.CreateAsyncScope())
        {
            tasks = await scope.ServiceProvider.GetRequiredService<AppDbContext>().TaskEvents.Select(e => e.TaskId).Distinct().ToListAsync(ct);
        }
        foreach (var task in tasks)
        {
            await NextAsync(task, ct);
        }
    }

    /// <summary>
    /// Each task that is due gets its next time first, only if no other replica moved it meanwhile (so it
    /// is not run twice), then runs; one still running from before skips this time.
    /// </summary>
    public async Task RunDueAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = clock.GetUtcNow();
        var due = await db.ScheduledTasks.AsNoTracking().Where(t => t.Enabled && t.NextRunAt != null && t.NextRunAt <= now).ToListAsync(ct);
        foreach (var task in due)
        {
            var was = task.NextRunAt;
            var next = Next(task, now);
            var taken = await db.ScheduledTasks.Where(t => t.Id == task.Id && t.NextRunAt == was)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.NextRunAt, next).SetProperty(t => t.LastRunAt, now), ct);
            if (taken == 1)
            {
                await StartAsync(task.Id, manual: false, ct: ct);
            }
        }
    }

    /// <summary>When its schedule runs it next after <paramref name="after"/>; never for a task that events run (or a schedule that names no real day).</summary>
    public static DateTimeOffset? Next(ScheduledTask task, DateTimeOffset after) => task.Trigger != Triggers.Schedule ? null :
        Cron.Parse(task.Cron).Cron?.Next(after, Models.Hours.Zone(task.TimeZone).Zone);

    [LoggerMessage(Level = LogLevel.Error, Message = "Scheduled task {Task}: the run failed")]
    private static partial void LogFailed(ILogger logger, Guid task, Exception ex);
}
