using Llm.Api.Access;
using Llm.Api.Chat.Tools;
using Llm.Api.Models;
using Llm.Core.Access;
using Llm.Core.Chat;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Chat;

/// <summary>
/// Who may use, edit and share an assistant, and what a chat with one starts with.
/// Its owner always may; its editors may use and edit it; members of its groups, or
/// everyone once it is company-wide, may use it. Nobody else sees it, admins included,
/// except that admins look after company-wide ones.
/// </summary>
public static class Assistants
{
    /// <summary>Icons and colours a page knows how to draw.</summary>
    public static readonly string[] Icons = ["bot", "sparkles", "code", "book", "briefcase", "flask", "shield", "pen", "chart", "globe", "graduation", "wrench"];

    public static readonly string[] Colors = ["blue", "green", "amber", "red", "violet", "pink", "teal", "slate"];

    public static bool MayEdit(Assistant a, Guid me, Membership m) =>
        a.UserId == me || a.EditorPeople.Contains(me) || a.EditorGroups.Any(m.Groups.Contains) || (a.Reach == Reach.Company && m.IsAdmin);

    public static bool MayUse(Assistant a, Guid me, Membership m) =>
        MayEdit(a, me, m) || a.Reach == Reach.Company || (a.Reach == Reach.Groups && a.Groups.Any(m.Groups.Contains));

    /// <summary>Its owner shares and removes it; admins too, once it is company-wide.</summary>
    public static bool MayShare(Assistant a, Guid me, Membership m) => a.UserId == me || (a.Reach == Reach.Company && m.IsAdmin);

    /// <summary>The assistants a person may use, filtered in the database (as <see cref="MayUse"/>).</summary>
    public static IQueryable<Assistant> Usable(IQueryable<Assistant> all, Guid me, Membership m)
    {
        var groups = m.Groups.ToArray();
        return all.Where(a => a.UserId == me || a.Reach == Reach.Company || a.EditorPeople.Contains(me) ||
            a.EditorGroups.Any(g => groups.Contains(g)) || (a.Reach == Reach.Groups && a.Groups.Any(g => groups.Contains(g))));
    }

    /// <summary>An assistant the person may use, to start a chat with or move one to; null when there is none.</summary>
    public static async Task<Assistant?> UsableAsync(AppDbContext db, AppUser me, Membership m, Guid id, CancellationToken ct)
    {
        var a = await db.Assistants.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        return a is not null && MayUse(a, me.Id, m) ? a : null;
    }

    /// <summary>
    /// A new chat's settings with an assistant: what the request does not set comes from
    /// the assistant, as far as the person may use it (a model they may not use, or a tool,
    /// is left to the defaults).
    /// </summary>
    public static async Task<NewConversation> StartAsync(NewConversation body, Assistant a, AppUser me, IReadOnlyList<ToolChoice> allowed, ModelPolicy policy, ChatModels models,
        ChatOptions chat, CancellationToken ct)
    {
        var model = body.Model;
        if (model is null && a.Model is { } wanted && (await models.ListAsync(ct)).Any(m => m.Name == wanted) && (await policy.AllowedAsync(me, [wanted], ct)).Contains(wanted))
        {
            model = wanted;
        }
        var thinking = body.Thinking ?? (a.Thinking is { } t && ChatEndpoints.ValidThinking(t, chat) ? t : null);
        var tools = body.Tools;
        if (tools is null && body.UseArgus is null && a.Tools is { } chosen)
        {
            var known = allowed.Select(x => x.Tool.Id).ToHashSet();
            tools = [.. chosen.Where(known.Contains)];
        }
        return body with { Model = model, Thinking = thinking, Tools = tools };
    }

    /// <summary>The assistant a chat is with, as the chat shows it; NoAccess when the person lost access to it.</summary>
    public static async Task<object?> OfChatAsync(AppDbContext db, AccessService access, AppUser me, Guid? id, CancellationToken ct)
    {
        if (id is not { } aid || await db.Assistants.AsNoTracking().SingleOrDefaultAsync(x => x.Id == aid, ct) is not { } a)
        {
            return null;
        }
        return MayUse(a, me.Id, await access.MembershipAsync(me, ct)) ? Brief(a) : new { a.Id, a.Name, a.Icon, a.Color, starters = Array.Empty<string>(), noAccess = true };
    }

    /// <summary>An assistant in a chat's header and on a new chat: its name, icon and starters.</summary>
    public static object? Brief(Assistant? a) => a is null ? null : new { a.Id, a.Name, a.Icon, a.Color, a.Starters, noAccess = false };

    /// <summary>Why a chat cannot go on with its assistant (the person lost access to it), or null when it can.</summary>
    public static async Task<string?> LostAsync(AppDbContext db, AccessService access, AppUser me, Conversation c, CancellationToken ct)
    {
        if (c.AssistantId is not { } id || await db.Assistants.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) is not { } a)
        {
            return null;
        }
        return MayUse(a, me.Id, await access.MembershipAsync(me, ct))
            ? null
            : $"You no longer have access to the assistant {a.Name}. Take this chat out of it to go on without it.";
    }
}
