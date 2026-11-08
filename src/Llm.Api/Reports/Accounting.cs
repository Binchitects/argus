using System.Globalization;
using System.Security.Claims;
using Llm.Api.Access;
using Llm.Api.Dashboards;
using Llm.Api.Endpoints;
using Llm.Api.Gateway;
using Llm.Api.Identity;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
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

    /// <summary>What an hour of a person's work costs (salary and overheads), for a project's work against doing it by hand.</summary>
    public decimal PersonHourly { get; set; } = 50m;

    /// <summary>How the running cost is shared out: "spend" (the prices of Settings → Prices), "tokens" or "time" (the model's).</summary>
    public string ShareBy { get; set; } = "spend";
}

/// <summary>One person's, group's, cost centre's, project's, model's, source's or kind's share of the window; a group's members under it.</summary>
public sealed record AccountRow(string Kind, Guid? Id, string Name, string? Email, string? CostCentre, int People, long Requests, long Tokens, double ModelHours, decimal Charged, double Share,
    decimal Cost, decimal? Credit = null, IReadOnlyList<AccountRow>? Members = null);

/// <summary>The running cost of a window, from the settings and the GPUs' measured power.</summary>
public sealed record RunningCost(double Hours, double? GpuKwh, double OtherKwh, double Kwh, decimal Power, decimal Hardware, decimal Service, decimal Total);

/// <summary>One bucket of the window: its running cost by part, and what was used and spent in it.</summary>
public sealed record AccountBucket(DateTimeOffset At, decimal Power, decimal Hardware, decimal Service, decimal Total, decimal Charged, long Tokens, long Requests, double Kwh);

/// <summary>What was used in a window: requests, tokens, the model's time, the spend at the prices, the people who used it.</summary>
public sealed record AccountUse(long Requests, long Tokens, double ModelHours, decimal Charged, int People);

/// <summary>The same numbers for the window before, as long as this one, and the change to this one (null: nothing to compare).</summary>
public sealed record AccountBefore(DateTimeOffset From, DateTimeOffset To, decimal Total, AccountUse Used, double? TotalChange, double? ChargedChange, double? TokensChange);

/// <summary>What a unit of use costs at the running cost.</summary>
public sealed record AccountUnit(decimal? PerMillionTokens, decimal? PerRequest, decimal? PerPerson, decimal? PerModelHour);

/// <summary>
/// How well the machine pays: the GPUs' busy share and the running cost of their idle time; a month at this window's rate
/// (running cost and spend); the spend at the prices as a share of the running cost.
/// </summary>
public sealed record AccountHealth(double? GpuBusy, decimal? IdleCost, decimal MonthlyRunning, decimal MonthlyCharged, double? Recovered);

/// <summary>Everything Admin → Accounting and its exports show.</summary>
public sealed record AccountingReport(DateTimeOffset From, DateTimeOffset To, string Basis, CostOptions Settings, RunningCost Running, AccountUse Used, AccountUnit Unit,
    AccountHealth Health, AccountBefore? Before, TimeSpan Interval, IReadOnlyList<AccountBucket> Series, IReadOnlyList<AccountRow> People, IReadOnlyList<AccountRow> Groups,
    IReadOnlyList<AccountRow> CostCentres, IReadOnlyList<AccountRow> Projects, IReadOnlyList<AccountRow> Models, IReadOnlyList<AccountRow> Sources, IReadOnlyList<AccountRow> Kinds);

/// <summary>
/// The accountant's view (billing.read): over a time, what running the machine cost (<see cref="RunningCost"/>: power
/// measured on the GPUs and set for the rest, times the overhead; the hardware written off; service), bucket by bucket;
/// what was used and what a unit costs; the window before, to compare; the GPUs' idle time and what it cost; a month at
/// this rate; and the running cost shared out by use to each person, group (a person in two groups counts in both, its
/// members under it), cost centre, project, model, source and kind of work. PDF, Excel and CSV for the finance team.
/// </summary>
public static class Accounting
{
    public static readonly string[] Bases = ["spend", "tokens", "time"];

    /// <summary>An average month, in hours.</summary>
    public const double MonthHours = 365.25 * 24 / 12;

