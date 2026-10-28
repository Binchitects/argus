using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Nodes;
using Llm.Api.Access;
using Llm.Api.ArenaMcp;
using Llm.Api.Chat.Tools;
using Llm.Api.Endpoints;
using Llm.Api.Identity;
using Llm.Core.Chat;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Chat;

/// <summary>A checklist as a person writes it: a name, a line about it, its items (one a line).</summary>
public sealed record ChecklistRequest(string? Name, string? Description, List<string>? Items);

/// <summary>Turns a checklist on in a chat (by its id), or ticks one item (1 is the first).</summary>
public sealed record ChecklistOn(Guid ChecklistId);

/// <param name="By">Code Arena: person when the person ticked it by hand (/checklist tick N); else the model.</param>
public sealed record ChecklistTick(int Item, bool Done = true, string? Note = null, string? By = null);

public sealed class ChecklistException(string message) : Exception(message);

/// <summary>Checklists and their runs in chats: what the pages, Code Arena and the checklist tool share.</summary>
public static class ChecklistBook
{
    /// <summary>The checklist as written, or why not: a name, 1 to 50 items of up to 500 characters (empty lines left out).</summary>
    public static void Apply(Checklist list, ChecklistRequest body)
    {
        var name = (body.Name ?? list.Name).Trim();
        if (name.Length is 0 or > 100)
        {
            throw new ChecklistException("A checklist needs a name of up to 100 characters.");
        }
        List<string> items = body.Items is null ? list.Items : [.. body.Items.Select(i => i.Trim()).Where(i => i.Length > 0)];
        if (items.Count is 0 or > Checklist.MostItems || items.Any(i => i.Length > Checklist.MostItemChars))
        {
            throw new ChecklistException($"A checklist has 1 to {Checklist.MostItems} items, each up to {Checklist.MostItemChars} characters.");
        }
        list.Name = name;
        list.Description = body.Description is null ? list.Description : body.Description.Trim() is { Length: > 0 } d ? (d.Length > 500 ? d[..500] : d) : null;
        list.Items = items;
        list.UpdatedAt = DateTimeOffset.UtcNow;
    }

    public static object Shape(Checklist c) => new { c.Id, c.Name, c.Description, c.Items, c.CreatedAt, c.UpdatedAt };

    public static object Shape(ChecklistRun r) => new
    {
        r.Id, r.ChecklistId, r.Name, r.StartedAt, r.UpdatedAt,
        items = r.Items.Select((i, n) => new { number = n + 1, i.Text, i.Done, i.Note, i.By, i.DoneAt }),
        done = r.Items.Count(i => i.Done), total = r.Items.Count,
    };

    /// <summary>Turns the person's checklist on in their chat, in place of the one on before: its items, none ticked.</summary>
    public static async Task<ChecklistRun> StartAsync(AppDbContext db, Guid chat, Guid person, Guid checklistId, CancellationToken ct)
    {
        var list = await db.Checklists.AsNoTracking().SingleOrDefaultAsync(c => c.Id == checklistId && c.UserId == person, ct)
            ?? throw new ChecklistException("That checklist is not yours, or not there any more.");
        await db.ChecklistRuns.Where(r => r.ConversationId == chat).ExecuteDeleteAsync(ct);
        var run = new ChecklistRun { ConversationId = chat, ChecklistId = list.Id, Name = list.Name, Items = [.. list.Items.Select(i => new ChecklistRunItem { Text = i })] };
        db.ChecklistRuns.Add(run);
        await db.SaveChangesAsync(ct);
        return run;
    }

    /// <summary>Ticks an item (or takes the tick back), with how it was done; by the model or the person.</summary>
    public static async Task<ChecklistRun> TickAsync(AppDbContext db, Guid chat, ChecklistTick tick, string by, CancellationToken ct)
    {
        var run = await db.ChecklistRuns.SingleOrDefaultAsync(r => r.ConversationId == chat, ct) ?? throw new ChecklistException("No checklist is on in this chat.");
        if (tick.Item < 1 || tick.Item > run.Items.Count)
        {
            throw new ChecklistException($"The checklist has items 1 to {run.Items.Count}.");
        }
        var item = run.Items[tick.Item - 1];
        item.Done = tick.Done;
        item.Note = tick.Note?.Trim() is { Length: > 0 } note ? (note.Length > 1000 ? note[..1000] : note) : tick.Done ? item.Note : null;
        item.By = tick.Done ? by : null;
        item.DoneAt = tick.Done ? DateTimeOffset.UtcNow : null;
        run.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return run;
    }

