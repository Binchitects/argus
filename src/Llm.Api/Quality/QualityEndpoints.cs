using System.Security.Claims;
using Llm.Api.Chat;
using Llm.Api.Endpoints;
using Llm.Api.Identity;
using Llm.Core.Chat;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Quality;

/// <summary>Thumbs on an answer. Reason, Comment and Share go with a down vote only.</summary>
public sealed record FeedbackRequest(bool Up, string? Reason = null, string? Comment = null, bool Share = false);

/// <summary>The person's vote on a comparison: a, b, tie or bad (both bad).</summary>
public sealed record VoteRequest(string? Vote);

/// <summary>
/// Feedback on answers (thumbs and a reason), the arena (a question to two models, blind,
/// and a vote), its leaderboard, and the admins' quality page.
/// </summary>
public static class QualityEndpoints
{
    /// <summary>Why an answer was bad.</summary>
    public static readonly string[] Reasons = ["wrong", "incomplete", "too_long", "unsafe", "ignored_instructions", "other"];

    public const int MaxComment = 1000;

    /// <summary>How many of the latest down-rated answers the quality page lists.</summary>
    public const int Latest = 30;

    public static void MapQuality(this IEndpointRouteBuilder app)
    {
        var chat = app.MapGroup("/api/chat").RequireAuthorization();
        chat.MapPut("/conversations/{id:guid}/messages/{messageId:guid}/feedback", RateAsync);
        chat.MapDelete("/conversations/{id:guid}/messages/{messageId:guid}/feedback", UnrateAsync);
        chat.MapPost("/conversations/{id:guid}/compare", ChatEndpoints.CompareAsync);
        chat.MapPost("/arena/{id:guid}/vote", VoteAsync);
        app.MapGet("/api/arena/leaderboard", LeaderboardAsync).RequireAuthorization();

        var admin = app.MapGroup("/api/admin/quality").RequireAuthorization(AdminEndpoints.Policy);
        admin.MapGet("", OverviewAsync);
        admin.MapGet("/feedback/{id:guid}", SharedAsync);
    }

    private static async Task<AppUser> Me(ClaimsPrincipal p, UserManager<AppUser> users) => (await users.GetUserAsync(p))!;

    /// <summary>Thumbs up or down on an answer of one's own chat: one per person per answer, changed by rating again.</summary>
    private static async Task<IResult> RateAsync(Guid id, Guid messageId, FeedbackRequest body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (!await db.Conversations.AnyAsync(c => c.Id == id && c.UserId == me.Id, ct))
        {
            return Results.NotFound();
        }
        if (!await db.ChatMessages.AnyAsync(m => m.Id == messageId && m.ConversationId == id && m.Role == "assistant", ct))
        {
            return AuthEndpoints.Problem(400, "message", "Rate an answer of this chat.");
        }
        var reason = string.IsNullOrWhiteSpace(body.Reason) ? null : body.Reason.Trim();
        if (reason is not null && !Reasons.Contains(reason))
        {
            return AuthEndpoints.Problem(400, "reason", $"The reason is one of {string.Join(", ", Reasons)}.");
        }
        var comment = string.IsNullOrWhiteSpace(body.Comment) ? null : body.Comment.Trim();
        if (comment is { Length: > MaxComment })
        {
            return AuthEndpoints.Problem(400, "comment", $"Say it in at most {MaxComment:N0} characters.");
        }
        var f = await db.AnswerFeedback.SingleOrDefaultAsync(x => x.MessageId == messageId && x.UserId == me.Id, ct);
        if (f is null)
        {
            f = new AnswerFeedback { MessageId = messageId, ConversationId = id, UserId = me.Id };
            db.AnswerFeedback.Add(f);
        }
        f.Up = body.Up;
        (f.Reason, f.Comment, f.Shared) = body.Up ? (null, null, false) : (reason, comment, body.Share);
        f.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Results.Ok(ChatQuality.Shape(f));
    }

    private static async Task<IResult> UnrateAsync(Guid id, Guid messageId, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (!await db.Conversations.AnyAsync(c => c.Id == id && c.UserId == me.Id, ct))
        {
            return Results.NotFound();
        }
        await db.AnswerFeedback.Where(f => f.MessageId == messageId && f.ConversationId == id && f.UserId == me.Id).ExecuteDeleteAsync(ct);
        return Results.NoContent();
    }

