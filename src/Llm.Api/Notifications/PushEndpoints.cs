using System.Security.Claims;
using Llm.Api.Endpoints;
using Llm.Api.Settings;
using Llm.Core.Chat;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Notifications;

/// <summary>The keys of a browser's push subscription, as PushSubscription.toJSON() gives them.</summary>
public sealed record PushKeys(string? P256dh, string? Auth);

/// <summary>A browser's push subscription (PushSubscription.toJSON()).</summary>
public sealed record PushSubscribe(string? Endpoint, PushKeys? Keys);

/// <summary>
/// The installable app: its web manifest (with the product's name), and each person's
/// devices that get the bell's news by push (Your account).
/// </summary>
public static class PushEndpoints
{
    public static void AddPush(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<PushOptions>(config.GetSection("Push"));
        services.AddSingleton<WebPush>();
        services.AddSingleton<PushOnSave>();
        services.AddHttpClient(WebPush.Client, c => c.Timeout = TimeSpan.FromSeconds(20));
    }

    public static void MapPush(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/app/manifest.webmanifest", Manifest).AllowAnonymous();
        var g = app.MapGroup("/api/push").RequireAuthorization();
        g.MapGet("", ListAsync);
        g.MapPost("/subscriptions", SubscribeAsync);
        g.MapDelete("/subscriptions/{id:guid}", RemoveAsync);
        g.MapPost("/test", TestAsync);
    }

    /// <summary>What makes the app installable: its name, colours and icons (the icons are the web's own files).</summary>
    private static IResult Manifest(HttpResponse response, IOptionsMonitor<BrandingOptions> branding)
    {
        var name = branding.CurrentValue.ProductName is { Length: > 0 } n ? n : AppInfo.Current.Name;
        response.Headers.CacheControl = "no-cache";
        return Results.Json(new
        {
            id = "/", name, short_name = name.Length <= 12 ? name : name.Split(' ')[0], description = branding.CurrentValue.SignInHeadline,
            start_url = "/", scope = "/", display = "standalone", background_color = "#070b1b", theme_color = "#141f45",
            icons = new object[]
            {
                new { src = "/icons/icon-192.png", sizes = "192x192", type = "image/png", purpose = "any" },
                new { src = "/icons/icon-512.png", sizes = "512x512", type = "image/png", purpose = "any" },
                new { src = "/icons/maskable-512.png", sizes = "512x512", type = "image/png", purpose = "maskable" },
                new { src = "/favicon.svg", sizes = "any", type = "image/svg+xml", purpose = "any" },
            },
        }, contentType: "application/manifest+json");
    }

    private static async Task<AppUser> Me(ClaimsPrincipal p, UserManager<AppUser> users) => (await users.GetUserAsync(p))!;

    private static object View(PushSubscription s) => new { s.Id, s.Device, s.CreatedAt, s.LastSentAt, s.LastError, s.Endpoint };

    private static async Task<IResult> ListAsync(ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, WebPush push, CancellationToken ct)
    {
        var me = await Me(p, users);
        var devices = await db.PushSubscriptions.AsNoTracking().Where(s => s.UserId == me.Id).OrderBy(s => s.CreatedAt).ToListAsync(ct);
        return Results.Ok(new { available = push.Available, publicKey = await push.PublicKeyAsync(ct), devices = devices.Select(View) });
    }

    private static async Task<IResult> SubscribeAsync(PushSubscribe body, HttpRequest request, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, WebPush push,
        IOptionsMonitor<PushOptions> options, TimeProvider clock, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (!push.Available)
        {
            return AuthEndpoints.Problem(409, "unavailable", "Push notifications are not available here: APP_DATA_KEY is not set.");
        }
        var endpoint = body.Endpoint?.Trim() ?? "";
        if (push.Refusal(endpoint) is { } refusal)
        {
            return AuthEndpoints.Problem(400, "endpoint", refusal);
        }
        if (WebPush.KeysRefusal(body.Keys?.P256dh, body.Keys?.Auth) is { } wrong)
        {
            return AuthEndpoints.Problem(400, "keys", wrong);
        }
        // The same browser again (or another person signed in on it): the subscription is renewed, and is theirs.
        var device = await db.PushSubscriptions.SingleOrDefaultAsync(s => s.Endpoint == endpoint, ct);
        if (device is null)
        {
            if (await db.PushSubscriptions.CountAsync(s => s.UserId == me.Id, ct) >= options.CurrentValue.PerPerson)
            {
                return AuthEndpoints.Problem(409, "limit", $"You have {options.CurrentValue.PerPerson} devices with push notifications, as many as one person may. Remove one first.");
            }
            device = new PushSubscription { Endpoint = endpoint, P256dh = "", Auth = "", CreatedAt = clock.GetUtcNow() };
            db.PushSubscriptions.Add(device);
        }
        device.UserId = me.Id;
        device.P256dh = body.Keys!.P256dh!.Trim();
        device.Auth = body.Keys.Auth!.Trim();
        device.Device = DeviceName(request.Headers.UserAgent.ToString());
        device.LastError = null;
        await db.SaveChangesAsync(ct);
        return Results.Ok(View(device));
    }

    private static async Task<IResult> RemoveAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, CancellationToken ct)
    {
        var me = await Me(p, users);
        return await db.PushSubscriptions.Where(s => s.Id == id && s.UserId == me.Id).ExecuteDeleteAsync(ct) > 0 ? Results.NoContent() : Results.NotFound();
    }

    /// <summary>A push to each of the person's devices now, to see that they get it.</summary>
    private static async Task<IResult> TestAsync(ClaimsPrincipal p, UserManager<AppUser> users, WebPush push, IOptionsMonitor<BrandingOptions> branding, CancellationToken ct)
    {
        var me = await Me(p, users);
        var (sent, failed) = await push.SendAsync(me.Id, new PushMessage($"{branding.CurrentValue.ProductName}: a test", "Push notifications reach this device.", "/account", "test", "test"), ct);
        return Results.Ok(new { sent, failed });
    }

    /// <summary>The browser and system a User-Agent names, as people call them: "Chrome on Android".</summary>
    public static string DeviceName(string userAgent)
    {
        bool Has(string s) => userAgent.Contains(s, StringComparison.OrdinalIgnoreCase);
        var browser = Has("Edg/") ? "Edge" : Has("OPR/") ? "Opera" : Has("Firefox/") ? "Firefox" : Has("Chrome/") ? "Chrome" : Has("Safari/") ? "Safari" : "A browser";
        var system = Has("Android") ? "Android" : Has("iPhone") || Has("iPad") ? "iOS" : Has("Windows") ? "Windows" : Has("Mac OS X") ? "macOS" : Has("CrOS") ? "ChromeOS"
            : Has("Linux") ? "Linux" : null;
        return system is null ? browser : $"{browser} on {system}";
    }
}
