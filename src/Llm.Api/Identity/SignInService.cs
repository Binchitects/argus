using Llm.Api.Ldap;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace Llm.Api.Identity;

public enum SignInOutcome
{
    Success,
    TwoFactorRequired,
    /// <summary>Wrong name or password. Deliberately the same answer for both.</summary>
    Invalid,
    LockedOut,
    Disabled,
    /// <summary>The directory knows them, but they are not in the group allowed to sign in.</summary>
    NotAllowed,
    Banned,
    /// <summary>The directory could not be reached.</summary>
    Unavailable,
}

public sealed partial class SignInService(
    Llm.Core.Data.AppDbContext db,
    SignInManager<AppUser> signIn,
    UserManager<AppUser> users,
    ILdapDirectory ldap,
    PeopleService people,
    DirectoryFile directory,
    Audit audit,
    LoginThrottle throttle,
    IHttpContextAccessor http,
    ILogger<SignInService> logger)
{
    private string? Ip => http.HttpContext?.Connection.RemoteIpAddress?.ToString();

    /// <summary>
    /// Two sign-ins of the same person at once (two devices, parallel scripts) both
    /// update their row, and Identity's concurrency stamp fails one of them. The
    /// loser retries against fresh data instead of answering 500.
    /// </summary>
    public async Task<SignInOutcome> PasswordAsync(string login, string password, bool remember)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await PasswordOnceAsync(login, password, remember);
            }
            catch (DbUpdateConcurrencyException) when (attempt < 5)
            {
                db.ChangeTracker.Clear();
            }
        }
    }

    public async Task<SignInOutcome> TwoFactorAsync(string code, bool recovery, bool remember)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await TwoFactorOnceAsync(code, recovery, remember);
            }
            catch (DbUpdateConcurrencyException) when (attempt < 5)
            {
                db.ChangeTracker.Clear();
            }
        }
    }

    private async Task<SignInOutcome> PasswordOnceAsync(string login, string password, bool remember)
    {
        // "CORP\jsmith" is jsmith (no username here holds a backslash): one name for the
        // directory, the lockout and the throttle, however the domain in front is typed.
        login = LdapDirectory.SignInName(login);
        if (throttle.IsBanned(Ip, login))
        {
            await audit.WriteAsync("sign_in", login, success: false, detail: "address banned");
            return SignInOutcome.Banned;
        }
        var user = await users.FindByNameAsync(login.ToLowerInvariant()) ?? await users.FindByEmailAsync(login);

        if (user is not null && user.Source == UserSource.Local)
        {
            var check = await signIn.CheckPasswordSignInAsync(user, password, lockoutOnFailure: false);
            if (check.IsLockedOut)
            {
                return await FailAsync(login, SignInOutcome.LockedOut, "account locked");
            }
            if (!check.Succeeded)
            {
                return await FailAsync(login, await CountFailureAsync(user) ? SignInOutcome.LockedOut : SignInOutcome.Invalid, "wrong password");
            }
            if (user.IsDisabled)
            {
                return await FailAsync(login, SignInOutcome.Disabled, "disabled", countsAsGuess: false);
            }
            return await CompleteAsync(user, remember, "pwd");
        }

        if (!ldap.Enabled)
        {
            return await FailAsync(login, SignInOutcome.Invalid, "unknown name");
        }
        if (user is not null && await users.IsLockedOutAsync(user))
        {
            return await FailAsync(login, SignInOutcome.LockedOut, "account locked");
        }
        LdapSignIn directorySays;
        try
        {
            directorySays = await ldap.SignInAsync(login, password);
        }
        catch (LdapUnavailableException ex)
        {
            LogLdapDown(logger, ex.Message);
            await audit.WriteAsync("sign_in", login, success: false, detail: "directory unavailable");
            return SignInOutcome.Unavailable;
        }
        if (directorySays.Person is not { } person)
        {
            if (user is not null)
            {
                await CountFailureAsync(user);
            }
            // The directory's reason (a wrong password, an expired one) is for the audit log only:
            // the person is told what anyone is told, so nobody learns which accounts exist.
            return await FailAsync(login, SignInOutcome.Invalid, $"directory refused: {directorySays.Refusal}");
        }
        if (!ldap.IsAllowed(person))
        {
            return await FailAsync(login, SignInOutcome.NotAllowed, "not in the sign-in group", countsAsGuess: false);
        }
        var (synced, refusal) = await SyncFromDirectoryAsync(person);
        if (synced is null)
        {
            return await FailAsync(login, SignInOutcome.NotAllowed, refusal, countsAsGuess: false);
        }
        if (synced.IsDisabled)
        {
            return await FailAsync(login, SignInOutcome.Disabled, "disabled", countsAsGuess: false);
        }
        await users.ResetAccessFailedCountAsync(synced);
        return await CompleteAsync(synced, remember, "ldap");
    }

    private async Task<SignInOutcome> TwoFactorOnceAsync(string code, bool recovery, bool remember)
    {
        var user = await signIn.GetTwoFactorAuthenticationUserAsync();
        if (user is null)
        {
            return SignInOutcome.Invalid; // the password step expired or never happened
        }
        if (throttle.IsBanned(Ip, user.UserName!))
        {
            return SignInOutcome.Banned;
        }
        // Authenticator codes are often typed as "123 456"; recovery codes contain a hyphen that is part of them.
        code = code.Replace(" ", "", StringComparison.Ordinal);
        if (!recovery)
        {
            code = code.Replace("-", "", StringComparison.Ordinal);
        }
        var result = recovery
            ? await signIn.TwoFactorRecoveryCodeSignInAsync(code)
            : await signIn.TwoFactorAuthenticatorSignInAsync(code, remember, rememberClient: false);
        if (result.IsLockedOut)
        {
            return await FailAsync(user.UserName!, SignInOutcome.LockedOut, "account locked (2fa)");
        }
        if (!result.Succeeded)
        {
            return await FailAsync(user.UserName!, SignInOutcome.Invalid, recovery ? "wrong recovery code" : "wrong 2fa code");
        }
        await SignedInAsync(user, recovery ? "recovery code" : "2fa");
        return SignInOutcome.Success;
    }

    /// <summary>The local record of a directory person (null before their first sign-in), or why they cannot have one here.</summary>
    private async Task<(AppUser? User, string? Refusal)> MatchDirectoryPersonAsync(LdapPerson person)
    {
        if (string.IsNullOrEmpty(person.Email))
        {
            return (null, "the directory entry has no email address");
        }
        var user = await users.Users.SingleOrDefaultAsync(u => u.LdapDn == person.Dn)
            ?? await users.FindByNameAsync(person.UserName);
        if (user is not null && user.Source != UserSource.Ldap)
        {
            // Never let a directory entry take over an existing local account.
            return (null, $"a local account named {person.UserName} already exists");
        }
        var byEmail = await users.FindByEmailAsync(person.Email);
        if (byEmail is not null && byEmail.Id != user?.Id)
        {
            return (null, $"another account already uses {person.Email}");
        }
        return (user, null);
    }

    /// <summary>
    /// Why the admin's check of a typed password may not go ahead, or null: a try of a person's sign-in (their
    /// username), or a test of a service account other than the saved one (its DN, or user@domain). Either is a
    /// guess at a password like any sign-in, so the same brakes hold it: the throttle on that name from this
    /// address, and the lock on their account here.
    /// </summary>
    public async Task<string?> TryRefusalAsync(string login)
    {
        var (key, shown, user) = await GuessedAsync(login);
        if (throttle.IsBanned(Ip, key))
        {
            return $"too many wrong passwords for \"{shown}\" (or for many names) from this address: sign-ins, tries and tests of it from here are refused for a while (how long: Settings → Sign-in and sessions)";
        }
        return user is not null && await users.IsLockedOutAsync(user)
            ? $"their account here ({user.UserName}) is locked for a while after wrong passwords, and a check waits as signing in does"
            : null;
    }

    /// <summary>A password the directory refused in the admin's try or test: counted as a sign-in's would be, against this address and their account here.</summary>
    public async Task TryRefusedAsync(string login)
    {
        var (key, _, user) = await GuessedAsync(login);
        throttle.Failure(Ip, key);
        if (user is not null)
        {
            await CountFailureAsync(user);
        }
    }

    /// <summary>
    /// Whose password a check guesses: the name the throttle holds (a username as signing in reads it; a DN in
    /// one form, however it is written), that name as shown, and their directory account here, if any.
    /// </summary>
    private async Task<(string Key, string Shown, AppUser? User)> GuessedAsync(string login)
    {
        if (LdapDirectory.Rdns(login) is { } rdns)
        {
            // A DN: the person whose entry it is, if they have signed in here (by their name first, then by the DN as written).
            var dn = login.Trim();
            var named = await users.FindByNameAsync(LdapDirectory.CommonName(dn).ToLowerInvariant());
            var exactly = dn.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
            var user = named is { Source: UserSource.Ldap, LdapDn: { } stored } && LdapDirectory.SameDn(stored, dn) ? named
                : await users.Users.FirstOrDefaultAsync(u => u.Source == UserSource.Ldap && u.LdapDn != null && EF.Functions.ILike(u.LdapDn, exactly));
            return (string.Join(',', rdns), dn, user);
        }
        var name = LdapDirectory.SignInName(login);
        var found = await users.FindByNameAsync(name.ToLowerInvariant()) ?? await users.FindByEmailAsync(name);
        return (name, name, found is { Source: UserSource.Ldap } ? found : null);
    }

    /// <summary>
    /// What signing in would do here for a person the directory let in, changing nothing (the
    /// admin's try): why the app would still refuse them, or how they come in.
    /// </summary>
    public async Task<(string? Refusal, string Verdict)> DirectoryVerdictAsync(LdapPerson person)
    {
        var (user, refusal) = await MatchDirectoryPersonAsync(person);
        if (refusal is not null)
        {
            return (refusal, "");
        }
        if (user is null)
        {
            // The checks that making the account runs (a username of letters, digits and - . _ @ +).
            var candidate = new AppUser { UserName = person.UserName, Email = person.Email, Source = UserSource.Ldap, LdapDn = person.Dn };
            foreach (var validator in users.UserValidators)
            {
                if (await validator.ValidateAsync(users, candidate) is { Succeeded: false } invalid)
                {
                    return ($"their account cannot be made: {string.Join(" ", invalid.Errors.Select(e => e.Description)).TrimEnd('.')}", "");
                }
            }
            return (null, "Their account here is made when they first sign in.");
        }
        if (user.IsDisabled && user.DisabledReason != "ldap")
        {
            return ("an admin disabled their account here (Admin → People)", "");
        }
        if (await users.IsLockedOutAsync(user))
        {
            return ("their account here is locked for a while after wrong passwords", "");
        }
        return (null, $"They have an account here ({user.UserName}): signing in brings it up to date with the directory.");
    }

    /// <summary>
    /// Creates or updates the local record of a directory person. The directory
    /// is authoritative for their name, email and admin role.
    /// </summary>
    public async Task<(AppUser? User, string Refusal)> SyncFromDirectoryAsync(LdapPerson person)
    {
        var (user, refusal) = await MatchDirectoryPersonAsync(person);
        if (refusal is not null)
        {
            return (null, refusal);
        }

        var admin = ldap.IsAdmin(person);
        if (user is null)
        {
            user = new AppUser
            {
                UserName = person.UserName,
                Email = person.Email,
                EmailConfirmed = true,
                DisplayName = person.DisplayName,
                Source = UserSource.Ldap,
                LdapDn = person.Dn,
                DirectoryGroups = [.. person.Groups.Order(StringComparer.OrdinalIgnoreCase)],
            };
            var created = await users.CreateAsync(user);
            if (!created.Succeeded)
            {
                return (null, string.Join(" ", created.Errors.Select(e => e.Description)));
            }
            await users.AddToRoleAsync(user, admin ? Roles.Admin : Roles.Member);
            await audit.WriteAsync("person.create", user.UserName, detail: "from the directory" + (admin ? ", admin" : ""));
            try
            {
                await people.ProvisionGatewayAsync(user);
            }
            catch (Gateway.GatewayException ex)
            {
                await audit.WriteAsync("person.provision_gateway", user.UserName, success: false, detail: ex.Message);
            }
            await directory.WriteAsync();
            return (user, "");
        }

        if (user.IsDisabled && user.DisabledReason == "ldap")
        {
            // Back in the directory: the same path as an admin enabling them, so their keys are unblocked too.
            await people.SetDisabledAsync(PeopleService.DirectorySync, user, disabled: false, reason: "ldap");
        }
        var changed = user.Email != person.Email || user.DisplayName != person.DisplayName || user.LdapDn != person.Dn || user.UserName != person.UserName;
        user.Email = person.Email;
        user.DisplayName = person.DisplayName;
        user.LdapDn = person.Dn;
        user.UserName = person.UserName;
        // Kept for access rules (tools, models) that name directory groups.
        user.DirectoryGroups = [.. person.Groups.Order(StringComparer.OrdinalIgnoreCase)];
        await users.UpdateAsync(user);
        if (await users.IsInRoleAsync(user, Roles.Admin) != admin)
        {
            await users.RemoveFromRolesAsync(user, Roles.All);
            await users.AddToRoleAsync(user, admin ? Roles.Admin : Roles.Member);
            await users.UpdateSecurityStampAsync(user);
            await audit.WriteAsync(admin ? "person.make_admin" : "person.remove_admin", user.UserName, detail: "from the directory");
        }
        if (changed)
        {
            await directory.WriteAsync();
        }
        return (user, "");
    }

    /// <summary>
    /// One atomic UPDATE, not Identity's read-modify-write: parallel wrong guesses
    /// would otherwise overwrite each other's count and never reach the lockout.
    /// </summary>
    /// <returns>True when this failure locked the account.</returns>
    private async Task<bool> CountFailureAsync(AppUser user)
    {
        var max = users.Options.Lockout.MaxFailedAccessAttempts;
        var until = DateTimeOffset.UtcNow + users.Options.Lockout.DefaultLockoutTimeSpan;
        // ONE statement: every SET sees the same row, and Postgres re-reads the row
        // under its lock when two of these meet. Two statements (check, then
        // increment) let two guesses at 8 both skip the lock and land on 10 unlocked.
        await db.Users.Where(u => u.Id == user.Id && u.LockoutEnabled).ExecuteUpdateAsync(set => set
            .SetProperty(u => u.LockoutEnd, u => u.AccessFailedCount + 1 >= max ? until : u.LockoutEnd)
            .SetProperty(u => u.AccessFailedCount, u => u.AccessFailedCount + 1 >= max ? 0 : u.AccessFailedCount + 1));
        return await db.Users.AnyAsync(u => u.Id == user.Id && u.LockoutEnd == until);
    }

    public async Task SignOutAsync()
    {
        var name = http.HttpContext?.User.Identity?.Name;
        await signIn.SignOutAsync();
        if (name is not null)
        {
            await audit.WriteAsync("sign_out", name);
        }
    }

    private async Task<SignInOutcome> CompleteAsync(AppUser user, bool remember, string method)
    {
        if (user.TwoFactorEnabled)
        {
            // What SignInManager does after a correct password: remember WHO is
            // halfway in, in a short-lived cookie, until the second factor arrives.
            var half = new ClaimsIdentity(IdentityConstants.TwoFactorUserIdScheme);
            half.AddClaim(new Claim(ClaimTypes.Name, user.Id.ToString()));
            await http.HttpContext!.SignInAsync(IdentityConstants.TwoFactorUserIdScheme, new ClaimsPrincipal(half));
            await audit.WriteAsync("sign_in.password_ok", user.UserName, detail: method + ", awaiting 2fa");
            return SignInOutcome.TwoFactorRequired;
        }
        await signIn.SignInWithClaimsAsync(user, remember, [new Claim("amr", method)]);
        await SignedInAsync(user, method);
        return SignInOutcome.Success;
    }

    private async Task SignedInAsync(AppUser user, string method)
    {
        throttle.Success(Ip, user.UserName!);
        if (user.Email is not null)
        {
            throttle.Success(Ip, user.Email);
        }
        // A direct update: it must not race the concurrency stamp of a parallel sign-in.
        var now = DateTimeOffset.UtcNow;
        await db.Users.Where(u => u.Id == user.Id).ExecuteUpdateAsync(set => set.SetProperty(u => u.LastSignInAt, now));
        await audit.WriteAsync("sign_in", user.UserName, detail: method, actor: user);
    }

    private async Task<SignInOutcome> FailAsync(string login, SignInOutcome outcome, string detail, bool countsAsGuess = true)
    {
        if (countsAsGuess)
        {
            throttle.Failure(Ip, login);
        }
        await audit.WriteAsync("sign_in", login, success: false, detail: detail);
        return outcome;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "LDAP sign-in failed: {Reason}")]
    private static partial void LogLdapDown(ILogger logger, string reason);
}