    /// <summary>The checklist as the model reads it: each item numbered, ticked or not, with its note.</summary>
    public static string Text(ChecklistRun run)
    {
        var text = new StringBuilder(string.Create(CultureInfo.InvariantCulture, $"Checklist \"{run.Name}\" ({run.Items.Count(i => i.Done)} of {run.Items.Count} done):\n"));
        for (var n = 0; n < run.Items.Count; n++)
        {
            var i = run.Items[n];
            text.Append(CultureInfo.InvariantCulture, $"{n + 1}. [{(i.Done ? "x" : " ")}] {i.Text}{(i.Note is { } note ? $" ({note})" : "")}\n");
        }
        return text.ToString();
    }
}

/// <summary>
/// The checklist on in a chat, for its answers: the items to go through, and checklist_check to tick each as it is done or
/// checked. Started for every answer of a chat that has one on; never chosen by hand.
/// </summary>
public sealed class ChecklistTool(AppDbContext db) : IChatTool
{
    public const string ToolId = "checklist", CheckFunction = "checklist_check", ShowFunction = "checklist_show";

    public string Id => ToolId;
    public string Title => "Checklist";
    public string Description => "The checklist the person turned on in this chat: go through it, and tick each item as it is done.";
    public string Icon => "list-checks";

    public Task<string?> UnavailableAsync(CancellationToken ct) => Task.FromResult<string?>(null);

    public async Task<IToolRun> StartAsync(ToolContext context, CancellationToken ct)
    {
        var run = await db.ChecklistRuns.AsNoTracking().SingleOrDefaultAsync(r => r.ConversationId == context.Conversation.Id, ct);
        var instructions = run is null ? null :
            "The person turned on a checklist in this chat: do the work it asks of you as you answer, and before you say a piece of work is done, go " +
            "through every item that applies: do it or check it (with your tools when you have them), then tick it with checklist_check and say in " +
            "a few words how (\"tests pass: 42 of 42\"); an item that does not apply is ticked with why. Do not tick what you did not do or check. " +
            "End by saying which items are left, if any.\n" + ChecklistBook.Text(run);
        return new LocalRun(
            [
                Schema.Function(CheckFunction, "Ticks an item of this chat's checklist (or takes the tick back), with how it was done or checked.",
                    new JsonObject
                    {
                        ["item"] = new JsonObject { ["type"] = "integer", ["description"] = "The item's number (1 is the first)." },
                        ["done"] = new JsonObject { ["type"] = "boolean", ["description"] = "false takes the tick back (default true)." },
                        ["note"] = Schema.Text("How it was done or checked, or why it does not apply: short, concrete."),
                    }, "item", "note"),
                Schema.Function(ShowFunction, "The checklist as it is now (the person may have ticked items too).", new JsonObject()),
            ],
            instructions,
            async (function, args, token) =>
            {
                try
                {
                    switch (function)
                    {
                        case CheckFunction:
                            var item = args["item"] is JsonValue v && v.TryGetValue<int>(out var number) ? number : 0;
                            var done = args["done"] is not JsonValue d || !d.TryGetValue<bool>(out var on) || on;
                            var ticked = await ChecklistBook.TickAsync(db, context.Conversation.Id, new ChecklistTick(item, done, args["note"]?.GetValue<string>()), "model", token);
                            return new ToolResult(ChecklistBook.Text(ticked));
                        case ShowFunction:
                            return await db.ChecklistRuns.AsNoTracking().SingleOrDefaultAsync(r => r.ConversationId == context.Conversation.Id, token) is { } now
                                ? new ToolResult(ChecklistBook.Text(now))
                                : new ToolResult("No checklist is on in this chat any more.", IsError: true);
                        default:
                            return new ToolResult($"There is no function {function}.", IsError: true);
                    }
                }
                catch (ChecklistException ex)
                {
                    return new ToolResult(ex.Message, IsError: true);
                }
            });
    }
}

