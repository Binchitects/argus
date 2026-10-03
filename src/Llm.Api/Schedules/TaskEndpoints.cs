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
public sealed record TaskRequest(string? Name = null, string? Prompt = null, string? Cron = null, string? TimeZone = null, string? Model = null, string? Thinking = null,
    List<string>? Tools = null, bool? SameChat = null, bool? Email = null, string? Webhook = null, bool? Enabled = null);

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
        TimeProvider clock, IOptionsMonitor<ScheduleOptions> options, CancellationToken ct)
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
            tasks = tasks.Select(t => View(t, lastRuns.GetValueOrDefault(t.Id), scheduler.IsRunning(t.Id), clock.GetUtcNow())),
        });
    }

    private static object View(ScheduledTask t, ScheduledRun? last, bool running, DateTimeOffset now) => new
    {
        t.Id, t.Name, t.Prompt, t.Cron, t.TimeZone, t.Model, t.Thinking, t.Tools, t.SameChat, t.Email, webhookSet = t.WebhookEncrypted is not null,
        t.Enabled, t.NextRunAt, t.LastRunAt, running,
        nextRuns = t.Enabled && Cron.Parse(t.Cron).Cron is { } cron ? cron.NextRuns(now, Hours.Zone(t.TimeZone).Zone, 3) : [],
        lastRun = last is null ? null : RunView(last),
    };

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
        db.ScheduledTasks.Add(task);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("task.add", task.Name, detail: $"{task.Cron} ({task.TimeZone})");
        return Results.Created($"/api/tasks/{task.Id}", View(task, null, false, context.Clock.GetUtcNow()));
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
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("task.update", task.Name, detail: $"{(task.Enabled ? "" : "off, ")}{task.Cron} ({task.TimeZone})");
        return Results.Ok(View(task, null, scheduler.IsRunning(task.Id), context.Clock.GetUtcNow()));
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
        ChatModels Models, ToolRegistry Registry, AccessService Access, IOptions<StackOptions> Stack, IOptions<AuthOptions> Auth);

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
        var (cron, cronProblem) = Cron.Parse(body.Cron ?? task.Cron);
        if (cron is null)
        {
            return AuthEndpoints.Problem(400, "cron", cronProblem!);
        }
        var now = x.Clock.GetUtcNow();
        if (cron.Next(now, zone) is null)
        {
            return AuthEndpoints.Problem(400, "cron", "That schedule never comes round (31 February?).");
        }
        var min = x.Options.CurrentValue.MinInterval;
        if (cron.ShortestGap(now, zone) is { } gap && gap < min)
        {
            return AuthEndpoints.Problem(400, "cron", $"It would run every {Describe(gap)}: at most once every {Describe(min)} here.");
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
        if (thinking is not null && thinking != "off" && !ThinkingPresets.Parse(x.Stack.Value.ThinkingPresets).Any(pr => pr.Level == thinking))
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
        task.Cron = cron.Expression;
        task.TimeZone = zone.Id;
        task.Model = model;
        task.Thinking = thinking;
        task.Tools = tools;
        task.SameChat = sameChat;
        task.Email = email;
        task.Enabled = body.Enabled ?? (creating || task.Enabled);
        task.NextRunAt = task.Enabled ? cron.Next(now, zone) : null;
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
