using System.Security.Claims;
using Llm.Api.Endpoints;
using Llm.Api.Identity;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;

namespace Llm.Api.Retention;

/// <summary>Legal hold on a person, or its end; a reason is needed to place it.</summary>
public sealed record LegalHoldRequest(bool Hold, string? Reason = null);

/// <summary>Legal hold and data exports: an admin's (eDiscovery), and each person's own copy.</summary>
public static class RetentionEndpoints
{
    public static void MapRetention(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin/people/{id:guid}").RequireAuthorization(AdminEndpoints.Policy);
        admin.MapPut("/legal-hold", HoldAsync);
        admin.MapGet("/export", AdminExportAsync);
        app.MapGet("/api/account/export", OwnExportAsync).RequireAuthorization();
        // How long the person's chats are kept, and what their files take of their room, for the account page.
        app.MapGet("/api/account/data", async (ClaimsPrincipal p, UserManager<AppUser> users, Retention retention, Storage.StorageQuotas quotas, CancellationToken ct) =>
        {
            var me = (await users.GetUserAsync(p))!;
            var (used, limit) = await quotas.OfAsync(me.Id, ct);
            return Results.Ok(new { retentionDays = await retention.DaysForAsync(me, ct), files = new { bytes = used, limitBytes = limit } });
        }).RequireAuthorization();
    }

    private static async Task<IResult> HoldAsync(Guid id, LegalHoldRequest body, PeopleService people, Retention retention, CancellationToken ct)
    {
        if (await people.FindAsync(id) is not { } user)
        {
            return Results.NotFound();
        }
        try
        {
            var erased = await retention.SetHoldAsync(user, body.Hold, body.Reason, ct);
            return Results.Ok(new { legalHoldSince = user.LegalHoldSince, legalHoldReason = user.LegalHoldReason, erasedChats = erased.Chats });
        }
        catch (PeopleException ex)
        {
            return AuthEndpoints.Problem(400, "reason", ex.Message);
        }
    }

    /// <summary>For eDiscovery: everything of the person, the chats they deleted under hold included. Audited.</summary>
    private static async Task AdminExportAsync(Guid id, HttpContext http, PeopleService people, DataExport export, Audit audit)
    {
        if (await people.FindAsync(id) is not { } user)
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        var (chats, files) = await export.CountAsync(user, hidden: true, http.RequestAborted);
        await audit.WriteAsync("person.export", user.UserName, detail: $"eDiscovery: {chats} chats, {files} files");
        await SendAsync(http, export, user, hidden: true);
    }

    /// <summary>A person's own copy of their data. Audited.</summary>
    private static async Task OwnExportAsync(HttpContext http, ClaimsPrincipal p, UserManager<AppUser> users, DataExport export, Audit audit)
    {
        var me = (await users.GetUserAsync(p))!;
        var (chats, files) = await export.CountAsync(me, hidden: false, http.RequestAborted);
        await audit.WriteAsync("account.export", me.UserName, detail: $"{chats} chats, {files} files");
        await SendAsync(http, export, me, hidden: false);
    }

    private static async Task SendAsync(HttpContext http, DataExport export, AppUser user, bool hidden)
    {
        http.Response.ContentType = "application/zip";
        http.Response.Headers.ContentDisposition = $"attachment; filename=\"{user.UserName}-data-{DateTime.UtcNow:yyyy-MM-dd}.zip\"";
        http.Response.Headers.CacheControl = "no-store";
        // Streamed as it is made (a person's files can be large): a zip entry's end is written synchronously.
        if (http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpBodyControlFeature>() is { } body)
        {
            body.AllowSynchronousIO = true;
        }
        await export.WriteAsync(user, hidden, http.Response.Body, http.RequestAborted);
    }
}
