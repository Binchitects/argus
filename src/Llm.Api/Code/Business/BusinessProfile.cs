using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Llm.Api.Code.Business;

/// <summary>
/// Where an item of the map was read: code (a file, and its line: "src/orders/cancel.cs:42"), a document of the
/// repository ("docs/pricing.md"), an issue ("12"), a merge request ("34") or a wiki page (its slug).
/// </summary>
public sealed record BusinessSource(string Kind, string Ref);

public sealed record BusinessCapability(string Name, string Description, IReadOnlyList<BusinessSource> Sources);
public sealed record BusinessEntity(string Name, string Meaning, IReadOnlyList<BusinessSource> Sources);
public sealed record BusinessRule(string Rule, string Why, IReadOnlyList<BusinessSource> Sources);
public sealed record BusinessProcess(string Name, IReadOnlyList<string> Steps, IReadOnlyList<BusinessSource> Sources);
public sealed record BusinessIntegration(string System, string How, IReadOnlyList<BusinessSource> Sources);
public sealed record BusinessTerm(string Term, string Meaning, IReadOnlyList<BusinessSource> Sources);

/// <summary>
/// What a project's software does for the business: what it lets the business do (capabilities), the things it is about
/// (entities), the rules it enforces and why, what happens step by step (processes), whom it talks to (integrations), and
/// the words people use for it (glossary); each item with the sources it was read from.
/// </summary>
public sealed record BusinessProfile(
    IReadOnlyList<BusinessCapability> Capabilities, IReadOnlyList<BusinessEntity> Entities, IReadOnlyList<BusinessRule> Rules,
    IReadOnlyList<BusinessProcess> Processes, IReadOnlyList<BusinessIntegration> Integrations, IReadOnlyList<BusinessTerm> Glossary)
{
    public static readonly string[] Kinds = ["code", "doc", "issue", "mr", "wiki"];
    public const int MostItems = 40, MostSources = 6, MostSteps = 15;

    public static readonly BusinessProfile Empty = new([], [], [], [], [], []);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public string Write() => JsonSerializer.Serialize(this, Json);

    public static BusinessProfile Read(string? json)
    {
        try
        {
            return json is { Length: > 2 } ? JsonSerializer.Deserialize<BusinessProfile>(json, Json) ?? Empty : Empty;
        }
        catch (JsonException)
        {
            return Empty;
        }
    }

    public int Count => Capabilities.Count + Entities.Count + Rules.Count + Processes.Count + Integrations.Count + Glossary.Count;

    /// <summary>
    /// What the mapper reported, kept to what can be shown: words within bounds, sources of a known kind, at most
    /// <see cref="MostItems"/> of each part; an item with nothing to say, or with no source at all, left out (the map
    /// says only what was read).
    /// </summary>
    public static BusinessProfile From(JsonObject reported)
    {
        static string Text(JsonNode? node, int most) => (node as JsonValue)?.ToString().Trim() is { } t ? (t.Length > most ? t[..most] : t) : "";
        static List<BusinessSource> Sources(JsonNode? node) =>
        [
            .. (node as JsonArray ?? []).OfType<JsonObject>()
                .Select(s => new BusinessSource(Text(s["kind"], 10).ToLowerInvariant(), Text(s["ref"], 300)))
                .Where(s => Kinds.Contains(s.Kind) && s.Ref.Length > 0)
                .DistinctBy(s => (s.Kind, s.Ref))
                .Take(MostSources),
        ];
        List<T> Part<T>(string key, Func<JsonObject, List<BusinessSource>, T?> make) where T : class =>
        [
            .. (reported[key] as JsonArray ?? []).OfType<JsonObject>()
                .Select(o => (Item: o, Sources: Sources(o["sources"])))
                .Where(x => x.Sources.Count > 0)
                .Select(x => make(x.Item, x.Sources))
                .OfType<T>()
                .Take(MostItems),
        ];
        return new BusinessProfile(
            Part("capabilities", (o, s) => Text(o["name"], 150) is { Length: > 0 } n ? new BusinessCapability(n, Text(o["description"], 1000), s) : null),
            Part("entities", (o, s) => Text(o["name"], 150) is { Length: > 0 } n ? new BusinessEntity(n, Text(o["meaning"], 1000), s) : null),
            Part("rules", (o, s) => Text(o["rule"], 600) is { Length: > 0 } r ? new BusinessRule(r, Text(o["why"], 1000), s) : null),
            Part("processes", (o, s) => Text(o["name"], 150) is { Length: > 0 } n
                ? new BusinessProcess(n, [.. (o["steps"] as JsonArray ?? []).Select(x => Text(x, 400)).Where(x => x.Length > 0).Take(MostSteps)], s)
                : null),
            Part("integrations", (o, s) => Text(o["system"], 150) is { Length: > 0 } n ? new BusinessIntegration(n, Text(o["how"], 1000), s) : null),
            Part("glossary", (o, s) => Text(o["term"], 100) is { Length: > 0 } t ? new BusinessTerm(t, Text(o["meaning"], 600), s) : null));
    }

    /// <summary>
    /// The items whose words hold the most of the question's (a name counts double), with their part and system, the best
    /// first: what the chat's business_explain finds before the model traces it through the code.
    /// </summary>
    public IEnumerable<(string Part, string Title, string Text, IReadOnlyList<BusinessSource> Sources, int Score)> Matches(string question)
    {
        var words = question.ToLowerInvariant().Split([' ', ',', '.', '?', '!', ':', ';', '"', '\'', '(', ')', '/', '-'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2 && !Stop.Contains(w)).Distinct().ToList();
        int Score(string title, string text)
        {
            var (t, x) = (title.ToLowerInvariant(), text.ToLowerInvariant());
            return words.Sum(w => (t.Contains(w, StringComparison.Ordinal) ? 2 : 0) + (x.Contains(w, StringComparison.Ordinal) ? 1 : 0));
        }
        IEnumerable<(string, string, string, IReadOnlyList<BusinessSource>)> all =
        [
            .. Capabilities.Select(c => ("capability", c.Name, c.Description, c.Sources)),
            .. Entities.Select(e => ("entity", e.Name, e.Meaning, e.Sources)),
            .. Rules.Select(r => ("rule", r.Rule, r.Why, r.Sources)),
            .. Processes.Select(p => ("process", p.Name, string.Join(" → ", p.Steps), p.Sources)),
            .. Integrations.Select(i => ("integration", i.System, i.How, i.Sources)),
            .. Glossary.Select(g => ("term", g.Term, g.Meaning, g.Sources)),
        ];
        return words.Count == 0 ? [] : all.Select(a => (a.Item1, a.Item2, a.Item3, a.Item4, Score: Score(a.Item2, a.Item3))).Where(a => a.Score > 0).OrderByDescending(a => a.Score);
    }

    private static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "what", "when", "how", "why", "who", "does", "our", "are", "with", "this", "that", "from", "into", "has", "have", "can",
        "about", "which", "where", "there", "their", "they", "you", "your", "its", "was", "were", "will", "would", "should", "happens", "explain",
    };
}
