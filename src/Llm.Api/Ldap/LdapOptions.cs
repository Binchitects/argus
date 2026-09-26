namespace Llm.Api.Ldap;

/// <summary>Configuration section "Ldap". LDAP is on when Url is set.</summary>
public sealed class LdapOptions
{
    /// <summary>ldap://host:389 or ldaps://host:636.</summary>
    public string? Url { get; set; }

    /// <summary>Upgrade a plain ldap:// connection with StartTLS before binding.</summary>
    public bool StartTls { get; set; }

    /// <summary>Accept any server certificate. For testing only; the app logs a warning when it is on.</summary>
    public bool IgnoreCertificateErrors { get; set; }

    /// <summary>A read-only service account used to find people and their groups.</summary>
    public string? BindDn { get; set; }
    public string? BindPassword { get; set; }

    public string UserBaseDn { get; set; } = "";

    /// <summary>{0} is the escaped sign-in name. Matches OpenLDAP (uid) and Active Directory (sAMAccountName) alike.</summary>
    public string UserFilter { get; set; } = "(&(|(objectClass=person)(objectClass=inetOrgPerson))(|(uid={0})(sAMAccountName={0})(mail={0})))";

    /// <summary>Tried in order; the first present becomes the username.</summary>
    public string UserNameAttributes { get; set; } = "uid,sAMAccountName";
    public string EmailAttribute { get; set; } = "mail";
    public string DisplayNameAttributes { get; set; } = "displayName,cn";

    /// <summary>When set, groups are also searched here (for directories without memberOf).</summary>
    public string? GroupBaseDn { get; set; }

    /// <summary>{0} is the escaped DN of the person.</summary>
    public string GroupFilter { get; set; } = "(&(|(objectClass=groupOfNames)(objectClass=groupOfUniqueNames)(objectClass=group))(|(member={0})(uniqueMember={0})))";

    /// <summary>Members are admins. A group CN ("llm-admins") or a full DN.</summary>
    public string? AdminGroup { get; set; }

    /// <summary>When set, only members may sign in; the sync disables people who leave it.</summary>
    public string? RequiredGroup { get; set; }

    public TimeSpan SyncInterval { get; set; } = TimeSpan.FromMinutes(15);

    public bool Enabled => !string.IsNullOrWhiteSpace(Url);
}
