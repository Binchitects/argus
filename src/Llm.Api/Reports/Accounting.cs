using System.Globalization;
using System.Text;
using Llm.Api.Access;
using Llm.Api.Dashboards;
using Llm.Api.Endpoints;
using Llm.Api.Gateway;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Reports;

/// <summary>
/// Configuration section "Costs" (Settings → Costs): what running the machine costs, for the accountant's view
/// (Admin → Accounting). Power is the GPUs' measured draw plus the rest of the machine's, times the cooling overhead; the
/// hardware is its price spread over its life; service is a monthly sum (support, licences, hosting, staff).
/// </summary>
public sealed class CostOptions
{
    /// <summary>The price of a kilowatt-hour, in the app's currency.</summary>
    public decimal PowerPricePerKwh { get; set; } = 0.15m;

    /// <summary>Power usage effectiveness: what the room draws for each watt of the machine (cooling, power supplies).</summary>
    public decimal Pue { get; set; } = 1.4m;

    /// <summary>What the machine draws besides its GPUs (CPUs, memory, disks, fans), in watts, on average.</summary>
    public decimal OtherPowerWatts { get; set; } = 300m;

    /// <summary>What the machine cost to buy; 0: left out.</summary>
    public decimal HardwarePrice { get; set; }

    /// <summary>The months the hardware is written off over.</summary>
    public int HardwareMonths { get; set; } = 36;

    /// <summary>Support, licences, hosting or rack space, the people who run it: a month.</summary>
    public decimal ServicePerMonth { get; set; }

    /// <summary>How the running cost is shared out: "spend" (the prices of Settings → Prices), "tokens" or "time" (the model's).</summary>
    public string ShareBy { get; set; } = "spend";
}

/// <summary>One person's, group's or project's share of the window.</summary>
public sealed record AccountRow(string Kind, Guid? Id, string Name, string? Email, string? CostCentre, int People, long Requests, long Tokens, double ModelHours, decimal Charged, double Share, decimal Cost);

/// <summary>The running cost of a window, from the settings and the GPUs' measured power.</summary>
public sealed record RunningCost(double Hours, double? GpuKwh, double OtherKwh, double Kwh, decimal Power, decimal Hardware, decimal Service, decimal Total);

/// <summary>
/// The accountant's view (billing.read): over a time, what running the machine cost (<see cref="RunningCost"/>), what was
/// used (requests, tokens, the model's time, the ledger's spend at the prices set), what a unit costs (a million tokens, a
/// request, a person), and the cost shared out to each person, group (a person in two groups counts in both), cost centre
/// and project in proportion to their use. CSV of every row for the finance team.
/// </summary>
public static class Accounting
{
    public static readonly string[] Bases = ["spend", "tokens", "time"];

    public static void MapAccounting(this IEndpointRouteBuilder app) =>
        app.MapGet("/api/admin/accounting", ReportAsync).RequirePermission(Llm.Core.Access.Permissions.BillingRead);

    private sealed record Used(long Requests, long Tokens, double Seconds, decimal Charged, Guid? Project);

    /// <summary>The running cost of a window: the GPUs' kWh measured (null: Prometheus could not say), the rest from the settings.</summary>
    public static RunningCost Cost(CostOptions o, DateTimeOffset from, DateTimeOffset to, double? gpuKwh)
    {
        var hours = Math.Max(0, (to - from).TotalHours);
        var months = (decimal)(hours / (365.25 * 24 / 12));
        var other = (double)o.OtherPowerWatts * hours / 1000;
        var kwh = ((gpuKwh ?? 0) + other) * (double)Math.Max(1, o.Pue);
        var power = Math.Round((decimal)kwh * o.PowerPricePerKwh, 2);
        var hardware = o.HardwareMonths > 0 ? Math.Round(o.HardwarePrice / o.HardwareMonths * months, 2) : 0;
        var service = Math.Round(o.ServicePerMonth * months, 2);
        return new RunningCost(Math.Round(hours, 2), gpuKwh is null ? null : Math.Round(gpuKwh.Value, 3), Math.Round(other, 3), Math.Round(kwh, 3), power, hardware, service, power + hardware + service);
    }

    /// <summary>Each one's share of what was used, by the basis; all of them add up to 1 when anything was used.</summary>
    public static double ShareOf(string basis, long tokens, double seconds, decimal charged, long allTokens, double allSeconds, decimal allCharged) => basis switch
    {
        "tokens" => allTokens > 0 ? (double)tokens / allTokens : 0,
        "time" => allSeconds > 0 ? seconds / allSeconds : 0,
        _ => allCharged > 0 ? (double)(charged / allCharged) : 0,
    };

