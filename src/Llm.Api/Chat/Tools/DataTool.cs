using System.Text;
using System.Text.Json.Nodes;
using Llm.Api.Insights;
using Llm.Core.Chat;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Chat.Tools;

/// <summary>
/// Company data in the chat: the databases an admin connected and the person may ask; the model reads their open tables,
/// writes SQL and runs it read only (each query kept for admins, as on the Data page). The answer shows the rows.
/// </summary>
public sealed class DataTool(DataQuestions questions, AppDbContext db) : IChatTool
{
    public const string ToolId = "data";
    private const int RowsShown = 50;

    public string Id => ToolId;
    public string Title => "Company data";
    public string Description => "Answers from the databases an admin connected: writes SQL, runs it read only, and shows the rows it found.";
    public string Icon => "database";

    public ToolSetting Defaults() => new() { ToolId = Id, OnByDefault = false };

    public async Task<string?> UnavailableAsync(CancellationToken ct) =>
        await db.DataSources.AnyAsync(s => s.Tables.Count > 0, ct) ? null : "No database is open to questions yet: an admin connects one (Admin → Data sources).";

    private const string Instructions =
        "You can read the company's databases (data_* functions), read only: PostgreSQL, MySQL or MariaDB, or SQL Server, each said with its tables.\n" +
        "- First data_tables, to see the databases you may ask, their tables and columns. Then data_query with one SELECT; use only the tables listed, with their schema.\n" +
        "- Say what the numbers show in plain words, then the rows as a Markdown table when there are a few; show the SQL in a ```sql block so the person can check it.\n" +
        "- When a query fails, read why and fix it; do not guess numbers the query did not return.";

    private static readonly JsonArray Functions =
    [
        Schema.Function("data_tables", "Lists the databases you may ask, and their tables with columns and types.", []),
        Schema.Function("data_query", "Runs one read-only SQL query (SELECT or WITH … SELECT) on a database and returns its rows (the first 50 here).",
            new JsonObject { ["database"] = Schema.Text("The database's name, as data_tables lists it."), ["sql"] = Schema.Text("One SELECT statement, in the database's own SQL (as data_tables says).") }, "database", "sql"),
    ];

    public Task<IToolRun> StartAsync(ToolContext context, CancellationToken ct) =>
        Task.FromResult<IToolRun>(new LocalRun(Functions, Instructions, (name, args, token) => CallAsync(context, name, args, token)));

    private async Task<ToolResult> CallAsync(ToolContext context, string name, JsonObject args, CancellationToken ct)
    {
        var sources = await questions.SourcesForAsync(context.User, ct);
        if (name == "data_tables")
        {
            if (sources.Count == 0)
            {
                return new ToolResult("There is no database you may ask.", IsError: true);
            }
            var w = new StringBuilder();
            foreach (var s in sources)
            {
                var engine = DataEngines.Of(s);
                w.Append("Database \"").Append(s.Name).Append("\" (").Append(engine.Title).Append(')').Append(s.Description is { Length: > 0 } d ? $": {d}" : "").AppendLine();
                w.Append("Write the tables ").Append(engine.Names).AppendLine(".");
                if (s.Notes is { Length: > 0 } notes)
                {
                    w.Append("What to know: ").AppendLine(notes);
                }
                w.AppendLine(DataEngines.Describe(DataQuestions.Open(s)));
            }
            return new ToolResult(w.ToString());
        }
        if (name != "data_query")
        {
            return new ToolResult($"There is no function {name}.", IsError: true);
        }
        var database = Schema.Str(args, "database")?.Trim() ?? "";
        if (sources.FirstOrDefault(s => string.Equals(s.Name, database, StringComparison.OrdinalIgnoreCase)) is not { } source)
        {
            return new ToolResult($"There is no database \"{database}\" you may ask. These are: {string.Join(", ", sources.Select(s => s.Name))}.", IsError: true);
        }
        try
        {
            var result = await questions.RunAsync(source, Schema.Str(args, "sql") ?? "", context.User, "chat", null, ct);
            return new ToolResult(Table(result));
        }
        catch (DataException ex)
        {
            return new ToolResult($"The query failed: {ex.Message}", IsError: true);
        }
    }

    /// <summary>The rows as a Markdown table, the first 50; how many there were and how long it took.</summary>
    public static string Table(DataResult r)
    {
        var w = new StringBuilder();
        var shown = r.Rows.Take(RowsShown).ToList();
        w.Append(r.Rows.Count).Append(r.Truncated ? "+" : "").Append(r.Rows.Count == 1 ? " row" : " rows").Append(", in ").Append(r.Milliseconds).AppendLine(" ms.");
        if (r.Columns.Count == 0)
        {
            return w.ToString();
        }
        w.Append("| ").AppendJoin(" | ", r.Columns.Select(c => Cell(c.Name))).AppendLine(" |");
        w.Append('|').AppendJoin("", r.Columns.Select(c => c.Type == "number" ? " ---: |" : " --- |")).AppendLine();
        foreach (var row in shown)
        {
            w.Append("| ").AppendJoin(" | ", row.Select(v => Cell(v is null ? "" : v is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : v.ToJsonString()))).AppendLine(" |");
        }
        if (r.Rows.Count > shown.Count)
        {
            w.Append("(The first ").Append(RowsShown).Append(" of ").Append(r.Rows.Count).AppendLine(" rows.)");
        }
        return w.ToString();
    }

    private static string Cell(string s) => s.Replace("|", "\\|", StringComparison.Ordinal).ReplaceLineEndings(" ");
}
