using System.Security.Claims;
using Llm.Api.Endpoints;
using Llm.Core.Chat;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Chat;

/// <summary>One place a search found its words: a chat's title, or one of its messages.</summary>
/// <param name="Where">title, prompt (the person's question), answer, tool (a tool's result), file (an attachment's name).</param>
public sealed record SearchHit(Guid ConversationId, string Title, bool Archived, Guid? MessageId, string Where, string Snippet, DateTimeOffset At, string? Model);

public static partial class ChatEndpoints
{
    private static readonly string[] Places = ["title", "prompt", "answer", "tool", "file"];

    /// <summary>
    /// Advanced search in one's own chats: words in titles, questions, answers, tools'
    /// results or attachments' names; archived chats too when asked; a time range and a
    /// model. Newest first, a snippet around the words for each.
    /// </summary>
    private static async Task<IResult> SearchAsync(ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, string? q, string? @in, bool archived = true,
        DateTimeOffset? from = null, DateTimeOffset? to = null, string? model = null, int limit = 60, CancellationToken ct = default)
    {
        var me = await Me(p, users);
        var words = (q ?? "").Trim();
        if (words.Length < 2)
        {
            return AuthEndpoints.Problem(400, "query", "Search for two letters or more.");
        }
        var places = string.IsNullOrWhiteSpace(@in) || @in == "all" ? Places : @in.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (places.FirstOrDefault(x => !Places.Contains(x)) is { } unknown)
        {
            return AuthEndpoints.Problem(400, "in", $"Search in title, prompt, answer, tool or file, not {unknown}.");
        }
        limit = Math.Clamp(limit, 1, 200);
        var like = "%" + words.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
        var chats = db.Conversations.AsNoTracking().Where(c => c.UserId == me.Id && (archived || c.ArchivedAt == null));
        var hits = new List<SearchHit>();

        if (places.Contains("title"))
        {
            var titled = await chats.Where(c => EF.Functions.ILike(c.Title, like) && (from == null || c.UpdatedAt >= from) && (to == null || c.UpdatedAt <= to)
                    && (model == null || c.Model == model))
                .OrderByDescending(c => c.UpdatedAt).Take(limit).Select(c => new { c.Id, c.Title, c.ArchivedAt, c.UpdatedAt, c.Model }).ToListAsync(ct);
            hits.AddRange(titled.Select(c => new SearchHit(c.Id, c.Title, c.ArchivedAt != null, null, "title", c.Title, c.UpdatedAt, c.Model)));
        }

        var roles = places.Select(x => x switch { "prompt" => "user", "answer" => "assistant", "tool" => "tool", _ => null }).OfType<string>().ToList();
        if (roles.Count > 0)
        {
            var found = await db.ChatMessages.AsNoTracking()
                .Join(chats, m => m.ConversationId, c => c.Id, (m, c) => new { m, c })
                .Where(x => roles.Contains(x.m.Role) && EF.Functions.ILike(x.m.Content, like) && (from == null || x.m.CreatedAt >= from) && (to == null || x.m.CreatedAt <= to)
                    && (model == null || x.m.Model == model || (x.m.Role != "assistant" && x.c.Model == model)))
                .OrderByDescending(x => x.m.CreatedAt).Take(limit)
                .Select(x => new { x.m.Id, x.m.ConversationId, x.m.Role, x.m.Content, x.m.CreatedAt, x.m.Model, x.m.ToolName, x.c.Title, x.c.ArchivedAt })
                .ToListAsync(ct);
            hits.AddRange(found.Select(m => new SearchHit(m.ConversationId, m.Title, m.ArchivedAt != null, m.Id, m.Role switch { "user" => "prompt", "assistant" => "answer", _ => "tool" },
                Snippet(m.Content, words), m.CreatedAt, m.Model ?? m.ToolName)));
        }

        if (places.Contains("file"))
        {
            var named = await db.ChatAttachments.AsNoTracking().Where(a => a.UserId == me.Id && EF.Functions.ILike(a.FileName, like))
                .OrderByDescending(a => a.CreatedAt).Take(limit).Select(a => new { a.Id, a.FileName }).ToListAsync(ct);
            foreach (var a in named)
            {
                var key = a.Id.ToString();
                var where = await db.ChatMessages.AsNoTracking().Join(chats, m => m.ConversationId, c => c.Id, (m, c) => new { m, c })
                    .Where(x => x.m.AttachmentsJson != null && x.m.AttachmentsJson.Contains(key) && (from == null || x.m.CreatedAt >= from) && (to == null || x.m.CreatedAt <= to))
                    .OrderByDescending(x => x.m.CreatedAt)
                    .Select(x => new { x.m.Id, x.m.ConversationId, x.m.CreatedAt, x.c.Title, x.c.ArchivedAt }).FirstOrDefaultAsync(ct);
                if (where is not null)
                {
                    hits.Add(new SearchHit(where.ConversationId, where.Title, where.ArchivedAt != null, where.Id, "file", a.FileName, where.CreatedAt, null));
                }
            }
        }
        // A comparison's answers not voted on yet stay blind here too.
        var blind = await Quality.ChatQuality.BlindAsync(db, me.Id, hits.Select(h => h.ConversationId), ct);
        bool Blind(SearchHit h) => h.Where == "answer" && h.MessageId is { } m && blind.ContainsKey(m);
        return Results.Ok(hits.Where(h => model is null || !Blind(h))
            .Select(h => Blind(h) ? h with { Model = Quality.Arena.Label(blind[h.MessageId!.Value]) } : h)
            .OrderByDescending(h => h.At).Take(limit));
    }

    /// <summary>The words with some of what is around them, on one line.</summary>
    internal static string Snippet(string text, string words, int around = 90)
    {
        var at = text.IndexOf(words, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
        {
            at = 0;
        }
        var start = Math.Max(0, at - around);
        var end = Math.Min(text.Length, at + words.Length + around);
        var piece = text[start..end].ReplaceLineEndings(" ").Trim();
        return (start > 0 ? "…" : "") + piece + (end < text.Length ? "…" : "");
    }
}
