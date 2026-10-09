using System.Globalization;
using System.Text;
using Llm.Api.Endpoints;
using Llm.Api.Gateway;
using Llm.Api.Identity;
using Llm.Api.Ldap;
using Llm.Core.Access;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Access;

/// <param name="Priority">Its members' place in the answers' line, <see cref="GroupEndpoints.MinPriority"/> to <see cref="GroupEndpoints.MaxPriority"/>; null: unchanged (0 for a new group).</param>
public sealed record GroupRequest(string? Name = null, string? Description = null, string? Directory = null, int? Priority = null);

public sealed record MembersRequest(Guid[] UserIds);

/// <summary>A group's policies, all of them at once: null keeps (or puts back) the company's setting.</summary>
/// <param name="RetentionDays">Members' chats and files are kept this many days.</param>
/// <param name="Credit">What the group may spend a month, across the chat and API keys.</param>
/// <param name="CreditPerMember">The credit is each member's, not shared.</param>
/// <param name="CostCentre">The label its spend is charged to (the chargeback report).</param>
/// <param name="SecretScanning">refuse, mask or off.</param>
/// <param name="RedactPii">mask or off.</param>
/// <param name="Moderation">check or off.</param>
/// <param name="BlockedPatterns">Whether the blocked words apply.</param>
/// <param name="RequestsPerMinute">Requests a minute each member's API key may send; 0: no limit.</param>
/// <param name="TokensPerMinute">Tokens a minute each member's API key may use; 0: no limit.</param>
public sealed record PoliciesRequest(int? RetentionDays = null, decimal? Credit = null, bool CreditPerMember = false, string? CostCentre = null,
    string? SecretScanning = null, string? RedactPii = null, string? Moderation = null, bool? BlockedPatterns = null, int? RequestsPerMinute = null, int? TokensPerMinute = null);

/// <summary>Groups for access rules, retention, credit, safeguards and API keys' rate limits: app groups whose members are chosen here, and directory groups.</summary>
public static class GroupEndpoints
{
    private const string ScimOwned = "The company's identity provider decides this group's name and members (SCIM).";
    public const int MinPriority = -10;
    public const int MaxPriority = 10;

    public static void MapGroups(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin/groups").RequireAuthorization(AdminEndpoints.Policy);
        g.MapGet("", ListAsync);
        g.MapPost("", CreateAsync);
        g.MapGet("/directory", DirectoryAsync);
        g.MapGet("/chargeback", ChargebackAsync);
        g.MapPut("/{id:guid}/policies", PoliciesAsync);
        g.MapGet("/{id:guid}", GetAsync);
        g.MapPatch("/{id:guid}", UpdateAsync);
        g.MapDelete("/{id:guid}", DeleteAsync);
        g.MapPost("/{id:guid}/members", AddMembersAsync);
        g.MapDelete("/{id:guid}/members/{userId:guid}", RemoveMemberAsync);
    }

    private static async Task<IResult> ListAsync(AppDbContext db)
    {
        var groups = await db.Groups.AsNoTracking().OrderBy(x => x.Name).ToListAsync();
        var counts = await db.GroupMembers.AsNoTracking().GroupBy(m => m.GroupId).Select(x => new { x.Key, n = x.Count() }).ToDictionaryAsync(x => x.Key, x => x.n);
        var directoryPeople = groups.Any(x => x.Directory is not null)
            ? await db.Users.AsNoTracking().Where(u => u.DirectoryGroups.Count > 0).Select(u => u.DirectoryGroups).ToListAsync()
            : [];
        return Results.Ok(groups.Select(x => new
        {
            x.Id, x.Name, x.Description, x.Directory, x.Scim, x.Priority, x.CreatedAt,
            members = x.Directory is { } d ? directoryPeople.Count(m => AccessService.InDirectoryGroup(m, d)) : counts.GetValueOrDefault(x.Id),
            x.RetentionDays, x.Credit, x.CreditPerMember, x.CostCentre, x.RequestsPerMinute, x.TokensPerMinute,
        }));
    }

    /// <summary>The groups the directory has put people in, to choose a directory group from.</summary>
    private static async Task<IResult> DirectoryAsync(AppDbContext db)
    {
        var all = await db.Users.AsNoTracking().Where(u => u.DirectoryGroups.Count > 0).Select(u => u.DirectoryGroups).ToListAsync();
        return Results.Ok(all.SelectMany(x => x).GroupBy(x => x, StringComparer.OrdinalIgnoreCase)
            .Select(x => new { dn = x.Key, name = LdapDirectory.CommonName(x.Key), people = x.Count() })
            .OrderBy(x => x.name, StringComparer.OrdinalIgnoreCase));
    }