    public static void MapAccounting(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/accounting", JsonAsync).RequirePermission(Llm.Core.Access.Permissions.BillingRead);
        app.MapGet("/api/admin/accounting/export", ExportAsync).RequirePermission(Llm.Core.Access.Permissions.BillingRead);
    }

    /// <summary>A part of the window: its hours, the GPUs' measured kWh (null: not measured), unrounded.</summary>
    private readonly record struct Part(double Hours, double? GpuKwh);

    /// <summary>The running cost of a window: the GPUs' kWh measured (null: Prometheus could not say), the rest from the settings.</summary>
    public static RunningCost Cost(CostOptions o, DateTimeOffset from, DateTimeOffset to, double? gpuKwh) => Sum(o, [new Part(Math.Max(0, (to - from).TotalHours), gpuKwh)]);

    private static RunningCost Sum(CostOptions o, IReadOnlyList<Part> parts)
    {
        var hours = parts.Sum(p => p.Hours);
        double? gpu = parts.Any(p => p.GpuKwh is not null) ? parts.Sum(p => p.GpuKwh ?? 0) : null;
        var months = (decimal)(hours / MonthHours);
        var other = (double)o.OtherPowerWatts * hours / 1000;
        var kwh = ((gpu ?? 0) + other) * (double)Math.Max(1, o.Pue);
        var power = Math.Round((decimal)kwh * o.PowerPricePerKwh, 2);
        var hardware = o.HardwareMonths > 0 ? Math.Round(o.HardwarePrice / o.HardwareMonths * months, 2) : 0;
        var service = Math.Round(o.ServicePerMonth * months, 2);
        return new RunningCost(Math.Round(hours, 2), gpu is null ? null : Math.Round(gpu.Value, 3), Math.Round(other, 3), Math.Round(kwh, 3), power, hardware, service, power + hardware + service);
    }

    /// <summary>Each one's share of what was used, by the basis; all of them add up to 1 when anything was used.</summary>
    public static double ShareOf(string basis, long tokens, double seconds, decimal charged, long allTokens, double allSeconds, decimal allCharged) => basis switch
    {
        "tokens" => allTokens > 0 ? (double)tokens / allTokens : 0,
        "time" => allSeconds > 0 ? seconds / allSeconds : 0,
        _ => allCharged > 0 ? (double)(charged / allCharged) : 0,
    };

    /// <summary>How much b is more (or less) than a, as a fraction; null when a is nothing.</summary>
    public static double? Change(double before, double now) => before > 0 ? Math.Round((now - before) / before, 4) : null;

    private sealed record Used(string? Person, Guid? Project, string Model, string Surface, string Kind, long Requests, long Tokens, double Seconds, decimal Charged);

    private static async Task<IResult> JsonAsync(DateTimeOffset from, DateTimeOffset to, string? basis, LedgerSql sql, AppDbContext db, Credit credit, PromDatasource prom,
        IOptionsMonitor<CostOptions> costs, TimeProvider clock, ILoggerFactory logs, CancellationToken ct)
    {
        try
        {
            return await ReadAsync(from, to, basis, sql, db, credit, prom, costs.CurrentValue, clock, logs, ct) is { } r ? Results.Ok(r) : Problem(from, to, basis)!;
        }
        catch (Npgsql.NpgsqlException ex)
        {
            return AuthEndpoints.Problem(503, "usage", "Usage cannot be read right now: " + ex.Message);
        }
    }

    private static IResult? Problem(DateTimeOffset from, DateTimeOffset to, string? basis) =>
        to <= from || to - from > DashboardEndpoints.MaxRange ? AuthEndpoints.Problem(400, "range", "The time range must be positive and at most 400 days.")
        : basis is { Length: > 0 } && !Bases.Contains(basis) ? AuthEndpoints.Problem(400, "basis", "Share by spend, tokens or time.")
        : null;

