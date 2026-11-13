using System.Globalization;
using System.Security.Claims;
using System.Text.Json.Nodes;
using Llm.Api.Access;
using Llm.Api.Chat;
using Llm.Api.Dashboards;
using Llm.Api.Endpoints;
using Llm.Api.Gateway;
using Llm.Api.Identity;
using Llm.Api.Reports;
using Llm.Core.Chat;
using Llm.Core.Data;
using Llm.Core.Identity;
using Llm.Core.Projects;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

using Branding;
namespace Llm.Api.Projects;

/// <summary>Only what is sent changes. MonthlyBudget: 0 or less takes the budget away; StartsOn and DueOn: "" clears them.</summary>
public sealed record ProjectRequest(string? Name = null, string? Description = null, bool? Global = null, string? Status = null, string? StartsOn = null, string? DueOn = null,
    decimal? MonthlyBudget = null, int? AlertPercent = null, bool? StopAtBudget = null, string[]? Tags = null);

public sealed record ProjectMembersRequest(Guid[]? UserIds = null, Guid[]? GroupIds = null, string? Role = null);

public sealed record ProjectRoleRequest(string Role);

public sealed record ProjectRepositoryRequest(string Path, string? Host = null);

public sealed record ProjectItemRequest(string Kind, Guid ItemId);

/// <summary>A chat's project: null takes it out of the one it is in.</summary>
public sealed record ChatProjectRequest(Guid? ProjectId);

/// <summary>Only what is sent changes. DueOn: "" clears it.</summary>
public sealed record ProjectGoalRequest(string? Title = null, string? Description = null, string? Status = null, string? DueOn = null, int? Position = null, decimal? HumanHours = null);

/// <summary>Only what is sent changes. GoalId and AssigneeId: Guid.Empty clears them.</summary>
public sealed record ProjectTaskRequest(string? Title = null, Guid? GoalId = null, Guid? AssigneeId = null, bool? Done = null, decimal? HumanHours = null);

/// <summary>
/// Projects in Arena and Code Arena: made by anyone who may (feature.projects; a global one, everyone's to see and add
/// to, with feature.projects.global), with their people and groups in roles, their repositories, the chats, rooms and Code
/// Arena sessions put in them, the assistants, skills and apps they use, their goals and tasks, and their accounting over any
/// time (the usage log's requests tagged with them) on their page and as PDF, Excel or CSV. Every change is audited.
/// </summary>
public static class ProjectEndpoints
{
    public const int MostMembers = 500;
    public const int MostGoals = 200;
    public const int MostTasks = 2000;

    public static void MapProjects(this IEndpointRouteBuilder app)
    {
        // The page signed in, and Code Arena's IDE and terminal with the person's key (its IDE relays them): the same routes
        // and the same checks; a key's person needs Code Arena too (KeyHolder).
        var g = app.MapGroup("/api/projects").RequireAuthorization(ArenaMcp.KeyAuthentication.Policy).WithMetadata(new PersonOnlyMetadata()).AddEndpointFilter(KeyHolder);
        g.MapGet("", ListAsync);
        g.MapGet("/may", MayAsync);
        g.MapPost("", CreateAsync).RequirePermission(Core.Access.Permissions.ProjectsCreate);
        g.MapGet("/{id:guid}", GetAsync);
        g.MapPatch("/{id:guid}", UpdateAsync);
        g.MapDelete("/{id:guid}", DeleteAsync);
        g.MapPost("/{id:guid}/members", AddMembersAsync);
        g.MapPatch("/{id:guid}/members/{memberId:guid}", ChangeMemberAsync);
        g.MapDelete("/{id:guid}/members/{memberId:guid}", RemoveMemberAsync);
        g.MapPost("/{id:guid}/repositories", AddRepositoryAsync);
        g.MapDelete("/{id:guid}/repositories", RemoveRepositoryAsync);
        g.MapPost("/{id:guid}/items", AddItemAsync);
        g.MapDelete("/{id:guid}/items/{kind}/{itemId:guid}", RemoveItemAsync);
        g.MapPost("/{id:guid}/goals", AddGoalAsync);
        g.MapPatch("/{id:guid}/goals/{goalId:guid}", ChangeGoalAsync);
        g.MapDelete("/{id:guid}/goals/{goalId:guid}", RemoveGoalAsync);
        g.MapPost("/{id:guid}/tasks", AddTaskAsync);
        g.MapPatch("/{id:guid}/tasks/{taskId:guid}", ChangeTaskAsync);
        g.MapDelete("/{id:guid}/tasks/{taskId:guid}", RemoveTaskAsync);
        g.MapGet("/{id:guid}/report", ReportAsync);
        g.MapGet("/{id:guid}/report/export", ExportAsync);
        app.MapPut("/api/chat/conversations/{id:guid}/project", MoveChatAsync).PersonOnly();
        var arena = app.MapGroup("/api/code-arena").AllowAnonymous("Code Arena: the person's API key is checked on each call.");
        arena.MapGet("/projects", CodeArenaListAsync);
        arena.MapPut("/chats/{id:guid}/project", CodeArenaMoveAsync);
    }

    private static async Task<AppUser> Me(ClaimsPrincipal p, UserManager<AppUser> users) => (await users.GetUserAsync(p))!;

    /// <summary>
    /// A call with a key (Code Arena's: its IDE and terminal) is the key's person's, who may use Code Arena (Admin → Access);
    /// what they may do in a project is then checked as for the page. A call signed in passes as it is.
    /// </summary>
    public static async ValueTask<object?> KeyHolder(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var http = ctx.HttpContext;
        if (http.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var (me, refusal) = await CodeArenaSync.CallerAsync(http, http.RequestServices.GetRequiredService<ArenaMcp.McpPeople>());
            if (me is null)
            {
                return refusal;
            }
        }
        return await next(ctx);
    }

    /// <summary>What the person may do with projects: make one, and make one global (the pages offer only that).</summary>
    private static async Task<IResult> MayAsync(ClaimsPrincipal principal, UserManager<AppUser> users, AccessService access, CancellationToken ct)
    {
        var me = await Me(principal, users);
        var m = await access.MembershipAsync(me, ct);
        return Results.Ok(new
        {
            create = m.Has(Core.Access.Permissions.ProjectsCreate),
            global = m.Has(Core.Access.Permissions.ProjectsCreate) && m.Has(Core.Access.Permissions.ProjectsGlobal),
        });
    }

    private static IResult NotYours() => AuthEndpoints.Problem(403, "role", "Only the project's owners may do that.");

    private static IResult ReadOnly() => AuthEndpoints.Problem(403, "role", "Viewers see the project; its contributors and owners add to it.");

    private static IResult Archived() => AuthEndpoints.Problem(409, "archived", "The project is archived: an owner can make it active again.");

