using System.Globalization;
using System.Text.Json.Nodes;
using Llm.Api.Company;
using Llm.Api.Gateway;
using Llm.Api.Identity;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using static Llm.Api.Scim.ScimEndpoints;

namespace Llm.Api.Scim;

/// <summary>
/// /scim/v2/Users: people made, changed and deactivated by the identity provider.
/// Deactivating (active false, or DELETE) disables the person here at once: signed
/// out everywhere, API keys blocked at the gateway. Nobody is deleted from here.
/// </summary>
public static class ScimUsers
{
    private static readonly ILookup<Guid, (Guid Id, string Name)> NoGroups = Array.Empty<(Guid, (Guid, string))>().ToLookup(x => x.Item1, x => x.Item2);

    /// <summary>What a request says about a person; null leaves a value as it is.</summary>
    private sealed class Model
    {
        public string? UserName { get; set; }
        public string? Email { get; set; }
        public string? DisplayName { get; set; }
        public string? Formatted { get; set; }
        public string? GivenName { get; set; }
        public string? FamilyName { get; set; }
        public bool? Active { get; set; }
        public string? ExternalId { get; set; }
        public bool ExternalIdSet { get; set; }
    }

    public static async Task<IResult> ListAsync(HttpRequest req, AppDbContext db, UserManager<AppUser> users, IOptions<AuthOptions> auth, CancellationToken ct)
    {
        if (ScimFilter.Parse(req.Query["filter"]) is not { } conditions)
        {
            return Error(400, "This filter is not understood here: use comparisons with eq, joined by and.", "invalidFilter");
        }
        var q = await VisibleAsync(db, users);
        foreach (var c in conditions)
        {
            if (Where(q, c, users) is not { } next)
            {
                return Error(400, $"Filtering people by {c.Path} with {c.Op} is not supported here.", "invalidFilter");
            }
            q = next;
        }
        var (start, count) = Page(req);
        var total = await q.CountAsync(ct);
        var page = count == 0 ? [] : await q.OrderBy(u => u.CreatedAt).ThenBy(u => u.Id).Skip(start - 1).Take(count).ToListAsync(ct);
        var groups = await GroupsOfAsync(db, page.Select(u => u.Id), ct);
        return new ScimResult(List([.. page.Select(u => Resource(u, groups, Url(auth)))], total, start));
    }

    public static async Task<IResult> GetAsync(string id, AppDbContext db, UserManager<AppUser> users, IOptions<AuthOptions> auth, CancellationToken ct) =>
        await FindAsync(id, db, users) is { } user
            ? new ScimResult(Resource(user, await GroupsOfAsync(db, [user.Id], ct), Url(auth)))
            : NotFound(id);

    public static async Task<IResult> CreateAsync(HttpRequest req, CompanyPeople company, IOptions<AuthOptions> auth)
    {
        if (await BodyAsync(req) is not { } body)
        {
            return BadBody();
        }
        var m = FromResource(body);
        var userName = CompanyPeople.UserName(m.UserName);
        if (userName is null)
        {
            return Error(400, m.UserName is null ? "userName is required." : BadUserName(m.UserName), "invalidValue");
        }
        var email = CompanyPeople.Email(m.Email) ?? CompanyPeople.Email(m.UserName);
        if (email is null)
        {
            return Error(400, "An email address is needed: the gateway knows people by it.", "invalidValue");
        }
        var active = m.Active ?? true;
        try
        {
            var user = await company.CreateAsync(userName, email, Display(m, null), active ? "from SCIM" : "from SCIM, inactive", u =>
            {
                u.ScimExternalId = m.ExternalId;
                if (!active)
                {
                    u.IsDisabled = true;
                    u.DisabledReason = "scim";
                }
            });
            var resource = Resource(user, NoGroups, Url(auth));
            return new ScimResult(resource, StatusCodes.Status201Created, resource["meta"]!["location"]!.GetValue<string>());
        }
        catch (CompanyRefusedException ex)
        {
            return Refused(ex);
        }
    }

