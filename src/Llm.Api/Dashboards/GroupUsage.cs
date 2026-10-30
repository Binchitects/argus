using System.Globalization;
using Llm.Api.Access;
using Llm.Api.Endpoints;
using Llm.Api.Gateway;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Dashboards;

/// <summary>
/// Admin → Usage by group (usage.read): over a time, each group's requests, tokens (input from the cache and not, output) and
/// spend, its members and how many of them used it, and each member's share; and the people in no group. By the dashboards'
/// rule of whose a request is (<see cref="UsageEndpoints.Person"/>), refusals for a key's rate limit left out. A person in two
/// groups counts in both, so the groups can add up to more than the whole.
/// </summary>
public static class GroupUsage
{
    public static void MapGroupUsage(this IEndpointRouteBuilder app) =>
        app.MapGet("/api/admin/usage/groups", UsageAsync).RequirePermission(Llm.Core.Access.Permissions.UsageRead);

    private sealed record Used(long Requests, long InputMiss, long InputHit, long Output, decimal Cost)
    {
        public static readonly Used None = new(0, 0, 0, 0, 0);

        public Used Add(Used o) => new(Requests + o.Requests, InputMiss + o.InputMiss, InputHit + o.InputHit, Output + o.Output, Cost + o.Cost);
    }

    private static async Task<IResult> UsageAsync(DateTimeOffset from, DateTimeOffset to, LedgerSql sql, AppDbContext db, Credit credit, CancellationToken ct)
    {
        if (to <= from || to - from > DashboardEndpoints.MaxRange)
        {
            return AuthEndpoints.Problem(400, "range", "The time range must be positive and at most 400 days.");
        }
        Dictionary<string, Used> byPerson;
        try
        {
            // Each person's from the app's own ledger (a rate limit's refusal left out, as the dashboards leave it).
            var rows = await sql.QueryAsync("""
                select r.person, count(*) as requests,
                       coalesce(sum(greatest(r.prompt_tokens - r.cached_tokens, 0)),0) as input_miss,
                       coalesce(sum(r.cached_tokens),0) as input_hit,
                       coalesce(sum(r.completion_tokens),0) as output,
                       coalesce(sum(r.cost),0) as cost
                from gateway.requests r
                where r.started_at >= @from and r.started_at < @to and r.person is not null and not (r.status = 'refused' and r.refusal in ('rpm', 'tpm', 'parallel'))
                group by 1
                """, new Dictionary<string, object> { ["from"] = from.ToUniversalTime(), ["to"] = to.ToUniversalTime() }, ct);
            byPerson = rows.Rows.ToDictionary(r => (string)r[0]!, r => new Used(Num(r[1]), Num(r[2]), Num(r[3]), Num(r[4]), Convert.ToDecimal(r[5] ?? 0m, CultureInfo.InvariantCulture)),
                StringComparer.OrdinalIgnoreCase);
        }
        catch (Npgsql.NpgsqlException ex)
        {
            return AuthEndpoints.Problem(503, "usage", "Usage cannot be read right now: " + ex.Message);
        }
        var people = (await db.Users.AsNoTracking().Where(u => u.Email != null).Select(u => new { u.Id, u.UserName, u.DisplayName, u.Email }).ToListAsync(ct))
            .ToDictionary(u => u.Email!.ToLowerInvariant(), StringComparer.OrdinalIgnoreCase);
        object Member(string email) => people.TryGetValue(email, out var p)
            ? new { id = (Guid?)p.Id, p.UserName, displayName = p.DisplayName == "" ? null : p.DisplayName, email, used = Shape(byPerson.GetValueOrDefault(email) ?? Used.None) }
            : new { id = (Guid?)null, UserName = (string?)null, displayName = (string?)null, email, used = Shape(byPerson.GetValueOrDefault(email) ?? Used.None) };
        object Row(Guid? id, string name, ICollection<string> members)
        {
            var used = members.Select(m => byPerson.GetValueOrDefault(m) ?? Used.None).Aggregate(Used.None, (a, b) => a.Add(b));
            var active = members.Where(m => byPerson.ContainsKey(m)).OrderByDescending(m => byPerson[m].Cost).ThenByDescending(m => byPerson[m].Requests).ToList();
            return new { id, name, members = members.Count, active = active.Count, used = Shape(used), people = active.Select(Member) };
        }
        var groups = await db.Groups.AsNoTracking().OrderBy(g => g.Name).ToListAsync(ct);
        var list = new List<object>();
        var grouped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in groups)
        {
            var members = (await credit.MembersAsync(g, ct)).Select(e => e.ToLowerInvariant()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            grouped.UnionWith(members);
            list.Add(Row(g.Id, g.Name, members));
        }
        var loose = people.Keys.Concat(byPerson.Keys.Where(k => k.Contains('@'))).Where(e => !grouped.Contains(e)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var total = byPerson.Values.Aggregate(Used.None, (a, b) => a.Add(b));
        return Results.Ok(new { groups = list, none = Row(null, "(in no group)", loose), total = Shape(total) });
    }

    private static long Num(object? v) => v is null ? 0 : Convert.ToInt64(v, CultureInfo.InvariantCulture);

    private static object Shape(Used u) => new { u.Requests, u.InputMiss, u.InputHit, u.Output, tokens = u.InputMiss + u.InputHit + u.Output, u.Cost };
}