    private static async Task<IResult> GetAsync(Guid id, AppDbContext db, CreditBook book, ILiteLlm gateway, CancellationToken ct)
    {
        if (await db.Groups.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) is not { } group)
        {
            return Results.NotFound();
        }
        List<AppUser> people;
        if (group.Directory is { } d)
        {
            var candidates = await db.Users.AsNoTracking().Where(u => u.DirectoryGroups.Count > 0).ToListAsync(ct);
            people = [.. candidates.Where(u => AccessService.InDirectoryGroup(u.DirectoryGroups, d))];
        }
        else
        {
            people = await db.GroupMembers.AsNoTracking().Where(m => m.GroupId == id)
                .Join(db.Users.AsNoTracking(), m => m.UserId, u => u.Id, (_, u) => u).ToListAsync(ct);
        }
        // This month's spend, each member's and the group's, when the gateway's request log can be read.
        var month = await book.ReadAsync(gateway, ct);
        decimal? Spent(AppUser u) => month is null ? null : month.Spend.GetValueOrDefault(u.Email ?? "");
        return Results.Ok(new
        {
            group.Id, group.Name, group.Description, group.Directory, group.Scim, group.Priority, group.CreatedAt,
            members = people.OrderBy(u => u.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(u => new { u.Id, u.UserName, u.DisplayName, u.Email, u.IsDisabled, spend = Spent(u) }),
            policies = new
            {
                group.RetentionDays, group.Credit, group.CreditPerMember, group.CostCentre,
                group.SecretScanning, group.RedactPii, group.Moderation, group.BlockedPatterns, group.RequestsPerMinute, group.TokensPerMinute,
            },
            spentThisMonth = month is null ? (decimal?)null : people.Sum(u => month.Spend.GetValueOrDefault(u.Email ?? "")),
        });
    }

    private static async Task<IResult> CreateAsync(GroupRequest body, AppDbContext db, Audit audit)
    {
        var name = (body.Name ?? "").Trim();
        if (await CheckAsync(db, null, name, body.Directory) is { } problem)
        {
            return problem;
        }
        if (PriorityProblem(body.Priority) is { } bad)
        {
            return bad;
        }
        var group = new Group { Name = name, Description = Clean(body.Description), Directory = Clean(body.Directory), Priority = body.Priority ?? 0 };
        db.Groups.Add(group);
        await db.SaveChangesAsync();
        await audit.WriteAsync("group.create", group.Name,
            detail: (group.Directory is { } d ? $"directory group {d}" : "app group") + (group.Priority != 0 ? $", priority {group.Priority}" : ""));
        return Results.Created($"/api/admin/groups/{group.Id}", new { group.Id, group.Name });
    }

    private static async Task<IResult> UpdateAsync(Guid id, GroupRequest body, AppDbContext db, Audit audit, Models.KeyAccessWatcher keys)
    {
        if (await db.Groups.SingleOrDefaultAsync(x => x.Id == id) is not { } group)
        {
            return Results.NotFound();
        }
        if (group.Scim && body.Name is not null && body.Name.Trim() != group.Name)
        {
            return AuthEndpoints.Problem(400, "scim", ScimOwned);
        }
        var name = body.Name is null ? group.Name : body.Name.Trim();
        var directory = body.Directory is null ? group.Directory : Clean(body.Directory);
        if ((group.Directory is null) != (directory is null))
        {
            return AuthEndpoints.Problem(400, "kind", "An app group cannot become a directory group, or the other way round. Make a new group.");
        }
        if (await CheckAsync(db, id, name, directory) is { } problem)
        {
            return problem;
        }
        if (PriorityProblem(body.Priority) is { } bad)
        {
            return bad;
        }
        var priorityWas = group.Priority;
        group.Name = name;
        group.Directory = directory;
        group.Priority = body.Priority ?? group.Priority;
        if (body.Description is not null)
        {
            group.Description = Clean(body.Description);
        }
        await db.SaveChangesAsync();
        await audit.WriteAsync("group.update", group.Name, detail: group.Priority != priorityWas ? $"priority {priorityWas} → {group.Priority}" : null);
        keys.Wake();
        return Results.NoContent();
    }

    private static IResult? PriorityProblem(int? priority) => priority is < MinPriority or > MaxPriority
        ? AuthEndpoints.Problem(400, "priority", $"A priority is {MinPriority} to {MaxPriority}: higher goes first in the answers' line, 0 is everyone's.")
        : null;

    private static async Task<IResult> DeleteAsync(Guid id, AppDbContext db, Audit audit, Models.KeyAccessWatcher keys)
    {
        if (await db.Groups.SingleOrDefaultAsync(x => x.Id == id) is not { } group)
        {
            return Results.NotFound();
        }
        db.Groups.Remove(group);
        await db.SaveChangesAsync();
        await audit.WriteAsync("group.delete", group.Name);
        keys.Wake();
        return Results.NoContent();
    }

