using System.Security.Claims;
using Llm.Api.Access;
using System.Text.Json.Nodes;
using Llm.Api.Chat.Tools;
using Llm.Api.Endpoints;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Chat;

/// <summary>A memory to keep or change: its words, and what of the rest is said (what is not stays).</summary>
public sealed record MemoryRequest(string? Text, string? Scope = null, string? Kind = null, string? ProjectKey = null, bool? Pinned = null);

/// <summary>Memory on or off for the person, and whether what it proposes from their quiet chats is kept on its own (left out: as it was).</summary>
public sealed record MemorySwitch(bool On, bool? Auto = null);

/// <summary>The person's answer on a memory card in the chat: keep it (in their words, if they changed them), or not.</summary>
public sealed record MemoryDecision(bool Keep, string? Text = null);

/// <summary>
/// A person's memories (Memory, and the chat's menu): their own (about them, and about their projects), only ever theirs,
/// admins included; and the company's, which everyone's answers read and those who manage the company's knowledge keep.
/// </summary>
public static class MemoryEndpoints
{
    public static void MapMemories(this IEndpointRouteBuilder app)
    {
        var me = app.MapGroup("/api/account/memories").PersonOnly();
        me.MapGet("", ListAsync);
        me.MapPost("", AddAsync).RequirePermission(Llm.Core.Access.Permissions.Memory);
        me.MapPut("/settings", SwitchAsync).RequirePermission(Llm.Core.Access.Permissions.Memory);
        me.MapPut("/{id:guid}", EditAsync).RequirePermission(Llm.Core.Access.Permissions.Memory);
        me.MapDelete("/{id:guid}", DeleteAsync);
        me.MapDelete("", DeleteAllAsync);
        me.MapPost("/offers/{messageId:guid}", DecideAsync).RequirePermission(Llm.Core.Access.Permissions.Memory);
        me.MapPost("/{id:guid}/keep", AcceptAsync).RequirePermission(Llm.Core.Access.Permissions.Memory);
    }

    private static async Task<AppUser> Me(ClaimsPrincipal p, UserManager<AppUser> users) => (await users.GetUserAsync(p))!;

    /// <summary>A memory as the page shows it; mine: the person's own (else the company's), and whether they may change it.</summary>
    public static object View(Core.Chat.Memory m, Guid me, bool keepsCompany) => new
    {
        m.Id, m.Text, m.Scope, m.Kind, project = m.ProjectKey, m.Source, m.SourceId, m.Pinned, m.UsedAt, m.Uses, m.ReplacedBy, m.Proposed, m.Replaces, m.CreatedAt, m.UpdatedAt,
        mine = m.UserId == me, canEdit = m.Scope == Core.Chat.Memory.Company ? keepsCompany : m.UserId == me,
    };

    private static Task<bool> KeepsCompanyAsync(AccessService access, AppUser me, CancellationToken ct) => access.MayAsync(me, Llm.Core.Access.Permissions.KnowledgeManage, ct);

    private static async Task<IResult> ListAsync(ClaimsPrincipal p, UserManager<AppUser> users, Memories memories, AccessService access, CancellationToken ct, bool replaced = false)
    {
        var me = await Me(p, users);
        var company = await KeepsCompanyAsync(access, me, ct);
        return Results.Ok(new
        {
            enabled = memories.Enabled, on = !me.MemoryOff, auto = me.MemoryAuto, max = Memories.PerPerson, maxChars = Memories.MaxChars, keepsCompany = company,
            kinds = Core.Chat.Memory.Kinds, scopes = Core.Chat.Memory.Scopes,
            memories = (await memories.ListAsync(me.Id, ct, replaced)).Select(m => View(m, me.Id, company)),
        });
    }

    private static async Task<IResult> AddAsync(MemoryRequest body, ClaimsPrincipal p, UserManager<AppUser> users, Memories memories, AccessService access, CancellationToken ct)
    {
        var me = await Me(p, users);
        var (text, problem) = Memories.Check(body.Text);
        var facts = new MemoryFacts(body.Scope ?? Core.Chat.Memory.Person, body.Kind ?? Core.Chat.Memory.Fact, body.ProjectKey?.Trim(), Core.Chat.Memory.Typed, null, body.Pinned ?? false);
        if ((problem ??= Memories.Problem(facts)) is { } why)
        {
            return AuthEndpoints.Problem(400, "text", why);
        }
        var company = await KeepsCompanyAsync(access, me, ct);
        if (facts.Scope == Core.Chat.Memory.Company && !company)
        {
            return AuthEndpoints.Problem(403, "company", "Only those who manage the company's knowledge keep the company's memories.");
        }
        return await memories.KeepAsync(me.Id, text, facts, ct) is { } kept
            ? Results.Created($"/api/account/memories/{kept.Id}", View(kept, me.Id, company))
            : Full(facts.Scope);
    }

