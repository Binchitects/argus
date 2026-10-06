using Llm.Core.Chat;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Chat;

/// <summary>
/// A fork: a new chat holding a branch of another up to one message, its questions,
/// answers, tool calls and files. A fork ends on a question or a finished answer,
/// never inside a tool round.
/// </summary>
public static class ChatForks
{
    /// <summary>Why a fork cannot end at this message, or null when it can.</summary>
    public static string? Refusal(ChatMessage last) =>
        last.Role == "tool" || last.ToolCallsJson is not null ? "Fork from a question or a finished answer." : null;

    public static string Title(string title) => (title + " (fork)")[..Math.Min(200, title.Length + 7)];

    /// <summary>
    /// Copies the branch down to <paramref name="last"/> into <paramref name="fork"/> (new ids,
    /// the same order) and makes it the fork's branch on screen. Files are mapped through
    /// <paramref name="files"/> when given (a fork of someone else's chat has copies of them).
    /// </summary>
    public static void Copy(AppDbContext db, Conversation fork, IReadOnlyDictionary<Guid, ChatMessage> byId, ChatMessage last, IReadOnlyDictionary<Guid, Guid>? files = null)
    {
        var copies = new Dictionary<Guid, Guid>();
        var sequence = 0;
        foreach (var m in ChatService.PathTo(byId, last.Id))
        {
            var copy = new ChatMessage
            {
                ConversationId = fork.Id, ParentId = m.ParentId is { } parent ? copies[parent] : null, Sequence = ++sequence, Role = m.Role,
                Content = m.Content, Reasoning = m.Reasoning, ToolCallsJson = m.ToolCallsJson, ToolCallId = m.ToolCallId, ToolName = m.ToolName,
                AttachmentsJson = Mapped(m.AttachmentsJson, files), DetailsJson = Mapped(m.DetailsJson, files), ContextJson = m.ContextJson, CutShort = m.CutShort, Model = m.Model,
                PromptTokens = m.PromptTokens, CachedTokens = m.CachedTokens, CompletionTokens = m.CompletionTokens, ThinkingMs = m.ThinkingMs, DurationMs = m.DurationMs,
                // What it cost, shown as it was; not its answer: it ran once, and is one prompt in usage.
                Cost = m.Cost,
                Status = m.Status, Error = m.Error, Summary = m.Summary, CreatedAt = m.CreatedAt,
            };
            copies[m.Id] = copy.Id;
            db.ChatMessages.Add(copy);
        }
        fork.CurrentLeafId = copies[last.Id];
    }

    /// <summary>A message's JSON (its files' ids, a sub-agent's work) with each file's id replaced by its copy's.</summary>
    private static string? Mapped(string? json, IReadOnlyDictionary<Guid, Guid>? files)
    {
        if (json is null || files is null || files.Count == 0)
        {
            return json;
        }
        foreach (var (from, to) in files)
        {
            json = json.Replace(from.ToString(), to.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        return json;
    }

    /// <summary>Copies of other people's files for <paramref name="owner"/>: the old id to the new one.</summary>
    public static async Task<Dictionary<Guid, Guid>> CopyFilesAsync(AppDbContext db, IEnumerable<ChatMessage> path, Guid owner, CancellationToken ct)
    {
        var ids = path.SelectMany(m => ChatService.ParseIds(m.AttachmentsJson)).Distinct().ToList();
        var map = new Dictionary<Guid, Guid>();
        foreach (var a in await db.ChatAttachments.AsNoTracking().Where(a => ids.Contains(a.Id)).ToListAsync(ct))
        {
            var copy = new ChatAttachment
            {
                UserId = owner, FileName = a.FileName, ContentType = a.ContentType, Size = a.Size, Text = a.Text, Truncated = a.Truncated, Kind = a.Kind,
                Data = a.Data, Sound = a.Sound, Seconds = a.Seconds,
            };
            db.ChatAttachments.Add(copy);
            map[a.Id] = copy.Id;
        }
        return map;
    }
}
