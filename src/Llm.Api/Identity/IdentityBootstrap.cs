using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Identity;

/// <summary>
/// Startup: roles exist; the first admin comes from .env when nobody exists
/// yet; the directory Argus reads is current.
/// </summary>
public sealed partial class IdentityBootstrap(
    AppDbContext db,
    UserManager<AppUser> users,
    RoleManager<AppRole> roles,
    DirectoryFile directory,
    IOptions<AuthOptions> options,
    ILogger<IdentityBootstrap> logger)
{
    public async Task RunAsync(CancellationToken ct = default)
    {
        foreach (var role in Roles.All)
        {
            if (!await roles.RoleExistsAsync(role))
            {
                await roles.CreateAsync(new AppRole(role));
            }
        }
        await SeedAdminAsync();
        await directory.WriteAsync(ct);
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Created the first admin {UserName} <{Email}>")]
    private static partial void LogAdminCreated(ILogger logger, string userName, string email);

    [LoggerMessage(Level = LogLevel.Error, Message = "Nobody can sign in: there are no people and ADMIN_PASSWORD is not set.")]
    private static partial void LogNoAdminPassword(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not create the first admin: {Reason}")]
    private static partial void LogAdminFailed(ILogger logger, string reason);
}
