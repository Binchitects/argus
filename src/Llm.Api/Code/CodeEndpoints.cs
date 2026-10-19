using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Llm.Api.Access;
using Llm.Api.Chat;
using Llm.Api.Endpoints;
using Llm.Api.Identity;
using Llm.Core.Chat;
using Llm.Core.Code;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Code;

/// <summary>A review to start: a merge request (project and number), or what one ref has over another (from, to).</summary>
public sealed record NewReview(string Project, int? Iid = null, string? From = null, string? To = null);

public sealed record FindingChange(string State);

/// <summary>A question about code for the online code assistant: a project, maybe a ref or a merge request, and what to ask.</summary>
public sealed record CodeQuestion(string Question, string? Project = null, string? Ref = null, int? Iid = null);

/// <summary>
/// Code review and the online code assistant: merge requests read through Argus with the person's own read-only access
/// (GitLab's rights stay theirs), reviews by the model with findings on lines, discussed in a room; and questions about a
/// repository, branch or merge request answered in a chat with Argus. Nothing is written to the code host.
/// </summary>
public static class CodeEndpoints
{
    public static void MapCode(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/code").PersonOnly();
        g.MapGet("/mrs", MergeRequestsAsync).RequirePermission(Core.Access.Permissions.CodeRead);
        g.MapGet("/mr", MergeRequestAsync).RequirePermission(Core.Access.Permissions.CodeRead);
        g.MapPost("/ask", AskAsync).RequirePermission(Core.Access.Permissions.CodeRead);
        g.MapGet("/reviews", ListAsync);
        g.MapPost("/reviews", StartAsync).RequirePermission(Core.Access.Permissions.ReviewRun);
        g.MapGet("/reviews/{id:guid}", GetAsync);
        g.MapPost("/reviews/{id:guid}/again", AgainAsync).RequirePermission(Core.Access.Permissions.ReviewRun);
        g.MapPut("/reviews/{id:guid}/findings/{findingId:guid}", DecideAsync);
        g.MapPost("/reviews/{id:guid}/room", RoomAsync).RequirePermission(Core.Access.Permissions.RoomsCreate);
        g.MapDelete("/reviews/{id:guid}", DeleteAsync);
    }

    private static async Task<AppUser> Me(ClaimsPrincipal p, UserManager<AppUser> users) => (await users.GetUserAsync(p))!;

    private static IResult NoArgus() => AuthEndpoints.Problem(503, "argus", "Argus is not set up here: it reads the code for reviews and the code assistant.");

    /// <summary>One Argus tool's answer as the person; a refusal (no access, no such project) as a problem.</summary>
    private static async Task<(JsonNode? Answer, IResult? Problem)> ArgusAsync(ArgusMcp argus, AppUser me, string tool, JsonObject args, CancellationToken ct)
    {
        if (!argus.Enabled)
        {
            return (null, NoArgus());
        }
        try
        {
            var session = await argus.ConnectAsync(me.Email!, ct);
            var (text, isError) = await session.CallAsync(tool, args, null, ct);
            if (isError)
            {
                return (null, AuthEndpoints.Problem(ArgusMcp.IsNoAccess(text) ? 404 : 400, "argus", text.Length > 600 ? text[..600] : text));
            }
            return (JsonNode.Parse(text), null);
        }
        catch (Exception ex) when (ex is McpException or HttpRequestException or JsonException or TaskCanceledException)
        {
            return (null, AuthEndpoints.Problem(503, "argus", "Argus could not be reached: try again in a moment."));
        }
    }

    /// <summary>A project's merge requests (open by default), as the person may read them.</summary>
    private static async Task<IResult> MergeRequestsAsync(string project, string? state, ClaimsPrincipal p, UserManager<AppUser> users, ArgusMcp argus, CancellationToken ct)
    {
        var me = await Me(p, users);
        var (answer, problem) = await ArgusAsync(argus, me, "mr_list", new JsonObject { ["project"] = project, ["state"] = state ?? "opened", ["limit"] = 50 }, ct);
        return problem ?? Results.Ok(answer is JsonObject o && o["result"] is JsonArray rows ? rows : answer);
    }

    /// <summary>One merge request with its diff, and the person's reviews of it.</summary>
    private static async Task<IResult> MergeRequestAsync(string project, int iid, ClaimsPrincipal p, UserManager<AppUser> users, ArgusMcp argus, AppDbContext db, CancellationToken ct)
    {
        var me = await Me(p, users);
        var (mr, problem) = await ArgusAsync(argus, me, "mr_get", new JsonObject { ["project"] = project, ["iid"] = iid }, ct);
        if (problem is not null)
        {
            return problem;
        }
        var (diff, diffProblem) = await ArgusAsync(argus, me, "mr_diff", new JsonObject { ["project"] = project, ["iid"] = iid }, ct);
        var reviews = await db.CodeReviews.AsNoTracking().Where(r => r.UserId == me.Id && r.Project == project && r.Iid == iid)
            .OrderByDescending(r => r.CreatedAt).Select(r => new { r.Id, r.Status, r.Verdict, r.Risk, r.HeadSha, r.CreatedAt }).ToListAsync(ct);
        return Results.Ok(new { mr, files = diffProblem is null ? diff?["files"] : null, reviews });
    }

