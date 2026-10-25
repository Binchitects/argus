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
        "the conversation is a coding session. An answer the person marked bad (said with the reason) is a lesson: what to do " +
        "differently. Skills too: when the conversation worked out a way of doing a task the person is likely to do again (several steps, " +
        "not one answer), propose one skill, written as instructions to an assistant, general enough to reuse (no one-off details), never " +
        "one the person has already (listed); and when a skill used here (given with its instructions) went wrong or the person corrected " +
        "what it did, propose its better version, whole. Answer with JSON only: {\"memories\":[{\"text\":\"…\",\"kind\":" +
        "\"preference|fact|decision|howto|lesson\",\"scope\":\"person|project\",\"replaces\":null}],\"skill\":{\"name\":\"kebab-case\"," +
        "\"title\":\"…\",\"description\":\"when to use it\",\"instructions\":\"…\"},\"improve\":[{\"skill\":\"its name\",\"instructions\":" +
        "\"the whole new instructions\",\"why\":\"what was wrong\"}]} — memories at most 5, an empty list when nothing is worth keeping; " +
        "skill null and improve empty unless there is one.";

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
            .Where(m => m.ConversationId == chat.Id && m.Sequence > chat.ReflectedSequence && (m.Role == "user" || m.Role == "assistant") && (m.Content != "" || m.ToolCallsJson != null))
            .OrderBy(m => m.Sequence).Select(m => new { m.Id, m.Role, m.Content, m.Sequence, m.Skill, m.ToolCallsJson }).ToListAsync(ct);
        // The answers the person marked bad, with why: lessons.
        var answerIds = said.Where(m => m.Role == "assistant").Select(m => m.Id).ToList();
        var bad = await db.AnswerFeedback.AsNoTracking().Where(f => f.ConversationId == chat.Id && !f.Up && answerIds.Contains(f.MessageId))
            .ToDictionaryAsync(f => f.MessageId, f => string.Join(": ", new[] { f.Reason, f.Comment }.Where(x => !string.IsNullOrWhiteSpace(x))), ct);
        // The skills used here: chosen with / (their call kept on the question), or by the model (use_skill).
        var usedNames = said.Select(m => SkillLibrary.Kept(m.Skill)?.Name).OfType<string>()
            .Concat(said.Where(m => m.ToolCallsJson?.Contains(Tools.SkillTool.Function, StringComparison.Ordinal) == true)
                .SelectMany(m => (JsonNode.Parse(m.ToolCallsJson!) as JsonArray ?? []).OfType<JsonObject>())
                .Where(c => c["function"]?["name"]?.GetValue<string>() == Tools.SkillTool.Function)
                .Select(c => JsonNode.Parse(c["function"]?["arguments"]?.GetValue<string>() ?? "{}")?["name"]?.GetValue<string>()).OfType<string>())
            .Select(n => n.Trim().TrimStart('/')).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var access = services.GetRequiredService<Access.AccessService>();
        var usable = await SkillEndpoints.UsableAsync(person, access, db, ct);
        var used = usable.Where(x => usedNames.Contains(x.Name, StringComparer.OrdinalIgnoreCase)).GroupBy(x => x.Name).Select(g => g.First()).ToList();
        var last = await LastAsync(db, chat.Id, ct);
        if (said.Count == 0 || said.Where(m => m.Role == "user").Sum(m => m.Content.Length) < 20)
        {
            chat.ReflectedSequence = last;
            return;
        }
        var text = new StringBuilder();
        foreach (var m in said.Where(m => m.Content.Length > 0))
        {
            var content = m.Role == "assistant" && m.Content.Length > 800 ? m.Content[..800] + " …" : m.Content;
            var skill = m.Role == "user" && SkillLibrary.Kept(m.Skill) is { } call ? $" (with the skill /{call.Name})" : "";
            text.Append(m.Role == "user" ? $"The person{skill}: " : "The assistant: ").Append(content).Append("\n\n");
            if (bad.TryGetValue(m.Id, out var why))
            {
                text.Append("(The person marked this answer bad").Append(why.Length > 0 ? $": {why}" : "").Append(".)\n\n");
            }
        }
        var conversation = text.Length > MostChars ? "…" + text.ToString()[^MostChars..] : text.ToString();
        var known = await memories.RecallAsync(person, conversation[Math.Max(0, conversation.Length - 3_000)..], ct, chat.ProjectKey, budget: 4_000, most: 30);
        var kept = known.Select(r => r.Memory).ToList();
        var question = new StringBuilder();
        question.Append("The person's skills already: ").AppendJoin(", ", usable.Select(x => "/" + x.Name).Distinct().Take(80)).Append("\n\n");
        foreach (var x in used)
        {
            question.Append("The skill /").Append(x.Name).Append(" used here, its instructions:\n").Append(x.Instructions.Length > 4_000 ? x.Instructions[..4_000] + " …" : x.Instructions).Append("\n\n");
        }
        if (kept.Count > 0)
        {
            question.Append("Remembered already:\n").AppendJoin('\n', kept.Select(m => $"- [{m.Id}] {Memories.Line(m)}")).Append("\n\n");
        }
        question.Append(chat.Origin == CodeArenaSync.Origin ? $"A coding session in {chat.ProjectKey ?? "a project"}:\n\n" : "The conversation:\n\n").Append(conversation);
        var asked = await services.GetRequiredService<OneQuestion>().AskAsync(person, question.ToString(), Instructions, "small", 3_000, Tag, ct);
        if (asked.Problem is not null || asked.Text is not { } answer)
        {
            // Refused (no model, the safeguards): not asked again for what is there now.
            chat.ReflectedSequence = last;
            return;
        }
        var (start, end) = (answer.IndexOf('{'), answer.LastIndexOf('}'));
        var parsed = start < 0 || end <= start ? null : JsonNode.Parse(answer[start..(end + 1)]);
        var proposals = (parsed?["memories"] as JsonArray ?? []).OfType<JsonObject>().Take(MostProposed).ToList();
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
        var title = string.IsNullOrWhiteSpace(chat.Title) ? "a chat" : $"“{chat.Title}”";
        await LearnSkillsAsync(services, db, chat, person, parsed, used, title, last, ct);
        if (waiting.Count + keptNow.Count == 0)
        {
            return;
        }
        var news = waiting.Count > 0
            ? new Notifications.News(Tag, waiting.Count == 1 ? $"Remember this from {title}?" : $"Remember {waiting.Count} things from {title}?",
                string.Join(" · ", waiting), "/memory", $"memory-reflect:{chat.Id}:{last}")
            : new Notifications.News(Tag, keptNow.Count == 1 ? $"Remembered from {title}" : $"Remembered {keptNow.Count} things from {title}",
                string.Join(" · ", keptNow), "/memory", $"memory-reflect:{chat.Id}:{last}");
        await services.GetRequiredService<Notifications.Notifier>().SendAsync(person.Id, news, ct);
    }

    /// <summary>
    /// The skill a chat worked out, and the better versions of those it used: each waiting for the person (Skills), or kept at
    /// once when they keep what is learned on their own; the bell says which.
    /// </summary>
    private static async Task LearnSkillsAsync(IServiceProvider services, AppDbContext db, Conversation chat, AppUser person, JsonNode? parsed, List<Skill> used,
        string title, int last, CancellationToken ct)
    {
        var keep = person.MemoryAuto;
        var said = new List<string>();
        if (parsed?["skill"] is JsonObject s && s["name"]?.GetValue<string>() is { } name)
        {
            var learned = new LearnedSkill(name.Trim().TrimStart('/'), s["title"]?.GetValue<string>() ?? name, s["description"]?.GetValue<string>() ?? "",
                s["instructions"]?.GetValue<string>() ?? "");
            if (await SkillLearning.LearnAsync(db, person.Id, learned, chat.Id, $"Worked out in {title}", keep, ct) is { } made)
            {
                said.Add($"/{made.Name}: {made.Title}");
            }
        }
        foreach (var i in (parsed?["improve"] as JsonArray ?? []).OfType<JsonObject>().Take(3))
        {
            var which = i["skill"]?.GetValue<string>()?.Trim().TrimStart('/');
            if (used.FirstOrDefault(x => string.Equals(x.Name, which, StringComparison.OrdinalIgnoreCase)) is { } skill
                && await SkillLearning.ImproveAsync(db, person.Id, skill, i["instructions"]?.GetValue<string>() ?? "", i["why"]?.GetValue<string>() ?? "", chat.Id, keep, ct) is not null)
            {
                said.Add($"/{skill.Name}, better");
            }
        }
        if (said.Count == 0)
        {
            return;
        }
        await db.SaveChangesAsync(ct);
        var news = new Notifications.News("skill",
            keep ? $"Learned from {title}" : said.Count == 1 ? $"A skill from {title}: keep it?" : $"{said.Count} skills from {title}: keep them?",
            string.Join(" · ", said), "/skills", $"skill-learn:{chat.Id}:{last}");
        await services.GetRequiredService<Notifications.Notifier>().SendAsync(person.Id, news, ct);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Memory reflection failed")]
    private static partial void LogFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Memory reflection on chat {Chat} failed: read again next pass")]
    private static partial void LogChatFailed(ILogger logger, Guid chat, Exception ex);
}
