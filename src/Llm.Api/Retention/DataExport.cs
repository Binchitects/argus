using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Llm.Api.Access;
using Llm.Api.Chat;
using Llm.Core.Chat;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Retention;

/// <summary>
/// A person's data as one zip: their profile and preferences, assistants, scheduled tasks
/// (never a secret: a webhook's address is only said to be set), every chat (all its
/// branches as JSON, and the branch on screen as Markdown) and every file. An admin's
/// export for eDiscovery has the chats they deleted while on legal hold too, marked so.
/// </summary>
public sealed class DataExport(AppDbContext db, AccessService access)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>How many chats and files the export holds, for the audit log before it is written.</summary>
    public async Task<(int Chats, int Files)> CountAsync(AppUser user, bool hidden, CancellationToken ct)
    {
        var chats = await Chats(user, hidden).CountAsync(ct);
        var files = await db.ChatAttachments.CountAsync(a => a.UserId == user.Id, ct);
        return (chats, files);
    }

    private IQueryable<Conversation> Chats(AppUser user, bool hidden) =>
        (hidden ? db.Conversations.IgnoreQueryFilters() : db.Conversations).AsNoTracking().Where(c => c.UserId == user.Id);

    /// <param name="hidden">With the chats deleted under legal hold (an admin's export).</param>
    public async Task WriteAsync(AppUser user, bool hidden, Stream output, CancellationToken ct)
    {
        await using var zip = await ZipArchive.CreateAsync(output, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: null, ct);
        var member = await access.MembershipAsync(user, ct);
        var groups = await db.Groups.AsNoTracking().Where(g => member.Groups.Contains(g.Id)).OrderBy(g => g.Name).Select(g => g.Name).ToListAsync(ct);
        await TextAsync(zip, "README.txt", Readme(user, hidden), ct);
        await JsonAsync(zip, "person.json", new
        {
            exportedAt = DateTimeOffset.UtcNow,
            user.UserName, user.DisplayName, user.Email,
            source = user.Source == UserSource.Ldap ? "ldap" : "local",
            user.CreatedAt, user.LastSignInAt,
            preferences = new { answerLength = user.AnswerLength ?? AnswerLengths.Normal },
            groups,
            legalHold = hidden && user.LegalHoldSince is { } since ? new { since, reason = user.LegalHoldReason } : null,
        }, ct);

        var assistants = await db.Assistants.AsNoTracking().Where(p => p.UserId == user.Id).OrderBy(p => p.CreatedAt).ToListAsync(ct);
        var assistantIds = assistants.Select(p => p.Id).ToList();
        var assistantFiles = await db.AssistantFiles.AsNoTracking().Where(f => assistantIds.Contains(f.AssistantId)).ToListAsync(ct);
        await JsonAsync(zip, "assistants.json", assistants.Select(p => new
        {
            p.Id, p.Name, p.Description, p.Instructions, p.Model, p.Thinking, p.Tools, p.Starters, p.Icon, p.Color, reach = p.Reach.ToString().ToLowerInvariant(), p.CreatedAt, p.UpdatedAt,
            files = assistantFiles.Where(f => f.AssistantId == p.Id).OrderBy(f => f.AddedAt).Select(f => f.AttachmentId),
        }), ct);

        var tasks = await db.ScheduledTasks.AsNoTracking().Where(t => t.UserId == user.Id).OrderBy(t => t.CreatedAt).ToListAsync(ct);
        await JsonAsync(zip, "tasks.json", tasks.Select(t => new
        {
            t.Name, t.Prompt, t.Cron, t.TimeZone, t.Model, t.Thinking, t.Tools, t.SameChat, t.Email,
            webhook = t.WebhookEncrypted is not null, t.Trigger, t.Events, t.ReplyInGitLab, t.Enabled, t.CreatedAt, t.LastRunAt,
        }), ct);

        var names = new Dictionary<Guid, string>();
        foreach (var file in await db.ChatAttachments.AsNoTracking().Where(a => a.UserId == user.Id).OrderBy(a => a.CreatedAt).Select(a => new { a.Id, a.FileName }).ToListAsync(ct))
        {
            names[file.Id] = $"files/{file.Id.ToString("N")[..8]}-{Safe(file.FileName)}";
        }
        foreach (var id in await Chats(user, hidden).OrderBy(c => c.CreatedAt).Select(c => c.Id).ToListAsync(ct))
        {
            var c = await Chats(user, hidden).SingleAsync(x => x.Id == id, ct);
            var messages = await db.ChatMessages.AsNoTracking().Where(m => m.ConversationId == id).OrderBy(m => m.Sequence).ToListAsync(ct);
            var stem = $"chats/{c.CreatedAt:yyyy-MM-dd}-{Safe(c.Title, 60)}-{c.Id.ToString("N")[..8]}";
            await JsonAsync(zip, stem + ".json", new
            {
                c.Id, c.Title, c.CreatedAt, c.UpdatedAt, c.DeletedAt, c.ArchivedAt, c.Model, c.Thinking, c.SystemPrompt, c.AssistantId, c.ForkedFromId, c.CurrentLeafId,
                messages = messages.Select(m => new
                {
                    m.Id, m.ParentId, m.Role, m.Content, m.Reasoning, m.ToolName, m.ToolCallId,
                    toolCalls = m.ToolCallsJson is null ? null : JsonNode.Parse(m.ToolCallsJson),
                    files = ChatService.ParseIds(m.AttachmentsJson).Select(f => names.GetValueOrDefault(f, f.ToString())),
                    status = m.Status.ToString().ToLowerInvariant(), m.Error, m.Model, m.PromptTokens, m.CompletionTokens, m.CreatedAt,
                }),
            }, ct);
            await TextAsync(zip, stem + ".md", Markdown(c, messages, names), ct);
        }

        // One file at a time: a video can be large.
        foreach (var (id, name) in names)
        {
            var a = await db.ChatAttachments.AsNoTracking().SingleAsync(x => x.Id == id, ct);
            if (a.Data is { } data)
            {
                await BytesAsync(zip, name, data, ct);
            }
            if (a.Text.Length > 0)
            {
                await TextAsync(zip, a.Data is null && name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ? name : name + ".txt", a.Text, ct);
            }
        }
    }

    private static string Readme(AppUser user, bool hidden) =>
        $"""
        The data of {user.DisplayName} ({user.UserName}, {user.Email}), exported {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm} UTC.

        person.json     the profile, preferences and groups
        assistants.json the assistants they made: instructions, settings and the files they hold
        tasks.json      scheduled tasks (a webhook's address is a secret: only whether one is set)
        chats/          every chat: .json has every branch and message, .md the branch on screen
        files/          every file: the file itself, and the text the model read (.txt)
        {(hidden ? "\nChats with a deletedAt were deleted by the person while on legal hold, and kept.\n" : "")}
        """;

    /// <summary>The branch on screen, for reading: questions, answers, and the tools that were used.</summary>
    private static string Markdown(Conversation c, List<ChatMessage> messages, Dictionary<Guid, string> names)
    {
        var md = new StringBuilder().Append("# ").AppendLine(c.Title).AppendLine();
        md.Append('_').Append(c.CreatedAt.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)).Append(" UTC");
        if (c.DeletedAt is { } deleted)
        {
            md.Append(" · deleted ").Append(deleted.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)).Append(" under legal hold");
        }
        md.AppendLine("_").AppendLine();
        foreach (var m in ChatService.PathTo(messages.ToDictionary(m => m.Id), c.CurrentLeafId))
        {
            switch (m.Role)
            {
                case "user":
                    md.AppendLine("## You").AppendLine().AppendLine(m.Content);
                    foreach (var f in ChatService.ParseIds(m.AttachmentsJson))
                    {
                        md.Append("- attached: ").AppendLine(names.GetValueOrDefault(f, f.ToString()));
                    }
                    md.AppendLine();
                    break;
                case "assistant" when m.Content.Length > 0 || m.Error is not null:
                    md.Append("## Assistant").AppendLine(m.Model is null ? "" : $" ({m.Model})").AppendLine().AppendLine(m.Content.Length > 0 ? m.Content : $"_{m.Error}_").AppendLine();
                    break;
                case "tool":
                    md.Append("_used ").Append(m.ToolName).AppendLine("_").AppendLine();
                    break;
            }
        }
        return md.ToString();
    }

    /// <summary>A name safe in a zip on any system.</summary>
    private static string Safe(string name, int max = 120)
    {
        var chars = name.Trim().Select(ch => char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_' or ' ' ? ch : '_').ToArray();
        var safe = new string(chars).Trim().Replace(' ', '-');
        safe = safe.Length > max ? safe[..max] : safe;
        return safe.Length == 0 || safe.All(ch => ch == '.') ? "file" : safe;
    }

    private static Task JsonAsync(ZipArchive zip, string name, object value, CancellationToken ct) =>
        BytesAsync(zip, name, JsonSerializer.SerializeToUtf8Bytes(value, Json), ct);

    private static Task TextAsync(ZipArchive zip, string name, string text, CancellationToken ct) =>
        BytesAsync(zip, name, Encoding.UTF8.GetBytes(text), ct);

    private static async Task BytesAsync(ZipArchive zip, string name, byte[] bytes, CancellationToken ct)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
        await using var stream = await entry.OpenAsync(ct);
        await stream.WriteAsync(bytes, ct);
    }
}