    /// <summary>The project, when the person holds at least <paramref name="need"/> in it (404 when they may not see it, 403 when they may but not that).</summary>
    private static async Task<(Project? Project, string? Role, IResult? Refused)> FindAsync(AppDbContext db, Guid id, AppUser me, Membership m, string need, CancellationToken ct,
        bool active = false)
    {
        var (p, role) = await ProjectAccess.FindAsync(db, id, me.Id, m, ct);
        if (p is null)
        {
            return (null, null, Results.NotFound());
        }
        if (!ProjectAccess.Holds(role, need))
        {
            return (null, role, need == ProjectMember.Owner ? NotYours() : ReadOnly());
        }
        return active && p.Status != Project.Active ? (null, role, Archived()) : (p, role, null);
    }

    private static string Name(AppUser u) => string.IsNullOrEmpty(u.DisplayName) ? u.UserName ?? u.Email ?? "" : u.DisplayName;

    // ------------------------------------------------------------- projects --

    /// <summary>The projects the person sees, with their role, people, chats and this month's spend; the global ones are marked.</summary>
    private static async Task<IResult> ListAsync(ClaimsPrincipal principal, UserManager<AppUser> users, AppDbContext db, AccessService access, CancellationToken ct)
    {
        var me = await Me(principal, users);
        var m = await access.MembershipAsync(me, ct);
        var projects = await ProjectAccess.Visible(db, me.Id, m).AsNoTracking().OrderBy(p => p.Name).Take(1000).ToListAsync(ct);
        var ids = projects.Select(p => p.Id).ToList();
        var groups = m.Groups.ToList();
        var mine = await db.ProjectMembers.AsNoTracking().Where(x => ids.Contains(x.ProjectId) && (x.UserId == me.Id || (x.GroupId != null && groups.Contains(x.GroupId.Value))))
            .Select(x => new { x.ProjectId, x.Role }).ToListAsync(ct);
        var people = await db.ProjectMembers.AsNoTracking().Where(x => ids.Contains(x.ProjectId)).GroupBy(x => x.ProjectId).Select(x => new { x.Key, Count = x.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var chats = await db.Conversations.AsNoTracking().Where(c => c.WorkProjectId != null && ids.Contains(c.WorkProjectId.Value)).GroupBy(c => c.WorkProjectId!.Value)
            .Select(x => new { x.Key, Count = x.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var goals = await db.ProjectGoals.AsNoTracking().Where(x => ids.Contains(x.ProjectId) && x.Status != ProjectGoal.Dropped).GroupBy(x => x.ProjectId)
            .Select(x => new { x.Key, All = x.Count(), Done = x.Count(y => y.Status == ProjectGoal.Done) }).ToDictionaryAsync(x => x.Key, ct);
        var owners = projects.Select(p => p.OwnerId).Distinct().ToList();
        var ownerNames = (await db.Users.AsNoTracking().Where(u => owners.Contains(u.Id)).ToListAsync(ct)).ToDictionary(u => u.Id, Name);
        var spent = await ProjectBudgets.SpentAsync(db, ids, DateTimeOffset.UtcNow, ct);
        var manage = m.Has(Core.Access.Permissions.ProjectsManage);
        return Results.Ok(projects.Select(p =>
        {
            var role = p.OwnerId == me.Id || manage ? ProjectMember.Owner : mine.Where(x => x.ProjectId == p.Id).Select(x => x.Role).MaxBy(ProjectMember.Rank);
            if (p.Global && ProjectMember.Rank(role) < ProjectMember.Rank(ProjectMember.Contributor))
            {
                role = ProjectMember.Contributor;
            }
            return new
            {
                p.Id, p.Name, p.Description, p.Global, p.Status, p.StartsOn, p.DueOn, p.MonthlyBudget, p.Tags, p.Brief, role, owner = ownerNames.GetValueOrDefault(p.OwnerId),
                mine = p.OwnerId == me.Id || mine.Any(x => x.ProjectId == p.Id),
                members = people.GetValueOrDefault(p.Id) + 1, chats = chats.GetValueOrDefault(p.Id),
                goals = goals.TryGetValue(p.Id, out var gl) ? gl.All : 0, goalsDone = goals.TryGetValue(p.Id, out var gd) ? gd.Done : 0,
                spentThisMonth = spent.GetValueOrDefault(p.Id), p.UpdatedAt,
            };
        }));
    }

    private static IResult? Check(ProjectRequest body, bool creating)
    {
        if (creating && string.IsNullOrWhiteSpace(body.Name))
        {
            return AuthEndpoints.Problem(400, "name", "Give the project a name.");
        }
        if (body.Name is { } n && n.Trim().Length is 0 or > 100)
        {
            return AuthEndpoints.Problem(400, "name", "A name has 1 to 100 characters.");
        }
        if (body.Description is { Length: > 2000 })
        {
            return AuthEndpoints.Problem(400, "description", "A description has at most 2,000 characters.");
        }
        if (body.Status is { } s && s is not (Project.Active or Project.Archived))
        {
            return AuthEndpoints.Problem(400, "status", "A project is active or archived.");
        }
        if (body.AlertPercent is { } a && a is < 1 or > 100)
        {
            return AuthEndpoints.Problem(400, "alert", "The alert is at 1% to 100% of the budget.");
        }
        if (body.MonthlyBudget is > 1_000_000)
        {
            return AuthEndpoints.Problem(400, "budget", "A budget is at most $1,000,000 a month.");
        }
        if ((body.StartsOn is { Length: > 0 } start && !DateOnly.TryParse(start, CultureInfo.InvariantCulture, out _))
            || (body.DueOn is { Length: > 0 } due && !DateOnly.TryParse(due, CultureInfo.InvariantCulture, out _)))
        {
            return AuthEndpoints.Problem(400, "date", "Dates are written 2026-12-31.");
        }
        return null;
    }

    private static DateOnly? Date(string? s) => s is { Length: > 0 } && DateOnly.TryParse(s, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static void Apply(Project p, ProjectRequest body)
    {
        if (body.Name is { } name)
        {
            p.Name = name.Trim();
        }
        if (body.Description is { } description)
        {
            p.Description = description.Trim();
        }
        if (body.Tags is { } tags)
        {
            p.Tags = Tags.Tagging.Clean(tags);
        }
        if (body.Global is { } global)
        {
            p.Global = global;
        }
        if (body.Status is { } status)
        {
            p.Status = status;
        }
        if (body.StartsOn is { } starts)
        {
            p.StartsOn = Date(starts);
        }
        if (body.DueOn is { } due)
        {
            p.DueOn = Date(due);
        }
        if (body.MonthlyBudget is { } budget)
        {
            p.MonthlyBudget = budget > 0 ? Math.Round(budget, 4) : null;
        }
        if (body.AlertPercent is { } alert)
        {
            p.AlertPercent = alert;
        }
        if (body.StopAtBudget is { } stop)
        {
            p.StopAtBudget = stop;
        }
    }

    private static async Task<IResult> CreateAsync(ProjectRequest body, ClaimsPrincipal principal, UserManager<AppUser> users, AppDbContext db, AccessService access, Audit audit,
        CancellationToken ct)
    {
        var me = await Me(principal, users);
        if (Check(body, creating: true) is { } problem)
        {
            return problem;
        }
        if (body.Global == true && !await access.MayAsync(me, Core.Access.Permissions.ProjectsGlobal, ct))
        {
            return AuthEndpoints.Problem(403, "global", "Making global projects is not among what you may do: make it for its people, or ask an admin.");
        }
        var p = new Project { Name = body.Name!.Trim(), OwnerId = me.Id };
        Apply(p, body with { Status = Project.Active });
        db.Projects.Add(p);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("project.create", p.Id.ToString(), detail: p.Global ? $"{p.Name} (global)" : p.Name);
        return Results.Created($"/api/projects/{p.Id}", new { id = p.Id });
    }

    /// <summary>
    /// The project with all that is in it, as the person may see it: its people, repositories and things; the chats of theirs,
    /// the rooms they are in by their titles, and the other chats only counted by whose they are (a chat stays its owner's).
    /// </summary>
    private static async Task<IResult> GetAsync(Guid id, ClaimsPrincipal principal, UserManager<AppUser> users, AppDbContext db, AccessService access, CancellationToken ct)
    {
        var me = await Me(principal, users);
        var m = await access.MembershipAsync(me, ct);
        var (p, role, refused) = await FindAsync(db, id, me, m, ProjectMember.Viewer, ct);
        if (p is null)
        {
            return refused!;
        }
        var owner = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == p.OwnerId, ct);
        var rows = await db.ProjectMembers.AsNoTracking().Where(x => x.ProjectId == id).OrderBy(x => x.AddedAt).ToListAsync(ct);
        var userIds = rows.Select(r => r.UserId).OfType<Guid>().ToList();
        var groupIds = rows.Select(r => r.GroupId).OfType<Guid>().ToList();
        var people = (await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToListAsync(ct)).ToDictionary(u => u.Id);
        var groupNames = await db.Groups.AsNoTracking().Where(x => groupIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        var members = rows.Select(r => new
        {
            r.Id, r.Role, r.UserId, r.GroupId, r.AddedAt,
            name = r.UserId is { } u ? (people.TryGetValue(u, out var person) ? Name(person) : "(a former person)") : groupNames.GetValueOrDefault(r.GroupId!.Value) ?? "(a deleted group)",
            email = r.UserId is { } e && people.TryGetValue(e, out var withEmail) ? withEmail.Email : null,
        });
        var repositories = await db.ProjectRepositories.AsNoTracking().Where(r => r.ProjectId == id).OrderBy(r => r.Path).Select(r => new { r.Path, r.Host, r.AddedAt }).ToListAsync(ct);
        var items = await db.ProjectItems.AsNoTracking().Where(i => i.ProjectId == id).ToListAsync(ct);
        var itemIds = items.Select(i => i.ItemId).ToList();
        var assistants = await db.Assistants.AsNoTracking().Where(a => itemIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Name, ct);
        var skills = await db.Skills.AsNoTracking().Where(s => itemIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => "/" + s.Name + " — " + s.Title, ct);
        var apps = await db.Apps.AsNoTracking().Where(a => itemIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Name, ct);
        var things = items.Select(i => new
        {
            i.Kind, id = i.ItemId, i.AddedAt,
            name = i.Kind switch
            {
                ProjectItem.Assistant => assistants.GetValueOrDefault(i.ItemId),
                ProjectItem.Skill => skills.GetValueOrDefault(i.ItemId),
                _ => apps.GetValueOrDefault(i.ItemId),
            } ?? "(removed)",
            mine = i.AddedById == me.Id,
        }).OrderBy(i => i.Kind, StringComparer.Ordinal).ThenBy(i => i.name, StringComparer.OrdinalIgnoreCase);
        var rooms = await RoomAccess.RoomsOfAsync(db, me, ct);
        var chats = await db.Conversations.AsNoTracking().Where(c => c.WorkProjectId == id)
            .Select(c => new { c.Id, c.Title, c.UserId, c.Kind, c.Origin, c.OriginPlace, c.UpdatedAt }).ToListAsync(ct);
        var shown = chats.Where(c => c.UserId == me.Id || rooms.Contains(c.Id)).OrderByDescending(c => c.UpdatedAt)
            .Select(c => new { c.Id, c.Title, kind = c.Kind == RoomMember.Room ? "room" : c.Origin == CodeArenaSync.Origin ? "session" : "chat", place = c.OriginPlace, c.UpdatedAt, mine = c.UserId == me.Id });
        var otherOwners = chats.Where(c => c.UserId != me.Id && !rooms.Contains(c.Id)).GroupBy(c => c.UserId).ToList();
        var otherIds = otherOwners.Select(o => o.Key).ToList();
        var otherNames = (await db.Users.AsNoTracking().Where(u => otherIds.Contains(u.Id)).ToListAsync(ct)).ToDictionary(u => u.Id, Name);
        var goals = await db.ProjectGoals.AsNoTracking().Where(x => x.ProjectId == id).OrderBy(x => x.Position).ThenBy(x => x.CreatedAt).ToListAsync(ct);
        var tasks = await db.ProjectTasks.AsNoTracking().Where(x => x.ProjectId == id).OrderBy(x => x.Done).ThenBy(x => x.CreatedAt).Take(MostTasks).ToListAsync(ct);
        var assignees = tasks.Select(t => t.AssigneeId).OfType<Guid>().Distinct().ToList();
        var assigneeNames = (await db.Users.AsNoTracking().Where(u => assignees.Contains(u.Id)).ToListAsync(ct)).ToDictionary(u => u.Id, Name);
        // The chat or session that set a goal or a task: named and linked when the person may open it, else only said.
        var seen = shown.ToDictionary(c => c.Id);
        object? From(Guid? chat) => chat is not { } c ? null
            : seen.TryGetValue(c, out var it) ? new { id = (Guid?)it.Id, title = (string?)it.Title, it.kind }
            : new { id = (Guid?)null, title = (string?)null, kind = chats.FirstOrDefault(x => x.Id == c)?.Origin == CodeArenaSync.Origin ? "session" : "chat" };
        return Results.Ok(new
        {
            p.Id, p.Name, p.Description, p.Global, p.Status, p.StartsOn, p.DueOn, p.MonthlyBudget, p.AlertPercent, p.StopAtBudget, p.Tags, p.Brief, p.CreatedAt, p.UpdatedAt,
            role, ownerId = p.OwnerId, owner = owner is null ? null : Name(owner), isOwner = p.OwnerId == me.Id,
            members, repositories, items = things, chats = shown,
            otherChats = otherOwners.Select(o => new { person = otherNames.GetValueOrDefault(o.Key) ?? "(a former person)", count = o.Count() }),
            goals = goals.Select(x => new { x.Id, x.Title, x.Description, x.Status, x.DueOn, x.DoneAt, x.Position, x.HumanHours, from = From(x.ChatId) }),
            tasks = tasks.Select(t => new { t.Id, t.Title, t.GoalId, t.AssigneeId, assignee = t.AssigneeId is { } a ? assigneeNames.GetValueOrDefault(a) : null, t.Done, t.DoneAt, t.HumanHours, t.CreatedAt, from = From(t.ChatId) }),
        });
    }

    private static async Task<IResult> UpdateAsync(Guid id, ProjectRequest body, ClaimsPrincipal principal, UserManager<AppUser> users, AppDbContext db, AccessService access, Audit audit,
        CancellationToken ct)
    {
        var me = await Me(principal, users);
        var m = await access.MembershipAsync(me, ct);
        var (p, _, refused) = await FindAsync(db, id, me, m, ProjectMember.Owner, ct);
        if (p is null)
        {
            return refused!;
        }
        if (Check(body, creating: false) is { } problem)
        {
            return problem;
        }
        if (body.Global == true && !p.Global && !m.Has(Core.Access.Permissions.ProjectsGlobal) && !m.Has(Core.Access.Permissions.ProjectsManage))
        {
            return AuthEndpoints.Problem(403, "global", "Making global projects is not among what you may do.");
        }
        var budgetBefore = (p.MonthlyBudget, p.AlertPercent, p.StopAtBudget);
        Apply(p, body);
        p.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        var budget = budgetBefore != (p.MonthlyBudget, p.AlertPercent, p.StopAtBudget)
            ? $"; budget {(p.MonthlyBudget is { } b ? ReportFiles.Money(b) : "none")}, alert at {p.AlertPercent}%{(p.StopAtBudget ? ", stops there" : "")}" : "";
        await audit.WriteAsync("project.update", p.Id.ToString(), detail: p.Name + (p.Global ? " (global)" : "") + (p.Status == Project.Archived ? " (archived)" : "") + budget);
        return Results.NoContent();
    }

    /// <summary>The project goes; its chats stay their owners' (in no project), and its requests stay in the usage log, no longer a project's.</summary>
    private static async Task<IResult> DeleteAsync(Guid id, ClaimsPrincipal principal, UserManager<AppUser> users, AppDbContext db, AccessService access, Audit audit, CancellationToken ct)
    {
        var me = await Me(principal, users);
        var (p, _, refused) = await FindAsync(db, id, me, await access.MembershipAsync(me, ct), ProjectMember.Owner, ct);
        if (p is null)
        {
            return refused!;
        }
        await db.GatewayRequests.Where(r => r.ProjectId == id).ExecuteUpdateAsync(s => s.SetProperty(r => r.ProjectId, (Guid?)null), ct);
        await db.Conversations.IgnoreQueryFilters().Where(c => c.WorkProjectId == id).ExecuteUpdateAsync(s => s.SetProperty(c => c.WorkProjectId, (Guid?)null), ct);
        db.Projects.Remove(p);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("project.delete", id.ToString(), detail: p.Name);
        return Results.NoContent();
    }

    // -------------------------------------------------------------- members --

    private static async Task<IResult> AddMembersAsync(Guid id, ProjectMembersRequest body, ClaimsPrincipal principal, UserManager<AppUser> users, AppDbContext db, AccessService access,
        Audit audit, CancellationToken ct)
    {
        var me = await Me(principal, users);
        var m = await access.MembershipAsync(me, ct);
        var (p, _, refused) = await FindAsync(db, id, me, m, ProjectMember.Owner, ct);
        if (p is null)
        {
            return refused!;
        }
        var role = body.Role ?? ProjectMember.Contributor;
        if (!ProjectMember.Roles.Contains(role))
        {
            return AuthEndpoints.Problem(400, "role", "A role is owner, contributor or viewer.");
        }
        var userIds = (body.UserIds ?? []).Distinct().ToList();
        var groupIds = (body.GroupIds ?? []).Distinct().ToList();
        if (userIds.Count + groupIds.Count == 0)
        {
            return AuthEndpoints.Problem(400, "members", "Choose people or groups.");
        }
        if (await db.Users.CountAsync(u => userIds.Contains(u.Id), ct) != userIds.Count || await db.Groups.CountAsync(x => groupIds.Contains(x.Id), ct) != groupIds.Count)
        {
            return AuthEndpoints.Problem(400, "members", "One of them is not there any more.");
        }
        var rows = await db.ProjectMembers.Where(x => x.ProjectId == id).ToListAsync(ct);
        // Adding a group is sharing with it: one's own groups, those already in, or any with sharing.groups.
        if (!m.Has(Core.Access.Permissions.SharingGroups) && groupIds.Any(g => !m.Groups.Contains(g) && !rows.Any(x => x.GroupId == g)))
        {
            return AuthEndpoints.Problem(403, "groups", "You can add only groups you are in; any group needs sharing.groups (Admin → Access).");
        }
        if (rows.Count + userIds.Count + groupIds.Count > MostMembers)
        {
            return AuthEndpoints.Problem(400, "members", $"A project has at most {MostMembers} people and groups: add a group instead.");
        }
        foreach (var u in userIds.Where(u => u != p.OwnerId))
        {
            if (rows.FirstOrDefault(x => x.UserId == u) is { } had)
            {
                had.Role = role;
            }
            else
            {
                db.ProjectMembers.Add(new ProjectMember { ProjectId = id, UserId = u, Role = role, AddedById = me.Id });
            }
        }
        foreach (var gid in groupIds)
        {
            if (rows.FirstOrDefault(x => x.GroupId == gid) is { } had)
            {
                had.Role = role;
            }
            else
            {
                db.ProjectMembers.Add(new ProjectMember { ProjectId = id, GroupId = gid, Role = role, AddedById = me.Id });
            }
        }
        p.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("project.members", id.ToString(), detail: $"{userIds.Count} people, {groupIds.Count} groups as {role}");
        return Results.NoContent();
    }

    private static async Task<IResult> ChangeMemberAsync(Guid id, Guid memberId, ProjectRoleRequest body, ClaimsPrincipal principal, UserManager<AppUser> users, AppDbContext db,
        AccessService access, Audit audit, CancellationToken ct)
    {
        var me = await Me(principal, users);
        var (p, _, refused) = await FindAsync(db, id, me, await access.MembershipAsync(me, ct), ProjectMember.Owner, ct);
        if (p is null)
        {
            return refused!;
        }
        if (!ProjectMember.Roles.Contains(body.Role))
        {
            return AuthEndpoints.Problem(400, "role", "A role is owner, contributor or viewer.");
        }
        if (await db.ProjectMembers.SingleOrDefaultAsync(x => x.Id == memberId && x.ProjectId == id, ct) is not { } row)
        {
            return Results.NotFound();
        }
        row.Role = body.Role;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("project.members", id.ToString(), detail: $"{(row.UserId is not null ? "a person" : "a group")} now {body.Role}");
        return Results.NoContent();
    }

    /// <summary>An owner takes anyone out; anyone takes themselves out (a global project's people stay its contributors).</summary>
    private static async Task<IResult> RemoveMemberAsync(Guid id, Guid memberId, ClaimsPrincipal principal, UserManager<AppUser> users, AppDbContext db, AccessService access,
        Audit audit, CancellationToken ct)
    {
        var me = await Me(principal, users);
        var m = await access.MembershipAsync(me, ct);
        var (p, role, refused) = await FindAsync(db, id, me, m, ProjectMember.Viewer, ct);
        if (p is null)
        {
            return refused!;
        }
        if (await db.ProjectMembers.SingleOrDefaultAsync(x => x.Id == memberId && x.ProjectId == id, ct) is not { } row)
        {
            return Results.NotFound();
        }
        if (role != ProjectMember.Owner && row.UserId != me.Id)
        {
            return NotYours();
        }
        db.ProjectMembers.Remove(row);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("project.members", id.ToString(), detail: row.UserId == me.Id ? "left" : $"{(row.UserId is not null ? "a person" : "a group")} taken out");
        return Results.NoContent();
    }

    // -------------------------------------------------- repositories, things --

    /// <summary>"group/repo", from a path, a clone address or a web address: what Code Arena's sessions and code-intel know it by.</summary>
    public static (string Path, string? Host)? RepositoryOf(string text, string? host)
    {
        var t = text.Trim();
        string? h = host?.Trim().ToLowerInvariant() is { Length: > 0 } given ? given : null;
        if (Uri.TryCreate(t, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" or "ssh" or "git")
        {
            (h, t) = (u.Host.ToLowerInvariant(), u.AbsolutePath);
        }
        else if (System.Text.RegularExpressions.Regex.Match(t, @"^(?:[^@/\s]+@)?([^:/\s]+):([^\s]+)$") is { Success: true } scp)
        {
            (h, t) = (scp.Groups[1].Value.ToLowerInvariant(), scp.Groups[2].Value);
        }
        t = t.Trim('/');
        if (t.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            t = t[..^4];
        }
        // A web address of a page in it: /-/tree/main and the like.
        if (t.IndexOf("/-/", StringComparison.Ordinal) is var dash and > 0)
        {
            t = t[..dash];
        }
        return t.Length is 0 or > 300 || !t.Contains('/') || t.Any(c => char.IsWhiteSpace(c) || c is '?' or '#') ? null : (t, h);
    }

    private static async Task<IResult> AddRepositoryAsync(Guid id, ProjectRepositoryRequest body, ClaimsPrincipal principal, UserManager<AppUser> users, AppDbContext db,
        AccessService access, Audit audit, CancellationToken ct)
    {
        var me = await Me(principal, users);
        var (p, _, refused) = await FindAsync(db, id, me, await access.MembershipAsync(me, ct), ProjectMember.Contributor, ct, active: true);
        if (p is null)
        {
            return refused!;
        }
        if (RepositoryOf(body.Path ?? "", body.Host) is not { } repo)
        {
            return AuthEndpoints.Problem(400, "path", "Give the repository as group/name, or its address.");
        }
        if (!await db.ProjectRepositories.AnyAsync(r => r.ProjectId == id && r.Path == repo.Path, ct))
        {
            db.ProjectRepositories.Add(new ProjectRepository { ProjectId = id, Path = repo.Path, Host = repo.Host, AddedById = me.Id });
            p.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync("project.repository", id.ToString(), detail: "added " + repo.Path);
        }
        return Results.Ok(new { path = repo.Path, host = repo.Host });
    }

    private static async Task<IResult> RemoveRepositoryAsync(Guid id, string path, ClaimsPrincipal principal, UserManager<AppUser> users, AppDbContext db, AccessService access,
        Audit audit, CancellationToken ct)
    {
        var me = await Me(principal, users);
        var (p, role, refused) = await FindAsync(db, id, me, await access.MembershipAsync(me, ct), ProjectMember.Contributor, ct);
        if (p is null)
        {
            return refused!;
        }
        if (await db.ProjectRepositories.SingleOrDefaultAsync(r => r.ProjectId == id && r.Path == path, ct) is not { } row)
        {
            return Results.NotFound();
        }
        if (role != ProjectMember.Owner && row.AddedById != me.Id)
        {
            return AuthEndpoints.Problem(403, "role", "Who added it, or an owner, takes it out.");
        }
        db.ProjectRepositories.Remove(row);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("project.repository", id.ToString(), detail: "removed " + path);
        return Results.NoContent();
    }

    /// <summary>An assistant, skill or app the person made (or may edit) goes in; being there shares it with no one.</summary>
    private static async Task<IResult> AddItemAsync(Guid id, ProjectItemRequest body, ClaimsPrincipal principal, UserManager<AppUser> users, AppDbContext db, AccessService access,
        Audit audit, CancellationToken ct)
    {
        var me = await Me(principal, users);
        var m = await access.MembershipAsync(me, ct);
        var (p, _, refused) = await FindAsync(db, id, me, m, ProjectMember.Contributor, ct, active: true);
        if (p is null)
        {
            return refused!;
        }
        var manage = m.Has(Core.Access.Permissions.ProjectsManage);
        bool? mayAdd = body.Kind switch
        {
            ProjectItem.Assistant => await db.Assistants.AsNoTracking().SingleOrDefaultAsync(a => a.Id == body.ItemId, ct) is { } a ? manage || Assistants.MayEdit(a, me.Id, m) : null,
            ProjectItem.Skill => await db.Skills.AsNoTracking().SingleOrDefaultAsync(s => s.Id == body.ItemId, ct) is { } s ? manage || s.UserId == me.Id : null,
            ProjectItem.App => await db.Apps.AsNoTracking().SingleOrDefaultAsync(x => x.Id == body.ItemId, ct) is { } app ? manage || app.OwnerId == me.Id : null,
            _ => false,
        };
        if (mayAdd is null)
        {
            return Results.NotFound();
        }
        if (mayAdd == false)
        {
            return ProjectItem.Kinds.Contains(body.Kind)
                ? AuthEndpoints.Problem(403, "owner", "Only what you made (or may edit) goes in a project.")
                : AuthEndpoints.Problem(400, "kind", "A project holds assistants, skills and apps.");
        }
        if (!await db.ProjectItems.AnyAsync(i => i.ProjectId == id && i.Kind == body.Kind && i.ItemId == body.ItemId, ct))
        {
            db.ProjectItems.Add(new ProjectItem { ProjectId = id, Kind = body.Kind, ItemId = body.ItemId, AddedById = me.Id });
            p.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync("project.item", id.ToString(), detail: $"added {body.Kind} {body.ItemId}");
        }
        return Results.NoContent();
    }

    private static async Task<IResult> RemoveItemAsync(Guid id, string kind, Guid itemId, ClaimsPrincipal principal, UserManager<AppUser> users, AppDbContext db, AccessService access,
        Audit audit, CancellationToken ct)
    {
        var me = await Me(principal, users);
        var (p, role, refused) = await FindAsync(db, id, me, await access.MembershipAsync(me, ct), ProjectMember.Contributor, ct);
        if (p is null)
        {
            return refused!;
        }
        if (await db.ProjectItems.SingleOrDefaultAsync(i => i.ProjectId == id && i.Kind == kind && i.ItemId == itemId, ct) is not { } row)
        {
            return Results.NotFound();
        }
        if (role != ProjectMember.Owner && row.AddedById != me.Id)
        {
            return AuthEndpoints.Problem(403, "role", "Who added it, or an owner, takes it out.");
        }
        db.ProjectItems.Remove(row);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("project.item", id.ToString(), detail: $"removed {kind} {itemId}");
        return Results.NoContent();
    }

    // ---------------------------------------------------------------- chats --

    /// <summary>
    /// A chat (its owner's), a room (its owner's) or a Code Arena session goes in a project they may add to, or out of one;
    /// its requests in the usage log go with it.
    /// </summary>
    public static async Task<IResult> PutChatAsync(AppDbContext db, AccessService access, Audit audit, AppUser me, Conversation c, Guid? projectId, CancellationToken ct)
    {
        if (projectId is { } pid)
        {
            var (p, _, refused) = await FindAsync(db, pid, me, await access.MembershipAsync(me, ct), ProjectMember.Contributor, ct, active: true);
            if (p is null)
            {
                return refused!;
            }
        }
        if (c.WorkProjectId != projectId)
        {
            var from = c.WorkProjectId;
            c.WorkProjectId = projectId;
            await db.SaveChangesAsync(ct);
            await ProjectTags.RetagAsync(db, c.Id, projectId, ct);
            await audit.WriteAsync("project.chat", (projectId ?? from)?.ToString(), detail: $"{(projectId is null ? "took out" : "put in")} {(c.IsRoom ? "room" : "chat")} {c.Id}");
        }
        return Results.Ok(new { projectId });
    }

    private static async Task<IResult> MoveChatAsync(Guid id, ChatProjectRequest body, ClaimsPrincipal principal, UserManager<AppUser> users, AppDbContext db, AccessService access,
        Audit audit, CancellationToken ct)
    {
        var me = await Me(principal, users);
        return await RoomAccess.FindAsync(db, id, me, Need.Manage, ct) is { } c ? await PutChatAsync(db, access, audit, me, c, body.ProjectId, ct) : Results.NotFound();
    }

    /// <summary>
    /// The projects a Code Arena session may go in (those the person may add to, active), and the one its repository is
    /// in (<paramref name="repo"/>: the session's repository key, host/group/name), if one is.
    /// </summary>
    private static async Task<IResult> CodeArenaListAsync(HttpContext http, ArenaMcp.McpPeople people, AppDbContext db, AccessService access, string? repo = null)
    {
        var (me, refusal) = await CodeArenaSync.CallerAsync(http, people);
        if (me is null)
        {
            return refusal!;
        }
        var ct = http.RequestAborted;
        var m = await access.MembershipAsync(me, ct);
        var all = await ProjectAccess.Visible(db, me.Id, m).AsNoTracking().Where(p => p.Status == Project.Active).OrderBy(p => p.Name).Take(500).ToListAsync(ct);
        var open = new List<Project>();
        foreach (var p in all)
        {
            if (ProjectAccess.Holds(await ProjectAccess.RoleAsync(db, p, me.Id, m, ct), ProjectMember.Contributor))
            {
                open.Add(p);
            }
        }
        var suggested = repo is { Length: > 0 } ? await ProjectChats.OfRepositoryAsync(db, open.Select(p => p.Id).ToList(), repo, ct) : null;
        return Results.Ok(new { projects = open.Select(p => new { p.Id, p.Name, p.Global, p.Description }), suggested });
    }

    private static async Task<IResult> CodeArenaMoveAsync(Guid id, ChatProjectRequest body, HttpContext http, ArenaMcp.McpPeople people, AppDbContext db, AccessService access,
        Audit audit)
    {
        var (me, refusal) = await CodeArenaSync.CallerAsync(http, people);
        if (me is null)
        {
            return refusal!;
        }
        return await db.Conversations.SingleOrDefaultAsync(c => c.Id == id && c.UserId == me.Id, http.RequestAborted) is { } c
            ? await PutChatAsync(db, access, audit, me, c, body.ProjectId, http.RequestAborted)
            : Results.NotFound();
    }

    // -------------------------------------------------------- goals, tasks --

    private static async Task<IResult> AddGoalAsync(Guid id, ProjectGoalRequest body, ClaimsPrincipal principal, UserManager<AppUser> users, AppDbContext db, AccessService access,
        CancellationToken ct)
    {
        var me = await Me(principal, users);
        var (p, _, refused) = await FindAsync(db, id, me, await access.MembershipAsync(me, ct), ProjectMember.Contributor, ct, active: true);
        if (p is null)
        {
            return refused!;
        }
        if (string.IsNullOrWhiteSpace(body.Title))
        {
            return AuthEndpoints.Problem(400, "title", "Give the goal a title.");
        }
        if (GoalProblem(body) is { } problem)
        {
            return problem;
        }
        if (await db.ProjectGoals.CountAsync(x => x.ProjectId == id, ct) >= MostGoals)
        {
            return AuthEndpoints.Problem(400, "goals", $"A project has at most {MostGoals} goals.");
        }
        var goal = new ProjectGoal { ProjectId = id, Title = body.Title.Trim(), Position = await db.ProjectGoals.Where(x => x.ProjectId == id).MaxAsync(x => (int?)x.Position, ct) + 1 ?? 0 };
        Apply(goal, body);
        db.ProjectGoals.Add(goal);
        p.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/projects/{id}/goals/{goal.Id}", new { id = goal.Id });
    }

    internal static IResult? GoalProblem(ProjectGoalRequest body) => GoalFault(body) is { } f ? AuthEndpoints.Problem(400, f.Code, f.Message) : null;

    /// <summary>What is wrong with a goal as asked (its code and words); null: nothing.</summary>
    internal static (string Code, string Message)? GoalFault(ProjectGoalRequest body) =>
        body.Title is { } t && t.Trim().Length is 0 or > 200 ? ("title", "A goal's title has 1 to 200 characters.")
        : body.Description is { Length: > 2000 } ? ("description", "A goal's description has at most 2,000 characters.")
        : body.Status is { } s && !ProjectGoal.States.Contains(s) ? ("status", "A goal is planned, doing, done or dropped.")
        : body.DueOn is { Length: > 0 } d && !DateOnly.TryParse(d, CultureInfo.InvariantCulture, out _) ? ("date", "Dates are written 2026-12-31.")
        : HoursFault(body.HumanHours);

    /// <summary>The hours by hand: 0 to 100,000 (0 takes the estimate away).</summary>
    private static IResult? HoursProblem(decimal? hours) => HoursFault(hours) is { } f ? AuthEndpoints.Problem(400, f.Code, f.Message) : null;

    private static (string Code, string Message)? HoursFault(decimal? hours) =>
        hours is < 0 or > 100_000 ? ("hours", "The hours by hand are 0 to 100,000 (0 takes the estimate away).") : null;

    private static decimal? Hours(decimal hours) => hours == 0 ? null : Math.Round(hours, 2);

    internal static void Apply(ProjectGoal goal, ProjectGoalRequest body)
    {
        if (body.Title is { } title)
        {
            goal.Title = title.Trim();
        }
        if (body.Description is { } description)
        {
            goal.Description = description.Trim() is { Length: > 0 } text ? text : null;
        }
        if (body.Status is { } status && status != goal.Status)
        {
            goal.Status = status;
            goal.DoneAt = status == ProjectGoal.Done ? DateTimeOffset.UtcNow : null;
        }
        if (body.DueOn is { } due)
        {
            goal.DueOn = Date(due);
        }
        if (body.Position is { } position)
        {
            goal.Position = position;
        }
        if (body.HumanHours is { } hours)
        {
            goal.HumanHours = Hours(hours);
        }
    }

    private static async Task<IResult> ChangeGoalAsync(Guid id, Guid goalId, ProjectGoalRequest body, ClaimsPrincipal principal, UserManager<AppUser> users, AppDbContext db,
        AccessService access, CancellationToken ct)
    {
        var me = await Me(principal, users);
        var (p, _, refused) = await FindAsync(db, id, me, await access.MembershipAsync(me, ct), ProjectMember.Contributor, ct, active: true);
        if (p is null)
        {
            return refused!;
        }
        if (GoalProblem(body) is { } problem)
        {
            return problem;
        }
        if (await db.ProjectGoals.SingleOrDefaultAsync(x => x.Id == goalId && x.ProjectId == id, ct) is not { } goal)
        {
            return Results.NotFound();
        }
        Apply(goal, body);
        p.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> RemoveGoalAsync(Guid id, Guid goalId, ClaimsPrincipal principal, UserManager<AppUser> users, AppDbContext db, AccessService access,
        CancellationToken ct)
    {
        var me = await Me(principal, users);
        var (p, _, refused) = await FindAsync(db, id, me, await access.MembershipAsync(me, ct), ProjectMember.Contributor, ct, active: true);
        if (p is null)
        {
            return refused!;
        }
        return await db.ProjectGoals.Where(x => x.Id == goalId && x.ProjectId == id).ExecuteDeleteAsync(ct) > 0 ? Results.NoContent() : Results.NotFound();
    }

    internal static async Task<IResult?> TaskProblemAsync(AppDbContext db, Guid id, ProjectTaskRequest body, CancellationToken ct) =>
        await TaskFaultAsync(db, id, body, ct) is { } f ? AuthEndpoints.Problem(400, f.Code, f.Message) : null;

    /// <summary>What is wrong with a task as asked (its code and words); null: nothing.</summary>
    internal static async Task<(string Code, string Message)?> TaskFaultAsync(AppDbContext db, Guid id, ProjectTaskRequest body, CancellationToken ct)
    {
        if (body.Title is { } t && t.Trim().Length is 0 or > 300)
        {
            return ("title", "A task's title has 1 to 300 characters.");
        }
        if (body.GoalId is { } g && g != Guid.Empty && !await db.ProjectGoals.AnyAsync(x => x.Id == g && x.ProjectId == id, ct))
        {
            return ("goal", "That goal is not this project's.");
        }
        if (body.AssigneeId is { } a && a != Guid.Empty && !await db.Users.AnyAsync(u => u.Id == a, ct))
        {
            return ("assignee", "That person is not there.");
        }
        return HoursFault(body.HumanHours);
    }

    internal static void Apply(ProjectTask task, ProjectTaskRequest body)
    {
        if (body.Title is { } title)
        {
            task.Title = title.Trim();
        }
        if (body.GoalId is { } goal)
        {
            task.GoalId = goal == Guid.Empty ? null : goal;
        }
        if (body.AssigneeId is { } assignee)
        {
            task.AssigneeId = assignee == Guid.Empty ? null : assignee;
        }
        if (body.Done is { } done && done != task.Done)
        {
            task.Done = done;
            task.DoneAt = done ? DateTimeOffset.UtcNow : null;
        }
        if (body.HumanHours is { } hours)
        {
            task.HumanHours = Hours(hours);
        }
    }

    private static async Task<IResult> AddTaskAsync(Guid id, ProjectTaskRequest body, ClaimsPrincipal principal, UserManager<AppUser> users, AppDbContext db, AccessService access,
        CancellationToken ct)
    {
        var me = await Me(principal, users);
        var (p, _, refused) = await FindAsync(db, id, me, await access.MembershipAsync(me, ct), ProjectMember.Contributor, ct, active: true);
        if (p is null)
        {
            return refused!;
        }
        if (string.IsNullOrWhiteSpace(body.Title))
        {
            return AuthEndpoints.Problem(400, "title", "Give the task a title.");
        }
        if (await TaskProblemAsync(db, id, body, ct) is { } problem)
        {
            return problem;
        }
        if (await db.ProjectTasks.CountAsync(x => x.ProjectId == id, ct) >= MostTasks)
        {
            return AuthEndpoints.Problem(400, "tasks", $"A project has at most {MostTasks} tasks.");
        }
        var task = new ProjectTask { ProjectId = id, Title = body.Title.Trim(), CreatedById = me.Id };
        Apply(task, body);
        db.ProjectTasks.Add(task);
        p.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/projects/{id}/tasks/{task.Id}", new { id = task.Id });
    }

    private static async Task<IResult> ChangeTaskAsync(Guid id, Guid taskId, ProjectTaskRequest body, ClaimsPrincipal principal, UserManager<AppUser> users, AppDbContext db,
        AccessService access, CancellationToken ct)
    {
        var me = await Me(principal, users);
        var (p, _, refused) = await FindAsync(db, id, me, await access.MembershipAsync(me, ct), ProjectMember.Contributor, ct, active: true);
        if (p is null)
        {
            return refused!;
        }
        if (await TaskProblemAsync(db, id, body, ct) is { } problem)
        {
            return problem;
        }
        if (await db.ProjectTasks.SingleOrDefaultAsync(x => x.Id == taskId && x.ProjectId == id, ct) is not { } task)
        {
            return Results.NotFound();
        }
        Apply(task, body);
        p.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> RemoveTaskAsync(Guid id, Guid taskId, ClaimsPrincipal principal, UserManager<AppUser> users, AppDbContext db, AccessService access,
        CancellationToken ct)
    {
        var me = await Me(principal, users);
        var (p, _, refused) = await FindAsync(db, id, me, await access.MembershipAsync(me, ct), ProjectMember.Contributor, ct, active: true);
        if (p is null)
        {
            return refused!;
        }
        return await db.ProjectTasks.Where(x => x.Id == taskId && x.ProjectId == id).ExecuteDeleteAsync(ct) > 0 ? Results.NoContent() : Results.NotFound();
    }

    // --------------------------------------------------------------- report --

    /// <summary>A time window as asked (from, to), checked: positive and at most 400 days; else a problem.</summary>
    public static IResult? RangeProblem(DateTimeOffset from, DateTimeOffset to) =>
        to <= from || to - from > DashboardEndpoints.MaxRange ? AuthEndpoints.Problem(400, "range", "The time range must be positive and at most 400 days.") : null;

    /// <summary>
    /// The project's numbers over a time, for its page: every viewer sees the totals, the progress and the charts; each
    /// person's share is for its owners (others see their own row, and that it is so).
    /// </summary>
    private static async Task<IResult> ReportAsync(Guid id, DateTimeOffset from, DateTimeOffset to, ClaimsPrincipal principal, UserManager<AppUser> users, AppDbContext db,
        AccessService access, LedgerSql sql, CodeIntelMcp codeIntel, Microsoft.Extensions.Options.IOptionsMonitor<Reports.CostOptions> costs, CancellationToken ct)
    {
        var me = await Me(principal, users);
        var (p, role, refused) = await FindAsync(db, id, me, await access.MembershipAsync(me, ct), ProjectMember.Viewer, ct);
        if (p is null)
        {
            return refused!;
        }
        if (RangeProblem(from, to) is { } problem)
        {
            return problem;
        }
        ProjectNumbers n;
        try
        {
            n = await ProjectReport.ReadAsync(sql, db, p, from, to, ProjectReport.IntervalFor(from, to), costs.CurrentValue.PersonHourly, ct);
        }
        catch (Npgsql.NpgsqlException ex)
        {
            return AuthEndpoints.Problem(503, "usage", "Usage cannot be read right now: " + ex.Message);
        }
        var (merged, mergedError) = await MergedAsync(db, codeIntel, me, p.Id, from, to, ct);
        var everyone = role == ProjectMember.Owner;
        return Results.Ok(new
        {
            from = n.From, to = n.To, intervalMs = (long)n.Interval.TotalMilliseconds, n.Totals, n.Progress, n.Models, n.Surfaces,
            human = new { n.Human.Estimated, n.Human.NotEstimated, n.Human.ByHandHours, n.Human.PeopleHours, n.Human.ModelHours, n.Human.HourlyRate, n.Human.ByHandCost, n.Human.ActualCost, n.Human.Faster, n.Human.SavedHours, n.Human.SavedCost },
            series = n.Series, people = everyone ? n.People : [.. n.People.Where(x => x.Id == me.Id)], peopleHidden = !everyone,
            budget = p.MonthlyBudget is { } b ? new { monthly = b, spent = n.SpentThisMonth, percent = b > 0 ? Math.Round(100 * n.SpentThisMonth / b, 1) : 0, p.AlertPercent, p.StopAtBudget } : null,
            merged = merged.Select(x => new { repository = x.Repository, iid = x.Iid, title = x.Title, author = x.Author, mergedAt = x.MergedAt, url = x.Url }), mergedError,
        });
    }

    private static async Task<IResult> ExportAsync(Guid id, DateTimeOffset from, DateTimeOffset to, string? format, ClaimsPrincipal principal, UserManager<AppUser> users,
        AppDbContext db, AccessService access, LedgerSql sql, CodeIntelMcp codeIntel, Audit audit, TimeProvider clock, Microsoft.Extensions.Options.IOptionsMonitor<Reports.CostOptions> costs, CancellationToken ct)
    {
        var me = await Me(principal, users);
        var (p, role, refused) = await FindAsync(db, id, me, await access.MembershipAsync(me, ct), ProjectMember.Viewer, ct);
        if (p is null)
        {
            return refused!;
        }
        if (RangeProblem(from, to) is { } problem)
        {
            return problem;
        }
        if (!ReportFiles.Formats.Contains((format ?? "pdf").ToLowerInvariant()))
        {
            return AuthEndpoints.Problem(400, "format", "Export as pdf, xlsx or csv.");
        }
        ProjectNumbers n;
        try
        {
            n = await ProjectReport.ReadAsync(sql, db, p, from, to, ProjectReport.IntervalFor(from, to), costs.CurrentValue.PersonHourly, ct);
        }
        catch (Npgsql.NpgsqlException ex)
        {
            return AuthEndpoints.Problem(503, "usage", "Usage cannot be read right now: " + ex.Message);
        }
        var (merged, _) = await MergedAsync(db, codeIntel, me, p.Id, from, to, ct);
        var goals = await db.ProjectGoals.AsNoTracking().Where(x => x.ProjectId == id).OrderBy(x => x.Position).ToListAsync(ct);
        var doc = ProjectReport.Document(p, n, goals, [.. merged.Select(x => (x.Repository, x.Iid, x.Title, x.Author, x.MergedAt))], role == ProjectMember.Owner, me.Id, Name(me),
            clock.GetUtcNow());
        await audit.WriteAsync("project.export", id.ToString(), detail: $"{format ?? "pdf"}, {ReportDocument.Day(from)} to {ReportDocument.Day(to)}");
        return ReportFiles.Result(doc, format, p.Name)!;
    }

    private sealed record Merged(string Repository, int Iid, string Title, string? Author, DateTimeOffset? MergedAt, string? Url);

    /// <summary>The changes merged in the project's repositories in the time, as the person may read them (code-intel, as them); why not, when it cannot say.</summary>
    private static async Task<(List<Merged> Merged, string? Error)> MergedAsync(AppDbContext db, CodeIntelMcp codeIntel, AppUser me, Guid id, DateTimeOffset from, DateTimeOffset to,
        CancellationToken ct)
    {
        var repos = await db.ProjectRepositories.AsNoTracking().Where(r => r.ProjectId == id).Select(r => r.Path).ToListAsync(ct);
        if (repos.Count == 0)
        {
            return ([], null);
        }
        if (!codeIntel.Enabled)
        {
            return ([], $"{Brand.CodeIntel} is not set up here: merged changes come from it.");
        }
        var list = new List<Merged>();
        var errors = new List<string>();
        try
        {
            var session = await codeIntel.ConnectAsync(me.Id, me.Email!, ct);
            foreach (var repo in repos.Take(20))
            {
                var (text, isError) = await session.CallAsync("mr_list", new JsonObject
                {
                    ["project"] = repo, ["state"] = "merged", ["updated_after"] = from.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture), ["limit"] = 100,
                }, null, ct);
                if (isError)
                {
                    errors.Add(CodeIntelMcp.IsNoAccess(text) ? $"{repo}: you may not read it." : $"{repo}: {(text.Length > 200 ? text[..200] : text)}");
                    continue;
                }
                var node = JsonNode.Parse(text);
                foreach (var mr in (node is JsonObject o && o["result"] is JsonArray rows ? rows : node as JsonArray ?? []).OfType<JsonObject>())
                {
                    var at = DateTimeOffset.TryParse(mr["merged_at"]?.ToString() ?? mr["updated_at"]?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : (DateTimeOffset?)null;
                    if (at is { } when && (when < from || when >= to))
                    {
                        continue;
                    }
                    list.Add(new Merged(repo, mr["iid"]?.GetValue<int>() ?? 0, mr["title"]?.ToString() ?? "", mr["author"]?.ToString(), at, mr["web_url"]?.ToString()));
                }
            }
        }
        catch (Exception ex) when (ex is McpException or HttpRequestException or System.Text.Json.JsonException or TaskCanceledException or InvalidOperationException or FormatException)
        {
            errors.Add($"{Brand.CodeIntel} could not be reached: try again in a moment.");
        }
        return ([.. list.OrderByDescending(x => x.MergedAt)], errors.Count == 0 ? null : string.Join(" ", errors));
    }
}
