using Microsoft.AspNetCore.Identity;

namespace Llm.Core.Identity;

public enum UserSource
{
    Local = 0,
    Ldap = 1,
}

/// <summary>
/// A person. UserName is the sign-in name, and for Argus it must equal their
/// GitLab username. Email is how LiteLLM knows them (their spend is under it).
/// </summary>
public sealed class AppUser : IdentityUser<Guid>
{
    public string DisplayName { get; set; } = "";
    public UserSource Source { get; set; }
    /// <summary>For LDAP people: their entry, so the sync can find them again.</summary>
    public string? LdapDn { get; set; }
    public bool IsDisabled { get; set; }
    /// <summary>"admin" or "ldap": the directory sync only re-enables people it disabled itself.</summary>
    public string? DisabledReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastSignInAt { get; set; }
    /// <summary>For directory people: the groups the directory lists (DNs), as of the last sign-in or sync.</summary>
    public List<string> DirectoryGroups { get; set; } = [];
}

public sealed class AppRole : IdentityRole<Guid>
{
    public AppRole() { }

    public AppRole(string name) : base(name) { }
}

public static class Roles
{
    public const string Admin = "admin";
    public const string Member = "member";
    public static readonly string[] All = [Admin, Member];
}

/// <summary>Who did what, when, from where. Written for sign-ins and every admin action.</summary>
public sealed class AuditEvent
{
    public long Id { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    public Guid? ActorId { get; set; }
    public string? Actor { get; set; }
    public required string Action { get; set; }
    public string? Target { get; set; }
    public bool Success { get; set; } = true;
    public string? Ip { get; set; }
    public string? Detail { get; set; }
}
