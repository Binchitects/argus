using System.Text.Json.Serialization;

namespace Llm.Core.Access;

/// <summary>Whom an assignment or a rule is for. The order is the order of precedence: a person's own rule wins over their groups', and so on.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SubjectKind>))]
public enum SubjectKind
{
    Person = 0,
    /// <summary>An app group, a directory group linked to one, or a SCIM group.</summary>
    Group = 1,
    /// <summary>Whoever holds the role (rules only).</summary>
    Role = 2,
    /// <summary>Every active person.</summary>
    Everyone = 3,
}

[JsonConverter(typeof(JsonStringEnumConverter<RuleEffect>))]
public enum RuleEffect
{
    Allow = 0,
    Deny = 1,
}

/// <summary>The kinds of resource a rule names, and the actions on each.</summary>
public static class ResourceKinds
{
    public const string Model = "model";
    public const string Tool = "tool";
    public const string KnowledgeSource = "knowledge_source";
    /// <summary>A repository, as "host:path" or a pattern ("gitlab:team-a/**").</summary>
    public const string Repo = "repo";
    /// <summary>A feature permission, given or taken per person, group or role.</summary>
    public const string Feature = "feature";

    public const string Use = "use";
    public const string Read = "read";

    /// <summary>What can be done to each kind: the first is what the "Who may use it" pickers write.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Actions = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        [Model] = [Use],
        [Tool] = [Use],
        [KnowledgeSource] = [Read],
        [Repo] = [Permissions.CodeRead, Permissions.ReviewRun, "review.see", "review.resolve"],
        [Feature] = [Use],
    };
}

/// <summary>A named set of permissions. The built-in ones (<see cref="BuiltInRoles"/>) are fixed; custom ones are made under Admin → Access.</summary>
public sealed class AccessRole
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    /// <summary>The built-in role's key ("owner", "member"), or a custom role's own ("custom-…").</summary>
    public required string Key { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public bool BuiltIn { get; set; }
    public List<string> Permissions { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Narrows what a role gives: a people admin or billing role to people in some groups, an Argus admin to some repositories.</summary>
public sealed class AccessScope
{
    /// <summary>The groups whose members (and the groups themselves) the role covers; empty: no narrowing by group.</summary>
    public List<Guid> Groups { get; set; } = [];
    /// <summary>Repository path patterns the role covers ("team-a/**"); empty: no narrowing by repository.</summary>
    public List<string> Repos { get; set; } = [];

    [JsonIgnore]
    public bool IsEmpty => Groups.Count == 0 && Repos.Count == 0;
}

/// <summary>A role given to a person, a group or everyone, maybe narrowed (<see cref="Scope"/>) and maybe until a time.</summary>
public sealed class AccessAssignment
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid RoleId { get; set; }
    /// <summary>Person, Group or Everyone.</summary>
    public SubjectKind SubjectKind { get; set; }
    /// <summary>The person's or the group's id; null for everyone.</summary>
    public Guid? SubjectId { get; set; }
    public AccessScope? Scope { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public Guid? CreatedById { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Allows or denies one action on a resource (or a pattern of them) to a person, a group, a role's holders or everyone.</summary>
public sealed class AccessRule
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public RuleEffect Effect { get; set; }
    public SubjectKind SubjectKind { get; set; }
    /// <summary>The person's, group's or role's id; null for everyone.</summary>
    public Guid? SubjectId { get; set; }
    /// <summary>One of <see cref="ResourceKinds"/>.</summary>
    public required string ResourceKind { get; set; }
    /// <summary>The resource's id or name, or a pattern: * stands for anything but a slash, ** for anything.</summary>
    public required string Resource { get; set; }
    public required string Action { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    /// <summary>Why, as the admin wrote it.</summary>
    public string? Note { get; set; }
    public Guid? CreatedById { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>One change to roles, assignments or rules, as it was before and after (JSON): the access history. Never changed or deleted.</summary>
public sealed class AccessChange
{
    public long Id { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    public Guid? ActorId { get; set; }
    /// <summary>"role", "assignment" or "rule".</summary>
    public required string Kind { get; set; }
    /// <summary>The row before (null when it was made) and after (null when it was removed), as JSON.</summary>
    public string? Before { get; set; }
    public string? After { get; set; }
    /// <summary>The changes made together (a bulk change, an import) share it.</summary>
    public Guid? BulkId { get; set; }
}

/// <summary>The one row that says which version of the access data is current: each change raises it, and every replica reads it again.</summary>
public sealed class AccessMeta
{
    public int Id { get; set; } = 1;
    public long Version { get; set; }
}
