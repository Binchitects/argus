using System.Globalization;
using System.Text.Json.Nodes;
using Argus.Util;
using Microsoft.Data.Sqlite;

namespace Argus.Store;

/// <summary>
/// What a repository's owners know about its links, to measure the graph against (argus graph-eval --truth FILE). One
/// line each, # for comments:
/// <code>
/// group/app -> group/lib            a link they confirm (any layer)
/// group/app -> group/base deploy    one in that layer (build, ci, deploy, runtime, declared)
/// group/app !-> group/other         one they say is not there
/// </code>
/// Every repository on the left of a line is judged: its owners listed all of its links, so a link the graph has from
/// it that the file does not name is wrong.
/// </summary>
public sealed record Truth(IReadOnlyList<(string From, string To, string? Layer)> Links, IReadOnlyList<(string From, string To)> Not, IReadOnlyList<string> Problems)
{
    public static Truth Parse(string text)
    {
        var links = new List<(string, string, string?)>();
        var not = new List<(string, string)>();
        var problems = new List<string>();
        var n = 0;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            n++;
            var line = (raw.IndexOf('#', StringComparison.Ordinal) is var hash and >= 0 ? raw[..hash] : raw).Trim();
            if (line.Length == 0) continue;
            var negative = line.Contains("!->", StringComparison.Ordinal);
            var parts = line.Split(negative ? "!->" : "->", 2, StringSplitOptions.TrimEntries);
            var right = parts.Length == 2 ? parts[1].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [];
            if (parts.Length != 2 || parts[0].Length == 0 || right.Length is 0 or > 2 || (right.Length == 2 && !LinkKinds.Layers.Contains(right[1])))
            {
                problems.Add($"line {n}: not FROM -> TO [layer] or FROM !-> TO: {PyStr.Prefix(line, 80)}");
                continue;
            }
            var (from, to) = (parts[0].Trim('/').ToLowerInvariant(), right[0].Trim('/').ToLowerInvariant());
            if (negative) not.Add((from, to));
            else links.Add((from, to, right.Length == 2 ? right[1] : null));
        }
        return new Truth(links, not, problems);
    }
}

/// <summary>
/// The graph measured against what owners know: for the repositories they judged, how many of its links between them are
/// right (precision by tier, layer and kind), how many of theirs it found (recall), whether its confidences mean what
/// they say (calibration), and priors that would (each kind's share of right links, drawn toward its prior while few).
/// </summary>
public static class GraphEval
{
    /// <summary>How many links of a kind its prior is worth: a kind with few links stays near its prior.</summary>
    const double PriorWeight = 4;
    /// <summary>The fewest links of a kind before a calibrated prior is written for it.</summary>
    public const int FewestForPrior = 5;
    const int MostListed = 50;

    static readonly (double From, double To)[] Buckets = [(0, 0.3), (0.3, 0.5), (0.5, 0.7), (0.7, 0.85), (0.85, 0.95), (0.95, 1.0001)];

    static double Ratio(long part, long whole) => whole == 0 ? 0 : Math.Round((double)part / whole, 3);

