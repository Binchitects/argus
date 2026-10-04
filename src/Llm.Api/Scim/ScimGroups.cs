using System.Globalization;
using System.Text.Json.Nodes;
using Llm.Api.Identity;
using Llm.Core.Access;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using static Llm.Api.Scim.ScimEndpoints;

namespace Llm.Api.Scim;

/// <summary>
/// /scim/v2/Groups: groups the identity provider makes and fills. They are app groups
/// that access rules (tools, models) can name; the provider decides their name and
/// members, so admins do not change those here. Groups made in the app are not seen.
/// </summary>
public static class ScimGroups
{
    public static async Task<IResult> ListAsync(HttpRequest req, AppDbContext db, IOptions<AuthOptions> auth, CancellationToken ct)
    {
        if (ScimFilter.Parse(req.Query["filter"]) is not { } conditions)
        {
            return Error(400, "This filter is not understood here: use comparisons with eq, joined by and.", "invalidFilter");
        }
        var q = db.Groups.AsNoTracking().Where(g => g.Scim);
        foreach (var c in conditions)
        {
            if (Where(q, c, db) is not { } next)
            {
                return Error(400, $"Filtering groups by {c.Path} with {c.Op} is not supported here.", "invalidFilter");
            }
            q = next;
        }
        var (start, count) = Page(req);
        var total = await q.CountAsync(ct);
        var page = count == 0 ? [] : await q.OrderBy(g => g.CreatedAt).ThenBy(g => g.Id).Skip(start - 1).Take(count).ToListAsync(ct);
        var members = WithMembers(req) ? await MembersOfAsync(db, page.Select(g => g.Id), ct) : null;
        return new ScimResult(List([.. page.Select(g => Resource(g, members, Url(auth)))], total, start));
    }

    public static async Task<IResult> GetAsync(string id, HttpRequest req, AppDbContext db, IOptions<AuthOptions> auth, CancellationToken ct) =>
        await FindAsync(id, db) is { } group
            ? new ScimResult(Resource(group, WithMembers(req) ? await MembersOfAsync(db, [group.Id], ct) : null, Url(auth)))
            : NotFound(id);

    public static async Task<IResult> CreateAsync(HttpRequest req, AppDbContext db, UserManager<AppUser> users, Audit audit, Models.KeyAccessWatcher keys,
        IOptions<AuthOptions> auth, CancellationToken ct)
    {
        if (await BodyAsync(req) is not { } body)
        {
            return BadBody();
        }
        var name = Text(body["displayName"])?.Trim();
        if (await CheckNameAsync(db, null, name) is { } problem)
        {
            return problem;
        }
        var group = new Group { Name = name!, Scim = true, ExternalId = Text(body["externalId"]) };
        db.Groups.Add(group);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("group.create", group.Name, detail: "from SCIM");
        await SetMembersAsync(group, Ids(body["members"]), db, users, audit, ct);
        keys.Wake();
        var resource = Resource(group, await MembersOfAsync(db, [group.Id], ct), Url(auth));
        return new ScimResult(resource, StatusCodes.Status201Created, resource["meta"]!["location"]!.GetValue<string>());
    }

    public static async Task<IResult> ReplaceAsync(string id, HttpRequest req, AppDbContext db, UserManager<AppUser> users, Audit audit, Models.KeyAccessWatcher keys,
        IOptions<AuthOptions> auth, CancellationToken ct)
    {
        if (await FindAsync(id, db) is not { } group)
        {
            return NotFound(id);
        }
        if (await BodyAsync(req) is not { } body)
        {
            return BadBody();
        }
        if (await RenameAsync(group, Text(body["displayName"]), db, audit) is { } problem)
        {
            return problem;
        }
        if (body.ContainsKey("externalId"))
        {
            group.ExternalId = Text(body["externalId"]);
            await db.SaveChangesAsync(ct);
        }
        await SetMembersAsync(group, Ids(body["members"]), db, users, audit, ct);
        keys.Wake();
        return new ScimResult(Resource(group, await MembersOfAsync(db, [group.Id], ct), Url(auth)));
    }