    /// <summary>The report; null when the window or the basis is not one (<see cref="Problem"/> says why).</summary>
    public static async Task<AccountingReport?> ReadAsync(DateTimeOffset from, DateTimeOffset to, string? basis, LedgerSql sql, AppDbContext db, Credit credit, PromDatasource prom,
        CostOptions o, TimeProvider clock, ILoggerFactory logs, CancellationToken ct)
    {
        if (Problem(from, to, basis) is not null)
        {
            return null;
        }
        var by = basis is { Length: > 0 } ? basis : Bases.Contains(o.ShareBy) ? o.ShareBy : "spend";
        var now = clock.GetUtcNow();
        var end = to > now ? now : to;
        var log = logs.CreateLogger("Llm.Api.Accounting");
        var interval = Projects.ProjectReport.IntervalFor(from, to);
        var window = new Dictionary<string, object> { ["from"] = from.ToUniversalTime(), ["to"] = to.ToUniversalTime(), ["secs"] = interval.TotalSeconds };
        var table = await sql.QueryAsync("""
            select r.person, r.project_id, coalesce(r.model, ''), coalesce(r.surface, ''), coalesce(r.credit_kind, ''), count(*),
                   coalesce(sum(r.prompt_tokens),0) + coalesce(sum(r.completion_tokens),0), coalesce(sum(r.duration_ms),0) / 1000.0, coalesce(sum(r.cost),0)
            from gateway.requests r
            where r.started_at >= @from and r.started_at < @to and r.status <> 'refused'
            group by 1, 2, 3, 4, 5
            """, window, ct);
        var rows = table.Rows.Select(r => new Used(((string?)r[0])?.ToLowerInvariant(), r[1] is Guid g ? g : null, (string)r[2]!, (string)r[3]!, (string)r[4]!, Num(r[5]), Num(r[6]),
            Dbl(r[7]), Dec(r[8]))).ToList();
        var byTime = (await sql.QueryAsync("""
            select floor(extract(epoch from r.started_at)/@secs)*@secs, count(*), coalesce(sum(r.prompt_tokens),0) + coalesce(sum(r.completion_tokens),0), coalesce(sum(r.cost),0)
            from gateway.requests r
            where r.started_at >= @from and r.started_at < @to and r.status <> 'refused'
            group by 1
            """, window, ct)).Rows.ToDictionary(r => (long)Dbl(r[0]), r => (Requests: Num(r[1]), Tokens: Num(r[2]), Charged: Dec(r[3])));

        // The running cost bucket by bucket: the GPUs' average draw over each, measured.
        var step = (long)interval.TotalSeconds;
        var starts = new List<DateTimeOffset>();
        for (var at = DateTimeOffset.FromUnixTimeSeconds((long)Math.Floor(from.ToUnixTimeSeconds() / (double)step) * step); at < to && starts.Count < 2000; at += interval)
        {
            starts.Add(at);
        }
        var watts = await WattsAsync(prom, starts, interval, from, end, log, ct);
        var series = new List<AccountBucket>();
        var parts = new List<Part>();
        foreach (var at in starts)
        {
            var a = at < from ? from : at;
            var b = at + interval > to ? to : at + interval;
            b = b > end ? end : b;
            var hours = b > a ? (b - a).TotalHours : 0;
            double? gpu = watts.TryGetValue(at, out var w) ? w * hours / 1000 : null;
            var part = new Part(hours, gpu);
            parts.Add(part);
            var c = Sum(o, [part]);
            var used = byTime.GetValueOrDefault(at.ToUnixTimeSeconds());
            series.Add(new AccountBucket(at, c.Power, c.Hardware, c.Service, c.Total, Math.Round(used.Charged, 4), used.Tokens, used.Requests, c.Kwh));
        }
        var running = Sum(o, parts);
        long allTokens = rows.Sum(r => r.Tokens), allRequests = rows.Sum(r => r.Requests);
        var allSeconds = rows.Sum(r => r.Seconds);
        var allCharged = rows.Sum(r => r.Charged);
        AccountRow Row(string kind, Guid? id, string name, string? email, string? centre, int people, IEnumerable<Used> some, decimal? creditOf = null, IReadOnlyList<AccountRow>? members = null)
        {
            var u = some.ToList();
            long tokens = u.Sum(x => x.Tokens), requests = u.Sum(x => x.Requests);
            var seconds = u.Sum(x => x.Seconds);
            var charged = u.Sum(x => x.Charged);
            var share = ShareOf(by, tokens, seconds, charged, allTokens, allSeconds, allCharged);
            return new AccountRow(kind, id, name, email, centre, people, requests, tokens, Math.Round(seconds / 3600, 2), Math.Round(charged, 4), Math.Round(share, 6),
                Math.Round(running.Total * (decimal)share, 2), creditOf, members);
        }
        static List<AccountRow> Ordered(IEnumerable<AccountRow> r) => [.. r.OrderBy(x => x.Name.StartsWith('(')).ThenByDescending(x => x.Cost).ThenByDescending(x => x.Requests)];

        var byPerson = rows.GroupBy(r => r.Person ?? "", StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var users = (await db.Users.AsNoTracking().Where(u => u.Email != null).Select(u => new { u.Id, u.DisplayName, u.UserName, u.Email }).ToListAsync(ct))
            .GroupBy(u => u.Email!.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        AccountRow Person(string email, IEnumerable<Used> some) => users.TryGetValue(email, out var u)
            ? Row("person", u.Id, u.DisplayName is { Length: > 0 } d ? d : u.UserName ?? email, email, null, 1, some)
            : Row("person", null, email, email.Contains('@') ? email : null, null, 1, some);
        var people = byPerson.Where(p => p.Key.Length > 0).Select(p => Person(p.Key, p.Value)).ToList();
        if (byPerson.TryGetValue("", out var nobody))
        {
            people.Add(Row("person", null, "(attributed to nobody)", null, null, 0, nobody));
        }
        var personRows = people.Where(p => p.Email is not null).ToDictionary(p => p.Email!, StringComparer.Ordinal);

        var groups = new List<AccountRow>();
        var grouped = new HashSet<string>(StringComparer.Ordinal);
        var centres = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in await db.Groups.AsNoTracking().OrderBy(g => g.Name).ToListAsync(ct))
        {
            var members = (await credit.MembersAsync(g, ct)).Select(e => e.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
            grouped.UnionWith(members);
            var monthly = Core.Access.Credits.Kinds.Any(k => Core.Access.Credits.Of(g, k) is not null) ? Core.Access.Credits.Kinds.Sum(k => Core.Access.Credits.Of(g, k) ?? 0m) : (decimal?)null;
            groups.Add(Row("group", g.Id, g.Name, null, g.CostCentre, members.Count, members.SelectMany(m => byPerson.GetValueOrDefault(m) ?? []),
                monthly, Ordered(members.Where(personRows.ContainsKey).Select(m => personRows[m]))));
            if (g.CostCentre is { Length: > 0 } c)
            {
                if (!centres.TryGetValue(c, out var at))
                {
                    centres[c] = at = new HashSet<string>(StringComparer.Ordinal);
                }
                at.UnionWith(members);
            }
        }
        var loose = byPerson.Keys.Where(k => k.Length > 0 && !grouped.Contains(k)).ToList();
        groups.Add(Row("group", null, "(in no group)", null, null, loose.Count, loose.SelectMany(m => byPerson[m]), null, Ordered(loose.Select(m => personRows[m]))));
        var centreRows = centres.OrderBy(c => c.Key, StringComparer.OrdinalIgnoreCase)
            .Select(c => Row("cost centre", null, c.Key, null, c.Key, c.Value.Count, c.Value.SelectMany(m => byPerson.GetValueOrDefault(m) ?? []), null,
                Ordered(c.Value.Where(personRows.ContainsKey).Select(m => personRows[m])))).ToList();
        var ids = rows.Where(r => r.Project is not null).Select(r => r.Project!.Value).Distinct().ToList();
        var names = await db.Projects.AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        int PeopleIn(IEnumerable<Used> some) => some.Select(x => x.Person).Where(p => p is not null).Distinct().Count();
        var projects = rows.GroupBy(r => r.Project)
            .Select(g => Row("project", g.Key, g.Key is { } id ? names.GetValueOrDefault(id) ?? "(a deleted project)" : "(in no project)", null, null, PeopleIn(g), g)).ToList();
        var models = rows.GroupBy(r => r.Model).Select(g => Row("model", null, g.Key.Length > 0 ? g.Key : "(no model)", null, null, PeopleIn(g), g)).ToList();
        var sources = rows.GroupBy(r => r.Surface).Select(g => Row("source", null, Projects.ProjectReport.Surface(g.Key), null, null, PeopleIn(g), g)).ToList();
        var kinds = rows.GroupBy(r => r.Kind).Select(g => Row("kind", null, KindName(g.Key), null, null, PeopleIn(g), g)).ToList();

        var active = byPerson.Keys.Count(k => k.Length > 0);
        var modelHours = Math.Round(allSeconds / 3600, 2);
        var unit = new AccountUnit(
            allTokens > 0 ? Math.Round(running.Total / allTokens * 1_000_000, 4) : null,
            allRequests > 0 ? Math.Round(running.Total / allRequests, 4) : null,
            active > 0 ? Math.Round(running.Total / active, 2) : null,
            allSeconds > 0 ? Math.Round(running.Total / (decimal)(allSeconds / 3600), 2) : null);

        // The GPUs' busy share over the window, and what their idle time cost.
        double? busy = null;
        if (end > from)
        {
            busy = await ScalarAsync(prom, $"avg(avg_over_time(nvidia_smi_utilization_gpu_ratio[{(long)Math.Max(60, (end - from).TotalSeconds)}s]))", end, log, ct);
        }
        var rate = running.Hours > 0 ? MonthHours / running.Hours : 0;
        var health = new AccountHealth(busy is null ? null : Math.Round(busy.Value, 4), busy is null ? null : Math.Round(running.Total * (decimal)(1 - Math.Clamp(busy.Value, 0, 1)), 2),
            Math.Round(running.Total * (decimal)rate, 2), Math.Round(allCharged * (decimal)rate, 2), running.Total > 0 ? Math.Round((double)(allCharged / running.Total), 4) : null);

        // The window before, as long: its running cost (the settings of now) and its use.
        AccountBefore? before = null;
        var length = to - from;
        var prevFrom = from - length;
        if (length <= DashboardEndpoints.MaxRange)
        {
            var prev = (await sql.QueryAsync("""
                select count(*), coalesce(sum(r.prompt_tokens),0) + coalesce(sum(r.completion_tokens),0), coalesce(sum(r.duration_ms),0) / 1000.0, coalesce(sum(r.cost),0), count(distinct lower(r.person))
                from gateway.requests r
                where r.started_at >= @from and r.started_at < @to and r.status <> 'refused'
                """, new Dictionary<string, object> { ["from"] = prevFrom.ToUniversalTime(), ["to"] = from.ToUniversalTime() }, ct)).Rows[0];
            var prevUse = new AccountUse(Num(prev[0]), Num(prev[1]), Math.Round(Dbl(prev[2]) / 3600, 2), Math.Round(Dec(prev[3]), 4), (int)Num(prev[4]));
            var prevWatts = await ScalarAsync(prom, $"sum(avg_over_time(nvidia_smi_power_draw_watts[{(long)Math.Max(60, length.TotalSeconds)}s]))", from, log, ct);
            var prevCost = Cost(o, prevFrom, from, prevWatts is { } pw ? pw * length.TotalHours / 1000 : null);
            before = new AccountBefore(prevFrom, from, prevCost.Total, prevUse, Change((double)prevCost.Total, (double)running.Total), Change((double)prevUse.Charged, (double)allCharged),
                Change(prevUse.Tokens, allTokens));
        }
        return new AccountingReport(from, to, by, o, running, new AccountUse(allRequests, allTokens, modelHours, Math.Round(allCharged, 4), active), unit, health, before, interval, series,
            Ordered(people), [.. groups.OrderBy(g => g.Id is null).ThenByDescending(g => g.Cost)], Ordered(centreRows), Ordered(projects), Ordered(models), Ordered(sources), Ordered(kinds));
    }

    /// <summary>Each bucket's average GPU draw in watts (from its start), measured; buckets Prometheus has nothing for are left out.</summary>
    private static async Task<Dictionary<DateTimeOffset, double>> WattsAsync(PromDatasource prom, List<DateTimeOffset> starts, TimeSpan interval, DateTimeOffset from, DateTimeOffset end,
        ILogger log, CancellationToken ct)
    {
        var out_ = new Dictionary<DateTimeOffset, double>();
        if (starts.Count == 0 || end <= from)
        {
            return out_;
        }
        try
        {
            // Each whole bucket by its end: avg_over_time over the bucket. The last (now inside it) by itself, up to now.
            var whole = starts.Where(s => s + interval <= end).ToList();
            if (whole.Count > 0)
            {
                var got = await prom.RangeAsync($"sum(avg_over_time(nvidia_smi_power_draw_watts[{(long)interval.TotalSeconds}s]))", whole[0] + interval, whole[^1] + interval, interval, ct);
                if (got.Count > 0)
                {
                    foreach (var p in got[0].Points)
                    {
                        if (p[1] is double w)
                        {
                            out_[DateTimeOffset.FromUnixTimeMilliseconds((long)p[0]!.Value) - interval] = w;
                        }
                    }
                }
            }
            foreach (var s in starts.Where(s => s < end && s + interval > end))
            {
                var a = s < from ? from : s;
                if (await ScalarAsync(prom, $"sum(avg_over_time(nvidia_smi_power_draw_watts[{(long)Math.Max(60, (end - a).TotalSeconds)}s]))", end, log, ct) is { } w)
                {
                    out_[s] = w;
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or DatasourceException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            PowerUnread(log, ex.Message);
        }
        return out_;
    }

    private static async Task<double?> ScalarAsync(PromDatasource prom, string query, DateTimeOffset at, ILogger log, CancellationToken ct)
    {
        try
        {
            var got = await prom.InstantAsync(query, at, ct);
            return got.Count > 0 && got[0].Points.Count > 0 ? got[0].Points[0][1] : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or DatasourceException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            PowerUnread(log, ex.Message);
            return null;
        }
    }

    /// <summary>A kind of work as the ledger names it (its credit kind), as people say it.</summary>
    public static string KindName(string kind) => kind switch
    {
        "chat" => "Chat answers",
        "api" => "API keys and Code Arena",
        "pictures" => "Pictures",
        "video" => "Video",
        "speech" => "Speech",
        "" => "(other)",
        _ => kind,
    };

    private static async Task<IResult> ExportAsync(DateTimeOffset from, DateTimeOffset to, string? basis, string? format, ClaimsPrincipal principal, UserManager<AppUser> users,
        LedgerSql sql, AppDbContext db, Credit credit, PromDatasource prom, IOptionsMonitor<CostOptions> costs, Audit audit, TimeProvider clock, ILoggerFactory logs, CancellationToken ct)
    {
        if (Problem(from, to, basis) is { } problem)
        {
            return problem;
        }
        if (!ReportFiles.Formats.Contains((format ?? "pdf").ToLowerInvariant()))
        {
            return AuthEndpoints.Problem(400, "format", "Export as pdf, xlsx or csv.");
        }
        AccountingReport r;
        try
        {
            r = (await ReadAsync(from, to, basis, sql, db, credit, prom, costs.CurrentValue, clock, logs, ct))!;
        }
        catch (Npgsql.NpgsqlException ex)
        {
            return AuthEndpoints.Problem(503, "usage", "Usage cannot be read right now: " + ex.Message);
        }
        var me = (await users.GetUserAsync(principal))!;
        await audit.WriteAsync("accounting.export", "accounting", detail: $"{(format ?? "pdf").ToLowerInvariant()}, {from:O} to {to:O}");
        return ReportFiles.Result(Document(r, string.IsNullOrEmpty(me.DisplayName) ? me.UserName ?? "" : me.DisplayName, clock.GetUtcNow()), format, "accounting")!;
    }

    /// <summary>The report as a document: its figures, its charts, and a table for each way it is shared out.</summary>
    public static ReportDocument Document(AccountingReport r, string madeFor, DateTimeOffset made)
    {
        var m = ReportFiles.Money;
        var run = r.Running;
        string Pct(double? v) => v is { } x ? (x * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%" : "–";
        string Delta(double? v) => v is { } x ? (x >= 0 ? "+" : "") + Pct(x) + " on the time before" : "Nothing to compare with";
        var figures = new List<ReportFigure>
        {
            new("Running cost", m(run.Total), Delta(r.Before?.TotalChange)),
            new("Power", m(run.Power), $"{run.Kwh.ToString("#,##0.#", CultureInfo.InvariantCulture)} kWh at {m(r.Settings.PowerPricePerKwh)}" + (run.GpuKwh is null ? "; the GPUs not measured" : "")),
            new("Hardware", m(run.Hardware), r.Settings.HardwarePrice > 0 ? $"{m(r.Settings.HardwarePrice)} over {r.Settings.HardwareMonths} months" : "No price set"),
            new("Service", m(run.Service), r.Settings.ServicePerMonth > 0 ? $"{m(r.Settings.ServicePerMonth)} a month" : "No monthly cost set"),
            new("A month at this rate", m(r.Health.MonthlyRunning), $"Spend at the prices {m(r.Health.MonthlyCharged)} a month"),
            new("Spend at the prices", m(r.Used.Charged), r.Health.Recovered is { } rec ? $"{Pct(rec)} of the running cost" : "No running cost set"),
            new("GPU idle", r.Health.IdleCost is { } idle ? m(idle) : "–", r.Health.GpuBusy is { } b ? $"Busy {Pct(b)} of the time" : "The GPUs not measured"),
            new("A million tokens", r.Unit.PerMillionTokens is { } t ? m(t) : "–", $"{ReportFiles.Compact(r.Used.Tokens)} tokens, {r.Used.Requests:#,##0} requests, {r.Used.People} people"),
        };
        var charts = new List<ReportChart>
        {
            new("Running cost", ["Power", "Hardware", "Service"], [.. r.Series.Select(b => (b.At, new[] { (double)b.Power, (double)b.Hardware, (double)b.Service }))], ReportKind.Money),
            new("Spend at the prices", ["Spend"], [.. r.Series.Select(b => (b.At, new[] { (double)b.Charged }))], ReportKind.Money),
        };
        ReportTable Shares(string title, string first, IEnumerable<AccountRow> rows, string? note = null) => new(title,
            [new(first, Width: 2.4), new("People", ReportKind.Number, 0.7), new("Requests", ReportKind.Number), new("Tokens", ReportKind.Number), new("Model hours", ReportKind.Number, 0.9),
                new("Spend", ReportKind.Money), new("Share (%)", ReportKind.Number, 0.8), new("Cost", ReportKind.Money)],
            [.. rows.Select(x => (object?[])[x.Name, x.People, x.Requests, x.Tokens, x.ModelHours, x.Charged, Math.Round(x.Share * 100, 2), x.Cost])], note);
        var tables = new List<ReportTable>
        {
            Shares("Groups", "Group", r.Groups, "A person in two groups counts in both, so the groups can add up to more than the whole."),
            Shares("Cost centres", "Cost centre", r.CostCentres, "Each person once in a cost centre."),
            Shares("People", "Person", r.People),
            Shares("Projects", "Project", r.Projects),
            Shares("Models", "Model", r.Models),
            Shares("Where it came from", "From", r.Sources),
            Shares("What it was for", "Kind", r.Kinds),
            new("By time", [new("From", ReportKind.Time, 1.4), new("Power", ReportKind.Money), new("Hardware", ReportKind.Money), new("Service", ReportKind.Money),
                    new("Running cost", ReportKind.Money), new("Spend", ReportKind.Money), new("Tokens", ReportKind.Number), new("Requests", ReportKind.Number)],
                [.. r.Series.Select(b => (object?[])[b.At, b.Power, b.Hardware, b.Service, b.Total, b.Charged, b.Tokens, b.Requests])]),
        };
        var basis = r.Basis switch { "tokens" => "tokens", "time" => "the model's time", _ => "spend at the prices" };
        return new ReportDocument("Accounting", $"What running the models cost, and each one's share by {basis}.", r.From, r.To, made, figures, charts, tables, madeFor);
    }

    private static readonly Action<ILogger, string, Exception?> PowerUnreadMessage =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(1, "PowerUnread"), "The GPUs' power could not be read: {Error}");

    private static void PowerUnread(ILogger log, string error) => PowerUnreadMessage(log, error, null);

    private static long Num(object? v) => v is null ? 0 : Convert.ToInt64(v, CultureInfo.InvariantCulture);

    private static double Dbl(object? v) => v is null ? 0 : Convert.ToDouble(v, CultureInfo.InvariantCulture);

    private static decimal Dec(object? v) => v is null ? 0 : Convert.ToDecimal(v, CultureInfo.InvariantCulture);
}
