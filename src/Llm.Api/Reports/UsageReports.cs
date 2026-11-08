using System.Globalization;
using System.Security.Claims;
using Llm.Api.Access;
using Llm.Api.Endpoints;
using Llm.Api.Gateway;
using Llm.Api.Identity;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Reports;

/// <summary>
/// The usage pages as files people can read and pass on (PDF, Excel, CSV) over any time: a person's own usage (Usage &amp;
/// cost → Mine), and everyone's by person (usage.read); by group and cost centre is Admin → Accounting's. All from the app's own ledger
/// as the pages read it, refusals left out. Each export is audited.
/// </summary>
public static class UsageReports
{
    public static void MapUsageReports(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/usage/me/export", MineAsync).PersonOnly();
        app.MapGet("/api/admin/usage/export", EveryoneAsync).RequirePermission(Core.Access.Permissions.UsageRead);
    }

    private const string Ledger = " from gateway.requests r where r.started_at >= @from and r.started_at < @to and r.status <> 'refused' ";

    private static IResult? Problem(DateTimeOffset from, DateTimeOffset to, string? format) =>
        Projects.ProjectEndpoints.RangeProblem(from, to)
        ?? (ReportFiles.Formats.Contains((format ?? "pdf").ToLowerInvariant()) ? null : AuthEndpoints.Problem(400, "format", "Export as pdf, xlsx or csv."));

    private static Dictionary<string, object> Window(DateTimeOffset from, DateTimeOffset to) => new()
    {
        ["from"] = from.ToUniversalTime(),
        ["to"] = to.ToUniversalTime(),
        ["secs"] = Projects.ProjectReport.IntervalFor(from, to).TotalSeconds,
    };

    /// <summary>Totals, a chart of tokens and one of cost, and the tables by model and by where it came from, of the requests <paramref name="where"/> picks.</summary>
    private static async Task<(List<ReportFigure> Figures, List<ReportChart> Charts, List<ReportTable> Tables)> CommonAsync(LedgerSql sql, string where,
        Dictionary<string, object> p, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var t = (await sql.QueryAsync($"""
            select count(*), coalesce(sum(greatest(r.prompt_tokens - r.cached_tokens, 0)),0), coalesce(sum(r.cached_tokens),0), coalesce(sum(r.completion_tokens),0),
                   coalesce(sum(r.cost),0), count(distinct r.person), coalesce(sum(r.duration_ms),0)
            {Ledger} {where}
            """, p, ct)).Rows[0];
        var byTime = await sql.QueryAsync($"""
            select floor(extract(epoch from r.started_at)/@secs)*@secs, coalesce(sum(r.prompt_tokens),0), coalesce(sum(r.completion_tokens),0), coalesce(sum(r.cost),0)
            {Ledger} {where} group by 1 order by 1
            """, p, ct);
        var byModel = await sql.QueryAsync($"select r.model, count(*), coalesce(sum(r.total_tokens),0), coalesce(sum(r.cost),0) {Ledger} {where} group by 1 order by 4 desc, 2 desc", p, ct);
        var bySurface = await sql.QueryAsync($"select r.surface, count(*), coalesce(sum(r.total_tokens),0), coalesce(sum(r.cost),0) {Ledger} {where} group by 1 order by 4 desc, 2 desc", p, ct);
        var step = (long)(double)p["secs"];
        var points = byTime.Rows.ToDictionary(r => (long)D(r[0]), r => (Input: D(r[1]), Output: D(r[2]), Cost: D(r[3])));
        var buckets = new List<(DateTimeOffset At, double In, double Out, double Cost)>();
        for (var at = (long)Math.Floor(from.ToUnixTimeSeconds() / (double)step) * step; at < to.ToUnixTimeSeconds() && buckets.Count < 2000; at += step)
        {
            var v = points.GetValueOrDefault(at);
            buckets.Add((DateTimeOffset.FromUnixTimeSeconds(at), v.Input, v.Output, v.Cost));
        }
        var (requests, input, cached, output, cost, people, ms) = (D(t[0]), D(t[1]), D(t[2]), D(t[3]), (decimal)D(t[4]), D(t[5]), D(t[6]));
        var prompt = input + cached;
        var figures = new List<ReportFigure>
        {
            new("Cost", ReportFiles.Money(cost)),
            new("Requests", requests.ToString("#,##0", CultureInfo.InvariantCulture), $"{byModel.Rows.Count} model{(byModel.Rows.Count == 1 ? "" : "s")}"),
            new("Input tokens", ReportFiles.Compact(prompt), prompt > 0 ? $"{Math.Round(100 * cached / prompt)}% from the cache" : null),
            new("Output tokens", ReportFiles.Compact(output)),
            new("People", people.ToString("0", CultureInfo.InvariantCulture)),
            new("Model time", ReportFiles.Hours(ms / 1000), "The models' time answering"),
        };
        var charts = new List<ReportChart>
        {
            new("Tokens", ["Input", "Output"], [.. buckets.Select(b => (b.At, new[] { b.In, b.Out }))]),
            new("Cost", ["Cost"], [.. buckets.Select(b => (b.At, new[] { b.Cost }))], ReportKind.Money),
        };
        var tables = new List<ReportTable>
        {
            new("Models", [new("Model", Width: 2.4), new("Requests", ReportKind.Number), new("Tokens", ReportKind.Number), new("Cost", ReportKind.Money)],
                [.. byModel.Rows.Select(r => (object?[])[r[0] as string ?? "", D(r[1]), D(r[2]), (decimal)D(r[3])])]),
            new("Where it came from", [new("From", Width: 2.4), new("Requests", ReportKind.Number), new("Tokens", ReportKind.Number), new("Cost", ReportKind.Money)],
                [.. bySurface.Rows.Select(r => (object?[])[Projects.ProjectReport.Surface(r[0] as string), D(r[1]), D(r[2]), (decimal)D(r[3])])]),
            new("By time", [new("From", ReportKind.Time, 1.4), new("Input tokens", ReportKind.Number), new("Output tokens", ReportKind.Number), new("Cost", ReportKind.Money)],
                [.. buckets.Where(b => b.In + b.Out > 0 || b.Cost > 0).Select(b => (object?[])[b.At, b.In, b.Out, (decimal)b.Cost])]),
        };
        return (figures, charts, tables);
    }

