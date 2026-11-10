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

/// <summary>What a person sends about Arena: idea, problem, praise or other; its words; the page it was sent from; screenshots.</summary>
public sealed record ProductFeedbackRequest(string? Kind, string? Text, string? Page, List<ProductFeedbackScreenshot>? Images = null);

/// <summary>A screenshot: its bytes in base64 (its type is read from them).</summary>
public sealed record ProductFeedbackScreenshot(string? Data);

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
    /// <summary>Screenshots with one feedback, and the largest each may be.</summary>
    public const int MaxImages = 3;
    public const int MaxImageBytes = 2 * 1024 * 1024;

    public static void MapProductFeedback(this IEndpointRouteBuilder app)
    {
        var mine = app.MapGroup("/api/feedback").PersonOnly();
        mine.MapPost("", async (ProductFeedbackRequest body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, Notifier notifier, Audit audit, TimeProvider clock) =>
            await SendAsync((await users.GetUserAsync(p))!, body, db, notifier, audit, clock));
        mine.MapGet("/mine", async (ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db) =>
        {
            var me = (await users.GetUserAsync(p))!;
            var list = await db.ProductFeedback.AsNoTracking().Where(f => f.UserId == me.Id).OrderByDescending(f => f.CreatedAt).Take(100).ToListAsync();
            var images = await ImageCountsAsync(db, list.Select(f => f.Id).ToList());
            return Results.Ok(list.Select(f => new { f.Id, f.Kind, f.Text, f.Page, f.Status, f.Answer, f.AnsweredAt, f.CreatedAt, images = images.GetValueOrDefault(f.Id) }));
        });
        // A screenshot of one's own feedback.
        mine.MapGet("/{id:guid}/images/{n:int}", async (Guid id, int n, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, HttpContext http) =>
        {
            var me = (await users.GetUserAsync(p))!;
            return await db.ProductFeedback.AnyAsync(f => f.Id == id && f.UserId == me.Id) ? await ImageAsync(id, n, db, http) : Results.NotFound();
        });

        var admin = app.MapGroup("/api/admin/feedback").RequirePermissions(Llm.Core.Access.Permissions.FeedbackManage, Llm.Core.Access.Permissions.FeedbackManage);
        admin.MapGet("", ListAsync);
        admin.MapPut("/{id:guid}", ChangeAsync);
        admin.MapGet("/{id:guid}/images/{n:int}", (Guid id, int n, AppDbContext db, HttpContext http) => ImageAsync(id, n, db, http));
    }

    /// <summary>How many screenshots each of these has.</summary>
    private static async Task<Dictionary<Guid, int>> ImageCountsAsync(AppDbContext db, List<Guid> ids) =>
        ids.Count == 0 ? [] : await db.ProductFeedbackImages.Where(i => ids.Contains(i.FeedbackId)).GroupBy(i => i.FeedbackId)
            .Select(g => new { g.Key, n = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.n);

    /// <summary>
    /// A screenshot, as the picture it is (its type read from its bytes when it came), never run: no sniffing, a sandbox, kept
    /// only by the browser that asked.
    /// </summary>
    private static async Task<IResult> ImageAsync(Guid id, int n, AppDbContext db, HttpContext http)
    {
        if (await db.ProductFeedbackImages.AsNoTracking().FirstOrDefaultAsync(i => i.FeedbackId == id && i.Position == n) is not { } image)
        {
            return Results.NotFound();
        }
        http.Response.Headers.CacheControl = "private, max-age=3600";
        http.Response.Headers["X-Content-Type-Options"] = "nosniff";
        http.Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
        return Results.File(image.Data, image.ContentType);
    }

    /// <summary>The screenshots sent, read: each base64 of a PNG, JPEG, GIF or WebP no larger than <see cref="MaxImageBytes"/>.</summary>
    public static (List<(string Type, byte[] Data)> Images, IResult? Refusal) ReadImages(List<ProductFeedbackScreenshot>? sent)
    {
        var images = new List<(string, byte[])>();
        if (sent is not { Count: > 0 })
        {
            return (images, null);
        }
        if (sent.Count > MaxImages)
        {
            return (images, AuthEndpoints.Problem(400, "images", $"Send at most {MaxImages} screenshots."));
        }
        foreach (var image in sent)
        {
            byte[] data;
            try
            {
                data = Convert.FromBase64String(image.Data ?? "");
            }
            catch (FormatException)
            {
                return (images, AuthEndpoints.Problem(400, "images", "A screenshot is its bytes in base64."));
            }
            if (data.Length > MaxImageBytes)
            {
                return (images, AuthEndpoints.Problem(413, "images", $"A screenshot may be at most {MaxImageBytes / 1024 / 1024} MB."));
            }
            if (Chat.Attachments.ImageType("", data) is not { } type)
            {
                return (images, AuthEndpoints.Problem(400, "images", "A screenshot is a PNG, JPEG, GIF or WebP picture."));
            }
            images.Add((type, data));
        }
        return (images, null);
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
        var (images, refused) = ReadImages(body.Images);
        if (refused is not null)
        {
            return refused;
        }
        var since = clock.GetUtcNow().AddDays(-1);
        if (await db.ProductFeedback.CountAsync(f => f.UserId == me.Id && f.CreatedAt >= since) >= PerDay)
        {
            return AuthEndpoints.Problem(429, "too_many", $"You sent {PerDay} today, the most there may be: send more tomorrow.");
        }
        var page = body.Page?.Trim() is { Length: > 0 } pg ? pg[..Math.Min(500, pg.Length)] : null;
        var f = new ProductFeedback { UserId = me.Id, Kind = kind, Text = text, Page = page, CreatedAt = clock.GetUtcNow() };
        db.ProductFeedback.Add(f);
        db.ProductFeedbackImages.AddRange(images.Select((x, n) => new ProductFeedbackImage { FeedbackId = f.Id, Position = n, ContentType = x.Type, Data = x.Data }));
        await db.SaveChangesAsync();
        await audit.WriteAsync("feedback.sent", me.UserName, detail: $"{kind}{(page is null ? "" : $" from {page}")}{(images.Count > 0 ? $", {images.Count} screenshot(s)" : "")}", actor: me);
        await notifier.ToAdminsAsync(new News("feedback", $"Feedback from {me.DisplayName ?? me.UserName}: {kind}", text.Length > 200 ? text[..199] + "…" : text, "/admin/feedback"));
        return Results.Created($"/api/feedback/{f.Id}", new { f.Id, f.Kind, f.Text, f.Page, f.Status, f.CreatedAt, images = images.Count });
    }

    /// <summary>What was sent, the newest first, by status; from and to: only what was sent in that window (the counts too).</summary>
    private static async Task<IResult> ListAsync(string? status, DateTimeOffset? from, DateTimeOffset? to, AppDbContext db)
    {
        if (from >= to)
        {
            return AuthEndpoints.Problem(400, "range", "The range must start before it ends.");
        }
        var sent = db.ProductFeedback.AsNoTracking().Where(f => (from == null || f.CreatedAt >= from) && (to == null || f.CreatedAt < to));
        var rows = sent;
        if (status is { Length: > 0 } s && s != "all")
        {
            rows = rows.Where(f => f.Status == s);
        }
        var list = await rows.OrderByDescending(f => f.CreatedAt).Take(500).ToListAsync();
        var ids = list.Select(f => f.UserId).Concat(list.Where(f => f.AnsweredBy != null).Select(f => f.AnsweredBy!.Value)).Distinct().ToList();
        var people = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => new { u.Id, u.UserName, u.DisplayName });
        var images = await ImageCountsAsync(db, list.Select(f => f.Id).ToList());
        var counts = await sent.GroupBy(f => f.Status).Select(g => new { g.Key, n = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.n);
        return Results.Ok(new
        {
            items = list.Select(f => new
            {
                f.Id, f.Kind, f.Text, f.Page, f.Status, f.Answer, f.AnsweredAt, f.CreatedAt, images = images.GetValueOrDefault(f.Id),
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