    public static async Task<IResult> PatchAsync(string id, HttpRequest req, AppDbContext db, UserManager<AppUser> users, Audit audit, Models.KeyAccessWatcher keys,
        IOptions<AuthOptions> auth, CancellationToken ct)
    {
        if (await FindAsync(id, db) is not { } group)
        {
            return NotFound(id);
        }
        if (await BodyAsync(req) is not { } body || PatchOperations(body) is not { } ops)
        {
            return Error(400, "The body is not a PatchOp with add, replace or remove operations.", "invalidSyntax");
        }
        var members = (await db.GroupMembers.AsNoTracking().Where(m => m.GroupId == group.Id).Select(m => m.UserId).ToListAsync(ct)).ToHashSet();
        string? name = null;
        foreach (var (op, path, value) in ops)
        {
            switch (path?.Attribute)
            {
                case null when value is JsonObject attributes:
                    name = Text(attributes["displayName"]) ?? name;
                    if (attributes.ContainsKey("externalId"))
                    {
                        group.ExternalId = Text(attributes["externalId"]);
                    }
                    if (attributes["members"] is { } listed)
                    {
                        if (op == "replace")
                        {
                            members.Clear();
                        }
                        members.UnionWith(Ids(listed));
                    }
                    break;
                case "displayname" when op != "remove":
                    name = Text(value);
                    break;
                case "externalid":
                    group.ExternalId = op == "remove" ? null : Text(value);
                    break;
                case "members" when op == "remove":
                    if (path.Filter is { Path: "value", Op: "eq", Value: { } one } && Guid.TryParse(one, out var gone))
                    {
                        members.Remove(gone);
                    }
                    else if (path.Filter is null && value is null)
                    {
                        members.Clear();
                    }
                    else if (path.Filter is null)
                    {
                        members.ExceptWith(Ids(value));
                    }
                    break;
                case "members":
                    if (op == "replace")
                    {
                        members.Clear();
                    }
                    members.UnionWith(Ids(value));
                    break;
            }
        }
        if (await RenameAsync(group, name, db, audit) is { } problem)
        {
            return problem;
        }
        await db.SaveChangesAsync(ct);
        await SetMembersAsync(group, members, db, users, audit, ct);
        keys.Wake();
        return new ScimResult(Resource(group, await MembersOfAsync(db, [group.Id], ct), Url(auth)));
    }

    public static async Task<IResult> DeleteAsync(string id, AppDbContext db, Audit audit, Models.KeyAccessWatcher keys, CancellationToken ct)
    {
        if (await FindAsync(id, db) is not { } group)
        {
            return NotFound(id);
        }
        db.Groups.Remove(group);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("group.delete", group.Name, detail: "from SCIM");
        keys.Wake();
        return Results.NoContent();
    }

    private static async Task<ScimResult?> RenameAsync(Group group, string? name, AppDbContext db, Audit audit)
    {
        name = name?.Trim();
        if (name is null || name == group.Name)
        {
            return null;
        }
        if (await CheckNameAsync(db, group.Id, name) is { } problem)
        {
            return problem;
        }
        var was = group.Name;
        group.Name = name;
        await db.SaveChangesAsync();
        await audit.WriteAsync("group.update", group.Name, detail: $"renamed from {was} by SCIM");
        return null;
    }

