using Llm.Api.Endpoints;
using Llm.Api.Identity;
using Llm.Api.Ldap;

namespace Llm.Api.Settings;

public sealed record SettingsSave(List<SettingChange> Changes);

/// <summary>The Settings page: every setting, saved, a restart, and a directory test. Admins only.</summary>
public static class SettingsEndpoints
{
    public static void MapSettings(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin/config").RequireAuthorization(AdminEndpoints.Policy);

        g.MapGet("", async (SettingsService settings, CancellationToken ct) => Results.Ok(await settings.ViewAsync(ct)));

        g.MapPut("", async (SettingsSave body, SettingsService settings, IServiceProvider services, CancellationToken ct) =>
        {
            if (body.Changes is not { Count: > 0 })
            {
                return AuthEndpoints.Problem(400, "empty", "Nothing to save.");
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
