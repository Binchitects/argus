using System.Text.Json;
using Llm.Api.Identity;
using Llm.Api.Ldap;
using Llm.Core.Access;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Access;

/// <summary>What someone belongs to (their groups, app and directory) and what access says they may do, as of one version of the access data.</summary>
public sealed class Membership(Principal principal, AccessSnapshot snapshot, DateTimeOffset now)
{
    public Principal Principal { get; } = principal;
    public AccessSnapshot Snapshot { get; } = snapshot;
    public DateTimeOffset Now { get; } = now;
    public IReadOnlySet<Guid> Groups => Principal.Groups;

    /// <summary>Holds the Owner role, unscoped: every permission and every resource.</summary>
    public bool IsOwner => Snapshot.IsOwner(Principal, Now);

    public bool IsAdmin => IsOwner;

    /// <summary>Their API access is off (feature.api_keys denied), whether or not they are disabled: their keys are blocked and none is made.</summary>
    public bool ApiOff => !Snapshot.Check(Principal with { Disabled = false }, Permissions.ApiKeys, Now).Allowed;

    /// <summary>Holds a permission (<see cref="Permissions"/>).</summary>
    public bool Has(string permission) => Snapshot.Check(Principal, permission, Now).Allowed;

    public Decision Check(string permission) => Snapshot.Check(Principal, permission, Now);

    /// <summary>May use a model or a tool, or read a knowledge source.</summary>
    public bool MayUse(string kind, string resource) => Check(kind, resource).Allowed;

    public Decision Check(string kind, string resource, string? action = null) =>
        Snapshot.Check(Principal, kind, resource, action ?? ResourceKinds.Actions[kind][0], Now);
}

/// <summary>The access data was not changed: it would lock everyone out, or take the actor's own access away.</summary>
public sealed class AccessRefusedException(string message) : Exception(message);

