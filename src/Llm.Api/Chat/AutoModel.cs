using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Llm.Api.Gateway;
using Llm.Core.Chat;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Chat;

/// <summary>Who answers a question on Auto, and why: the small model itself, or the chat's main model with a thinking level.</summary>
/// <param name="Kind">
/// What the small model said the question is: chat, lookup, code, reasoning or research. Or, not asked: files (attachments),
/// deep (deep research), thinking (the person chose a level), unknown (the small model could not say), unavailable (no small model for this person now).
/// </param>
/// <param name="Reason">Why, in a few words (the small model's own, or the app's).</param>
/// <param name="Model">The model that answers.</param>
/// <param name="Main">The chat's main model: "Ask the big model" answers again with it.</param>
/// <param name="Thinking">The thinking level the answer gets; null: the chat's own.</param>
/// <param name="Small">The small model answers.</param>
public sealed record AutoRoute(string Kind, string Reason, string Model, string Main, string? Thinking, bool Small)
{
    /// <summary>As it is kept with the answer (its first message's details) and sent to the page.</summary>
    public JsonObject ToJson() => new()
    {
        ["kind"] = Kind, ["reason"] = Reason, ["model"] = Model, ["main"] = Main, ["thinking"] = Thinking, ["small"] = Small,
    };
}

