using System.Globalization;
using System.Text;
using Llm.Api.Access;
using Llm.Api.Endpoints;
using Llm.Api.Gateway;
using Llm.Api.Identity;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Quality;

/// <summary>A model's (or a person's, a tag's, a day's) share of the prompt log in a window.</summary>
public sealed record PromptReportRow(string Name, long Count, long Failed = 0, long Stopped = 0, long Refused = 0, double? AverageMs = null, double? MedianMs = null, double? P95Ms = null,
    long Tokens = 0, long Api = 0, long Chat = 0);

/// <summary>The prompt log over a window: how many questions, from the chat and the API, how they ended, how long they took.</summary>
public sealed record PromptReport(DateTimeOffset From, DateTimeOffset To, long Api, long ApiFailed, long ApiRefused, long Chat, long ChatFailed, double? ApiP95Ms, double? ChatP95Ms,
    IReadOnlyList<PromptReportRow> ApiModels, IReadOnlyList<PromptReportRow> ChatModels, IReadOnlyList<PromptReportRow> People, IReadOnlyList<PromptReportRow> Tags,
    IReadOnlyList<PromptReportRow> Days);

/// <summary>
/// Admin → Prompt log → Report (prompts.review): over a window, the API requests by model (done, failed, stopped, refused,
/// the time from arrival to end: average, median, 95th percentile, and tokens) and the chat's answers by model, the people
/// who asked most, the tags (a chat question counts under its chat's), and each day's count. CSV for the spreadsheet; audited.
/// </summary>
public static class PromptLogReport
{
    public static void MapPromptLogReport(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin/prompts").RequirePermissions(Core.Access.Permissions.PromptsReview, Core.Access.Permissions.PromptsReview);
        g.MapGet("/report", async (DateTimeOffset from, DateTimeOffset to, string? format, LedgerSql sql, AppDbContext db, Audit audit, CancellationToken ct) =>
        {
            if (to <= from || to - from > Dashboards.DashboardEndpoints.MaxRange)
            {
                return AuthEndpoints.Problem(400, "range", "The time range must be positive and at most 400 days.");
            }
            var r = await ReadAsync(from, to, sql, db, ct);
            await audit.WriteAsync("prompts.report", null, detail: $"{from:O} to {to:O}{(format == "csv" ? " (CSV)" : "")}");
            return format == "csv"
                ? Results.File(Encoding.UTF8.GetBytes(Csv(r)), "text/csv; charset=utf-8", $"prompt-log-{from:yyyy-MM-dd}-to-{to:yyyy-MM-dd}.csv")
                : Results.Ok(r);
        });
    }

    private static long Num(object? v) => v is null or DBNull ? 0 : Convert.ToInt64(v, CultureInfo.InvariantCulture);
    private static string Day(object? v) => v switch
    {
        DateTime d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTimeOffset d => d.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        _ => (Convert.ToString(v, CultureInfo.InvariantCulture) ?? "")[..Math.Min(10, (Convert.ToString(v, CultureInfo.InvariantCulture) ?? "").Length)],
    };

    private static double? Ms(object? v) => v is null or DBNull ? null : Math.Round(Convert.ToDouble(v, CultureInfo.InvariantCulture));

