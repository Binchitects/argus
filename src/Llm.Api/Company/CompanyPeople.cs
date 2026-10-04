using Llm.Api.Gateway;
using Llm.Api.Identity;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Company;

/// <summary>Why the identity provider's view of a person cannot be applied here. A conflict is a name or email someone else has.</summary>
public sealed class CompanyRefusedException(string message, bool conflict = false) : Exception(message)
{
    public bool Conflict { get; } = conflict;
}

/// <summary>
/// People who come from the company's identity provider, by company sign-in or by
/// SCIM: made, matched and kept current here. The provider is in charge of their
/// name, email and (at sign-in) role; their password is never checked here.
/// </summary>
public sealed partial class CompanyPeople(UserManager<AppUser> users, PeopleService people, ILiteLlm gateway, Audit audit, DirectoryFile directory)
{
    [System.Text.RegularExpressions.GeneratedRegex("^[a-z0-9][a-z0-9._-]{1,63}$")]
    private static partial System.Text.RegularExpressions.Regex UserNamePattern();

    [System.Text.RegularExpressions.GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial System.Text.RegularExpressions.Regex EmailPattern();

    /// <summary>
    /// The username here for the provider's value: lower case, and an email-like value
    /// (Entra's alice@example.com) gives its part before the @. Null when it cannot be one.
    /// </summary>
    public static string? UserName(string? value)
    {
        var v = (value ?? "").Trim().ToLowerInvariant();
        if (v.IndexOf('@', StringComparison.Ordinal) is var at and > 0)
        {
            v = v[..at];
        }
        return UserNamePattern().IsMatch(v) ? v : null;
    }

    public static string? Email(string? value)
    {
        var v = (value ?? "").Trim().ToLowerInvariant();
        return EmailPattern().IsMatch(v) ? v : null;
    }

    /// <summary>The provider's group names are compared exactly, ignoring case: a look-alike group elsewhere in it never counts.</summary>
    public static bool InGroup(IEnumerable<string> groups, string? group) =>
        !string.IsNullOrWhiteSpace(group) && groups.Any(g => string.Equals(g.Trim(), group.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>A new person from the provider: no password, a member, a key at the gateway.</summary>
    public async Task<AppUser> CreateAsync(string userName, string email, string? displayName, string how, Action<AppUser>? also = null)
    {
        if (await users.FindByNameAsync(userName) is not null)
        {
            throw new CompanyRefusedException($"a person named {userName} already exists here", conflict: true);
        }
        if (await users.FindByEmailAsync(email) is not null)
        {
            throw new CompanyRefusedException($"someone here already uses {email}", conflict: true);
        }
        var user = new AppUser
        {
            UserName = userName,
            Email = email,
            EmailConfirmed = true,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? userName : displayName.Trim(),
            Source = UserSource.Oidc,
        };
        also?.Invoke(user);
        var created = await users.CreateAsync(user);
        if (!created.Succeeded)
        {
            throw new CompanyRefusedException(string.Join(" ", created.Errors.Select(e => e.Description)));
        }
        await users.AddToRoleAsync(user, Roles.Member);
        await audit.WriteAsync("person.create", user.UserName, detail: how);
        if (!user.IsDisabled)
        {
            try
            {
                await people.ProvisionGatewayAsync(user);
            }
            catch (GatewayException ex)
            {
                await audit.WriteAsync("person.provision_gateway", user.UserName, success: false, detail: ex.Message);
            }
        }
        await directory.WriteAsync();
        return user;
    }

    /// <summary>
    /// Makes an existing person the provider's (a local or directory account of the
    /// same email). Never a local admin: they stay local, a way in when the provider is down.
    /// </summary>
    public async Task AdoptAsync(AppUser user, string how)
    {
        if (user.Source == UserSource.Oidc)
        {
            return;
        }
        if (user.Source == UserSource.Local && await users.IsInRoleAsync(user, Roles.Admin))
        {
            throw new CompanyRefusedException($"{user.UserName} is a local admin: local admins stay local, so there is a way in when the company sign-in is down", conflict: true);
        }
        var was = UserSources.Name(user.Source);
        user.Source = UserSource.Oidc;
        user.LdapDn = null;
        // Two-factor sign-in is the provider's from now on; a code set up here would never be asked for.
        user.TwoFactorEnabled = false;
        Check(await users.UpdateAsync(user));
        await audit.WriteAsync("person.adopt", user.UserName, detail: $"{how} (was {was})");
    }

    /// <summary>The provider's username, email and name for an existing person; null leaves one as it is.</summary>
    public async Task UpdateAsync(AppUser user, string? userName, string? email, string? displayName)
    {
        var renamed = false;
        if (userName is not null && userName != user.UserName)
        {
            if (await users.FindByNameAsync(userName) is { } other && other.Id != user.Id)
            {
                throw new CompanyRefusedException($"another person here is named {userName}", conflict: true);
            }
            user.UserName = userName;
            renamed = true;
        }
        string? oldEmail = null;
        if (email is not null && email != user.Email)
        {
            if (await users.FindByEmailAsync(email) is { } other && other.Id != user.Id)
            {
                throw new CompanyRefusedException($"someone else here uses {email}", conflict: true);
            }
            oldEmail = user.Email;
            user.Email = email;
        }
        if (!string.IsNullOrWhiteSpace(displayName) && displayName.Trim() != user.DisplayName)
        {
            user.DisplayName = displayName.Trim();
            renamed = true;
        }
        if (!renamed && oldEmail is null)
        {
            return;
        }
        Check(await users.UpdateAsync(user));
        if (renamed)
        {
            await audit.WriteAsync("person.rename", user.UserName, detail: "from the identity provider");
        }
        if (oldEmail is not null)
        {
            await MoveGatewayAsync(user, oldEmail);
        }
        await directory.WriteAsync();
    }

    /// <summary>The gateway knows people by email: a new email gets a new key, and the old one stops.</summary>
    private async Task MoveGatewayAsync(AppUser user, string? oldEmail)
    {
        try
        {
            if (!string.IsNullOrEmpty(oldEmail))
            {
                await gateway.DeleteKeysAsync((await gateway.KeysAsync(oldEmail)).Select(k => k.Token));
            }
            // A disabled person gets no key now; they make one under Your account when they are back.
            if (!user.IsDisabled)
            {
                await people.ProvisionGatewayAsync(user);
            }
            await audit.WriteAsync("person.change_email", user.UserName, detail: $"{oldEmail} to {user.Email}, a new API key");
        }
        catch (GatewayException ex)
        {
            await audit.WriteAsync("person.change_email", user.UserName, success: false, detail: ex.Message);
        }
    }

    /// <summary>The provider's groups, kept for access rules (tools, models) that name them.</summary>
    public async Task SetGroupsAsync(AppUser user, IEnumerable<string> groups)
    {
        var sorted = groups.Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (!sorted.SequenceEqual(user.DirectoryGroups, StringComparer.Ordinal))
        {
            user.DirectoryGroups = sorted;
            Check(await users.UpdateAsync(user));
        }
    }

    /// <summary>The role from the provider's admin group; a change signs them out of other apps (fresh tokens carry it).</summary>
    public async Task SetRoleAsync(AppUser user, bool admin, string how)
    {
        if (await users.IsInRoleAsync(user, Roles.Admin) == admin)
        {
            return;
        }
        await users.RemoveFromRolesAsync(user, Roles.All);
        await users.AddToRoleAsync(user, admin ? Roles.Admin : Roles.Member);
        await users.UpdateSecurityStampAsync(user);
        await audit.WriteAsync(admin ? "person.make_admin" : "person.remove_admin", user.UserName, detail: how);
    }

    public Task<AppUser?> FindBySubjectAsync(string subject) =>
        users.Users.FirstOrDefaultAsync(u => u.OidcSubject == subject && u.Source == UserSource.Oidc);

    private static void Check(IdentityResult result)
    {
        if (!result.Succeeded)
        {
            throw new CompanyRefusedException(string.Join(" ", result.Errors.Select(e => e.Description)));
        }
    }
}
