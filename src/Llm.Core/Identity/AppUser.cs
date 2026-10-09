using Microsoft.AspNetCore.Identity;

namespace Llm.Core.Identity;

public enum UserSource
{
    Local = 0,
    Ldap = 1,
    /// <summary>Company sign-in (an OIDC or SAML identity provider), or made by its SCIM provisioning.</summary>
    Oidc = 2,
}

public static class UserSources
{
    /// <summary>The name the API and the pages use: local, ldap or oidc.</summary>
    public static string Name(UserSource source) => source switch
    {
        UserSource.Ldap => "ldap",
        UserSource.Oidc => "oidc",
        _ => "local",
    };
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
    /// <summary>For company sign-in people: the identity provider's subject (OIDC sub, or "saml:" and the SAML NameID), set at their first sign-in.</summary>
    public string? OidcSubject { get; set; }
    /// <summary>For people SCIM made or manages: the identity provider's own id for them (externalId).</summary>
    public string? ScimExternalId { get; set; }
    public bool IsDisabled { get; set; }
    /// <summary>"admin", "ldap", "oidc" or "scim": each source only re-enables people it disabled itself.</summary>
    public string? DisabledReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastSignInAt { get; set; }
    /// <summary>For directory and company sign-in people: the groups the directory or the identity provider lists, as of the last sign-in or sync.</summary>
    public List<string> DirectoryGroups { get; set; } = [];
    /// <summary>How long answers should be: "short", "thorough", or null for the model's own judgement.</summary>
    public string? AnswerLength { get; set; }
    /// <summary>The person turned memory off: their answers neither read nor offer memories (Your account → Memory).</summary>
    public bool MemoryOff { get; set; }
    /// <summary>Set while the person is on legal hold: nothing of theirs is deleted, by retention or by them.</summary>
    public DateTimeOffset? LegalHoldSince { get; set; }
    /// <summary>Why the hold was placed (a matter, a ticket), as the admin wrote it.</summary>
    public string? LegalHoldReason { get; set; }
    /// <summary>Their API key's repeated requests are answered from the answer cache (when an admin lets people choose).</summary>
    public bool CacheApiAnswers { get; set; }
    /// <summary>Their speech choices (Your account → Voice) as JSON: the language they speak, a voice per language, the speed, reading aloud. Null: the company's.</summary>
    public string? Voice { get; set; }
    /// <summary>Requests a minute each of their API keys may send to the gateway, their own; null: their groups' or the company's, 0: no limit.</summary>
    public int? RequestsPerMinute { get; set; }
    /// <summary>Tokens a minute each of their API keys may use at the gateway, their own; null: their groups' or the company's, 0: no limit.</summary>
    public int? TokensPerMinute { get; set; }
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
