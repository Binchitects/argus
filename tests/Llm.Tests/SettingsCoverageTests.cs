using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.RegularExpressions;
using Llm.Api.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Llm.Tests;

/// <summary>
/// Everything the app can be set to has a place in Settings: each option it binds and each configuration key the docs
/// name is an app setting, an installation setting (shown with the command that changes it), or the stack's own wiring.
/// Read from the app's own registrations, so an option added is covered as soon as it is bound.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed partial class SettingsCoverageTests(AppFixture app)
{
    /// <summary>Every key of every options class the app binds, a nested class's keys under its own (Bots:Slack:BotToken), with its type and default.</summary>
    private List<(string Key, PropertyInfo Property, object? Default)> BoundKeys()
    {
        IServiceCollection? services = null;
        using (var f = app.Factory.WithWebHostBuilder(b => b.ConfigureServices(s => services = s)))
        {
            _ = f.Services;
        }
        var keys = new List<(string, PropertyInfo, object?)>();
        foreach (var d in services!.Where(d => d.ServiceType.IsGenericType && d.ServiceType.GetGenericTypeDefinition() == typeof(IOptionsChangeTokenSource<>)))
        {
            var type = d.ServiceType.GetGenericArguments()[0];
            if (type.Namespace?.StartsWith("Llm.", StringComparison.Ordinal) != true
                || d.ImplementationInstance?.GetType().GetField("_config", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(d.ImplementationInstance) is not IConfigurationSection section)
            {
                continue;
            }
            Walk(section.Path, type, Activator.CreateInstance(type), keys);
        }
        return keys;
    }

    private static void Walk(string prefix, Type type, object? instance, List<(string, PropertyInfo, object?)> keys)
    {
        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.SetMethod is { IsPublic: true }))
        {
            var key = $"{prefix}:{p.Name}";
            var value = instance is null ? null : p.GetValue(instance);
            if (p.PropertyType.IsClass && p.PropertyType != typeof(string) && p.PropertyType.Namespace?.StartsWith("Llm.", StringComparison.Ordinal) == true)
            {
                Walk(key, p.PropertyType, value, keys);
            }
            else
            {
                keys.Add((key, p, value));
            }
        }
    }

    private static bool HasAPlace(string key) =>
        SettingsCatalog.ByKey.ContainsKey(key) || InstallSettings.ConfigKeys.Contains(key, StringComparer.Ordinal) || InstallSettings.Wiring.ContainsKey(key);

    [Fact]
    public void Every_option_the_app_binds_is_a_setting_an_installation_setting_or_wiring()
    {
        var keys = BoundKeys();
        Assert.Contains(keys, k => k.Key == "Ldap:Url");
        var missing = keys.Select(k => k.Key).Where(k => !HasAPlace(k)).Distinct().Order(StringComparer.Ordinal).ToList();
        Assert.True(missing.Count == 0, "Options with no place in Settings (add each to SettingsCatalog, or to InstallSettings when the installation sets it or it is the stack's wiring):\n"
            + string.Join('\n', missing));
        // The wiring names only keys there are, and no key is in two places.
        var bound = keys.Select(k => k.Key).ToHashSet(StringComparer.Ordinal);
        Assert.All(InstallSettings.Wiring.Keys.Where(w => !w.StartsWith("Keys:", StringComparison.Ordinal) && !w.StartsWith("Downloads:", StringComparison.Ordinal) && !w.StartsWith("Database:", StringComparison.Ordinal)),
            w => Assert.True(bound.Contains(w), $"{w} is wiring, but nothing binds it"));
        Assert.All(InstallSettings.Wiring.Keys, w => Assert.False(SettingsCatalog.ByKey.ContainsKey(w), $"{w} is a setting and wiring"));
        Assert.All(InstallSettings.ConfigKeys, k => Assert.False(SettingsCatalog.ByKey.ContainsKey(k), $"{k} is the app's setting and the installation's"));
    }

    /// <summary>
    /// What an admin may want to change is a setting, not wiring: the replicas' timing, the dashboards' scrape interval,
    /// the embedder, the security alerts, the admin group other services see, a Hugging Face mirror. What stays wiring is
    /// the stack's plumbing alone, each entry with why it is no setting; an option that means nothing (email in has no
    /// channels; Argus's GitLab sign-in is Argus's own) is not bound at all.
    /// </summary>
    [Fact]
    public void What_an_admin_may_change_is_a_setting_and_the_wiring_says_why_each_is_not()
    {
        string[] settings = ["Replicas:Enabled", "Replicas:Renew", "Replicas:AskTimeout", "Dashboards:ScrapeInterval", "Knowledge:EmbedUrl", "Knowledge:EmbedModel",
            "Knowledge:EmbedKey", "Knowledge:DocumentPrefix", "Knowledge:QueryPrefix", "Knowledge:PgVector", "Security:Watch", "Auth:AdminGroup", "HuggingFace:BaseUrl"];
        Assert.All(settings, k => Assert.True(SettingsCatalog.ByKey.ContainsKey(k), $"{k} is no setting"));
        Assert.All(InstallSettings.Wiring, w => Assert.True(w.Value.Length >= 20 && !w.Value.Contains('\n'), $"{w.Key} is wiring with no reason given"));
        Assert.Null(typeof(Llm.Api.Bots.MailInOptions).GetProperty("Channels"));
        Assert.Null(typeof(Llm.Api.Operations.ArgusOptions).GetProperty("GitlabAuth"));
        Assert.Null(typeof(Llm.Api.Operations.ArgusOptions).GetProperty("GitlabUsername"));
        // The embedder's prefixes end in a space, which is part of them; its key is never shown.
        Assert.True(SettingsCatalog.ByKey["Knowledge:DocumentPrefix"].Exact);
        Assert.True(SettingsCatalog.ByKey["Knowledge:EmbedKey"].IsSecret);
    }

    [Fact]
    public async Task The_admin_group_other_services_see_and_the_replicas_timing_apply_when_saved()
    {
        var admin = await new TestBrowser(app.Factory).SignedInAsync("admin", AppFixture.AdminPassword);
        using var scope = app.Factory.Services.CreateScope();
        var person = await scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Llm.Core.Identity.AppUser>>().FindByNameAsync("admin");
        var claims = scope.ServiceProvider.GetRequiredService<Llm.Api.Oidc.PersonClaims>();
        Assert.Contains("admins", await claims.GroupsAsync(person!));
        var res = await admin.Http.PutAsJsonAsync(new Uri("/api/admin/config", UriKind.Relative), new
        {
            changes = new[] { new { key = "Auth:AdminGroup", value = (string?)"arena-admins" }, new { key = "Replicas:AskTimeout", value = (string?)"00:00:07" } },
        });
        await StatusAssert.Is(HttpStatusCode.OK, res);
        try
        {
            Assert.Contains("arena-admins", await claims.GroupsAsync(person!));
            Assert.Equal(TimeSpan.FromSeconds(7), app.Factory.Services.GetRequiredService<IOptionsMonitor<Llm.Api.Operations.ReplicaOptions>>().CurrentValue.AskTimeout);
            // One name other services match: a comma would split forwardAuth's Remote-Groups.
            res = await admin.Http.PutAsJsonAsync(new Uri("/api/admin/config", UriKind.Relative), new { changes = new[] { new { key = "Auth:AdminGroup", value = "a,b" } } });
            await StatusAssert.Is(HttpStatusCode.BadRequest, res);
        }
        finally
        {
            await StatusAssert.Is(HttpStatusCode.OK, await admin.Http.PutAsJsonAsync(new Uri("/api/admin/config", UriKind.Relative), new
            {
                changes = new[] { new { key = "Auth:AdminGroup", reset = true }, new { key = "Replicas:AskTimeout", reset = true } },
            }));
        }
    }

    [GeneratedRegex(@"`([A-Z][A-Za-z]+(?::[A-Z][A-Za-z0-9]+)+)`")]
    private static partial Regex ColonKey();

    [GeneratedRegex(@"\b([A-Z][A-Za-z]+(?:__[A-Z][A-Za-z0-9]*)+)")]
    private static partial Regex EnvKey();

    [Fact]
    public void Every_configuration_key_the_docs_name_has_a_place_in_settings()
    {
        var docs = Path.GetFullPath(Path.Combine(AppFixture.DashboardsPath, "..", "..", "..", "..", "docs"));
        var files = Directory.GetFiles(docs, "*.md").Concat(Directory.GetFiles(Path.Combine(docs, "argus"), "*.md")).ToList();
        Assert.Contains(files, f => f.EndsWith("settings.md", StringComparison.Ordinal));
        var named = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            foreach (Match m in ColonKey().Matches(text))
            {
                named.Add(m.Groups[1].Value);
            }
            // Environment variables of the app (Engine__Vllm__0__Name): the key, without an index and what follows it.
            foreach (Match m in EnvKey().Matches(text))
            {
                var parts = m.Groups[1].Value.Split("__");
                named.Add(string.Join(':', parts.TakeWhile(p => p.Length > 0 && !char.IsDigit(p[0]) && p != "N")));
            }
        }
        named.Remove("Section:Key");
        var missing = named.Where(k => k.Contains(':', StringComparison.Ordinal) && !HasAPlace(k)).Order(StringComparer.Ordinal).ToList();
        Assert.True(missing.Count == 0, "Configuration keys the docs name with no place in Settings:\n" + string.Join('\n', missing));
    }

    private static string? CodeDefault(object? value) => value switch
    {
        null => null,
        bool b => b ? "true" : "false",
        TimeSpan t => t.ToString("c", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        string s => s.Length == 0 ? null : s,
        _ => null,
    };

    [Fact]
    public void Each_settings_default_is_the_codes_own()
    {
        var wrong = new List<string>();
        foreach (var (key, property, value) in BoundKeys())
        {
            if (SettingsCatalog.ByKey.GetValueOrDefault(key) is { Default: { } shown } d && !d.IsSecret && CodeDefault(value) is { } code
                && property.PropertyType != typeof(string) && shown != code)
            {
                wrong.Add($"{key}: the page says {shown}, the code {code}");
            }
        }
        Assert.True(wrong.Count == 0, "Defaults that are not the code's:\n" + string.Join('\n', wrong));
    }

    [Fact]
    [Trait("Control", "DAT-05")]
    public async Task Settings_lists_what_the_installation_sets_with_the_command_that_changes_it_and_no_secret()
    {
        var admin = await new TestBrowser(app.Factory).SignedInAsync("admin", AppFixture.AdminPassword);
        var view = await admin.JsonAsync(await admin.GetAsync("/api/admin/config"));
        var rows = view.GetProperty("installation").EnumerateArray().ToList();
        var lines = File.ReadAllLines(Path.GetFullPath(Path.Combine(AppFixture.DashboardsPath, "..", "..", "..", "..", "deploy", "settings")))
            .Where(l => l.Length > 0 && !l.StartsWith('#')).Select(l => l.Split('|')[0].Trim()).ToList();
        // Every line of deploy/settings, in its order, then the features and the installer's secrets.
        Assert.Equal(lines, rows.Take(lines.Count).Select(r => r.GetProperty("env").GetString()));
        Assert.Equal(rows.Count, rows.Select(r => r.GetProperty("env").GetString()).Distinct().Count());
        var domain = rows.Single(r => r.GetProperty("env").GetString() == "DOMAIN");
        Assert.Equal("arenactl config --set DOMAIN=VALUE", domain.GetProperty("command").GetString());
        Assert.False(string.IsNullOrEmpty(domain.GetProperty("value").GetString()));
        foreach (var secret in rows.Where(r => r.GetProperty("secret").GetBoolean()))
        {
            Assert.Equal(System.Text.Json.JsonValueKind.Null, secret.GetProperty("value").ValueKind);
        }
        var key = rows.Single(r => r.GetProperty("env").GetString() == "HF_TOKEN");
        Assert.True(key.GetProperty("secret").GetBoolean());
        Assert.Contains("--set HF_TOKEN=@FILE", key.GetProperty("command").GetString());
        Assert.DoesNotContain(AppFixture.AdminPassword, await (await admin.GetAsync("/api/admin/config")).Content.ReadAsStringAsync());

        // What was set only by configuration before is saved here, and applies at once.
        var res = await admin.Http.PutAsJsonAsync(new Uri("/api/admin/config", UriKind.Relative), new { changes = new[] { new { key = "Rooms:Calls:IceServers", value = "stun:stun.example.test:3478, turn:turn.example.test:3478" } } });
        await StatusAssert.Is(HttpStatusCode.OK, res);
        var options = app.Factory.Services.GetRequiredService<IOptionsMonitor<Llm.Api.Chat.RoomCallOptions>>();
        Assert.Equal("stun:stun.example.test:3478, turn:turn.example.test:3478", options.CurrentValue.IceServers);
        res = await admin.Http.PutAsJsonAsync(new Uri("/api/admin/config", UriKind.Relative), new { changes = new[] { new { key = "Rooms:Calls:IceServers", value = "http://nope" } } });
        await StatusAssert.Is(HttpStatusCode.BadRequest, res);
        await StatusAssert.Is(HttpStatusCode.OK, await admin.Http.PutAsJsonAsync(new Uri("/api/admin/config", UriKind.Relative), new { changes = new[] { new { key = "Rooms:Calls:IceServers", value = (string?)null } } }));
    }
}