    private static async Task<IResult> MineAsync(DateTimeOffset from, DateTimeOffset to, string? format, ClaimsPrincipal principal, UserManager<AppUser> users, LedgerSql sql,
        Audit audit, TimeProvider clock, CancellationToken ct)
    {
        if (Problem(from, to, format) is { } problem)
        {
            return problem;
        }
        var me = (await users.GetUserAsync(principal))!;
        var p = Window(from, to);
        p["me"] = me.Email!.ToLowerInvariant();
        try
        {
            var (figures, charts, tables) = await CommonAsync(sql, "and r.person = @me", p, from, to, ct);
            figures.RemoveAll(f => f.Label == "People");
            var name = string.IsNullOrEmpty(me.DisplayName) ? me.UserName! : me.DisplayName;
            var doc = new ReportDocument($"Usage of {name}", "Your chats, Code Arena and API keys: tokens, cost and models, at the prices when each request ran.", from, to,
                clock.GetUtcNow(), figures, charts, tables, name);
            await audit.WriteAsync("usage.export", "me", detail: Detail(format, from, to));
            return ReportFiles.Result(doc, format, "usage-" + (me.UserName ?? "me"))!;
        }
        catch (Npgsql.NpgsqlException ex)
        {
            return AuthEndpoints.Problem(503, "usage", "Usage cannot be read right now: " + ex.Message);
        }
    }

    private static async Task<IResult> EveryoneAsync(DateTimeOffset from, DateTimeOffset to, string? format, ClaimsPrincipal principal, UserManager<AppUser> users, AppDbContext db,
        LedgerSql sql, Audit audit, TimeProvider clock, CancellationToken ct)
    {
        if (Problem(from, to, format) is { } problem)
        {
            return problem;
        }
        var me = (await users.GetUserAsync(principal))!;
        var p = Window(from, to);
        try
        {
            var (figures, charts, tables) = await CommonAsync(sql, "", p, from, to, ct);
            var byPerson = await sql.QueryAsync($"""
                select coalesce(r.person, '(no one)'), count(*), coalesce(sum(greatest(r.prompt_tokens - r.cached_tokens, 0)),0), coalesce(sum(r.cached_tokens),0),
                       coalesce(sum(r.completion_tokens),0), coalesce(sum(r.cost),0), coalesce(sum(r.duration_ms),0)
                {Ledger} group by 1 order by 6 desc, 2 desc
                """, p, ct);
            var names = await Names(db, ct);
            tables.Insert(0, new ReportTable("People",
                [new("Person", Width: 2.2), new("Email", Width: 2.2), new("Requests", ReportKind.Number), new("Input, cache miss", ReportKind.Number), new("Input, cache hit", ReportKind.Number),
                    new("Output", ReportKind.Number), new("Cost", ReportKind.Money), new("Model time", ReportKind.Hours)],
                [.. byPerson.Rows.Select(r => (object?[])[names.GetValueOrDefault((string)r[0]!) ?? (string)r[0]!, r[0], D(r[1]), D(r[2]), D(r[3]), D(r[4]), (decimal)D(r[5]), D(r[6]) / 1000])]));
            var doc = new ReportDocument("Usage by person", "Everyone's chats, Code Arena and API keys: each person's requests, tokens and cost.", from, to, clock.GetUtcNow(),
                figures, charts, tables, Name(me));
            await audit.WriteAsync("usage.export", "everyone", detail: Detail(format, from, to));
            return ReportFiles.Result(doc, format, "usage-by-person")!;
        }
        catch (Npgsql.NpgsqlException ex)
        {
            return AuthEndpoints.Problem(503, "usage", "Usage cannot be read right now: " + ex.Message);
        }
    }

    private static async Task<Dictionary<string, string>> Names(AppDbContext db, CancellationToken ct) =>
        (await db.Users.AsNoTracking().Where(u => u.Email != null).Select(u => new { u.Email, u.DisplayName, u.UserName }).ToListAsync(ct))
        .GroupBy(u => u.Email!.ToLowerInvariant()).ToDictionary(g => g.Key, g => string.IsNullOrEmpty(g.First().DisplayName) ? g.First().UserName ?? g.Key : g.First().DisplayName!);

    private static string Name(AppUser u) => string.IsNullOrEmpty(u.DisplayName) ? u.UserName ?? "" : u.DisplayName;

    private static string Detail(string? format, DateTimeOffset from, DateTimeOffset to) => $"{format ?? "pdf"}, {ReportDocument.Day(from)} to {ReportDocument.Day(to)}";

    private static double D(object? v) => v is null ? 0 : Convert.ToDouble(v, CultureInfo.InvariantCulture);
}
