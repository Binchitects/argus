using System.Text.RegularExpressions;
using Llm.Api.Endpoints;
using Llm.Core.Access;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Access;

/// <summary>A custom role: its name, what it is for, and its permissions (from the catalog).</summary>
public sealed record RoleRequest(string? Name = null, string? Description = null, string[]? Permissions = null);

/// <summary>A role held by a person, a group (a directory group linked to one too) or everyone, maybe narrowed and until a date.</summary>
public sealed record AssignmentRequest(Guid RoleId, SubjectKind SubjectKind, Guid? SubjectId = null, AccessScope? Scope = null, DateTimeOffset? ExpiresAt = null);

/// <summary>An allow or a deny of an action on a resource (a model, a tool, a knowledge source, a repository, a feature), by its name or a pattern.</summary>
public sealed record RuleRequest(RuleEffect Effect, SubjectKind SubjectKind, Guid? SubjectId, string ResourceKind, string Resource, string? Action = null,
    DateTimeOffset? ExpiresAt = null, string? Note = null);

/// <summary>
/// Admin → Access: roles (built-in and custom), who holds them, resource rules, the checker ("may this person...?")
/// and the history of every change. Reads need access.read, changes access.manage.
/// </summary>
public static partial class AccessEndpoints
{
    public static void MapAccess(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin/access").RequirePermissions(Permissions.AccessRead, Permissions.AccessManage);
        g.MapGet("", OverviewAsync);
        g.MapPost("/roles", CreateRoleAsync);
        g.MapPatch("/roles/{id:guid}", UpdateRoleAsync);
        g.MapDelete("/roles/{id:guid}", DeleteRoleAsync);
        g.MapPost("/assignments", AssignAsync);
        g.MapDelete("/assignments/{id:guid}", UnassignAsync);
        g.MapPost("/rules", AddRuleAsync);
        g.MapDelete("/rules/{id:guid}", RemoveRuleAsync);
        // A read: it changes nothing, so access.read is enough.
        g.MapGet("/check", CheckAsync);
        g.MapGet("/history", HistoryAsync);
    }

    private static object Subject(AccessSnapshot s, SubjectKind kind, Guid? id) => new { kind, id, name = s.Describe(kind, id) };

