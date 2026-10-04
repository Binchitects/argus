using System.Security.Claims;
using Llm.Api.Access;
using Llm.Api.Chat;
using Llm.Api.Chat.Tools;
using Llm.Api.Endpoints;
using Llm.Api.Identity;
using Llm.Api.Models;
using Llm.Api.Operations;
using Llm.Api.Settings;
using Llm.Core.Chat;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Schedules;

/// <param name="Cron">Five fields: minute hour day-of-month month day-of-week.</param>
/// <param name="TimeZone">IANA, e.g. Europe/Berlin; the schedule is its wall clock.</param>
/// <param name="Tools">The chat tools its runs use; null: those on in new chats.</param>
/// <param name="Webhook">A URL to post the answer to; "": none; null: unchanged.</param>
/// <param name="Trigger">"schedule" (Cron), "webhook" (any system posting to its address) or "gitlab" (GitLab's events, <paramref name="Events"/>).</param>
/// <param name="ReplyInGitLab">Its answer goes back to GitLab as a comment (by the GitLab bot).</param>
public sealed record TaskRequest(string? Name = null, string? Prompt = null, string? Cron = null, string? TimeZone = null, string? Model = null, string? Thinking = null,
    List<string>? Tools = null, bool? SameChat = null, bool? Email = null, string? Webhook = null, bool? Enabled = null,
    string? Trigger = null, List<string>? Events = null, bool? ReplyInGitLab = null);

/// <summary>Scheduled tasks: questions asked on a schedule, as their owner. Each person sees and changes only their own.</summary>
public static class TaskEndpoints
{
    public static void MapTasks(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/tasks").RequireAuthorization();
        g.MapGet("", ListAsync);
        g.MapPost("", AddAsync);
        g.MapPatch("/{id:guid}", UpdateAsync);
        g.MapDelete("/{id:guid}", RemoveAsync);
        g.MapPost("/{id:guid}/run", RunAsync);
        g.MapPost("/{id:guid}/secret", SecretAsync);
        // Events that run a task: anyone may post, only with its secret.
        app.MapPost("/api/hooks/{id:guid}", HookAsync).AllowAnonymous().DisableAntiforgery();
        g.MapGet("/{id:guid}/runs", RunsAsync);

        var n = app.MapGroup("/api/notifications").RequireAuthorization();
        n.MapGet("", NotificationsAsync);
        n.MapPost("/{id:guid}/read", ReadAsync);
        n.MapPost("/read", ReadAllAsync);
        n.MapDelete("/{id:guid}", ClearAsync);
        n.MapDelete("", ClearAllAsync);
    }

    private static async Task<AppUser> Me(ClaimsPrincipal p, UserManager<AppUser> users) => (await users.GetUserAsync(p))!;

    private static async Task<IResult> ListAsync(ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, Scheduler scheduler, Mailer mailer,
        TimeProvider clock, IOptionsMonitor<ScheduleOptions> options, IOptions<AuthOptions> auth, GitLabBot bot, CancellationToken ct)
    {
        var me = await Me(p, users);
        var tasks = await db.ScheduledTasks.AsNoTracking().Where(t => t.UserId == me.Id).OrderBy(t => t.CreatedAt).ToListAsync(ct);
        var ids = tasks.Select(t => t.Id).ToList();
        var lastRuns = (await db.ScheduledRuns.AsNoTracking().Where(r => ids.Contains(r.TaskId)).ToListAsync(ct))
            .GroupBy(r => r.TaskId).ToDictionary(g => g.Key, g => g.MaxBy(r => r.StartedAt));
        var o = options.CurrentValue;
        return Results.Ok(new
        {
            enabled = o.Enabled, perPerson = o.PerPerson, minIntervalMinutes = (int)o.MinInterval.TotalMinutes, webhookHosts = o.WebhookHosts,
            email = mailer.Configured ? me.Email : null,
            tasks = tasks.Select(t => View(t, lastRuns.GetValueOrDefault(t.Id), scheduler.IsRunning(t.Id), clock.GetUtcNow(), auth.Value.Origin)),
            gitlabBot = bot.Ready,
        });
    }

