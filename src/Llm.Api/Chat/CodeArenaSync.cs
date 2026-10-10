using System.Text.Json;
using System.Text.Json.Nodes;
using Llm.Api.ArenaMcp;
using Llm.Api.Endpoints;
using Llm.Api.Gateway;
using Llm.Core.Chat;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Chat;

/// <summary>A message as Code Arena and this end exchange it: the model's own shape, with its model, tokens and time.</summary>
public sealed record SyncMessage(
    string Role,
    JsonNode? Content,
    string? Reasoning = null,
    JsonNode? ToolCalls = null,
    string? ToolCallId = null,
    string? Name = null,
    string? Model = null,
    int? PromptTokens = null,
    int? CachedTokens = null,
    int? CompletionTokens = null,
    DateTimeOffset? At = null,
    string? Ref = null);

public sealed record SyncNewChat(string? Ref, string? Place, string? Model, string? Title);

/// <summary>Messages Code Arena adds after the <paramref name="After"/> it has already (the chat's count as it knows it).</summary>
public sealed record SyncAppend(int After, List<SyncMessage>? Messages);

/// <summary>
/// Code Arena's chats kept in step with the person's chats here, both ways, signed in by their API key (as Arena MCP is):
/// a session there is a chat here (its folder shown), continued here with the chat's tools, and a chat here can be
/// continued there. Each side adds to the branch on screen; a count of its messages says what the other lacks.
/// </summary>
public static class CodeArenaSync
{
    public const string Origin = "code-arena";
    /// <summary>The most messages one request adds.</summary>
    public const int MostAtOnce = 500;

    public static void MapCodeArenaSync(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/code-arena");
        g.MapGet("/chats", ListAsync);
        g.MapPost("/chats", CreateAsync);
        g.MapGet("/chats/{id:guid}", ReadAsync);
        g.MapPost("/chats/{id:guid}/messages", AppendAsync);
    }

    private static async Task<(AppUser? Me, IResult? Refusal)> CallerAsync(HttpContext http, McpPeople people)
    {
        try
        {
            var caller = await people.FromHeaderAsync(http.Request.Headers.Authorization.ToString() is { Length: > 0 } a ? a : null, http.RequestAborted);
            return caller.User is { } me ? (me, null) : (null, AuthEndpoints.Problem(401, "key", caller.Refusal ?? McpPeople.NoKey));
        }
        catch (GatewayException)
        {
            return (null, AuthEndpoints.Problem(503, "gateway", "The gateway cannot say whose that key is now: try again in a moment."));
        }
    }

    /// <summary>
    /// The person's chats, newest first, to list beside Code Arena's own sessions and continue one there: a page of
    /// <paramref name="limit"/> (200 at most), those changed before <paramref name="before"/> (the last one's UpdatedAt) for
    /// the next page; <c>more</c> says whether there is one.
    /// </summary>
    private static async Task<IResult> ListAsync(HttpContext http, McpPeople people, AppDbContext db, int limit = 30, DateTimeOffset? before = null)
    {
        var (me, refusal) = await CallerAsync(http, people);
        if (me is null)
        {
            return refusal!;
        }
        var take = Math.Clamp(limit, 1, 200);
        var chats = await db.Conversations.AsNoTracking().Where(c => c.UserId == me.Id && c.ArchivedAt == null && (before == null || c.UpdatedAt < before))
            .OrderByDescending(c => c.UpdatedAt).Take(take + 1)
            .Select(c => new { c.Id, c.Title, c.Origin, c.OriginRef, c.OriginPlace, c.Model, c.UpdatedAt, messages = c.Messages.Count })
            .ToListAsync(http.RequestAborted);
        return Results.Ok(new { chats = chats.Take(take), more = chats.Count > take });
    }