    public static async Task<IResult> ReplaceAsync(string id, HttpRequest req, AppDbContext db, UserManager<AppUser> users, CompanyPeople company, PeopleService people,
        Audit audit, IOptions<AuthOptions> auth, CancellationToken ct)
    {
        if (await FindAsync(id, db, users) is not { } user)
        {
            return NotFound(id);
        }
        if (await BodyAsync(req) is not { } body)
        {
            return BadBody();
        }
        return await ApplyAsync(user, FromResource(body), null, users, company, people, audit)
            ?? new ScimResult(Resource(user, await GroupsOfAsync(db, [user.Id], ct), Url(auth)));
    }

    public static async Task<IResult> PatchAsync(string id, HttpRequest req, AppDbContext db, UserManager<AppUser> users, CompanyPeople company, PeopleService people,
        Audit audit, IOptions<AuthOptions> auth, CancellationToken ct)
    {
        if (await FindAsync(id, db, users) is not { } user)
        {
            return NotFound(id);
        }
        if (await BodyAsync(req) is not { } body || PatchOperations(body) is not { } ops)
        {
            return Error(400, "The body is not a PatchOp with add, replace or remove operations.", "invalidSyntax");
        }
        var m = new Model();
        foreach (var (op, path, value) in ops)
        {
            if (op == "remove")
            {
                if (path?.Attribute == "externalid")
                {
                    m.ExternalId = null;
                    m.ExternalIdSet = true;
                }
                continue; // the other attributes here cannot be empty
            }
            if (path is null)
            {
                // No path: the value is an object of attributes, whose names may be paths themselves.
                foreach (var pair in value as JsonObject ?? new JsonObject())
                {
                    if (ScimFilter.ParsePath(pair.Key) is { } p)
                    {
                        Set(m, p, pair.Value);
                    }
                }
            }
            else
            {
                Set(m, path, value);
            }
        }
        return await ApplyAsync(user, m, user, users, company, people, audit)
            ?? new ScimResult(Resource(user, await GroupsOfAsync(db, [user.Id], ct), Url(auth)));
    }

    /// <summary>A person removed in the identity provider is disabled here, not deleted: an admin deletes them under People.</summary>
    public static async Task<IResult> DeleteAsync(string id, AppDbContext db, UserManager<AppUser> users, CompanyPeople company, PeopleService people, Audit audit)
    {
        if (await FindAsync(id, db, users) is not { } user)
        {
            return NotFound(id);
        }
        return await ApplyAsync(user, new Model { Active = false }, user, users, company, people, audit) ?? Results.NoContent();
    }

    private static async Task<IResult?> ApplyAsync(AppUser user, Model m, AppUser? current, UserManager<AppUser> users, CompanyPeople company, PeopleService people, Audit audit)
    {
        string? userName = null;
        string? email = null;
        if (m.UserName is not null && (userName = CompanyPeople.UserName(m.UserName)) is null)
        {
            return Error(400, BadUserName(m.UserName), "invalidValue");
        }
        if (m.Email is not null && (email = CompanyPeople.Email(m.Email)) is null)
        {
            return Error(400, $"\"{m.Email}\" is not an email address.", "invalidValue");
        }
        try
        {
            await company.AdoptAsync(user, "managed by SCIM");
            await company.UpdateAsync(user, userName, email, Display(m, current));
            if (m.ExternalIdSet && user.ScimExternalId != m.ExternalId)
            {
                user.ScimExternalId = m.ExternalId;
                await users.UpdateAsync(user);
            }
            if (m.Active == false && !user.IsDisabled)
            {
                await people.SetDisabledAsync(PeopleService.DirectorySync, user, disabled: true, reason: "scim");
            }
            else if (m.Active == true && user.IsDisabled && user.DisabledReason == "scim")
            {
                // Only what SCIM disabled: an admin's or a safeguard's decision stands.
                await people.SetDisabledAsync(PeopleService.DirectorySync, user, disabled: false, reason: "scim");
                try
                {
                    await people.ProvisionGatewayAsync(user);
                }
                catch (GatewayException ex)
                {
                    await audit.WriteAsync("person.provision_gateway", user.UserName, success: false, detail: ex.Message);
                }
            }
        }
        catch (CompanyRefusedException ex)
        {
            return Refused(ex);
        }
        catch (PeopleException ex)
        {
            return Error(400, ex.Message, "mutability");
        }
        return null;
    }