/// <summary>
/// The one place access is decided (<see cref="AccessSnapshot"/>), and changed. People's groups are read
/// for each request; roles, assignments and rules are read again only when their version moved, so a
/// change on any replica applies from the next request. Each change is kept in the history (access_changes)
/// and raises the version.
/// </summary>
public sealed class AccessService(AppDbContext db, AccessSnapshotCache cache, IOptions<AuthOptions> auth, TimeProvider clock, Audit audit,
    Models.KeyAccessWatcher keys, IHttpContextAccessor http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
    private readonly Dictionary<Guid, Membership> _seen = [];

    /// <summary>The access data now: the held one when its version is current.</summary>
    public async Task<AccessSnapshot> SnapshotAsync(CancellationToken ct = default)
    {
        var version = await db.AccessMeta.AsNoTracking().Where(m => m.Id == 1).Select(m => m.Version).FirstOrDefaultAsync(ct);
        return await cache.GetAsync(version, token => LoadAsync(db, version, token), ct);
    }

    /// <summary>Reads the roles, assignments and rules, with the names they mention, through <paramref name="from"/> (a transaction's context sees its own changes).</summary>
    public static async Task<AccessSnapshot> LoadAsync(AppDbContext from, long version, CancellationToken ct)
    {
        var roles = await from.AccessRoles.AsNoTracking().ToListAsync(ct);
        var assignments = await from.AccessAssignments.AsNoTracking().ToListAsync(ct);
        var rules = await from.AccessRules.AsNoTracking().ToListAsync(ct);
        var people = assignments.Where(a => a.SubjectKind == SubjectKind.Person).Select(a => a.SubjectId)
            .Concat(rules.Where(r => r.SubjectKind == SubjectKind.Person).Select(r => r.SubjectId)).OfType<Guid>().Distinct().ToList();
        var groups = assignments.Where(a => a.SubjectKind == SubjectKind.Group).Select(a => a.SubjectId)
            .Concat(rules.Where(r => r.SubjectKind == SubjectKind.Group).Select(r => r.SubjectId)).OfType<Guid>().Distinct().ToList();
        var names = new Dictionary<Guid, string>();
        foreach (var p in await from.Users.AsNoTracking().Where(u => people.Contains(u.Id)).Select(u => new { u.Id, u.UserName }).ToListAsync(ct))
        {
            names[p.Id] = p.UserName ?? p.Id.ToString();
        }
        foreach (var g in await from.Groups.AsNoTracking().Where(g => groups.Contains(g.Id)).Select(g => new { g.Id, g.Name }).ToListAsync(ct))
        {
            names[g.Id] = g.Name;
        }
        return new AccessSnapshot(version, roles, assignments, rules, names);
    }

    public async Task<Membership> MembershipAsync(AppUser user, CancellationToken ct = default)
    {
        if (_seen.TryGetValue(user.Id, out var seen))
        {
            return seen;
        }
        var groups = await db.GroupMembers.AsNoTracking().Where(m => m.UserId == user.Id).Select(m => m.GroupId).ToListAsync(ct);
        if (user.DirectoryGroups.Count > 0)
        {
            var directory = await db.Groups.AsNoTracking().Where(g => g.Directory != null).Select(g => new { g.Id, g.Directory }).ToListAsync(ct);
            groups.AddRange(directory.Where(g => InDirectoryGroup(user.DirectoryGroups, g.Directory!)).Select(g => g.Id));
        }
        var membership = new Membership(PrincipalOf(user, groups), await SnapshotAsync(ct), clock.GetUtcNow());
        _seen[user.Id] = membership;
        return membership;
    }

    /// <summary>Everyone's memberships at once (the key sync, the owners' list): two reads, not two a person.</summary>
    public async Task<Dictionary<Guid, Membership>> MembershipsAsync(IReadOnlyCollection<AppUser> people, CancellationToken ct = default)
    {
        var ids = people.Select(p => p.Id).ToList();
        var app = (await db.GroupMembers.AsNoTracking().Where(m => ids.Contains(m.UserId)).Select(m => new { m.UserId, m.GroupId }).ToListAsync(ct))
            .ToLookup(m => m.UserId, m => m.GroupId);
        var directory = await db.Groups.AsNoTracking().Where(g => g.Directory != null).Select(g => new { g.Id, g.Directory }).ToListAsync(ct);
        var snapshot = await SnapshotAsync(ct);
        var now = clock.GetUtcNow();
        return people.ToDictionary(p => p.Id, p => new Membership(
            PrincipalOf(p, [.. app[p.Id], .. directory.Where(g => p.DirectoryGroups.Count > 0 && InDirectoryGroup(p.DirectoryGroups, g.Directory!)).Select(g => g.Id)]),
            snapshot, now));
    }

    private Principal PrincipalOf(AppUser user, IEnumerable<Guid> groups)
    {
        var owner = new List<string>();
        // Break-glass: the first admin (.env's ADMIN_USERNAME) is always an Owner, so no change to the data can lock everyone out.
        if (user.Source == UserSource.Local && string.Equals(user.UserName, auth.Value.AdminUserName, StringComparison.OrdinalIgnoreCase))
        {
            owner.Add("the first admin (ADMIN_USERNAME)");
        }
        return Principal.Of(user.Id, groups, user.IsDisabled, owner);
    }

    public async Task<bool> IsOwnerAsync(AppUser user, CancellationToken ct = default) => (await MembershipAsync(user, ct)).IsOwner;

    /// <summary>Whether the person holds a permission now.</summary>
    public async Task<bool> MayAsync(AppUser user, string permission, CancellationToken ct = default) => (await MembershipAsync(user, ct)).Has(permission);

    /// <summary>The people who hold Owner, unscoped: the active ones, or (<paramref name="includeDisabled"/>) also those who would if enabled.</summary>
    public async Task<List<AppUser>> OwnersAsync(bool includeDisabled = false, CancellationToken ct = default)
    {
        var people = await db.Users.AsNoTracking().Where(u => includeDisabled || !u.IsDisabled).ToListAsync(ct);
        var memberships = await MembershipsAsync(people, ct);
        return [.. people.Where(p => memberships[p.Id].Snapshot.IsOwner(memberships[p.Id].Principal with { Disabled = false }, memberships[p.Id].Now))];
    }

    /// <summary>Whether the person's API access is off (<see cref="Membership.ApiOff"/>).</summary>
    public async Task<bool> ApiOffAsync(AppUser user, CancellationToken ct = default) => (await MembershipAsync(user, ct)).ApiOff;

    /// <summary>The built-in roles as this version has them: made when missing, their names and permissions put back. Run at every start.</summary>
    public static async Task SyncBuiltInRolesAsync(AppDbContext db, CancellationToken ct = default)
    {
        var stored = await db.AccessRoles.Where(r => r.BuiltIn).ToDictionaryAsync(r => r.Key, ct);
        var changed = false;
        foreach (var role in BuiltInRoles.All)
        {
            if (!stored.TryGetValue(role.Key, out var row))
            {
                row = new AccessRole { Key = role.Key, Name = role.Name, BuiltIn = true };
                db.AccessRoles.Add(row);
                changed = true;
            }
            if (row.Name != role.Name || row.Description != role.Description || !row.Permissions.Order(StringComparer.Ordinal).SequenceEqual(role.Permissions.Order(StringComparer.Ordinal)))
            {
                row.Name = role.Name;
                row.Description = role.Description;
                row.Permissions = [.. role.Permissions];
                changed = true;
            }
        }
        if (changed)
        {
            await db.SaveChangesAsync(ct);
            await db.AccessMeta.Where(m => m.Id == 1).ExecuteUpdateAsync(u => u.SetProperty(m => m.Version, m => m.Version + 1), ct);
        }
    }

    /// <summary>Their place in the answers' line: the highest priority of their groups; 0 when none sets one.</summary>
    public async Task<int> PriorityAsync(AppUser user, CancellationToken ct = default)
    {
        var groups = (await MembershipAsync(user, ct)).Groups;
        return groups.Count == 0 ? 0 : await db.Groups.AsNoTracking().Where(g => groups.Contains(g.Id)).MaxAsync(g => (int?)g.Priority, ct) ?? 0;
    }

    /// <summary>
    /// Their requests answered at once, chat and API together, and where it comes from: their own ("person"), else the most
    /// generous of their groups that set one ("group", a 0 there is no limit and wins), else none here ("company": the
    /// chat's Chat:AnswersPerPerson holds their answers, nothing their API requests). 0: no limit.
    /// </summary>
    public async Task<(int? Value, string From, string? Group)> AnswersAtOnceAsync(AppUser user, CancellationToken ct = default)
    {
        if (user.AnswersAtOnce is { } own)
        {
            return (own, "person", null);
        }
        var groups = (await MembershipAsync(user, ct)).Groups;
        var set = groups.Count == 0 ? [] : await db.Groups.AsNoTracking().Where(g => groups.Contains(g.Id) && g.AnswersAtOnce != null)
            .Select(g => new { g.Name, g.AnswersAtOnce }).ToListAsync(ct);
        if (set.Count == 0)
        {
            return (null, "company", null);
        }
        var best = set.OrderBy(g => g.AnswersAtOnce == 0 ? 0 : 1).ThenByDescending(g => g.AnswersAtOnce).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase).First();
        return (best.AnswersAtOnce, "group", best.Name);
    }

    /// <summary>The directory's rule for group names: the full DN, or its common name, in any case.</summary>
    public static bool InDirectoryGroup(IEnumerable<string> memberOf, string group) => memberOf.Any(g => LdapDirectory.IsNamed(g, group));

    /// <summary>Who may use a resource, as the "Who may use it" pickers show it: everyone (no allow names it), the listed groups, or admins (Owners).</summary>
    public static (Audience Audience, List<Guid> Groups) AudienceOf(AccessSnapshot snapshot, string kind, string resource)
    {
        var action = ResourceKinds.Actions[kind][0];
        var allows = snapshot.Rules.Where(r => r.Effect == RuleEffect.Allow && r.ResourceKind == kind && r.Action == action && r.ExpiresAt is null
            && string.Equals(r.Resource, resource, StringComparison.OrdinalIgnoreCase)).ToList();
        var groups = allows.Where(r => r.SubjectKind == SubjectKind.Group).Select(r => r.SubjectId!.Value).Distinct().ToList();
        return allows.Count == 0 || allows.Any(r => r.SubjectKind == SubjectKind.Everyone) ? (Audience.Everyone, [])
            : groups.Count > 0 ? (Audience.Groups, groups)
            : (Audience.Admins, []);
    }

    /// <summary>One change to the access data, made in a transaction with its history; see <see cref="ChangeAsync"/>.</summary>
    public sealed class Edit(AppDbContext db, Guid? actor, Guid bulk, DateTimeOffset now)
    {
        internal List<AccessChange> Changes { get; } = [];
        public AppDbContext Db => db;

        public void Add(AccessRule rule)
        {
            rule.CreatedById ??= actor;
            rule.CreatedAt = now;
            db.AccessRules.Add(rule);
            Changes.Add(new AccessChange { At = now, ActorId = actor, Kind = "rule", After = JsonSerializer.Serialize(rule, Json), BulkId = bulk });
        }

        public void Remove(AccessRule rule)
        {
            db.AccessRules.Remove(rule);
            Changes.Add(new AccessChange { At = now, ActorId = actor, Kind = "rule", Before = JsonSerializer.Serialize(rule, Json), BulkId = bulk });
        }

        public void Add(AccessAssignment assignment)
        {
            assignment.CreatedById ??= actor;
            assignment.CreatedAt = now;
            db.AccessAssignments.Add(assignment);
            Changes.Add(new AccessChange { At = now, ActorId = actor, Kind = "assignment", After = JsonSerializer.Serialize(assignment, Json), BulkId = bulk });
        }

        public void Remove(AccessAssignment assignment)
        {
            db.AccessAssignments.Remove(assignment);
            Changes.Add(new AccessChange { At = now, ActorId = actor, Kind = "assignment", Before = JsonSerializer.Serialize(assignment, Json), BulkId = bulk });
        }

        public void Changed(string kind, object? before, object? after) =>
            Changes.Add(new AccessChange
            {
                At = now, ActorId = actor, Kind = kind, BulkId = bulk,
                Before = before is null ? null : JsonSerializer.Serialize(before, Json),
                After = after is null ? null : JsonSerializer.Serialize(after, Json),
            });
    }

    /// <summary>
    /// Makes a change to roles, assignments or rules in one transaction: its history rows, a new version,
    /// and the break-glass checks. A change that would leave no active Owner, or take the actor's own
    /// access.manage, is refused (<see cref="AccessRefusedException"/>) and nothing is kept.
    /// </summary>
    /// <returns>How many rows changed (0: nothing to do, and no new version).</returns>
    public async Task<int> ChangeAsync(AppUser? actor, Func<Edit, Task> work, string? auditAction = null, string? auditTarget = null, CancellationToken ct = default)
    {
        if (actor is not null)
        {
            // What the actor held before, for the check that they keep access.manage.
            await MembershipAsync(actor, ct);
        }
        var strategy = db.Database.CreateExecutionStrategy();
        var changed = await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var edit = new Edit(db, actor?.Id ?? Signed(), Guid.CreateVersion7(), clock.GetUtcNow());
            await work(edit);
            if (edit.Changes.Count == 0)
            {
                return 0;
            }
            db.AccessChanges.AddRange(edit.Changes);
            await db.SaveChangesAsync(ct);
            await db.AccessMeta.Where(m => m.Id == 1).ExecuteUpdateAsync(u => u.SetProperty(m => m.Version, m => m.Version + 1), ct);
            await RefuseLockoutAsync(actor, ct);
            await tx.CommitAsync(ct);
            return edit.Changes.Count;
        });
        _seen.Clear();
        if (changed > 0)
        {
            // People's keys carry the models they may use, and stop without API access.
            keys.Wake();
            if (auditAction is not null)
            {
                await audit.WriteAsync(auditAction, auditTarget, detail: $"{changed} change{(changed == 1 ? "" : "s")}");
            }
        }
        return changed;
    }

    /// <summary>The person signed in, when a change names no actor (an admin page's request).</summary>
    private Guid? Signed() =>
        Guid.TryParse(http.HttpContext?.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;

    /// <summary>The break-glass rules, on the data as the transaction left it.</summary>
    private async Task RefuseLockoutAsync(AppUser? actor, CancellationToken ct)
    {
        var version = await db.AccessMeta.Where(m => m.Id == 1).Select(m => m.Version).FirstAsync(ct);
        var after = await LoadAsync(db, version, ct);
        var now = clock.GetUtcNow();
        var people = await db.Users.AsNoTracking().Where(u => !u.IsDisabled).ToListAsync(ct);
        var app = (await db.GroupMembers.AsNoTracking().Select(m => new { m.UserId, m.GroupId }).ToListAsync(ct)).ToLookup(m => m.UserId, m => m.GroupId);
        var directory = await db.Groups.AsNoTracking().Where(g => g.Directory != null).Select(g => new { g.Id, g.Directory }).ToListAsync(ct);
        Principal Of(AppUser p) => PrincipalOf(p, [.. app[p.Id], .. directory.Where(g => p.DirectoryGroups.Count > 0 && InDirectoryGroup(p.DirectoryGroups, g.Directory!)).Select(g => g.Id)]);
        if (!people.Any(p => after.IsOwner(Of(p), now)))
        {
            throw new AccessRefusedException("That would leave nobody who holds Owner, so nobody could administer the service.");
        }
        if (actor is not null && _seen.TryGetValue(actor.Id, out var before) && before.Has(Permissions.AccessManage)
            && people.FirstOrDefault(p => p.Id == actor.Id) is { } self && !after.Check(Of(self), Permissions.AccessManage, now).Allowed)
        {
            throw new AccessRefusedException("You cannot take your own access.manage away: ask another admin who holds it.");
        }
    }

    /// <summary>The groups, and the people in them, a role held for some groups only reaches.</summary>
    public sealed record Reach(IReadOnlySet<Guid> Groups, IReadOnlySet<Guid> People);

    /// <summary>
    /// Whom the person's <paramref name="permission"/> reaches: null when it is held for everyone (or as Owner);
    /// otherwise the groups of the roles' scopes and their members (directory members too). Empty: no one.
    /// </summary>
    public async Task<Reach?> ReachAsync(AppUser user, string permission, CancellationToken ct = default)
    {
        var m = await MembershipAsync(user, ct);
        if (m.Snapshot.ScopesOf(m.Principal, permission, m.Now) is not { } scopes)
        {
            return null;
        }
        var groups = scopes.SelectMany(x => x.Groups).ToHashSet();
        var people = (await db.GroupMembers.AsNoTracking().Where(x => groups.Contains(x.GroupId)).Select(x => x.UserId).ToListAsync(ct)).ToHashSet();
        var directory = await db.Groups.AsNoTracking().Where(g => groups.Contains(g.Id) && g.Directory != null).Select(g => g.Directory!).ToListAsync(ct);
        if (directory.Count > 0)
        {
            var listed = await db.Users.AsNoTracking().Where(u => u.DirectoryGroups.Count > 0).Select(u => new { u.Id, u.DirectoryGroups }).ToListAsync(ct);
            people.UnionWith(listed.Where(u => directory.Any(d => InDirectoryGroup(u.DirectoryGroups, d))).Select(u => u.Id));
        }
        return new Reach(groups, people);
    }

    /// <summary>Gives or takes the Owner role, the person's own (not through a group).</summary>
    public async Task SetOwnerAsync(AppUser? actor, AppUser user, bool owner, CancellationToken ct = default)
    {
        await ChangeAsync(actor, async edit =>
        {
            var role = await edit.Db.AccessRoles.SingleAsync(r => r.Key == BuiltInRoles.Owner, ct);
            var held = await edit.Db.AccessAssignments
                .Where(a => a.RoleId == role.Id && a.SubjectKind == SubjectKind.Person && a.SubjectId == user.Id).ToListAsync(ct);
            if (owner && held.Count == 0)
            {
                edit.Add(new AccessAssignment { RoleId = role.Id, SubjectKind = SubjectKind.Person, SubjectId = user.Id });
            }
            else if (!owner)
            {
                held.ForEach(edit.Remove);
            }
        }, ct: ct);
    }

    /// <summary>Gives back or takes a feature permission from the person: their own deny rule, made or removed.</summary>
    public async Task SetFeatureAsync(AppUser? actor, AppUser user, string permission, bool on, string? note = null, CancellationToken ct = default)
    {
        await ChangeAsync(actor, async edit =>
        {
            var denies = await edit.Db.AccessRules.Where(r => r.SubjectKind == SubjectKind.Person && r.SubjectId == user.Id && r.ResourceKind == ResourceKinds.Feature
                && r.Resource == permission && r.Action == ResourceKinds.Use && r.Effect == RuleEffect.Deny).ToListAsync(ct);
            if (on)
            {
                denies.ForEach(edit.Remove);
            }
            else if (denies.Count == 0)
            {
                edit.Add(new AccessRule
                {
                    Effect = RuleEffect.Deny, SubjectKind = SubjectKind.Person, SubjectId = user.Id, ResourceKind = ResourceKinds.Feature,
                    Resource = permission, Action = ResourceKinds.Use, Note = note,
                });
            }
        }, ct: ct);
    }

    /// <summary>
    /// What a "Who may use it" picker chose, as rules on the resource: an allow for everyone, for each
    /// group, or for the Owner role (admins). Rules for single people, and denies, are left as they are.
    /// </summary>
    public async Task SetAudienceAsync(AppUser? actor, string kind, string resource, Audience audience, IReadOnlyCollection<Guid> groups, CancellationToken ct = default)
    {
        var action = ResourceKinds.Actions[kind][0];
        await ChangeAsync(actor, async edit =>
        {
            var owner = await edit.Db.AccessRoles.Where(r => r.Key == BuiltInRoles.Owner).Select(r => r.Id).SingleAsync(ct);
            var old = await edit.Db.AccessRules.Where(r => r.ResourceKind == kind && r.Resource == resource && r.Action == action && r.Effect == RuleEffect.Allow
                && r.ExpiresAt == null && (r.SubjectKind == SubjectKind.Everyone || r.SubjectKind == SubjectKind.Group || (r.SubjectKind == SubjectKind.Role && r.SubjectId == owner)))
                .ToListAsync(ct);
            var want = audience switch
            {
                Audience.Everyone => [(SubjectKind.Everyone, (Guid?)null)],
                Audience.Groups => groups.Distinct().Select(g => (SubjectKind.Group, (Guid?)g)).ToList(),
                _ => [(SubjectKind.Role, (Guid?)owner)],
            };
            foreach (var rule in old.Where(r => !want.Contains((r.SubjectKind, r.SubjectId))))
            {
                edit.Remove(rule);
            }
            foreach (var (subject, id) in want.Where(w => !old.Any(r => (r.SubjectKind, r.SubjectId) == w)))
            {
                edit.Add(new AccessRule { Effect = RuleEffect.Allow, SubjectKind = subject, SubjectId = id, ResourceKind = kind, Resource = resource, Action = action });
            }
        }, ct: ct);
    }

    /// <summary>A resource is gone (a model, a tool, a knowledge source): its own rules go with it; patterns stay.</summary>
    public Task RemoveResourceAsync(AppUser? actor, string kind, string resource, CancellationToken ct = default) =>
        ChangeAsync(actor, async edit =>
        {
            foreach (var rule in await edit.Db.AccessRules.Where(r => r.ResourceKind == kind && r.Resource == resource).ToListAsync(ct))
            {
                edit.Remove(rule);
            }
        }, ct: ct);

    /// <summary>A person or a group is gone: their assignments and rules go with them.</summary>
    public Task RemoveSubjectAsync(AppUser? actor, SubjectKind kind, Guid id, CancellationToken ct = default) =>
        ChangeAsync(actor, async edit =>
        {
            foreach (var a in await edit.Db.AccessAssignments.Where(a => a.SubjectKind == kind && a.SubjectId == id).ToListAsync(ct))
            {
                edit.Remove(a);
            }
            foreach (var r in await edit.Db.AccessRules.Where(r => r.SubjectKind == kind && r.SubjectId == id).ToListAsync(ct))
            {
                edit.Remove(r);
            }
        }, ct: ct);
}
