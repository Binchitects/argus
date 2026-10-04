using System.Security.Claims;
using Llm.Api.Access;
using Llm.Api.Endpoints;
using Llm.Api.Identity;
using Llm.Api.Models;
using Llm.Core.Access;
using Llm.Core.Chat;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Chat;

/// <summary>
/// A chat's link: for the company or chosen groups; Branch shares only the branch that
/// ends at <see cref="MessageId"/> (default: the end of the branch on screen).
/// </summary>
public sealed record ShareRequest(Reach Reach = Reach.Company, Guid[]? Groups = null, bool Branch = false, Guid? MessageId = null);

/// <summary>
/// Shared chats: a read-only link to a chat for people in the company or in chosen
/// groups, which they can fork into their own chats. Its owner revokes it in one
/// click and sees how many opened it. Links never work signed out, and a link the
/// person may not open is a link that does not exist.
/// </summary>
public static class ShareEndpoints
{
    public static void MapShares(this IEndpointRouteBuilder app)
    {
        var mine = app.MapGroup("/api/chat/conversations/{id:guid}/share").RequireAuthorization();
        mine.MapGet("", GetAsync);
        mine.MapPut("", PutAsync);
        mine.MapDelete("", RevokeAsync);
        var g = app.MapGroup("/api/shared").RequireAuthorization();
        g.MapGet("/{shareId:guid}", OpenAsync);
        g.MapPost("/{shareId:guid}/fork", ForkAsync);
    }

    private static async Task<AppUser> Me(ClaimsPrincipal p, UserManager<AppUser> users) => (await users.GetUserAsync(p))!;

    public static bool MayOpen(ChatShare s, Guid me, Membership m) =>
        s.UserId == me || s.Reach == Reach.Company || (s.Reach == Reach.Groups && s.Groups.Any(m.Groups.Contains));

    /// <summary>The link, when it exists and the person may open it.</summary>
    private static async Task<ChatShare?> FindAsync(AppDbContext db, AccessService access, AppUser me, Guid shareId, CancellationToken ct)
    {
        var share = await db.ChatShares.AsNoTracking().SingleOrDefaultAsync(s => s.Id == shareId, ct);
        return share is not null && MayOpen(share, me.Id, await access.MembershipAsync(me, ct)) ? share : null;
    }

    /// <summary>The chat's link as its owner sees it: who may open it, what it shows, and how many opened it.</summary>
    private static async Task<object> ShapeAsync(AppDbContext db, ChatShare s, CancellationToken ct)
    {
        var groups = await db.Groups.AsNoTracking().Where(g => s.Groups.Contains(g.Id)).OrderBy(g => g.Name).Select(g => new { g.Id, g.Name }).ToListAsync(ct);
        var people = await db.ChatShareViews.CountAsync(v => v.ShareId == s.Id, ct);
        return new { s.Id, s.Reach, groups, branch = s.LeafId is not null, s.LeafId, s.Opens, people, s.CreatedAt };
    }