    private static object Brief(CodeReview r) => new
    {
        r.Id, r.Project, r.Iid, r.FromRef, r.ToRef, r.Title, r.WebUrl, r.Status, r.Step, r.Verdict, r.Risk, r.Files, r.CreatedAt, r.FinishedAt, r.Error,
    };

    /// <summary>The person's reviews, and those of the rooms they are in; newest first.</summary>
    private static async Task<IResult> ListAsync(ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, CancellationToken ct)
    {
        var me = await Me(p, users);
        var rooms = await RoomAccess.RoomsOfAsync(db, me, ct);
        var list = await db.CodeReviews.AsNoTracking().Where(r => r.UserId == me.Id || (r.RoomId != null && rooms.Contains(r.RoomId.Value)))
            .OrderByDescending(r => r.CreatedAt).Take(200).ToListAsync(ct);
        var counts = await db.ReviewFindings.AsNoTracking().Where(f => list.Select(r => r.Id).Contains(f.ReviewId) && f.Verified != false && f.State == ReviewFinding.Open)
            .GroupBy(f => f.ReviewId).Select(x => new { x.Key, n = x.Count() }).ToDictionaryAsync(x => x.Key, x => x.n, ct);
        return Results.Ok(list.Select(r => new { review = Brief(r), open = counts.GetValueOrDefault(r.Id), mine = r.UserId == me.Id }));
    }

    /// <summary>A review the person may see: theirs, one of a room they are in, or any for a reviews admin.</summary>
    private static async Task<CodeReview?> SeenAsync(AppDbContext db, AccessService access, AppUser me, Guid id, CancellationToken ct)
    {
        var r = await db.CodeReviews.Include(x => x.Findings).SingleOrDefaultAsync(x => x.Id == id, ct);
        if (r is null)
        {
            return null;
        }
        if (r.UserId == me.Id || (r.RoomId is { } room && await RoomAccess.FindAsync(db, room, me, Need.Read, ct) is not null))
        {
            return r;
        }
        return await access.MayAsync(me, Core.Access.Permissions.ReviewsAdmin, ct) ? r : null;
    }

    private static async Task<IResult> StartAsync(NewReview body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, ArgusMcp argus, ReviewEngine engine,
        Audit audit, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (!argus.Enabled)
        {
            return NoArgus();
        }
        var project = (body.Project ?? "").Trim().Trim('/');
        if (project.Length is 0 or > 500)
        {
            return AuthEndpoints.Problem(400, "project", "Name the project by its path, as group/project.");
        }
        if (body.Iid is null && (string.IsNullOrWhiteSpace(body.From) || string.IsNullOrWhiteSpace(body.To)))
        {
            return AuthEndpoints.Problem(400, "change", "Review a merge request (its number), or what one branch has over another (from and to).");
        }
        if (await db.CodeReviews.CountAsync(r => r.UserId == me.Id && (r.Status == CodeReview.Queued || r.Status == CodeReview.Running), ct) >= 3)
        {
            return AuthEndpoints.Problem(429, "busy", "Three of your reviews are running: wait for one to end.");
        }
        var review = new CodeReview
        {
            UserId = me.Id, Project = project, Iid = body.Iid, FromRef = body.From?.Trim(), ToRef = body.To?.Trim(),
            Title = body.Iid is { } iid ? $"!{iid}" : $"{body.To} over {body.From}",
        };
        db.CodeReviews.Add(review);
        await db.SaveChangesAsync(ct);
        engine.Start(review.Id);
        await audit.WriteAsync("review.start", $"{project}{(body.Iid is { } n ? $"!{n}" : "")}");
        return Results.Accepted($"/api/code/reviews/{review.Id}", new { review.Id });
    }

