using System.Security.Claims;
using Llm.Api.Identity;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Company;

/// <summary>How a company sign-in ended. The sign-in page words each one (login?error=company_...).</summary>
public enum CompanyOutcome
{
    Success,
    /// <summary>Not in the required group.</summary>
    NotAllowed,
    Disabled,
    /// <summary>The provider's account cannot be a person here: no email, a name someone else has, a local admin...</summary>
    Refused,
}

/// <summary>
/// Signs in the person the identity provider vouched for: made on their first
/// sign-in, matched by email to someone already here, kept current every time.
/// Two-factor sign-in is the provider's business.
/// </summary>
public sealed class CompanySignIn(
    AppDbContext db,
    UserManager<AppUser> users,
    SignInManager<AppUser> signIn,
    PeopleService people,
    CompanyPeople company,
    Audit audit,
    IOptionsMonitor<CompanySignInOptions> options)
{
    public const string Amr = "oidc";
    public const string SamlAmr = "saml";

    /// <summary>The audit log's words for a company sign-in, with the protocol it came by.</summary>
    public static string Detail(bool saml) => saml ? "company sign-in (SAML)" : "company sign-in";

    /// <summary>Two sign-ins of one person at once both update their row; the loser retries on fresh data.</summary>
    public async Task<CompanyOutcome> SignInAsync(CompanyPerson person, bool remember)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await SignInOnceAsync(person, remember);
            }
            catch (DbUpdateConcurrencyException) when (attempt < 5)
            {
                db.ChangeTracker.Clear();
            }
        }
    }

    private async Task<CompanyOutcome> SignInOnceAsync(CompanyPerson person, bool remember)
    {
        var o = options.CurrentValue;
        var who = person.UserName ?? person.Email ?? person.Subject;
        var userName = CompanyPeople.UserName(person.UserName);
        if (userName is null)
        {
            var source = !person.Saml ? $"the {o.UserNameClaim} claim"
                : string.IsNullOrWhiteSpace(o.SamlUserNameAttribute) ? "the NameID" : $"the {o.SamlUserNameAttribute.Trim()} attribute";
            return await FailAsync(person, who, CompanyOutcome.Refused, $"{source} (\"{person.UserName}\") cannot be a username here");
        }
        var email = CompanyPeople.Email(person.Email) ?? CompanyPeople.Email(person.UserName);
        if (email is null)
        {
            return await FailAsync(person, userName, CompanyOutcome.Refused, "the company account has no email address");
        }
        if (person.EmailVerified == false)
        {
            return await FailAsync(person, userName, CompanyOutcome.Refused, $"the identity provider has not verified {email}");
        }

        var user = await company.FindBySubjectAsync(person.Subject) ?? await users.FindByEmailAsync(email);
        // The groups SCIM put them in count too: Entra ID's tokens name groups by ID, its SCIM by name.
        var groups = person.Groups.ToList();
        if (user is not null)
        {
            groups.AddRange(await db.GroupMembers.Where(m => m.UserId == user.Id)
                .Join(db.Groups.Where(g => g.Scim), m => m.GroupId, g => g.Id, (_, g) => g.Name).ToListAsync());
        }
        if (!CompanyPeople.InGroup(groups, o.RequiredGroup) && !string.IsNullOrWhiteSpace(o.RequiredGroup))
        {
            // The provider says they may not: someone already here is disabled until it says otherwise.
            if (user is { Source: UserSource.Oidc, IsDisabled: false })
            {
                try
                {
                    await people.SetDisabledAsync(PeopleService.DirectorySync, user, disabled: true, reason: "oidc");
                }
                catch (PeopleException)
                {
                    // The last admin stays enabled; they are still refused here.
                }
            }
            return await FailAsync(person, userName, CompanyOutcome.NotAllowed, $"not in the sign-in group {o.RequiredGroup}");
        }

        try
        {
            if (user is null)
            {
                user = await company.CreateAsync(userName, email, person.DisplayName, "from company sign-in", u =>
                {
                    u.OidcSubject = person.Subject;
                    u.DirectoryGroups = [.. person.Groups.Order(StringComparer.OrdinalIgnoreCase)];
                });
            }
            else
            {
                if (user.IsDisabled && user.DisabledReason != "oidc")
                {
                    return await FailAsync(person, userName, CompanyOutcome.Disabled, "disabled");
                }
                if (user.OidcSubject != person.Subject)
                {
                    await company.AdoptAsync(user, "matched by email at company sign-in");
                    user.OidcSubject = person.Subject;
                    await users.UpdateAsync(user);
                }
                if (user.IsDisabled)
                {
                    // Back in the sign-in group: the same path as an admin enabling them, so their keys are unblocked too.
                    await people.SetDisabledAsync(PeopleService.DirectorySync, user, disabled: false, reason: "oidc");
                }
                await company.UpdateAsync(user, userName, email, person.DisplayName);
                await company.SetGroupsAsync(user, person.Groups);
            }
            await company.SetRoleAsync(user, CompanyPeople.InGroup(groups, o.AdminGroup), "from company sign-in");
        }
        catch (CompanyRefusedException ex)
        {
            return await FailAsync(person, userName, CompanyOutcome.Refused, ex.Message);
        }

        await signIn.SignInWithClaimsAsync(user, remember, [new Claim("amr", person.Saml ? SamlAmr : Amr)]);
        var now = DateTimeOffset.UtcNow;
        await db.Users.Where(u => u.Id == user.Id).ExecuteUpdateAsync(set => set.SetProperty(u => u.LastSignInAt, now));
        await audit.WriteAsync("sign_in", user.UserName, detail: Detail(person.Saml), actor: user);
        return CompanyOutcome.Success;
    }

    private async Task<CompanyOutcome> FailAsync(CompanyPerson person, string who, CompanyOutcome outcome, string detail)
    {
        await audit.WriteAsync("sign_in", who, success: false, detail: Detail(person.Saml) + ": " + detail);
        return outcome;
    }
}
