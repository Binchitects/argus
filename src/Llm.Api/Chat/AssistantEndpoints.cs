using System.Security.Claims;
using Llm.Api.Access;
using Llm.Api.Chat.Tools;
using Llm.Api.Endpoints;
using Llm.Api.Identity;
using Llm.Api.Models;
using Llm.Core.Access;
using Llm.Core.Chat;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Chat;

/// <summary>Only what is sent changes. Model and Thinking: "" goes back to the deployment's default.</summary>
public sealed record AssistantRequest(string? Name = null, string? Description = null, string? Instructions = null, string? Model = null, string? Thinking = null,
    string[]? Tools = null, string[]? Starters = null, string? Icon = null, string? Color = null);

/// <summary>Who may use an assistant besides its owner and editors (the groups, for Groups), and who may edit it.</summary>
public sealed record AssistantSharing(Reach Reach, Guid[]? Groups = null, Guid[]? EditorPeople = null, Guid[]? EditorGroups = null);

public sealed record AssistantFileRequest(Guid AttachmentId);

/// <summary>
/// Assistants (projects, grown up): instructions, files, a model, thinking, tools and
/// conversation starters for the chats with them; a gallery of those a person may use,
/// with how much each is used; and sharing with groups or the company.
/// </summary>
public static class AssistantEndpoints
{
    public const int MaxAssistants = 200;
    /// <summary>With the embedder, answers read a big assistant's files by their passages (Knowledge/Retrieval.cs).</summary>
    public const int MaxFiles = 200;
    public const int MaxStarters = 4;

    public static void MapAssistants(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/assistants").RequireAuthorization();
        g.MapGet("", ListAsync);
        g.MapPost("", CreateAsync);
        g.MapGet("/{id:guid}", GetAsync);
        g.MapPatch("/{id:guid}", UpdateAsync);
        g.MapPut("/{id:guid}/sharing", ShareAsync);
        g.MapDelete("/{id:guid}", DeleteAsync);
        g.MapPost("/{id:guid}/files", AddFileAsync);
        g.MapDelete("/{id:guid}/files/{attachmentId:guid}", RemoveFileAsync);
    }

    private static async Task<AppUser> Me(ClaimsPrincipal p, UserManager<AppUser> users) => (await users.GetUserAsync(p))!;

    /// <summary>
    /// The assistant, when the person may use it (otherwise 404: it does not exist for them),
    /// and may edit it when <paramref name="edit"/> is set (otherwise 403).
    /// </summary>
    private static async Task<(Assistant? Assistant, IResult? Refused)> FindAsync(AppDbContext db, Guid id, AppUser me, Membership m, bool edit, CancellationToken ct)
    {
        var a = await db.Assistants.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (a is null || !Assistants.MayUse(a, me.Id, m))
        {
            return (null, Results.NotFound());
        }
        return edit && !Assistants.MayEdit(a, me.Id, m)
            ? (null, AuthEndpoints.Problem(403, "not_editor", "Only its owner and the people they chose change this assistant."))
            : (a, null);
    }