    /// <summary>Reviews again what came after the last review's head (new commits on the merge request).</summary>
    private static async Task<IResult> AgainAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, AccessService access, ReviewEngine engine, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await SeenAsync(db, access, me, id, ct) is not { } previous)
        {
            return Results.NotFound();
        }
        var review = new CodeReview
        {
            UserId = me.Id, Project = previous.Project, Iid = previous.Iid, FromRef = previous.FromRef, ToRef = previous.ToRef, Title = previous.Title,
            PreviousId = previous.Iid is null ? null : previous.Id, RoomId = previous.RoomId,
        };
        db.CodeReviews.Add(review);
        await db.SaveChangesAsync(ct);
        engine.Start(review.Id);
        return Results.Accepted($"/api/code/reviews/{review.Id}", new { review.Id });
    }

    private static async Task<IResult> GetAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, AccessService access, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await SeenAsync(db, access, me, id, ct) is not { } r)
        {
            return Results.NotFound();
        }
        var by = await db.Users.AsNoTracking().Where(u => u.Id == r.UserId).Select(u => u.DisplayName == "" ? u.UserName : u.DisplayName).SingleOrDefaultAsync(ct);
        return Results.Ok(new
        {
            review = Brief(r), r.Summary, r.Ci, r.Model, r.HeadSha, r.PreviousId, r.RoomId, r.PromptTokens, r.CompletionTokens, by, mine = r.UserId == me.Id,
            findings = r.Findings.OrderBy(f => f.Verified == false).ThenBy(f => f.Severity switch { "critical" => 0, "major" => 1, "minor" => 2, _ => 3 }).ThenBy(f => f.Path).ThenBy(f => f.Line)
                .Select(f => new { f.Id, f.Path, f.Line, f.Severity, f.Category, f.Title, f.Body, f.Suggestion, f.Verified, f.VerifyNote, f.State, f.DecidedAt }),
        });
    }

    /// <summary>A finding resolved, dismissed, or opened again: by whoever may see the review.</summary>
    private static async Task<IResult> DecideAsync(Guid id, Guid findingId, FindingChange body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, AccessService access,
        CancellationToken ct)
    {
        var me = await Me(p, users);
        if (body.State is not (ReviewFinding.Open or ReviewFinding.Resolved or ReviewFinding.Dismissed))
        {
            return AuthEndpoints.Problem(400, "state", "A finding is open, resolved or dismissed.");
        }
        if (await SeenAsync(db, access, me, id, ct) is not { } r || r.Findings.FirstOrDefault(f => f.Id == findingId) is not { } finding)
        {
            return Results.NotFound();
        }
        finding.State = body.State;
        finding.DecidedById = body.State == ReviewFinding.Open ? null : me.Id;
        finding.DecidedAt = body.State == ReviewFinding.Open ? null : DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    /// <summary>The review's room (made on first ask, the person's): its summary and findings in it, for people to discuss.</summary>
    private static async Task<IResult> RoomAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, AccessService access, RoomHub hub, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await SeenAsync(db, access, me, id, ct) is not { } r)
        {
            return Results.NotFound();
        }
        if (r.RoomId is { } existing)
        {
            return Results.Ok(new { id = existing });
        }
        if (r.Status != CodeReview.Done)
        {
            return AuthEndpoints.Problem(409, "running", "The review is not done yet.");
        }
        var room = new Conversation { UserId = me.Id, Title = $"Review: {r.Project} {r.Title}"[..Math.Min(200, $"Review: {r.Project} {r.Title}".Length)], Kind = RoomMember.Room, AnswerMode = RoomEndpoints.MentionMode };
        db.Conversations.Add(room);
        var text = $"**Review of {r.Project} {r.Title}** ({ReviewEngine.VerdictText(r.Verdict)}, {r.Risk} risk) — [open the review](/code/reviews/{r.Id})" +
            (r.WebUrl is { } url ? $" · [in GitLab]({url})" : "") + "\n\n" + (r.Summary ?? "") + "\n\n" +
            string.Join('\n', r.Findings.Where(f => f.Verified != false).Select(f => $"- **{f.Severity}** `{f.Path}{(f.Line is { } l ? $":{l}" : "")}` {f.Title}"));
        var message = new ChatMessage { ConversationId = room.Id, Role = "user", Sequence = 1, Content = text.Length > 60_000 ? text[..60_000] : text, AuthorId = me.Id, ForPeople = true };
        db.ChatMessages.Add(message);
        room.CurrentLeafId = message.Id;
        r.RoomId = room.Id;
        await db.SaveChangesAsync(ct);
        hub.Said(room.Id, message.Id);
        return Results.Ok(new { id = room.Id });
    }

    private static async Task<IResult> DeleteAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, CancellationToken ct)
    {
        var me = await Me(p, users);
        return await db.CodeReviews.Where(r => r.Id == id && r.UserId == me.Id).ExecuteDeleteAsync(ct) == 0 ? Results.NotFound() : Results.NoContent();
    }

    /// <summary>
    /// The online code assistant: a chat with Argus on, the question with where it is about (a project, a branch, a merge
    /// request), answered as the person (their read access). Returns the chat, which the page opens and the answer streams in.
    /// </summary>
    private static async Task<IResult> AskAsync(CodeQuestion body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, AccessService access,
        Chat.Tools.ToolRegistry registry, CancellationToken ct)
    {
        var me = await Me(p, users);
        var question = (body.Question ?? "").Trim();
        if (question.Length == 0)
        {
            return AuthEndpoints.Problem(400, "question", "Ask something first.");
        }
        if (!await registry.MayUseAsync(await access.MembershipAsync(me, ct), "argus", ct))
        {
            return AuthEndpoints.Problem(403, "argus", "The code tool (Argus) is not among your tools: ask an admin.");
        }
        var where = body.Project is { Length: > 0 } project
            ? $"About the project {project}" + (body.Iid is { } iid ? $", merge request !{iid}" : body.Ref is { Length: > 0 } r ? $", at {r}" : "") + ":\n\n"
            : "";
        var c = new Conversation { UserId = me.Id, Title = ChatService.TitleFrom(question), Tools = ["argus"] };
        db.Conversations.Add(c);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { id = c.Id, message = where + question });
    }
}