    public static async Task<PromptReport> ReadAsync(DateTimeOffset from, DateTimeOffset to, LedgerSql sql, AppDbContext db, CancellationToken ct)
    {
        var w = new Dictionary<string, object> { ["from"] = from.ToUniversalTime(), ["to"] = to.ToUniversalTime() };
        const string Api = """ from prompt_log p where p."At" >= @from and p."At" < @to """;
        const string Asked = """ from chat_messages m join conversations c on c."Id" = m."ConversationId" where m."Role" = 'user' and m."CreatedAt" >= @from and m."CreatedAt" < @to """;
        const string Answers = """ from chat_messages a where a."Role" = 'assistant' and a."CreatedAt" >= @from and a."CreatedAt" < @to """;
        var apiModels = (await sql.QueryAsync($"""
            select p."Model", count(*), count(*) filter (where p."State" = 'failed'), count(*) filter (where p."State" = 'stopped'), count(*) filter (where p."State" = 'refused'),
                   avg(p."DurationMs"), percentile_cont(0.5) within group (order by p."DurationMs"), percentile_cont(0.95) within group (order by p."DurationMs"),
                   coalesce(sum(p."PromptTokens"), 0) + coalesce(sum(p."CompletionTokens"), 0)
            {Api} group by 1 order by 2 desc limit 50
            """, w, ct)).Rows.Select(r => new PromptReportRow((string?)r[0] is { Length: > 0 } m ? m : "(no model)", Num(r[1]), Num(r[2]), Num(r[3]), Num(r[4]), Ms(r[5]), Ms(r[6]), Ms(r[7]), Num(r[8]))).ToList();
        var chatModels = (await sql.QueryAsync($"""
            select coalesce(a."Model", ''), count(distinct coalesce(a."AnswerId", a."Id")), count(distinct coalesce(a."AnswerId", a."Id")) filter (where a."Status" = 2),
                   count(distinct coalesce(a."AnswerId", a."Id")) filter (where a."Status" = 1), 0,
                   avg(a."AnswerMs"), percentile_cont(0.5) within group (order by a."AnswerMs"), percentile_cont(0.95) within group (order by a."AnswerMs"), coalesce(sum(a."CompletionTokens"), 0)
            {Answers} group by 1 order by 2 desc limit 50
            """, w, ct)).Rows.Select(r => new PromptReportRow((string?)r[0] is { Length: > 0 } m ? m : "(no model)", Num(r[1]), Num(r[2]), Num(r[3]), 0, Ms(r[5]), Ms(r[6]), Ms(r[7]), Num(r[8]))).ToList();
        var totals = (await sql.QueryAsync($"""
            select (select percentile_cont(0.95) within group (order by p."DurationMs") {Api}), (select percentile_cont(0.95) within group (order by a."AnswerMs") {Answers}),
                   (select count(*) {Asked})
            """, w, ct)).Rows[0];
        var people = (await sql.QueryAsync($"""
            select x.person, sum(x.api), sum(x.chat) from (
              select p."UserId" as person, count(*) as api, 0 as chat {Api} group by 1
              union all
              select coalesce(m."AuthorId", c."UserId"), 0, count(*) {Asked} group by 1) x
            group by 1 order by 2 + 3 desc limit 50
            """, w, ct)).Rows.Select(r => (Id: r[0] is Guid g ? g : Guid.Parse(Convert.ToString(r[0], CultureInfo.InvariantCulture)!), Api: Num(r[1]), Chat: Num(r[2]))).ToList();
        var ids = people.Select(p => p.Id).ToList();
        var names = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName is { Length: > 0 } d ? d : u.UserName ?? u.Email ?? "", ct);
        var tags = (await sql.QueryAsync($"""
            select x.tag, sum(x.api), sum(x.chat) from (
              select t as tag, count(*) as api, 0 as chat from prompt_log p cross join lateral unnest(p."Tags") t where p."At" >= @from and p."At" < @to group by 1
              union all
              select t, 0, count(*) from chat_messages m join conversations c on c."Id" = m."ConversationId" cross join lateral unnest(c."Tags") t
              where m."Role" = 'user' and m."CreatedAt" >= @from and m."CreatedAt" < @to group by 1) x
            group by 1 order by 2 + 3 desc limit 100
            """, w, ct)).Rows.Select(r => new PromptReportRow((string)r[0]!, Num(r[1]) + Num(r[2]), Api: Num(r[1]), Chat: Num(r[2]))).ToList();
        var days = (await sql.QueryAsync($"""
            select x.day, sum(x.api), sum(x.chat) from (
              select date_trunc('day', p."At" at time zone 'UTC') as day, count(*) as api, 0 as chat {Api} group by 1
              union all
              select date_trunc('day', m."CreatedAt" at time zone 'UTC'), 0, count(*) {Asked} group by 1) x
            group by 1 order by 1 limit 400
            """, w, ct)).Rows.Select(r => new PromptReportRow(Day(r[0]), Num(r[1]) + Num(r[2]), Api: Num(r[1]), Chat: Num(r[2]))).ToList();
        return new PromptReport(from, to, apiModels.Sum(m => m.Count), apiModels.Sum(m => m.Failed), apiModels.Sum(m => m.Refused), Num(totals[2]), chatModels.Sum(m => m.Failed),
            Ms(totals[0]), Ms(totals[1]), apiModels, chatModels,
            [.. people.Select(p => new PromptReportRow(names.GetValueOrDefault(p.Id) ?? "(a deleted person)", p.Api + p.Chat, Api: p.Api, Chat: p.Chat))], tags, days);
    }

    private static string Csv(PromptReport r)
    {
        var csv = new StringBuilder("section,name,count,failed,stopped,refused,average_ms,median_ms,p95_ms,tokens,api,chat\n");
        static string N(double? v) => v?.ToString("0", CultureInfo.InvariantCulture) ?? "";
        void Add(string section, IEnumerable<PromptReportRow> rows)
        {
            foreach (var x in rows)
            {
                csv.AppendJoin(',', new[]
                {
                    section, Operations.OperationsEndpoints.Csv(x.Name), x.Count.ToString(CultureInfo.InvariantCulture), x.Failed.ToString(CultureInfo.InvariantCulture),
                    x.Stopped.ToString(CultureInfo.InvariantCulture), x.Refused.ToString(CultureInfo.InvariantCulture), N(x.AverageMs), N(x.MedianMs), N(x.P95Ms),
                    x.Tokens.ToString(CultureInfo.InvariantCulture), x.Api.ToString(CultureInfo.InvariantCulture), x.Chat.ToString(CultureInfo.InvariantCulture),
                }).Append('\n');
            }
        }
        Add("api model", r.ApiModels);
        Add("chat model", r.ChatModels);
        Add("person", r.People);
        Add("tag", r.Tags);
        Add("day", r.Days);
        return csv.ToString();
    }
}