    /// <summary>The chat for a Code Arena session: made the first time, the same one after (by the session's id).</summary>
    private static async Task<IResult> CreateAsync(SyncNewChat body, HttpContext http, McpPeople people, AppDbContext db)
    {
        var (me, refusal) = await CallerAsync(http, people);
        if (me is null)
        {
            return refusal!;
        }
        var reference = Clip(body.Ref, 128);
        if (reference is not null && await db.Conversations.FirstOrDefaultAsync(c => c.UserId == me.Id && c.Origin == Origin && c.OriginRef == reference, http.RequestAborted) is { } known)
        {
            return Results.Ok(new { id = known.Id, created = false });
        }
        var c = new Conversation
        {
            UserId = me.Id,
            Origin = Origin,
            OriginRef = reference,
            OriginPlace = Clip(body.Place, 1000),
            Model = Clip(body.Model, 200),
            Title = Clip(body.Title, 200) ?? "New chat",
        };
        db.Conversations.Add(c);
        await db.SaveChangesAsync(http.RequestAborted);
        return Results.Ok(new { id = c.Id, created = true });
    }

    /// <summary>
    /// The chat's messages after the first <paramref name="after"/> on the branch on screen, their count, and its stamp
    /// (the branch's last message). Given the stamp of a read that brought nothing new, and the branch has not moved
    /// since, it says only that (<c>unchanged</c>): Code Arena asks every few seconds while it waits, and a long chat is
    /// not read each time.
    /// </summary>
    private static async Task<IResult> ReadAsync(Guid id, HttpContext http, McpPeople people, AppDbContext db, AnswerJobs jobs, int after = 0, string? stamp = null)
    {
        var (me, refusal) = await CallerAsync(http, people);
        if (me is null)
        {
            return refusal!;
        }
        if (await db.Conversations.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id && c.UserId == me.Id, http.RequestAborted) is not { } c)
        {
            return AuthEndpoints.Problem(404, "gone", "That chat is not there any more: it was deleted.");
        }
        if (jobs.IsAnswering(id))
        {
            return AuthEndpoints.Problem(409, "answering", "The chat is answering on the web: its messages come when the answer ends.");
        }
        if (stamp is not null && stamp == Stamp(c.CurrentLeafId))
        {
            return Results.Ok(new { unchanged = true, stamp });
        }
        var path = await BranchAsync(db, c, http.RequestAborted);
        return Results.Ok(new
        {
            c.Id, c.Title, c.Model, c.Origin, c.OriginPlace, count = path.Count, stamp = Stamp(c.CurrentLeafId),
            messages = path.Skip(Math.Max(0, after)).Select(Out),
        });
    }

    /// <summary>
    /// Adds Code Arena's new messages to the branch on screen. When the chat has more than Code Arena knows (the
    /// person wrote on the web meanwhile), nothing is added: 409 with those messages, to take in before sending again.
    /// </summary>
    private static async Task<IResult> AppendAsync(Guid id, SyncAppend body, HttpContext http, McpPeople people, AppDbContext db, AnswerJobs jobs)
    {
        var (me, refusal) = await CallerAsync(http, people);
        if (me is null)
        {
            return refusal!;
        }
        var ct = http.RequestAborted;
        if (await db.Conversations.SingleOrDefaultAsync(c => c.Id == id && c.UserId == me.Id, ct) is not { } c)
        {
            return AuthEndpoints.Problem(404, "gone", "That chat is not there any more: it was deleted.");
        }
        var messages = body.Messages ?? [];
        if (messages.Count > MostAtOnce)
        {
            return AuthEndpoints.Problem(400, "too_many", $"At most {MostAtOnce} messages at once.");
        }
        if (messages.FirstOrDefault(m => m.Role is not ("user" or "assistant" or "tool")) is { } odd)
        {
            return AuthEndpoints.Problem(400, "role", $"A message's role is user, assistant or tool, not \"{odd.Role}\".");
        }
        if (jobs.IsAnswering(id))
        {
            return AuthEndpoints.Problem(409, "answering", "The chat is answering on the web: send again when the answer ends.");
        }
        var path = await BranchAsync(db, c, ct);
        if (body.After != path.Count)
        {
            return Results.Conflict(new { status = "behind", count = path.Count, messages = path.Skip(Math.Clamp(body.After, 0, path.Count)).Select(Out) });
        }
        var sequence = await db.ChatMessages.Where(m => m.ConversationId == id).MaxAsync(m => (int?)m.Sequence, ct) ?? 0;
        var parent = c.CurrentLeafId;
        foreach (var m in messages)
        {
            var message = new ChatMessage
            {
                ConversationId = id,
                ParentId = parent,
                Sequence = ++sequence,
                Role = m.Role,
                Content = Text(m.Content),
                Reasoning = string.IsNullOrEmpty(m.Reasoning) ? null : m.Reasoning,
                ToolCallsJson = m.Role == "assistant" && m.ToolCalls is JsonArray { Count: > 0 } calls ? calls.ToJsonString() : null,
                ToolCallId = m.Role == "tool" ? Clip(m.ToolCallId, 200) : null,
                ToolName = m.Role == "tool" ? Clip(m.Name, 200) : null,
                Model = m.Role == "assistant" ? Clip(m.Model, 200) : null,
                PromptTokens = m.PromptTokens,
                CachedTokens = m.CachedTokens,
                CompletionTokens = m.CompletionTokens,
                CreatedAt = m.At ?? DateTimeOffset.UtcNow,
                SyncRef = Clip(m.Ref, 160),
            };
            db.ChatMessages.Add(message);
            parent = message.Id;
        }
        if (messages.Count > 0)
        {
            c.CurrentLeafId = parent;
            c.UpdatedAt = DateTimeOffset.UtcNow;
            c.ArchivedAt = null;
            if (c.Title == "New chat" && messages.FirstOrDefault(m => m.Role == "user") is { } first && TitleOf(Text(first.Content)) is { } title)
            {
                c.Title = title;
            }
            await db.SaveChangesAsync(ct);
        }
        return Results.Ok(new { count = path.Count + messages.Count, stamp = Stamp(c.CurrentLeafId) });
    }

    /// <summary>The branch on screen as one word: any message added to the chat, or another branch shown, changes it.</summary>
    private static string Stamp(Guid? leaf) => leaf?.ToString("N") ?? "empty";

    /// <summary>The branch on screen, as the model sees it: questions, answers with words or calls, and the calls' results.</summary>
    internal static async Task<List<ChatMessage>> BranchAsync(AppDbContext db, Conversation c, CancellationToken ct)
    {
        var all = await db.ChatMessages.AsNoTracking().Where(m => m.ConversationId == c.Id).ToDictionaryAsync(m => m.Id, ct);
        return [.. ChatService.PathTo(all, c.CurrentLeafId).Where(Counts)];
    }

    /// <summary>A message that is part of the conversation: not an answer that failed or was declined with nothing in it.</summary>
    private static bool Counts(ChatMessage m) =>
        m.Role switch
        {
            "user" or "tool" => true,
            "assistant" => m.ToolCallsJson is not null || m.Content.Length > 0,
            _ => false,
        };

    private static SyncMessage Out(ChatMessage m) => new(
        m.Role,
        m.Content,
        m.Reasoning,
        m.ToolCallsJson is null ? null : JsonNode.Parse(m.ToolCallsJson),
        m.ToolCallId,
        m.ToolName,
        m.Model,
        m.PromptTokens,
        m.CachedTokens,
        m.CompletionTokens,
        m.CreatedAt,
        m.SyncRef);

    /// <summary>A message's text: a string as it is; parts (text and images), their text, an image said as such.</summary>
    private static string Text(JsonNode? content) => content switch
    {
        null => "",
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonArray parts => string.Join("\n", parts.OfType<JsonObject>().Select(p => p["type"]?.GetValue<string>() == "text" ? p["text"]?.GetValue<string>() ?? "" : "[an image]")),
        _ => content.ToJsonString(),
    };

    private static string? TitleOf(string text)
    {
        var line = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        return line is null ? null : line.Length <= 80 ? line : line[..77].TrimEnd() + "...";
    }

    private static string? Clip(string? value, int most) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().Length <= most ? value.Trim() : value.Trim()[..most];
}
