using System.Security.Claims;
using Llm.Api.Identity;
using Llm.Core.Compliance;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Llm.Api.Compliance;

/// <summary>Configuration section "Notice": what everyone accepts before using the service (acceptable use, the privacy notice).</summary>
public sealed class NoticeOptions
{
    public string Title { get; set; } = "Before you use this service";

    /// <summary>Markdown; empty: no notice.</summary>
    public string? Text { get; set; }

    /// <summary>Changed when the text changes in substance: everyone accepts the new version at their next request.</summary>
    public string Version { get; set; } = "1";

    public bool On => !string.IsNullOrWhiteSpace(Text);
}

/// <summary>
/// The sign-in notice: shown on the sign-in page, and accepted by each person in its current version before anything else (their
/// sessions answer nothing but it until they have, whichever way they signed in). Each acceptance is kept and audited.
/// </summary>
public sealed class Notice(AppDbContext db, IOptionsMonitor<NoticeOptions> options, IMemoryCache cache, Audit audit, TimeProvider clock)
{
    private static string Key(Guid user, string version) => $"notice:{user}:{version}";

    public NoticeOptions Current => options.CurrentValue;

    /// <summary>Whether this person still has to accept the current notice (remembered a minute, forgotten when they accept).</summary>
    public async Task<bool> MustAcceptAsync(Guid userId, CancellationToken ct = default)
    {
        var o = options.CurrentValue;
        if (!o.On)
        {
            return false;
        }
        if (!cache.TryGetValue(Key(userId, o.Version), out bool accepted))
        {
            accepted = await db.NoticeAcceptances.AnyAsync(a => a.UserId == userId && a.Version == o.Version, ct);
            cache.Set(Key(userId, o.Version), accepted, TimeSpan.FromMinutes(1));
        }
        return !accepted;
    }

    public async Task<NoticeAcceptance?> AcceptedAsync(Guid userId, CancellationToken ct) =>
        await db.NoticeAcceptances.AsNoTracking().Where(a => a.UserId == userId).OrderByDescending(a => a.At).FirstOrDefaultAsync(ct);

    /// <summary>Accepts the version shown; refused when it is no longer the current one (the page shows the new one).</summary>
    public async Task<bool> AcceptAsync(Guid userId, string userName, string version, string? ip, CancellationToken ct)
    {
        var o = options.CurrentValue;
        if (!o.On || version != o.Version)
        {
            return false;
        }
        if (!await db.NoticeAcceptances.AnyAsync(a => a.UserId == userId && a.Version == version, ct))
        {
            db.NoticeAcceptances.Add(new NoticeAcceptance { UserId = userId, Version = version, At = clock.GetUtcNow(), Ip = ip });
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync("notice.accept", userName, detail: $"version {version}");
        }
        cache.Set(Key(userId, version), true, TimeSpan.FromMinutes(1));
        return true;
    }
}

public static class NoticeGate
{
    /// <summary>
    /// A session whose person has not accepted the current notice reaches only who is signed in, signing in and out, the notice,
    /// setting up two-factor sign-in and the app's info: the rest answers 403 notice. Keys are not sessions: they pass.
    /// </summary>
    public static IApplicationBuilder UseNoticeGate(this IApplicationBuilder app) =>
        app.Use(async (ctx, next) =>
        {
            var path = ctx.Request.Path;
            if (ctx.User.Identity is { IsAuthenticated: true, AuthenticationType: var scheme } && scheme == Microsoft.AspNetCore.Identity.IdentityConstants.ApplicationScheme
                && (path.StartsWithSegments("/api") || path.StartsWithSegments("/connect"))
                && !path.StartsWithSegments("/api/auth") && !path.StartsWithSegments("/api/account/notice") && !path.StartsWithSegments("/api/account/2fa")
                && !path.StartsWithSegments("/api/info")
                && Guid.TryParse(ctx.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
                && await ctx.RequestServices.GetRequiredService<Notice>().MustAcceptAsync(id, ctx.RequestAborted))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                await ctx.Response.WriteAsJsonAsync(new { status = "notice", error = "Read and accept the notice first." });
                return;
            }
            await next(ctx);
        });
}
