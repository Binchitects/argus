using Llm.Core.Access;

namespace Llm.Tests;

/// <summary>
/// The one access check, row by row: a person's rule over their groups', a group's over a role's, a role's
/// over everyone's; deny over allow within a level; exact over pattern; expiry; Owner; ceilings.
/// </summary>
public sealed class AccessPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Sara = Guid.Parse("00000000-0000-0000-0000-00000000a001");
    private static readonly Guid Omid = Guid.Parse("00000000-0000-0000-0000-00000000a002");
    private static readonly Guid Ml = Guid.Parse("00000000-0000-0000-0000-00000000b001");
    private static readonly Guid Ops = Guid.Parse("00000000-0000-0000-0000-00000000b002");

    private static readonly Dictionary<string, AccessRole> Roles = BuiltInRoles.All.ToDictionary(r => r.Key, r => new AccessRole
    {
        Key = r.Key, Name = r.Name, BuiltIn = true, Permissions = [.. r.Permissions],
    });

    private static AccessAssignment Assign(string role, SubjectKind kind, Guid? id, DateTimeOffset? expires = null, AccessScope? scope = null) =>
        new() { RoleId = Roles[role].Id, SubjectKind = kind, SubjectId = id, ExpiresAt = expires, Scope = scope };

    private static AccessRule Rule(RuleEffect effect, SubjectKind kind, Guid? id, string resourceKind, string resource, string? action = null, DateTimeOffset? expires = null) =>
        new() { Effect = effect, SubjectKind = kind, SubjectId = id, ResourceKind = resourceKind, Resource = resource, Action = action ?? ResourceKinds.Actions[resourceKind][0], ExpiresAt = expires };

    private static AccessSnapshot World(params object[] rows) => new(1, Roles.Values,
        [Assign(BuiltInRoles.Member, SubjectKind.Everyone, null), .. rows.OfType<AccessAssignment>()], rows.OfType<AccessRule>());

    private static Principal SaraIn(params Guid[] groups) => Principal.Of(Sara, groups);

    public static TheoryData<string> Names => [.. Cases.Keys];

    private static readonly Dictionary<string, (Func<bool> Actual, bool Expected)> Cases = new()
    {
        // Feature permissions: everyone's Member role is the default.
        ["everyone holds the features"] = (() => World().Check(SaraIn(), Permissions.Chat, Now).Allowed, true),
        ["nobody holds an admin permission by default"] = (() => World().Check(SaraIn(), Permissions.ModelsManage, Now).Allowed, false),
        ["a person deny takes a feature"] = (() => World(Rule(RuleEffect.Deny, SubjectKind.Person, Sara, ResourceKinds.Feature, Permissions.ApiKeys))
            .Check(SaraIn(), Permissions.ApiKeys, Now).Allowed, false),
        ["a person deny takes only their feature"] = (() => World(Rule(RuleEffect.Deny, SubjectKind.Person, Omid, ResourceKinds.Feature, Permissions.ApiKeys))
            .Check(SaraIn(), Permissions.ApiKeys, Now).Allowed, true),
        ["a group deny takes a feature from its members"] = (() => World(Rule(RuleEffect.Deny, SubjectKind.Group, Ml, ResourceKinds.Feature, Permissions.WebSearch))
            .Check(SaraIn(Ml), Permissions.WebSearch, Now).Allowed, false),
        ["a group deny leaves others"] = (() => World(Rule(RuleEffect.Deny, SubjectKind.Group, Ml, ResourceKinds.Feature, Permissions.WebSearch))
            .Check(SaraIn(Ops), Permissions.WebSearch, Now).Allowed, true),
        ["a person allow opens what their group's deny closes"] = (() => World(
            Rule(RuleEffect.Deny, SubjectKind.Group, Ml, ResourceKinds.Feature, Permissions.WebSearch),
            Rule(RuleEffect.Allow, SubjectKind.Person, Sara, ResourceKinds.Feature, Permissions.WebSearch)).Check(SaraIn(Ml), Permissions.WebSearch, Now).Allowed, true),
        ["deny wins within a level"] = (() => World(
            Rule(RuleEffect.Deny, SubjectKind.Group, Ml, ResourceKinds.Feature, Permissions.WebSearch),
            Rule(RuleEffect.Allow, SubjectKind.Group, Ops, ResourceKinds.Feature, Permissions.WebSearch)).Check(SaraIn(Ml, Ops), Permissions.WebSearch, Now).Allowed, false),
        ["a role's deny wins over everyone's role"] = (() => World(
            Assign(BuiltInRoles.Billing, SubjectKind.Person, Sara),
            Rule(RuleEffect.Deny, SubjectKind.Role, Roles[BuiltInRoles.Billing].Id, ResourceKinds.Feature, Permissions.Pictures)).Check(SaraIn(), Permissions.Pictures, Now).Allowed, false),
        ["a group allow wins over a role deny"] = (() => World(
            Assign(BuiltInRoles.Billing, SubjectKind.Person, Sara),
            Rule(RuleEffect.Deny, SubjectKind.Role, Roles[BuiltInRoles.Billing].Id, ResourceKinds.Feature, Permissions.Pictures),
            Rule(RuleEffect.Allow, SubjectKind.Group, Ml, ResourceKinds.Feature, Permissions.Pictures)).Check(SaraIn(Ml), Permissions.Pictures, Now).Allowed, true),
        ["a feature pattern applies"] = (() => World(Rule(RuleEffect.Deny, SubjectKind.Person, Sara, ResourceKinds.Feature, "feature.*"))
            .Check(SaraIn(), Permissions.Video, Now).Allowed, false),
        ["an expired deny no longer counts"] = (() => World(Rule(RuleEffect.Deny, SubjectKind.Person, Sara, ResourceKinds.Feature, Permissions.Chat, expires: Now.AddMinutes(-1)))
            .Check(SaraIn(), Permissions.Chat, Now).Allowed, true),

        // Roles and assignments.
        ["a person's role gives its permissions"] = (() => World(Assign(BuiltInRoles.ModelAdmin, SubjectKind.Person, Sara)).Check(SaraIn(), Permissions.ModelsManage, Now).Allowed, true),
        ["a role gives only its permissions"] = (() => World(Assign(BuiltInRoles.ModelAdmin, SubjectKind.Person, Sara)).Check(SaraIn(), Permissions.PeopleManage, Now).Allowed, false),
        ["a group's role reaches its members"] = (() => World(Assign(BuiltInRoles.Auditor, SubjectKind.Group, Ml)).Check(SaraIn(Ml), Permissions.AuditRead, Now).Allowed, true),
        ["a group's role skips others"] = (() => World(Assign(BuiltInRoles.Auditor, SubjectKind.Group, Ml)).Check(SaraIn(Ops), Permissions.AuditRead, Now).Allowed, false),
        ["the auditor never writes"] = (() => World(Assign(BuiltInRoles.Auditor, SubjectKind.Person, Sara)).Check(SaraIn(), Permissions.SettingsManage, Now).Allowed, false),
        ["an expired assignment no longer counts"] = (() => World(Assign(BuiltInRoles.ModelAdmin, SubjectKind.Person, Sara, Now.AddSeconds(-1)))
            .Check(SaraIn(), Permissions.ModelsManage, Now).Allowed, false),
        ["an assignment until later counts"] = (() => World(Assign(BuiltInRoles.ModelAdmin, SubjectKind.Person, Sara, Now.AddHours(1)))
            .Check(SaraIn(), Permissions.ModelsManage, Now).Allowed, true),
        ["a scoped role gives the permission, scoped"] = (() => World(Assign(BuiltInRoles.PeopleAdmin, SubjectKind.Person, Sara, scope: new AccessScope { Groups = [Ml] }))
            .ScopesOf(SaraIn(), Permissions.PeopleManage, Now) is [{ Groups: [var g] }] && g == Ml, true),
        ["an unscoped role makes the permission unscoped"] = (() => World(
            Assign(BuiltInRoles.PeopleAdmin, SubjectKind.Person, Sara, scope: new AccessScope { Groups = [Ml] }),
            Assign(BuiltInRoles.PeopleAdmin, SubjectKind.Group, Ops)).ScopesOf(SaraIn(Ops), Permissions.PeopleManage, Now) is null, true),

        // Owner.
        ["Owner holds every permission"] = (() => World(Assign(BuiltInRoles.Owner, SubjectKind.Person, Sara)).Check(SaraIn(), Permissions.AccessManage, Now).Allowed, true),
        ["Owner uses every resource"] = (() => World(Assign(BuiltInRoles.Owner, SubjectKind.Person, Sara),
            Rule(RuleEffect.Allow, SubjectKind.Group, Ml, ResourceKinds.Model, "qwen")).Check(SaraIn(), ResourceKinds.Model, "qwen", ResourceKinds.Use, Now).Allowed, true),
        ["a group deny does not take from an Owner"] = (() => World(Assign(BuiltInRoles.Owner, SubjectKind.Person, Sara),
            Rule(RuleEffect.Deny, SubjectKind.Group, Ml, ResourceKinds.Model, "qwen")).Check(SaraIn(Ml), ResourceKinds.Model, "qwen", ResourceKinds.Use, Now).Allowed, true),
        ["a person deny takes a resource from an Owner"] = (() => World(Assign(BuiltInRoles.Owner, SubjectKind.Person, Sara),
            Rule(RuleEffect.Deny, SubjectKind.Person, Sara, ResourceKinds.Model, "qwen")).Check(SaraIn(), ResourceKinds.Model, "qwen", ResourceKinds.Use, Now).Allowed, false),
        ["a person deny takes a feature from an Owner"] = (() => World(Assign(BuiltInRoles.Owner, SubjectKind.Person, Sara),
            Rule(RuleEffect.Deny, SubjectKind.Person, Sara, ResourceKinds.Feature, Permissions.ApiKeys)).Check(SaraIn(), Permissions.ApiKeys, Now).Allowed, false),
        ["the bootstrap admin is Owner whatever the data says"] = (() => World().Check(Principal.Of(Sara, ownerBecause: ["the bootstrap admin"]), Permissions.AccessManage, Now).Allowed, true),
        ["a scoped Owner is not everything"] = (() => World(Assign(BuiltInRoles.Owner, SubjectKind.Person, Sara, scope: new AccessScope { Groups = [Ml] }))
            .IsOwner(SaraIn(), Now), false),

        // Resources.
        ["a resource no rule names is open"] = (() => World().Check(SaraIn(), ResourceKinds.Tool, "web", ResourceKinds.Use, Now).Allowed, true),
        ["a resource allowed to a group is closed to others"] = (() => World(Rule(RuleEffect.Allow, SubjectKind.Group, Ml, ResourceKinds.Tool, "web"))
            .Check(SaraIn(Ops), ResourceKinds.Tool, "web", ResourceKinds.Use, Now).Allowed, false),
        ["a resource allowed to a group is open to it"] = (() => World(Rule(RuleEffect.Allow, SubjectKind.Group, Ml, ResourceKinds.Tool, "web"))
            .Check(SaraIn(Ml), ResourceKinds.Tool, "web", ResourceKinds.Use, Now).Allowed, true),
        ["a resource allowed to Owners only is closed to members"] = (() => World(Rule(RuleEffect.Allow, SubjectKind.Role, Roles[BuiltInRoles.Owner].Id, ResourceKinds.Tool, "web"))
            .Check(SaraIn(), ResourceKinds.Tool, "web", ResourceKinds.Use, Now).Allowed, false),
        ["a deny alone closes only to whom it names"] = (() => World(Rule(RuleEffect.Deny, SubjectKind.Person, Omid, ResourceKinds.Model, "qwen"))
            .Check(SaraIn(), ResourceKinds.Model, "qwen", ResourceKinds.Use, Now).Allowed, true),
        ["a group deny beats an open resource"] = (() => World(Rule(RuleEffect.Deny, SubjectKind.Group, Ml, ResourceKinds.Model, "qwen"))
            .Check(SaraIn(Ml), ResourceKinds.Model, "qwen", ResourceKinds.Use, Now).Allowed, false),
        ["an exact rule wins over a pattern at the same level"] = (() => World(
            Rule(RuleEffect.Deny, SubjectKind.Group, Ml, ResourceKinds.Repo, "gitlab:team-a/**", Permissions.CodeRead),
            Rule(RuleEffect.Allow, SubjectKind.Group, Ml, ResourceKinds.Repo, "gitlab:team-a/api", Permissions.CodeRead))
            .Check(SaraIn(Ml), ResourceKinds.Repo, "gitlab:team-a/api", Permissions.CodeRead, Now).Allowed, true),
        ["a longer pattern wins over a shorter"] = (() => World(
            Rule(RuleEffect.Allow, SubjectKind.Group, Ml, ResourceKinds.Repo, "gitlab:**", Permissions.CodeRead),
            Rule(RuleEffect.Deny, SubjectKind.Group, Ml, ResourceKinds.Repo, "gitlab:team-a/**", Permissions.CodeRead))
            .Check(SaraIn(Ml), ResourceKinds.Repo, "gitlab:team-a/api", Permissions.CodeRead, Now).Allowed, false),
        ["a person pattern wins over a group's exact rule"] = (() => World(
            Rule(RuleEffect.Deny, SubjectKind.Group, Ml, ResourceKinds.Repo, "gitlab:team-a/api", Permissions.CodeRead),
            Rule(RuleEffect.Allow, SubjectKind.Person, Sara, ResourceKinds.Repo, "gitlab:team-a/**", Permissions.CodeRead))
            .Check(SaraIn(Ml), ResourceKinds.Repo, "gitlab:team-a/api", Permissions.CodeRead, Now).Allowed, true),
        ["one star stops at a slash"] = (() => Pattern.Matches("gitlab:team-a/*", "gitlab:team-a/sub/api"), false),
        ["two stars cross slashes"] = (() => Pattern.Matches("gitlab:team-a/**", "gitlab:team-a/sub/api"), true),
        ["names match in any case"] = (() => Pattern.Matches("Qwen3*", "qwen3.8-flash"), true),
        ["an action is its own"] = (() => World(Rule(RuleEffect.Deny, SubjectKind.Person, Sara, ResourceKinds.Repo, "gitlab:x", Permissions.ReviewRun))
            .Check(SaraIn(), ResourceKinds.Repo, "gitlab:x", Permissions.CodeRead, Now).Allowed, true),
        ["a closed kind needs an allow"] = (() => World().Check(SaraIn(), ResourceKinds.Repo, "git:x", Permissions.CodeRead, Now, openWhenUnnamed: false).Allowed, false),

        // Ceilings.
        ["a disabled person is denied a feature"] = (() => World().Check(Principal.Of(Sara, disabled: true), Permissions.Chat, Now).Allowed, false),
        ["a disabled Owner is denied"] = (() => World(Assign(BuiltInRoles.Owner, SubjectKind.Person, Sara)).Check(Principal.Of(Sara, disabled: true), Permissions.AccessManage, Now).Allowed, false),
        ["a disabled person is denied an open resource"] = (() => World().Check(Principal.Of(Sara, disabled: true), ResourceKinds.Tool, "web", ResourceKinds.Use, Now).Allowed, false),
    };

    [Theory]
    [MemberData(nameof(Names))]
    public void Decides(string name)
    {
        var (actual, expected) = Cases[name];
        Assert.Equal(expected, actual());
    }

    [Fact]
    public void Says_why()
    {
        var world = World(
            Rule(RuleEffect.Deny, SubjectKind.Group, Ml, ResourceKinds.Feature, Permissions.WebSearch),
            Rule(RuleEffect.Allow, SubjectKind.Person, Sara, ResourceKinds.Feature, Permissions.WebSearch));
        var decision = world.Check(SaraIn(Ml), Permissions.WebSearch, Now);
        Assert.True(decision.Allowed);
        Assert.StartsWith("allow for person", decision.Because[0], StringComparison.Ordinal);
        Assert.Contains(decision.Because, b => b.StartsWith("overridden: deny for group", StringComparison.Ordinal));
        Assert.Contains(decision.Because, b => b.StartsWith("overridden: role Member via everyone", StringComparison.Ordinal));
        Assert.Equal(["account disabled"], world.Check(Principal.Of(Sara, disabled: true), Permissions.WebSearch, Now).Because);
    }

    [Fact]
    public async Task The_cache_reads_again_only_when_the_version_moves()
    {
        using var cache = new AccessSnapshotCache();
        var reads = 0;
        Task<AccessSnapshot> Load(long version) { reads++; return Task.FromResult(new AccessSnapshot(version, [], [], [])); }
        Assert.Equal(3, (await cache.GetAsync(3, _ => Load(3), default)).Version);
        Assert.Equal(3, (await cache.GetAsync(3, _ => Load(3), default)).Version);
        Assert.Equal(1, reads);
        Assert.Equal(4, (await cache.GetAsync(4, _ => Load(4), default)).Version);
        Assert.Equal(2, reads);
        cache.Drop();
        await cache.GetAsync(4, _ => Load(4), default);
        Assert.Equal(3, reads);
    }
}