    private static async Task<ScimResult?> CheckNameAsync(AppDbContext db, Guid? id, string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 100)
        {
            return Error(400, "A group needs a displayName of up to 100 characters.", "invalidValue");
        }
        var pattern = Like(name);
        if (await db.Groups.AnyAsync(g => g.Id != id && EF.Functions.ILike(g.Name, pattern)))
        {
            return Error(409, $"There is already a group named {name} here.", "uniqueness");
        }
        return null;
    }

    /// <summary>Makes the members exactly these, of the people SCIM may see; each change is audited.</summary>
    private static async Task SetMembersAsync(Group group, IEnumerable<Guid> wanted, AppDbContext db, UserManager<AppUser> users, Audit audit, CancellationToken ct)
    {
        var target = wanted.ToHashSet();
        var localAdmins = (await users.GetUsersInRoleAsync(Roles.Admin)).Where(u => u.Source == UserSource.Local).Select(u => u.Id).ToHashSet();
        target.ExceptWith(localAdmins);
        var people = await db.Users.AsNoTracking().Where(u => target.Contains(u.Id)).Select(u => new { u.Id, u.UserName }).ToDictionaryAsync(u => u.Id, u => u.UserName, ct);
        var current = await db.GroupMembers.Where(m => m.GroupId == group.Id).ToListAsync(ct);
        var gone = current.Where(m => !people.ContainsKey(m.UserId)).ToList();
        var added = people.Keys.Where(p => current.All(m => m.UserId != p)).ToList();
        if (gone.Count == 0 && added.Count == 0)
        {
            return;
        }
        db.GroupMembers.RemoveRange(gone);
        db.GroupMembers.AddRange(added.Select(p => new GroupMember { GroupId = group.Id, UserId = p }));
        await db.SaveChangesAsync(ct);
        var names = gone.Count == 0 ? [] : await db.Users.AsNoTracking().Where(u => gone.Select(m => m.UserId).Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.UserName, ct);
        foreach (var m in gone)
        {
            await audit.WriteAsync("group.remove_member", names.GetValueOrDefault(m.UserId), detail: $"{group.Name}, by SCIM");
        }
        foreach (var p in added)
        {
            await audit.WriteAsync("group.add_member", people[p], detail: $"{group.Name}, by SCIM");
        }
    }

    /// <summary>The ids of [{"value": "..."}] (or one such object); anything else is left out.</summary>
    private static List<Guid> Ids(JsonNode? node)
    {
        var items = node switch
        {
            JsonArray a => a.OfType<JsonObject>().ToList(),
            JsonObject o => [o],
            _ => [],
        };
        return [.. items.Select(i => Text(i["value"])).Select(v => Guid.TryParse(v, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty)];
    }

    private static IQueryable<Group>? Where(IQueryable<Group> q, ScimCondition c, AppDbContext db)
    {
        if (c.Op != "eq" || c.Value is null)
        {
            return null;
        }
        var v = c.Value;
        switch (c.Path)
        {
            case "displayname":
                var pattern = Like(v);
                return q.Where(g => EF.Functions.ILike(g.Name, pattern));
            case "externalid":
                return q.Where(g => g.ExternalId == v);
            case "id":
                return Guid.TryParse(v, out var id) ? q.Where(g => g.Id == id) : q.Where(_ => false);
            case "members" or "members.value":
                return Guid.TryParse(v, out var member) ? q.Where(g => db.GroupMembers.Any(m => m.GroupId == g.Id && m.UserId == member)) : q.Where(_ => false);
            default:
                return null;
        }
    }

    /// <summary>excludedAttributes=members (Entra asks so), or attributes without members, leaves the members out.</summary>
    private static bool WithMembers(HttpRequest req)
    {
        static bool Lists(string? list, string name) =>
            (list ?? "").Split(',', StringSplitOptions.TrimEntries).Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        var only = req.Query["attributes"].ToString();
        return !Lists(req.Query["excludedAttributes"], "members") && (only.Length == 0 || Lists(only, "members"));
    }

    private static async Task<Group?> FindAsync(string id, AppDbContext db) =>
        Guid.TryParse(id, out var guid) ? await db.Groups.FirstOrDefaultAsync(g => g.Id == guid && g.Scim) : null;

    private static async Task<ILookup<Guid, (Guid Id, string Name)>> MembersOfAsync(AppDbContext db, IEnumerable<Guid> groups, CancellationToken ct)
    {
        var ids = groups.ToList();
        var rows = await db.GroupMembers.AsNoTracking().Where(m => ids.Contains(m.GroupId))
            .Join(db.Users.AsNoTracking(), m => m.UserId, u => u.Id, (m, u) => new { m.GroupId, u.Id, u.DisplayName })
            .ToListAsync(ct);
        return rows.ToLookup(r => r.GroupId, r => (r.Id, r.DisplayName));
    }

    private static JsonObject Resource(Group g, ILookup<Guid, (Guid Id, string Name)>? members, string baseUrl)
    {
        var o = new JsonObject
        {
            ["schemas"] = new JsonArray(ScimFilter.GroupSchema),
            ["id"] = g.Id.ToString(),
            ["displayName"] = g.Name,
            ["meta"] = new JsonObject
            {
                ["resourceType"] = "Group",
                ["created"] = g.CreatedAt.ToString("o", CultureInfo.InvariantCulture),
                ["location"] = $"{baseUrl}/Groups/{g.Id}",
            },
        };
        if (g.ExternalId is not null)
        {
            o["externalId"] = g.ExternalId;
        }
        if (members is not null)
        {
            o["members"] = new JsonArray([.. members[g.Id].Select(m => new JsonObject
            {
                ["value"] = m.Id.ToString(),
                ["display"] = m.Name,
                ["type"] = "User",
                ["$ref"] = $"{baseUrl}/Users/{m.Id}",
            })]);
        }
        return o;
    }

    /// <summary>The name as an ILIKE pattern that matches only itself, ignoring case.</summary>
    private static string Like(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);

    private static string Url(IOptions<AuthOptions> auth) => auth.Value.Origin + Base;

    private static ScimResult NotFound(string id) => Error(404, $"There is no SCIM group {id} here.");
}
