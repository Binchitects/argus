using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using Llm.Api.Chat;
using Llm.Api.Identity;
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
        // The task's own notification says how it went (and its email, webhook).
        job.Notify = false;
        jobs.Start(job, question.Id, new AnswerOverrides());
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
/// runs once when it is back, then keeps its schedule.
/// </summary>
public sealed partial class Scheduler(IServiceScopeFactory scopes, TimeProvider clock, IOptionsMonitor<ScheduleOptions> options, ILogger<Scheduler> logger)
    : BackgroundService
{
    private readonly ConcurrentDictionary<Guid, Task> _running = new();
    // Events that came while their task was answering: each runs after the one before, in order.
    private readonly ConcurrentDictionary<Guid, ConcurrentQueue<TriggerEvent>> _waiting = new();
    private CancellationToken _stopping;

    /// <summary>Events one task keeps waiting while it answers; past them the oldest goes.</summary>
    public const int MaxWaiting = 20;

    public bool IsRunning(Guid task) => _running.ContainsKey(task);

    /// <summary>Events waiting for a task's run to end.</summary>
    public int Waiting(Guid task) => _waiting.TryGetValue(task, out var q) ? q.Count : 0;

    /// <summary>
    /// An event for a task: run now, or, while a run of it is going, queued to run after it
    /// (a busy repository's events must not be lost). False when it had to wait.
    /// </summary>
    public bool StartOrQueue(Guid task, TriggerEvent trigger)
    {
        if (Start(task, manual: false, trigger))
        {
            return true;
        }
        var queue = _waiting.GetOrAdd(task, _ => new ConcurrentQueue<TriggerEvent>());
        queue.Enqueue(trigger);
        while (queue.Count > MaxWaiting && queue.TryDequeue(out _))
        {
        }
        // The run may have ended meanwhile: then nobody would take the queue.
        if (!IsRunning(task))
        {
            Next(task);
        }
        return false;
    }

    private void Next(Guid task)
    {
        if (_waiting.TryGetValue(task, out var queue) && queue.TryDequeue(out var next) && !Start(task, manual: false, next))
        {
            // Started meanwhile by another: back to the front is not possible, so back of the line.
            queue.Enqueue(next);
        }
    }

    /// <summary>Runs the task now, in the background; false when a run of it is still going.</summary>
    public bool Start(Guid task, bool manual, TriggerEvent? trigger = null)
    {
        var gate = new TaskCompletionSource();
        if (!_running.TryAdd(task, gate.Task))
        {
            return false;
        }
        // The entry is the gate, done when the run is: a run that ends at once cannot leave a stale entry behind.
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
                _running.TryRemove(task, out _);
                gate.TrySetResult();
                if (!_stopping.IsCancellationRequested)
                {
                    Next(task);
                }
            }
        });
        return true;
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
                if (options.CurrentValue.Enabled)
                {
                    await RunDueAsync(stoppingToken);
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

    /// <summary>Each task that is due gets its next time first (so it is not run twice), then runs.</summary>
    public async Task RunDueAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = clock.GetUtcNow();
        var due = await db.ScheduledTasks.Where(t => t.Enabled && t.NextRunAt != null && t.NextRunAt <= now).ToListAsync(ct);
        foreach (var task in due)
        {
            task.NextRunAt = Next(task, now);
            task.LastRunAt = now;
        }
        await db.SaveChangesAsync(ct);
        foreach (var task in due.Where(t => !IsRunning(t.Id)))
        {
            Start(task.Id, manual: false);
        }
    }

    /// <summary>When a task runs next after <paramref name="after"/>; null: never (its schedule names no real day).</summary>
    /// <summary>When its schedule runs it next; never for a task that events run.</summary>
    public static DateTimeOffset? Next(ScheduledTask task, DateTimeOffset after) => task.Trigger != Triggers.Schedule ? null :
        Cron.Parse(task.Cron).Cron?.Next(after, Models.Hours.Zone(task.TimeZone).Zone);

    [LoggerMessage(Level = LogLevel.Error, Message = "Scheduled task {Task}: the run failed")]
    private static partial void LogFailed(ILogger logger, Guid task, Exception ex);
}
