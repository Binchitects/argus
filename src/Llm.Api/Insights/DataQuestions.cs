using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Llm.Api.Access;
using Llm.Api.Chat;
using Llm.Core.Data;
using Llm.Core.Identity;
using Llm.Core.Insights;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Insights;

/// <param name="Sql">"" when the model found the question cannot be answered from the tables.</param>
/// <param name="Chart">How it suggests drawing it: <c>{ type: bar|line|number|table, x, y: [] }</c>.</param>
public sealed record DataAnswer(string Question, string Sql, string Explanation, string Title, JsonObject Chart, DataResult? Result, string? Error, string? Model);

/// <summary>A question asked before, for one that builds on it ("and by country?").</summary>
public sealed record EarlierQuestion(string? Question, string? Sql);

/// <summary>
/// Ask your data: who may ask which database, a question turned into SQL by the model (as the person: their model,
/// credit and safeguards), the SQL checked and run read only, and every query kept for admins to read.
/// </summary>
public sealed partial class DataQuestions(AppDbContext db, AccessService access, OneQuestion one, IDataProtectionProvider protection, TimeProvider clock)
{
    private readonly IDataProtector _connections = protection.CreateProtector("data-source-connection");

    public string Protect(string connection) => _connections.Protect(connection);

    public string Unprotect(DataSource s) => _connections.Unprotect(s.ConnectionProtected);

    /// <summary>The sources this person may ask: everyone's, and their groups'.</summary>
    public async Task<List<DataSource>> SourcesForAsync(AppUser me, CancellationToken ct)
    {
        var groups = (await access.MembershipAsync(me, ct)).Groups.ToList();
        return await db.DataSources.AsNoTracking()
            .Where(s => s.Reach == DataSource.Company || (s.Reach == DataSource.Groups && s.GroupIds.Any(g => groups.Contains(g))))
            .OrderBy(s => s.Name).ToListAsync(ct);
    }

    public async Task<DataSource?> SourceForAsync(Guid id, AppUser me, CancellationToken ct) =>
        (await SourcesForAsync(me, ct)).FirstOrDefault(s => s.Id == id);

    /// <summary>Only the tables people may ask, as the database last described them.</summary>
    public static List<DataTable> Open(DataSource s) => [.. PostgresData.Parse(s.Schema).Where(t => s.Tables.Contains(t.Name))];

