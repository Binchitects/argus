using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Llm.Api.Access;
using Llm.Api.Chat;
using Llm.Api.Endpoints;
using Llm.Api.Identity;
using Llm.Api.Models;
using Llm.Api.Schedules;
using Llm.Core.Chat;
using Llm.Core.Code;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Code;

/// <summary>A project's automatic reviews, as set or changed; null: unchanged (Guid.Empty for the room: none).</summary>
public sealed record RuleRequest(string? Project = null, bool? Enabled = null, bool? MergeRequests = null, List<string>? TargetBranches = null, bool? SkipDrafts = null,
    int? CheckMinutes = null, string? Branch = null, string? Base = null, string? Cron = null, string? TimeZone = null, Guid? RoomId = null, int? MostPerDay = null);

/// <summary>
/// Automatic reviews: each project's rule looks at its open merge requests every few minutes (and GitLab's webhook, sent to
/// the rule's own address, says at once when one opens or gets new commits), and runs its scheduled branch review on its
/// cron. A review is queued for what changed, at most two of a rule's run at once, and a rule starts no more than its
/// reviews a day. The code is read with the rule's person's read-only access (Argus), never written to.
/// </summary>
public sealed partial class AutoReviews(IServiceScopeFactory scopes, ReviewEngine engine, QueuedMessages queue, TimeProvider clock, ILogger<AutoReviews> logger) : BackgroundService
{
    /// <summary>How many of one rule's reviews run at once; the others wait their turn.</summary>
    public const int MostRunning = 2;
    /// <summary>How many rules one person may have.</summary>
    public const int MostRules = 20;
    /// <summary>The fewest minutes between looks at the open merge requests (0: only the webhook).</summary>
    public const int FewestMinutes = 5;