    /// <summary>
    /// The person's vote on one of their comparisons, once both answers are written; then the
    /// names. A or B better: the chat goes on from that answer (when it was on one of the two).
    /// </summary>
    private static async Task<IResult> VoteAsync(Guid id, VoteRequest body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, AnswerJobs jobs, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await db.ArenaMatches.SingleOrDefaultAsync(m => m.Id == id && m.UserId == me.Id, ct) is not { } match)
        {
            return Results.NotFound();
        }
        if (body.Vote is not { } vote || !Arena.Votes.Contains(vote))
        {
            return AuthEndpoints.Problem(400, "vote", "Vote a (A was better), b (B was better), tie or bad (both were bad).");
        }
        if (match.Vote is not null)
        {
            return AuthEndpoints.Problem(409, "voted", "You have voted on this comparison already.");
        }
        if (match.AnswerA is not { } a || match.AnswerB is not { } b || match.ConversationId is { } busy && jobs.IsAnswering(busy))
        {
            return AuthEndpoints.Problem(409, "answering", "Vote once both answers are written.");
        }
        (match.Vote, match.VotedAt) = (vote, DateTimeOffset.UtcNow);
        Guid? leaf = null;
        if (vote is "a" or "b" && match.ConversationId is { } cid && await db.Conversations.SingleOrDefaultAsync(c => c.Id == cid, ct) is { } c)
        {
            var messages = await db.ChatMessages.AsNoTracking().Where(m => m.ConversationId == cid).ToListAsync(ct);
            var byId = messages.ToDictionary(m => m.Id);
            var children = messages.Where(m => m.ParentId is not null).ToLookup(m => m.ParentId!.Value);
            if (c.CurrentLeafId is { } now && (Arena.Chain(a, byId, children).Contains(now) || Arena.Chain(b, byId, children).Contains(now)))
            {
                c.CurrentLeafId = leaf = Arena.End(vote == "a" ? a : b, children);
            }
        }
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { match.Vote, models = new { a = match.ModelA, b = match.ModelB }, currentLeafId = leaf });
    }

    /// <summary>The company's models on its own questions: Elo from every vote, for everyone unless an admin keeps it to the admins.</summary>
    private static async Task<IResult> LeaderboardAsync(ClaimsPrincipal p, AppDbContext db, IOptionsMonitor<QualityOptions> options, CancellationToken ct)
    {
        var open = options.CurrentValue.PublicLeaderboard;
        if (!open && !p.IsInRole(Roles.Admin))
        {
            return AuthEndpoints.Problem(403, "hidden", "Your admins keep the leaderboard to themselves.");
        }
        return Results.Ok(new { @public = open, board = await BoardAsync(db, null, null, ct) });
    }

    private static async Task<object> BoardAsync(AppDbContext db, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct)
    {
        var votes = await db.ArenaMatches.AsNoTracking()
            .Where(m => m.Vote != null && (from == null || m.VotedAt >= from) && (to == null || m.VotedAt < to))
            .OrderBy(m => m.VotedAt).ThenBy(m => m.Id).Select(m => new { m.ModelA, m.ModelB, m.Vote, m.UserId }).ToListAsync(ct);
        return new
        {
            votes = votes.Count,
            people = votes.Select(v => v.UserId).Distinct().Count(),
            models = Arena.Leaderboard(votes.Select(v => (v.ModelA, v.ModelB, v.Vote!))),
        };
    }

    private sealed record Counts(int Answers, int Up, int Down, Dictionary<string, int> Reasons);

    /// <summary>
    /// Admin → Quality: for the answers written in a time range (default the last 30 days),
    /// per model and per project, how many, how many were rated, the share rated up and the
    /// reasons; the latest down-rated (no content, unless the person shared the chat); and
    /// the arena's leaderboard from the votes cast in it.
    /// </summary>
    private static async Task<IResult> OverviewAsync(AppDbContext db, UserManager<AppUser> users, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct)
    {
        var until = to ?? DateTimeOffset.UtcNow;
        var since = from ?? until.AddDays(-30);
        if (since >= until)
        {
            return AuthEndpoints.Problem(400, "range", "The range must start before it ends.");
        }
        // An answer is the last message of its rounds (one that called no tool).
        var answers = db.ChatMessages.AsNoTracking().Where(m => m.Role == "assistant" && m.ToolCallsJson == null && m.CreatedAt >= since && m.CreatedAt < until)
            .Join(db.Conversations, m => m.ConversationId, c => c.Id, (m, c) => new { m.Id, m.Model, c.ProjectId });
        var written = await answers.GroupBy(x => new { x.Model, x.ProjectId }).Select(g => new { g.Key.Model, g.Key.ProjectId, Count = g.Count() }).ToListAsync(ct);
        var rated = await db.AnswerFeedback.AsNoTracking().Join(answers, f => f.MessageId, x => x.Id, (f, x) => new { f.Up, f.Reason, x.Model, x.ProjectId })
            .GroupBy(x => new { x.Model, x.ProjectId, x.Up, x.Reason }).Select(g => new { g.Key.Model, g.Key.ProjectId, g.Key.Up, g.Key.Reason, Count = g.Count() })
            .ToListAsync(ct);

        object Row(IEnumerable<int> wrote, IEnumerable<(bool Up, string? Reason, int Count)> votes)
        {
            var list = votes.ToList();
            var (n, up, down) = (wrote.Sum(), list.Where(v => v.Up).Sum(v => v.Count), list.Where(v => !v.Up).Sum(v => v.Count));
            return new
            {
                answers = n, rated = up + down, up, down,
                ratedShare = n == 0 ? 0 : Math.Round((double)(up + down) / n, 3),
                upRate = up + down == 0 ? (double?)null : Math.Round((double)up / (up + down), 3),
                reasons = list.Where(v => !v.Up).GroupBy(v => v.Reason ?? "none").ToDictionary(g => g.Key, g => g.Sum(v => v.Count)),
            };
        }

        var models = written.Select(w => w.Model).Concat(rated.Select(r => r.Model)).Distinct()
            .Select(m => new
            {
                model = m ?? "unknown",
                counts = Row(written.Where(w => w.Model == m).Select(w => w.Count), rated.Where(r => r.Model == m).Select(r => (r.Up, r.Reason, r.Count))),
                n = written.Where(w => w.Model == m).Sum(w => w.Count),
            })
            .OrderByDescending(x => x.n).Select(x => new { x.model, x.counts }).ToList();

        var projectIds = written.Select(w => w.ProjectId).Concat(rated.Select(r => r.ProjectId)).Distinct().ToList();
        var ids = projectIds.OfType<Guid>().ToList();
        var names = await db.Projects.AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        var projects = projectIds
            .Select(pid => new
            {
                id = pid, name = pid is { } g ? names.GetValueOrDefault(g, "A project") : null,
                counts = Row(written.Where(w => w.ProjectId == pid).Select(w => w.Count), rated.Where(r => r.ProjectId == pid).Select(r => (r.Up, r.Reason, r.Count))),
                n = written.Where(w => w.ProjectId == pid).Sum(w => w.Count),
            })
            .OrderByDescending(x => x.n).Take(25).Select(x => new { x.id, x.name, x.counts }).ToList();

        var latest = await db.AnswerFeedback.AsNoTracking().Where(f => !f.Up && f.UpdatedAt >= since && f.UpdatedAt < until)
            .Join(db.ChatMessages, f => f.MessageId, m => m.Id, (f, m) => new { f, m.Model })
            .Join(db.Conversations, x => x.f.ConversationId, c => c.Id, (x, c) => new { x.f, x.Model, c.Title, c.ProjectId })
            .OrderByDescending(x => x.f.UpdatedAt).Take(Latest)
            .Select(x => new { x.f.Id, x.f.UpdatedAt, x.Title, x.Model, x.f.Reason, x.f.Comment, x.f.Shared, x.f.UserId, x.ProjectId })
            .ToListAsync(ct);
        // Who rated is said only with a shared chat: they chose to show it to the admins.
        var sharers = latest.Where(x => x.Shared).Select(x => x.UserId).Distinct().ToList();
        var people = await users.Users.AsNoTracking().Where(u => sharers.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName != "" ? u.DisplayName : u.UserName, ct);

        return Results.Ok(new
        {
            from = since, to = until,
            total = Row(written.Select(w => w.Count), rated.Select(r => (r.Up, r.Reason, r.Count))),
            models, projects,
            latest = latest.Select(x => new
            {
                x.Id, at = x.UpdatedAt, x.Title, x.Model, x.Reason, x.Comment, x.Shared,
                project = x.ProjectId is { } g ? names.GetValueOrDefault(g) : null,
                person = x.Shared ? people.GetValueOrDefault(x.UserId) : null,
            }),
            leaderboard = await BoardAsync(db, since, until, ct),
        });
    }

    /// <summary>
    /// A chat its owner shared with the admins on a down vote: its branch down to the rated
    /// answer (questions and answers; a tool's result by its name only). Every read is audited.
    /// </summary>
    private static async Task<IResult> SharedAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, Audit audit, CancellationToken ct)
    {
        var f = await db.AnswerFeedback.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (f is not { Shared: true, Up: false })
        {
            return AuthEndpoints.Problem(404, "not_shared", "This chat was not shared with the admins.");
        }
        var c = await db.Conversations.AsNoTracking().SingleAsync(x => x.Id == f.ConversationId, ct);
        var all = await db.ChatMessages.AsNoTracking().Where(m => m.ConversationId == c.Id).ToDictionaryAsync(m => m.Id, ct);
        var owner = await users.FindByIdAsync(f.UserId.ToString());
        await audit.WriteAsync("quality.read_shared", owner?.UserName, detail: c.Title, actor: await Me(p, users));
        return Results.Ok(new
        {
            c.Title, f.Reason, f.Comment, at = f.UpdatedAt, person = owner is null ? null : owner.DisplayName.Length > 0 ? owner.DisplayName : owner.UserName,
            model = all.GetValueOrDefault(f.MessageId)?.Model,
            messages = ChatService.PathTo(all, f.MessageId).Select(m => new
            {
                m.Id, m.Role, content = m.Role == "tool" ? "" : m.Content, m.ToolName, m.Model, m.CreatedAt, rated = m.Id == f.MessageId,
            }),
        });
    }
}
