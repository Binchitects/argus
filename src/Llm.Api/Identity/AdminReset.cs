using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Identity;

/// <summary>
/// `dotnet Llm.Api.dll reset-admin-password [--user NAME] [--two-factor-off]`, run in the app's container by `arenactl
/// reset-admin-password` (as root on the host): the admin's password set to the one read from stdin (one line, never an
/// argument, so it is in no process list or shell history), the password policy applied; their lockout and failed tries
/// cleared, their sessions signed out (a new security stamp), and with --two-factor-off their second factor taken away too
/// (the phone with it lost). Written to the audit log. The admin is the installer's first one (Auth:AdminUserName),
/// else the person named. A person who signs in through the directory or SSO only has no password here to reset.
/// </summary>
public static class AdminReset
{
    public static async Task<int> RunAsync(IServiceProvider services, IReadOnlyList<string> args, TextReader input, TextWriter output, TextWriter error)
    {
        string? name = null;
        var twoFactorOff = false;
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--user" when i + 1 < args.Count:
                    name = args[++i];
                    break;
                case "--two-factor-off":
                    twoFactorOff = true;
                    break;
                default:
                    await error.WriteLineAsync($"reset-admin-password: no option {args[i]} (--user NAME, --two-factor-off).");
                    return 2;
            }
        }
        var password = (await input.ReadLineAsync())?.TrimEnd('\r') ?? "";
        if (password.Length == 0)
        {
            await error.WriteLineAsync("reset-admin-password: give the new password on stdin, one line.");
            return 2;
        }
        await using var scope = services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        name ??= scope.ServiceProvider.GetRequiredService<IOptions<AuthOptions>>().Value.AdminUserName;
        var user = await users.FindByNameAsync(name);
        if (user is null)
        {
            await error.WriteLineAsync($"reset-admin-password: there is nobody named {name} (--user names the admin).");
            return 3;
        }
        // The policy first, so a refused password leaves the old one in place.
        foreach (var validator in users.PasswordValidators)
        {
            var checkedOk = await validator.ValidateAsync(users, user, password);
            if (!checkedOk.Succeeded)
            {
                await error.WriteLineAsync("reset-admin-password: the password is refused: " + string.Join(" ", checkedOk.Errors.Select(e => e.Description)));
                return 4;
            }
        }
        if (await users.HasPasswordAsync(user))
        {
            var removed = await users.RemovePasswordAsync(user);
            if (!removed.Succeeded)
            {
                await error.WriteLineAsync("reset-admin-password: " + string.Join(" ", removed.Errors.Select(e => e.Description)));
                return 5;
            }
        }
        var added = await users.AddPasswordAsync(user, password);
        if (!added.Succeeded)
        {
            await error.WriteLineAsync("reset-admin-password: " + string.Join(" ", added.Errors.Select(e => e.Description)));
            return 5;
        }
        await users.SetLockoutEndDateAsync(user, null);
        await users.ResetAccessFailedCountAsync(user);
        if (twoFactorOff)
        {
            await users.SetTwoFactorEnabledAsync(user, false);
            await users.ResetAuthenticatorKeyAsync(user);
        }
        // Every session of theirs signs in again.
        await users.UpdateSecurityStampAsync(user);
        // An admin turned off can sign in again: this is how they get back in.
        if (user.IsDisabled)
        {
            (user.IsDisabled, user.DisabledReason) = (false, null);
            await users.UpdateAsync(user);
        }
        await scope.ServiceProvider.GetRequiredService<Audit>().WriteAsync("admin.password_reset", user.UserName,
            detail: "arenactl reset-admin-password, as root on the host" + (twoFactorOff ? "; the second factor taken away" : ""), actor: user);
        await output.WriteLineAsync($"The password of {user.UserName} is reset{(twoFactorOff ? ", and their second factor is off" : "")}: their sessions sign in again.");
        return 0;
    }
}