    private static async Task<IResult> GetAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (!await db.Conversations.AnyAsync(c => c.Id == id && c.UserId == me.Id, ct))
        {
            return Results.NotFound();
        }
        var share = await db.ChatShares.AsNoTracking().SingleOrDefaultAsync(s => s.ConversationId == id, ct);
        return Results.Ok(new { share = share is null ? null : await ShapeAsync(db, share, ct) });
    }

    /// <summary>Shares the chat, or changes its link (one link a chat; its address stays).</summary>
    private static async Task<IResult> PutAsync(Guid id, ShareRequest body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, AccessService access, Audit audit,
        CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await db.Conversations.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id && c.UserId == me.Id, ct) is not { } c)
        {
            return Results.NotFound();
        }
        if (c.CurrentLeafId is null)
        {
            return AuthEndpoints.Problem(400, "empty", "This chat has nothing to share yet.");
        }
        if (body.Reach == Reach.Private)
        {
            return AuthEndpoints.Problem(400, "reach", "A link is for the company or for chosen groups.");
        }
        var share = await db.ChatShares.SingleOrDefaultAsync(s => s.ConversationId == id, ct);
        List<Guid> groups = body.Reach == Reach.Groups ? [.. (body.Groups ?? []).Distinct()] : [];
        if (body.Reach == Reach.Groups && groups.Count == 0)
        {
            return AuthEndpoints.Problem(400, "groups", "Choose at least one group, or share with the company.");
        }
        var names = await db.Groups.AsNoTracking().Where(g => groups.Contains(g.Id)).ToDictionaryAsync(g => g.Id, g => g.Name, ct);
        if (names.Count != groups.Count)
        {
            return AuthEndpoints.Problem(400, "groups", "A group in the list does not exist.");
        }
        var m = await access.MembershipAsync(me, ct);
        if (!m.IsAdmin && groups.Any(g => !m.Groups.Contains(g) && !(share?.Groups.Contains(g) ?? false)))
        {
            return AuthEndpoints.Problem(403, "groups", "You can share with groups you are in.");
        }
        Guid? leaf = null;
        if (body.Branch)
        {
            leaf = body.MessageId ?? c.CurrentLeafId;
            if (await db.ChatMessages.AsNoTracking().SingleOrDefaultAsync(x => x.Id == leaf && x.ConversationId == id, ct) is not { } last)
            {
                return AuthEndpoints.Problem(400, "message", "That message is not in this chat.");
            }
            if (last.Role == "tool" || last.ToolCallsJson is not null)
            {
                return AuthEndpoints.Problem(400, "message", "Share a branch that ends on a question or a finished answer.");
            }
        }
        if (share is null)
        {
            share = new ChatShare { ConversationId = id, UserId = me.Id };
            db.ChatShares.Add(share);
        }
        (share.Reach, share.Groups, share.LeafId) = (body.Reach, groups, leaf);
        await db.SaveChangesAsync(ct);
        var who = body.Reach == Reach.Company ? "the company" : "groups " + string.Join(", ", groups.Select(g => names[g]));
        // The chat's own words stay out of the log: it says what was opened to whom.
        await audit.WriteAsync("chat.share", $"chat {id}", detail: $"{(leaf is null ? "the whole chat" : "one branch")} with {who}");
        return Results.Ok(await ShapeAsync(db, share, ct));
    }

    /// <summary>Revokes the link: it stops working at once, for everyone.</summary>
    private static async Task<IResult> RevokeAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, Audit audit, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await db.ChatShares.Where(s => s.ConversationId == id && s.UserId == me.Id).ExecuteDeleteAsync(ct) == 0)
        {
            return Results.NotFound();
        }
        await audit.WriteAsync("chat.unshare", $"chat {id}");
        return Results.NoContent();
    }

    /// <summary>
    /// The shared chat, read-only: its messages and files (the whole chat with its branches,
    /// or the one branch shared), who shared it, and whether it is the person's own.
    /// </summary>
    private static async Task<IResult> OpenAsync(Guid shareId, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, AccessService access, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await FindAsync(db, access, me, shareId, ct) is not { } share)
        {
            return Results.NotFound();
        }
        var c = await db.Conversations.AsNoTracking().SingleAsync(x => x.Id == share.ConversationId, ct);
        var all = await db.ChatMessages.AsNoTracking().Where(m => m.ConversationId == c.Id).OrderBy(m => m.Sequence).ToListAsync(ct);
        var shown = share.LeafId is { } leaf ? ChatService.PathTo(all.ToDictionary(m => m.Id), leaf) : all;
        var mine = share.UserId == me.Id;
        if (!mine)
        {
            await SeenAsync(db, share.Id, me.Id, ct);
        }
        var owner = await db.Users.AsNoTracking().Where(u => u.Id == share.UserId).Select(u => u.DisplayName == "" ? u.UserName : u.DisplayName).SingleAsync(ct);
        return Results.Ok(new
        {
            share.Id, c.Title, owner, mine, branch = share.LeafId is not null, sharedAt = share.CreatedAt, c.UpdatedAt,
            currentLeafId = share.LeafId ?? c.CurrentLeafId, messages = await ChatEndpoints.MessagesAsync(db, shown, ct),
            // Its owner manages the link from here too.
            link = mine ? await ShapeAsync(db, share, ct) : null, conversationId = mine ? c.Id : (Guid?)null,
        });
    }

    /// <summary>Counts an opening by someone other than the owner, once per person for "how many people".</summary>
    private static async Task SeenAsync(AppDbContext db, Guid shareId, Guid userId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await db.ChatShares.Where(s => s.Id == shareId).ExecuteUpdateAsync(s => s.SetProperty(x => x.Opens, x => x.Opens + 1), ct);
        if (await db.ChatShareViews.Where(v => v.ShareId == shareId && v.UserId == userId).ExecuteUpdateAsync(s => s.SetProperty(x => x.LastAt, now), ct) > 0)
        {
            return;
        }
        db.ChatShareViews.Add(new ChatShareView { ShareId = shareId, UserId = userId, FirstAt = now, LastAt = now });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // The same person opened it twice at once: counted already.
        }
    }

    /// <summary>
    /// Forks the shared chat into the person's own chats, up to a message it shows (default: the
    /// end of what it shows). The files are copied: the fork stays whole whatever becomes of
    /// the shared chat. The owner's own instructions for the chat stay theirs.
    /// </summary>
    private static async Task<IResult> ForkAsync(Guid shareId, ForkRequest? body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, AccessService access,
        ModelPolicy policy, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await FindAsync(db, access, me, shareId, ct) is not { } share)
        {
            return Results.NotFound();
        }
        var c = await db.Conversations.AsNoTracking().SingleAsync(x => x.Id == share.ConversationId, ct);
        var byId = await db.ChatMessages.AsNoTracking().Where(m => m.ConversationId == c.Id).ToDictionaryAsync(m => m.Id, ct);
        var visible = share.LeafId is { } leaf ? ChatService.PathTo(byId, leaf).ToDictionary(m => m.Id) : byId;
        if ((body?.MessageId ?? share.LeafId ?? c.CurrentLeafId) is not { } end || !visible.TryGetValue(end, out var last))
        {
            return AuthEndpoints.Problem(400, "message", visible.Count == 0 ? "This chat has nothing to fork yet." : "That message is not in this shared chat.");
        }
        if (ChatForks.Refusal(last) is { } why)
        {
            return AuthEndpoints.Problem(400, "message", why);
        }
        var model = c.Model is { } wanted && (await policy.AllowedAsync(me, [wanted], ct)).Contains(wanted) ? wanted : null;
        var fork = new Conversation { UserId = me.Id, Title = ChatForks.Title(c.Title), Thinking = c.Thinking, Tools = c.Tools is null ? null : [.. c.Tools], Model = model };
        var files = await ChatForks.CopyFilesAsync(db, ChatService.PathTo(visible, last.Id), me.Id, ct);
        ChatForks.Copy(db, fork, visible, last, files);
        db.Conversations.Add(fork);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/chat/conversations/{fork.Id}", new { fork.Id, fork.Title });
    }

    /// <summary>
    /// Whether someone may read a file that is not theirs: a file of an assistant they may use,
    /// or one in a chat shared with them, on what its link shows.
    /// </summary>
    public static async Task<bool> MayReadFileAsync(AppDbContext db, AccessService access, AppUser me, Guid attachmentId, CancellationToken ct)
    {
        var m = await access.MembershipAsync(me, ct);
        var assistants = await db.AssistantFiles.AsNoTracking().Where(f => f.AttachmentId == attachmentId)
            .Join(db.Assistants.AsNoTracking(), f => f.AssistantId, a => a.Id, (_, a) => a).ToListAsync(ct);
        if (assistants.Any(a => Assistants.MayUse(a, me.Id, m)))
        {
            return true;
        }
        var key = attachmentId.ToString();
        var shares = await db.ChatShares.AsNoTracking()
            .Where(s => db.ChatMessages.Any(x => x.ConversationId == s.ConversationId && x.AttachmentsJson != null && x.AttachmentsJson.Contains(key)))
            .ToListAsync(ct);
        foreach (var share in shares.Where(s => MayOpen(s, me.Id, m)))
        {
            if (share.LeafId is not { } leaf)
            {
                return true;
            }
            var byId = await db.ChatMessages.AsNoTracking().Where(x => x.ConversationId == share.ConversationId)
                .Select(x => new ChatMessage { Id = x.Id, ParentId = x.ParentId, Role = x.Role, AttachmentsJson = x.AttachmentsJson }).ToDictionaryAsync(x => x.Id, ct);
            if (ChatService.PathTo(byId, leaf).Any(x => ChatService.ParseIds(x.AttachmentsJson).Contains(attachmentId)))
            {
                return true;
            }
        }
        return false;
    }
}
