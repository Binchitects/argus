using Llm.Api.Endpoints;
using Llm.Api.Identity;
using Llm.Api.Ldap;
using Microsoft.Extensions.Options;

namespace Llm.Api.Settings;

public sealed record SettingsSave(List<SettingChange> Changes);

/// <summary>The Settings page: every setting, saved (live or pending), a restart, and a directory test. Admins only.</summary>
public static class SettingsEndpoints
{
    public static void MapSettings(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin/config").RequireAuthorization(AdminEndpoints.Policy);

        g.MapGet("", async (SettingsService settings, CancellationToken ct) => Results.Ok(await settings.ViewAsync(ct)));

        g.MapPut("", async (SettingsSave body, SettingsService settings, CancellationToken ct) =>
        {
            if (body.Changes is not { Count: > 0 })
            {
                return AuthEndpoints.Problem(400, "empty", "Nothing to save.");
            }
            try
            {
                await settings.SaveAsync(body.Changes, ct);
                return Results.Ok(await settings.ViewAsync(ct));
            }
            catch (SettingsValidationException ex)
            {
                return Results.Json(new { status = "invalid", error = "Some settings are not valid.", errors = ex.Errors }, statusCode: 400);
            }
            catch (SettingsUnavailableException ex)
            {
                return AuthEndpoints.Problem(503, "unavailable", ex.Message);
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

        // Tries directory settings before they are saved: the form's values over the current ones.
        g.MapPost("/ldap-test", async (Dictionary<string, string?> form, IOptionsMonitor<LdapOptions> current, CancellationToken ct) =>
        {
            var c = current.CurrentValue;
            string? F(string name, string? fallback) => form.TryGetValue(name, out var v) ? v : fallback;
            var o = new LdapOptions
            {
                Url = F("Ldap:Url", c.Url)?.Trim(),
                StartTls = bool.TryParse(F("Ldap:StartTls", c.StartTls.ToString()), out var tls) && tls,
                IgnoreCertificateErrors = bool.TryParse(F("Ldap:IgnoreCertificateErrors", c.IgnoreCertificateErrors.ToString()), out var ignore) && ignore,
                BindDn = F("Ldap:BindDn", c.BindDn)?.Trim(),
                // A blank password field means "keep the saved one".
                BindPassword = string.IsNullOrEmpty(F("Ldap:BindPassword", null)) ? c.BindPassword : form["Ldap:BindPassword"],
                UserBaseDn = F("Ldap:UserBaseDn", c.UserBaseDn)?.Trim() ?? "",
                UserFilter = c.UserFilter,
            };
            return Results.Ok(await LdapDirectory.TestAsync(o, ct));
        });
    }
}
