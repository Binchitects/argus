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

/// <summary>Who else may read a review: everyone, chosen groups (Reach), and chosen people by user name or email whatever the reach.</summary>
public sealed record ReviewShare(Core.Access.Reach Reach = Core.Access.Reach.Private, Guid[]? Groups = null, string[]? People = null);

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
        var g = app.MapGroup("/api/code").RequireFeature("code-studio").PersonOnly();
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
        g.MapPut("/reviews/{id:guid}/share", ShareAsync).RequirePermission(Core.Access.Permissions.ShareLinks);
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
            var session = await argus.ConnectAsync(me.Id, me.Email!, ct);
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
        r.Id, r.Project, r.Iid, r.FromRef, r.ToRef, r.Title, r.WebUrl, r.Status, r.Step, r.Verdict, r.Risk, r.Files, r.CreatedAt, r.FinishedAt, r.Error, r.RuleId, r.Trigger,
        shared = r.Reach != Core.Access.Reach.Private || r.SharePeople.Count > 0,
    };

    /// <summary>
    /// The person's reviews, those of the rooms they are in, and those shared with them of projects they may read themselves
    /// (each project asked of Argus once, as the person); newest first.
    /// </summary>
    private static async Task<IResult> ListAsync(ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, AccessService access, ArgusMcp argus, CancellationToken ct)
    {
        var me = await Me(p, users);
        var rooms = await RoomAccess.RoomsOfAsync(db, me, ct);
        var groups = (await access.MembershipAsync(me, ct)).Groups.ToList();
        var list = await db.CodeReviews.AsNoTracking().Where(r => r.UserId == me.Id || (r.RoomId != null && rooms.Contains(r.RoomId.Value))
                || (r.Status == CodeReview.Done && (r.Reach == Core.Access.Reach.Company || r.SharePeople.Contains(me.Id) || (r.Reach == Core.Access.Reach.Groups && r.ShareGroups.Any(g => groups.Contains(g))))))
            .OrderByDescending(r => r.CreatedAt).Take(200).ToListAsync(ct);
        var shared = list.Where(r => r.UserId != me.Id && !(r.RoomId is { } room && rooms.Contains(room))).ToList();
        var readable = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var project in shared.Select(r => r.Project).Distinct().Take(20))
        {
            readable[project] = await ReadsAsync(argus, me, project, ct);
        }
        list = [.. list.Where(r => !shared.Contains(r) || readable.GetValueOrDefault(r.Project))];
        var counts = await db.ReviewFindings.AsNoTracking().Where(f => list.Select(r => r.Id).Contains(f.ReviewId) && f.Verified != false && f.State == ReviewFinding.Open)
            .GroupBy(f => f.ReviewId).Select(x => new { x.Key, n = x.Count() }).ToDictionaryAsync(x => x.Key, x => x.n, ct);
        var owners = await NamesAsync(db, shared.Select(r => r.UserId), ct);
        return Results.Ok(list.Select(r => new
        {
            review = Brief(r), open = counts.GetValueOrDefault(r.Id), mine = r.UserId == me.Id, sharedBy = shared.Contains(r) ? owners.GetValueOrDefault(r.UserId) : null,
        }));
    }

    /// <summary>Whether the person may read a project themselves: Argus, as them, lists its merge requests.</summary>
    private static async Task<bool> ReadsAsync(ArgusMcp argus, AppUser me, string project, CancellationToken ct) =>
        (await ArgusAsync(argus, me, "mr_list", new JsonObject { ["project"] = project, ["state"] = "all", ["limit"] = 1 }, ct)).Problem is null;

    private static async Task<Dictionary<Guid, string>> NamesAsync(AppDbContext db, IEnumerable<Guid> ids, CancellationToken ct)
    {
        var wanted = ids.Distinct().ToList();
        return await db.Users.AsNoTracking().Where(u => wanted.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName == "" ? u.UserName! : u.DisplayName, ct);
    }

    /// <summary>Whether a review is shared with the person (not whether they may read its project: <see cref="ReadsAsync"/>).</summary>
    private static bool SharedWith(CodeReview r, AppUser me, Membership m) =>
        r.Status == CodeReview.Done && (r.Reach == Core.Access.Reach.Company || r.SharePeople.Contains(me.Id) || (r.Reach == Core.Access.Reach.Groups && r.ShareGroups.Any(m.Groups.Contains)));

    /// <summary>
    /// A review the person may see: theirs, one of a room they are in, or any for a reviews admin; with <paramref name="argus"/>,
    /// also one shared with them, while they may read its project themselves (to read only).
    /// </summary>
    private static async Task<CodeReview?> SeenAsync(AppDbContext db, AccessService access, AppUser me, Guid id, CancellationToken ct, ArgusMcp? argus = null)
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
        if (await access.MayAsync(me, Core.Access.Permissions.ReviewsAdmin, ct))
        {
            return r;
        }
        return argus is not null && SharedWith(r, me, await access.MembershipAsync(me, ct)) && await ReadsAsync(argus, me, r.Project, ct) ? r : null;
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
        // Automatic reviews wait their own turn: they do not hold up the person's.
        if (await db.CodeReviews.CountAsync(r => r.UserId == me.Id && r.RuleId == null && (r.Status == CodeReview.Queued || r.Status == CodeReview.Running), ct) >= 3)
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

    private static async Task<IResult> GetAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, AccessService access, ArgusMcp argus, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await SeenAsync(db, access, me, id, ct, argus) is not { } r)
        {
            return Results.NotFound();
        }
        var by = await db.Users.AsNoTracking().Where(u => u.Id == r.UserId).Select(u => u.DisplayName == "" ? u.UserName : u.DisplayName).SingleOrDefaultAsync(ct);
        // Shared with them only: they read it, and change nothing of it.
        var readOnly = await SeenAsync(db, access, me, id, ct) is null;
        // Its owner sees whom it is shared with.
        object? share = null;
        if (r.UserId == me.Id)
        {
            var groupNames = await db.Groups.AsNoTracking().Where(g => r.ShareGroups.Contains(g.Id)).OrderBy(g => g.Name).Select(g => new { g.Id, g.Name }).ToListAsync(ct);
            var people = await db.Users.AsNoTracking().Where(u => r.SharePeople.Contains(u.Id)).OrderBy(u => u.UserName).Select(u => new { u.Id, u.UserName, u.DisplayName }).ToListAsync(ct);
            share = new { r.Reach, groups = groupNames, people, r.SharedAt };
        }
        return Results.Ok(new
        {
            review = Brief(r), r.Summary, r.Ci, r.Model, r.HeadSha, r.PreviousId, r.RoomId, r.PromptTokens, r.CompletionTokens, by, mine = r.UserId == me.Id, share, readOnly,
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
        var room = new Conversation { UserId = me.Id, Title = $"Review: {r.Project} {r.Title}"[..Math.Min(200, $"Review: {r.Project} {r.Title}".Length)], Kind = RoomMember.Room };
        db.Conversations.Add(room);
        var message = new ChatMessage { ConversationId = room.Id, Role = "user", Sequence = 1, Content = RoomText(r, "Review"), AuthorId = me.Id, ForPeople = true };
        db.ChatMessages.Add(message);
        room.CurrentLeafId = message.Id;
        r.RoomId = room.Id;
        await db.SaveChangesAsync(ct);
        hub.Said(room.Id, message.Id);
        return Results.Ok(new { id = room.Id });
    }

    /// <summary>The host a project is on, by its name: github:owner/repo, gitea:owner/repo, else GitLab.</summary>
    public static string HostOf(string project) =>
        project.StartsWith("github:", StringComparison.OrdinalIgnoreCase) ? "GitHub" : project.StartsWith("gitea:", StringComparison.OrdinalIgnoreCase) ? "Gitea" : "GitLab";

    /// <summary>A review as a room's message: its verdict and risk, the summary, and the findings that held.</summary>
    public static string RoomText(CodeReview r, string label)
    {
        var text = $"**{label} of {r.Project} {r.Title}** ({ReviewEngine.VerdictText(r.Verdict)}, {r.Risk} risk) — [open the review](/code/reviews/{r.Id})" +
            (r.WebUrl is { } url ? $" · [in {HostOf(r.Project)}]({url})" : "") + "\n\n" + (r.Summary ?? "") + "\n\n" +
            string.Join('\n', r.Findings.Where(f => f.Verified != false).Select(f => $"- **{f.Severity}** `{f.Path}{(f.Line is { } l ? $":{l}" : "")}` {f.Title}"));
        return text.Length > 60_000 ? text[..60_000] : text;
    }

    /// <summary>
    /// Shares the person's review (done) with everyone, chosen groups, or chosen people, or takes the share back (private, no
    /// one): each of them reads it, without changing its findings, while they may read its project. Audited.
    /// </summary>
    private static async Task<IResult> ShareAsync(Guid id, ReviewShare body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, AccessService access, Audit audit,
        Notifications.Notifier notifier, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await db.CodeReviews.SingleOrDefaultAsync(r => r.Id == id && r.UserId == me.Id, ct) is not { } r)
        {
            return Results.NotFound();
        }
        if (r.Status != CodeReview.Done)
        {
            return AuthEndpoints.Problem(409, "running", "The review is not done yet.");
        }
        List<Guid> groups = body.Reach == Core.Access.Reach.Groups ? [.. (body.Groups ?? []).Distinct()] : [];
        if (body.Reach == Core.Access.Reach.Groups && groups.Count == 0)
        {
            return AuthEndpoints.Problem(400, "groups", "Choose at least one group, or share it with everyone.");
        }
        if (await db.Groups.AsNoTracking().CountAsync(g => groups.Contains(g.Id), ct) != groups.Count)
        {
            return AuthEndpoints.Problem(400, "groups", "A group in the list does not exist.");
        }
        var m = await access.MembershipAsync(me, ct);
        if (!m.IsAdmin && groups.Any(g => !m.Groups.Contains(g) && !r.ShareGroups.Contains(g)))
        {
            return AuthEndpoints.Problem(403, "groups", "You can share with groups you are in.");
        }
        var people = new List<Guid>();
        foreach (var named in (body.People ?? []).Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var lower = named.ToLowerInvariant();
            if (await db.Users.AsNoTracking().Where(u => u.UserName == lower || u.Email == named).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(ct) is not { } person)
            {
                return AuthEndpoints.Problem(400, "people", $"There is no one named {named}.");
            }
            if (person != me.Id)
            {
                people.Add(person);
            }
        }
        var newly = people.Except(r.SharePeople).ToList();
        (r.Reach, r.ShareGroups, r.SharePeople) = (body.Reach, groups, people);
        r.SharedAt = body.Reach == Core.Access.Reach.Private && people.Count == 0 ? null : DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("review.share", r.Id.ToString(), detail: r.SharedAt is null ? "not shared" :
            $"{r.Reach.ToString().ToLowerInvariant()}{(groups.Count > 0 ? $", {groups.Count} groups" : "")}{(people.Count > 0 ? $", {people.Count} people" : "")}");
        // The people named are told, in their bell.
        var from = me.DisplayName == "" ? me.UserName : me.DisplayName;
        foreach (var person in newly)
        {
            await notifier.SendAsync(person, new Notifications.News("review", $"{from} shared a review with you", $"{r.Project} {r.Title}", $"/code/reviews/{r.Id}", $"review-share:{r.Id}:{person}"), ct);
        }
        return Results.Ok(new { r.Reach, groups = r.ShareGroups, people = r.SharePeople, r.SharedAt });
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