    private static IResult Full(string scope = Core.Chat.Memory.Person) => scope == Core.Chat.Memory.Company
        ? AuthEndpoints.Problem(409, "full", $"The company keeps {Memories.CompanyMost} memories already: delete some first.")
        : AuthEndpoints.Problem(409, "full", $"You keep {Memories.PerPerson} memories already: delete some first.");

    /// <summary>A memory the person may change: their own, or the company's when they keep those.</summary>
    private static async Task<Core.Chat.Memory?> ChangeableAsync(Guid id, AppUser me, AppDbContext db, AccessService access, CancellationToken ct) =>
        await db.Memories.SingleOrDefaultAsync(m => m.Id == id, ct) is { } m && (m.Scope == Core.Chat.Memory.Company ? await KeepsCompanyAsync(access, me, ct) : m.UserId == me.Id)
            ? m : null;

    private static async Task<IResult> EditAsync(Guid id, MemoryRequest body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, Memories memories, AccessService access,
        CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await ChangeableAsync(id, me, db, access, ct) is not { } memory)
        {
            return Results.NotFound();
        }
        var (text, problem) = body.Text is null ? (memory.Text, null) : Memories.Check(body.Text);
        var facts = new MemoryFacts(memory.Scope, body.Kind ?? memory.Kind, memory.ProjectKey, memory.Source, memory.SourceId, body.Pinned ?? memory.Pinned);
        if ((problem ??= Memories.Problem(facts)) is { } why)
        {
            return AuthEndpoints.Problem(400, "text", why);
        }
        if (text != memory.Text)
        {
            memory.Text = text;
            await memories.ReembedAsync(memory, ct);
        }
        (memory.Kind, memory.Pinned, memory.UpdatedAt) = (facts.Kind, facts.Pinned, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(ct);
        return Results.Ok(View(memory, me.Id, await KeepsCompanyAsync(access, me, ct)));
    }

    private static async Task<IResult> DeleteAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, AccessService access, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await ChangeableAsync(id, me, db, access, ct) is not { } memory)
        {
            return Results.NotFound();
        }
        db.Memories.Remove(memory);
        // What it replaced is read again.
        await db.Memories.Where(m => m.ReplacedBy == id).ExecuteUpdateAsync(s => s.SetProperty(m => m.ReplacedBy, (Guid?)null), ct);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    /// <summary>Every memory of the person's own (the company's stay).</summary>
    private static async Task<IResult> DeleteAllAsync(ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, CancellationToken ct)
    {
        var me = await Me(p, users);
        await db.Memories.Where(m => m.UserId == me.Id && m.Scope != Core.Chat.Memory.Company).ExecuteDeleteAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> SwitchAsync(MemorySwitch body, ClaimsPrincipal p, UserManager<AppUser> users)
    {
        var me = await Me(p, users);
        me.MemoryOff = !body.On;
        me.MemoryAuto = body.Auto ?? me.MemoryAuto;
        await users.UpdateAsync(me);
        return Results.Ok(new { on = body.On, auto = me.MemoryAuto });
    }

    /// <summary>A memory proposed from a quiet chat, kept by the person: read from now on (and what it says otherwise replaced).</summary>
    private static async Task<IResult> AcceptAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, Memories memories, AccessService access, CancellationToken ct)
    {
        var me = await Me(p, users);
        return await memories.AcceptAsync(me.Id, id, ct) is { } kept ? Results.Ok(View(kept, me.Id, await KeepsCompanyAsync(access, me, ct))) : Results.NotFound();
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
                var kind = card["kind"]?.GetValue<string>() is { } k && Core.Chat.Memory.Kinds.Contains(k) ? k : Core.Chat.Memory.Fact;
                if (await memories.KeepAsync(me.Id, words, new MemoryFacts(Kind: kind, Source: Core.Chat.Memory.FromChat, SourceId: message.ConversationId), ct) is not { } kept)
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
        var now = MemoryTool.Card(id, text, state!, card["kind"]?.GetValue<string>() ?? Core.Chat.Memory.Fact);
        message.DetailsJson = now.ToJsonString();
        await db.SaveChangesAsync(ct);
        return Results.Ok(now["memory"]);
    }
}