    private static async Task<IResult> ReportAsync(DateTimeOffset from, DateTimeOffset to, string? basis, string? format, LedgerSql sql, AppDbContext db, Credit credit,
        PromDatasource prom, IOptionsMonitor<CostOptions> costs, TimeProvider clock, ILoggerFactory logs, CancellationToken ct)
    {
        if (to <= from || to - from > DashboardEndpoints.MaxRange)
        {
            return AuthEndpoints.Problem(400, "range", "The time range must be positive and at most 400 days.");
        }
        var o = costs.CurrentValue;
        var by = basis is { Length: > 0 } ? basis : Bases.Contains(o.ShareBy) ? o.ShareBy : "spend";
        if (!Bases.Contains(by))
        {
            return AuthEndpoints.Problem(400, "basis", "Share by spend, tokens or time.");
        }
        var now = clock.GetUtcNow();
        var end = to > now ? now : to;
        List<(string? Person, Used Used)> rows;
        try
        {
            var table = await sql.QueryAsync("""
                select r.person, r.project_id, count(*), coalesce(sum(r.prompt_tokens),0) + coalesce(sum(r.completion_tokens),0), coalesce(sum(r.duration_ms),0) / 1000.0, coalesce(sum(r.cost),0)
                from gateway.requests r
                where r.started_at >= @from and r.started_at < @to and r.status <> 'refused'
                group by 1, 2
                """, new Dictionary<string, object> { ["from"] = from.ToUniversalTime(), ["to"] = to.ToUniversalTime() }, ct);
            rows = [.. table.Rows.Select(r => ((string?)r[0], new Used(Num(r[2]), Num(r[3]), Convert.ToDouble(r[4] ?? 0d, CultureInfo.InvariantCulture),
                Convert.ToDecimal(r[5] ?? 0m, CultureInfo.InvariantCulture), r[1] is Guid g ? g : null)))];
        }
        catch (Npgsql.NpgsqlException ex)
        {
            return AuthEndpoints.Problem(503, "usage", "Usage cannot be read right now: " + ex.Message);
        }
        // The GPUs' energy in the window, measured: their average draw over it, times its length.
        double? gpuKwh = null;
        var span = (long)Math.Max(60, (end - from).TotalSeconds);
        if (end > from)
        {
            try
            {
                var got = await prom.InstantAsync($"sum(avg_over_time(nvidia_smi_power_draw_watts[{span}s]))", end, ct);
                if (got.Count > 0 && got[0].Points.Count > 0 && got[0].Points[0][1] is double watts)
                {
                    gpuKwh = watts * span / 3600 / 1000;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or DatasourceException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                PowerUnread(logs.CreateLogger("Llm.Api.Accounting"), ex.Message);
            }
        }
        var running = Cost(o, from, end > from ? end : from, gpuKwh);
        long allTokens = rows.Sum(r => r.Used.Tokens), allRequests = rows.Sum(r => r.Used.Requests);
        var allSeconds = rows.Sum(r => r.Used.Seconds);
        var allCharged = rows.Sum(r => r.Used.Charged);
        AccountRow Row(string kind, Guid? id, string name, string? email, string? centre, int people, IEnumerable<Used> used)
        {
            var u = used.ToList();
            long tokens = u.Sum(x => x.Tokens), requests = u.Sum(x => x.Requests);
            var seconds = u.Sum(x => x.Seconds);
            var charged = u.Sum(x => x.Charged);
            var share = ShareOf(by, tokens, seconds, charged, allTokens, allSeconds, allCharged);
            return new AccountRow(kind, id, name, email, centre, people, requests, tokens, Math.Round(seconds / 3600, 2), Math.Round(charged, 4), Math.Round(share, 6),
                Math.Round(running.Total * (decimal)share, 2));
        }
        var byPerson = rows.GroupBy(r => r.Person?.ToLowerInvariant() ?? "", StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Select(x => x.Used).ToList(), StringComparer.Ordinal);
        var users = (await db.Users.AsNoTracking().Where(u => u.Email != null).Select(u => new { u.Id, u.DisplayName, u.UserName, u.Email }).ToListAsync(ct))
            .GroupBy(u => u.Email!.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var people = byPerson.Where(p => p.Key.Length > 0)
            .Select(p => users.TryGetValue(p.Key, out var u)
                ? Row("person", u.Id, u.DisplayName is { Length: > 0 } d ? d : u.UserName ?? p.Key, p.Key, null, 1, p.Value)
                : Row("person", null, p.Key, p.Key.Contains('@') ? p.Key : null, null, 1, p.Value))
            .ToList();
        if (byPerson.TryGetValue("", out var nobody))
        {
            people.Add(Row("person", null, "(attributed to nobody)", null, null, 0, nobody));
        }
        people = [.. people.OrderByDescending(r => r.Cost).ThenByDescending(r => r.Requests)];
        var groups = new List<AccountRow>();
        var grouped = new HashSet<string>(StringComparer.Ordinal);
        var centres = new Dictionary<string, (string Name, HashSet<string> People)>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in await db.Groups.AsNoTracking().OrderBy(g => g.Name).ToListAsync(ct))
        {
            var members = (await credit.MembersAsync(g, ct)).Select(e => e.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
            grouped.UnionWith(members);
            groups.Add(Row("group", g.Id, g.Name, null, g.CostCentre, members.Count, members.SelectMany(m => byPerson.GetValueOrDefault(m) ?? [])));
            if (g.CostCentre is { Length: > 0 } c)
            {
                if (!centres.TryGetValue(c, out var at))
                {
                    centres[c] = at = (c, new HashSet<string>(StringComparer.Ordinal));
                }
                at.People.UnionWith(members);
            }
        }
        var loose = byPerson.Keys.Where(k => k.Length > 0 && !grouped.Contains(k)).ToList();
        groups.Add(Row("group", null, "(in no group)", null, null, loose.Count, loose.SelectMany(m => byPerson[m])));
        var centreRows = centres.Values.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(c => Row("cost centre", null, c.Name, null, c.Name, c.People.Count, c.People.SelectMany(m => byPerson.GetValueOrDefault(m) ?? []))).ToList();
        var ids = rows.Where(r => r.Used.Project is not null).Select(r => r.Used.Project!.Value).Distinct().ToList();
        var names = await db.Projects.AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        var projects = rows.GroupBy(r => r.Used.Project)
            .Select(g => g.Key is { } id
                ? Row("project", id, names.GetValueOrDefault(id) ?? "(a deleted project)", null, null, g.Select(x => x.Person).Distinct().Count(p => p is not null), g.Select(x => x.Used))
                : Row("project", null, "(in no project)", null, null, g.Select(x => x.Person).Distinct().Count(p => p is not null), g.Select(x => x.Used)))
            .OrderBy(r => r.Id is null).ThenByDescending(r => r.Cost).ToList();
        var active = byPerson.Keys.Count(k => k.Length > 0);
        if (format == "csv")
        {
            var csv = new StringBuilder("kind,name,email,cost_centre,people,requests,tokens,model_hours,charged,share,cost\n");
            foreach (var r in people.Concat(groups).Concat(centreRows).Concat(projects))
            {
                csv.AppendJoin(',', new[]
                {
                    r.Kind, Operations.OperationsEndpoints.Csv(r.Name), Operations.OperationsEndpoints.Csv(r.Email), Operations.OperationsEndpoints.Csv(r.CostCentre),
                    r.People.ToString(CultureInfo.InvariantCulture), r.Requests.ToString(CultureInfo.InvariantCulture), r.Tokens.ToString(CultureInfo.InvariantCulture),
                    r.ModelHours.ToString("0.00", CultureInfo.InvariantCulture), r.Charged.ToString("0.0000", CultureInfo.InvariantCulture),
                    r.Share.ToString("0.000000", CultureInfo.InvariantCulture), r.Cost.ToString("0.00", CultureInfo.InvariantCulture),
                }).Append('\n');
            }
            csv.Append(CultureInfo.InvariantCulture, $"running,power,,,,,,,,,{running.Power:0.00}\nrunning,hardware,,,,,,,,,{running.Hardware:0.00}\nrunning,service,,,,,,,,,{running.Service:0.00}\nrunning,total,,,,,,,,,{running.Total:0.00}\n");
            return Results.File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv; charset=utf-8", $"accounting-{from:yyyy-MM-dd}-to-{to:yyyy-MM-dd}.csv");
        }
        return Results.Ok(new
        {
            from, to, basis = by, settings = o, running,
            used = new { requests = allRequests, tokens = allTokens, modelHours = Math.Round(allSeconds / 3600, 2), charged = Math.Round(allCharged, 4), people = active },
            unit = new
            {
                perMillionTokens = allTokens > 0 ? Math.Round(running.Total / allTokens * 1_000_000, 4) : (decimal?)null,
                perRequest = allRequests > 0 ? Math.Round(running.Total / allRequests, 4) : (decimal?)null,
                perPerson = active > 0 ? Math.Round(running.Total / active, 2) : (decimal?)null,
                perKwh = running.Kwh > 0 ? Math.Round(running.Power / (decimal)running.Kwh, 4) : (decimal?)null,
            },
            people, groups, costCentres = centreRows, projects,
        });
    }

    private static readonly Action<ILogger, string, Exception?> PowerUnreadMessage =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(1, "PowerUnread"), "The GPUs' power could not be read: {Error}");

    private static void PowerUnread(ILogger log, string error) => PowerUnreadMessage(log, error, null);

    private static long Num(object? v) => v is null ? 0 : Convert.ToInt64(v, CultureInfo.InvariantCulture);
}
