using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Llm.Api.Access;
using Llm.Api.Endpoints;
using Llm.Api.Identity;
using Llm.Core.Data;
using Llm.Core.Identity;
using Llm.Core.Insights;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Insights;

public sealed record DataAsk(Guid SourceId, string? Question, EarlierQuestion? Earlier = null);

public sealed record DataRun(Guid SourceId, string? Sql, string? Question = null);

/// <param name="Reach">private, groups (<paramref name="GroupIds"/>) or company.</param>
public sealed record BoardRequest(string? Name = null, string? Description = null, string? Reach = null, List<Guid>? GroupIds = null);

public sealed record PinRequest(Guid? SourceId = null, string? Title = null, string? Question = null, string? Sql = null, JsonObject? Chart = null, string? Refresh = null,
    int? Position = null);

/// <summary>
/// Ask your data: the databases a person may ask, a question answered with its SQL (shown, explained, run read only), SQL
/// they changed run again, and boards of the charts they pinned, refreshed on their schedules and shared like apps.
/// </summary>
public static class DataEndpoints
{
    public const int MostPins = 24;
    public const int MostBoards = 50;

    public static void MapData(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/data").RequireFeature("data").PersonOnly().RequirePermission(Core.Access.Permissions.Data);
        g.MapGet("/sources", SourcesAsync);
        g.MapPost("/ask", AskAsync);
        g.MapPost("/run", RunAsync);
        g.MapGet("/boards", BoardsAsync);
        g.MapPost("/boards", AddBoardAsync);
        g.MapGet("/boards/{id:guid}", BoardAsync);
        g.MapPut("/boards/{id:guid}", ChangeBoardAsync);
        g.MapDelete("/boards/{id:guid}", RemoveBoardAsync);
        g.MapPost("/boards/{id:guid}/pins", PinAsync);
        g.MapPut("/pins/{id:guid}", ChangePinAsync);
        g.MapDelete("/pins/{id:guid}", UnpinAsync);
        g.MapPost("/pins/{id:guid}/refresh", RefreshAsync);
    }

    private static async Task<AppUser> Me(ClaimsPrincipal p, UserManager<AppUser> users) => (await users.GetUserAsync(p))!;

    private static object Result(DataResult r) => new { r.Columns, r.Rows, r.Truncated, r.Milliseconds };

    private static object Answer(DataAnswer a) => new
    {
        a.Question, a.Sql, a.Explanation, a.Title, a.Chart, result = a.Result is { } r ? Result(r) : null, a.Error, a.Model,
    };

    /// <summary>The databases this person may ask, with the tables open to questions and their columns.</summary>
    private static async Task<IResult> SourcesAsync(ClaimsPrincipal p, UserManager<AppUser> users, DataQuestions questions, CancellationToken ct)
    {
        var me = await Me(p, users);
        var sources = await questions.SourcesForAsync(me, ct);
        return Results.Ok(sources.Select(s => new { s.Id, s.Name, s.Description, tables = DataQuestions.Open(s) }));
    }

