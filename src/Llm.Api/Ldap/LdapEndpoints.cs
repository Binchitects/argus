using Llm.Api.Identity;
using Llm.Api.Settings;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Ldap;

/// <summary>A person's sign-in to try: the form's directory settings (as for the test), their username and their password. Nothing of it is kept.</summary>
public sealed record LdapTry(Dictionary<string, string?>? Settings, string? Login, string? Password);

/// <summary>The Settings page's directory checks, with the form's values over the saved ones: Test, and a person's sign-in tried. Admins only.</summary>
public static class LdapEndpoints
{
    public static void MapDirectoryChecks(this RouteGroupBuilder g)
    {
        // Audited with the server it went to: the saved password goes only to the saved server and account, as safely as saved.
        g.MapPost("/ldap-test", async (Dictionary<string, string?> form, IOptionsMonitor<LdapOptions> current, AppDbContext db, Audit audit, CancellationToken ct) =>
        {
            var (o, from, withheld, problem, notes) = await FromFormAsync(form, current.CurrentValue, db, ct);
            var result = problem ?? await LdapCheck.TestAsync(o, from, notes, withheld, ct);
            await audit.WriteAsync("settings.ldap_test", Clip(o.Url), success: result.Ok, detail: result.Message);
            return Results.Ok(result);
        });

        // Checked as a sign-in is (the same search, bind and groups, and this app's own rules),
        // with the form's settings. The password is never stored or logged; the try itself is audited.
        // A try is a guess at a password too: held by the same throttle and lockout as signing in.
        g.MapPost("/ldap-try", async (LdapTry body, IOptionsMonitor<LdapOptions> current, AppDbContext db, SignInService signIn, Audit audit, CancellationToken ct) =>
        {
            var (o, from, withheld, problem, notes) = await FromFormAsync(body.Settings ?? [], current.CurrentValue, db, ct);
            if (problem is not null)
            {
                return Results.Ok(problem);
            }
            var login = body.Login?.Trim() ?? "";
            LdapCheckResult result;
            if (login.Length > 0 && await signIn.TryRefusalAsync(login) is { } held)
            {
                var text = $"Not tried: {held}.";
                result = new(false, text, [new LdapStep("fail", text)]);
            }
            else
            {
                var (tried, person, refused) = await LdapCheck.TryAsync(o, from, login, body.Password ?? "", notes, withheld, ct);
                result = tried;
                if (refused)
                {
                    await signIn.TryRefusedAsync(login);
                }
                if (person is not null)
                {
                    var (refusal, verdict) = await signIn.DirectoryVerdictAsync(person);
                    result = LdapCheck.WithAppVerdict(result, refusal, verdict);
                }
            }
            if (login.Length > 0)
            {
                await audit.WriteAsync("settings.ldap_try", Clip(login), success: result.Ok, detail: $"{o.Url}: {result.Message}");
            }
            return Results.Ok(result);
        }).RequireRateLimiting("sign-in");
    }

    /// <summary>An audit entry's target, at most its column's 256 characters.</summary>
    private static string? Clip(string? value) => value is { Length: > 256 } ? value[..256] : value;

