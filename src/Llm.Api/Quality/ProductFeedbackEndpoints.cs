using System.Security.Claims;
using Llm.Api.Access;
using Llm.Api.Endpoints;
using Llm.Api.Identity;
using Llm.Api.Notifications;
using Llm.Core.Chat;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Quality;

/// <summary>What a person sends about Arena: idea, problem, praise or other; its words; the page it was sent from.</summary>
public sealed record ProductFeedbackRequest(string? Kind, string? Text, string? Page);

/// <summary>An admin marks it (new, read, done), answers it, or both.</summary>
public sealed record ProductFeedbackChange(string? Status = null, string? Answer = null);

/// <summary>
/// Send feedback (anywhere in Arena, and Code Arena with the person's key): what people say about Arena itself, kept and
/// listed on Admin → Feedback for those with feedback.manage, who mark it and answer it; the answer reaches the person's
/// bell. A person sends at most <see cref="PerDay"/> a day.
/// </summary>
public static class ProductFeedbackEndpoints
{
    public const int MaxText = 4000;
    public const int PerDay = 20;

    public static void MapProductFeedback(this IEndpointRouteBuilder app)
    {
        var mine = app.MapGroup("/api/feedback").PersonOnly();
        mine.MapPost("", async (ProductFeedbackRequest body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, Notifier notifier, Audit audit, TimeProvider clock) =>
            await SendAsync((await users.GetUserAsync(p))!, body, db, notifier, audit, clock));
        mine.MapGet("/mine", async (ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db) =>
        {
            var me = (await users.GetUserAsync(p))!;
            return Results.Ok(await db.ProductFeedback.AsNoTracking().Where(f => f.UserId == me.Id).OrderByDescending(f => f.CreatedAt).Take(100)
                .Select(f => new { f.Id, f.Kind, f.Text, f.Page, f.Status, f.Answer, f.AnsweredAt, f.CreatedAt }).ToListAsync());
        });

        var admin = app.MapGroup("/api/admin/feedback").RequirePermissions(Llm.Core.Access.Permissions.FeedbackManage, Llm.Core.Access.Permissions.FeedbackManage);
        admin.MapGet("", ListAsync);
        admin.MapPut("/{id:guid}", ChangeAsync);
    }

    /// <summary>Kept, and the admins told; refused past <see cref="PerDay"/> a day, or without words or a kind.</summary>
    public static async Task<IResult> SendAsync(AppUser me, ProductFeedbackRequest body, AppDbContext db, Notifier notifier, Audit audit, TimeProvider clock)
    {
        var kind = body.Kind?.Trim().ToLowerInvariant();
        if (kind is null || !ProductFeedback.Kinds.Contains(kind))
        {
            return AuthEndpoints.Problem(400, "kind", $"Say what it is: {string.Join(", ", ProductFeedback.Kinds)}.");
        }
        if (body.Text?.Trim() is not { Length: > 0 } text)
        {
            return AuthEndpoints.Problem(400, "text", "Write what you want to say.");
        }
        if (text.Length > MaxText)
        {
            return AuthEndpoints.Problem(400, "text", $"Say it in at most {MaxText:N0} characters.");
        }
        var since = clock.GetUtcNow().AddDays(-1);
        if (await db.ProductFeedback.CountAsync(f => f.UserId == me.Id && f.CreatedAt >= since) >= PerDay)
        {
            return AuthEndpoints.Problem(429, "too_many", $"You sent {PerDay} today, the most there may be: send more tomorrow.");
        }
        var page = body.Page?.Trim() is { Length: > 0 } pg ? pg[..Math.Min(500, pg.Length)] : null;
        var f = new ProductFeedback { UserId = me.Id, Kind = kind, Text = text, Page = page, CreatedAt = clock.GetUtcNow() };
        db.ProductFeedback.Add(f);
        await db.SaveChangesAsync();
        await audit.WriteAsync("feedback.sent", me.UserName, detail: $"{kind}{(page is null ? "" : $" from {page}")}", actor: me);
        await notifier.ToAdminsAsync(new News("feedback", $"Feedback from {me.DisplayName ?? me.UserName}: {kind}", text.Length > 200 ? text[..199] + "…" : text, "/admin/feedback"));
        return Results.Created($"/api/feedback/{f.Id}", new { f.Id, f.Kind, f.Text, f.Page, f.Status, f.CreatedAt });
    }

    private static async Task<IResult> ListAsync(string? status, AppDbContext db)
    {
        var rows = db.ProductFeedback.AsNoTracking();
        if (status is { Length: > 0 } s && s != "all")
        {
            rows = rows.Where(f => f.Status == s);
        }
        var list = await rows.OrderByDescending(f => f.CreatedAt).Take(500).ToListAsync();
        var ids = list.Select(f => f.UserId).Concat(list.Where(f => f.AnsweredBy != null).Select(f => f.AnsweredBy!.Value)).Distinct().ToList();
        var people = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => new { u.Id, u.UserName, u.DisplayName });
        var counts = await db.ProductFeedback.AsNoTracking().GroupBy(f => f.Status).Select(g => new { g.Key, n = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.n);
        return Results.Ok(new
        {
            items = list.Select(f => new
            {
                f.Id, f.Kind, f.Text, f.Page, f.Status, f.Answer, f.AnsweredAt, f.CreatedAt,
                person = people.GetValueOrDefault(f.UserId), answeredBy = f.AnsweredBy is { } a ? people.GetValueOrDefault(a) : null,
            }),
            counts = ProductFeedback.Statuses.ToDictionary(x => x, x => counts.GetValueOrDefault(x)),
        });
    }

    private static async Task<IResult> ChangeAsync(Guid id, ProductFeedbackChange body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, Notifier notifier, Audit audit, TimeProvider clock)
    {
        var f = await db.ProductFeedback.SingleOrDefaultAsync(x => x.Id == id);
        if (f is null)
        {
            return Results.NotFound();
        }
        if (body.Status is { } status && !ProductFeedback.Statuses.Contains(status))
        {
            return AuthEndpoints.Problem(400, "status", $"It is {string.Join(", ", ProductFeedback.Statuses)}.");
        }
        var answer = body.Answer?.Trim();
        if (answer is { Length: > MaxText })
        {
            return AuthEndpoints.Problem(400, "answer", $"Answer in at most {MaxText:N0} characters.");
        }
        var me = (await users.GetUserAsync(p))!;
        if (body.Status is { } st)
        {
            f.Status = st;
        }
        if (answer is { Length: > 0 })
        {
            (f.Answer, f.AnsweredBy, f.AnsweredAt) = (answer, me.Id, clock.GetUtcNow());
            // Answered is read at least.
            if (f.Status == ProductFeedback.New)
            {
                f.Status = ProductFeedback.Read;
            }
            await notifier.SendAsync(f.UserId, new News("feedback", "Your feedback was answered", answer.Length > 300 ? answer[..299] + "…" : answer, "/account#feedback", $"feedback:{f.Id}:{f.AnsweredAt:O}"));
        }
        await db.SaveChangesAsync();
        await audit.WriteAsync("feedback.handled", f.Id.ToString(), detail: $"{f.Status}{(answer is { Length: > 0 } ? ", answered" : "")}");
        return Results.Ok(new { f.Id, f.Status, f.Answer, f.AnsweredAt });
    }
}
