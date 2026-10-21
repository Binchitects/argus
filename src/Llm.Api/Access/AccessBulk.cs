using System.Security.Claims;
using Llm.Api.Endpoints;
using Llm.Api.Identity;
using Llm.Core.Access;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Access;

/// <summary>
/// One change for everyone a bulk change reaches: role.give and role.take (their own holding of a role), group.join and
/// group.leave (an app group), feature.allow, feature.deny and feature.default (their own rule on a feature), account.disable and
/// account.enable.
/// </summary>
public sealed record BulkStep(string Kind, Guid? RoleId = null, Guid? GroupId = null, string? Feature = null, DateTimeOffset? ExpiresAt = null);

/// <param name="People">People by id.</param>
/// <param name="Groups">Everyone in these groups now (app, SCIM and directory groups).</param>
/// <param name="DryRun">Say what would change, and change nothing (the default).</param>
public sealed record BulkRequest(List<BulkStep> Steps, List<Guid>? People = null, List<Guid>? Groups = null, bool DryRun = true);

/// <summary>
/// Admin → Access → Many people: changes to many people at once (roles, groups, features, accounts), first as a dry run that
/// says, person by person, what would change and what already is so; then for real, in one change (one entry in the history,
/// undone by its parts), refused whole if it would lock everyone out.
/// </summary>
public static class AccessBulk
{
    public const int MostPeople = 1000;
    public static readonly string[] Kinds = ["role.give", "role.take", "group.join", "group.leave", "feature.allow", "feature.deny", "feature.default", "account.disable", "account.enable"];

    public static void MapAccessBulk(this IEndpointRouteBuilder app) =>
        app.MapPost("/api/admin/access/bulk", BulkAsync).RequirePermission(Permissions.AccessManage);

    private static string NameOf(AppUser u) => string.IsNullOrWhiteSpace(u.DisplayName) ? u.UserName! : u.DisplayName;

    private sealed record Line(Guid Id, string Name, List<string> Changes, List<string> Unchanged);

