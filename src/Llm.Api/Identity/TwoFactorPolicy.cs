using Llm.Api.Access;
using Llm.Core.Access;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Identity;

/// <summary>
/// Who must sign in with a second factor (Auth:TwoFactorRequired, and groups that require it). A person it covers who has none
/// is signed in with <see cref="EnrolClaim"/>: until they set one up, the app answers only what setting it up needs.
/// </summary>
public sealed class TwoFactorPolicy(AppDbContext db, AccessService access, IOptionsMonitor<AuthOptions> auth)
{
    public const string Off = "off", Admins = "admins", Everyone = "everyone";

    /// <summary>On a session that must set up two-factor sign-in before anything else.</summary>
    public const string EnrolClaim = "enrol_2fa";

    /// <summary>Why this person must sign in with a second factor, in words; null when they need not.</summary>
    public async Task<string?> RequiredAsync(AppUser user, CancellationToken ct = default)
    {
        var policy = auth.CurrentValue.TwoFactorRequired;
        if (policy == Everyone)
        {
            return "the company requires it of everyone";
        }
        var member = await access.MembershipAsync(user, ct);
        if (policy == Admins && Permissions.AdminKeys.Any(member.Has))
        {
            return "the company requires it of admins";
        }
        var groups = member.Groups.ToList();
        var group = await db.Groups.AsNoTracking().Where(g => groups.Contains(g.Id) && g.RequireTwoFactor).OrderBy(g => g.Name).Select(g => g.Name).FirstOrDefaultAsync(ct);
        return group is null ? null : $"the group {group} requires it";
    }
}