    private static async Task<IResult> AddMembersAsync(Guid id, MembersRequest body, AppDbContext db, Audit audit, Models.KeyAccessWatcher keys)
    {
        if (await db.Groups.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id) is not { } group)
        {
            return Results.NotFound();
        }
        if (group.Directory is not null)
        {
            return AuthEndpoints.Problem(400, "directory", "The directory decides who is in a directory group.");
        }
        if (group.Scim)
        {
            return AuthEndpoints.Problem(400, "scim", ScimOwned);
        }
        var ids = (body.UserIds ?? []).Distinct().ToList();
        var people = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).Select(u => new { u.Id, u.UserName }).ToListAsync();
        if (people.Count != ids.Count)
        {
            return AuthEndpoints.Problem(400, "person", "Someone in the list does not exist.");
        }
        var already = await db.GroupMembers.Where(m => m.GroupId == id && ids.Contains(m.UserId)).Select(m => m.UserId).ToListAsync();
        foreach (var p in people.Where(p => !already.Contains(p.Id)))
        {
            db.GroupMembers.Add(new GroupMember { GroupId = id, UserId = p.Id });
            await audit.WriteAsync("group.add_member", p.UserName, detail: group.Name);
        }
        await db.SaveChangesAsync();
        keys.Wake();
        return Results.NoContent();
    }

    private static async Task<IResult> RemoveMemberAsync(Guid id, Guid userId, AppDbContext db, Audit audit, Models.KeyAccessWatcher keys)
    {
        if (await db.GroupMembers.SingleOrDefaultAsync(m => m.GroupId == id && m.UserId == userId) is not { } member)
        {
            return Results.NotFound();
        }
        if (await db.Groups.AnyAsync(x => x.Id == id && x.Scim))
        {
            return AuthEndpoints.Problem(400, "scim", ScimOwned);
        }
        db.GroupMembers.Remove(member);
        await db.SaveChangesAsync();
        var name = await db.Groups.Where(x => x.Id == id).Select(x => x.Name).SingleAsync();
        var person = await db.Users.Where(u => u.Id == userId).Select(u => u.UserName).SingleAsync();
        await audit.WriteAsync("group.remove_member", person, detail: name);
        keys.Wake();
        return Results.NoContent();
    }

    /// <summary>Sets a group's retention, credit, cost centre, safeguards and rate limits. Audited; the gateway's teams and keys follow (KeyAccessWatcher).</summary>
    private static async Task<IResult> PoliciesAsync(Guid id, PoliciesRequest body, AppDbContext db, Audit audit, Models.KeyAccessWatcher keys)
    {
        if (await db.Groups.SingleOrDefaultAsync(x => x.Id == id) is not { } group)
        {
            return Results.NotFound();
        }
        if (body.RetentionDays is < 1 or > 36500)
        {
            return AuthEndpoints.Problem(400, "retention", "Keep chats from 1 to 36,500 days, or leave it empty for the company's setting.");
        }
        if (body.Credit is < 0 or > 1_000_000_000)
        {
            return AuthEndpoints.Problem(400, "credit", "Credit is a number of dollars from 0, or empty for no group limit.");
        }
        if (body.RequestsPerMinute is < 0 or > RateLimits.MaxRequests || body.TokensPerMinute is < 0 or > RateLimits.MaxTokens)
        {
            return AuthEndpoints.Problem(400, "rate_limits",
                $"Requests a minute are 0 (no limit) to {RateLimits.MaxRequests:N0}, tokens a minute 0 to {RateLimits.MaxTokens:N0}; empty: the company's setting.");
        }
        if (body.CostCentre?.Trim() is { Length: > 100 })
        {
            return AuthEndpoints.Problem(400, "cost_centre", "A cost centre is at most 100 characters.");
        }
        if ((Invalid(body.SecretScanning, Safeguards.Safeguards.SecretLevels) ?? Invalid(body.RedactPii, Safeguards.Safeguards.PiiLevels) ??
            Invalid(body.Moderation, Safeguards.Safeguards.ModerationLevels)) is { } bad)
        {
            return AuthEndpoints.Problem(400, "safeguards", $"\"{bad}\" is not one of the choices.");
        }
        group.RetentionDays = body.RetentionDays;
        group.Credit = body.Credit;
        group.CreditPerMember = body.Credit is not null && body.CreditPerMember;
        group.CostCentre = Clean(body.CostCentre);
        group.SecretScanning = Clean(body.SecretScanning);
        group.RedactPii = Clean(body.RedactPii);
        group.Moderation = Clean(body.Moderation);
        group.BlockedPatterns = body.BlockedPatterns;
        group.RequestsPerMinute = body.RequestsPerMinute;
        group.TokensPerMinute = body.TokensPerMinute;
        await db.SaveChangesAsync();
        await audit.WriteAsync("group.policies", group.Name, detail: Describe(group));
        keys.Wake();
        return Results.NoContent();

        static string? Invalid(string? value, string[] levels) => string.IsNullOrWhiteSpace(value) || levels.Contains(value.Trim()) ? null : value;
    }

    /// <summary>The policies in a line, for the audit log.</summary>
    private static string Describe(Group g) => string.Join("; ", new[]
    {
        $"chats kept {(g.RetentionDays is { } d ? $"{d} days" : "as the company's")}",
        $"credit {(g.Credit is { } c ? Credit.Money(c) + (g.CreditPerMember ? " each member" : " shared") : "none")}",
        g.CostCentre is { } cc ? $"cost centre {cc}" : null,
        g.SecretScanning is { } s ? $"secrets {s}" : null,
        g.RedactPii is { } p ? $"personal data {p}" : null,
        g.Moderation is { } m ? $"model's check {m}" : null,
        g.BlockedPatterns is { } b ? $"blocked words {(b ? "on" : "off")}" : null,
        g.RequestsPerMinute is { } r ? $"requests a minute {(r == 0 ? "no limit" : r.ToString("N0", CultureInfo.InvariantCulture))}" : null,
        g.TokensPerMinute is { } t ? $"tokens a minute {(t == 0 ? "no limit" : t.ToString("N0", CultureInfo.InvariantCulture))}" : null,
    }.OfType<string>());

    /// <summary>
    /// Spend per group and cost centre, month by month (UTC), from <paramref name="from"/> to <paramref name="to"/>
    /// (yyyy-MM, both included; default the last three months), as JSON or CSV (?format=csv).
    /// </summary>
    private static async Task<IResult> ChargebackAsync(Credit credit, TimeProvider clock, string? from = null, string? to = null, string? format = null, CancellationToken ct = default)
    {
        var now = CreditBook.MonthOf(clock.GetUtcNow());
        if (!TryMonth(to, now, out var last) || !TryMonth(from, last.AddMonths(-2), out var first) || first > last || first < last.AddMonths(-23))
        {
            return AuthEndpoints.Problem(400, "months", "Months are yyyy-MM, from before to, at most 24 of them.");
        }
        List<ChargeRow> rows;
        try
        {
            rows = await credit.ChargebackAsync(first, last, ct);
        }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or InvalidOperationException)
        {
            return AuthEndpoints.Problem(503, "usage", "Spend cannot be read right now: " + ex.Message);
        }
        if (format == "csv")
        {
            var csv = new StringBuilder("month,kind,name,cost_centre,members,spend,credit\n");
            foreach (var r in rows)
            {
                csv.AppendJoin(',', new[]
                {
                    r.Month, r.Kind, Operations.OperationsEndpoints.Csv(r.Name), Operations.OperationsEndpoints.Csv(r.CostCentre), r.Members.ToString(CultureInfo.InvariantCulture),
                    r.Spend.ToString("0.0000", CultureInfo.InvariantCulture), r.Credit?.ToString(CultureInfo.InvariantCulture) ?? "",
                }).Append('\n');
            }
            return Results.File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv; charset=utf-8", $"chargeback-{first:yyyy-MM}-to-{last:yyyy-MM}.csv");
        }
        return Results.Ok(new { from = first.ToString("yyyy-MM", CultureInfo.InvariantCulture), to = last.ToString("yyyy-MM", CultureInfo.InvariantCulture), rows });
    }

    private static bool TryMonth(string? text, DateTimeOffset fallback, out DateTimeOffset month)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            month = fallback;
            return true;
        }
        var ok = DateTime.TryParseExact(text, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed);
        month = ok ? new DateTimeOffset(parsed.Year, parsed.Month, 1, 0, 0, 0, TimeSpan.Zero) : default;
        return ok;
    }

    private static async Task<IResult?> CheckAsync(AppDbContext db, Guid? id, string name, string? directory)
    {
        if (name.Length is 0 or > 100)
        {
            return AuthEndpoints.Problem(400, "name", "A group needs a name of up to 100 characters.");
        }
        if (directory is { Length: > 1000 })
        {
            return AuthEndpoints.Problem(400, "directory", "The directory group's name is too long.");
        }
        if (await db.Groups.AnyAsync(x => x.Id != id && EF.Functions.ILike(x.Name, name.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal))))
        {
            return AuthEndpoints.Problem(409, "exists", $"There is already a group named {name}.");
        }
        return null;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
