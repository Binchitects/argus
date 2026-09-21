using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Llm.Api.Identity;

/// <summary>
/// Startup: roles exist; Authelia's people are imported once (with their
/// passwords); the first admin comes from .env when nobody exists yet; the
/// directory Argus reads is current.
/// </summary>
public sealed partial class IdentityBootstrap(
    AppDbContext db,
    UserManager<AppUser> users,
    RoleManager<AppRole> roles,
    DirectoryFile directory,
    IOptions<AuthOptions> options,
    ILogger<IdentityBootstrap> logger)
{
    private const string ImportedKey = "identity.legacy_import";

    public async Task RunAsync(CancellationToken ct = default)
    {
        foreach (var role in Roles.All)
        {
            if (!await roles.RoleExistsAsync(role))
            {
                await roles.CreateAsync(new AppRole(role));
            }
        }
        await ImportLegacyUsersAsync(ct);
        await SeedAdminAsync();
        await directory.WriteAsync(ct);
    }

    private async Task ImportLegacyUsersAsync(CancellationToken ct)
    {
        var path = options.Value.ImportUsersFile;
        if (string.IsNullOrEmpty(path) || !File.Exists(path) || await db.Settings.AnyAsync(s => s.Key == ImportedKey, ct))
        {
            return;
        }
        var doc = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).IgnoreUnmatchedProperties().Build()
            .Deserialize<LegacyFile>(await File.ReadAllTextAsync(path, ct));
        var imported = 0;
        foreach (var (name, entry) in doc?.Users ?? [])
        {
            var userName = name.Trim().ToLowerInvariant();
            if (await users.FindByNameAsync(userName) is not null || string.IsNullOrEmpty(entry.Email))
            {
                continue;
            }
            var user = new AppUser
            {
                UserName = userName,
                Email = entry.Email.Trim().ToLowerInvariant(),
                EmailConfirmed = true,
                DisplayName = entry.DisplayName ?? userName,
                IsDisabled = entry.Disabled,
                Source = UserSource.Local,
                // Authelia's argon2id hash, verified by LegacyAwarePasswordHasher and upgraded at first sign-in.
                PasswordHash = entry.Password,
            };
            var created = await users.CreateAsync(user);
            if (!created.Succeeded)
            {
                LogImportSkipped(logger, userName, string.Join(" ", created.Errors.Select(e => e.Description)));
                continue;
            }
            var admin = entry.Groups?.Contains(options.Value.AdminGroup, StringComparer.OrdinalIgnoreCase) == true;
            await users.AddToRoleAsync(user, admin ? Roles.Admin : Roles.Member);
            imported++;
        }
        db.Settings.Add(new Setting { Key = ImportedKey, Value = $"{imported} people from {path}" });
        await db.SaveChangesAsync(ct);
        LogImported(logger, imported, path);
    }

    private async Task SeedAdminAsync()
    {
        if (await db.Users.AnyAsync())
        {
            return;
        }
        var o = options.Value;
        if (string.IsNullOrEmpty(o.AdminPassword))
        {
            LogNoAdminPassword(logger);
            return;
        }
        var admin = new AppUser
        {
            UserName = o.AdminUserName,
            Email = o.AdminEmail,
            EmailConfirmed = true,
            DisplayName = "Administrator",
        };
        var result = await users.CreateAsync(admin, o.AdminPassword);
        if (!result.Succeeded)
        {
            LogAdminFailed(logger, string.Join(" ", result.Errors.Select(e => e.Description)));
            return;
        }
        await users.AddToRoleAsync(admin, Roles.Admin);
        LogAdminCreated(logger, o.AdminUserName, o.AdminEmail);
    }

    private sealed class LegacyFile
    {
        public Dictionary<string, LegacyUser>? Users { get; set; }
    }

    private sealed class LegacyUser
    {
        public bool Disabled { get; set; }
        [YamlMember(Alias = "displayname")] public string? DisplayName { get; set; }
        public string? Password { get; set; }
        public string? Email { get; set; }
        public List<string>? Groups { get; set; }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Imported {Count} people from {Path}")]
    private static partial void LogImported(ILogger logger, int count, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Not importing {UserName}: {Reason}")]
    private static partial void LogImportSkipped(ILogger logger, string userName, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Created the first admin {UserName} <{Email}>")]
    private static partial void LogAdminCreated(ILogger logger, string userName, string email);

    [LoggerMessage(Level = LogLevel.Error, Message = "Nobody can sign in: there are no people and ADMIN_PASSWORD is not set.")]
    private static partial void LogNoAdminPassword(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not create the first admin: {Reason}")]
    private static partial void LogAdminFailed(ILogger logger, string reason);
}