    public static JsonObject Run(SqliteConnection conn, Truth truth)
    {
        var calibrated = LinkKinds.ReadPriors(conn.DataSource);
        var repos = Sql.Query(conn, "SELECT id, lower(path_with_namespace) AS path FROM repos WHERE branch = default_branch")
            .GroupBy(r => r.Str("path")).ToDictionary(g => g.Key, g => g.First().Long("id"), StringComparer.Ordinal);
        var names = repos.ToDictionary(kv => kv.Value, kv => kv.Key);
        var unknown = truth.Links.SelectMany(l => new[] { l.From, l.To }).Concat(truth.Not.SelectMany(l => new[] { l.From, l.To }))
            .Where(p => !repos.ContainsKey(p)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var judged = truth.Links.Select(l => l.From).Concat(truth.Not.Select(l => l.From)).Where(repos.ContainsKey).Select(p => repos[p]).ToHashSet();
        var confirmed = truth.Links.Where(l => repos.ContainsKey(l.From) && repos.ContainsKey(l.To))
            .GroupBy(l => (repos[l.From], repos[l.To])).ToDictionary(g => g.Key, g => g.Select(l => l.Layer).ToList());
        var denied = truth.Not.Where(l => repos.ContainsKey(l.From) && repos.ContainsKey(l.To)).Select(l => (repos[l.From], repos[l.To])).ToHashSet();

        bool Right((long, long) pair, string? layer = null) =>
            confirmed.TryGetValue(pair, out var layers) && (layer is null || layers.Contains(null) || layers.Contains(layer));

        var edges = Sql.Query(conn, "SELECT from_repo_id, to_repo_id, layer, confidence, tier, kinds FROM repo_edges")
            .Where(e => judged.Contains(e.Long("from_repo_id")))
            .Select(e => (Pair: (e.Long("from_repo_id"), e.Long("to_repo_id")), Layer: e.Str("layer"), Confidence: e.Double("confidence"), Tier: e.Str("tier"), Kinds: e.Str("kinds")))
            .ToList();
        var links = Sql.Query(conn, "SELECT from_repo_id, to_repo_id, kind, how, confidence, tier FROM repo_links")
            .Where(l => judged.Contains(l.Long("from_repo_id")))
            .Select(l => (Pair: (l.Long("from_repo_id"), l.Long("to_repo_id")), Kind: l.Str("kind"), How: l.Str("how"), Confidence: l.Double("confidence"), Tier: l.Str("tier")))
            .ToList();

        // Pairs: walked (a walkable layer at Walked or more) against what the owners confirm.
        var walked = edges.Where(e => LinkKinds.Walkable.Contains(e.Layer) && e.Confidence >= LinkKinds.Walked).Select(e => e.Pair).ToHashSet();
        var truePairs = confirmed.Keys.ToHashSet();
        var tp = walked.Count(truePairs.Contains);
        var fp = walked.Count - tp;
        var missed = truePairs.Where(p => !walked.Contains(p)).ToList();
        var precision = Ratio(tp, walked.Count);
        var recall = Ratio(tp, truePairs.Count);

        JsonObject Group(IEnumerable<(bool Right, string Key)> rows) => new(rows.GroupBy(r => r.Key).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => KeyValuePair.Create(g.Key, (JsonNode?)new JsonObject { ["links"] = g.Count(), ["right"] = g.Count(r => r.Right), ["precision"] = Ratio(g.Count(r => r.Right), g.Count()) })));

        var byKind = new JsonArray();
        var priors = new JsonObject();
        foreach (var g in links.Where(l => l.Tier != "candidate").GroupBy(l => (l.Kind, l.How)).OrderByDescending(g => g.Count()).ThenBy(g => g.Key.Kind, StringComparer.Ordinal))
        {
            var right = g.Count(l => Right(l.Pair));
            var settled = g.Key.How.StartsWith("settled:", StringComparison.Ordinal);
            var prior = settled ? LinkKinds.Prior(g.Key.Kind, "exact", g.Key.How, calibrated) : LinkKinds.Prior(g.Key.Kind, g.Key.How, null, calibrated);
            var suggested = Math.Round(Math.Clamp((right + PriorWeight * prior) / (g.Count() + PriorWeight), 0.05, 0.99), 2);
            byKind.Add(new JsonObject
            {
                ["kind"] = g.Key.Kind, ["how"] = g.Key.How, ["links"] = g.Count(), ["right"] = right, ["precision"] = Ratio(right, g.Count()),
                ["prior"] = Math.Round(prior, 3), ["suggested"] = g.Count() >= FewestForPrior && !settled ? suggested : null,
            });
            if (g.Count() >= FewestForPrior && !settled) priors[$"{g.Key.Kind}|{g.Key.How}"] = suggested;
        }

        var calibration = new JsonArray();
        foreach (var (lo, hi) in Buckets)
        {
            var inBucket = edges.Where(e => e.Confidence >= lo && e.Confidence < hi).ToList();
            if (inBucket.Count == 0) continue;
            calibration.Add(new JsonObject
            {
                ["from"] = lo, ["to"] = Math.Min(hi, 1.0), ["links"] = inBucket.Count,
                ["mean_confidence"] = Math.Round(inBucket.Average(e => e.Confidence), 3),
                ["observed"] = Ratio(inBucket.Count(e => Right(e.Pair, e.Layer)), inBucket.Count),
            });
        }

        string Name(long id) => names.GetValueOrDefault(id, id.ToString(CultureInfo.InvariantCulture));
        var wrong = edges.Where(e => walked.Contains(e.Pair) && !Right(e.Pair)).OrderByDescending(e => e.Confidence).Take(MostListed)
            .Select(e => (JsonNode)new JsonObject
            {
                ["from"] = Name(e.Pair.Item1), ["to"] = Name(e.Pair.Item2), ["layer"] = e.Layer, ["confidence"] = e.Confidence, ["kinds"] = e.Kinds,
                ["owners_say_no"] = denied.Contains(e.Pair),
            });
        var notFound = missed.OrderBy(p => Name(p.Item1), StringComparer.Ordinal).ThenBy(p => Name(p.Item2), StringComparer.Ordinal).Take(MostListed)
            .Select(p => (JsonNode)new JsonObject
            {
                ["from"] = Name(p.Item1), ["to"] = Name(p.Item2),
                // Seen, but below what is walked: its best confidence, if any.
                ["seen_at"] = edges.Where(e => e.Pair == p).Select(e => (double?)e.Confidence).Max(),
            });
        return new JsonObject
        {
            ["judged_repos"] = judged.Count,
            ["confirmed_links"] = truePairs.Count,
            ["unknown_repos"] = new JsonArray([.. unknown.Select(u => (JsonNode)u)]),
            ["problems"] = new JsonArray([.. truth.Problems.Select(p => (JsonNode)p)]),
            ["pairs"] = new JsonObject
            {
                ["walked"] = walked.Count, ["right"] = tp, ["wrong"] = fp, ["missed"] = missed.Count, ["precision"] = precision, ["recall"] = recall,
                ["f1"] = precision + recall == 0 ? 0 : Math.Round(2 * precision * recall / (precision + recall), 3),
            },
            ["by_tier"] = Group(edges.Select(e => (Right(e.Pair, e.Layer), e.Tier))),
            ["by_layer"] = Group(edges.Select(e => (Right(e.Pair, e.Layer), e.Layer))),
            ["by_kind"] = byKind,
            ["calibration"] = calibration,
            ["wrong"] = new JsonArray([.. wrong]),
            ["missed"] = new JsonArray([.. notFound]),
            ["priors"] = priors,
            ["priors_in_use"] = new JsonObject(calibrated.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
        };
    }
}