    /// <summary>The gallery: every assistant the person may use, with how much it is used.</summary>
    private static async Task<IResult> ListAsync(ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, AccessService access, string? q = null, CancellationToken ct = default)
    {
        var me = await Me(p, users);
        var m = await access.MembershipAsync(me, ct);
        var query = Assistants.Usable(db.Assistants.AsNoTracking(), me.Id, m);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var like = "%" + q.Trim().Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
            query = query.Where(a => EF.Functions.ILike(a.Name, like) || (a.Description != null && EF.Functions.ILike(a.Description, like)));
        }
        var since = DateTimeOffset.UtcNow.AddDays(-30);
        var rows = await query.OrderBy(a => a.Name).Take(500).Select(a => new
        {
            a,
            owner = db.Users.Where(u => u.Id == a.UserId).Select(u => u.DisplayName == "" ? u.UserName : u.DisplayName).FirstOrDefault(),
            people = db.Conversations.Where(c => c.AssistantId == a.Id && c.UpdatedAt >= since).Select(c => c.UserId).Distinct().Count(),
            myChats = db.Conversations.Count(c => c.AssistantId == a.Id && c.UserId == me.Id),
            files = db.AssistantFiles.Count(f => f.AssistantId == a.Id),
        }).ToListAsync(ct);
        return Results.Ok(rows.Select(x => new
        {
            x.a.Id, x.a.Name, x.a.Description, x.a.Icon, x.a.Color, x.a.Reach, x.owner, mine = x.a.UserId == me.Id, canEdit = Assistants.MayEdit(x.a, me.Id, m),
            chats = x.a.ChatsStarted, x.people, x.myChats, x.files, x.a.UpdatedAt,
        }));
    }

    private static async Task<IResult?> CheckAsync(AssistantRequest body, bool creating, AppUser me, Membership m, ChatModels models, ModelPolicy policy, ToolRegistry registry,
        ChatOptions chat, CancellationToken ct)
    {
        if (creating && string.IsNullOrWhiteSpace(body.Name))
        {
            return AuthEndpoints.Problem(400, "name", "Give the assistant a name.");
        }
        if (body.Name is { } n && (n.Trim().Length is 0 or > 100))
        {
            return AuthEndpoints.Problem(400, "name", "A name has 1 to 100 characters.");
        }
        if (body.Description is { Length: > 500 })
        {
            return AuthEndpoints.Problem(400, "description", "A description has at most 500 characters.");
        }
        if (body.Instructions is { Length: > 20_000 })
        {
            return AuthEndpoints.Problem(400, "instructions", "Instructions have at most 20,000 characters.");
        }
        if (body.Starters is { } starters)
        {
            if (starters.Length > MaxStarters)
            {
                return AuthEndpoints.Problem(400, "starters", $"At most {MaxStarters} conversation starters.");
            }
            if (starters.Any(s => s is null || s.Trim().Length is 0 or > 200))
            {
                return AuthEndpoints.Problem(400, "starters", "A conversation starter has 1 to 200 characters.");
            }
        }
        if (body.Icon is { } icon && !Assistants.Icons.Contains(icon))
        {
            return AuthEndpoints.Problem(400, "icon", "Unknown icon.");
        }
        if (body.Color is { } color && !Assistants.Colors.Contains(color))
        {
            return AuthEndpoints.Problem(400, "color", "Unknown colour.");
        }
        if (body.Thinking is { Length: > 0 } thinking && !ChatEndpoints.ValidThinking(thinking, chat))
        {
            return AuthEndpoints.Problem(400, "thinking", "Unknown thinking level.");
        }
        if (body.Model is { Length: > 0 } model)
        {
            if ((await models.ListAsync(ct)).All(x => x.Name != model))
            {
                return AuthEndpoints.Problem(400, "model", $"The gateway does not serve {model}.");
            }
            if (!(await policy.AllowedAsync(me, [model], ct)).Contains(model))
            {
                return AuthEndpoints.Problem(403, "model", $"You may not use {model}.");
            }
        }
        if (body.Tools is { } tools)
        {
            var known = (await registry.ForAsync(m, ct)).Select(t => t.Tool.Id).ToHashSet();
            if (tools.FirstOrDefault(t => !known.Contains(t)) is { } unknown)
            {
                return AuthEndpoints.Problem(400, "tools", $"The tool {unknown} is not available to you.");
            }
        }
        return null;
    }

    private static void Apply(Assistant a, AssistantRequest body)
    {
        if (body.Name is { } name)
        {
            a.Name = name.Trim();
        }
        if (body.Description is not null)
        {
            a.Description = Blank(body.Description);
        }
        if (body.Instructions is not null)
        {
            a.Instructions = Blank(body.Instructions);
        }
        if (body.Model is not null)
        {
            a.Model = Blank(body.Model);
        }
        if (body.Thinking is not null)
        {
            a.Thinking = Blank(body.Thinking);
        }
        if (body.Tools is not null)
        {
            a.Tools = [.. body.Tools.Distinct()];
        }
        if (body.Starters is not null)
        {
            a.Starters = [.. body.Starters.Select(s => s.Trim())];
        }
        if (body.Icon is not null)
        {
            a.Icon = body.Icon;
        }
        if (body.Color is not null)
        {
            a.Color = body.Color;
        }
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>A new assistant: its maker's alone until they share it.</summary>
    private static async Task<IResult> CreateAsync(AssistantRequest body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, AccessService access, ChatModels models,
        ModelPolicy policy, ToolRegistry registry, IOptionsMonitor<ChatOptions> chat, CancellationToken ct)
    {
        var me = await Me(p, users);
        var m = await access.MembershipAsync(me, ct);
        if (await CheckAsync(body, true, me, m, models, policy, registry, chat.CurrentValue, ct) is { } problem)
        {
            return problem;
        }
        if (await db.Assistants.CountAsync(x => x.UserId == me.Id, ct) >= MaxAssistants)
        {
            return AuthEndpoints.Problem(409, "limit", $"At most {MaxAssistants} assistants each: remove one first.");
        }
        var a = new Assistant { UserId = me.Id, Name = "" };
        Apply(a, body);
        db.Assistants.Add(a);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/assistants/{a.Id}", new { a.Id, a.Name, a.Description, a.Instructions, a.UpdatedAt });
    }

    private static async Task<IResult> GetAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, AccessService access, CancellationToken ct)
    {
        var me = await Me(p, users);
        var m = await access.MembershipAsync(me, ct);
        var (a, refused) = await FindAsync(db, id, me, m, false, ct);
        if (a is null)
        {
            return refused!;
        }
        var files = await db.AssistantFiles.AsNoTracking().Where(f => f.AssistantId == id)
            .Join(db.ChatAttachments, f => f.AttachmentId, x => x.Id, (f, x) => new { f.AddedAt, x })
            .OrderBy(x => x.AddedAt)
            .Select(x => new { x.x.Id, x.x.FileName, x.x.Size, x.x.Truncated, x.x.Kind, x.x.ContentType, original = x.x.Kind != "image" && x.x.Data != null, x.AddedAt })
            .ToListAsync(ct);
        // The person's own chats with it: other people's stay theirs.
        var chats = await db.Conversations.AsNoTracking().Where(c => c.AssistantId == id && c.UserId == me.Id).OrderByDescending(c => c.UpdatedAt).Take(300)
            .Select(c => new { c.Id, c.Title, c.UpdatedAt, c.ArchivedAt }).ToListAsync(ct);
        var since = DateTimeOffset.UtcNow.AddDays(-30);
        var people = await db.Conversations.Where(c => c.AssistantId == id && c.UpdatedAt >= since).Select(c => c.UserId).Distinct().CountAsync(ct);
        var owner = await db.Users.AsNoTracking().Where(u => u.Id == a.UserId).Select(u => new { u.Id, name = u.DisplayName == "" ? u.UserName : u.DisplayName }).SingleOrDefaultAsync(ct);
        var canEdit = Assistants.MayEdit(a, me.Id, m);
        return Results.Ok(new
        {
            a.Id, a.Name, a.Description, a.Instructions, a.Model, a.Thinking, a.Tools, a.Starters, a.Icon, a.Color, a.Reach, owner,
            mine = a.UserId == me.Id, canEdit, canShare = Assistants.MayShare(a, me.Id, m), a.CreatedAt, a.UpdatedAt, files, chats,
            usage = new { chats = a.ChatsStarted, people },
            // Who it is shared with, for those who may change it.
            sharing = canEdit ? await SharingAsync(db, a, ct) : null,
        });
    }

    private static async Task<object> SharingAsync(AppDbContext db, Assistant a, CancellationToken ct)
    {
        var groupIds = a.Groups.Concat(a.EditorGroups).Distinct().ToList();
        var groups = await db.Groups.AsNoTracking().Where(g => groupIds.Contains(g.Id)).ToDictionaryAsync(g => g.Id, g => g.Name, ct);
        var editors = a.EditorPeople;
        var people = await db.Users.AsNoTracking().Where(u => editors.Contains(u.Id))
            .Select(u => new { u.Id, name = u.DisplayName == "" ? u.UserName : u.DisplayName, u.UserName }).ToListAsync(ct);
        return new
        {
            groups = a.Groups.Where(groups.ContainsKey).Select(g => new { id = g, name = groups[g] }),
            editorPeople = people.OrderBy(x => x.name, StringComparer.OrdinalIgnoreCase),
            editorGroups = a.EditorGroups.Where(groups.ContainsKey).Select(g => new { id = g, name = groups[g] }),
        };
    }

    private static async Task<IResult> UpdateAsync(Guid id, AssistantRequest body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, AccessService access, ChatModels models,
        ModelPolicy policy, ToolRegistry registry, IOptionsMonitor<ChatOptions> chat, CancellationToken ct)
    {
        var me = await Me(p, users);
        var m = await access.MembershipAsync(me, ct);
        var (a, refused) = await FindAsync(db, id, me, m, true, ct);
        if (a is null)
        {
            return refused!;
        }
        if (await CheckAsync(body, false, me, m, models, policy, registry, chat.CurrentValue, ct) is { } problem)
        {
            return problem;
        }
        Apply(a, body);
        a.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    /// <summary>
    /// Who may use it and who may edit it: its owner decides (an admin too, once it is company-wide);
    /// only admins make it company-wide; a person who is not an admin shares with groups they are in.
    /// </summary>
    private static async Task<IResult> ShareAsync(Guid id, AssistantSharing body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, AccessService access, Audit audit,
        CancellationToken ct)
    {
        var me = await Me(p, users);
        var m = await access.MembershipAsync(me, ct);
        var (a, refused) = await FindAsync(db, id, me, m, false, ct);
        if (a is null)
        {
            return refused!;
        }
        if (!Assistants.MayShare(a, me.Id, m))
        {
            return AuthEndpoints.Problem(403, "not_owner", "Only its owner shares this assistant (and admins, once it is company-wide).");
        }
        if (body.Reach == Reach.Company && !m.IsAdmin)
        {
            return AuthEndpoints.Problem(403, "company", "Only admins share an assistant with the whole company.");
        }
        List<Guid> groups = body.Reach == Reach.Groups ? [.. (body.Groups ?? []).Distinct()] : [];
        if (body.Reach == Reach.Groups && groups.Count == 0)
        {
            return AuthEndpoints.Problem(400, "groups", "Choose at least one group, or keep it private.");
        }
        List<Guid> editorGroups = [.. (body.EditorGroups ?? []).Distinct()];
        var all = groups.Concat(editorGroups).Distinct().ToList();
        var names = await db.Groups.AsNoTracking().Where(g => all.Contains(g.Id)).ToDictionaryAsync(g => g.Id, g => g.Name, ct);
        if (names.Count != all.Count)
        {
            return AuthEndpoints.Problem(400, "groups", "A group in the list does not exist.");
        }
        // Groups it has already stay; a new one is one of the person's own (any, for an admin).
        if (!m.IsAdmin && all.Any(g => !m.Groups.Contains(g) && !a.Groups.Contains(g) && !a.EditorGroups.Contains(g)))
        {
            return AuthEndpoints.Problem(403, "groups", "You can share with groups you are in.");
        }
        List<Guid> editors = [.. (body.EditorPeople ?? []).Where(x => x != a.UserId).Distinct()];
        if (await db.Users.CountAsync(u => editors.Contains(u.Id), ct) != editors.Count)
        {
            return AuthEndpoints.Problem(400, "people", "Someone in the list does not exist.");
        }
        (a.Reach, a.Groups, a.EditorPeople, a.EditorGroups, a.UpdatedAt) = (body.Reach, groups, editors, editorGroups, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(ct);
        var who = body.Reach switch
        {
            Reach.Company => "the company",
            Reach.Groups => "groups " + string.Join(", ", groups.Select(g => names[g])),
            _ => "private",
        };
        await audit.WriteAsync("assistant.share", a.Name, detail: $"{who}; editors: {editors.Count} people, {editorGroups.Count} groups");
        return Results.NoContent();
    }

    /// <summary>Removes an assistant: chats with it stay (without it), or the person's own go too with ?chats=delete.</summary>
    private static async Task<IResult> DeleteAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, AccessService access, Audit audit,
        Retention.Retention retention, string? chats = null, CancellationToken ct = default)
    {
        var me = await Me(p, users);
        var m = await access.MembershipAsync(me, ct);
        var (a, refused) = await FindAsync(db, id, me, m, false, ct);
        if (a is null)
        {
            return refused!;
        }
        if (!Assistants.MayShare(a, me.Id, m))
        {
            return AuthEndpoints.Problem(403, "not_owner", "Only its owner removes this assistant (and admins, once it is company-wide).");
        }
        if (chats == "delete")
        {
            // As a chat deleted on its own: its files go with it, and under legal hold it is only hidden.
            await retention.DeleteAsync(me, await db.Conversations.Where(c => c.AssistantId == id && c.UserId == me.Id).Select(c => c.Id).ToListAsync(ct), ct);
        }
        db.Assistants.Remove(a);
        await db.SaveChangesAsync(ct);
        if (a.Reach != Reach.Private)
        {
            await audit.WriteAsync("assistant.delete", a.Name);
        }
        return Results.NoContent();
    }

    private static async Task<IResult> AddFileAsync(Guid id, AssistantFileRequest body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, AccessService access,
        Knowledge.FileIndex index, CancellationToken ct)
    {
        var me = await Me(p, users);
        var (a, refused) = await FindAsync(db, id, me, await access.MembershipAsync(me, ct), true, ct);
        if (a is null)
        {
            return refused!;
        }
        if (!await db.ChatAttachments.AnyAsync(x => x.Id == body.AttachmentId && x.UserId == me.Id, ct))
        {
            return AuthEndpoints.Problem(400, "attachment", "Upload the file first; only your own files can be added.");
        }
        if (await db.AssistantFiles.AnyAsync(f => f.AssistantId == id && f.AttachmentId == body.AttachmentId, ct))
        {
            return Results.NoContent();
        }
        if (await db.AssistantFiles.CountAsync(f => f.AssistantId == id, ct) >= MaxFiles)
        {
            return AuthEndpoints.Problem(409, "limit", $"At most {MaxFiles} files an assistant.");
        }
        db.AssistantFiles.Add(new AssistantFile { AssistantId = id, AttachmentId = body.AttachmentId });
        a.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        // Embedded now, so an assistant too big to inline is read by its passages from its first question.
        _ = index.QueueAsync(body.AttachmentId);
        return Results.NoContent();
    }

    private static async Task<IResult> RemoveFileAsync(Guid id, Guid attachmentId, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, AccessService access,
        CancellationToken ct)
    {
        var me = await Me(p, users);
        var (a, refused) = await FindAsync(db, id, me, await access.MembershipAsync(me, ct), true, ct);
        if (a is null)
        {
            return refused!;
        }
        return await db.AssistantFiles.Where(f => f.AssistantId == id && f.AttachmentId == attachmentId).ExecuteDeleteAsync(ct) > 0 ? Results.NoContent() : Results.NotFound();
    }
}
