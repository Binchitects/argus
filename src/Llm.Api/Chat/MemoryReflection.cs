using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Llm.Core.Chat;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Chat;

/// <summary>
/// Memories that form themselves: when a chat (the web's, or a Code Arena session kept in step) has gone quiet, the model
/// for small steps reads what it said since memory last read it, beside what is remembered already, and proposes what is
/// worth keeping — preferences, facts, decisions, how things are done, lessons. In the chat they wait for the person
/// (Memory, and a word in the bell), unless they keep them on their own; in a Code Arena session a project's are kept and
/// said. A proposal that says otherwise than a memory replaces it once kept. On the replica that leads, every few minutes.
/// </summary>
public sealed partial class MemoryReflection(IServiceScopeFactory scopes, Operations.Replicas replicas, TimeProvider clock, ILogger<MemoryReflection> logger) : BackgroundService
{
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(5);

    /// <summary>A chat this long without a new message has gone quiet.</summary>
    public static readonly TimeSpan Quiet = TimeSpan.FromMinutes(10);

    /// <summary>Chats older than this are not read any more (what they said is read when they come back).</summary>
    public static readonly TimeSpan Recent = TimeSpan.FromDays(3);

    /// <summary>Chats read in one pass at most; what a chat gives the model at most (its newest part).</summary>
    public const int MostChats = 10, MostChars = 12_000;

    /// <summary>Memories proposed from one chat at most.</summary>
    public const int MostProposed = 5;

    public const string Tag = "memory";

    /// <summary>How the model is asked (the tests' fake model knows it by its start).</summary>
    public const string Instructions =
        "You keep what an AI assistant remembers about a person across their chats. Read the conversation and propose only what is worth " +
        "remembering in later chats: lasting preferences (how they like answers, their tools and languages), facts about them or their work " +
        "(role, team, projects, the stack), decisions with their reasons, how things are done (commands to build, test or deploy that worked), " +
        "and lessons (what went wrong, or what they corrected). Never secrets, passwords or keys, health, other people's personal details, " +
        "what matters only to this conversation (what they asked for here, a question, a summary or a draft they wanted), or what is " +
        "remembered already (listed with ids). One memory for one thing: a decision with its reason in the same memory, never two that " +
        "say the same in other words. When the conversation says otherwise " +
        "than a kept memory, propose the new one with \"replaces\": that memory's id. One or two short sentences each, about the person or " +
        "their work, in the person's language. A project's memory (scope project) is about the code base they worked in here, only when " +
        "the conversation is a coding session. Answer with JSON only: {\"memories\":[{\"text\":\"…\",\"kind\":\"preference|fact|decision|howto|lesson\"," +
        "\"scope\":\"person|project\",\"replaces\":null}]} — an empty list when there is nothing worth keeping; at most 5.";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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

