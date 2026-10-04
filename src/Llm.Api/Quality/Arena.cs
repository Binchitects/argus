using System.Text.Json.Nodes;
using Llm.Core.Chat;

namespace Llm.Api.Quality;

/// <summary>A model's place on the leaderboard: its Elo rating, and its votes.</summary>
/// <param name="Ties">Votes "tie" and "both bad" together; <paramref name="Bad"/> counts the second alone.</param>
public sealed record Standing(string Model, int Rating, int Matches, int Wins, int Losses, int Ties, int Bad, double WinRate);

/// <summary>
/// Arena mode: one question answered by two models, one after the other, shown side by
/// side as "Model A" and "Model B". The names stay out of everything the person is sent
/// until they vote (a model may still name itself in its own words).
/// </summary>
public static class Arena
{
    /// <summary>a: A was better; b: B was; tie; bad: both were bad.</summary>
    public static readonly string[] Votes = ["a", "b", "tie", "bad"];

    /// <summary>Ratings start here.</summary>
    public const double StartRating = 1000;

    /// <summary>How far one vote moves two ratings at most.</summary>
    public const double K = 32;

    public static string Label(string side) => side == "a" ? "Model A" : "Model B";

    /// <summary>A text with the match's two names replaced by their labels (the longer name first: one may begin with the other).</summary>
    public static string Blind(string text, string modelA, string modelB)
    {
        foreach (var (name, label) in new[] { (modelA, Label("a")), (modelB, Label("b")) }.OrderByDescending(x => x.Item1.Length))
        {
            text = text.Replace(name, label, StringComparison.Ordinal);
        }
        return text;
    }

    /// <summary>
    /// An event of an arena answer, blind: every string in it with the names replaced, the
    /// model's words as they stream (content, thinking) left as written.
    /// </summary>
    public static JsonNode? Blind(JsonNode? node, string modelA, string modelB)
    {
        if (node is JsonObject o && (o["type"]?.GetValue<string>() is "content" or "reasoning"
            || o["type"]?.GetValue<string>() == "agent" && o["event"]?.GetValue<string>() is "content" or "reasoning"))
        {
            return node;
        }
        Walk(node, modelA, modelB);
        return node;
    }

    private static void Walk(JsonNode? node, string modelA, string modelB)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var key in o.Select(p => p.Key).ToList())
                {
                    if (o[key] is JsonValue v && v.TryGetValue<string>(out var s))
                    {
                        o[key] = Blind(s, modelA, modelB);
                    }
                    else
                    {
                        Walk(o[key], modelA, modelB);
                    }
                }
                break;
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                {
                    if (arr[i] is JsonValue v && v.TryGetValue<string>(out var s))
                    {
                        arr[i] = Blind(s, modelA, modelB);
                    }
                    else
                    {
                        Walk(arr[i], modelA, modelB);
                    }
                }
                break;
        }
    }

    /// <summary>The messages of one answer: its first message and all below it, down to (not into) the next question.</summary>
    public static HashSet<Guid> Chain(Guid first, IReadOnlyDictionary<Guid, ChatMessage> byId, ILookup<Guid, ChatMessage> children)
    {
        var chain = new HashSet<Guid>();
        if (!byId.ContainsKey(first))
        {
            return chain;
        }
        var todo = new Stack<Guid>([first]);
        while (todo.TryPop(out var id))
        {
            if (!chain.Add(id))
            {
                continue;
            }
            foreach (var child in children[id].Where(c => c.Role != "user"))
            {
                todo.Push(child.Id);
            }
        }
        return chain;
    }

    /// <summary>Where an answer ends: from its first message, the newest line down to before the next question.</summary>
    public static Guid End(Guid first, ILookup<Guid, ChatMessage> children)
    {
        var at = first;
        while (children[at].Where(c => c.Role != "user").OrderByDescending(c => c.Sequence).FirstOrDefault() is { } next)
        {
            at = next.Id;
        }
        return at;
    }

    /// <summary>
    /// Elo ratings from the votes in the order they were cast: every model starts at 1000,
    /// and a vote moves both by at most 32, more when the lower rated wins. A tie and
    /// "both bad" count as half a win each. Best first.
    /// </summary>
    public static List<Standing> Leaderboard(IEnumerable<(string A, string B, string Vote)> votes)
    {
        var rating = new Dictionary<string, double>(StringComparer.Ordinal);
        var tally = new Dictionary<string, (int Matches, int Wins, int Losses, int Ties, int Bad)>(StringComparer.Ordinal);
        foreach (var (a, b, vote) in votes)
        {
            var ra = rating.GetValueOrDefault(a, StartRating);
            var rb = rating.GetValueOrDefault(b, StartRating);
            var expected = 1 / (1 + Math.Pow(10, (rb - ra) / 400));
            var score = vote switch { "a" => 1.0, "b" => 0.0, _ => 0.5 };
            rating[a] = ra + K * (score - expected);
            rating[b] = rb + K * (expected - score);
            Count(a, vote switch { "a" => 1, "b" => -1, _ => 0 }, vote == "bad");
            Count(b, vote switch { "b" => 1, "a" => -1, _ => 0 }, vote == "bad");
        }
        return [.. tally.Select(t => new Standing(t.Key, (int)Math.Round(rating[t.Key]), t.Value.Matches, t.Value.Wins, t.Value.Losses, t.Value.Ties, t.Value.Bad,
                t.Value.Matches == 0 ? 0 : Math.Round((double)t.Value.Wins / t.Value.Matches, 3)))
            .OrderByDescending(s => s.Rating).ThenByDescending(s => s.Matches).ThenBy(s => s.Model, StringComparer.Ordinal)];

        void Count(string model, int outcome, bool bad)
        {
            var t = tally.GetValueOrDefault(model);
            tally[model] = (t.Matches + 1, t.Wins + (outcome > 0 ? 1 : 0), t.Losses + (outcome < 0 ? 1 : 0), t.Ties + (outcome == 0 ? 1 : 0), t.Bad + (bad ? 1 : 0));
        }
    }
}
