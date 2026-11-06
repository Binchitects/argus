using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Llm.Api.Endpoints;
using Llm.Core.Chat;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Chat;

/// <summary>Leaves a file that went with a message out of what the model reads, or takes it back.</summary>
public sealed record FileContext(Guid File, bool LeftOut);

public static partial class ChatEndpoints
{
    /// <summary>
    /// What fills the chat's window, as Claude Code's /context shows it: the request of the last answer on the branch on screen,
    /// piece by piece in the model's tokens (counted with its tokenizer where its engine has one, else estimated, and it says
    /// which), adding up to what the engine counted, with the room kept for the answer, what is free and what could be freed.
    /// What changed since (a message or file left out, a compaction) is said: the next answer counts it.
    /// </summary>
    private static async Task<IResult> ContextWindowAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await Owned(db, id, me, Need.Read) is not { } c)
        {
            return Results.NotFound();
        }
        var branch = await Tools.ChatHistoryTool.BranchAsync(db, c, ct);
        var at = branch.FindLastIndex(m => m.WindowJson is not null);
        if (at < 0 || JsonNode.Parse(branch[at].WindowJson!) is not JsonObject window)
        {
            return Results.Ok(new { window = (object?)null, compactedSince = false });
        }
        var byId = branch.ToDictionary(m => m.Id.ToString());
        var fileIds = branch.Where(m => m.Role == "user").SelectMany(m => ChatService.ParseIds(m.AttachmentsJson)).ToHashSet();
        var names = await db.ChatAttachments.AsNoTracking().Where(a => fileIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.FileName, ct);
        // What the person left out since: struck in the view, and no longer offered to free.
        foreach (var part in (window["parts"] as JsonArray ?? []).OfType<JsonObject>())
        {
            foreach (var item in (part["items"] as JsonArray ?? []).OfType<JsonObject>())
            {
                if (item["ref"]?.GetValue<string>() is { } r && byId.TryGetValue(r, out var m) && (m.LeftOut || (part["key"]?.GetValue<string>() == "files" && FileOf(m, item["name"]?.GetValue<string>(), names) is { LeftOut: true })))
                {
                    item["leftOut"] = true;
                }
            }
        }
        var freeable = new JsonArray();
        foreach (var f in (window["freeable"] as JsonArray ?? []).OfType<JsonObject>().Select(f => (JsonObject)f.DeepClone()))
        {
            var r = f["ref"]?.GetValue<string>();
            var m = r is null ? null : byId.GetValueOrDefault(r);
            switch (f["action"]?.GetValue<string>())
            {
                case "leave-out" when m is null || m.LeftOut:
                    continue;
                case "drop-file":
                    if (m is null || m.LeftOut || FileOf(m, f["detail"]?.GetValue<string>(), names) is not { LeftOut: false } file)
                    {
                        continue;
                    }
                    f["file"] = file.Id;
                    break;
            }
            freeable.Add(f);
        }
        window["freeable"] = freeable;
        return Results.Ok(new
        {
            window,
            at = branch[at].Id,
            model = branch[at].Model,
            // A compaction since: the next request starts from its summary.
            compactedSince = branch.Skip(at + 1).Any(m => m.Summary is not null),
        });
    }

    /// <summary>The file of a message by its name, and whether the person left it out.</summary>
    private static (Guid Id, bool LeftOut)? FileOf(ChatMessage m, string? name, Dictionary<Guid, string> names)
    {
        var file = ChatService.ParseIds(m.AttachmentsJson).Select(a => (Guid?)a).FirstOrDefault(a => names.GetValueOrDefault(a!.Value) == name);
        return name is not null && file is { } f ? (f, ChatService.ParseIds(m.LeftOutFilesJson).Contains(f)) : null;
    }

    /// <summary>A question's file left out of what the model reads from the next answer on, or taken back; the message's files left out.</summary>
    private static async Task<IResult> FileContextAsync(Guid id, Guid messageId, FileContext body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db)
    {
        var me = await Me(p, users);
        if (await Owned(db, id, me, Need.Manage) is null || await db.ChatMessages.SingleOrDefaultAsync(m => m.Id == messageId && m.ConversationId == id) is not { } message)
        {
            return Results.NotFound();
        }
        if (message.Role != "user" || !ChatService.ParseIds(message.AttachmentsJson).Contains(body.File))
        {
            return AuthEndpoints.Problem(400, "file", "That file did not go with this message.");
        }
        var left = ChatService.ParseIds(message.LeftOutFilesJson).ToHashSet();
        if (body.LeftOut)
        {
            left.Add(body.File);
        }
        else
        {
            left.Remove(body.File);
        }
        message.LeftOutFilesJson = left.Count == 0 ? null : JsonSerializer.Serialize(left);
        await db.SaveChangesAsync();
        return Results.Ok(new { message.Id, leftOutFiles = left });
    }
}