    /// <summary>Runs SQL on the source read only, and keeps that it was run (by whom, why, how it went).</summary>
    public async Task<DataResult> RunAsync(DataSource s, string sql, AppUser me, string via, string? question, CancellationToken ct)
    {
        var timer = Stopwatch.StartNew();
        var query = new DataQuery { SourceId = s.Id, UserId = me.Id, Question = question is { Length: > 2000 } q ? q[..2000] : question, Sql = sql, Via = via };
        try
        {
            var result = await PostgresData.RunAsync(Unprotect(s), sql, s.Tables, s.MostRows, s.Seconds, ct);
            query.Rows = result.Rows.Count;
            return result;
        }
        catch (DataException ex)
        {
            query.Error = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message;
            throw;
        }
        finally
        {
            query.Milliseconds = (int)timer.ElapsedMilliseconds;
            db.DataQueries.Add(query);
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }

    private const string Shape =
        """{"sql": "one SELECT (or WITH … SELECT) statement", "explanation": "what it counts or lists, in one or two plain sentences for someone who does not read SQL", "title": "a short title for the result", "chart": {"type": "bar, line, number or table", "x": "the column along the bottom", "y": ["the columns to plot"]}}""";

    /// <summary>What the model is told: the database's open tables and the admin's notes, and how to answer.</summary>
    public string Instructions(DataSource s, EarlierQuestion? earlier)
    {
        var today = clock.GetUtcNow().ToString("yyyy-MM-dd (dddd)", CultureInfo.InvariantCulture);
        var notes = s.Notes is { Length: > 0 } n ? $"\nWhat to know about the data:\n{n}\n" : "";
        var before = earlier is { Sql.Length: > 0 }
            ? $"\nThe question before this one was \"{earlier.Question}\", answered with:\n{earlier.Sql}\nThe new question may build on it (\"and by country?\").\n"
            : "";
        return $$"""
            You answer questions about a PostgreSQL database by writing one query that only reads. The database is "{{s.Name}}"{{(s.Description is { Length: > 0 } d ? $": {d}" : "")}}.
            Today is {{today}}, UTC.

            The tables you may read, and nothing else:
            {{PostgresData.Describe(Open(s))}}{{notes}}{{before}}
            Answer with JSON only, no other words:
            {{Shape}}

            Write the tables with their schema (public.orders). Give the columns short names people read ("Orders", "Month"). At most {{s.MostRows}} rows, in a sensible order.
            One figure: one row and one column, chart "number". Change over time: one date column first (date_trunc('month', placed_at)::date as "Month"), chart "line". A few groups compared: chart "bar". A list: chart "table".
            If the tables cannot answer the question, set "sql" to "" and say why in "explanation".
            """;
    }

    /// <summary>The question to SQL by the model, run; when it fails, the model is told why and writes it once more.</summary>
    public async Task<(DataAnswer? Answer, IResult? Problem)> AskAsync(DataSource s, AppUser me, string question, EarlierQuestion? earlier, string via, CancellationToken ct)
    {
        var system = Instructions(s, earlier);
        var asked = await one.AskAsync(me, question, system, null, 1500, "data", ct);
        if (asked.Problem is not null)
        {
            return (null, asked.Problem);
        }
        var said = Read(asked.Text ?? "");
        if (said.Sql.Length == 0)
        {
            return (new DataAnswer(question, "", said.Explanation is { Length: > 0 } why ? why : "The tables open to questions cannot answer that.", said.Title, said.Chart, null, null, asked.Model), null);
        }
        try
        {
            return (said with { Question = question, Result = await RunAsync(s, said.Sql, me, via, question, ct), Model = asked.Model }, null);
        }
        catch (DataException first)
        {
            var again = await one.AskAsync(me, question, system, null, 1500, "data", ct,
            [
                new JsonObject { ["role"] = "assistant", ["content"] = asked.Text },
                new JsonObject { ["role"] = "user", ["content"] = $"That query failed: {first.Message}\nWrite it again, as JSON only." },
            ]);
            var fixedUp = again.Problem is null ? Read(again.Text ?? "") : null;
            if (fixedUp is not { Sql.Length: > 0 })
            {
                return (said with { Question = question, Error = first.Message, Model = asked.Model }, null);
            }
            try
            {
                return (fixedUp with { Question = question, Result = await RunAsync(s, fixedUp.Sql, me, via, question, ct), Model = again.Model }, null);
            }
            catch (DataException second)
            {
                return (fixedUp with { Question = question, Error = second.Message, Model = again.Model }, null);
            }
        }
    }

    /// <summary>The model's JSON (in a code fence or not); a bare SQL block when it wrote no JSON.</summary>
    public static DataAnswer Read(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            try
            {
                if (JsonNode.Parse(text[start..(end + 1)]) is JsonObject o)
                {
                    var chart = o["chart"] as JsonObject ?? [];
                    return new DataAnswer("", Str(o["sql"]).Trim(), Str(o["explanation"]).Trim(), Str(o["title"]).Trim(), (JsonObject)chart.DeepClone(), null, null, null);
                }
            }
            catch (System.Text.Json.JsonException)
            {
            }
        }
        var fenced = SqlFence().Match(text);
        return new DataAnswer("", fenced.Success ? fenced.Groups[1].Value.Trim() : "", "", "", [], null, null, null);
    }

    private static string Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    [GeneratedRegex(@"```sql\s*\n(.*?)```", RegexOptions.Singleline)]
    private static partial Regex SqlFence();
}