    private static void Set(Model m, ScimPath path, JsonNode? value)
    {
        switch (path.Attribute)
        {
            case "username":
                m.UserName = Text(value);
                break;
            case "displayname":
                m.DisplayName = Text(value);
                break;
            case "externalid":
                m.ExternalId = Text(value);
                m.ExternalIdSet = true;
                break;
            case "active":
                m.Active = Bool(value);
                break;
            case "name" when path.Sub is null && value is JsonObject name:
                m.Formatted = Text(name["formatted"]) ?? m.Formatted;
                m.GivenName = Text(name["givenName"]) ?? m.GivenName;
                m.FamilyName = Text(name["familyName"]) ?? m.FamilyName;
                break;
            case "name" when path.Sub == "formatted":
                m.Formatted = Text(value);
                break;
            case "name" when path.Sub == "givenname":
                m.GivenName = Text(value);
                break;
            case "name" when path.Sub == "familyname":
                m.FamilyName = Text(value);
                break;
            case "emails" when path.Sub == "value":
                m.Email = Text(value);
                break;
            case "emails" when path.Sub is null:
                m.Email = PickEmail(value) ?? m.Email;
                break;
        }
    }

    private static Model FromResource(JsonObject body)
    {
        var m = new Model
        {
            UserName = Text(body["userName"]),
            DisplayName = Text(body["displayName"]),
            Email = PickEmail(body["emails"]),
            Active = Bool(body["active"]),
            ExternalId = Text(body["externalId"]),
            ExternalIdSet = body.ContainsKey("externalId"),
        };
        if (body["name"] is JsonObject name)
        {
            m.Formatted = Text(name["formatted"]);
            m.GivenName = Text(name["givenName"]);
            m.FamilyName = Text(name["familyName"]);
        }
        return m;
    }

    /// <summary>The primary email, else the work one, else the first.</summary>
    private static string? PickEmail(JsonNode? emails)
    {
        var list = emails switch
        {
            JsonArray a => a.OfType<JsonObject>().ToList(),
            JsonObject o => [o],
            _ => [],
        };
        var chosen = list.FirstOrDefault(e => Bool(e["primary"]) == true)
            ?? list.FirstOrDefault(e => string.Equals(Text(e["type"]), "work", StringComparison.OrdinalIgnoreCase))
            ?? list.FirstOrDefault();
        return Text(chosen?["value"]);
    }

    /// <summary>
    /// The name to show: displayName, else name.formatted, else given and family names.
    /// A change of one of those two keeps the other from the current name.
    /// </summary>
    private static string? Display(Model m, AppUser? current)
    {
        if (!string.IsNullOrWhiteSpace(m.DisplayName))
        {
            return m.DisplayName;
        }
        if (!string.IsNullOrWhiteSpace(m.Formatted))
        {
            return m.Formatted;
        }
        if (m.GivenName is null && m.FamilyName is null)
        {
            return null;
        }
        var parts = (current?.DisplayName ?? "").Split(' ', 2, StringSplitOptions.TrimEntries);
        var joined = $"{m.GivenName ?? parts[0]} {m.FamilyName ?? (parts.Length > 1 ? parts[1] : "")}".Trim();
        return joined.Length == 0 ? null : joined;
    }

    /// <summary>Everyone but local admins: they stay a way in that the identity provider cannot change.</summary>
    private static async Task<IQueryable<AppUser>> VisibleAsync(AppDbContext db, UserManager<AppUser> users)
    {
        var localAdmins = (await users.GetUsersInRoleAsync(Roles.Admin)).Where(u => u.Source == UserSource.Local).Select(u => u.Id).ToList();
        return db.Users.Where(u => !localAdmins.Contains(u.Id));
    }

