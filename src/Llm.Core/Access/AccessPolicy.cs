namespace Llm.Core.Access;

/// <summary>
/// Who is asking, as access sees them: their groups (app groups, and directory groups linked to one),
/// whether they are disabled, and why they hold Owner whatever the data says (the bootstrap admin,
/// a directory or company sign-in admin group).
/// </summary>
public sealed record Principal(Guid UserId, bool Disabled, IReadOnlySet<Guid> Groups, IReadOnlyList<string> OwnerBecause)
{
    public static Principal Of(Guid userId, IEnumerable<Guid>? groups = null, bool disabled = false, IEnumerable<string>? ownerBecause = null) =>
        new(userId, disabled, (groups ?? []).ToHashSet(), [.. ownerBecause ?? []]);
}

/// <summary>A yes or a no, and why, in the order the check found it.</summary>
public sealed record Decision(bool Allowed, IReadOnlyList<string> Because)
{
    public static Decision Yes(params string[] because) => new(true, because);

    public static Decision No(params string[] because) => new(false, because);
}

/// <summary>A role a person holds, how they hold it, and what narrows it.</summary>
/// <param name="Level">Its precedence: 0 the person's own, 1 through a group, 3 through everyone.</param>
public sealed record HeldRole(AccessRole Role, int Level, string Via, AccessScope? Scope, Guid? AssignmentId);

