using System.Security.Claims;
using Llm.Api.Access;
using Llm.Api.Endpoints;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Chat.Tools.Browser;

/// <summary>The person's hand on the page: a click (where, as shares of the screen), text typed, a key, a scroll.</summary>
public sealed record BrowserInput(string Kind, double X = 0, double Y = 0, string? Text = null, string? Key = null, int Dy = 0);

/// <summary>
/// A chat's browser as its person sees it: whether it is open and where, its screen live, and taking it over (the agent's
/// calls wait) to click, type and scroll themselves (a password, a card number: theirs to type), then handing it back.
/// </summary>
public static class BrowserEndpoints
{
    public static void MapBrowser(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/chat/browser/{id:guid}").PersonOnly();
        g.MapGet("", StatusAsync);
        g.MapGet("/screen", ScreenAsync);
        g.MapPost("/take", TakeAsync);
        g.MapPost("/give", GiveAsync);
        g.MapPost("/input", InputAsync);
        g.MapDelete("", CloseAsync);
    }

    /// <summary>The chat's open browser, when the chat is the person's.</summary>
    private static async Task<BrowserSession?> MineAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, BrowserSessions sessions, CancellationToken ct)
    {
        var me = await users.GetUserAsync(p);
        return me is not null && await db.Conversations.AnyAsync(c => c.Id == id && c.UserId == me.Id, ct) && sessions.Find(id) is { } s && s.UserId == me.Id ? s : null;
    }

    private static async Task<IResult> StatusAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, BrowserSessions sessions, CancellationToken ct) =>
        await MineAsync(id, p, users, db, sessions, ct) is { } s
            ? Results.Ok(new { open = true, s.Url, s.Title, takenOver = s.TakenOver, width = BrowserSessions.Width, height = BrowserSessions.Height })
            : Results.Ok(new { open = false });

    private static async Task<IResult> ScreenAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, BrowserSessions sessions, CancellationToken ct)
    {
        if (await MineAsync(id, p, users, db, sessions, ct) is not { } s)
        {
            return Results.NotFound();
        }
        try
        {
            return Results.File(await sessions.ScreenAsync(s, ct), "image/jpeg");
        }
        catch (CdpException ex)
        {
            return AuthEndpoints.Problem(503, "browser", ex.Message);
        }
    }

    private static async Task<IResult> TakeAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, BrowserSessions sessions, Identity.Audit audit, CancellationToken ct)
    {
        if (await MineAsync(id, p, users, db, sessions, ct) is not { } s)
        {
            return Results.NotFound();
        }
        s.TakenOver = true;
        await audit.WriteAsync("browser.take", s.Url);
        return Results.NoContent();
    }

    private static async Task<IResult> GiveAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, BrowserSessions sessions, CancellationToken ct)
    {
        if (await MineAsync(id, p, users, db, sessions, ct) is not { } s)
        {
            return Results.NotFound();
        }
        s.TakenOver = false;
        return Results.NoContent();
    }

    private static async Task<IResult> InputAsync(Guid id, BrowserInput body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, BrowserSessions sessions, CancellationToken ct)
    {
        if (await MineAsync(id, p, users, db, sessions, ct) is not { } s)
        {
            return Results.NotFound();
        }
        if (!s.TakenOver)
        {
            return AuthEndpoints.Problem(409, "not_taken", "Take the browser over first.");
        }
        await s.Turn.WaitAsync(ct);
        try
        {
            switch (body.Kind)
            {
                case "click":
                    await sessions.ClickAsync(s, Math.Clamp(body.X, 0, 1) * BrowserSessions.Width, Math.Clamp(body.Y, 0, 1) * BrowserSessions.Height, ct);
                    break;
                case "type" when body.Text is { Length: > 0 and <= 2000 } text:
                    await sessions.TypeAsync(s, text, ct);
                    break;
                case "key" when body.Key is { } key && BrowserSessions.Keys.ContainsKey(key):
                    await sessions.KeyAsync(s, key, ct);
                    break;
                case "scroll":
                    await sessions.EvaluateAsync(s, $"window.scrollBy(0, {Math.Clamp(body.Dy, -5000, 5000)})", ct);
                    break;
                default:
                    return AuthEndpoints.Problem(400, "input", "A click (x, y), text, a key or a scroll.");
            }
            s.Shot = null;
            return Results.NoContent();
        }
        catch (CdpException ex)
        {
            return AuthEndpoints.Problem(503, "browser", ex.Message);
        }
        finally
        {
            s.Turn.Release();
        }
    }

    private static async Task<IResult> CloseAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, BrowserSessions sessions, CancellationToken ct)
    {
        if (await MineAsync(id, p, users, db, sessions, ct) is null)
        {
            return Results.NotFound();
        }
        await sessions.CloseAsync(id, ct);
        return Results.NoContent();
    }
}