    private static async Task<AppUser?> FindAsync(string id, AppDbContext db, UserManager<AppUser> users) =>
        Guid.TryParse(id, out var guid) ? await (await VisibleAsync(db, users)).FirstOrDefaultAsync(u => u.Id == guid) : null;

    private static IQueryable<AppUser>? Where(IQueryable<AppUser> q, ScimCondition c, UserManager<AppUser> users)
    {
        if (c.Op != "eq" || c.Value is null)
        {
            return null;
        }
        var v = c.Value;
        switch (c.Path)
        {
            case "username":
                var name = users.NormalizeName(CompanyPeople.UserName(v) ?? "\0");
                if (!v.Contains('@', StringComparison.Ordinal))
                {
                    return q.Where(u => u.NormalizedUserName == name);
                }
                // An email-like userName finds that email, or the person SCIM or company sign-in made from it.
                var mail = users.NormalizeEmail(v);
                return q.Where(u => u.NormalizedEmail == mail || (u.NormalizedUserName == name && u.Source == UserSource.Oidc));
            case "emails" or "emails.value":
                var email = users.NormalizeEmail(v);
                return q.Where(u => u.NormalizedEmail == email);
            case "externalid":
                return q.Where(u => u.ScimExternalId == v);
            case "id":
                return Guid.TryParse(v, out var id) ? q.Where(u => u.Id == id) : q.Where(_ => false);
            case "displayname":
                return q.Where(u => u.DisplayName == v);
            case "active" when bool.TryParse(v, out var active):
                return q.Where(u => u.IsDisabled != active);
            default:
                return null;
        }
    }

    private static async Task<ILookup<Guid, (Guid Id, string Name)>> GroupsOfAsync(AppDbContext db, IEnumerable<Guid> people, CancellationToken ct)
    {
        var ids = people.ToList();
        var rows = await db.GroupMembers.AsNoTracking().Where(m => ids.Contains(m.UserId))
            .Join(db.Groups.AsNoTracking().Where(g => g.Scim), m => m.GroupId, g => g.Id, (m, g) => new { m.UserId, g.Id, g.Name })
            .ToListAsync(ct);
        return rows.ToLookup(r => r.UserId, r => (r.Id, r.Name));
    }

    private static JsonObject Resource(AppUser u, ILookup<Guid, (Guid Id, string Name)> groups, string baseUrl)
    {
        var o = new JsonObject
        {
            ["schemas"] = new JsonArray(ScimFilter.UserSchema),
            ["id"] = u.Id.ToString(),
            ["userName"] = u.UserName,
            ["name"] = new JsonObject { ["formatted"] = u.DisplayName },
            ["displayName"] = u.DisplayName,
            ["emails"] = new JsonArray(new JsonObject { ["value"] = u.Email, ["type"] = "work", ["primary"] = true }),
            ["active"] = !u.IsDisabled,
            ["groups"] = new JsonArray([.. groups[u.Id].Select(g => new JsonObject
            {
                ["value"] = g.Id.ToString(),
                ["display"] = g.Name,
                ["$ref"] = $"{baseUrl}/Groups/{g.Id}",
            })]),
            ["meta"] = new JsonObject
            {
                ["resourceType"] = "User",
                ["created"] = u.CreatedAt.ToString("o", CultureInfo.InvariantCulture),
                ["location"] = $"{baseUrl}/Users/{u.Id}",
            },
        };
        if (u.ScimExternalId is not null)
        {
            o["externalId"] = u.ScimExternalId;
        }
        return o;
    }

    private static string Url(IOptions<AuthOptions> auth) => auth.Value.Origin + Base;

    private static ScimResult NotFound(string id) => Error(404, $"There is no person {id} here.");

    private static string BadUserName(string value) =>
        $"\"{value}\" cannot be a username here: 2-64 characters of a-z 0-9 . _ - (an email-like value gives its part before the @).";

    public static ScimResult Refused(CompanyRefusedException ex) =>
        Error(ex.Conflict ? 409 : 400, char.ToUpperInvariant(ex.Message[0]) + ex.Message[1..] + ".", ex.Conflict ? "uniqueness" : "invalidValue");
}
