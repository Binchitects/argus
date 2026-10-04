using System.Security.Claims;
using System.Text.Json.Nodes;
using Llm.Api.Chat.Tools;
using Llm.Api.Endpoints;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Chat;

public sealed record MemoryRequest(string? Text);

/// <summary>Memory on or off, for the person.</summary>
public sealed record MemorySwitch(bool On);

/// <summary>The person's answer on a memory card in the chat: keep it (in their words, if they changed them), or not.</summary>
public sealed record MemoryDecision(bool Keep, string? Text = null);

/// <summary>A person's own memories (Your account → Memory, and the chat's menu): only ever theirs, admins included.</summary>
public static class MemoryEndpoints
{
    public static void MapMemories(this IEndpointRouteBuilder app)
    {
        var me = app.MapGroup("/api/account/memories").RequireAuthorization();
        me.MapGet("", ListAsync);
        me.MapPost("", AddAsync);
        me.MapPut("/settings", SwitchAsync);
        me.MapPut("/{id:guid}", EditAsync);
        me.MapDelete("/{id:guid}", DeleteAsync);
        me.MapDelete("", DeleteAllAsync);
        me.MapPost("/offers/{messageId:guid}", DecideAsync);
    }

    private static async Task<AppUser> Me(ClaimsPrincipal p, UserManager<AppUser> users) => (await users.GetUserAsync(p))!;

    private static object View(Core.Chat.Memory m) => new { m.Id, m.Text, m.CreatedAt, m.UpdatedAt };

    private static async Task<IResult> ListAsync(ClaimsPrincipal p, UserManager<AppUser> users, Memories memories, CancellationToken ct)
    {
        var me = await Me(p, users);
        return Results.Ok(new
        {
            enabled = memories.Enabled, on = !me.MemoryOff, max = Memories.PerPerson, maxChars = Memories.MaxChars,
            memories = (await memories.ListAsync(me.Id, ct)).Select(View),
        });
    }

    private static async Task<IResult> AddAsync(MemoryRequest body, ClaimsPrincipal p, UserManager<AppUser> users, Memories memories, CancellationToken ct)
    {
        var me = await Me(p, users);
        var (text, problem) = Memories.Check(body.Text);
        if (problem is not null)
        {
            return AuthEndpoints.Problem(400, "text", problem);
        }
        return await memories.KeepAsync(me.Id, text, ct) is { } kept
            ? Results.Created($"/api/account/memories/{kept.Id}", View(kept))
            : Full();
    }

    private static IResult Full() => AuthEndpoints.Problem(409, "full", $"You keep {Memories.PerPerson} memories already: delete some first.");

    private static async Task<IResult> EditAsync(Guid id, MemoryRequest body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, CancellationToken ct)
    {
        var me = await Me(p, users);
        var (text, problem) = Memories.Check(body.Text);
        if (problem is not null)
        {
            return AuthEndpoints.Problem(400, "text", problem);
        }
        if (await db.Memories.SingleOrDefaultAsync(m => m.Id == id && m.UserId == me.Id, ct) is not { } memory)
        {
            return Results.NotFound();
        }
        memory.Text = text;
        memory.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Results.Ok(View(memory));
    }

    private static async Task<IResult> DeleteAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, CancellationToken ct)
    {
        var me = await Me(p, users);
        return await db.Memories.Where(m => m.Id == id && m.UserId == me.Id).ExecuteDeleteAsync(ct) > 0 ? Results.NoContent() : Results.NotFound();
    }

    private static async Task<IResult> DeleteAllAsync(ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, CancellationToken ct)
    {
        var me = await Me(p, users);
        await db.Memories.Where(m => m.UserId == me.Id).ExecuteDeleteAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> SwitchAsync(MemorySwitch body, ClaimsPrincipal p, UserManager<AppUser> users)
    {
        var me = await Me(p, users);
        me.MemoryOff = !body.On;
        await users.UpdateAsync(me);
        return Results.Ok(new { on = body.On });
    }

    /// <summary>
    /// A memory card in the chat: an offer kept or declined, or a memory kept taken back (and
    /// kept again). The card's state is kept with the call, so the chat shows it after a reload.
    /// </summary>
    private static async Task<IResult> DecideAsync(Guid messageId, MemoryDecision body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, Memories memories,
        CancellationToken ct)
    {
        var me = await Me(p, users);
        var message = await db.ChatMessages.SingleOrDefaultAsync(m => m.Id == messageId && m.ToolName == MemoryTool.Function &&
            db.Conversations.Any(c => c.Id == m.ConversationId && c.UserId == me.Id), ct);
        if (message?.DetailsJson is not { } json || JsonNode.Parse(json)?["memory"] is not JsonObject card)
        {
            return Results.NotFound();
        }
        var state = card["state"]?.GetValue<string>();
        var id = Guid.TryParse(card["id"]?.GetValue<string>(), out var known) ? known : (Guid?)null;
        var text = card["text"]?.GetValue<string>() ?? "";
        if (body.Keep)
        {
            var (words, problem) = Memories.Check(body.Text ?? text);
            if (problem is not null)
            {
                return AuthEndpoints.Problem(400, "text", problem);
            }
            if (state != MemoryTool.Kept || id is null || !await db.Memories.AnyAsync(m => m.Id == id && m.UserId == me.Id, ct))
            {
                if (await memories.KeepAsync(me.Id, words, ct) is not { } kept)
                {
                    return Full();
                }
                (id, text, state) = (kept.Id, kept.Text, MemoryTool.Kept);
            }
        }
        else
        {
            if (state == MemoryTool.Kept && id is { } kept)
            {
                await db.Memories.Where(m => m.Id == kept && m.UserId == me.Id).ExecuteDeleteAsync(ct);
            }
            (id, state) = (null, state is MemoryTool.Kept or MemoryTool.Forgotten ? MemoryTool.Forgotten : MemoryTool.Declined);
        }
        var now = MemoryTool.Card(id, text, state!);
        message.DetailsJson = now.ToJsonString();
        await db.SaveChangesAsync(ct);
        return Results.Ok(now["memory"]);
    }
}
