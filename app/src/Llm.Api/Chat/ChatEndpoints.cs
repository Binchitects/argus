using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Json;
using Llm.Api.Endpoints;
using Llm.Api.Operations;
using Llm.Core.Chat;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Chat;

public sealed record NewConversation(string? Thinking = null, bool? UseArgus = null);
public sealed record ConversationChange(string? Title = null, string? Thinking = null, bool? UseArgus = null);
public sealed record NewMessage(string Content, Guid[]? Attachments = null);

public static class ChatEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>One answer at a time per conversation: a second tab cannot interleave two.</summary>
    private static readonly ConcurrentDictionary<Guid, byte> Answering = new();

    public static void MapChat(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/chat").RequireAuthorization();
        g.MapGet("/config", Config);
        g.MapGet("/conversations", ListAsync);
        g.MapPost("/conversations", CreateAsync);
        g.MapGet("/conversations/{id:guid}", GetAsync);
        g.MapPatch("/conversations/{id:guid}", UpdateAsync);
        g.MapDelete("/conversations/{id:guid}", DeleteAsync);
        g.MapPost("/conversations/{id:guid}/messages", SendAsync);
        g.MapPost("/conversations/{id:guid}/regenerate", RegenerateAsync);
        g.MapPost("/attachments", UploadAsync).DisableAntiforgery();
    }

    private static IResult Config(IOptions<StackOptions> stack, ArgusMcp argus) => Results.Ok(new
    {
        model = stack.Value.ModelName,
        presets = ThinkingPresets.Parse(stack.Value.ThinkingPresets),
        defaultThinking = string.Equals(stack.Value.ModelEnableThinking, "false", StringComparison.OrdinalIgnoreCase) ? "off" : stack.Value.ModelReasoningEffort,
        argus = argus.Enabled,
    });

    private static async Task<AppUser> Me(ClaimsPrincipal p, UserManager<AppUser> users) => (await users.GetUserAsync(p))!;

    private static Task<Conversation?> Owned(AppDbContext db, Guid id, AppUser me) =>
        db.Conversations.SingleOrDefaultAsync(c => c.Id == id && c.UserId == me.Id);

    private static async Task<IResult> ListAsync(ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, string? q = null)
    {
        var me = await Me(p, users);
        var query = db.Conversations.AsNoTracking().Where(c => c.UserId == me.Id);
        if (!string.IsNullOrWhiteSpace(q))
        {
            query = query.Where(c => EF.Functions.ILike(c.Title, "%" + q.Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%"));
        }
        return Results.Ok(await query.OrderByDescending(c => c.UpdatedAt).Take(300)
            .Select(c => new { c.Id, c.Title, c.UpdatedAt }).ToListAsync());
    }

    private static async Task<IResult> CreateAsync(NewConversation body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, IOptions<StackOptions> stack)
    {
        var me = await Me(p, users);
        if (body.Thinking is { } t && !ValidThinking(t, stack.Value))
        {
            return AuthEndpoints.Problem(400, "thinking", "Unknown thinking level.");
        }
        var c = new Conversation { UserId = me.Id, Thinking = body.Thinking, UseArgus = body.UseArgus ?? true };
        db.Conversations.Add(c);
        await db.SaveChangesAsync();
        return Results.Created($"/api/chat/conversations/{c.Id}", Shape(c, []));
    }

    private static bool ValidThinking(string level, StackOptions stack) =>
        level == "off" || ThinkingPresets.Parse(stack.ThinkingPresets).Any(p => p.Level == level);

    private static async Task<IResult> GetAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db)
    {
        var me = await Me(p, users);
        if (await Owned(db, id, me) is not { } c)
        {
            return Results.NotFound();
        }
        var messages = await db.ChatMessages.AsNoTracking().Where(m => m.ConversationId == id).OrderBy(m => m.Sequence).ToListAsync();
        var ids = messages.SelectMany(m => ChatService.ParseIds(m.AttachmentsJson)).ToHashSet();
        var files = await db.ChatAttachments.AsNoTracking().Where(a => ids.Contains(a.Id))
            .Select(a => new { a.Id, a.FileName, a.Size, a.Truncated }).ToDictionaryAsync(a => a.Id);
        return Results.Ok(Shape(c, messages.Select(m => (object)new
        {
            m.Id, m.Role, m.Content, m.Reasoning, m.ToolName, m.ToolCallId,
            toolCalls = m.ToolCallsJson is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(m.ToolCallsJson),
            attachments = ChatService.ParseIds(m.AttachmentsJson).Where(files.ContainsKey).Select(a => files[a]),
            status = m.Status.ToString().ToLowerInvariant(), m.Error, m.Model,
            m.PromptTokens, m.CachedTokens, m.CompletionTokens, m.CreatedAt,
            noAccess = m.Role == "tool" && ArgusMcp.IsNoAccess(m.Content),
        })));
    }

    private static object Shape(Conversation c, IEnumerable<object> messages) =>
        new { c.Id, c.Title, c.Thinking, c.UseArgus, c.CreatedAt, c.UpdatedAt, messages };

    private static async Task<IResult> UpdateAsync(Guid id, ConversationChange body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, IOptions<StackOptions> stack)
    {
        var me = await Me(p, users);
        if (await Owned(db, id, me) is not { } c)
        {
            return Results.NotFound();
        }
        if (body.Title is { } title)
        {
            c.Title = string.IsNullOrWhiteSpace(title) ? "New chat" : title.Trim()[..Math.Min(200, title.Trim().Length)];
        }
        if (body.Thinking is { } t)
        {
            if (t != "" && !ValidThinking(t, stack.Value))
            {
                return AuthEndpoints.Problem(400, "thinking", "Unknown thinking level.");
            }
            c.Thinking = t == "" ? null : t;
        }
        if (body.UseArgus is { } a)
        {
            c.UseArgus = a;
        }
        await db.SaveChangesAsync();
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db)
    {
        var me = await Me(p, users);
        if (await Owned(db, id, me) is not { } c)
        {
            return Results.NotFound();
        }
        db.Conversations.Remove(c);
        await db.SaveChangesAsync();
        return Results.NoContent();
    }

    private static async Task SendAsync(Guid id, NewMessage body, HttpContext http, UserManager<AppUser> users, AppDbContext db, ChatService chat)
    {
        var me = await Me(http.User, users);
        if (await Owned(db, id, me) is not { } c)
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        var text = (body.Content ?? "").Trim();
        var attachments = (body.Attachments ?? []).Distinct().ToArray();
        if (text.Length == 0 && attachments.Length == 0)
        {
            await Problem(http, 400, "empty", "Write a message first.");
            return;
        }
        if (attachments.Length > 0 && await db.ChatAttachments.CountAsync(a => attachments.Contains(a.Id) && a.UserId == me.Id) != attachments.Length)
        {
            await Problem(http, 400, "attachment", "An attachment is missing or is not yours.");
            return;
        }
        await RunAsync(http, c, me, db, chat, async () =>
        {
            var next = await db.ChatMessages.Where(m => m.ConversationId == c.Id).MaxAsync(m => (int?)m.Sequence) ?? 0;
            var first = next == 0;
            db.ChatMessages.Add(new ChatMessage
            {
                ConversationId = c.Id, Role = "user", Sequence = next + 1, Content = text,
                AttachmentsJson = attachments.Length > 0 ? JsonSerializer.Serialize(attachments) : null,
            });
            if (first)
            {
                c.Title = ChatService.TitleFrom(text.Length > 0 ? text : "Attached files");
            }
            c.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            return first;
        });
    }

    private static async Task RegenerateAsync(Guid id, HttpContext http, UserManager<AppUser> users, AppDbContext db, ChatService chat)
    {
        var me = await Me(http.User, users);
        if (await Owned(db, id, me) is not { } c)
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        await RunAsync(http, c, me, db, chat, async () =>
        {
            var lastUser = await db.ChatMessages.Where(m => m.ConversationId == c.Id && m.Role == "user").MaxAsync(m => (int?)m.Sequence);
            if (lastUser is null)
            {
                throw new InvalidOperationException("There is no question to answer again.");
            }
            await db.ChatMessages.Where(m => m.ConversationId == c.Id && m.Sequence > lastUser).ExecuteDeleteAsync();
            return false;
        });
    }

    /// <summary>Server-sent events: one JSON object per event, flushed as it happens.</summary>
    private static async Task RunAsync(HttpContext http, Conversation c, AppUser me, AppDbContext db, ChatService chat, Func<Task<bool>> prepare)
    {
        if (!Answering.TryAdd(c.Id, 0))
        {
            await Problem(http, 409, "busy", "This chat is already answering. Stop it first, or wait.");
            return;
        }
        try
        {
            bool titled;
            try
            {
                titled = await prepare();
            }
            catch (InvalidOperationException ex)
            {
                await Problem(http, 400, "invalid", ex.Message);
                return;
            }
            http.Response.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            // Tell proxies not to buffer: every token should reach the browser at once.
            http.Response.Headers["X-Accel-Buffering"] = "no";
            await http.Response.Body.FlushAsync();
            async Task Emit(object e)
            {
                await http.Response.WriteAsync("data: " + JsonSerializer.Serialize(e, Json) + "\n\n", http.RequestAborted);
                await http.Response.Body.FlushAsync(http.RequestAborted);
            }
            if (titled)
            {
                await Emit(new { type = "title", title = c.Title });
            }
            try
            {
                await chat.AnswerAsync(me, c, Emit, http.RequestAborted);
            }
            catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
            {
                // The person left or pressed stop; ChatService saved what there was.
            }
        }
        finally
        {
            Answering.TryRemove(c.Id, out _);
        }
    }

    private static async Task Problem(HttpContext http, int status, string code, string message)
    {
        http.Response.StatusCode = status;
        await http.Response.WriteAsJsonAsync(new { status = code, error = message });
    }

    private static async Task<IResult> UploadAsync(HttpRequest request, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, IOptions<ChatOptions> options)
    {
        var me = await Me(p, users);
        if (!request.HasFormContentType || (await request.ReadFormAsync()).Files is not { Count: 1 } files)
        {
            return AuthEndpoints.Problem(400, "file", "Send exactly one file.");
        }
        var file = files[0];
        if (file.Length > options.Value.MaxUploadBytes)
        {
            return AuthEndpoints.Problem(413, "too_large", $"{file.FileName} is larger than {options.Value.MaxUploadBytes / 1024 / 1024} MB.");
        }
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms);
        try
        {
            var (text, truncated) = Attachments.Extract(file.FileName, file.ContentType ?? "", ms.ToArray(), options.Value.MaxAttachmentChars);
            var a = new ChatAttachment
            {
                UserId = me.Id, FileName = Path.GetFileName(file.FileName), ContentType = file.ContentType ?? "application/octet-stream",
                Size = file.Length, Text = text, Truncated = truncated,
            };
            db.ChatAttachments.Add(a);
            await db.SaveChangesAsync();
            return Results.Ok(new { a.Id, a.FileName, a.Size, chars = text.Length, a.Truncated });
        }
        catch (AttachmentException ex)
        {
            return AuthEndpoints.Problem(400, "unreadable", ex.Message);
        }
    }
}
