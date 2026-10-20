using Llm.Api.Endpoints;
using Llm.Api.Access;
using Llm.Api.Identity;
using Llm.Api.Ldap;

namespace Llm.Api.Settings;

public sealed record SettingsSave(List<SettingChange> Changes);

/// <summary>The Settings page: every setting, saved, a restart, and a directory test. Admins only.</summary>
public static class SettingsEndpoints
{
    public static void MapSettings(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin/config").RequirePermissions(Llm.Core.Access.Permissions.SettingsRead, Llm.Core.Access.Permissions.SettingsManage,
            new Dictionary<string, string>
            {
                // Each change is checked against its own group's permission (below): reading them is the least to come in with.
                ["PUT /api/admin/config"] = Llm.Core.Access.Permissions.SettingsRead,
                ["POST /api/admin/config/restart"] = Llm.Core.Access.Permissions.SettingsRestart,
            });

        g.MapGet("", async (SettingsService settings, CancellationToken ct) => Results.Ok(await settings.ViewAsync(ct)));

        g.MapPut("", async (SettingsSave body, SettingsService settings, IServiceProvider services, HttpContext http, CancellationToken ct) =>
        {
            if (body.Changes is not { Count: > 0 })
            {
                return AuthEndpoints.Problem(400, "empty", "Nothing to save.");
            }
            // Each setting by its own group's permission (Admin → Access: settings.chat, settings.safeguards...).
            var me = await services.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Llm.Core.Identity.AppUser>>().GetUserAsync(http.User);
            var member = await services.GetRequiredService<Access.AccessService>().MembershipAsync(me!, ct);
            var refused = body.Changes.Select(c => SettingsCatalog.ByKey.GetValueOrDefault(c.Key)).OfType<SettingDefinition>()
                .Where(d => !member.Has(SettingsCatalog.PermissionOf(d))).Select(d => d.Label).Distinct().ToList();
            if (refused.Count > 0)
            {
                return AuthEndpoints.Problem(403, "settings", $"You may not change: {string.Join(", ", refused)}.");
            }
            try
            {
                await settings.SaveAsync(body.Changes, ct);
                if (body.Changes.Any(c => c.Key.StartsWith("Prices:", StringComparison.Ordinal)))
                {
                    await Gateway.PriceBook.RegisterAsync(services, ct);
                }
                return Results.Ok(await settings.ViewAsync(ct));
            }
            catch (SettingsValidationException ex)
            {
                return Results.Json(new { status = "invalid", error = "Some settings are not valid.", errors = ex.Errors }, statusCode: 400);
            }
        });

        // Settings read at start (session lifetimes): the app stops, and the container's
        // restart policy starts it again with them. No Docker socket involved.
        g.MapPost("/restart", async (IAppRestarter restarter, Audit audit) =>
        {
            await audit.WriteAsync("settings.restart", detail: "to apply settings read at start");
            restarter.Restart();
            return Results.Accepted(value: new { status = "restarting" });
        });

        // Tries directory settings before they are saved (the form's values over the saved ones), and a person's sign-in.
        g.MapDirectoryChecks();
    }
}
