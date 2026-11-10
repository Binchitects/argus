using System.Security.Claims;
using Llm.Api.Access;
using Llm.Api.Endpoints;
using Llm.Api.Identity;
using Llm.Core.Data;
using Llm.Core.Identity;
using Llm.Core.Notifications;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Notifications;

/// <summary>A broadcast sent: its words, how loud, to whom (no groups: everyone), and until when (null: until ended).</summary>
public sealed record BroadcastRequest(string? Title, string? Body, string? Level = null, Guid[]? Groups = null, DateTimeOffset? Until = null);

/// <summary>
/// Broadcasts: an admin's message to everyone or to some groups (broadcasts.send). Each person it is for gets it in their
/// bell (and their phone), above every page of the Arena and in Code Arena (with their key), until they dismiss it, it ends
/// by itself, or an admin ends it. A critical one is not dismissed: it shows while it lasts.
/// </summary>
public static class BroadcastEndpoints
{
    public const int MaxTitle = 200;
    public const int MaxBody = 4000;

    public static void MapBroadcasts(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin/broadcasts").RequirePermissions(Core.Access.Permissions.BroadcastsSend, Core.Access.Permissions.BroadcastsSend);
        admin.MapGet("", ListAsync);
        admin.MapPost("", SendAsync);
        admin.MapPost("/{id:guid}/end", EndAsync);

        var mine = app.MapGroup("/api/broadcasts").PersonOnly();
        mine.MapGet("", async (ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, AccessService access, TimeProvider clock, CancellationToken ct) =>
            Results.Ok(await ActiveForAsync((await users.GetUserAsync(p))!, db, access, clock, ct)));
        mine.MapPost("/{id:guid}/dismiss", async (Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, AccessService access, TimeProvider clock, CancellationToken ct) =>
            await DismissAsync((await users.GetUserAsync(p))!, id, db, access, clock, ct));
    }

    /// <summary>Whether it is on now: not ended, and not past its end.</summary>
    private static bool On(Broadcast b, DateTimeOffset now) => b.EndedAt is null && (b.Until is null || b.Until > now);

    /// <summary>The broadcasts on now that are for this person (everyone's, or one of their groups'), that they have not dismissed.</summary>
    public static async Task<List<object>> ActiveForAsync(AppUser me, AppDbContext db, AccessService access, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var groups = (await access.MembershipAsync(me, ct)).Groups;
        var dismissed = await db.BroadcastDismissals.AsNoTracking().Where(d => d.UserId == me.Id).Select(d => d.BroadcastId).ToListAsync(ct);
        var on = await db.Broadcasts.AsNoTracking().Where(b => b.EndedAt == null && (b.Until == null || b.Until > now)).OrderByDescending(b => b.CreatedAt).Take(50).ToListAsync(ct);
        return [.. on.Where(b => (b.Groups.Length == 0 || b.Groups.Any(groups.Contains)) && (b.Level == Broadcast.Critical || !dismissed.Contains(b.Id)))
            .Select(b => (object)new { b.Id, b.Title, b.Body, b.Level, b.CreatedAt, b.Until, dismissable = b.Level != Broadcast.Critical })];
    }

    /// <summary>One of theirs dismissed: it shows no more for them (a critical one stays while it lasts).</summary>
    public static async Task<IResult> DismissAsync(AppUser me, Guid id, AppDbContext db, AccessService access, TimeProvider clock, CancellationToken ct)
    {
        if (await db.Broadcasts.AsNoTracking().SingleOrDefaultAsync(b => b.Id == id, ct) is not { } b)
        {
            return Results.NotFound();
        }
        var groups = (await access.MembershipAsync(me, ct)).Groups;
        if (b.Groups.Length > 0 && !b.Groups.Any(groups.Contains))
        {
            return Results.NotFound();
        }
        if (b.Level == Broadcast.Critical && On(b, clock.GetUtcNow()))
        {
            return AuthEndpoints.Problem(409, "critical", "A critical message shows while it lasts.");
        }
        if (!await db.BroadcastDismissals.AnyAsync(d => d.BroadcastId == id && d.UserId == me.Id, ct))
        {
            db.BroadcastDismissals.Add(new BroadcastDismissal { BroadcastId = id, UserId = me.Id, At = clock.GetUtcNow() });
            await db.SaveChangesAsync(ct);
        }
        return Results.NoContent();
    }

