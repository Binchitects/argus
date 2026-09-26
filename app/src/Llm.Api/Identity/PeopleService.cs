using System.Security.Cryptography;
using Llm.Api.Gateway;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;

namespace Llm.Api.Identity;

public sealed record NewPerson(string UserName, string Email, string? DisplayName, bool Admin, decimal? Budget);

/// <summary>Shown once and never stored: the generated password and the API key.</summary>
public sealed record Secrets(string? Password, string? ApiKey, string? Warning = null);

public sealed class PeopleException(string message) : Exception(message);

/// <summary>Every change to a person goes through here, so the rules live in one place.</summary>
public sealed partial class PeopleService(
    UserManager<AppUser> users,
    AppDbContext db,
    ILiteLlm gateway,
    Models.KeyAccess keyAccess,
    Audit audit,
    DirectoryFile directory,
    IOpenIddictTokenManager tokens,
    IOpenIddictAuthorizationManager authorizations)
{
    [System.Text.RegularExpressions.GeneratedRegex("^[a-z0-9][a-z0-9._-]{1,63}$")]
    private static partial System.Text.RegularExpressions.Regex UserNamePattern();

    [System.Text.RegularExpressions.GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial System.Text.RegularExpressions.Regex EmailPattern();

    public static string KeyAlias(AppUser user) => $"app-{user.UserName}";

    /// <summary>Stands in for "who did it" when the directory sync acts; never equal to a real person.</summary>
    public static readonly AppUser DirectorySync = new() { Id = Guid.Empty, UserName = "directory-sync" };

    /// <summary>20 URL-safe characters from a CSPRNG (about 120 bits).</summary>
    public static string GeneratePassword() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(15)).Replace('+', '-').Replace('/', '_');

    public async Task<(AppUser User, Secrets Secrets)> CreateAsync(NewPerson p)
    {
        var userName = p.UserName.Trim().ToLowerInvariant();
        var email = p.Email.Trim().ToLowerInvariant();
        if (!UserNamePattern().IsMatch(userName))
        {
            throw new PeopleException("Username must be 2-64 characters of a-z 0-9 . _ - (it must match their GitLab username).");
        }
        if (!EmailPattern().IsMatch(email))
        {
            throw new PeopleException("That does not look like an email address.");
        }
        if (await users.FindByNameAsync(userName) is not null)
        {
            throw new PeopleException($"{userName} already exists.");
        }
        if (await users.FindByEmailAsync(email) is not null)
        {
            throw new PeopleException($"Someone already uses {email}.");
        }

        var user = new AppUser
        {
            UserName = userName,
            Email = email,
            EmailConfirmed = true,
            DisplayName = string.IsNullOrWhiteSpace(p.DisplayName) ? userName : p.DisplayName.Trim(),
            Source = UserSource.Local,
        };
        var password = GeneratePassword();
        Check(await users.CreateAsync(user, password));
        Check(await users.AddToRoleAsync(user, p.Admin ? Roles.Admin : Roles.Member));
        await audit.WriteAsync("person.create", userName, detail: p.Admin ? "admin" : "member");
        await directory.WriteAsync();

        try
        {
            await gateway.EnsureUserAsync(email);
            if (p.Budget is not null)
            {
                await gateway.SetBudgetAsync(email, p.Budget);
            }
            var key = await gateway.GenerateKeyAsync(email, KeyAlias(user), await keyAccess.ListForAsync(user), keyAccess.MaxParallel);
            return (user, new Secrets(password, key));
        }
        catch (GatewayException ex)
        {
            return (user, new Secrets(password, null, $"The account exists, but the gateway refused the API key: {ex.Message}"));
        }
    }

    /// <summary>For people who arrive through LDAP: a gateway user and a key, so they can use the API at once.</summary>
    public async Task ProvisionGatewayAsync(AppUser user)
    {
        await gateway.EnsureUserAsync(user.Email!);
        if ((await gateway.KeysAsync(user.Email!)).Count == 0)
        {
            await gateway.GenerateKeyAsync(user.Email!, KeyAlias(user), await keyAccess.ListForAsync(user), keyAccess.MaxParallel);
        }
    }

    public async Task SetAdminAsync(AppUser actor, AppUser user, bool admin)
    {
        var isAdmin = await users.IsInRoleAsync(user, Roles.Admin);
        if (isAdmin == admin)
        {
            return;
        }
        if (!admin)
        {
            if (user.Id == actor.Id)
            {
                throw new PeopleException("You cannot remove your own admin role.");
            }
            await EnsureAnotherAdminAsync(user);
        }
        Check(await users.RemoveFromRolesAsync(user, Roles.All.Where(r => r != (admin ? Roles.Admin : Roles.Member))));
        Check(await users.AddToRoleAsync(user, admin ? Roles.Admin : Roles.Member));
        // Role changes reach other services through fresh tokens only.
        await EndSessionsAsync(user);
        await audit.WriteAsync(admin ? "person.make_admin" : "person.remove_admin", user.UserName);
    }

    public async Task SetDisabledAsync(AppUser actor, AppUser user, bool disabled, string reason = "admin")
    {
        if (user.IsDisabled == disabled)
        {
            return;
        }
        if (disabled && user.Id == actor.Id)
        {
            throw new PeopleException("You cannot disable yourself.");
        }
        if (disabled && await users.IsInRoleAsync(user, Roles.Admin))
        {
            await EnsureAnotherAdminAsync(user);
        }
        user.IsDisabled = disabled;
        user.DisabledReason = disabled ? reason : null;
        Check(await users.UpdateAsync(user));
        if (disabled)
        {
            await EndSessionsAsync(user);
        }
        await SetKeysBlockedAsync(user, disabled);
        await audit.WriteAsync(disabled ? "person.disable" : "person.enable", user.UserName, detail: reason);
        await directory.WriteAsync();
    }

    public async Task<Secrets> ResetPasswordAsync(AppUser user)
    {
        if (user.Source != UserSource.Local)
        {
            throw new PeopleException($"{user.UserName} signs in through LDAP; change the password in the directory.");
        }
        var password = GeneratePassword();
        Check(await users.RemovePasswordAsync(user));
        Check(await users.AddPasswordAsync(user, password));
        await users.SetLockoutEndDateAsync(user, null);
        await users.ResetAccessFailedCountAsync(user);
        await EndSessionsAsync(user);
        await audit.WriteAsync("person.reset_password", user.UserName);
        return new Secrets(password, null);
    }

    public async Task ResetTwoFactorAsync(AppUser user)
    {
        Check(await users.SetTwoFactorEnabledAsync(user, false));
        Check(await users.ResetAuthenticatorKeyAsync(user));
        await audit.WriteAsync("person.reset_2fa", user.UserName);
    }

    /// <summary>Revoke first, then mint: the reverse leaves a window where the old key still works.</summary>
    public async Task<Secrets> RotateKeyAsync(AppUser user)
    {
        var old = await gateway.KeysAsync(user.Email!);
        await gateway.DeleteKeysAsync(old.Select(k => k.Token));
        await gateway.EnsureUserAsync(user.Email!);
        var key = await gateway.GenerateKeyAsync(user.Email!, KeyAlias(user), await keyAccess.ListForAsync(user), keyAccess.MaxParallel);
        await audit.WriteAsync("person.rotate_key", user.UserName, detail: $"revoked {old.Count}");
        return new Secrets(null, key);
    }

    public async Task SetBudgetAsync(AppUser user, decimal? budget)
    {
        if (budget < 0)
        {
            throw new PeopleException("Credit cannot be negative.");
        }
        await gateway.EnsureUserAsync(user.Email!);
        await gateway.SetBudgetAsync(user.Email!, budget);
        await audit.WriteAsync("person.set_budget", user.UserName, detail: budget?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unlimited");
    }

    public async Task DeleteAsync(AppUser actor, AppUser user)
    {
        if (user.Id == actor.Id)
        {
            throw new PeopleException("You cannot delete yourself.");
        }
        if (await users.IsInRoleAsync(user, Roles.Admin))
        {
            await EnsureAnotherAdminAsync(user);
        }
        await EndSessionsAsync(user);
        string? warning = null;
        try
        {
            await gateway.DeleteUserAsync(user.Email!);
        }
        catch (GatewayException ex)
        {
            warning = ex.Message;
        }
        Check(await users.DeleteAsync(user));
        await audit.WriteAsync("person.delete", user.UserName, detail: warning);
        await directory.WriteAsync();
    }

    /// <summary>Signs the person out everywhere: cookies die at their next check, OIDC grants are revoked.</summary>
    public async Task EndSessionsAsync(AppUser user)
    {
        await users.UpdateSecurityStampAsync(user);
        var subject = user.Id.ToString();
        await tokens.RevokeBySubjectAsync(subject);
        await authorizations.RevokeBySubjectAsync(subject);
    }

    private async Task SetKeysBlockedAsync(AppUser user, bool blocked)
    {
        try
        {
            var keys = await gateway.KeysAsync(user.Email!);
            await gateway.SetBlockedAsync(keys.Where(k => k.Blocked != blocked).Select(k => k.Token), blocked);
        }
        catch (GatewayException ex)
        {
            await audit.WriteAsync(blocked ? "person.block_keys" : "person.unblock_keys", user.UserName, success: false, detail: ex.Message);
        }
    }

    private async Task EnsureAnotherAdminAsync(AppUser leaving)
    {
        var admins = await users.GetUsersInRoleAsync(Roles.Admin);
        if (!admins.Any(a => a.Id != leaving.Id && !a.IsDisabled))
        {
            throw new PeopleException("That would leave nobody who can administer the service.");
        }
    }

    private static void Check(IdentityResult result)
    {
        if (!result.Succeeded)
        {
            throw new PeopleException(string.Join(" ", result.Errors.Select(e => e.Description)));
        }
    }

    public Task<AppUser?> FindAsync(Guid id) => db.Users.SingleOrDefaultAsync(u => u.Id == id);
}
