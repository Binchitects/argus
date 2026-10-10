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

/// <summary>This calendar month's spend per person (by email) and credit kind.</summary>
public sealed record MonthBook(DateTimeOffset Month, IReadOnlyDictionary<string, IReadOnlyDictionary<CreditKind, decimal>> Spend, DateTimeOffset ReadAt)
{
    /// <summary>What a person spent this month on one kind.</summary>
    public decimal Of(string email, CreditKind kind) =>
        Spend.TryGetValue(email, out var kinds) && kinds.TryGetValue(kind, out var spent) ? spent : 0m;

    /// <summary>What a person spent this month, every kind together.</summary>
    public decimal Total(string email) => Spend.TryGetValue(email, out var kinds) ? kinds.Values.Sum() : 0m;
}

/// <summary>A group's spend in a month, or a cost centre's (each person once).</summary>
public sealed record ChargeRow(string Month, string Kind, string Name, string? CostCentre, int Members, decimal Spend, decimal? Credit);

/// <summary>The month's spend, read at most every <see cref="CreditOptions.Refresh"/> (one reader at a time).</summary>
public sealed partial class CreditBook(SqlDatasource sql, IOptionsMonitor<CreditOptions> options, TimeProvider clock, ILogger<CreditBook> logger) : IDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private MonthBook? _book;

    public static DateTimeOffset MonthOf(DateTimeOffset at) => new(at.Year, at.Month, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Null when the request log cannot be read: the checks then let the request go (and it is logged). <paramref name="fresh"/>:
    /// read now, not what was read within <see cref="CreditOptions.Refresh"/> (the pages; the checks reuse it).
    /// </summary>
    public async Task<MonthBook?> ReadAsync(CancellationToken ct, bool fresh = false)
    {
        var now = clock.GetUtcNow();
        if (!fresh && _book is { } known && known.Month == MonthOf(now) && now - known.ReadAt < options.CurrentValue.Refresh)
        {
            return known;
        }
        await _lock.WaitAsync(ct);
        try
        {
            if (!fresh && _book is { } again && again.Month == MonthOf(now) && now - again.ReadAt < options.CurrentValue.Refresh)
            {
                return again;
            }
            var month = MonthOf(now);
            return _book = new MonthBook(month, await SpendAsync(month, month.AddMonths(1), ct), now);
        }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or InvalidOperationException)
        {
            LogUnread(logger, ex.Message);
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>The next read asks the request log again (a credit was just changed, a test moved on).</summary>
    public void Forget() => _book = null;

    /// <summary>Spend per person (lower-case email) and kind between two times, over every path, by the dashboards' rule.</summary>
    public async Task<Dictionary<string, IReadOnlyDictionary<CreditKind, decimal>>> SpendAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var rows = await sql.QueryAsync($"""
            select lower({UsageEndpoints.Person}) as person, {UsageEndpoints.CreditKind} as kind, coalesce(sum(s.spend),0) as spend
            {UsageEndpoints.From} where s."startTime" >= @from and s."startTime" < @to
            group by 1, 2
            """, new Dictionary<string, object> { ["from"] = from.UtcDateTime, ["to"] = to.UtcDateTime }, ct);
        var spend = new Dictionary<string, Dictionary<CreditKind, decimal>>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows.Rows)
        {
            if (Credits.Parse((string?)r[1]) is not { } kind)
            {
                continue;
            }
            var person = spend.TryGetValue((string)r[0]!, out var p) ? p : spend[(string)r[0]!] = [];
            person[kind] = person.GetValueOrDefault(kind) + Convert.ToDecimal(r[2] ?? 0m, CultureInfo.InvariantCulture);
        }
        return spend.ToDictionary(p => p.Key, p => (IReadOnlyDictionary<CreditKind, decimal>)p.Value, StringComparer.OrdinalIgnoreCase);
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

/// <summary>Where a person stands on one kind of credit this month: spent, their own credit, and the tightest of their groups'.</summary>
/// <param name="Group">The group whose credit binds first (its name), and how much of it is left; null: no group credit of this kind.</param>
public sealed record CreditStanding(CreditKind Kind, decimal Spent, decimal? Credit, string? Group, decimal? GroupLeft);

/// <summary>
/// A credit per kind (the chat's answers, API keys' text requests, pictures, videos, speech), each a calendar month's spend
/// (UTC) as the gateway's request log puts it to the person, held to their own credit of that kind and to each of their
/// groups' (shared by the members, or each member's). The chat asks before every answer and before each picture, video or
/// speech it makes (ModelPolicy, the tools); the gateway asks for each API request, by its model's kind (the guardrail,
/// GuardrailEndpoints). The credits are the app's: the gateway holds no budget of its own.
/// </summary>
public sealed class Credit(AppDbContext db, AccessService access, CreditBook book)
{
    /// <summary>Why this person may not spend more of this kind now, or null.</summary>
    public async Task<string?> RefusalAsync(AppUser user, CreditKind kind, CancellationToken ct = default)
    {
        if (user.Email is not { } email || await book.ReadAsync(ct) is not { } month)
        {
            return null;
        }
        var spent = month.Of(email, kind);
        var what = Credits.Label(kind);
        if (Credits.Of(user, kind) is { } own && spent >= own)
        {
            return $"You have used all your {what} credit for this month ({Money(own)}). Ask an admin to raise it.";
        }
        foreach (var (g, credit) in await GroupsAsync(user, kind, ct))
        {
            if (g.CreditPerMember)
            {
                if (spent >= credit)
                {
                    return $"You have used your {what} credit as a member of {g.Name} for this month ({Money(credit)}). Ask an admin to raise it.";
                }
            }
            else if ((await MembersAsync(g, ct)).Sum(e => month.Of(e, kind)) >= credit)
            {
                return $"{g.Name} has used its {what} credit for this month ({Money(credit)}, shared by its members). Ask an admin to raise it.";
            }
        }
        return null;
    }

    /// <summary>Where the person stands on each kind this month; null when the request log cannot be read.</summary>
    public async Task<IReadOnlyList<CreditStanding>?> StandingAsync(AppUser user, CancellationToken ct = default)
    {
        if (user.Email is not { } email || await book.ReadAsync(ct) is not { } month)
        {
            return null;
        }
        var list = new List<CreditStanding>();
        foreach (var kind in Credits.Kinds)
        {
            var spent = month.Of(email, kind);
            (string Name, decimal Left)? tightest = null;
            foreach (var (g, credit) in await GroupsAsync(user, kind, ct))
            {
                var used = g.CreditPerMember ? spent : (await MembersAsync(g, ct)).Sum(e => month.Of(e, kind));
                var left = Math.Max(0, credit - used);
                if (tightest is null || left < tightest.Value.Left)
                {
                    tightest = (g.Name, left);
                }
            }
            list.Add(new CreditStanding(kind, spent, Credits.Of(user, kind), tightest?.Name, tightest?.Left));
        }
        return list;
    }

    /// <summary>The person's groups with a credit of this kind, the smallest first.</summary>
    private async Task<List<(Group Group, decimal Credit)>> GroupsAsync(AppUser user, CreditKind kind, CancellationToken ct)
    {
        var member = await access.MembershipAsync(user, ct);
        var groups = await db.Groups.AsNoTracking().Where(g => member.Groups.Contains(g.Id)).ToListAsync(ct);
        return [.. groups.Select(g => (g, Credits.Of(g, kind))).Where(x => x.Item2 is not null).Select(x => (x.g, x.Item2!.Value)).OrderBy(x => x.Item2)];
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
            rows.AddRange(groups.Select(g => new ChargeRow(key, "group", g.Name, g.CostCentre, members[g.Id].Count, Sum(members[g.Id]), Total(g))));
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

    /// <summary>A group's credits of every kind together, for the chargeback report; null: none.</summary>
    private static decimal? Total(Group g) => Credits.Any(g) ? Credits.Kinds.Sum(k => Credits.Of(g, k) ?? 0m) : null;

    public static string Money(decimal value) => value.ToString("$0.00", CultureInfo.InvariantCulture);
}