    private readonly SemaphoreSlim _gate = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), clock);
        do
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                LogTick(logger, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public override void Dispose()
    {
        _gate.Dispose();
        base.Dispose();
    }

    /// <summary>Every rule that is due: its look at open merge requests, its scheduled review, and the reviews waiting their turn.</summary>
    public async Task TickAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = clock.GetUtcNow();
        foreach (var id in await db.ReviewRules.AsNoTracking().Where(r => r.Enabled).Select(r => r.Id).ToListAsync(ct))
        {
            var rule = await db.ReviewRules.SingleAsync(r => r.Id == id, ct);
            if (rule.MergeRequests && rule.CheckMinutes > 0 && (rule.CheckedAt is null || rule.CheckedAt.Value.AddMinutes(rule.CheckMinutes) <= now))
            {
                await CheckAsync(scope.ServiceProvider, rule, ct);
            }
            if (rule.NextRunAt is { } next && next <= now)
            {
                await ScheduledAsync(scope.ServiceProvider, rule, ct);
            }
            await StartWaitingAsync(db, rule, ct);
        }
    }

    /// <summary>The rule's person, when they may still review and set automatic reviews; else why not (kept on the rule).</summary>
    private static async Task<AppUser?> OwnerAsync(IServiceProvider services, ReviewRule rule, CancellationToken ct)
    {
        var user = await services.GetRequiredService<UserManager<AppUser>>().FindByIdAsync(rule.UserId.ToString());
        var access = services.GetRequiredService<AccessService>();
        rule.LastError = user is null || user.IsDisabled ? "Its person's account is disabled."
            : !await access.MayAsync(user, Core.Access.Permissions.ReviewRun, ct) || !await access.MayAsync(user, Core.Access.Permissions.ReviewPolicy, ct)
                ? "Its person may no longer run automatic reviews."
                : null;
        return rule.LastError is null ? user : null;
    }

    /// <summary>The open merge requests of the rule's project, as its person may read them: those changed since the rule was made.</summary>
    private static async Task<List<JsonObject>> OpenAsync(IServiceProvider services, ReviewRule rule, AppUser owner, CancellationToken ct)
    {
        var session = await services.GetRequiredService<ArgusMcp>().ConnectAsync(owner.Id, owner.Email!, ct);
        var args = new JsonObject { ["project"] = rule.Project, ["state"] = "opened", ["limit"] = 100, ["updated_after"] = rule.CreatedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture) };
        var (text, isError) = await session.CallAsync("mr_list", args, null, ct);
        if (isError)
        {
            throw new ReviewException(text.Length > 500 ? text[..500] : text);
        }
        var answer = JsonNode.Parse(text);
        return ((answer as JsonObject)?["result"] as JsonArray ?? answer as JsonArray ?? []).OfType<JsonObject>().ToList();
    }

    /// <summary>Whether the rule takes a merge request into this branch, draft or not.</summary>
    private static bool Takes(ReviewRule rule, string? target, bool draft) =>
        !(rule.SkipDrafts && draft) && (rule.TargetBranches.Count == 0 || (target is not null && rule.TargetBranches.Contains(target)));

    /// <summary>One look at the open merge requests: a review queued for each whose newest commit no review read yet.</summary>
    public async Task<List<string>> CheckAsync(IServiceProvider services, ReviewRule rule, CancellationToken ct)
    {
        var db = services.GetRequiredService<AppDbContext>();
        rule.CheckedAt = clock.GetUtcNow();
        var said = new List<string>();
        if (await OwnerAsync(services, rule, ct) is not { } owner)
        {
            await db.SaveChangesAsync(ct);
            return [rule.LastError!];
        }
        try
        {
            foreach (var mr in await OpenAsync(services, rule, owner, ct))
            {
                var iid = mr["iid"]?.GetValue<int>() ?? 0;
                if (iid <= 0)
                {
                    continue;
                }
                if (!Takes(rule, mr["target_branch"]?.ToString(), mr["draft"]?.GetValue<bool>() == true))
                {
                    said.Add($"!{iid}: not one the rule takes");
                    continue;
                }
                said.Add($"!{iid}: {await QueueAsync(db, rule, iid, mr["sha"]?.ToString(), ReviewRule.ByCheck, ct)}");
            }
        }
        catch (Exception ex) when (ex is ReviewException or McpException or HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            rule.LastError = ex is ReviewException ? ex.Message : "Argus could not be reached.";
            said.Add(rule.LastError);
        }
        await db.SaveChangesAsync(ct);
        return said;
    }

    /// <summary>A GitLab event for the rule: a merge request opened, opened again, or given new commits; else nothing.</summary>
    public async Task<string> EventAsync(IServiceProvider services, ReviewRule rule, JsonObject e, CancellationToken ct)
    {
        var attrs = e["object_attributes"] as JsonObject ?? [];
        var action = attrs["action"]?.ToString();
        if (e["object_kind"]?.ToString() != "merge_request" || !(action is "open" or "reopen" || (action == "update" && attrs["oldrev"] is not null)))
        {
            return "ignored: not a merge request opened or given new commits";
        }
        if (!string.Equals(e["project"]?["path_with_namespace"]?.ToString(), rule.Project, StringComparison.OrdinalIgnoreCase))
        {
            return "ignored: another project's";
        }
        var draft = attrs["draft"]?.GetValue<bool>() == true || attrs["work_in_progress"]?.GetValue<bool>() == true;
        if (!rule.MergeRequests || !Takes(rule, attrs["target_branch"]?.ToString(), draft))
        {
            return "ignored: not one the rule takes";
        }
        var db = services.GetRequiredService<AppDbContext>();
        if (await OwnerAsync(services, rule, ct) is null)
        {
            await db.SaveChangesAsync(ct);
            return "ignored: " + rule.LastError;
        }
        var iid = attrs["iid"]?.GetValue<int>() ?? 0;
        var status = await QueueAsync(db, rule, iid, attrs["last_commit"]?["id"]?.ToString(), ReviewRule.ByWebhook, ct);
        await StartWaitingAsync(db, rule, ct);
        return status;
    }

    /// <summary>The scheduled review: what the branch has over its base (one already waiting is enough); the next run worked out.</summary>
    private async Task ScheduledAsync(IServiceProvider services, ReviewRule rule, CancellationToken ct)
    {
        var db = services.GetRequiredService<AppDbContext>();
        rule.NextRunAt = NextRun(rule, clock.GetUtcNow());
        if (await OwnerAsync(services, rule, ct) is not null && rule.Branch is { } branch && rule.Base is { } from)
        {
            if (!await db.CodeReviews.AnyAsync(r => r.RuleId == rule.Id && r.Trigger == ReviewRule.BySchedule && r.Status == CodeReview.Queued, ct))
            {
                await NewAsync(db, rule, null, from, branch, ReviewRule.BySchedule, ct);
            }
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>When the rule's scheduled review runs next; null: it has none.</summary>
    public static DateTimeOffset? NextRun(ReviewRule rule, DateTimeOffset after) =>
        rule.Enabled && rule.Branch is not null && Schedules.Cron.Parse(rule.Cron).Cron is { } cron ? cron.Next(after, Hours.Zone(rule.TimeZone).Zone) : null;

    /// <summary>A review of a merge request queued, unless one waits already or the last one read its newest commit; or why not.</summary>
    private async Task<string> QueueAsync(AppDbContext db, ReviewRule rule, int iid, string? sha, string trigger, CancellationToken ct)
    {
        var last = await db.CodeReviews.AsNoTracking().Where(r => r.UserId == rule.UserId && r.Project == rule.Project && r.Iid == iid && r.Status != CodeReview.Failed)
            .OrderByDescending(r => r.CreatedAt).Select(r => new { r.Status, r.HeadSha }).FirstOrDefaultAsync(ct);
        if (last?.Status == CodeReview.Queued)
        {
            return "waiting already";
        }
        if (last is not null && (last.HeadSha == sha || (last.Status == CodeReview.Running && last.HeadSha is null)))
        {
            return last.Status == CodeReview.Done ? "reviewed already" : "being reviewed";
        }
        var since = clock.GetUtcNow().AddDays(-1);
        if (await db.CodeReviews.CountAsync(r => r.RuleId == rule.Id && r.CreatedAt > since, ct) >= rule.MostPerDay)
        {
            rule.LastError = $"It started its {rule.MostPerDay} reviews of the day: the rest wait for tomorrow.";
            return "over the day's reviews";
        }
        await NewAsync(db, rule, iid, null, null, trigger, ct);
        return "queued";
    }

    private async Task NewAsync(AppDbContext db, ReviewRule rule, int? iid, string? from, string? to, string trigger, CancellationToken ct)
    {
        db.CodeReviews.Add(new CodeReview
        {
            UserId = rule.UserId, Project = rule.Project, Iid = iid, FromRef = from, ToRef = to, Title = iid is { } n ? $"!{n}" : $"{to} over {from}",
            RuleId = rule.Id, Trigger = trigger, RoomId = rule.RoomId, CreatedAt = clock.GetUtcNow(),
        });
        rule.LastError = null;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The rule's waiting reviews started, the oldest first, while fewer than <see cref="MostRunning"/> run (one at a time for each merge request).</summary>
    public async Task StartWaitingAsync(AppDbContext db, ReviewRule rule, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var running = await db.CodeReviews.Where(r => r.RuleId == rule.Id && r.Status == CodeReview.Running).Select(r => new { r.Iid, r.Trigger }).ToListAsync(ct);
            foreach (var w in await db.CodeReviews.Where(r => r.RuleId == rule.Id && r.Status == CodeReview.Queued).OrderBy(r => r.CreatedAt).ToListAsync(ct))
            {
                if (running.Count >= MostRunning)
                {
                    break;
                }
                if (running.Any(r => r.Iid == w.Iid && r.Trigger == (w.Iid is null ? w.Trigger : r.Trigger)))
                {
                    continue;
                }
                // The review before it, of the same merge request (or branch), so that it reads only what came after.
                w.PreviousId = await db.CodeReviews.AsNoTracking()
                    .Where(r => r.UserId == w.UserId && r.Project == w.Project && r.Iid == w.Iid && r.Status == CodeReview.Done && r.HeadSha != null
                        && (w.Iid != null || (r.FromRef == w.FromRef && r.ToRef == w.ToRef)))
                    .OrderByDescending(r => r.CreatedAt).Select(r => (Guid?)r.Id).FirstOrDefaultAsync(ct);
                w.Status = CodeReview.Running;
                w.Step = "Starting";
                await db.SaveChangesAsync(ct);
                running.Add(new { w.Iid, w.Trigger });
                engine.Start(w.Id);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>A finished automatic review, posted in the rule's room by its person, in line after what the room is answering.</summary>
    public async Task PostAsync(AppDbContext db, CodeReview review, Guid room, CancellationToken ct)
    {
        var text = CodeEndpoints.RoomText(review, review.Trigger == ReviewRule.BySchedule ? "Scheduled review" : "Automatic review");
        var last = await db.QueuedMessages.Where(q => q.ConversationId == room).MaxAsync(q => (int?)q.Position, ct) ?? 0;
        db.QueuedMessages.Add(new QueuedMessage { ConversationId = room, Content = text, UserId = review.UserId, ForPeople = true, Position = last + 1 });
        await db.SaveChangesAsync(ct);
        await queue.StartNextAsync(room);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Automatic reviews: a look at the rules failed")]
    private static partial void LogTick(ILogger logger, Exception ex);
}

/// <summary>Code → Automatic reviews: a person's rules (every rule, for a reviews admin), and GitLab's webhook for each.</summary>
public static class ReviewRuleEndpoints
{
    public static void MapReviewRules(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/code/rules").PersonOnly().RequirePermission(Core.Access.Permissions.ReviewPolicy);
        g.MapGet("", ListAsync);
        g.MapPost("", CreateAsync).RequirePermission(Core.Access.Permissions.ReviewRun);
        g.MapPut("/{id:guid}", UpdateAsync);
        g.MapDelete("/{id:guid}", DeleteAsync);
        g.MapPost("/{id:guid}/secret", SecretAsync);
        g.MapPost("/{id:guid}/check", CheckAsync);
        app.MapPost("/api/hooks/reviews/{id:guid}", HookAsync)
            .AllowAnonymous("GitLab's merge request events for a project's automatic reviews: the rule's secret, in X-Gitlab-Token, is checked.").DisableAntiforgery();
    }

    private static async Task<AppUser> Me(ClaimsPrincipal p, UserManager<AppUser> users) => (await users.GetUserAsync(p))!;

    /// <summary>The person's own rule, or any for a reviews admin.</summary>
    private static async Task<ReviewRule?> FindAsync(AppDbContext db, AccessService access, AppUser me, Guid id, CancellationToken ct) =>
        await db.ReviewRules.SingleOrDefaultAsync(r => r.Id == id, ct) is { } rule && (rule.UserId == me.Id || await access.MayAsync(me, Core.Access.Permissions.ReviewsAdmin, ct))
            ? rule
            : null;

    private static string Hash(string secret) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    private static string NewSecret(ReviewRule rule)
    {
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        rule.SecretHash = Hash(secret);
        return secret;
    }

    private static object Shape(ReviewRule r, HttpRequest request, string? by = null, int today = 0) => new
    {
        r.Id, r.Project, r.Enabled, r.MergeRequests, r.TargetBranches, r.SkipDrafts, r.CheckMinutes, r.Branch, r.Base, r.Cron, r.TimeZone, r.RoomId, r.MostPerDay,
        r.CheckedAt, r.NextRunAt, r.LastError, r.CreatedAt, by, today, hookUrl = $"{request.Scheme}://{request.Host}/api/hooks/reviews/{r.Id}",
    };

    private static async Task<IResult> ListAsync(bool? all, HttpRequest request, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, AccessService access, TimeProvider clock,
        CancellationToken ct)
    {
        var me = await Me(p, users);
        var everyone = all == true && await access.MayAsync(me, Core.Access.Permissions.ReviewsAdmin, ct);
        var rules = await db.ReviewRules.AsNoTracking().Where(r => everyone || r.UserId == me.Id).OrderBy(r => r.Project).ToListAsync(ct);
        var ids = rules.Select(r => r.UserId).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName == "" ? u.UserName! : u.DisplayName, ct);
        var since = clock.GetUtcNow().AddDays(-1);
        var ruleIds = rules.Select(r => (Guid?)r.Id).ToList();
        var today = await db.CodeReviews.AsNoTracking().Where(r => ruleIds.Contains(r.RuleId) && r.CreatedAt > since).GroupBy(r => r.RuleId)
            .Select(x => new { x.Key, n = x.Count() }).ToDictionaryAsync(x => x.Key!.Value, x => x.n, ct);
        return Results.Ok(rules.Select(r => Shape(r, request, names.GetValueOrDefault(r.UserId), today.GetValueOrDefault(r.Id))));
    }

    /// <summary>The request applied to the rule, or why not.</summary>
    private static async Task<IResult?> ApplyAsync(ReviewRule rule, RuleRequest body, AppUser me, AppDbContext db, TimeProvider clock, CancellationToken ct)
    {
        rule.Enabled = body.Enabled ?? rule.Enabled;
        rule.MergeRequests = body.MergeRequests ?? rule.MergeRequests;
        rule.SkipDrafts = body.SkipDrafts ?? rule.SkipDrafts;
        if (body.TargetBranches is { } targets)
        {
            var clean = targets.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct().ToList();
            if (clean.Count > 20 || clean.Any(t => t.Length > 300))
            {
                return AuthEndpoints.Problem(400, "target_branches", "At most 20 target branches, each up to 300 characters.");
            }
            rule.TargetBranches = clean;
        }
        if (body.CheckMinutes is { } minutes)
        {
            if (minutes != 0 && minutes is < AutoReviews.FewestMinutes or > 1440)
            {
                return AuthEndpoints.Problem(400, "check_minutes", $"Look at the open merge requests every {AutoReviews.FewestMinutes} to 1,440 minutes, or 0 for the webhook only.");
            }
            rule.CheckMinutes = minutes;
        }
        if (body.MostPerDay is { } most)
        {
            if (most is < 1 or > 500)
            {
                return AuthEndpoints.Problem(400, "most_per_day", "A rule starts 1 to 500 reviews a day.");
            }
            rule.MostPerDay = most;
        }
        if (body.TimeZone is { } zoneName)
        {
            var (zone, problem) = Hours.Zone(zoneName.Trim());
            if (problem is not null)
            {
                return AuthEndpoints.Problem(400, "time_zone", $"\"{zoneName}\" is not a time zone: use an IANA name such as Europe/Berlin.");
            }
            rule.TimeZone = zone.Id;
        }
        if (body.Cron is not null || body.Branch is not null || body.Base is not null)
        {
            rule.Branch = body.Branch is null ? rule.Branch : body.Branch.Trim() is { Length: > 0 } b ? b : null;
            rule.Base = body.Base is null ? rule.Base : body.Base.Trim() is { Length: > 0 } f ? f : null;
            rule.Cron = body.Cron is null ? rule.Cron : body.Cron.Trim() is { Length: > 0 } c ? c : null;
        }
        if (rule.Branch is not null || rule.Cron is not null)
        {
            if (rule.Branch is null || rule.Base is null || rule.Cron is null || rule.Branch.Length > 300 || rule.Base.Length > 300)
            {
                return AuthEndpoints.Problem(400, "schedule", "A scheduled review needs a branch, the branch it is compared with, and when (cron).");
            }
            var (cron, problem) = Schedules.Cron.Parse(rule.Cron);
            if (cron is null)
            {
                return AuthEndpoints.Problem(400, "cron", problem!);
            }
            if (cron.ShortestGap(clock.GetUtcNow(), Hours.Zone(rule.TimeZone).Zone) is { } gap && gap < TimeSpan.FromHours(1))
            {
                return AuthEndpoints.Problem(400, "cron", "A scheduled review runs at most once an hour.");
            }
        }
        rule.Base = rule.Branch is null ? null : rule.Base;
        if (body.RoomId is { } room)
        {
            if (room == Guid.Empty)
            {
                rule.RoomId = null;
            }
            else if (await RoomAccess.FindAsync(db, room, me, Need.Write, ct) is not { IsRoom: true })
            {
                return AuthEndpoints.Problem(400, "room", "Choose a room you write in.");
            }
            else
            {
                rule.RoomId = room;
            }
        }
        rule.NextRunAt = AutoReviews.NextRun(rule, clock.GetUtcNow());
        return null;
    }

    private static async Task<IResult> CreateAsync(RuleRequest body, HttpRequest request, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, ArgusMcp argus, Audit audit,
        TimeProvider clock, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (!argus.Enabled)
        {
            return AuthEndpoints.Problem(503, "argus", "Argus is not set up here: it reads the code for reviews.");
        }
        var project = (body.Project ?? "").Trim().Trim('/');
        if (project.Length is 0 or > 500)
        {
            return AuthEndpoints.Problem(400, "project", "Name the project by its path, as group/project.");
        }
        if (await db.ReviewRules.CountAsync(r => r.UserId == me.Id, ct) >= AutoReviews.MostRules)
        {
            return AuthEndpoints.Problem(400, "rules", $"You have {AutoReviews.MostRules} rules: change one, or take one away first.");
        }
        if (await db.ReviewRules.AnyAsync(r => r.UserId == me.Id && r.Project == project, ct))
        {
            return AuthEndpoints.Problem(409, "project", "You have a rule for this project: change that one.");
        }
        // The person must read the project: a rule reads it as them.
        try
        {
            var session = await argus.ConnectAsync(me.Id, me.Email!, ct);
            var (text, isError) = await session.CallAsync("mr_list", new JsonObject { ["project"] = project, ["state"] = "opened", ["limit"] = 1 }, null, ct);
            if (isError)
            {
                return AuthEndpoints.Problem(ArgusMcp.IsNoAccess(text) ? 404 : 400, "project", text.Length > 600 ? text[..600] : text);
            }
        }
        catch (Exception ex) when (ex is McpException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return AuthEndpoints.Problem(503, "argus", "Argus could not be reached: try again in a moment.");
        }
        var rule = new ReviewRule { UserId = me.Id, Project = project, CreatedAt = clock.GetUtcNow() };
        if (await ApplyAsync(rule, body, me, db, clock, ct) is { } problem)
        {
            return problem;
        }
        var secret = NewSecret(rule);
        db.ReviewRules.Add(rule);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("review.rule.add", project);
        return Results.Ok(new { rule = Shape(rule, request), secret });
    }

    private static async Task<IResult> UpdateAsync(Guid id, RuleRequest body, HttpRequest request, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, AccessService access,
        Audit audit, TimeProvider clock, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await FindAsync(db, access, me, id, ct) is not { } rule)
        {
            return Results.NotFound();
        }
        if (body.Project is { } project && project.Trim().Trim('/') != rule.Project)
        {
            return AuthEndpoints.Problem(400, "project", "A rule stays with its project: make another for another project.");
        }
        if (await ApplyAsync(rule, body, me, db, clock, ct) is { } problem)
        {
            return problem;
        }
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("review.rule.update", rule.Project, detail: rule.Enabled ? null : "off");
        return Results.Ok(Shape(rule, request));
    }

    private static async Task<IResult> DeleteAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, AccessService access, Audit audit, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await FindAsync(db, access, me, id, ct) is not { } rule)
        {
            return Results.NotFound();
        }
        // Reviews still waiting go with it; those done stay.
        await db.CodeReviews.Where(r => r.RuleId == rule.Id && r.Status == CodeReview.Queued).ExecuteDeleteAsync(ct);
        db.ReviewRules.Remove(rule);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("review.rule.delete", rule.Project);
        return Results.NoContent();
    }

    private static async Task<IResult> SecretAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, AccessService access, Audit audit, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await FindAsync(db, access, me, id, ct) is not { } rule)
        {
            return Results.NotFound();
        }
        var secret = NewSecret(rule);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("review.rule.secret", rule.Project);
        return Results.Ok(new { secret });
    }

    /// <summary>A look at the open merge requests now, saying for each what it did.</summary>
    private static async Task<IResult> CheckAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, AccessService access, AutoReviews auto,
        IServiceProvider services, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await FindAsync(db, access, me, id, ct) is not { } rule)
        {
            return Results.NotFound();
        }
        var said = await auto.CheckAsync(services, rule, ct);
        await auto.StartWaitingAsync(db, rule, ct);
        return Results.Ok(new { said });
    }

    /// <summary>GitLab's event for a rule (its secret in X-Gitlab-Token); one the rule does not take is acknowledged and dropped.</summary>
    private static async Task<IResult> HookAsync(Guid id, HttpRequest request, AppDbContext db, AutoReviews auto, IServiceProvider services, CancellationToken ct)
    {
        var rule = await db.ReviewRules.SingleOrDefaultAsync(r => r.Id == id, ct);
        var supplied = request.Headers["X-Gitlab-Token"].FirstOrDefault() ?? "";
        if (rule?.SecretHash is not { } expected || supplied.Length == 0
            || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(supplied)), Encoding.ASCII.GetBytes(expected)))
        {
            return Results.Json(new { error = "unknown rule or wrong secret" }, statusCode: 401);
        }
        if (!rule.Enabled)
        {
            return Results.Ok(new { status = "ignored", reason = "the rule is off" });
        }
        using var reader = new StreamReader(request.Body);
        var body = await reader.ReadToEndAsync(ct);
        if (body.Length > 2_000_000)
        {
            return Results.Json(new { error = "the event is larger than 2 MB" }, statusCode: 413);
        }
        JsonObject? e;
        try
        {
            e = JsonNode.Parse(body.Length == 0 ? "{}" : body) as JsonObject;
        }
        catch (JsonException)
        {
            e = null;
        }
        if (e is null)
        {
            return Results.BadRequest(new { error = "the event is not a JSON object" });
        }
        var status = await auto.EventAsync(services, rule, e, ct);
        return status.StartsWith("ignored", StringComparison.Ordinal) ? Results.Ok(new { status }) : Results.Accepted(value: new { status });
    }
}