/// <summary>
/// Workspace → Checklists: a person's checklists; a chat's checklist (turned on, ticked by hand, turned off); and the same
/// for Code Arena with the person's key, so its sessions use the same checklists and show the same ticks.
/// </summary>
public static class ChecklistEndpoints
{
    public static void MapChecklists(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/checklists").PersonOnly();
        g.MapGet("", ListAsync);
        g.MapPost("", CreateAsync);
        g.MapPut("/{id:guid}", UpdateAsync);
        g.MapDelete("/{id:guid}", DeleteAsync);
        var chat = app.MapGroup("/api/chat/conversations/{id:guid}/checklist").PersonOnly();
        chat.MapGet("", RunAsync);
        chat.MapPut("", StartAsync);
        chat.MapPost("/tick", TickAsync);
        chat.MapDelete("", StopAsync);
        var arena = app.MapGroup("/api/code-arena").AllowAnonymous("Code Arena: the person's API key is checked on each call.");
        arena.MapGet("/checklists", ArenaListAsync);
        arena.MapGet("/chats/{id:guid}/checklist", ArenaRunAsync);
        arena.MapPut("/chats/{id:guid}/checklist", ArenaStartAsync);
        arena.MapPost("/chats/{id:guid}/checklist/tick", ArenaTickAsync);
        arena.MapDelete("/chats/{id:guid}/checklist", ArenaStopAsync);
    }

    private static async Task<AppUser> Me(ClaimsPrincipal p, UserManager<AppUser> users) => (await users.GetUserAsync(p))!;

    private static IResult Bad(ChecklistException ex) => AuthEndpoints.Problem(400, "checklist", ex.Message);

    private static async Task<object> MineAsync(AppDbContext db, Guid person, CancellationToken ct) =>
        (await db.Checklists.AsNoTracking().Where(c => c.UserId == person).OrderBy(c => c.Name).ToListAsync(ct)).Select(ChecklistBook.Shape);

    private static async Task<IResult> ListAsync(ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, CancellationToken ct) =>
        Results.Ok(await MineAsync(db, (await Me(p, users)).Id, ct));