    private static async Task<IResult> AskAsync(DataAsk body, ClaimsPrincipal p, UserManager<AppUser> users, DataQuestions questions, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await questions.SourceForAsync(body.SourceId, me, ct) is not { } source)
        {
            return Results.NotFound();
        }
        var question = body.Question?.Trim() ?? "";
        if (question.Length is 0 or > 2000)
        {
            return AuthEndpoints.Problem(400, "question", "Ask something, up to 2,000 characters.");
        }
        if (source.Tables.Count == 0)
        {
            return AuthEndpoints.Problem(409, "tables", $"No table of {source.Name} is open to questions yet: an admin chooses them.");
        }
        var (answer, problem) = await questions.AskAsync(source, me, question, body.Earlier, "ask", ct);
        return problem ?? Results.Ok(Answer(answer!));
    }

    /// <summary>SQL as the person wrote or changed it, run as a question's would be.</summary>
    private static async Task<IResult> RunAsync(DataRun body, ClaimsPrincipal p, UserManager<AppUser> users, DataQuestions questions, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await questions.SourceForAsync(body.SourceId, me, ct) is not { } source)
        {
            return Results.NotFound();
        }
        try
        {
            return Results.Ok(Result(await questions.RunAsync(source, body.Sql?.Trim() ?? "", me, "run", body.Question, ct)));
        }
        catch (DataException ex)
        {
            return AuthEndpoints.Problem(400, "sql", ex.Message);
        }
    }

    /// <summary>Whether this person may see the board: theirs, everyone's, or one of their groups'.</summary>
    private static async Task<DataBoard?> VisibleAsync(Guid id, AppUser me, AppDbContext db, AccessService access, CancellationToken ct)
    {
        if (await db.DataBoards.SingleOrDefaultAsync(b => b.Id == id, ct) is not { } b)
        {
            return null;
        }
        return b.OwnerId == me.Id || b.Reach == DataBoard.Company || (b.Reach == DataBoard.Groups && (await access.MembershipAsync(me, ct)).Groups.Any(b.GroupIds.Contains))
            ? b
            : null;
    }

    private static object Brief(DataBoard b, string? owner, Guid me, int pins) => new
    {
        b.Id, b.Name, b.Description, b.Reach, b.GroupIds, b.CreatedAt, b.UpdatedAt, owner, mine = b.OwnerId == me, pins,
    };

    private static object Pin(DataPin x, string? source) => new
    {
        x.Id, x.BoardId, x.SourceId, source, x.Title, x.Question, x.Sql, chart = JsonNode.Parse(x.Chart), x.Refresh, x.Position,
        result = x.Result is { } r ? JsonNode.Parse(r) : null, x.RanAt, x.Error,
    };

    private static async Task<IResult> BoardsAsync(ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, AccessService access, CancellationToken ct)
    {
        var me = await Me(p, users);
        var groups = (await access.MembershipAsync(me, ct)).Groups.ToList();
        var boards = await db.DataBoards.AsNoTracking()
            .Where(b => b.OwnerId == me.Id || b.Reach == DataBoard.Company || (b.Reach == DataBoard.Groups && b.GroupIds.Any(g => groups.Contains(g))))
            .OrderByDescending(b => b.UpdatedAt).Take(200).ToListAsync(ct);
        var ids = boards.Select(b => b.Id).ToList();
        var counts = await db.DataPins.AsNoTracking().Where(x => ids.Contains(x.BoardId)).GroupBy(x => x.BoardId).Select(g => new { g.Key, n = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.n, ct);
        var owners = boards.Select(b => b.OwnerId).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => owners.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName == "" ? u.UserName! : u.DisplayName, ct);
        return Results.Ok(boards.Select(b => Brief(b, names.GetValueOrDefault(b.OwnerId), me.Id, counts.GetValueOrDefault(b.Id))));
    }

    private static async Task<IResult?> ApplyAsync(DataBoard b, BoardRequest body, AppUser me, AppDbContext db, AccessService access, CancellationToken ct)
    {
        if (body.Name is { } name)
        {
            name = name.Trim();
            if (name.Length is 0 or > 100)
            {
                return AuthEndpoints.Problem(400, "name", "A board needs a name of up to 100 characters.");
            }
            b.Name = name;
        }
        if (body.Description is { } description)
        {
            b.Description = description.Trim() is var d && d.Length > 500 ? d[..500] : d;
        }
        if (body.Reach is { } reach)
        {
            if (reach is not (DataBoard.Private or DataBoard.Groups or DataBoard.Company))
            {
                return AuthEndpoints.Problem(400, "reach", "Who may see it: private, groups or company.");
            }
            b.Reach = reach;
        }
        if (body.GroupIds is { } groupIds)
        {
            var known = await db.Groups.AsNoTracking().Where(g => groupIds.Contains(g.Id)).Select(g => g.Id).ToListAsync(ct);
            if (known.Count != groupIds.Distinct().Count())
            {
                return AuthEndpoints.Problem(400, "groups", "A group of those is not here.");
            }
            // Shared with groups the person is in, unless they may share with any (as assistants and apps are).
            var mine = (await access.MembershipAsync(me, ct)).Groups;
            if (!await access.MayAsync(me, Core.Access.Permissions.AssistantsShare, ct) && known.Any(g => !mine.Contains(g)))
            {
                return AuthEndpoints.Problem(403, "groups", "Share it with groups you are in.");
            }
            b.GroupIds = known;
        }
        if (b.Reach == DataBoard.Groups && b.GroupIds.Count == 0)
        {
            return AuthEndpoints.Problem(400, "groups", "Name the groups that may see it.");
        }
        return null;
    }

    private static async Task<IResult> AddBoardAsync(BoardRequest body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, AccessService access, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await db.DataBoards.CountAsync(b => b.OwnerId == me.Id, ct) >= MostBoards)
        {
            return AuthEndpoints.Problem(409, "boards", $"You have {MostBoards} boards, the most: remove one first.");
        }
        var b = new DataBoard { OwnerId = me.Id, Name = "" };
        if (body.Name is null)
        {
            return AuthEndpoints.Problem(400, "name", "A board needs a name.");
        }
        if (await ApplyAsync(b, body, me, db, access, ct) is { } problem)
        {
            return problem;
        }
        db.DataBoards.Add(b);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/data/boards/{b.Id}", Brief(b, null, me.Id, 0));
    }

    private static async Task<IResult> BoardAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, AccessService access, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await VisibleAsync(id, me, db, access, ct) is not { } b)
        {
            return Results.NotFound();
        }
        var pins = await db.DataPins.AsNoTracking().Where(x => x.BoardId == id).OrderBy(x => x.Position).ThenBy(x => x.CreatedAt).ToListAsync(ct);
        var sourceIds = pins.Select(x => x.SourceId).Distinct().ToList();
        var sources = await db.DataSources.AsNoTracking().Where(s => sourceIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        var owner = await db.Users.AsNoTracking().Where(u => u.Id == b.OwnerId).Select(u => u.DisplayName == "" ? u.UserName : u.DisplayName).SingleOrDefaultAsync(ct);
        return Results.Ok(new { board = Brief(b, owner, me.Id, pins.Count), pins = pins.Select(x => Pin(x, sources.GetValueOrDefault(x.SourceId))) });
    }

    private static async Task<IResult> ChangeBoardAsync(Guid id, BoardRequest body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, AccessService access,
        CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await db.DataBoards.SingleOrDefaultAsync(b => b.Id == id && b.OwnerId == me.Id, ct) is not { } b)
        {
            return Results.NotFound();
        }
        if (await ApplyAsync(b, body, me, db, access, ct) is { } problem)
        {
            return problem;
        }
        b.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Results.Ok(Brief(b, null, me.Id, await db.DataPins.CountAsync(x => x.BoardId == id, ct)));
    }

    private static async Task<IResult> RemoveBoardAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await db.DataBoards.SingleOrDefaultAsync(b => b.Id == id && b.OwnerId == me.Id, ct) is not { } b)
        {
            return Results.NotFound();
        }
        db.DataBoards.Remove(b);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static string Cut(string s, int most) => s.Length > most ? s[..most] : s;

    private static string? CheckRefresh(string? refresh) =>
        refresh is null or DataPin.Off or DataPin.Hourly or DataPin.Daily ? null : "Refresh it off, hourly or daily.";

    /// <summary>A chart pinned to one of the person's boards: its SQL run now (so it works), then on its schedule.</summary>
    private static async Task<IResult> PinAsync(Guid id, PinRequest body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, DataQuestions questions,
        CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await db.DataBoards.SingleOrDefaultAsync(b => b.Id == id && b.OwnerId == me.Id, ct) is not { } board)
        {
            return Results.NotFound();
        }
        if (body.SourceId is not { } sourceId || await questions.SourceForAsync(sourceId, me, ct) is not { } source)
        {
            return AuthEndpoints.Problem(400, "source", "Pin an answer from a database you may ask.");
        }
        if (CheckRefresh(body.Refresh) is { } bad)
        {
            return AuthEndpoints.Problem(400, "refresh", bad);
        }
        var count = await db.DataPins.CountAsync(x => x.BoardId == id, ct);
        if (count >= MostPins)
        {
            return AuthEndpoints.Problem(409, "pins", $"A board holds {MostPins} charts at most.");
        }
        var question = Cut((body.Question ?? "").Trim(), 2000);
        var title = Cut((body.Title ?? "").Trim() is { Length: > 0 } t ? t : question.Length > 0 ? question : "A chart", 200);
        var pin = new DataPin
        {
            BoardId = id, SourceId = sourceId, Title = title, Question = question,
            Sql = body.Sql?.Trim() ?? "", Chart = (body.Chart ?? []).ToJsonString(), Refresh = body.Refresh ?? DataPin.Daily, Position = count,
        };
        try
        {
            var result = await questions.RunAsync(source, pin.Sql, me, "board", pin.Question, ct);
            pin.Result = JsonSerializer.Serialize(Result(result), JsonSerializerOptions.Web);
            pin.RanAt = DateTimeOffset.UtcNow;
        }
        catch (DataException ex)
        {
            return AuthEndpoints.Problem(400, "sql", ex.Message);
        }
        db.DataPins.Add(pin);
        board.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/data/pins/{pin.Id}", Pin(pin, source.Name));
    }

    private static async Task<(DataPin? Pin, DataBoard? Board)> OwnedPinAsync(Guid id, AppUser me, AppDbContext db, CancellationToken ct)
    {
        var pin = await db.DataPins.SingleOrDefaultAsync(x => x.Id == id, ct);
        var board = pin is null ? null : await db.DataBoards.SingleOrDefaultAsync(b => b.Id == pin.BoardId && b.OwnerId == me.Id, ct);
        return board is null ? (null, null) : (pin, board);
    }

    private static async Task<IResult> ChangePinAsync(Guid id, PinRequest body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await OwnedPinAsync(id, me, db, ct) is not ({ } pin, { } board))
        {
            return Results.NotFound();
        }
        if (CheckRefresh(body.Refresh) is { } bad)
        {
            return AuthEndpoints.Problem(400, "refresh", bad);
        }
        if (body.Title is { } title)
        {
            title = title.Trim();
            if (title.Length is 0 or > 200)
            {
                return AuthEndpoints.Problem(400, "title", "A chart's title is 1 to 200 characters.");
            }
            pin.Title = title;
        }
        if (body.Chart is { } chart)
        {
            pin.Chart = chart.ToJsonString();
        }
        if (body.Refresh is { } refresh)
        {
            pin.Refresh = refresh;
        }
        if (body.Position is { } position)
        {
            pin.Position = Math.Clamp(position, 0, MostPins);
        }
        board.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Results.Ok(Pin(pin, await db.DataSources.Where(s => s.Id == pin.SourceId).Select(s => s.Name).SingleOrDefaultAsync(ct)));
    }

    private static async Task<IResult> UnpinAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await OwnedPinAsync(id, me, db, ct) is not ({ } pin, { } board))
        {
            return Results.NotFound();
        }
        db.DataPins.Remove(pin);
        board.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    /// <summary>Its SQL run again now, by its board's owner.</summary>
    private static async Task<IResult> RefreshAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, DataQuestions questions, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await OwnedPinAsync(id, me, db, ct) is not ({ } pin, _))
        {
            return Results.NotFound();
        }
        await RunPinAsync(pin, me, db, questions, ct);
        return Results.Ok(Pin(pin, await db.DataSources.Where(s => s.Id == pin.SourceId).Select(s => s.Name).SingleOrDefaultAsync(ct)));
    }

    /// <summary>Runs a pin's SQL as its board's owner (when they may still ask its database), keeping its result or why not.</summary>
    public static async Task RunPinAsync(DataPin pin, AppUser owner, AppDbContext db, DataQuestions questions, CancellationToken ct)
    {
        if (await questions.SourceForAsync(pin.SourceId, owner, ct) is not { } source)
        {
            pin.Error = "Its board's owner may no longer ask this database.";
        }
        else
        {
            try
            {
                var result = await questions.RunAsync(source, pin.Sql, owner, "board", pin.Question, ct);
                pin.Result = JsonSerializer.Serialize(Result(result), JsonSerializerOptions.Web);
                pin.Error = null;
            }
            catch (DataException ex)
            {
                pin.Error = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message;
            }
        }
        pin.RanAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }
}
