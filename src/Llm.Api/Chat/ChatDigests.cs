using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Llm.Api.Chat.Tools;
using Llm.Core.Chat;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Chat;

/// <summary>
/// Each chat and Code Arena session gets a digest as it goes quiet, for finding it again (search_history): what it is about,
/// what was asked, done and decided, the names that matter and how it ended. The model for small steps writes it anew from
/// the digest before and what came after; its vector lets a question find it by meaning. Leader only, as memory is on for
/// the person; a few chats each pass, the most recent first, so older ones are caught up over time.
/// </summary>
public sealed partial class ChatDigests(IServiceScopeFactory scopes, Operations.Replicas replicas, TimeProvider clock, IConfiguration config, ILogger<ChatDigests> logger)
    : BackgroundService
{
    /// <summary>How often it looks, how long a chat stays quiet first, and how many chats one pass reads.</summary>
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(5), Quiet = TimeSpan.FromMinutes(10);

    public const int MostChats = 10;

    /// <summary>What of a chat's new messages one digest reads (its end, past that), and of each answer.</summary>
    public const int MostChars = 12_000, AnswerChars = 600;

    /// <summary>For the gateway's and the bill's tags.</summary>
    public const string Tag = "digest";

    public const string Instructions =
        "You keep the digest of a conversation between a person and an AI assistant, so it can be found again later by what it was about. " +
        "Given the digest so far (if any) and what came after it, write the digest anew. First line: what the conversation is about, as a " +
        "short title. Then a few short lines: what was asked, what was done or made, what was decided (and why), the names that matter " +
        "(systems, files, products, numbers, people's roles), and how it ended or what is left. At most 150 words, in the person's language. " +
        "Write only the digest.";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Settings → Chat → Digests of past chats.
        if (bool.TryParse(config["Memory:Digests"], out var on) && !on)
        {
            return;
        }
        using var timer = new PeriodicTimer(Every, clock);
        do
        {
            if (!replicas.IsLeader)
            {
                continue;
            }
            try
            {
                await RunAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or Npgsql.NpgsqlException)
            {
                LogFailed(logger, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>One pass: the quiet chats with something new digested; how many were.</summary>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<AppDbContext>();
        var memories = services.GetRequiredService<Memories>();
        if (!memories.Enabled)
        {
            return 0;
        }
        var quiet = clock.GetUtcNow() - Quiet;
        var chats = await db.Conversations
            .Where(c => c.Kind != RoomMember.Room && c.DeletedAt == null && c.UpdatedAt <= quiet
                && db.ChatMessages.Any(m => m.ConversationId == c.Id && m.Role == "user" && m.Sequence > c.DigestedSequence))
            .OrderByDescending(c => c.UpdatedAt).Take(MostChats).ToListAsync(ct);
        var done = 0;
        foreach (var chat in chats)
        {
            var last = await db.ChatMessages.Where(m => m.ConversationId == chat.Id).MaxAsync(m => (int?)m.Sequence, ct) ?? 0;
            if (await db.Users.FindAsync([chat.UserId], ct) is not { } person || !memories.On(person))
            {
                chat.DigestedSequence = last;
                continue;
            }
            try
            {
                if (await DigestAsync(services, db, memories, chat, person, ct) is { } digest)
                {
                    chat.Digest = digest;
                    chat.DigestEmbedding = await memories.DocumentVectorAsync(Searched(chat), ct);
                    done++;
                }
                chat.DigestedSequence = last;
            }
            catch (Exception ex) when (ex is ChatGatewayException or HttpRequestException)
            {
                // Digested at the next pass (the model was away).
                LogChatFailed(logger, chat.Id, ex);
            }
        }
        await db.SaveChangesAsync(ct);
        return done;
    }

    /// <summary>What a chat is found by: its title and its digest.</summary>
    public static string Searched(Conversation chat) => $"{chat.Title}\n{chat.Digest}";

    /// <summary>The digest anew, from the one before and the messages after it; null when there is nothing to say (or it was refused).</summary>
    private static async Task<string?> DigestAsync(IServiceProvider services, AppDbContext db, Memories memories, Conversation chat, AppUser person, CancellationToken ct)
    {
        var said = await db.ChatMessages.AsNoTracking()
            .Where(m => m.ConversationId == chat.Id && m.Sequence > chat.DigestedSequence && (m.Role == "user" || m.Role == "assistant") && m.Content != "")
            .OrderBy(m => m.Sequence).Select(m => new { m.Role, m.Content }).ToListAsync(ct);
        if (said.Where(m => m.Role == "user").Sum(m => m.Content.Length) < 20)
        {
            return null;
        }
        var text = new StringBuilder();
        foreach (var m in said)
        {
            var content = m.Role == "assistant" && m.Content.Length > AnswerChars ? m.Content[..AnswerChars] + " …" : m.Content;
            text.Append(m.Role == "user" ? "The person: " : "The assistant: ").Append(content).Append("\n\n");
        }
        var conversation = text.Length > MostChars ? "…" + text.ToString()[^MostChars..] : text.ToString();
        var question = new StringBuilder();
        if (chat.Digest is { Length: > 0 } before)
        {
            question.Append("The digest so far:\n").Append(before).Append("\n\nWhat came after it:\n\n");
        }
        else
        {
            question.Append(chat.Origin == CodeArenaSync.Origin ? $"A coding session in {chat.OriginPlace ?? chat.ProjectKey ?? "a project"}" : "The conversation")
                .Append(string.IsNullOrWhiteSpace(chat.Title) ? "" : $", titled \"{chat.Title}\"").Append(":\n\n");
        }
        question.Append(conversation);
        var asked = await services.GetRequiredService<OneQuestion>().AskAsync(person, question.ToString(), Instructions, "small", 400, Tag, ct);
        return asked.Problem is null && asked.Text?.Trim() is { Length: > 0 } digest ? digest[..Math.Min(digest.Length, 4_000)] : null;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Chat digests failed")]
    private static partial void LogFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Digest of chat {Chat} failed: tried again next pass")]
    private static partial void LogChatFailed(ILogger logger, Guid chat, Exception ex);
}

/// <summary>A past chat found for a question: how near it is (0 to 1), and where its words were.</summary>
public sealed record PastChat(Conversation Chat, double Score);

/// <summary>
/// The person's past chats and Code Arena sessions, found by what they were about: nearest by meaning to the question
/// (their digests' vectors), else by its words in their titles and digests. The chat's search_history and Code Arena's.
/// </summary>
public sealed class PastChats(AppDbContext db, Memories memories)
{
    /// <summary>The most chats one search gives, and how near by meaning one must be (beside the nearest).</summary>
    public const int Most = 8;
    public const double Near = 0.45, Spread = 0.12;

    public async Task<List<PastChat>> SearchAsync(AppUser me, string query, CancellationToken ct, Guid? besides = null)
    {
        var terms = HistoryWords.Terms(query);
        var vector = await memories.QueryVectorAsync(query, ct);
        var chats = await db.Conversations.AsNoTracking()
            .Where(c => c.UserId == me.Id && c.DeletedAt == null && c.Kind != RoomMember.Room && c.Id != besides)
            .Select(c => new { Chat = c, c.DigestEmbedding }).ToListAsync(ct);
        var scored = chats.Select(c =>
        {
            var words = terms.Count == 0 ? 0 : HistoryWords.Hits(ChatDigests.Searched(c.Chat), terms) / (double)terms.Count;
            double? meaning = vector is not null && c.DigestEmbedding is { Length: > 0 } v && v.Length == vector.Length ? Memories.Dot(vector, v) : null;
            // By meaning when it can; the words lift a chat that names what was asked for, and find one with no vector alone.
            return (Past: new PastChat(c.Chat, Math.Max(meaning ?? 0, 0) + 0.15 * words), Meaning: meaning, Words: words);
        }).ToList();
        var top = scored.Where(p => p.Meaning is not null).Select(p => p.Past.Score).DefaultIfEmpty(0).Max();
        var bar = Math.Max(Near, top - Spread);
        return [.. scored.Where(p => p.Meaning is not null ? p.Past.Score >= bar : p.Words > 0).Select(p => p.Past)
            .OrderByDescending(p => p.Score).ThenByDescending(p => p.Chat.UpdatedAt).Take(Most)];
    }

    /// <summary>How near by meaning a past chat must be to be named with a question (and above the typical, and near the best), and how many.</summary>
    public const double RelatedNear = 0.55, RelatedStand = 0.12, RelatedSpread = 0.08;
    public const int MostRelated = 2;

    /// <summary>
    /// The past chats a question bears on, by their digests' meaning alone (nothing without the embedder): those well above
    /// the typical chat's score and near the best, at most two. Words are not enough here: a note on every question that shares
    /// a word with an old chat would be noise.
    /// </summary>
    public async Task<List<PastChat>> RelatedAsync(AppUser me, string question, Guid besides, CancellationToken ct)
    {
        if (question.Trim().Length < 12 || await memories.QueryVectorAsync(question, ct) is not { } vector)
        {
            return [];
        }
        var chats = await db.Conversations.AsNoTracking()
            .Where(c => c.UserId == me.Id && c.DeletedAt == null && c.Kind != RoomMember.Room && c.Id != besides && c.DigestEmbedding != null)
            .ToListAsync(ct);
        var scored = chats.Where(c => c.DigestEmbedding!.Length == vector.Length).Select(c => new PastChat(c, Memories.Dot(vector, c.DigestEmbedding!))).ToList();
        if (scored.Count == 0)
        {
            return [];
        }
        var sorted = scored.Select(p => p.Score).Order().ToList();
        var typical = sorted.Count >= 5 ? sorted[sorted.Count / 2] : 0;
        var bar = Math.Max(RelatedNear, Math.Max(typical + RelatedStand, sorted[^1] - RelatedSpread));
        return [.. scored.Where(p => p.Score >= bar).OrderByDescending(p => p.Score).Take(MostRelated)];
    }

    /// <summary>What goes with a question about the past chats it bears on: each one's id, title, when, and its digest's start.</summary>
    public static string Note(IReadOnlyList<PastChat> related) =>
        "(Past chats of the person's that may bear on this, by their digests; open_chat reads one when it matters:" +
        string.Concat(related.Select(p => $"\n- [{p.Chat.Id}] \"{p.Chat.Title}\" (last {p.Chat.UpdatedAt.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}): " +
            Fmt(p.Chat.Digest))) + ")";

    private static string Fmt(string? digest)
    {
        var text = (digest ?? "").ReplaceLineEndings(" ").Trim();
        return text.Length > 240 ? text[..240] + "…" : text;
    }

    /// <summary>search_history's answer: the chats found, each with its id and digest.</summary>
    public async Task<ToolResult> FindAsync(AppUser me, string query, CancellationToken ct, Guid? besides = null)
    {
        if (query.Length == 0)
        {
            return new ToolResult("Say what the earlier work was about: {\"query\":\"…\"}.", IsError: true);
        }
        var found = await SearchAsync(me, query, ct, besides);
        return found.Count == 0
            ? new ToolResult("No past chat or Code Arena session of the person's is about that (those not digested yet are found by their words only).")
            : new ToolResult(string.Join("\n\n", found.Select(f => Line(f.Chat))) + "\n\nopen_chat reads one (with a query, the messages that hold its words).");
    }

    /// <summary>A past chat as search_history and open_chat give it: its id, title, when, where, and its digest.</summary>
    private static string Line(Conversation c) =>
        $"[{c.Id}] \"{c.Title}\" ({(c.Origin == CodeArenaSync.Origin ? $"Code Arena session{(c.OriginPlace is { Length: > 0 } place ? $" in {place}" : "")}" : "chat")}, " +
        $"last {c.UpdatedAt.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}, /chat/{c.Id})\n" + (c.Digest ?? "(no digest yet)");

    /// <summary>open_chat's answer: one of the person's chats, its digest, and the messages a query finds or a range of them (args from and to).</summary>
    public async Task<ToolResult> OpenAsync(AppUser me, string? chatId, JsonObject args, CancellationToken ct)
    {
        if (!Guid.TryParse(chatId?.Trim('[', ']', ' '), out var id)
            || await db.Conversations.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id && c.UserId == me.Id && c.DeletedAt == null && c.Kind != RoomMember.Room, ct) is not { } chat)
        {
            return new ToolResult("There is no such chat of the person's: give an id search_history gave.", IsError: true);
        }
        var branch = await ChatHistoryTool.BranchAsync(db, chat, ct);
        var head = Line(chat) + $"\n({branch.Count} messages.)";
        if (args["query"] is JsonValue q && q.TryGetValue<string>(out var query) && query.Trim().Length > 0)
        {
            return new ToolResult(head + "\n\n" + ChatHistoryTool.Search(branch, query.Trim(), "that chat", "open_chat with from").Text);
        }
        if (ChatHistoryTool.Int(args, "from") is not null)
        {
            var read = ChatHistoryTool.Read(branch, args);
            return read.IsError ? read : new ToolResult(head + "\n\n" + read.Text);
        }
        return new ToolResult(head + "\n\nWith query, open_chat finds messages in it; with from (and to), it reads them by number.");
    }
}
