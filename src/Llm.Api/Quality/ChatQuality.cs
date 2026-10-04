using System.Text.Json.Nodes;
using Llm.Core.Chat;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Quality;

/// <summary>
/// What a chat shows of feedback and the arena: the person's own thumbs on its answers,
/// its comparisons, and each answer of a comparison not voted on yet with "Model A" or
/// "Model B" for its model (its error and its sub-agents' work too).
/// </summary>
public sealed class ChatQuality
{
    private readonly List<ArenaMatch> _matches;
    private readonly Dictionary<Guid, (ArenaMatch Match, string Side)> _blind;
    private readonly Dictionary<Guid, AnswerFeedback> _feedback;

    private ChatQuality(List<ArenaMatch> matches, Dictionary<Guid, (ArenaMatch, string)> blind, Dictionary<Guid, AnswerFeedback> feedback) =>
        (_matches, _blind, _feedback) = (matches, blind, feedback);

    public static async Task<ChatQuality> ForAsync(AppDbContext db, Guid userId, Guid conversationId, IReadOnlyList<ChatMessage> messages, CancellationToken ct)
    {
        var matches = await db.ArenaMatches.AsNoTracking().Where(m => m.ConversationId == conversationId).OrderBy(m => m.CreatedAt).ToListAsync(ct);
        var feedback = await db.AnswerFeedback.AsNoTracking().Where(f => f.ConversationId == conversationId && f.UserId == userId).ToDictionaryAsync(f => f.MessageId, ct);
        var blind = new Dictionary<Guid, (ArenaMatch, string)>();
        if (matches.Any(m => m.Vote is null))
        {
            var byId = messages.ToDictionary(m => m.Id);
            var children = messages.Where(m => m.ParentId is not null).ToLookup(m => m.ParentId!.Value);
            foreach (var match in matches.Where(m => m.Vote is null))
            {
                foreach (var (side, first) in new[] { ("a", match.AnswerA), ("b", match.AnswerB) })
                {
                    foreach (var id in first is { } f ? Arena.Chain(f, byId, children) : [])
                    {
                        blind[id] = (match, side);
                    }
                }
            }
        }
        return new ChatQuality(matches, blind, feedback);
    }

    public string? Model(ChatMessage m) => _blind.TryGetValue(m.Id, out var b) ? Arena.Label(b.Side) : m.Model;

    public string? Error(ChatMessage m) => m.Error is { } e && _blind.TryGetValue(m.Id, out var b) ? Arena.Blind(e, b.Match.ModelA, b.Match.ModelB) : m.Error;

    /// <summary>The message's details (sub-agents' work, which names the model it ran on) as JSON.</summary>
    public string? Details(ChatMessage m) =>
        m.DetailsJson is { } json && _blind.TryGetValue(m.Id, out var b)
            ? Arena.Blind(JsonNode.Parse(json), b.Match.ModelA, b.Match.ModelB)?.ToJsonString()
            : m.DetailsJson;

    public object? Feedback(Guid messageId) => _feedback.TryGetValue(messageId, out var f) ? Shape(f) : null;

    public static object Shape(AnswerFeedback f) => new { f.Up, f.Reason, f.Comment, f.Shared, f.UpdatedAt };

    /// <summary>The chat's comparisons: the question, each answer's first message, the vote, and the names once voted.</summary>
    public IEnumerable<object> Arenas => _matches.Select(Shape);

    public static object Shape(ArenaMatch m) => new
    {
        m.Id, m.QuestionId, a = m.AnswerA, b = m.AnswerB, m.Vote, m.VotedAt,
        models = m.Vote is null ? null : new { a = m.ModelA, b = m.ModelB },
    };
}