    private static async Task<IResult> CreateAsync(ChecklistRequest body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await db.Checklists.CountAsync(c => c.UserId == me.Id, ct) >= Checklist.MostPerPerson)
        {
            return AuthEndpoints.Problem(409, "checklist", $"You have {Checklist.MostPerPerson} checklists: delete one you no longer use.");
        }
        var list = new Checklist { UserId = me.Id, Name = "" };
        try
        {
            ChecklistBook.Apply(list, body);
        }
        catch (ChecklistException ex)
        {
            return Bad(ex);
        }
        db.Checklists.Add(list);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/checklists/{list.Id}", ChecklistBook.Shape(list));
    }

    private static async Task<IResult> UpdateAsync(Guid id, ChecklistRequest body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await db.Checklists.SingleOrDefaultAsync(c => c.Id == id && c.UserId == me.Id, ct) is not { } list)
        {
            return Results.NotFound();
        }
        try
        {
            ChecklistBook.Apply(list, body);
        }
        catch (ChecklistException ex)
        {
            return Bad(ex);
        }
        await db.SaveChangesAsync(ct);
        return Results.Ok(ChecklistBook.Shape(list));
    }

    private static async Task<IResult> DeleteAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, CancellationToken ct)
    {
        var me = await Me(p, users);
        return await db.Checklists.Where(c => c.Id == id && c.UserId == me.Id).ExecuteDeleteAsync(ct) == 0 ? Results.NotFound() : Results.NoContent();
    }

    private static Task<bool> OwnsAsync(AppDbContext db, Guid chat, Guid person, CancellationToken ct) =>
        db.Conversations.AnyAsync(c => c.Id == chat && c.UserId == person && c.DeletedAt == null, ct);

    private static async Task<IResult> RunOfAsync(AppDbContext db, Guid chat, CancellationToken ct) =>
        await db.ChecklistRuns.AsNoTracking().SingleOrDefaultAsync(r => r.ConversationId == chat, ct) is { } run ? Results.Ok(ChecklistBook.Shape(run)) : Results.NoContent();

    private static async Task<IResult> StartOnAsync(AppDbContext db, Guid chat, Guid person, ChecklistOn body, CancellationToken ct)
    {
        try
        {
            return Results.Ok(ChecklistBook.Shape(await ChecklistBook.StartAsync(db, chat, person, body.ChecklistId, ct)));
        }
        catch (ChecklistException ex)
        {
            return Bad(ex);
        }
    }

    private static async Task<IResult> TickOnAsync(AppDbContext db, Guid chat, ChecklistTick body, string by, CancellationToken ct)
    {
        try
        {
            return Results.Ok(ChecklistBook.Shape(await ChecklistBook.TickAsync(db, chat, body, by, ct)));
        }
        catch (ChecklistException ex)
        {
            return Bad(ex);
        }
    }

    private static async Task<IResult> RunAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, CancellationToken ct) =>
        await OwnsAsync(db, id, (await Me(p, users)).Id, ct) ? await RunOfAsync(db, id, ct) : Results.NotFound();

    private static async Task<IResult> StartAsync(Guid id, ChecklistOn body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, CancellationToken ct)
    {
        var me = await Me(p, users);
        return await OwnsAsync(db, id, me.Id, ct) ? await StartOnAsync(db, id, me.Id, body, ct) : Results.NotFound();
    }

    private static async Task<IResult> TickAsync(Guid id, ChecklistTick body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, CancellationToken ct) =>
        await OwnsAsync(db, id, (await Me(p, users)).Id, ct) ? await TickOnAsync(db, id, body, "person", ct) : Results.NotFound();

    private static async Task<IResult> StopAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, CancellationToken ct)
    {
        if (!await OwnsAsync(db, id, (await Me(p, users)).Id, ct))
        {
            return Results.NotFound();
        }
        await db.ChecklistRuns.Where(r => r.ConversationId == id).ExecuteDeleteAsync(ct);
        return Results.NoContent();
    }

    // Code Arena: the same, with the person's key; a chat is its session's (synced to Arena).

    private static async Task<IResult> ArenaListAsync(HttpContext http, McpPeople people, AppDbContext db)
    {
        var (me, refusal) = await CodeArenaSync.CallerAsync(http, people);
        return me is null ? refusal! : Results.Ok(await MineAsync(db, me.Id, http.RequestAborted));
    }

    private static async Task<(AppUser? Me, IResult? Refusal)> ArenaChatAsync(Guid id, HttpContext http, McpPeople people, AppDbContext db)
    {
        var (me, refusal) = await CodeArenaSync.CallerAsync(http, people);
        if (me is null)
        {
            return (null, refusal);
        }
        return await OwnsAsync(db, id, me.Id, http.RequestAborted) ? (me, null) : (null, AuthEndpoints.Problem(404, "gone", "That chat is not there."));
    }

    private static async Task<IResult> ArenaRunAsync(Guid id, HttpContext http, McpPeople people, AppDbContext db)
    {
        var (me, refusal) = await ArenaChatAsync(id, http, people, db);
        return me is null ? refusal! : await RunOfAsync(db, id, http.RequestAborted);
    }

    private static async Task<IResult> ArenaStartAsync(Guid id, ChecklistOn body, HttpContext http, McpPeople people, AppDbContext db)
    {
        var (me, refusal) = await ArenaChatAsync(id, http, people, db);
        return me is null ? refusal! : await StartOnAsync(db, id, me.Id, body, http.RequestAborted);
    }

    private static async Task<IResult> ArenaTickAsync(Guid id, ChecklistTick body, HttpContext http, McpPeople people, AppDbContext db)
    {
        var (me, refusal) = await ArenaChatAsync(id, http, people, db);
        return me is null ? refusal! : await TickOnAsync(db, id, body, body.By == "person" ? "person" : "model", http.RequestAborted);
    }

    private static async Task<IResult> ArenaStopAsync(Guid id, HttpContext http, McpPeople people, AppDbContext db)
    {
        var (me, refusal) = await ArenaChatAsync(id, http, people, db);
        if (me is null)
        {
            return refusal!;
        }
        await db.ChecklistRuns.Where(r => r.ConversationId == id).ExecuteDeleteAsync(http.RequestAborted);
        return Results.NoContent();
    }
}