    private static async Task<IResult> OverviewAsync(AccessService access, CancellationToken ct)
    {
        var s = await access.SnapshotAsync(ct);
        return Results.Ok(new
        {
            s.Version,
            permissions = Permissions.All.Select(p => new { p.Key, p.Area, p.Description, p.Admin }),
            resourceKinds = ResourceKinds.Actions.Select(k => new { kind = k.Key, actions = k.Value }),
            roles = s.Roles.OrderBy(r => !r.BuiltIn).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).Select(r => new
            {
                r.Id, r.Key, r.Name, r.Description, r.BuiltIn, r.Permissions,
                holders = s.Assignments.Where(a => a.RoleId == r.Id).Select(a => new { a.Id, subject = Subject(s, a.SubjectKind, a.SubjectId), a.Scope, a.ExpiresAt, a.CreatedAt }),
            }),
            rules = s.Rules.OrderBy(r => r.ResourceKind, StringComparer.Ordinal).ThenBy(r => r.Resource, StringComparer.Ordinal).Select(r => new
            {
                r.Id, r.Effect, subject = Subject(s, r.SubjectKind, r.SubjectId), r.ResourceKind, r.Resource, r.Action, r.ExpiresAt, r.Note, r.CreatedAt,
            }),
        });
    }

    private static IResult Refused(string message) => AuthEndpoints.Problem(400, "access", message);

    private static string? Wrong(RoleRequest body, bool creating)
    {
        if (creating && string.IsNullOrWhiteSpace(body.Name))
        {
            return "A role needs a name.";
        }
        if (body.Name is { } name && (name.Trim().Length is 0 or > 80))
        {
            return "A role's name is 1 to 80 characters.";
        }
        if (body.Description is { Length: > 500 })
        {
            return "A role's description is at most 500 characters.";
        }
        return body.Permissions?.FirstOrDefault(p => !Permissions.IsKnown(p)) is { } unknown ? $"{unknown} is not a permission." : null;
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NotKey();

    private static async Task<IResult> CreateRoleAsync(RoleRequest body, AccessService access, HttpContext http, CancellationToken ct)
    {
        if (Wrong(body, creating: true) is { } wrong)
        {
            return Refused(wrong);
        }
        var role = new AccessRole
        {
            Key = "",
            Name = body.Name!.Trim(),
            Description = string.IsNullOrWhiteSpace(body.Description) ? null : body.Description.Trim(),
            Permissions = [.. (body.Permissions ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
        };
        return await RunAsync(access, http, async edit =>
        {
            var stem = "custom-" + NotKey().Replace(role.Name.ToLowerInvariant(), "-").Trim('-');
            var taken = await edit.Db.AccessRoles.Where(r => r.Key.StartsWith(stem)).Select(r => r.Key).ToListAsync(ct);
            role.Key = Enumerable.Range(1, taken.Count + 1).Select(n => n == 1 ? stem : $"{stem}-{n}").First(k => !taken.Contains(k));
            if (await edit.Db.AccessRoles.AnyAsync(r => r.Name == role.Name, ct))
            {
                throw new AccessRefusedException($"There is a role named {role.Name} already.");
            }
            edit.Db.AccessRoles.Add(role);
            edit.Changed("role", null, role);
        }, "access.role.create", role.Name, () => Results.Created($"/api/admin/access/roles/{role.Id}", new { role.Id, role.Key }), ct);
    }

    private static async Task<IResult> UpdateRoleAsync(Guid id, RoleRequest body, AccessService access, HttpContext http, CancellationToken ct)
    {
        if (Wrong(body, creating: false) is { } wrong)
        {
            return Refused(wrong);
        }
        return await RunAsync(access, http, async edit =>
        {
            var role = await edit.Db.AccessRoles.SingleOrDefaultAsync(r => r.Id == id, ct) ?? throw new KeyNotFoundException();
            if (role.BuiltIn)
            {
                throw new AccessRefusedException("A built-in role stays as it is: make a role of your own from it.");
            }
            var before = new { role.Name, role.Description, Permissions = role.Permissions.ToList() };
            role.Name = body.Name?.Trim() ?? role.Name;
            role.Description = body.Description is null ? role.Description : body.Description.Trim() is { Length: > 0 } d ? d : null;
            if (body.Permissions is { } permissions)
            {
                role.Permissions = [.. permissions.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
            }
            edit.Changed("role", before, new { role.Name, role.Description, role.Permissions });
        }, "access.role.update", id.ToString(), () => Results.NoContent(), ct);
    }

    private static Task<IResult> DeleteRoleAsync(Guid id, AccessService access, HttpContext http, CancellationToken ct) =>
        RunAsync(access, http, async edit =>
        {
            var role = await edit.Db.AccessRoles.SingleOrDefaultAsync(r => r.Id == id, ct) ?? throw new KeyNotFoundException();
            if (role.BuiltIn)
            {
                throw new AccessRefusedException("A built-in role cannot be deleted.");
            }
            var held = await edit.Db.AccessAssignments.CountAsync(a => a.RoleId == id, ct);
            var named = await edit.Db.AccessRules.CountAsync(r => r.SubjectKind == SubjectKind.Role && r.SubjectId == id, ct);
            if (held + named > 0)
            {
                throw new AccessRefusedException($"{role.Name} is still held ({held}) or named in rules ({named}): take those away first.");
            }
            edit.Db.AccessRoles.Remove(role);
            edit.Changed("role", role, null);
        }, "access.role.delete", id.ToString(), () => Results.NoContent(), ct);

    /// <summary>The subject is there: a person, a group, or a role (rules only); everyone names no id.</summary>
    private static async Task CheckSubjectAsync(AppDbContext db, SubjectKind kind, Guid? id, bool rule, CancellationToken ct)
    {
        var there = kind switch
        {
            SubjectKind.Everyone => id is null,
            SubjectKind.Person => id is { } p && await db.Users.AnyAsync(u => u.Id == p, ct),
            SubjectKind.Group => id is { } g && await db.Groups.AnyAsync(x => x.Id == g, ct),
            SubjectKind.Role => rule && id is { } r && await db.AccessRoles.AnyAsync(x => x.Id == r, ct),
            _ => false,
        };
        if (!there)
        {
            throw new AccessRefusedException(kind == SubjectKind.Role && !rule ? "A role is held by a person, a group or everyone." : $"There is no such {kind.ToString().ToLowerInvariant()}.");
        }
    }

    private static Task<IResult> AssignAsync(AssignmentRequest body, AccessService access, TimeProvider clock, HttpContext http, CancellationToken ct)
    {
        var made = new AccessAssignment { RoleId = body.RoleId, SubjectKind = body.SubjectKind, SubjectId = body.SubjectKind == SubjectKind.Everyone ? null : body.SubjectId, ExpiresAt = body.ExpiresAt };
        return RunAsync(access, http, async edit =>
        {
            if (body.ExpiresAt is { } until && until <= clock.GetUtcNow())
            {
                throw new AccessRefusedException("That date has passed.");
            }
            if (!await edit.Db.AccessRoles.AnyAsync(r => r.Id == body.RoleId, ct))
            {
                throw new AccessRefusedException("There is no such role.");
            }
            await CheckSubjectAsync(edit.Db, body.SubjectKind, made.SubjectId, rule: false, ct);
            if (body.Scope is { IsEmpty: false } scope)
            {
                var groups = scope.Groups.Distinct().ToList();
                if (await edit.Db.Groups.CountAsync(x => groups.Contains(x.Id), ct) != groups.Count)
                {
                    throw new AccessRefusedException("A group of the scope is not there.");
                }
                made.Scope = new AccessScope { Groups = groups, Repos = [.. scope.Repos.Select(r => r.Trim()).Where(r => r.Length > 0).Distinct(StringComparer.Ordinal)] };
            }
            if (await edit.Db.AccessAssignments.AnyAsync(a => a.RoleId == made.RoleId && a.SubjectKind == made.SubjectKind && a.SubjectId == made.SubjectId, ct))
            {
                throw new AccessRefusedException("They hold that role already: take it away first to hold it otherwise.");
            }
            edit.Add(made);
        }, "access.assign", body.RoleId.ToString(), () => Results.Created($"/api/admin/access/assignments/{made.Id}", new { made.Id }), ct);
    }

    private static Task<IResult> UnassignAsync(Guid id, AccessService access, HttpContext http, CancellationToken ct) =>
        RunAsync(access, http, async edit => edit.Remove(await edit.Db.AccessAssignments.SingleOrDefaultAsync(a => a.Id == id, ct) ?? throw new KeyNotFoundException()),
            "access.unassign", id.ToString(), () => Results.NoContent(), ct);

    private static Task<IResult> AddRuleAsync(RuleRequest body, AccessService access, TimeProvider clock, HttpContext http, CancellationToken ct)
    {
        var rule = new AccessRule
        {
            Effect = body.Effect, SubjectKind = body.SubjectKind, SubjectId = body.SubjectKind == SubjectKind.Everyone ? null : body.SubjectId,
            ResourceKind = body.ResourceKind ?? "", Resource = body.Resource?.Trim() ?? "", Action = body.Action ?? "",
            ExpiresAt = body.ExpiresAt, Note = string.IsNullOrWhiteSpace(body.Note) ? null : body.Note.Trim(),
        };
        return RunAsync(access, http, async edit =>
        {
            if (!ResourceKinds.Actions.TryGetValue(rule.ResourceKind, out var actions))
            {
                throw new AccessRefusedException($"A rule is on one of: {string.Join(", ", ResourceKinds.Actions.Keys)}.");
            }
            rule.Action = rule.Action.Length == 0 ? actions[0] : rule.Action;
            if (!actions.Contains(rule.Action))
            {
                throw new AccessRefusedException($"On a {rule.ResourceKind} a rule is about: {string.Join(", ", actions)}.");
            }
            if (rule.Resource.Length is 0 or > 300)
            {
                throw new AccessRefusedException("A rule names its resource (1 to 300 characters; * for a pattern).");
            }
            if (rule.ResourceKind == ResourceKinds.Feature && !Permissions.IsFeature(rule.Resource))
            {
                throw new AccessRefusedException($"{rule.Resource} is not a feature: {string.Join(", ", Permissions.FeatureKeys)}.");
            }
            if (rule.Note is { Length: > 500 })
            {
                throw new AccessRefusedException("A note is at most 500 characters.");
            }
            if (rule.ExpiresAt is { } until && until <= clock.GetUtcNow())
            {
                throw new AccessRefusedException("That date has passed.");
            }
            await CheckSubjectAsync(edit.Db, rule.SubjectKind, rule.SubjectId, rule: true, ct);
            edit.Add(rule);
        }, "access.rule.add", $"{body.ResourceKind}:{body.Resource}", () => Results.Created($"/api/admin/access/rules/{rule.Id}", new { rule.Id }), ct);
    }

    private static Task<IResult> RemoveRuleAsync(Guid id, AccessService access, HttpContext http, CancellationToken ct) =>
        RunAsync(access, http, async edit => edit.Remove(await edit.Db.AccessRules.SingleOrDefaultAsync(r => r.Id == id, ct) ?? throw new KeyNotFoundException()),
            "access.rule.remove", id.ToString(), () => Results.NoContent(), ct);

    /// <summary>A change through <see cref="AccessService.ChangeAsync"/>: refused with the reason, 404 for what is not there.</summary>
    private static async Task<IResult> RunAsync(AccessService access, HttpContext http, Func<AccessService.Edit, Task> work, string audit, string target, Func<IResult> done, CancellationToken ct)
    {
        try
        {
            // The actor, so they cannot take their own access.manage away.
            var me = await http.RequestServices.GetRequiredService<UserManager<AppUser>>().GetUserAsync(http.User);
            await access.ChangeAsync(me, work, audit, target, ct);
            return done();
        }
        catch (AccessRefusedException ex)
        {
            return Refused(ex.Message);
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound();
        }
    }

    /// <summary>
    /// "May this person ...?": a permission, or an action on a resource; yes or no with the reasons in order,
    /// and the roles they hold and how.
    /// </summary>
    private static async Task<IResult> CheckAsync(Guid person, string? permission, string? kind, string? resource, string? action,
        UserManager<AppUser> users, AccessService access, CancellationToken ct)
    {
        if (await users.FindByIdAsync(person.ToString()) is not { } user)
        {
            return Results.NotFound();
        }
        var m = await access.MembershipAsync(user, ct);
        Decision decision;
        if (permission is { Length: > 0 })
        {
            if (!Permissions.IsKnown(permission))
            {
                return Refused($"{permission} is not a permission.");
            }
            decision = m.Check(permission);
        }
        else if (kind is { Length: > 0 } && resource is { Length: > 0 } && ResourceKinds.Actions.TryGetValue(kind, out var actions))
        {
            if (action is { Length: > 0 } && !actions.Contains(action))
            {
                return Refused($"On a {kind} the actions are: {string.Join(", ", actions)}.");
            }
            decision = m.Check(kind, resource, action);
        }
        else
        {
            return Refused("Ask about a permission, or a resource's kind and name.");
        }
        return Results.Ok(new
        {
            decision.Allowed, decision.Because, user.IsDisabled,
            roles = m.Snapshot.RolesOf(m.Principal, m.Now).Select(h => new { h.Role.Id, h.Role.Name, h.Via, h.Scope }),
        });
    }

    private static async Task<IResult> HistoryAsync(AppDbContext db, int? take, CancellationToken ct)
    {
        var rows = await db.AccessChanges.AsNoTracking().OrderByDescending(c => c.Id).Take(Math.Clamp(take ?? 100, 1, 1000)).ToListAsync(ct);
        var actors = rows.Where(r => r.ActorId is not null).Select(r => r.ActorId!.Value).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => actors.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.UserName, ct);
        return Results.Ok(rows.Select(r => new { r.Id, r.At, r.Kind, actor = r.ActorId is { } a ? names.GetValueOrDefault(a) ?? a.ToString() : null, r.Before, r.After, r.BulkId }));
    }
}
