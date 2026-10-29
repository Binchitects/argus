using Llm.Api.Access;
using Llm.Api.Endpoints;
using Llm.Api.Identity;
using Llm.Core.Data;
using Llm.Core.Insights;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Insights;

/// <summary>A data source to add or change: a missing value is kept; a connection is given again only to change it.</summary>
/// <param name="Kind">postgres (the default), mysql or sqlserver; given when it is added, and it stays.</param>
/// <param name="Reach">company or groups (<paramref name="GroupIds"/>).</param>
public sealed record DataSourceRequest(string? Name = null, string? Description = null, string? Connection = null, List<string>? Tables = null, string? Notes = null,
    string? Reach = null, List<Guid>? GroupIds = null, int? MostRows = null, int? Seconds = null, string? Kind = null);

/// <summary>A connection to try before it is saved (or a saved source's, read again), of a kind (a saved source's when not given).</summary>
public sealed record DataProbe(string? Connection, Guid? Id = null, string? Kind = null);

/// <summary>
/// Admin → Data sources: the databases people may ask (PostgreSQL, MySQL or MariaDB, SQL Server), which of their tables,
/// and who may ask them; and what was asked of them. The connection is kept protected, and never shown again.
/// MySQL's tables open are every table its account may read (<see cref="IDataEngine.GrantsOnly"/>).
/// </summary>
public static class DataSourceEndpoints
{
    public static void MapDataSources(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin/data-sources").RequireFeature("data").RequirePermissions(Core.Access.Permissions.DataManage, Core.Access.Permissions.DataManage);
        g.MapGet("", ListAsync);
        g.MapPost("/probe", ProbeAsync);
        g.MapPost("", AddAsync);
        g.MapPatch("/{id:guid}", UpdateAsync);
        g.MapPost("/{id:guid}/schema", ReadAgainAsync);
        g.MapDelete("/{id:guid}", RemoveAsync);
        g.MapPost("/{id:guid}/files", UploadAsync).DisableAntiforgery().WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(MostUpload))
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestFormLimitsAttribute { MultipartBodyLengthLimit = MostUpload });
        g.MapDelete("/{id:guid}/files/{table}", RemoveTableAsync);
        g.MapGet("/{id:guid}/queries", QueriesAsync);
    }

    private const long MostUpload = 1L * 1024 * 1024 * 1024;
    private const long MostFile = 200L * 1024 * 1024;

    /// <summary>The stack's own database, where data files go: as its own user (who makes their schemas and roles).</summary>
    private static string Owner(AppDbContext db) => db.Database.GetConnectionString() ?? throw new DataException("The stack's database has no connection string.");

    private static object Brief(DataSource s, DataQuestions questions) => new
    {
        s.Id, s.Name, s.Description, s.Kind, s.Tables, s.Notes, s.Reach, s.GroupIds, s.MostRows, s.Seconds, s.SchemaReadAt, s.CreatedAt, s.UpdatedAt,
        where = DataEngines.Of(s).Where(questions.Unprotect(s)),
        schema = DataEngines.Parse(s.Schema),
    };

    /// <summary>The kind asked for, or why it is none here.</summary>
    private static (IDataEngine? Engine, IResult? Problem) EngineOf(string? kind) =>
        DataEngines.All.TryGetValue((kind ?? DataSource.Postgres).Trim().ToLowerInvariant(), out var engine)
            ? (engine, null)
            : (null, AuthEndpoints.Problem(400, "kind", $"A database here is {string.Join(", ", DataEngines.All.Keys)}."));

    private static async Task<IResult> ListAsync(AppDbContext db, DataQuestions questions, CancellationToken ct)
    {
        var sources = await db.DataSources.AsNoTracking().OrderBy(s => s.Name).ToListAsync(ct);
        return Results.Ok(sources.Select(s => Brief(s, questions)));
    }

    /// <summary>The tables a connection's role sees, before it is saved; with a saved source's connection when none is given.</summary>
    private static async Task<IResult> ProbeAsync(DataProbe body, AppDbContext db, DataQuestions questions, CancellationToken ct)
    {
        var connection = body.Connection;
        var saved = body.Id is { } id ? await db.DataSources.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, ct) : null;
        if (string.IsNullOrWhiteSpace(connection) && saved is not null)
        {
            connection = questions.Unprotect(saved);
        }
        var (engine, wrong) = EngineOf(saved?.Kind ?? body.Kind);
        if (wrong is not null)
        {
            return wrong;
        }
        if (engine!.CheckConnection(connection) is { } bad)
        {
            return AuthEndpoints.Problem(400, "connection", bad);
        }
        try
        {
            var (tables, warnings) = await engine.ReadAsync(connection!, ct);
            return Results.Ok(new { tables, warnings, where = engine.Where(connection!), grantsOnly = engine.GrantsOnly });
        }
        catch (DataException ex)
        {
            return AuthEndpoints.Problem(400, "connection", ex.Message);
        }
    }

    /// <summary>Checks and applies a request; a new connection is read now (a wrong one is found here, not when someone asks).</summary>
    private static async Task<IResult?> ApplyAsync(DataSource s, DataSourceRequest body, AppDbContext db, DataQuestions questions, CancellationToken ct)
    {
        var engine = DataEngines.Of(s);
        if (body.Name is { } name)
        {
            name = name.Trim();
            if (name.Length is 0 or > 100)
            {
                return AuthEndpoints.Problem(400, "name", "A data source needs a name of up to 100 characters.");
            }
            if (await db.DataSources.AnyAsync(x => x.Name == name && x.Id != s.Id, ct))
            {
                return AuthEndpoints.Problem(409, "name", "Another data source has that name.");
            }
            s.Name = name;
        }
        if (body.Description is { } description)
        {
            s.Description = description.Trim() is var d && d.Length > 500 ? d[..500] : d;
        }
        if (body.Notes is { } notes)
        {
            s.Notes = notes.Trim() is var n && n.Length > 4000 ? n[..4000] : n;
        }
        if (body.Connection is { Length: > 0 } connection)
        {
            if (engine.CheckConnection(connection) is { } bad)
            {
                return AuthEndpoints.Problem(400, "connection", bad);
            }
            try
            {
                var (tables, _) = await engine.ReadAsync(connection, ct);
                s.Schema = System.Text.Json.JsonSerializer.Serialize(tables, System.Text.Json.JsonSerializerOptions.Web);
                s.SchemaReadAt = DateTimeOffset.UtcNow;
            }
            catch (DataException ex)
            {
                return AuthEndpoints.Problem(400, "connection", ex.Message);
            }
            s.ConnectionProtected = questions.Protect(connection);
        }
        var known = DataEngines.Parse(s.Schema).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        if (body.Tables is { } wanted)
        {
            var unknown = wanted.Where(t => !known.Contains(t)).ToList();
            if (unknown.Count > 0)
            {
                return AuthEndpoints.Problem(400, "tables", $"The database has no {string.Join(", ", unknown.Take(5))} its role may read.");
            }
            if (engine.GrantsOnly && !known.SetEquals(wanted))
            {
                return AuthEndpoints.Problem(400, "tables", $"{engine.Title} does not show which tables a query reads: every table its account may read is open. To keep one out, take its SELECT away from the account, then read its tables again.");
            }
            s.Tables = [.. wanted.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        }
        else if (engine.GrantsOnly)
        {
            s.Tables = [.. known.Order(StringComparer.Ordinal)];
        }
        if (body.Reach is { } reach)
        {
            if (reach is not (DataSource.Company or DataSource.Groups))
            {
                return AuthEndpoints.Problem(400, "reach", "Who may ask it: company or groups.");
            }
            s.Reach = reach;
        }
        if (body.GroupIds is { } groupIds)
        {
            var found = await db.Groups.AsNoTracking().Where(g => groupIds.Contains(g.Id)).Select(g => g.Id).ToListAsync(ct);
            if (found.Count != groupIds.Distinct().Count())
            {
                return AuthEndpoints.Problem(400, "groups", "A group of those is not here.");
            }
            s.GroupIds = found;
        }
        if (s.Reach == DataSource.Groups && s.GroupIds.Count == 0)
        {
            return AuthEndpoints.Problem(400, "groups", "Name the groups that may ask it.");
        }
        if (body.MostRows is { } rows)
        {
            if (rows is < 1 or > 10_000)
            {
                return AuthEndpoints.Problem(400, "mostRows", "An answer brings back 1 to 10,000 rows.");
            }
            s.MostRows = rows;
        }
        if (body.Seconds is { } seconds)
        {
            if (seconds is < 1 or > 120)
            {
                return AuthEndpoints.Problem(400, "seconds", "A query may run 1 to 120 seconds.");
            }
            s.Seconds = seconds;
        }
        return null;
    }

    private static async Task<IResult> AddAsync(DataSourceRequest body, AppDbContext db, DataQuestions questions, Audit audit, CancellationToken ct)
    {
        var (engine, wrong) = EngineOf(body.Kind);
        if (wrong is not null)
        {
            return wrong;
        }
        var files = engine!.Kind == DataSource.Files;
        if (body.Name is null || (!files && string.IsNullOrWhiteSpace(body.Connection)))
        {
            return AuthEndpoints.Problem(400, "source", "A data source needs a name and its connection.");
        }
        var s = new DataSource { Name = "", ConnectionProtected = "", Kind = engine.Kind };
        if (files)
        {
            // Its schema and role, made now; its tables come as files are uploaded.
            try
            {
                body = body with { Connection = await FilesData.EnsureAsync(Owner(db), s.Id, ct), Tables = null };
            }
            catch (DataException ex)
            {
                return AuthEndpoints.Problem(400, "files", ex.Message);
            }
        }
        if (await ApplyAsync(s, body, db, questions, ct) is { } problem)
        {
            if (files)
            {
                await FilesData.DropAsync(Owner(db), s.Id, CancellationToken.None);
            }
            return problem;
        }
        db.DataSources.Add(s);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("data.source.add", s.Name, detail: $"{engine.Title}: {engine.Where(body.Connection ?? "")}");
        return Results.Created($"/api/admin/data-sources/{s.Id}", Brief(s, questions));
    }

    private static async Task<IResult> UpdateAsync(Guid id, DataSourceRequest body, AppDbContext db, DataQuestions questions, Audit audit, CancellationToken ct)
    {
        if (await db.DataSources.SingleOrDefaultAsync(s => s.Id == id, ct) is not { } s)
        {
            return Results.NotFound();
        }
        if (body.Kind is { } kind && !string.Equals(kind.Trim(), s.Kind, StringComparison.OrdinalIgnoreCase))
        {
            return AuthEndpoints.Problem(400, "kind", "A data source stays the kind of database it was made: add another for a different one.");
        }
        if (s.Kind == DataSource.Files && body.Connection is { Length: > 0 })
        {
            return AuthEndpoints.Problem(400, "connection", "Uploaded files live in the stack's own database: there is no connection to give.");
        }
        if (await ApplyAsync(s, body, db, questions, ct) is { } problem)
        {
            return problem;
        }
        s.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("data.source.update", s.Name, detail: string.Join(", ", s.Tables));
        return Results.Ok(Brief(s, questions));
    }

    /// <summary>The database's tables read again (a column added); the tables people may ask stay as chosen.</summary>
    private static async Task<IResult> ReadAgainAsync(Guid id, AppDbContext db, DataQuestions questions, CancellationToken ct)
    {
        if (await db.DataSources.SingleOrDefaultAsync(s => s.Id == id, ct) is not { } s)
        {
            return Results.NotFound();
        }
        try
        {
            var engine = DataEngines.Of(s);
            if (s.Kind == DataSource.Files)
            {
                // Its role made again where it is missing (a restore into another server keeps no roles).
                s.ConnectionProtected = questions.Protect(await FilesData.EnsureAsync(Owner(db), s.Id, ct));
            }
            var (tables, warnings) = await engine.ReadAsync(questions.Unprotect(s), ct);
            s.Schema = System.Text.Json.JsonSerializer.Serialize(tables, System.Text.Json.JsonSerializerOptions.Web);
            s.SchemaReadAt = s.UpdatedAt = DateTimeOffset.UtcNow;
            var gone = s.Tables.Where(t => tables.All(x => x.Name != t)).ToList();
            if (engine.GrantsOnly)
            {
                var added = tables.Select(t => t.Name).Where(t => !s.Tables.Contains(t)).ToList();
                if (added.Count > 0)
                {
                    warnings.Add($"Now open to questions too, as the account may read them: {string.Join(", ", added)}.");
                }
                s.Tables = [.. tables.Select(t => t.Name).Order(StringComparer.Ordinal)];
            }
            else if (gone.Count > 0)
            {
                warnings.Add($"Open to questions but not there any more: {string.Join(", ", gone)}.");
            }
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { source = Brief(s, questions), warnings });
        }
        catch (DataException ex)
        {
            return AuthEndpoints.Problem(400, "connection", ex.Message);
        }
    }

    private static async Task<IResult> RemoveAsync(Guid id, AppDbContext db, Audit audit, CancellationToken ct)
    {
        if (await db.DataSources.SingleOrDefaultAsync(s => s.Id == id, ct) is not { } s)
        {
            return Results.NotFound();
        }
        db.DataSources.Remove(s);
        await db.SaveChangesAsync(ct);
        if (s.Kind == DataSource.Files)
        {
            await FilesData.DropAsync(Owner(db), s.Id, ct);
        }
        await audit.WriteAsync("data.source.remove", s.Name);
        return Results.NoContent();
    }

    /// <summary>The source's tables read again after a change to them: every one open.</summary>
    private static async Task<List<DataTable>> RefreshAsync(DataSource s, AppDbContext db, DataQuestions questions, CancellationToken ct)
    {
        var (tables, _) = await DataEngines.Of(s).ReadAsync(questions.Unprotect(s), ct);
        s.Schema = System.Text.Json.JsonSerializer.Serialize(tables, System.Text.Json.JsonSerializerOptions.Web);
        s.Tables = [.. tables.Select(t => t.Name).Order(StringComparer.Ordinal)];
        s.SchemaReadAt = s.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return tables;
    }

    /// <summary>Data files into a files source: each file, or each sheet of a workbook, a table (one of the same name replaced).</summary>
    private static async Task<IResult> UploadAsync(Guid id, HttpRequest request, AppDbContext db, DataQuestions questions, Audit audit, CancellationToken ct)
    {
        if (await db.DataSources.SingleOrDefaultAsync(s => s.Id == id && s.Kind == DataSource.Files, ct) is not { } s)
        {
            return Results.NotFound();
        }
        if (!request.HasFormContentType)
        {
            return AuthEndpoints.Problem(400, "files", "Send the files as a form (multipart/form-data).");
        }
        var form = await request.ReadFormAsync(ct);
        if (form.Files.Count is 0 or > 50)
        {
            return AuthEndpoints.Problem(400, "files", "Send from 1 to 50 files at a time.");
        }
        var imported = new List<FilesData.Imported>();
        try
        {
            foreach (var file in form.Files)
            {
                var name = Path.GetFileName(file.FileName);
                if (file.Length is 0 or > MostFile)
                {
                    return AuthEndpoints.Problem(400, "files", $"{name} is empty or over {MostFile / (1024 * 1024)} MB.");
                }
                using var ms = new MemoryStream();
                await file.CopyToAsync(ms, ct);
                var sheets = TabularFiles.Read(name, ms.ToArray());
                if (sheets.Count == 0)
                {
                    return AuthEndpoints.Problem(400, "files", $"{name} has no rows with a header.");
                }
                foreach (var sheet in sheets)
                {
                    imported.Add(await FilesData.ImportAsync(Owner(db), s.Id, sheet, sheets.Count == 1 ? name : $"{name}, sheet {sheet.Name}", ct));
                }
            }
            await RefreshAsync(s, db, questions, ct);
        }
        catch (DataException ex)
        {
            return AuthEndpoints.Problem(400, "files", ex.Message);
        }
        await audit.WriteAsync("data.source.files", s.Name, detail: string.Join(", ", imported.Select(i => $"{i.Table} ({i.Rows} rows)")));
        return Results.Ok(new { imported, source = Brief(s, questions) });
    }

    private static async Task<IResult> RemoveTableAsync(Guid id, string table, AppDbContext db, DataQuestions questions, Audit audit, CancellationToken ct)
    {
        if (await db.DataSources.SingleOrDefaultAsync(s => s.Id == id && s.Kind == DataSource.Files, ct) is not { } s)
        {
            return Results.NotFound();
        }
        var name = table.Contains('.', StringComparison.Ordinal) ? table[(table.IndexOf('.', StringComparison.Ordinal) + 1)..] : table;
        if (!s.Tables.Contains($"{FilesData.SchemaOf(s.Id)}.{name}"))
        {
            return Results.NotFound();
        }
        await FilesData.DropTableAsync(Owner(db), s.Id, name, ct);
        await RefreshAsync(s, db, questions, ct);
        await audit.WriteAsync("data.source.files", s.Name, detail: $"removed {name}");
        return Results.NoContent();
    }

    /// <summary>What was asked of the source lately: by whom, the question, the SQL, how it went.</summary>
    private static async Task<IResult> QueriesAsync(Guid id, AppDbContext db, CancellationToken ct, int take = 100)
    {
        var rows = await (from q in db.DataQueries.AsNoTracking()
                          join u in db.Users on q.UserId equals u.Id
                          where q.SourceId == id
                          orderby q.At descending
                          select new { q.Id, q.At, q.Via, q.Question, q.Sql, q.Rows, q.Milliseconds, q.Error, person = u.DisplayName == "" ? u.UserName : u.DisplayName })
            .Take(Math.Clamp(take, 1, 500)).ToListAsync(ct);
        return Results.Ok(rows);
    }
}