    /// <summary>One pass: the quiet chats with something new read, and what they proposed; how many chats were read.</summary>
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
        var now = clock.GetUtcNow();
        var (quiet, recent) = (now - Quiet, now - Recent);
        var chats = await db.Conversations
            .Where(c => c.Kind != RoomMember.Room && c.ScheduledTaskId == null && c.DeletedAt == null && c.UpdatedAt <= quiet && c.UpdatedAt >= recent
                && db.ChatMessages.Any(m => m.ConversationId == c.Id && m.Role == "user" && m.Sequence > c.ReflectedSequence))
            .OrderBy(c => c.UpdatedAt).Take(MostChats).ToListAsync(ct);
        var read = 0;
        foreach (var chat in chats)
        {
            if (await db.Users.FindAsync([chat.UserId], ct) is not { } person || !memories.On(person))
            {
                chat.ReflectedSequence = await LastAsync(db, chat.Id, ct);
                continue;
            }
            try
            {
                await ReflectAsync(services, db, memories, chat, person, ct);
                read++;
            }
            catch (Exception ex) when (ex is ChatGatewayException or JsonException or HttpRequestException)
            {
                // Read again at the next pass (the model was away, or answered in words).
                LogChatFailed(logger, chat.Id, ex);
            }
        }
        await db.SaveChangesAsync(ct);
        return read;
    }

    /// <summary>The chat's last message, by its sequence (0: none).</summary>
    private static async Task<int> LastAsync(AppDbContext db, Guid chat, CancellationToken ct) =>
        await db.ChatMessages.Where(m => m.ConversationId == chat).MaxAsync(m => (int?)m.Sequence, ct) ?? 0;

    private static async Task ReflectAsync(IServiceProvider services, AppDbContext db, Memories memories, Conversation chat, AppUser person, CancellationToken ct)
    {
        var said = await db.ChatMessages.AsNoTracking()
            .Where(m => m.ConversationId == chat.Id && m.Sequence > chat.ReflectedSequence && (m.Role == "user" || m.Role == "assistant") && m.Content != "")
            .OrderBy(m => m.Sequence).Select(m => new { m.Role, m.Content, m.Sequence }).ToListAsync(ct);
        var last = await LastAsync(db, chat.Id, ct);
        if (said.Count == 0 || said.Where(m => m.Role == "user").Sum(m => m.Content.Length) < 20)
        {
            chat.ReflectedSequence = last;
            return;
        }
        var text = new StringBuilder();
        foreach (var m in said)
        {
            var content = m.Role == "assistant" && m.Content.Length > 800 ? m.Content[..800] + " …" : m.Content;
            text.Append(m.Role == "user" ? "The person: " : "The assistant: ").Append(content).Append("\n\n");
        }
        var conversation = text.Length > MostChars ? "…" + text.ToString()[^MostChars..] : text.ToString();
        var known = await memories.RecallAsync(person, conversation[Math.Max(0, conversation.Length - 3_000)..], ct, chat.ProjectKey, budget: 4_000, most: 30);
        var kept = known.Select(r => r.Memory).ToList();
        var question = new StringBuilder();
        if (kept.Count > 0)
        {
            question.Append("Remembered already:\n").AppendJoin('\n', kept.Select(m => $"- [{m.Id}] {Memories.Line(m)}")).Append("\n\n");
        }
        question.Append(chat.Origin == CodeArenaSync.Origin ? $"A coding session in {chat.ProjectKey ?? "a project"}:\n\n" : "The conversation:\n\n").Append(conversation);
        var asked = await services.GetRequiredService<OneQuestion>().AskAsync(person, question.ToString(), Instructions, "small", 1_500, Tag, ct);
        if (asked.Problem is not null || asked.Text is not { } answer)
        {
            // Refused (no model, the safeguards): not asked again for what is there now.
            chat.ReflectedSequence = last;
            return;
        }
        var (start, end) = (answer.IndexOf('{'), answer.LastIndexOf('}'));
        var proposals = start < 0 || end <= start ? [] : (JsonNode.Parse(answer[start..(end + 1)])?["memories"] as JsonArray ?? []).OfType<JsonObject>().Take(MostProposed).ToList();
        var waiting = new List<string>();
        var keptNow = new List<string>();
        var fromCodeArena = chat.Origin == CodeArenaSync.Origin;
        foreach (var p in proposals)
        {
            var (words, problem) = Memories.Check(p["text"]?.GetValue<string>());
            var kind = p["kind"]?.GetValue<string>() is { } k && Memory.Kinds.Contains(k) ? k : Memory.Fact;
            var project = p["scope"]?.GetValue<string>() == Memory.Project && chat.ProjectKey is { Length: > 0 } key ? key : null;
            var replaces = Guid.TryParse(p["replaces"]?.ToString(), out var r) && kept.Any(m => m.Id == r && m.UserId == person.Id) ? r : (Guid?)null;
            if (problem is not null)
            {
                continue;
            }
            // A Code Arena session's project memories are kept, and said; the rest wait for the person unless they keep them on their own.
            var proposed = !(person.MemoryAuto || (fromCodeArena && project is not null));
            var facts = new MemoryFacts(project is null ? Memory.Person : Memory.Project, kind, project, Memory.Reflected, chat.Id, Proposed: proposed, Replaces: replaces);
            if (await memories.KeepAsync(person.Id, words, facts, ct) is not { } made || (made.Proposed != proposed))
            {
                continue;
            }
            (proposed ? waiting : keptNow).Add(words);
        }
        chat.ReflectedSequence = last;
        if (waiting.Count + keptNow.Count == 0)
        {
            return;
        }
        var title = string.IsNullOrWhiteSpace(chat.Title) ? "a chat" : $"“{chat.Title}”";
        var news = waiting.Count > 0
            ? new Notifications.News(Tag, waiting.Count == 1 ? $"Remember this from {title}?" : $"Remember {waiting.Count} things from {title}?",
                string.Join(" · ", waiting), "/memory", $"memory-reflect:{chat.Id}:{last}")
            : new Notifications.News(Tag, keptNow.Count == 1 ? $"Remembered from {title}" : $"Remembered {keptNow.Count} things from {title}",
                string.Join(" · ", keptNow), "/memory", $"memory-reflect:{chat.Id}:{last}");
        await services.GetRequiredService<Notifications.Notifier>().SendAsync(person.Id, news, ct);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Memory reflection failed")]
    private static partial void LogFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Memory reflection on chat {Chat} failed: read again next pass")]
    private static partial void LogChatFailed(ILogger logger, Guid chat, Exception ex);
}