    private static object View(ScheduledTask t, ScheduledRun? last, bool running, DateTimeOffset now, string origin, string? secret = null) => new
    {
        t.Id, t.Name, t.Prompt, t.Cron, t.TimeZone, t.Model, t.Thinking, t.Tools, t.SameChat, t.Email, webhookSet = t.WebhookEncrypted is not null,
        t.Enabled, t.NextRunAt, t.LastRunAt, running,
        nextRuns = t.Enabled && t.Trigger == Triggers.Schedule && Cron.Parse(t.Cron).Cron is { } cron ? cron.NextRuns(now, Hours.Zone(t.TimeZone).Zone, 3) : [],
        lastRun = last is null ? null : RunView(last),
        t.Trigger, t.Events, t.ReplyInGitLab,
        hookUrl = t.Trigger == Triggers.Schedule ? null : $"{origin}/api/hooks/{t.Id}",
        // Only in the answer that made it.
        hookToken = secret,
    };

    /// <summary>A new secret for a task that events run (the old one stops working); only its hash is kept.</summary>
    private static string NewSecret(ScheduledTask task)
    {
        var secret = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
        task.TriggerSecretHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret)));
        return secret;
    }

    private static object RunView(ScheduledRun r) => new { r.Id, r.StartedAt, r.FinishedAt, r.Status, r.ConversationId, r.Error, r.Manual, r.Delivery };

    private static async Task<IResult> AddAsync(TaskRequest body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, TaskContext context, Audit audit, CancellationToken ct)
    {
        var me = await Me(p, users);
        var o = context.Options.CurrentValue;
        if (!o.Enabled)
        {
            return AuthEndpoints.Problem(403, "disabled", "Scheduled tasks are off here (Settings → Scheduled tasks).");
        }
        if (await db.ScheduledTasks.CountAsync(t => t.UserId == me.Id, ct) >= o.PerPerson)
        {
            return AuthEndpoints.Problem(409, "limit", $"You have {o.PerPerson} scheduled tasks, as many as one person may. Remove one first.");
        }
        var task = new ScheduledTask { UserId = me.Id, Name = "", Prompt = "", Cron = "" };
        if (await ApplyAsync(task, body, me, context, creating: true, ct) is { } problem)
        {
            return problem;
        }
        var secret = task.Trigger != Triggers.Schedule ? NewSecret(task) : null;
        db.ScheduledTasks.Add(task);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("task.add", task.Name, detail: task.Trigger == Triggers.Schedule ? $"{task.Cron} ({task.TimeZone})" : $"on {task.Trigger} events");
        return Results.Created($"/api/tasks/{task.Id}", View(task, null, false, context.Clock.GetUtcNow(), context.Auth.Value.Origin, secret));
    }

    private static async Task<IResult> UpdateAsync(Guid id, TaskRequest body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, TaskContext context,
        Scheduler scheduler, Audit audit, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await db.ScheduledTasks.SingleOrDefaultAsync(t => t.Id == id && t.UserId == me.Id, ct) is not { } task)
        {
            return Results.NotFound();
        }
        if (await ApplyAsync(task, body, me, context, creating: false, ct) is { } problem)
        {
            return problem;
        }
        var secret = task.Trigger != Triggers.Schedule && task.TriggerSecretHash is null ? NewSecret(task) : null;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("task.update", task.Name, detail: $"{(task.Enabled ? "" : "off, ")}{(task.Trigger == Triggers.Schedule ? $"{task.Cron} ({task.TimeZone})" : $"on {task.Trigger} events")}");
        return Results.Ok(View(task, null, scheduler.IsRunning(task.Id), context.Clock.GetUtcNow(), context.Auth.Value.Origin, secret));
    }

    private static async Task<IResult> RemoveAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, Audit audit, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await db.ScheduledTasks.SingleOrDefaultAsync(t => t.Id == id && t.UserId == me.Id, ct) is not { } task)
        {
            return Results.NotFound();
        }
        // Its chats stay: they are the person's, as any chat.
        db.ScheduledTasks.Remove(task);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("task.remove", task.Name);
        return Results.NoContent();
    }

    /// <summary>Runs it now, besides its schedule (which stays as it is).</summary>
    private static async Task<IResult> RunAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, Scheduler scheduler, Audit audit, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await db.ScheduledTasks.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id && t.UserId == me.Id, ct) is not { } task)
        {
            return Results.NotFound();
        }
        if (!scheduler.Start(task.Id, manual: true))
        {
            return AuthEndpoints.Problem(409, "running", "It is running now; wait for this run to end.");
        }
        await audit.WriteAsync("task.run", task.Name);
        return Results.Accepted();
    }

    private static async Task<IResult> RunsAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (!await db.ScheduledTasks.AnyAsync(t => t.Id == id && t.UserId == me.Id, ct))
        {
            return Results.NotFound();
        }
        var runs = await db.ScheduledRuns.AsNoTracking().Where(r => r.TaskId == id).OrderByDescending(r => r.StartedAt).Take(50).ToListAsync(ct);
        return Results.Ok(runs.Select(RunView));
    }

    /// <summary>What checking a task needs: the clock, the options, and the chat's models and tools as this person may use them.</summary>
    public sealed record TaskContext(TimeProvider Clock, IOptionsMonitor<ScheduleOptions> Options, Mailer Mailer, Webhooks Webhooks, ModelPolicy Policy,
        ChatModels Models, ToolRegistry Registry, AccessService Access, IOptionsMonitor<Chat.ChatOptions> Chat, IOptions<AuthOptions> Auth);

    private static async Task<IResult> SecretAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, Audit audit, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await db.ScheduledTasks.SingleOrDefaultAsync(t => t.Id == id && t.UserId == me.Id && t.Trigger != Triggers.Schedule, ct) is not { } task)
        {
            return Results.NotFound();
        }
        var secret = NewSecret(task);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("task.secret", task.Name);
        return Results.Ok(new { hookToken = secret });
    }

    /// <summary>
    /// An event for a task: GitLab's (its secret in X-Gitlab-Token) or any system's JSON (in X-Hook-Secret). An event the
    /// task does not take is acknowledged and dropped, as GitLab turns off a webhook that keeps failing.
    /// </summary>
    private static async Task<IResult> HookAsync(Guid id, HttpRequest request, AppDbContext db, Scheduler scheduler, GitLabBot bot, IOptionsMonitor<ScheduleOptions> options,
        CancellationToken ct)
    {
        var task = await db.ScheduledTasks.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id && t.Trigger != Triggers.Schedule, ct);
        var supplied = request.Headers["X-Gitlab-Token"].FirstOrDefault() ?? request.Headers["X-Hook-Secret"].FirstOrDefault() ?? "";
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(supplied)));
        if (task?.TriggerSecretHash is not { } expected || supplied.Length == 0
            || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(hash), System.Text.Encoding.ASCII.GetBytes(expected)))
        {
            return Results.Json(new { error = "unknown task or wrong secret" }, statusCode: 401);
        }
        if (!task.Enabled || !options.CurrentValue.Enabled)
        {
            return Results.Ok(new { status = "ignored", reason = "the task is off" });
        }
        using var reader = new StreamReader(request.Body);
        var body = await reader.ReadToEndAsync(ct);
        if (body.Length > 1_000_000)
        {
            return Results.Json(new { error = "the event is larger than 1 MB" }, statusCode: 413);
        }
        TriggerEvent? trigger;
        if (task.Trigger == Triggers.GitLab)
        {
            if (System.Text.Json.Nodes.JsonNode.Parse(body.Length == 0 ? "{}" : body) is not System.Text.Json.Nodes.JsonObject e)
            {
                return Results.BadRequest(new { error = "the event is not a JSON object" });
            }
            trigger = await Triggers.FromGitLabAsync(e, task.Events, bot, ct);
        }
        else
        {
            trigger = Triggers.FromJson(body);
        }
        if (trigger is null)
        {
            return Results.Ok(new { status = "ignored", reason = "not an event this task takes" });
        }
        return scheduler.Start(task.Id, manual: false, trigger)
            ? Results.Accepted(value: new { status = "started" })
            : Results.Ok(new { status = "busy", reason = "the run before is still answering" });
    }

    private static async Task<IResult?> ApplyAsync(ScheduledTask task, TaskRequest body, AppUser me, TaskContext x, bool creating, CancellationToken ct)
    {
        var name = body.Name?.Trim() ?? task.Name;
        if (name.Length is 0 or > 100)
        {
            return AuthEndpoints.Problem(400, "name", "A task needs a name of up to 100 characters, e.g. Morning digest.");
        }
        var prompt = body.Prompt?.Trim() ?? task.Prompt;
        if (prompt.Length is 0 or > 20_000)
        {
            return AuthEndpoints.Problem(400, "prompt", "Write what to ask (up to 20,000 characters).");
        }
        var zoneName = body.TimeZone?.Trim() ?? task.TimeZone;
        var (zone, zoneProblem) = Hours.Zone(zoneName);
        if (zoneProblem is not null)
        {
            return AuthEndpoints.Problem(400, "time_zone", $"\"{zoneName}\" is not a time zone: use an IANA name such as Europe/Berlin.");
        }
        var trigger = body.Trigger?.Trim() ?? task.Trigger;
        if (!Triggers.Kinds.Contains(trigger))
        {
            return AuthEndpoints.Problem(400, "trigger", "A task runs on a schedule, on a webhook, or on GitLab's events.");
        }
        var events = body.Events is { } given ? given.Distinct().ToList() : task.Events;
        if (trigger == Triggers.GitLab && (events.Count == 0 || events.Any(e => !Triggers.GitLabEvents.Contains(e))))
        {
            return AuthEndpoints.Problem(400, "events", "Choose the GitLab events that run it: merge_request, pipeline_failed, issue.");
        }
        var now = x.Clock.GetUtcNow();
        Cron? cron = null;
        if (trigger == Triggers.Schedule)
        {
            (cron, var cronProblem) = Cron.Parse(body.Cron ?? task.Cron);
            if (cron is null)
            {
                return AuthEndpoints.Problem(400, "cron", cronProblem!);
            }
            if (cron.Next(now, zone) is null)
            {
                return AuthEndpoints.Problem(400, "cron", "That schedule never comes round (31 February?).");
            }
            var min = x.Options.CurrentValue.MinInterval;
            if (cron.ShortestGap(now, zone) is { } gap && gap < min)
            {
                return AuthEndpoints.Problem(400, "cron", $"It would run every {Describe(gap)}: at most once every {Describe(min)} here.");
            }
        }
        var model = body.Model is null ? task.Model : body.Model.Trim() is { Length: > 0 } m ? m : null;
        if (model is not null)
        {
            if ((await x.Models.ListAsync(ct)).All(g => g.Name != model))
            {
                return AuthEndpoints.Problem(400, "model", $"There is no model named {model}.");
            }
            // Whether it is loaded is a question for each run, not for the schedule.
            if (!(await x.Policy.AllowedAsync(me, [model], ct)).Contains(model))
            {
                return AuthEndpoints.Problem(403, "model", $"You may not use {model}. Choose another model.");
            }
        }
        var thinking = body.Thinking is null ? task.Thinking : body.Thinking.Trim() is { Length: > 0 } th ? th : null;
        if (thinking is not null && thinking != "off" && !ThinkingPresets.Parse(x.Chat.CurrentValue.ThinkingPresets).Any(pr => pr.Level == thinking))
        {
            return AuthEndpoints.Problem(400, "thinking", "Unknown thinking level.");
        }
        var tools = body.Tools ?? task.Tools;
        if (body.Tools is not null)
        {
            var allowed = (await x.Registry.ForAsync(await x.Access.MembershipAsync(me, ct), ct)).Select(t => t.Tool.Id).ToHashSet();
            if (body.Tools.FirstOrDefault(t => !allowed.Contains(t)) is { } unknown)
            {
                return AuthEndpoints.Problem(400, "tools", $"The tool {unknown} is not available to you.");
            }
            tools = [.. body.Tools.Distinct()];
        }
        var email = body.Email ?? task.Email;
        if (email && (body.Email == true) && !x.Mailer.Configured)
        {
            return AuthEndpoints.Problem(400, "email", "Email is not set up here: an admin sets the mail server under Settings → Email.");
        }
        if (email && body.Email == true && string.IsNullOrWhiteSpace(me.Email))
        {
            return AuthEndpoints.Problem(400, "email", "Your account has no email address to send to.");
        }
        if (body.Webhook is { Length: > 0 } hook)
        {
            if (x.Webhooks.Refusal(hook.Trim()) is { } refused)
            {
                return AuthEndpoints.Problem(400, "webhook", refused);
            }
            if (string.IsNullOrEmpty(x.Auth.Value.DataKey))
            {
                return AuthEndpoints.Problem(400, "data_key", "APP_DATA_KEY is not set, so a webhook URL (a secret) cannot be stored encrypted.");
            }
            task.WebhookEncrypted = SettingsCrypto.Encrypt(hook.Trim(), x.Auth.Value.DataKey);
        }
        else if (body.Webhook is "")
        {
            task.WebhookEncrypted = null;
        }
        var sameChat = body.SameChat ?? task.SameChat;
        if (!sameChat)
        {
            task.ConversationId = null;
        }
        task.Name = name;
        task.Prompt = prompt;
        task.Cron = cron?.Expression ?? "";
        task.Trigger = trigger;
        task.Events = trigger == Triggers.GitLab ? events : [];
        task.ReplyInGitLab = trigger == Triggers.GitLab && (body.ReplyInGitLab ?? task.ReplyInGitLab);
        if (trigger == Triggers.Schedule)
        {
            task.TriggerSecretHash = null;
        }
        task.TimeZone = zone.Id;
        task.Model = model;
        task.Thinking = thinking;
        task.Tools = tools;
        task.SameChat = sameChat;
        task.Email = email;
        task.Enabled = body.Enabled ?? (creating || task.Enabled);
        task.NextRunAt = task.Enabled ? cron?.Next(now, zone) : null;
        task.UpdatedAt = now;
        return null;
    }

    private static string Describe(TimeSpan t) => t.TotalHours >= 1 && t.TotalMinutes % 60 == 0
        ? $"{t.TotalHours:0} hour{(t.TotalHours == 1 ? "" : "s")}"
        : $"{t.TotalMinutes:0} minute{(t.TotalMinutes == 1 ? "" : "s")}";

    private static async Task<IResult> NotificationsAsync(ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, CancellationToken ct)
    {
        var me = await Me(p, users);
        var items = await db.Notifications.AsNoTracking().Where(x => x.UserId == me.Id && !x.Cleared).OrderByDescending(x => x.CreatedAt).Take(30).ToListAsync(ct);
        var unread = await db.Notifications.CountAsync(x => x.UserId == me.Id && x.ReadAt == null && !x.Cleared, ct);
        return Results.Ok(new { unread, items = items.Select(x => new { x.Id, x.Kind, x.Title, x.Body, x.Link, x.CreatedAt, read = x.ReadAt != null }) });
    }

    private static async Task<IResult> ReadAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var me = await Me(p, users);
        var now = clock.GetUtcNow();
        return await db.Notifications.Where(x => x.Id == id && x.UserId == me.Id && x.ReadAt == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.ReadAt, now), ct) > 0
            || await db.Notifications.AnyAsync(x => x.Id == id && x.UserId == me.Id, ct)
            ? Results.NoContent()
            : Results.NotFound();
    }

    private static async Task<IResult> ReadAllAsync(ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var me = await Me(p, users);
        var now = clock.GetUtcNow();
        await db.Notifications.Where(x => x.UserId == me.Id && x.ReadAt == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.ReadAt, now), ct);
        // The last hundred are kept; news said once (an alert's firing, a credit threshold) a month, so it is not said again meanwhile.
        var month = now.AddDays(-30);
        var old = await db.Notifications.Where(x => x.UserId == me.Id).OrderByDescending(x => x.CreatedAt).Skip(100)
            .Where(x => x.Key == null || x.CreatedAt < month).Select(x => x.Id).ToListAsync(ct);
        if (old.Count > 0)
        {
            await db.Notifications.Where(x => old.Contains(x.Id)).ExecuteDeleteAsync(ct);
        }
        return Results.NoContent();
    }

    /// <summary>Clears one: gone, or (news said once) hidden, so it is not said again.</summary>
    private static async Task<IResult> ClearAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var me = await Me(p, users);
        var mine = db.Notifications.Where(x => x.Id == id && x.UserId == me.Id);
        if (!await mine.AnyAsync(ct))
        {
            return Results.NotFound();
        }
        await ClearWhereAsync(mine, clock.GetUtcNow(), ct);
        return Results.NoContent();
    }

    private static async Task<IResult> ClearAllAsync(ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var me = await Me(p, users);
        await ClearWhereAsync(db.Notifications.Where(x => x.UserId == me.Id), clock.GetUtcNow(), ct);
        return Results.NoContent();
    }

    private static async Task ClearWhereAsync(IQueryable<Notification> which, DateTimeOffset now, CancellationToken ct)
    {
        await which.Where(x => x.Key == null).ExecuteDeleteAsync(ct);
        await which.Where(x => x.Key != null).ExecuteUpdateAsync(s => s.SetProperty(x => x.Cleared, true).SetProperty(x => x.ReadAt, x => x.ReadAt ?? now), ct);
    }
}
