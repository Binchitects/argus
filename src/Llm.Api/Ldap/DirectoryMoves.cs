using Llm.Api.Identity;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Ldap;

/// <summary>
/// One local account and the directory entry it would sign in with: what moving it changes, or why it cannot move.
/// Their account stays (its id, so their chats, files, groups, keys and spend), and so does their email.
/// </summary>
public sealed record DirectoryMove(
    Guid Id,
    string UserName,
    string Email,
    string DisplayName,
    bool Admin,
    string? Dn,
    string? NewUserName,
    string? NewDisplayName,
    bool? NewAdmin,
    string? Refusal);

/// <summary>
/// Moves people who have a local account to directory sign-in: only the password check moves (their local
/// password is removed and the directory's is used), as a person's first directory sign-in would make them.
/// </summary>
public sealed class DirectoryMoves(UserManager<AppUser> users, ILdapDirectory ldap, SignInService signIn, Audit audit)
{
    /// <summary>What moving each of these people would do, finding each in the directory by their email, then by their username.</summary>
    public async Task<List<DirectoryMove>> PlanAsync(IReadOnlyList<Guid> ids, AppUser actor, CancellationToken ct = default) =>
        [.. (await PlanWithEntriesAsync(ids, actor, ct)).Select(p => p.Plan)];

    private async Task<List<(DirectoryMove Plan, LdapPerson? Person)>> PlanWithEntriesAsync(IReadOnlyList<Guid> ids, AppUser actor, CancellationToken ct)
    {
        var people = await users.Users.Where(u => ids.Contains(u.Id)).OrderBy(u => u.UserName).ToListAsync(ct);
        var local = people.Where(u => u.Source == UserSource.Local).ToList();
        var found = local.Count == 0 ? [] : await ldap.FindAsync([.. local.SelectMany(u => new[] { u.Email ?? "", u.UserName ?? "" })], ct);
        // A directory never takes the last local admin: they are how anyone signs in while it cannot be reached.
        var localAdmins = (await users.GetUsersInRoleAsync(Roles.Admin)).Where(u => u.Source == UserSource.Local && !u.IsDisabled).Select(u => u.Id).ToHashSet();
        var plans = new List<(DirectoryMove Plan, LdapPerson? Person)>();
        foreach (var user in people)
        {
            var at = local.IndexOf(user);
            var (plan, person) = await PlanOneAsync(user, actor, at < 0 ? [] : [.. found[2 * at], .. found[2 * at + 1]]);
            if (plan.Refusal is null && localAdmins.Contains(user.Id) && localAdmins.All(id => id == user.Id || plans.Any(p => p.Plan.Id == id && p.Plan.Refusal is null)))
            {
                (plan, person) = (plan with { Refusal = "the last local admin: keep one, so an admin can sign in while the directory cannot be reached" }, null);
            }
            plans.Add((plan, person));
        }
        return plans;
    }

    private async Task<(DirectoryMove Plan, LdapPerson? Person)> PlanOneAsync(AppUser user, AppUser actor, IReadOnlyList<LdapPerson> found)
    {
        var email = user.Email ?? "";
        var admin = await users.IsInRoleAsync(user, Roles.Admin);
        (DirectoryMove, LdapPerson?) Refused(string why) => (new(user.Id, user.UserName!, email, user.DisplayName, admin, null, null, null, null, why), null);
        if (user.Source != UserSource.Local)
        {
            return Refused(user.Source == UserSource.Ldap ? "already signs in with the directory" : "signs in with the company's identity provider");
        }
        if (user.Id == actor.Id)
        {
            return Refused("your own account: another admin moves it");
        }
        var entries = found.DistinctBy(p => p.Dn, StringComparer.OrdinalIgnoreCase).ToList();
        var same = entries.Where(p => string.Equals(p.Email, email, StringComparison.OrdinalIgnoreCase)).ToList();
        if (same.Count > 1)
        {
            return Refused($"{same.Count} directory entries have the email {email}");
        }
        if (same.Count == 0)
        {
            return Refused(entries.FirstOrDefault() is not { } other ? "not in the directory, by email or by username"
                : other.Email is null ? $"the directory's entry {other.Dn} has no email"
                : $"the directory's entry {other.UserName} has the email {other.Email}, not {email}: their keys and spend here are under {email}, so make the two the same first");
        }
        var person = same[0];
        if (!ldap.IsAllowed(person))
        {
            return Refused("not in the directory's sign-in group");
        }
        if (await users.Users.AnyAsync(u => u.Id != user.Id && (u.LdapDn == person.Dn || u.NormalizedUserName == users.NormalizeName(person.UserName))))
        {
            return Refused($"another account here is already {person.UserName}, or has that directory entry");
        }
        var candidate = new AppUser { UserName = person.UserName, Email = email };
        foreach (var validator in users.UserValidators)
        {
            if (await validator.ValidateAsync(users, candidate) is { Succeeded: false } invalid && invalid.Errors.Any(e => !e.Code.Contains("Duplicate", StringComparison.Ordinal)))
            {
                return Refused($"the directory's username {person.UserName} cannot be one here: {string.Join(" ", invalid.Errors.Select(e => e.Description)).TrimEnd('.')}");
            }
        }
        return (new(user.Id, user.UserName!, email, user.DisplayName, admin, person.Dn,
            person.UserName == user.UserName ? null : person.UserName,
            person.DisplayName == user.DisplayName ? null : person.DisplayName,
            ldap.IsAdmin(person) == admin ? null : ldap.IsAdmin(person),
            null), person);
    }

    /// <summary>Moves those of these people who can move (planned again now), and says who moved and why the others did not.</summary>
    public async Task<(List<DirectoryMove> Moved, List<DirectoryMove> Refused)> MoveAsync(IReadOnlyList<Guid> ids, AppUser actor, CancellationToken ct = default)
    {
        var plans = await PlanWithEntriesAsync(ids, actor, ct);
        var moved = new List<DirectoryMove>();
        var refused = plans.Where(p => p.Person is null).Select(p => p.Plan).ToList();
        foreach (var (plan, person) in plans.Where(p => p.Person is not null))
        {
            var user = (await users.FindByIdAsync(plan.Id.ToString()))!;
            // Only the password check moves: no new security stamp, so their sessions and tokens go on.
            var password = user.PasswordHash;
            user.Source = UserSource.Ldap;
            user.LdapDn = person!.Dn;
            user.PasswordHash = null;
            user.AccessFailedCount = 0;
            await users.UpdateAsync(user);
            var (synced, why) = await signIn.SyncFromDirectoryAsync(person);
            if (synced is null)
            {
                (user.Source, user.LdapDn, user.PasswordHash) = (UserSource.Local, null, password);
                await users.UpdateAsync(user);
                refused.Add(plan with { Refusal = why });
                continue;
            }
            await audit.WriteAsync("person.to_directory", synced.UserName, detail: $"from the local account {plan.UserName}, as {person.Dn}");
            moved.Add(plan);
        }
        return (moved, refused);
    }
}