/// <summary>
/// Every role, assignment and rule as of one version, and the one check that decides on them
/// (docs/design/v5.9-code-review-access.md, 11.2). Read-only and shared between requests.
/// </summary>
/// <param name="Names">Names of the groups, people and roles the data mentions, for the reasons; an id without one is shown as it is.</param>
public sealed class AccessSnapshot(long version, IEnumerable<AccessRole> roles, IEnumerable<AccessAssignment> assignments, IEnumerable<AccessRule> rules,
    IReadOnlyDictionary<Guid, string>? names = null)
{
    public const string EveryoneLabel = "everyone";

    public long Version { get; } = version;
    public IReadOnlyList<AccessRole> Roles { get; } = [.. roles];
    public IReadOnlyList<AccessAssignment> Assignments { get; } = [.. assignments];
    public IReadOnlyList<AccessRule> Rules { get; } = [.. rules];

    private readonly IReadOnlyDictionary<Guid, string> _names = names ?? new Dictionary<Guid, string>();
    private Dictionary<Guid, AccessRole>? _rolesById;
    private Dictionary<Guid, AccessRole> RolesById => _rolesById ??= Roles.ToDictionary(r => r.Id);

    public static AccessSnapshot Empty { get; } = new(0, [], [], []);

    public AccessRole? Owner => Roles.FirstOrDefault(r => r.Key == BuiltInRoles.Owner);

    public string NameOf(Guid id) => _names.TryGetValue(id, out var name) ? name : RolesById.TryGetValue(id, out var role) ? role.Name : id.ToString();

    /// <summary>The subject in words: "person Sara", "group ML team", "role Billing", "everyone".</summary>
    public string Describe(SubjectKind kind, Guid? id) => kind switch
    {
        SubjectKind.Person => "person " + NameOf(id ?? Guid.Empty),
        SubjectKind.Group => "group " + NameOf(id ?? Guid.Empty),
        SubjectKind.Role => "role " + NameOf(id ?? Guid.Empty),
        _ => EveryoneLabel,
    };

    private static bool Live(DateTimeOffset? expires, DateTimeOffset now) => expires is null || expires > now;

    /// <summary>The roles the person holds now, most specific first.</summary>
    public IReadOnlyList<HeldRole> RolesOf(Principal p, DateTimeOffset now)
    {
        var held = new List<HeldRole>();
        if (Owner is { } owner)
        {
            held.AddRange(p.OwnerBecause.Select(why => new HeldRole(owner, 0, why, null, null)));
        }
        foreach (var a in Assignments)
        {
            if (!Live(a.ExpiresAt, now) || !RolesById.TryGetValue(a.RoleId, out var role))
            {
                continue;
            }
            var level = a.SubjectKind switch
            {
                SubjectKind.Person when a.SubjectId == p.UserId => 0,
                SubjectKind.Group when a.SubjectId is { } g && p.Groups.Contains(g) => 1,
                SubjectKind.Everyone => 3,
                _ => -1,
            };
            if (level >= 0)
            {
                held.Add(new HeldRole(role, level, Describe(a.SubjectKind, a.SubjectId), a.Scope is { IsEmpty: false } s ? s : null, a.Id));
            }
        }
        return [.. held.OrderBy(h => h.Level)];
    }

    public bool IsOwner(Principal p, DateTimeOffset now) => !p.Disabled && RolesOf(p, now).Any(h => h.Role.Key == BuiltInRoles.Owner && h.Scope is null);

    /// <summary>Whether the person holds a permission.</summary>
    public Decision Check(Principal p, string permission, DateTimeOffset now)
    {
        if (p.Disabled)
        {
            return Decision.No("account disabled");
        }
        var held = RolesOf(p, now);
        var feature = Permissions.IsFeature(permission);
        var personDeny = feature ? Matching(p, held, ResourceKinds.Feature, permission, ResourceKinds.Use, now).FirstOrDefault(m => m.Rule is { Effect: RuleEffect.Deny } && m.Level == 0) : null;
        if (held.FirstOrDefault(h => h.Role.Key == BuiltInRoles.Owner && h.Scope is null) is { } owner)
        {
            return personDeny is not null
                ? Decision.No($"{Describe(personDeny)} overrides role Owner")
                : Decision.Yes($"role Owner via {owner.Via}");
        }
        var found = new List<Match>();
        foreach (var h in held.Where(h => h.Role.Permissions.Contains(permission)))
        {
            found.Add(new Match(h.Level, 0, 0, null, h));
        }
        if (feature)
        {
            found.AddRange(Matching(p, held, ResourceKinds.Feature, permission, ResourceKinds.Use, now));
        }
        if (found.Count == 0)
        {
            return Decision.No(feature ? "no role or rule gives it" : "no role gives it");
        }
        return Decide(found);
    }

    /// <summary>The scopes a permission comes with: null when one of the person's roles gives it unscoped (or they are Owner); otherwise each scope it is held under.</summary>
    public IReadOnlyList<AccessScope>? ScopesOf(Principal p, string permission, DateTimeOffset now)
    {
        if (p.Disabled)
        {
            return [];
        }
        var giving = RolesOf(p, now).Where(h => h.Role.Key == BuiltInRoles.Owner || h.Role.Permissions.Contains(permission)).ToList();
        if (giving.Any(h => h.Scope is null) || (Permissions.IsFeature(permission) && Check(p, permission, now).Allowed))
        {
            return null;
        }
        return [.. giving.Select(h => h.Scope!)];
    }

    /// <summary>Whether the person may do an action to a resource (a model, a tool, a knowledge source, a repository).</summary>
    /// <param name="openWhenUnnamed">A resource no allow rule names is open to everyone (models, tools and knowledge sources were, before anyone chose who may use them); otherwise closed.</param>
    public Decision Check(Principal p, string kind, string resource, string action, DateTimeOffset now, bool openWhenUnnamed = true)
    {
        if (p.Disabled)
        {
            return Decision.No("account disabled");
        }
        var held = RolesOf(p, now);
        var found = Matching(p, held, kind, resource, action, now);
        if (held.FirstOrDefault(h => h.Role.Key == BuiltInRoles.Owner && h.Scope is null) is { } owner)
        {
            return found.FirstOrDefault(m => m.Rule is { Effect: RuleEffect.Deny } && m.Level == 0) is { } deny
                ? Decision.No($"{Describe(deny)} overrides role Owner")
                : Decision.Yes($"role Owner via {owner.Via}");
        }
        if (openWhenUnnamed && !Named(kind, resource, action, now))
        {
            found.Add(new Match(3, 2, 0, null, null));
        }
        if (found.Count == 0)
        {
            return Decision.No($"no rule lets them {action} it");
        }
        return Decide(found);
    }

    /// <summary>Whether an allow rule, for anyone, names the resource: then only those it names may use it.</summary>
    public bool Named(string kind, string resource, string action, DateTimeOffset now) =>
        Rules.Any(r => r.Effect == RuleEffect.Allow && r.ResourceKind == kind && r.Action == action && Live(r.ExpiresAt, now) && Pattern.Matches(r.Resource, resource));

    /// <summary>A role or a rule that matched, ranked: subject level, then exact over pattern, then a longer pattern over a shorter.</summary>
    private sealed record Match(int Level, int Exactness, int Length, AccessRule? Rule, HeldRole? Role)
    {
        public bool Allows => Rule is null || Rule.Effect == RuleEffect.Allow;
    }

    private List<Match> Matching(Principal p, IReadOnlyList<HeldRole> held, string kind, string resource, string action, DateTimeOffset now)
    {
        var roles = held.Select(h => h.Role.Id).ToHashSet();
        var found = new List<Match>();
        foreach (var r in Rules)
        {
            if (r.ResourceKind != kind || r.Action != action || !Live(r.ExpiresAt, now) || !Pattern.Matches(r.Resource, resource))
            {
                continue;
            }
            var level = r.SubjectKind switch
            {
                SubjectKind.Person when r.SubjectId == p.UserId => 0,
                SubjectKind.Group when r.SubjectId is { } g && p.Groups.Contains(g) => 1,
                SubjectKind.Role when r.SubjectId is { } id && roles.Contains(id) => 2,
                SubjectKind.Everyone => 3,
                _ => -1,
            };
            if (level >= 0)
            {
                var exact = Pattern.IsPattern(r.Resource) ? 1 : 0;
                found.Add(new Match(level, exact, -r.Resource.Length, r, null));
            }
        }
        return found;
    }

    /// <summary>At the most specific level that has any match, a deny wins over an allow; less specific levels do not count.</summary>
    private Decision Decide(List<Match> found)
    {
        var top = found.OrderBy(m => m.Level).ThenBy(m => m.Exactness).ThenBy(m => m.Length).First();
        var same = found.Where(m => m.Level == top.Level && m.Exactness == top.Exactness && m.Length == top.Length).ToList();
        var deny = same.FirstOrDefault(m => !m.Allows);
        var chosen = deny ?? same[0];
        var because = new List<string> { Describe(chosen) };
        because.AddRange(found.Where(m => m != chosen && !same.Contains(m)).OrderBy(m => m.Level).Select(m => "overridden: " + Describe(m)));
        return new Decision(deny is null, because);
    }

    private string Describe(Match m) => m switch
    {
        { Role: { } h } => $"role {h.Role.Name} via {h.Via}",
        { Rule: { } r } => $"{(r.Effect == RuleEffect.Allow ? "allow" : "deny")} for {Describe(r.SubjectKind, r.SubjectId)} on {r.ResourceKind} {r.Resource}" +
            (string.IsNullOrWhiteSpace(r.Note) ? "" : $" ({r.Note.Trim()})"),
        _ => "open to everyone: no rule names it",
    };
}

/// <summary>Resource patterns: * stands for anything but a slash, ** for anything; letters in any case.</summary>
public static class Pattern
{
    public static bool IsPattern(string pattern) => pattern.Contains('*', StringComparison.Ordinal);

    public static bool Matches(string pattern, string value)
    {
        if (!IsPattern(pattern))
        {
            return string.Equals(pattern, value, StringComparison.OrdinalIgnoreCase);
        }
        return Match(pattern.AsSpan(), value.AsSpan());
    }

    private static bool Match(ReadOnlySpan<char> p, ReadOnlySpan<char> v)
    {
        while (p.Length > 0)
        {
            if (p[0] == '*')
            {
                var any = p.Length > 1 && p[1] == '*';
                var rest = p[(any ? 2 : 1)..];
                for (var i = 0; i <= v.Length; i++)
                {
                    if (Match(rest, v[i..]))
                    {
                        return true;
                    }
                    if (i < v.Length && !any && v[i] == '/')
                    {
                        return false;
                    }
                }
                return false;
            }
            if (v.Length == 0 || char.ToUpperInvariant(p[0]) != char.ToUpperInvariant(v[0]))
            {
                return false;
            }
            p = p[1..];
            v = v[1..];
        }
        return v.Length == 0;
    }
}