/// <summary>
/// Auto, a choice in the chat's model menu while a model for small steps is set: the small
/// model sorts the question in one short call (small talk, a quick lookup or rewrite, code,
/// reasoning, research) and answers the easy ones itself, without thinking; the rest go to
/// the chat's main model, with a thinking level set by how hard they are. The answer keeps
/// who answered and why, and "Ask the big model" answers it again with the main model.
/// </summary>
public sealed partial class AutoModel(AppDbContext db, GatewayChat gateway, Safeguards.Safeguards safeguards, ChatModels models, IOptionsMonitor<ChatOptions> chat,
    ILogger<AutoModel> logger)
{
    internal const string SortPrompt =
        "You sort the questions people send to an AI assistant, to choose who answers: a small fast model, or a big one that thinks. The kinds:\n" +
        "- chat: a greeting, thanks or small talk\n" +
        "- lookup: a quick fact or definition, a translation, or rewording a short text\n" +
        "- code: writing, reading, fixing or explaining code\n" +
        "- reasoning: maths, logic, planning, a decision with trade-offs, or anything else that needs careful thought\n" +
        "- research: searching code, the web or documents, comparing many sources, or a long report\n" +
        "When unsure between two kinds, choose the later one. Reply with JSON only: {\"kind\": the kind, \"reason\": why, in at most eight words}.";

    /// <summary>The kinds the small model answers itself.</summary>
    private static readonly HashSet<string> Easy = ["chat", "lookup"];

    private static readonly HashSet<string> Kinds = ["chat", "lookup", "code", "reasoning", "research"];

    /// <summary>Whether this answer is on Auto: the chat chose it (or chose no model while Auto is the default), and no other model was asked for.</summary>
    public bool IsAuto(Conversation conversation, AnswerOverrides overrides) =>
        (overrides.Model ?? conversation.Model) is var chosen && (chosen == SmallModel.Auto || (chosen is null && models.AutoByDefault));

    /// <summary>
    /// Who answers <paramref name="question"/>: the small model (<paramref name="small"/>, for this
    /// person, or null) for an easy question, else <paramref name="main"/>. Questions with files and
    /// deep research go to the main model unasked; so does an answer with a thinking level chosen.
    /// </summary>
    public async Task<AutoRoute> RouteAsync(AppUser user, ChatMessage question, AnswerOverrides overrides, GatewayModel? small, string main, CancellationToken ct)
    {
        AutoRoute Main(string kind, string reason, string? thinking = null) => new(kind, reason, main, main, thinking, false);
        if (small is null)
        {
            return Main("unavailable", "The model for small steps is not available to you now.");
        }
        if (overrides.Research)
        {
            return Main("deep", "Deep research needs the main model.");
        }
        if (overrides.Thinking is not null)
        {
            return Main("thinking", "You chose how hard it thinks.");
        }
        if (ChatService.ParseIds(question.AttachmentsJson).Any())
        {
            return Main("files", "Files are attached: the main model reads them.");
        }
        var (kind, reason) = await SortAsync(user, question, small, ct);
        return kind switch
        {
            _ when Easy.Contains(kind) => new(kind, reason, small.Name, main, SmallModel.NoThinking(small), true),
            "code" => Main(kind, reason, Level(3)),
            "reasoning" => Main(kind, reason, Level(5)),
            "research" => Main(kind, reason, Level(2)),
            _ => Main("unknown", reason),
        };
    }

    /// <summary>The small model's word on the question, with the exchange before it for context; "unknown" with why when it gives none.</summary>
    private async Task<(string Kind, string Reason)> SortAsync(AppUser user, ChatMessage question, GatewayModel small, CancellationToken ct)
    {
        var ask = new StringBuilder();
        var mask = await safeguards.MaskerAsync(user.Email ?? "", ct);
        var earlier = await EarlierAsync(question, ct);
        if (earlier.Count > 0)
        {
            ask.Append("The conversation so far, for context:\n<earlier>\n");
            foreach (var m in earlier)
            {
                ask.Append(m.Role == "user" ? "Person: " : "Assistant: ").Append(Cut(mask(m.Content), 400)).Append('\n');
            }
            ask.Append("</earlier>\n\n");
        }
        ask.Append("The question to sort:\n<question>\n").Append(Cut(mask(question.Content), 4000)).Append("\n</question>");
        var request = new JsonObject
        {
            ["model"] = small.Name,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "system", ["content"] = SortPrompt }, new JsonObject { ["role"] = "user", ["content"] = ask.ToString() }),
            ["stream"] = true, ["max_tokens"] = 80, ["temperature"] = 0, ["user"] = user.Email!.ToLowerInvariant(),
        };
        if (ThinkingPresets.TemplateKwargs(SmallModel.NoThinking(small)) is { } kwargs)
        {
            request["chat_template_kwargs"] = kwargs;
        }
        try
        {
            var said = new StringBuilder();
            await foreach (var e in gateway.StreamAsync(request, user.Email!, ct))
            {
                if (e is ContentDelta c)
                {
                    said.Append(c.Text);
                }
            }
            var text = said.ToString();
            var (start, end) = (text.IndexOf('{'), text.LastIndexOf('}'));
            if (start >= 0 && end > start && JsonNode.Parse(text[start..(end + 1)]) is JsonObject json
                && json["kind"]?.ToString().Trim().ToLowerInvariant() is { } kind && Kinds.Contains(kind))
            {
                var reason = json["reason"]?.ToString().Trim() is { Length: > 0 } r ? Cut(r, 120) : "";
                return (kind, reason);
            }
            return ("unknown", "The small model could not sort the question.");
        }
        catch (Exception ex) when (ex is ChatGatewayException or JsonException or InvalidOperationException or HttpRequestException)
        {
            LogSortFailed(logger, ex.Message);
            return ("unknown", "The small model did not answer.");
        }
    }

    /// <summary>The question and answer before this question on its branch (the newest words of each), oldest first.</summary>
    private async Task<List<ChatMessage>> EarlierAsync(ChatMessage question, CancellationToken ct)
    {
        var found = new List<ChatMessage>();
        var id = question.ParentId;
        // Up the branch past tool rounds, to the answer's words and the question before it.
        for (var step = 0; id is { } current && step < 12 && found.Count < 2; step++)
        {
            var m = await db.ChatMessages.AsNoTracking().Where(x => x.Id == current).Select(x => new { x.Role, x.Content, x.ParentId }).SingleOrDefaultAsync(ct);
            if (m is null)
            {
                break;
            }
            if (m.Content.Length > 0 && (m.Role == "user" || (m.Role == "assistant" && found.All(f => f.Role != "assistant"))))
            {
                found.Add(new ChatMessage { Role = m.Role, Content = m.Content });
            }
            if (m.Role == "user")
            {
                break;
            }
            id = m.ParentId;
        }
        found.Reverse();
        return found;
    }

    /// <summary>
    /// A thinking level of the chat's own (Chat:ThinkingPresets) nearest to how hard a question is:
    /// 2 light, 3 some, 5 the deepest there is. Null when the chat offers none but off.
    /// </summary>
    private string? Level(int hard)
    {
        static int Rank(string level) => level switch
        {
            "minimal" => 1,
            "low" => 2,
            "medium" => 3,
            "high" => 4,
            "xhigh" => 5,
            _ => 3,
        };
        return ThinkingPresets.Parse(chat.CurrentValue.ThinkingPresets).Select(p => p.Level).Where(l => l != "off")
            .OrderBy(l => Math.Abs(Rank(l) - hard)).ThenByDescending(Rank).FirstOrDefault();
    }

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    [LoggerMessage(Level = LogLevel.Warning, Message = "Auto could not sort a question, so the main model answers: {Reason}")]
    private static partial void LogSortFailed(ILogger logger, string reason);
}
