using System.Globalization;
using Llm.Api.Access;
using Llm.Api.Dashboards;
using Llm.Core.Access;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Gateway;

/// <summary>Configuration section "Credit".</summary>
public sealed class CreditOptions
{
    /// <summary>How long the month's spend and the budgets, once read, are reused by the checks (each read scans the month's requests).</summary>
    public TimeSpan Refresh { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>This calendar month's spend per person (every path, by email), and each person's own budget at the gateway.</summary>
public sealed record MonthBook(DateTimeOffset Month, IReadOnlyDictionary<string, decimal> Spend, IReadOnlyDictionary<string, decimal?> Budgets, DateTimeOffset ReadAt);

/// <summary>A group's spend in a month, or a cost centre's (each person once).</summary>
public sealed record ChargeRow(string Month, string Kind, string Name, string? CostCentre, int Members, decimal Spend, decimal? Credit);

/// <summary>The month's spend and the budgets, read at most every <see cref="CreditOptions.Refresh"/> (one reader at a time).</summary>
public sealed partial class CreditBook(SqlDatasource sql, IOptionsMonitor<CreditOptions> options, TimeProvider clock, ILogger<CreditBook> logger) : IDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private MonthBook? _book;

    public static DateTimeOffset MonthOf(DateTimeOffset at) => new(at.Year, at.Month, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Null when the request log or the gateway cannot be read: the checks then let the request go (and it is logged).</summary>
    public async Task<MonthBook?> ReadAsync(ILiteLlm gateway, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (_book is { } fresh && fresh.Month == MonthOf(now) && now - fresh.ReadAt < options.CurrentValue.Refresh)
        {
            return fresh;
        }
        await _lock.WaitAsync(ct);
        try
        {
            if (_book is { } again && again.Month == MonthOf(now) && now - again.ReadAt < options.CurrentValue.Refresh)
            {
                return again;
            }
            var month = MonthOf(now);
            var spend = await SpendAsync(month, month.AddMonths(1), ct);
            var budgets = (await gateway.UsersAsync(ct)).ToDictionary(u => u.Key.ToLowerInvariant(), u => u.Value.Budget, StringComparer.OrdinalIgnoreCase);
            return _book = new MonthBook(month, spend, budgets, now);
        }
        catch (Exception ex) when (ex is GatewayException or Npgsql.NpgsqlException or InvalidOperationException)
        {
            LogUnread(logger, ex.Message);
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Spend per person (lower-case email) between two times, over every path, by the dashboards' rule.</summary>
    public async Task<Dictionary<string, decimal>> SpendAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var rows = await sql.QueryAsync($"""
            select lower({UsageEndpoints.Person}) as person, coalesce(sum(s.spend),0) as spend
            {UsageEndpoints.From} where s."startTime" >= @from and s."startTime" < @to
            group by 1
            """, new Dictionary<string, object> { ["from"] = from.UtcDateTime, ["to"] = to.UtcDateTime }, ct);
        return rows.Rows.ToDictionary(r => (string)r[0]!, r => Convert.ToDecimal(r[1] ?? 0m, CultureInfo.InvariantCulture), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Spend per month (yyyy-MM) and person, for the chargeback report.</summary>
    public async Task<List<(string Month, string Person, decimal Spend)>> MonthlyAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var rows = await sql.QueryAsync($"""
            select to_char(date_trunc('month', s."startTime"), 'YYYY-MM') as month, lower({UsageEndpoints.Person}) as person, coalesce(sum(s.spend),0) as spend
            {UsageEndpoints.From} where s."startTime" >= @from and s."startTime" < @to
            group by 1, 2
            """, new Dictionary<string, object> { ["from"] = from.UtcDateTime, ["to"] = to.UtcDateTime }, ct);
        return [.. rows.Rows.Select(r => ((string)r[0]!, (string)r[1]!, Convert.ToDecimal(r[2] ?? 0m, CultureInfo.InvariantCulture)))];
    }

    public void Dispose() => _lock.Dispose();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Credit: this month's spend could not be read, so nothing is held back by it: {Reason}")]
    private static partial void LogUnread(ILogger logger, string reason);
}

/// <summary>
/// One credit across the chat and API keys, and the credit of groups. The gateway's own counters
/// keep a person's two paths apart (the chat books to them as an end user, their keys as an
/// internal user), so each would let the full budget through. The app checks the sum instead:
/// what the request log puts to a person this calendar month (UTC), over every path, against
/// their own budget and each of their groups' credit (shared by the members, or each member's).
/// The chat asks before every answer (ModelPolicy); the gateway asks for each API request
/// (the guardrail, GuardrailEndpoints).
/// </summary>
public sealed class Credit(AppDbContext db, AccessService access, ILiteLlm gateway, CreditBook book)
{
    /// <summary>Why this person may not spend more now, or null.</summary>
    public async Task<string?> RefusalAsync(AppUser user, CancellationToken ct = default)
    {
        if (user.Email is not { } email || await book.ReadAsync(gateway, ct) is not { } month)
        {
            return null;
        }
        var spent = month.Spend.GetValueOrDefault(email);
        if (month.Budgets.GetValueOrDefault(email) is { } budget && spent >= budget)
        {
            return $"You have used all your credit for this month ({Money(budget)}, the chat and your API keys together). Ask an admin to raise it.";
        }
        var member = await access.MembershipAsync(user, ct);
        var groups = await db.Groups.AsNoTracking().Where(g => member.Groups.Contains(g.Id) && g.Credit != null).OrderBy(g => g.Credit).ToListAsync(ct);
        foreach (var g in groups)
        {
            if (g.CreditPerMember)
            {
                if (spent >= g.Credit)
                {
                    return $"You have used your credit as a member of {g.Name} for this month ({Money(g.Credit!.Value)}). Ask an admin to raise it.";
                }
            }
            else if ((await MembersAsync(g, ct)).Sum(e => month.Spend.GetValueOrDefault(e)) >= g.Credit)
            {
                return $"{g.Name} has used its credit for this month ({Money(g.Credit!.Value)}, shared by its members). Ask an admin to raise it.";
            }
        }
        return null;
    }

    /// <summary>A group's members' emails (lower case): those added to an app group, or whoever the directory puts in a directory group.</summary>
    public async Task<List<string>> MembersAsync(Group group, CancellationToken ct = default)
    {
        if (group.Directory is { } d)
        {
            var candidates = await db.Users.AsNoTracking().Where(u => u.DirectoryGroups.Count > 0 && u.Email != null).Select(u => new { u.Email, u.DirectoryGroups }).ToListAsync(ct);
            return [.. candidates.Where(u => AccessService.InDirectoryGroup(u.DirectoryGroups, d)).Select(u => u.Email!.ToLowerInvariant())];
        }
        return [.. (await db.GroupMembers.AsNoTracking().Where(m => m.GroupId == group.Id).Join(db.Users.AsNoTracking(), m => m.UserId, u => u.Id, (_, u) => u.Email)
            .Where(e => e != null).ToListAsync(ct)).Select(e => e!.ToLowerInvariant())];
    }

    /// <summary>
    /// The chargeback report, month by month: each group's members' spend (people in several
    /// groups count in each), and each cost centre's (each person once), with the people in no
    /// group, and requests attributed to nobody.
    /// </summary>
    public async Task<List<ChargeRow>> ChargebackAsync(DateTimeOffset fromMonth, DateTimeOffset toMonth, CancellationToken ct = default)
    {
        var spend = await book.MonthlyAsync(fromMonth, toMonth.AddMonths(1), ct);
        var groups = await db.Groups.AsNoTracking().OrderBy(g => g.Name).ToListAsync(ct);
        var members = new Dictionary<Guid, HashSet<string>>();
        foreach (var g in groups)
        {
            members[g.Id] = (await MembersAsync(g, ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        var grouped = members.Values.SelectMany(m => m).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rows = new List<ChargeRow>();
        for (var m = fromMonth; m <= toMonth; m = m.AddMonths(1))
        {
            var key = m.ToString("yyyy-MM", CultureInfo.InvariantCulture);
            var byPerson = spend.Where(s => s.Month == key).ToDictionary(s => s.Person, s => s.Spend, StringComparer.OrdinalIgnoreCase);
            decimal Sum(IEnumerable<string> people) => people.Sum(p => byPerson.GetValueOrDefault(p));
            rows.AddRange(groups.Select(g => new ChargeRow(key, "group", g.Name, g.CostCentre, members[g.Id].Count, Sum(members[g.Id]), g.Credit)));
            foreach (var centre in groups.Where(g => g.CostCentre is not null).GroupBy(g => g.CostCentre!, StringComparer.OrdinalIgnoreCase).OrderBy(c => c.Key, StringComparer.OrdinalIgnoreCase))
            {
                var people = centre.SelectMany(g => members[g.Id]).ToHashSet(StringComparer.OrdinalIgnoreCase);
                rows.Add(new ChargeRow(key, "cost centre", centre.Key, centre.Key, people.Count, Sum(people), null));
            }
            var loose = byPerson.Keys.Where(p => p.Contains('@') && !grouped.Contains(p)).ToList();
            rows.Add(new ChargeRow(key, "none", "(in no group)", null, loose.Count, Sum(loose), null));
            if (byPerson.Keys.Where(p => !p.Contains('@')).ToList() is { Count: > 0 } nobody)
            {
                rows.Add(new ChargeRow(key, "none", "(attributed to nobody)", null, 0, Sum(nobody), null));
            }
        }
        return rows;
    }

    public static string Money(decimal value) => value.ToString("$0.00", CultureInfo.InvariantCulture);
}
