using Llm.Api.Access;
using Llm.Api.Endpoints;
using Llm.Api.Identity;
using Llm.Core.Data;
using Llm.Core.Insights;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Llm.Api.Insights;

/// <summary>A data source to add or change: a missing value is kept; a connection is given again only to change it.</summary>
/// <param name="Reach">company or groups (<paramref name="GroupIds"/>).</param>
public sealed record DataSourceRequest(string? Name = null, string? Description = null, string? Connection = null, List<string>? Tables = null, string? Notes = null,
    string? Reach = null, List<Guid>? GroupIds = null, int? MostRows = null, int? Seconds = null);

/// <summary>A connection to try before it is saved (or a saved source's, read again).</summary>
public sealed record DataProbe(string? Connection, Guid? Id = null);

/// <summary>
/// Admin → Data sources: the databases people may ask (PostgreSQL), which of their tables, and who may ask them; and
/// what was asked of them. The connection is kept protected, and never shown again.
/// </summary>
public static class DataSourceEndpoints
{
    public static void MapDataSources(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin/data-sources").RequirePermissions(Core.Access.Permissions.DataManage, Core.Access.Permissions.DataManage);
        g.MapGet("", ListAsync);
        g.MapPost("/probe", ProbeAsync);
        g.MapPost("", AddAsync);
        g.MapPatch("/{id:guid}", UpdateAsync);
        g.MapPost("/{id:guid}/schema", ReadAgainAsync);
        g.MapDelete("/{id:guid}", RemoveAsync);
        g.MapGet("/{id:guid}/queries", QueriesAsync);
    }

    /// <summary>Who the connection signs in as, and where: never its password.</summary>
    private static string Where(string connection)
    {
        try
        {
            var b = new NpgsqlConnectionStringBuilder(connection);
            return $"{b.Username}@{b.Host}{(b.Port is 5432 ? "" : $":{b.Port}")}/{b.Database}";
        }
        catch (ArgumentException)
        {
            return "";
        }
    }

    private static object Brief(DataSource s, DataQuestions questions) => new
    {
        s.Id, s.Name, s.Description, s.Kind, s.Tables, s.Notes, s.Reach, s.GroupIds, s.MostRows, s.Seconds, s.SchemaReadAt, s.CreatedAt, s.UpdatedAt,
        where = Where(questions.Unprotect(s)),
        schema = PostgresData.Parse(s.Schema),
    };

    private static async Task<IResult> ListAsync(AppDbContext db, DataQuestions questions, CancellationToken ct)
    {
        var sources = await db.DataSources.AsNoTracking().OrderBy(s => s.Name).ToListAsync(ct);
        return Results.Ok(sources.Select(s => Brief(s, questions)));
    }

    /// <summary>The tables a connection's role sees, before it is saved; with a saved source's connection when none is given.</summary>
    private static async Task<IResult> ProbeAsync(DataProbe body, AppDbContext db, DataQuestions questions, CancellationToken ct)
    {
        var connection = body.Connection;
        if (string.IsNullOrWhiteSpace(connection) && body.Id is { } id && await db.DataSources.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, ct) is { } saved)
        {
            connection = questions.Unprotect(saved);
        }
        if (PostgresData.CheckConnection(connection) is { } bad)
        {
            return AuthEndpoints.Problem(400, "connection", bad);
        }
        try
        {
            var (tables, warnings) = await PostgresData.ReadAsync(connection!, ct);
            return Results.Ok(new { tables, warnings, where = Where(connection!) });
        }
        catch (DataException ex)
        {
            return AuthEndpoints.Problem(400, "connection", ex.Message);
        }
    }

    /// <summary>Checks and applies a request; a new connection is read now (a wrong one is found here, not when someone asks).</summary>
    private static async Task<IResult?> ApplyAsync(DataSource s, DataSourceRequest body, AppDbContext db, DataQuestions questions, CancellationToken ct)
    {
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
            if (PostgresData.CheckConnection(connection) is { } bad)
            {
                return AuthEndpoints.Problem(400, "connection", bad);
            }
            try
            {
                var (tables, _) = await PostgresData.ReadAsync(connection, ct);
                s.Schema = System.Text.Json.JsonSerializer.Serialize(tables, System.Text.Json.JsonSerializerOptions.Web);
                s.SchemaReadAt = DateTimeOffset.UtcNow;
            }
            catch (DataException ex)
            {
                return AuthEndpoints.Problem(400, "connection", ex.Message);
            }
            s.ConnectionProtected = questions.Protect(connection);
        }
        if (body.Tables is { } wanted)
        {
            var known = PostgresData.Parse(s.Schema).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
            var unknown = wanted.Where(t => !known.Contains(t)).ToList();
            if (unknown.Count > 0)
            {
                return AuthEndpoints.Problem(400, "tables", $"The database has no {string.Join(", ", unknown.Take(5))} its role may read.");
            }
            s.Tables = [.. wanted.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
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
        if (body.Name is null || string.IsNullOrWhiteSpace(body.Connection))
        {
            return AuthEndpoints.Problem(400, "source", "A data source needs a name and its connection.");
        }
        var s = new DataSource { Name = "", ConnectionProtected = "" };
        if (await ApplyAsync(s, body, db, questions, ct) is { } problem)
        {
            return problem;
        }
        db.DataSources.Add(s);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("data.source.add", s.Name, detail: Where(body.Connection));
        return Results.Created($"/api/admin/data-sources/{s.Id}", Brief(s, questions));
    }

    private static async Task<IResult> UpdateAsync(Guid id, DataSourceRequest body, AppDbContext db, DataQuestions questions, Audit audit, CancellationToken ct)
    {
        if (await db.DataSources.SingleOrDefaultAsync(s => s.Id == id, ct) is not { } s)
        {
            return Results.NotFound();
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
            var (tables, warnings) = await PostgresData.ReadAsync(questions.Unprotect(s), ct);
            s.Schema = System.Text.Json.JsonSerializer.Serialize(tables, System.Text.Json.JsonSerializerOptions.Web);
            s.SchemaReadAt = s.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            var gone = s.Tables.Where(t => tables.All(x => x.Name != t)).ToList();
            if (gone.Count > 0)
            {
                warnings.Add($"Open to questions but not there any more: {string.Join(", ", gone)}.");
            }
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
        await audit.WriteAsync("data.source.remove", s.Name);
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