    private static async Task<IResult> BulkAsync(BulkRequest body, ClaimsPrincipal principal, UserManager<AppUser> users, AppDbContext db, AccessService access, Audit audit,
        TimeProvider clock, CancellationToken ct)
    {
        var me = (await users.GetUserAsync(principal))!;
        var member = await access.MembershipAsync(me, ct);
        if (body.Steps is not { Count: > 0 } || body.Steps.Count > 20)
        {
            return AuthEndpoints.Problem(400, "steps", "Say what to change: 1 to 20 steps.");
        }
        var roles = await db.AccessRoles.AsNoTracking().ToDictionaryAsync(r => r.Id, ct);
        var groups = await db.Groups.AsNoTracking().ToDictionaryAsync(g => g.Id, ct);
        foreach (var s in body.Steps)
        {
            var wrong = s.Kind switch
            {
                _ when !Kinds.Contains(s.Kind) => $"{s.Kind} is not one of {string.Join(", ", Kinds)}.",
                "role.give" or "role.take" when s.RoleId is not { } r || !roles.ContainsKey(r) => "Say which role.",
                "role.give" when roles[s.RoleId!.Value].Key == BuiltInRoles.Member => "Everyone holds Member.",
                "group.join" or "group.leave" when s.GroupId is not { } g || !groups.ContainsKey(g) => "Say which group.",
                "group.join" or "group.leave" when groups[s.GroupId!.Value] is { } g && (g.Directory is not null || g.Scim) =>
                    $"{groups[s.GroupId!.Value].Name}'s members come from the directory or the identity provider, not from here.",
                "group.join" or "group.leave" when !member.Has(Permissions.GroupsMembers) => "Changing groups' members is not among what you may do.",
                "feature.allow" or "feature.deny" or "feature.default" when s.Feature is null || !Permissions.IsFeature(s.Feature) => "Say which feature (feature.web_search...).",
                "account.disable" or "account.enable" when !member.Has(Permissions.PeopleDisable) => "Disabling people is not among what you may do.",
                _ => null,
            };
            if (wrong is not null)
            {
                return AuthEndpoints.Problem(400, "step", wrong);
            }
        }

        // Whom it reaches: the people named, and everyone in the groups named (a directory group's by the directory).
        var ids = (body.People ?? []).ToHashSet();
        var named = (body.Groups ?? []).Where(groups.ContainsKey).ToList();
        if (named.Count != (body.Groups ?? []).Count)
        {
            return AuthEndpoints.Problem(400, "groups", "A group of those is not here.");
        }
        ids.UnionWith(await db.GroupMembers.AsNoTracking().Where(m => named.Contains(m.GroupId)).Select(m => m.UserId).ToListAsync(ct));
        var directory = named.Select(g => groups[g].Directory).OfType<string>().ToList();
        if (directory.Count > 0)
        {
            var listed = await db.Users.AsNoTracking().Where(u => u.DirectoryGroups.Count > 0).Select(u => new { u.Id, u.DirectoryGroups }).ToListAsync(ct);
            ids.UnionWith(listed.Where(u => directory.Any(d => AccessService.InDirectoryGroup(u.DirectoryGroups, d))).Select(u => u.Id));
        }
        var people = await db.Users.Where(u => ids.Contains(u.Id)).OrderBy(u => u.UserName).ToListAsync(ct);
        if (people.Count == 0)
        {
            return AuthEndpoints.Problem(400, "people", "It reaches nobody: name people or groups with people in them.");
        }
        if (people.Count > MostPeople)
        {
            return AuthEndpoints.Problem(400, "people", $"At most {MostPeople:N0} people at once.");
        }

        var personIds = people.Select(p => (Guid?)p.Id).ToList();
        var assignments = await db.AccessAssignments.Where(a => a.SubjectKind == SubjectKind.Person && personIds.Contains(a.SubjectId)).ToListAsync(ct);
        var rules = await db.AccessRules.Where(r => r.SubjectKind == SubjectKind.Person && personIds.Contains(r.SubjectId) && r.ResourceKind == ResourceKinds.Feature).ToListAsync(ct);
        var memberships = await db.GroupMembers.Where(m => ids.Contains(m.UserId)).ToListAsync(ct);
        var now = clock.GetUtcNow();
        var report = new List<Line>();
        // What to do, worked out once: the dry run reports it, the real run does exactly it.
        var work = new List<Action<AccessService.Edit>>();
        foreach (var person in people)
        {
            var line = new Line(person.Id, NameOf(person), [], []);
            report.Add(line);
            foreach (var s in body.Steps)
            {
                switch (s.Kind)
                {
                    case "role.give" or "role.take":
                    {
                        var role = roles[s.RoleId!.Value];
                        // Their own holding of it for everything (an empty scope is none): one held over some groups is another matter.
                        var held = assignments.Where(a => a.SubjectId == person.Id && a.RoleId == role.Id && (a.Scope is null || (a.Scope.Groups.Count == 0 && a.Scope.Repos.Count == 0))
                            && (a.ExpiresAt is null || a.ExpiresAt > now)).ToList();
                        if (s.Kind == "role.give" && held.Count == 0)
                        {
                            line.Changes.Add($"gets {role.Name}{(s.ExpiresAt is { } until ? $" until {until:yyyy-MM-dd}" : "")}");
                            work.Add(e => e.Add(new AccessAssignment { RoleId = role.Id, SubjectKind = SubjectKind.Person, SubjectId = person.Id, ExpiresAt = s.ExpiresAt }));
                        }
                        else if (s.Kind == "role.take" && held.Count > 0)
                        {
                            line.Changes.Add($"loses {role.Name}");
                            work.Add(e => held.ForEach(e.Remove));
                        }
                        else
                        {
                            line.Unchanged.Add(s.Kind == "role.give" ? $"holds {role.Name} already" : $"does not hold {role.Name} themselves");
                        }
                        break;
                    }
                    case "group.join" or "group.leave":
                    {
                        var group = groups[s.GroupId!.Value];
                        var row = memberships.FirstOrDefault(m => m.UserId == person.Id && m.GroupId == group.Id);
                        if (s.Kind == "group.join" && row is null)
                        {
                            line.Changes.Add($"joins {group.Name}");
                            work.Add(e =>
                            {
                                e.Db.GroupMembers.Add(new GroupMember { GroupId = group.Id, UserId = person.Id });
                                e.Changed("group_member", null, new { group = group.Name, person = person.UserName });
                            });
                        }
                        else if (s.Kind == "group.leave" && row is not null)
                        {
                            line.Changes.Add($"leaves {group.Name}");
                            work.Add(e =>
                            {
                                e.Db.GroupMembers.Remove(row);
                                e.Changed("group_member", new { group = group.Name, person = person.UserName }, null);
                            });
                        }
                        else
                        {
                            line.Unchanged.Add(s.Kind == "group.join" ? $"is in {group.Name} already" : $"is not in {group.Name}");
                        }
                        break;
                    }
                    case "feature.allow" or "feature.deny" or "feature.default":
                    {
                        var own = rules.Where(r => r.SubjectId == person.Id && r.Resource == s.Feature).ToList();
                        var effect = s.Kind == "feature.allow" ? RuleEffect.Allow : RuleEffect.Deny;
                        if (s.Kind == "feature.default")
                        {
                            if (own.Count > 0)
                            {
                                line.Changes.Add($"{s.Feature}: as their roles and groups say");
                                work.Add(e => own.ForEach(e.Remove));
                            }
                            else
                            {
                                line.Unchanged.Add($"{s.Feature}: as their roles and groups say already");
                            }
                        }
                        else if (own.Count == 1 && own[0].Effect == effect)
                        {
                            line.Unchanged.Add($"{s.Feature}: {(effect == RuleEffect.Allow ? "allowed" : "denied")} already");
                        }
                        else
                        {
                            line.Changes.Add($"{s.Feature}: {(effect == RuleEffect.Allow ? "allowed" : "denied")}");
                            work.Add(e =>
                            {
                                own.ForEach(e.Remove);
                                e.Add(new AccessRule
                                {
                                    Effect = effect, SubjectKind = SubjectKind.Person, SubjectId = person.Id, ResourceKind = ResourceKinds.Feature, Resource = s.Feature!,
                                    Action = ResourceKinds.Use, ExpiresAt = s.ExpiresAt, Note = "bulk change",
                                });
                            });
                        }
                        break;
                    }
                    case "account.disable" or "account.enable":
                    {
                        var disable = s.Kind == "account.disable";
                        if (disable && person.Id == me.Id)
                        {
                            line.Unchanged.Add("is you: not disabled");
                        }
                        else if (person.IsDisabled == disable)
                        {
                            line.Unchanged.Add(disable ? "is disabled already" : "is enabled already");
                        }
                        else
                        {
                            line.Changes.Add(disable ? "is disabled (signed out everywhere)" : "is enabled");
                            work.Add(e =>
                            {
                                person.IsDisabled = disable;
                                person.DisabledReason = disable ? $"Disabled in a bulk change by {NameOf(me)}." : null;
                                person.SecurityStamp = Guid.NewGuid().ToString();
                                e.Changed("account", new { person = person.UserName, disabled = !disable }, new { person = person.UserName, disabled = disable });
                            });
                        }
                        break;
                    }
                }
            }
        }

        var total = report.Sum(l => l.Changes.Count);
        object Shape(bool done) => new
        {
            dryRun = !done, done, people = report.Count, changes = total,
            lines = report.Select(l => new { l.Id, l.Name, l.Changes, l.Unchanged }),
        };
        if (body.DryRun || total == 0)
        {
            return Results.Ok(Shape(false));
        }
        try
        {
            await access.ChangeAsync(me, edit =>
            {
                work.ForEach(w => w(edit));
                return Task.CompletedTask;
            }, "access.bulk", $"{total} changes for {report.Count(l => l.Changes.Count > 0)} people", ct);
        }
        catch (AccessRefusedException ex)
        {
            return AuthEndpoints.Problem(409, "lockout", ex.Message);
        }
        return Results.Ok(Shape(true));
    }
}
