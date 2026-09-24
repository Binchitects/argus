using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Llm.Api.Chat;
using Llm.Api.Identity;
using Llm.Api.Settings;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Llm.Tests;

/// <summary>The Settings page: every setting typed, validated, saved where it belongs, and in effect.</summary>
[Collection(nameof(AppCollection))]
public sealed partial class SettingsTests(AppFixture app)
{
    private const string DataKey = "a-data-key-for-settings-tests";

    private sealed class FakeRestarter : IAppRestarter
    {
        public int Count { get; private set; }
        public void Restart() => Count++;
    }

    /// <summary>Its own app and database: saved settings must never leak into other tests.</summary>
    private (WebApplicationFactory<Program> App, string Pending, FakeRestarter Restarter, string Db) NewApp(Dictionary<string, string?>? extra = null, string? db = null, string? pendingDir = null)
    {
        db ??= app.ConnectionStringFor("settings_" + Guid.NewGuid().ToString("N")[..8]);
        pendingDir ??= Directory.CreateTempSubdirectory("llm-settings-").FullName;
        var settings = new Dictionary<string, string?>
        {
            ["Auth:DataKey"] = DataKey,
            ["Settings:PendingFile"] = Path.Combine(pendingDir, "pending.env"),
            ["StackEnv:MODEL_CONTEXT"] = "131072",
            ["StackEnv:MODEL_NAME"] = "Test-Model",
            ["StackEnv:ARGUS_GITLAB_TOKEN_SET"] = "yes",
            ["StackEnv:COMPOSE_PROFILES"] = "gateway,proxy,auth,llamacpp",
            ["Ldap:AdminGroup"] = "env-admins",
        };
        foreach (var (k, v) in extra ?? [])
        {
            settings[k] = v;
        }
        var restarter = new FakeRestarter();
        var factory = app.Create(db, new FakeGateway(), settings)
            .WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IAppRestarter>(restarter)));
        return (factory, Path.Combine(pendingDir, "pending.env"), restarter, db);
    }

    private static async Task<TestBrowser> Admin(WebApplicationFactory<Program> f) => await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);

    private static Task<HttpResponseMessage> Save(TestBrowser b, params object[] changes) =>
        b.Http.PutAsJsonAsync(new Uri("/api/admin/config", UriKind.Relative), new { changes });

    private static JsonElement Setting(JsonElement view, string key) =>
        view.GetProperty("groups").EnumerateArray().SelectMany(g => g.GetProperty("settings").EnumerateArray()).Single(s => s.GetProperty("key").GetString() == key);

    [Fact]
    public async Task Only_admins_see_the_settings()
    {
        var (f, _, _, _) = NewApp();
        await using var _f = f;
        var admin = await Admin(f);
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = "setmember", email = "setmember@example.test" }));
        var member = await new TestBrowser(f).SignedInAsync("setmember", made.GetProperty("password").GetString()!);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync("/api/admin/config")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Save(member, new { key = "Chat:MaxToolRounds", value = "3" })).StatusCode);
    }

    [Fact]
    public async Task The_view_shows_values_and_where_they_come_from_and_never_a_secret()
    {
        var (f, _, _, _) = NewApp();
        await using var _f = f;
        var admin = await Admin(f);
        var view = await admin.JsonAsync(await admin.GetAsync("/api/admin/config"));
        Assert.Equal("131072", Setting(view, "MODEL_CONTEXT").GetProperty("value").GetString());
        Assert.Equal("stack", Setting(view, "MODEL_CONTEXT").GetProperty("source").GetString());
        var token = Setting(view, "ARGUS_GITLAB_TOKEN");
        Assert.Equal(JsonValueKind.Null, token.GetProperty("value").ValueKind);
        Assert.True(token.GetProperty("isSet").GetBoolean());
        Assert.Equal("environment", Setting(view, "Ldap:AdminGroup").GetProperty("source").GetString());
        Assert.Equal("default", Setting(view, "Chat:MaxToolRounds").GetProperty("source").GetString());
        Assert.Equal("8", Setting(view, "Chat:MaxToolRounds").GetProperty("value").GetString());
        Assert.True(view.GetProperty("pendingFileWritable").GetBoolean());
    }

    [Fact]
    public async Task A_live_setting_applies_when_saved_and_a_reset_brings_the_default_back()
    {
        var (f, _, _, _) = NewApp();
        await using var _f = f;
        var admin = await Admin(f);
        var monitor = f.Services.GetRequiredService<IOptionsMonitor<ChatOptions>>();
        Assert.Equal(8, monitor.CurrentValue.MaxToolRounds);

        var saved = await Save(admin, new { key = "Chat:MaxToolRounds", value = "3" });
        await StatusAssert.Is(HttpStatusCode.OK, saved);
        Assert.Equal(3, monitor.CurrentValue.MaxToolRounds);
        Assert.Equal("saved", Setting(await admin.JsonAsync(saved), "Chat:MaxToolRounds").GetProperty("source").GetString());

        await StatusAssert.Is(HttpStatusCode.OK, await Save(admin, new { key = "Chat:MaxToolRounds", reset = true }));
        Assert.Equal(8, monitor.CurrentValue.MaxToolRounds);
    }

    [Fact]
    public async Task A_saved_value_wins_over_the_environment_and_the_page_says_what_it_overrides()
    {
        var (f, _, _, _) = NewApp();
        await using var _f = f;
        var admin = await Admin(f);
        var view = await admin.JsonAsync(await Save(admin, new { key = "Ldap:AdminGroup", value = "saved-admins" }));
        Assert.Equal("saved-admins", f.Services.GetRequiredService<IOptionsMonitor<Llm.Api.Ldap.LdapOptions>>().CurrentValue.AdminGroup);
        var row = Setting(view, "Ldap:AdminGroup");
        Assert.Equal("saved", row.GetProperty("source").GetString());
        Assert.Equal("env-admins", row.GetProperty("environmentValue").GetString());
    }

    [Fact]
    public async Task Invalid_values_are_refused_all_together_and_nothing_is_saved()
    {
        var (f, _, _, _) = NewApp();
        await using var _f = f;
        var admin = await Admin(f);
        var res = await Save(admin,
            new { key = "Chat:MaxToolRounds", value = "5" },
            new { key = "Chat:MaxAttachmentChars", value = "a lot" },
            new { key = "Ldap:Url", value = "http://not-ldap" },
            new { key = "MODEL_NAME", value = "bad$(id)" },
            new { key = "COMPOSE_PROFILES", value = "gateway,nonsense" },
            new { key = "No:Such", value = "x" });
        await StatusAssert.Is(HttpStatusCode.BadRequest, res);
        var errors = (await admin.JsonAsync(res)).GetProperty("errors");
        foreach (var key in new[] { "Chat:MaxAttachmentChars", "Ldap:Url", "MODEL_NAME", "COMPOSE_PROFILES", "No:Such" })
        {
            Assert.True(errors.TryGetProperty(key, out _), key);
        }
        Assert.False(errors.TryGetProperty("Chat:MaxToolRounds", out _));
        Assert.Equal(8, f.Services.GetRequiredService<IOptionsMonitor<ChatOptions>>().CurrentValue.MaxToolRounds);
    }

    [Fact]
    public async Task A_secret_is_stored_encrypted_used_in_plain_and_never_sent_back()
    {
        var (f, _, _, db) = NewApp();
        await using var _f = f;
        var admin = await Admin(f);
        const string secret = "directory-service-password-123";
        var res = await Save(admin, new { key = "Ldap:BindPassword", value = secret });
        await StatusAssert.Is(HttpStatusCode.OK, res);
        Assert.DoesNotContain(secret, await res.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain(secret, await (await admin.GetAsync("/api/admin/config")).Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(secret, f.Services.GetRequiredService<IOptionsMonitor<Llm.Api.Ldap.LdapOptions>>().CurrentValue.BindPassword);

        await using var conn = new NpgsqlConnection(db);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("select value from settings where key = 'config:Ldap:BindPassword'", conn);
        var stored = (string)(await cmd.ExecuteScalarAsync())!;
        Assert.StartsWith("enc:", stored, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, stored, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Saved_settings_are_in_effect_from_the_start_after_a_restart()
    {
        var dir = Directory.CreateTempSubdirectory("llm-settings-").FullName;
        var (first, _, _, db) = NewApp(pendingDir: dir);
        await using (first)
        {
            await StatusAssert.Is(HttpStatusCode.OK, await Save(await Admin(first), new { key = "Branding:ProductName", value = "Acme AI" }));
        }
        var (second, _, _, _) = NewApp(db: db, pendingDir: dir);
        await using var _s = second;
        var info = await new TestBrowser(second).JsonAsync(await new TestBrowser(second).GetAsync("/api/info"));
        Assert.Equal("Acme AI", info.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Branding_shows_on_the_public_info_at_once()
    {
        var (f, _, _, _) = NewApp();
        await using var _f = f;
        var admin = await Admin(f);
        await Save(admin, new { key = "Branding:SupportContact", value = "it-help@example.test" }, new { key = "Branding:SignInHeadline", value = "Ask anything." });
        var info = await new TestBrowser(f).JsonAsync(await new TestBrowser(f).GetAsync("/api/info"));
        Assert.Equal("it-help@example.test", info.GetProperty("supportContact").GetString());
        Assert.Equal("Ask anything.", info.GetProperty("signInHeadline").GetString());
    }

    [Fact]
    public async Task A_stack_setting_waits_in_the_pending_file_for_the_host_script()
    {
        var (f, pending, _, _) = NewApp();
        await using var _f = f;
        var admin = await Admin(f);
        var view = await admin.JsonAsync(await Save(admin,
            new { key = "MODEL_CONTEXT", value = "65536" },
            new { key = "ARGUS_GITLAB_TOKEN", value = "glpat-new-token" },
            new { key = "COMPOSE_PROFILES", value = "argus, gateway ,proxy" }));
        var text = await File.ReadAllTextAsync(pending);
        Assert.Contains("MODEL_CONTEXT=65536", text, StringComparison.Ordinal);
        Assert.Contains("ARGUS_GITLAB_TOKEN=glpat-new-token", text, StringComparison.Ordinal);
        // Normalised into the catalog's order, whatever order it was typed in.
        Assert.Contains("COMPOSE_PROFILES=gateway,proxy,argus", text, StringComparison.Ordinal);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(pending));
        }

        Assert.Equal(3, view.GetProperty("pendingStack").GetInt32());
        Assert.Equal("65536", Setting(view, "MODEL_CONTEXT").GetProperty("pending").GetString());
        Assert.Equal("131072", Setting(view, "MODEL_CONTEXT").GetProperty("value").GetString());
        var token = Setting(view, "ARGUS_GITLAB_TOKEN");
        Assert.True(token.GetProperty("pendingSet").GetBoolean());
        Assert.Equal(JsonValueKind.Null, token.GetProperty("pending").ValueKind);
        Assert.DoesNotContain("glpat-new-token", view.GetRawText(), StringComparison.Ordinal);

        // Discarding one keeps the others.
        await Save(admin, new { key = "MODEL_CONTEXT", reset = true });
        Assert.DoesNotContain("MODEL_CONTEXT", await File.ReadAllTextAsync(pending), StringComparison.Ordinal);
        await Save(admin, new { key = "ARGUS_GITLAB_TOKEN", reset = true }, new { key = "COMPOSE_PROFILES", reset = true });
        Assert.False(File.Exists(pending));
    }

    [Fact]
    public async Task A_pending_change_already_in_effect_is_dropped()
    {
        var (f, pending, _, _) = NewApp();
        await using var _f = f;
        // The script applied it (or someone edited .env): the stack now has what was pending.
        await File.WriteAllTextAsync(pending, "MODEL_CONTEXT=131072\nMODEL_NAME=Other-Model\n");
        var view = await (await Admin(f)).JsonAsync(await (await Admin(f)).GetAsync("/api/admin/config"));
        Assert.Equal(1, view.GetProperty("pendingStack").GetInt32());
        Assert.Equal("MODEL_NAME=Other-Model", (await File.ReadAllLinesAsync(pending)).Single(l => !l.StartsWith('#')));
    }

    [Fact]
    public async Task A_setting_read_at_start_asks_for_a_restart_which_the_app_does_itself()
    {
        var (f, _, restarter, _) = NewApp();
        await using var _f = f;
        var admin = await Admin(f);
        var view = await admin.JsonAsync(await Save(admin, new { key = "Auth:SessionIdle", value = "00:30:00" }));
        Assert.True(view.GetProperty("restartNeeded").GetBoolean());
        Assert.True(Setting(view, "Auth:SessionIdle").GetProperty("restartPending").GetBoolean());
        await StatusAssert.Is(HttpStatusCode.Accepted, await admin.PostAsync("/api/admin/config/restart"));
        Assert.Equal(1, restarter.Count);
        var audit = await admin.JsonAsync(await admin.GetAsync("/api/admin/audit?take=20"));
        Assert.Contains(audit.EnumerateArray(), e => e.GetProperty("action").GetString() == "settings.restart");
        Assert.Contains(audit.EnumerateArray(), e => e.GetProperty("action").GetString() == "settings.change" && e.GetProperty("target").GetString() == "Auth:SessionIdle");
    }

    [Fact]
    public async Task Durations_are_checked_in_their_unit()
    {
        var (f, _, _, _) = NewApp();
        await using var _f = f;
        var admin = await Admin(f);
        var res = await Save(admin, new { key = "Auth:SessionIdle", value = "00:01:00" });
        await StatusAssert.Is(HttpStatusCode.BadRequest, res);
        Assert.Equal("At least 5 minutes.", (await admin.JsonAsync(res)).GetProperty("errors").GetProperty("Auth:SessionIdle").GetString());
    }

    [Fact]
    public async Task The_directory_test_says_what_is_wrong_before_anything_is_saved()
    {
        var (f, _, _, _) = NewApp();
        await using var _f = f;
        var admin = await Admin(f);
        var none = await admin.JsonAsync(await admin.PostAsync("/api/admin/config/ldap-test", new Dictionary<string, string?> { ["Ldap:Url"] = "" }));
        Assert.False(none.GetProperty("ok").GetBoolean());
        var down = await admin.JsonAsync(await admin.PostAsync("/api/admin/config/ldap-test", new Dictionary<string, string?> { ["Ldap:Url"] = "ldap://127.0.0.1:9" }));
        Assert.False(down.GetProperty("ok").GetBoolean());
        Assert.Contains("127.0.0.1:9", down.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_throttle_follows_its_settings()
    {
        var o = new ThrottleOptions { MaxFailuresPerAccount = 2 };
        var throttle = new LoginThrottle(TimeProvider.System, new StaticMonitor<ThrottleOptions>(o));
        throttle.Failure("10.9.9.9", "someone");
        Assert.False(throttle.IsBanned("10.9.9.9", "someone"));
        throttle.Failure("10.9.9.9", "someone");
        Assert.True(throttle.IsBanned("10.9.9.9", "someone"));
    }

    private sealed class StaticMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    [Fact]
    public void Every_stack_setting_reaches_the_app_through_compose_and_nothing_else_does()
    {
        var compose = File.ReadAllText(Path.Combine(AppFixture.DashboardsPath, "..", "..", "..", "docker-compose.yml"));
        var passed = StackEnvName().Matches(compose).Select(m => m.Groups[1].Value).ToHashSet();
        foreach (var d in SettingsCatalog.StackSettings)
        {
            Assert.Contains(d.IsSecret ? d.Key + "_SET" : d.Key, passed);
        }
        var known = SettingsCatalog.StackSettings.Select(d => d.IsSecret ? d.Key + "_SET" : d.Key).ToHashSet();
        Assert.Empty(passed.Except(known));
        // A secret is passed only as "is it set", never its value.
        foreach (var d in SettingsCatalog.StackSettings.Where(d => d.IsSecret))
        {
            Assert.Contains($"StackEnv__{d.Key}_SET: ${{{d.Key}:+yes}}", compose, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_catalog_defaults_are_the_code_defaults()
    {
        var chat = new ChatOptions();
        var throttle = new ThrottleOptions();
        var auth = new AuthOptions();
        var ldap = new Llm.Api.Ldap.LdapOptions();
        var branding = new BrandingOptions();
        var expected = new Dictionary<string, string>
        {
            ["Chat:MaxToolRounds"] = chat.MaxToolRounds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Chat:MaxUploadBytes"] = chat.MaxUploadBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Chat:MaxAttachmentChars"] = chat.MaxAttachmentChars.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Chat:RequestTimeout"] = chat.RequestTimeout.ToString("c"),
            ["Throttle:MaxFailuresPerAccount"] = throttle.MaxFailuresPerAccount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Throttle:MaxFailuresPerAddress"] = throttle.MaxFailuresPerAddress.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Throttle:Window"] = throttle.Window.ToString("c"),
            ["Throttle:AccountBan"] = throttle.AccountBan.ToString("c"),
            ["Throttle:AddressBan"] = throttle.AddressBan.ToString("c"),
            ["Auth:SessionIdle"] = auth.SessionIdle.ToString("c"),
            ["Auth:SessionMax"] = auth.SessionMax.ToString("c"),
            ["Auth:RememberMe"] = auth.RememberMe.ToString("c"),
            ["Ldap:SyncInterval"] = ldap.SyncInterval.ToString("c"),
            ["Branding:ProductName"] = branding.ProductName,
            ["Branding:SignInHeadline"] = branding.SignInHeadline!,
        };
        foreach (var (key, value) in expected)
        {
            Assert.Equal(value, SettingsCatalog.ByKey[key].Default);
        }
    }

    [GeneratedRegex(@"StackEnv__([A-Z0-9_]+):")]
    private static partial Regex StackEnvName();
}