    /// <summary>
    /// What in the form keeps the saved password from going there, or null when nothing does. It goes only
    /// to the saved server (its host and port), as the saved service account, over a connection at least as
    /// safe as the saved one: encrypted if that was, its certificate checked if that was, against the same CA.
    /// </summary>
    private static string? NotAsSaved(LdapOptions form, LdapOptions saved)
    {
        static Uri? At(string? url) => Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) && uri.IdnHost.Length > 0 ? uri : null;
        static bool Encrypted(Uri uri, LdapOptions o) => LdapDirectory.IsLdaps(uri) || o.StartTls;
        static string Pem(string? pem) => string.Concat((pem ?? "").Where(c => !char.IsWhiteSpace(c)));
        var (here, there) = (At(form.Url), At(saved.Url));
        var changes = new List<string>();
        if (here is null || there is null || !string.Equals(here.IdnHost, there.IdnHost, StringComparison.OrdinalIgnoreCase) || LdapDirectory.PortOf(here) != LdapDirectory.PortOf(there))
        {
            changes.Add("names another server");
        }
        if (!string.Equals(form.BindDn?.Trim() ?? "", saved.BindDn?.Trim() ?? "", StringComparison.OrdinalIgnoreCase))
        {
            changes.Add("names another service account");
        }
        if (here is not null && there is not null && Encrypted(there, saved))
        {
            if (!Encrypted(here, form))
            {
                changes.Add("turns \"Use StartTLS\" off, so it would cross the network unencrypted");
            }
            else if (!saved.IgnoreCertificateErrors && form.IgnoreCertificateErrors)
            {
                changes.Add("turns \"Accept any certificate\" on, so anyone posing as the server would get it");
            }
            else if (!saved.IgnoreCertificateErrors && Pem(form.CaCertificate) != Pem(saved.CaCertificate))
            {
                changes.Add("changes \"Directory's CA\", so a server that CA vouches for would get it");
            }
        }
        return changes.Count == 0 ? null : string.Join(" and ", changes);
    }

    /// <summary>
    /// The settings a check uses: each one of the form, made exactly as saving would make it (the same
    /// checks, the same trimming), else the saved one. A blank password field means the saved password,
    /// with the saved server and service account over a connection as safe as the saved one only.
    /// </summary>
    private static async Task<(LdapOptions Options, PasswordFrom From, string? Withheld, LdapCheckResult? Problem, List<string> Notes)> FromFormAsync(
        Dictionary<string, string?> form, LdapOptions saved, AppDbContext db, CancellationToken ct)
    {
        var notes = new List<string>();
        var errors = new List<string>();
        string? Value(string name, string? fallback)
        {
            var key = "Ldap:" + name;
            if (!form.TryGetValue(key, out var raw))
            {
                return fallback;
            }
            var def = SettingsCatalog.ByKey[key];
            if ((SettingsService.Normalise(def, raw, out var value) ?? (key == "Ldap:CaCertificate" && !string.IsNullOrEmpty(value) ? Chat.Tools.ServerTls.CheckCa(value) : null)) is { } error)
            {
                errors.Add($"{def.Label}: {error}");
            }
            return value;
        }
        var o = new LdapOptions
        {
            Url = Value("Url", saved.Url)?.Trim(),
            StartTls = Value("StartTls", saved.StartTls ? "true" : "false") == "true",
            CaCertificate = Value("CaCertificate", saved.CaCertificate),
            IgnoreCertificateErrors = Value("IgnoreCertificateErrors", saved.IgnoreCertificateErrors ? "true" : "false") == "true",
            BindDn = Value("BindDn", saved.BindDn)?.Trim(),
            UserBaseDn = Value("UserBaseDn", saved.UserBaseDn)?.Trim() ?? "",
            UserFilter = Value("UserFilter", saved.UserFilter) is { Length: > 0 } filter ? filter : LdapOptions.DefaultUserFilter,
            GroupBaseDn = Value("GroupBaseDn", saved.GroupBaseDn)?.Trim(),
            AdminGroup = Value("AdminGroup", saved.AdminGroup)?.Trim(),
            RequiredGroup = Value("RequiredGroup", saved.RequiredGroup)?.Trim(),
            // Not in the form: as saved.
            UserNameAttributes = saved.UserNameAttributes,
            EmailAttribute = saved.EmailAttribute,
            DisplayNameAttributes = saved.DisplayNameAttributes,
            GroupFilter = saved.GroupFilter,
        };
        PasswordFrom from;
        string? withheld = null;
        if (form.TryGetValue("Ldap:BindPassword", out var typed) && !string.IsNullOrEmpty(typed))
        {
            o.BindPassword = Value("BindPassword", null);
            from = PasswordFrom.Typed;
            if (o.BindPassword is { Length: > 0 } p && (char.IsWhiteSpace(p[0]) || char.IsWhiteSpace(p[^1])))
            {
                // Often a copy and paste that took a space with it: said, never fixed silently.
                notes.Add("The typed password starts or ends with a space: it is tried, and would be saved, exactly as typed.");
            }
        }
        else if (!string.IsNullOrEmpty(saved.BindPassword) && NotAsSaved(o, saved) is { } change)
        {
            // Else a blank field would send the saved secret to whatever server the form names, or in the clear.
            from = PasswordFrom.SavedWithheld;
            withheld = change;
        }
        else if (!string.IsNullOrEmpty(saved.BindPassword))
        {
            o.BindPassword = saved.BindPassword;
            from = PasswordFrom.Saved;
        }
        else
        {
            // A saved row that the configuration left out: it no longer decrypts (APP_KEY changed).
            var row = DatabaseConfigurationProvider.RowPrefix + "Ldap:BindPassword";
            from = await db.Settings.AsNoTracking().AnyAsync(s => s.Key == row, ct) ? PasswordFrom.SavedUnreadable : PasswordFrom.None;
        }
        var problem = errors.Count > 0 ? new LdapCheckResult(false, string.Join(" ", errors), [.. errors.Select(e => new LdapStep("fail", e))]) : null;
        return (o, from, withheld, problem, notes);
    }
}