    private static async Task<IResult> ListAsync(AppDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var list = await db.Broadcasts.AsNoTracking().OrderByDescending(b => b.CreatedAt).Take(200).ToListAsync(ct);
        var ids = list.Select(b => b.Id).ToList();
        var dismissed = await db.BroadcastDismissals.AsNoTracking().Where(d => ids.Contains(d.BroadcastId)).GroupBy(d => d.BroadcastId)
            .Select(g => new { g.Key, n = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.n, ct);
        var people = list.Where(b => b.CreatedBy is not null).Select(b => b.CreatedBy!.Value).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => people.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName ?? u.UserName, ct);
        var groupIds = list.SelectMany(b => b.Groups).Distinct().ToList();
        var groupNames = await db.Groups.AsNoTracking().Where(g => groupIds.Contains(g.Id)).ToDictionaryAsync(g => g.Id, g => g.Name, ct);
        return Results.Ok(list.Select(b => new
        {
            b.Id, b.Title, b.Body, b.Level, b.CreatedAt, b.Until, b.EndedAt, b.Sent, on = On(b, now),
            groups = b.Groups.Select(g => new { id = g, name = groupNames.GetValueOrDefault(g) ?? "(a deleted group)" }),
            by = b.CreatedBy is { } c ? names.GetValueOrDefault(c) : null, dismissed = dismissed.GetValueOrDefault(b.Id),
        }));
    }

    private static async Task<IResult> SendAsync(BroadcastRequest body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, Gateway.Credit credit,
        Notifier notifier, Audit audit, TimeProvider clock, CancellationToken ct)
    {
        var title = body.Title?.Trim();
        if (title is not { Length: > 0 })
        {
            return AuthEndpoints.Problem(400, "title", "Give it a title.");
        }
        if (title.Length > MaxTitle || body.Body?.Length > MaxBody)
        {
            return AuthEndpoints.Problem(400, "length", $"A title of at most {MaxTitle} characters, and words of at most {MaxBody:N0}.");
        }
        var level = body.Level?.Trim().ToLowerInvariant() is { Length: > 0 } l ? l : Broadcast.Info;
        if (!Broadcast.Levels.Contains(level))
        {
            return AuthEndpoints.Problem(400, "level", $"It is {string.Join(", ", Broadcast.Levels)}.");
        }
        var now = clock.GetUtcNow();
        if (body.Until is { } until && until <= now)
        {
            return AuthEndpoints.Problem(400, "until", "It would end before it starts.");
        }
        var groupIds = (body.Groups ?? []).Distinct().ToArray();
        var groups = await db.Groups.AsNoTracking().Where(g => groupIds.Contains(g.Id)).ToListAsync(ct);
        if (groups.Count != groupIds.Length)
        {
            return AuthEndpoints.Problem(400, "groups", "One of the groups is not there any more.");
        }
        // Who it is for: everyone who may sign in, or the groups' members.
        List<Guid> to;
        if (groups.Count == 0)
        {
            to = await db.Users.AsNoTracking().Where(u => !u.IsDisabled).Select(u => u.Id).ToListAsync(ct);
        }
        else
        {
            var emails = new HashSet<string>(StringComparer.Ordinal);
            foreach (var g in groups)
            {
                emails.UnionWith(await credit.MembersAsync(g, ct));
            }
            var upper = emails.Select(e => e.ToUpperInvariant()).ToList();
            to = await db.Users.AsNoTracking().Where(u => !u.IsDisabled && u.NormalizedEmail != null && upper.Contains(u.NormalizedEmail!)).Select(u => u.Id).ToListAsync(ct);
        }
        var me = (await users.GetUserAsync(p))!;
        var b = new Broadcast { Title = title, Body = body.Body?.Trim() is { Length: > 0 } words ? words : null, Level = level, Groups = groupIds, CreatedBy = me.Id, CreatedAt = now, Until = body.Until, Sent = to.Count };
        db.Broadcasts.Add(b);
        await db.SaveChangesAsync(ct);
        await notifier.SendManyAsync(to, new News("broadcast", title, b.Body, null, $"broadcast:{b.Id}"), ct);
        await audit.WriteAsync("broadcast.sent", title, detail: $"{level}, to {(groups.Count == 0 ? "everyone" : string.Join(", ", groups.Select(g => g.Name)))} ({to.Count})", actor: me);
        return Results.Created($"/api/admin/broadcasts/{b.Id}", new { b.Id, b.Sent });
    }

    private static async Task<IResult> EndAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, Audit audit, TimeProvider clock, CancellationToken ct)
    {
        if (await db.Broadcasts.SingleOrDefaultAsync(b => b.Id == id, ct) is not { } b)
        {
            return Results.NotFound();
        }
        b.EndedAt ??= clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("broadcast.ended", b.Title, actor: await users.GetUserAsync(p));
        return Results.NoContent();
    }
}
