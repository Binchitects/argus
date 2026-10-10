using System.Globalization;
using System.Text.Json.Nodes;
using Argus.Util;
using Microsoft.Data.Sqlite;

namespace Argus.Store;

/// <summary>
/// Questions about the estate as a graph of repositories (repo_edges, repo_links): what each uses and is used by, by
/// what (a package, an import, an #include, a submodule, a CI include, an image), how sure and where; how one repository
/// reaches another; what a change reaches, repository by repository and to the lines that name a symbol. Only
/// repositories the caller may read are shown or walked through.
/// </summary>
public static class GraphQueries
{
    public sealed record Repo(long Id, string Path, string Branch, bool Default);

    /// <summary>One way one repository uses another: its kind and name, layer, scope, how its name matched, files, confidence, and up to three uses (path:line).</summary>
    public sealed record Way(string Kind, string Name, string Layer, string Scope, string How, long Files, double Confidence, string Tier, IReadOnlyList<string> Where);

    /// <summary>One link between two repositories: how sure it is (the noisy-OR of its layers), its files, its layers, and every way it is made.</summary>
    public sealed record Edge(long From, long To, double Confidence, long Files, string Scope, IReadOnlyDictionary<string, double> ByLayer, List<Way> Ways)
    {
        public string Tier => LinkKinds.Tier(Confidence);
    }

    /// <summary>A link the filter does not walk that a reader may still want: a name several repositories provide (a candidate), or a weak link.</summary>
    public sealed record Possible(long From, long To, string Kind, string Name, double Confidence, string Tier, string? Why, IReadOnlyList<string> Candidates);

    /// <summary>
    /// What a question walks: links at least this sure (likely and strong by default), of these layers (all but history
    /// by default), and the product's own unless tests are asked for too.
    /// </summary>
    public sealed record GraphFilter(double MinConfidence, IReadOnlySet<string> Layers, bool IncludeTests, bool LayersGiven)
    {
        public static readonly GraphFilter Default = new(LinkKinds.Walked, LinkKinds.Walkable.ToHashSet(StringComparer.Ordinal), false, false);

        /// <summary>From a tool's arguments: layers is a list of build, ci, deploy, runtime, declared, history, test or all.</summary>
        public static GraphFilter Parse(double? minConfidence, string? layers, bool includeTests = false)
        {
            if (minConfidence is { } m && (double.IsNaN(m) || m < 0 || m > 1))
                throw new QueryError($"min_confidence is {m.ToString(CultureInfo.InvariantCulture)}: give a number from 0 to 1 (0.5 walks likely and strong links, 0.85 strong ones only).");
            var chosen = new HashSet<string>(StringComparer.Ordinal);
            var tests = includeTests;
            foreach (var token in (layers ?? "").Split([',', ' ', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var t = token.ToLowerInvariant();
                if (t == "all") chosen.UnionWith(LinkKinds.Layers);
                else if (t is "test" or "tests") tests = true;
                else if (LinkKinds.Layers.Contains(t)) chosen.Add(t);
                else throw new QueryError($"Unknown layer '{token}': use build, ci, deploy, runtime, declared, history, test or all.");
            }
            var given = chosen.Count > 0;
            return new GraphFilter(minConfidence ?? LinkKinds.Walked, given ? chosen : Default.Layers, tests, given);
        }

        public JsonObject ToJson() => new()
        {
            ["min_confidence"] = MinConfidence,
            ["layers"] = new JsonArray([.. LinkKinds.Layers.Where(Layers.Contains).Select(l => (JsonNode)l)]),
            ["include_tests"] = IncludeTests,
        };
    }

    public sealed class Estate
    {
        public required Dictionary<long, Repo> Repos { get; init; }
        public required Dictionary<(long From, long To), Edge> Edges { get; init; }
        public required GraphFilter Filter { get; init; }
        public Dictionary<long, List<Edge>> Out { get; } = [];
        public Dictionary<long, List<Edge>> In { get; } = [];
        public List<Possible> Possible { get; } = [];

        /// <summary>Pairs linked in the index that the filter leaves out (weaker, of another layer, or by tests only).</summary>
        public int Hidden { get; set; }

        public string Name(long id) => Repos[id].Path + (Repos[id].Default ? "" : "@" + Repos[id].Branch);
    }

    /// <summary>The graph among the repositories the caller may read, as the filter admits it. Links are made between default branches.</summary>
    public static Estate Load(IReadOnlyList<long> allowed, SqliteConnection conn, GraphFilter? filter = null)
    {
        filter ??= GraphFilter.Default;
        var ids = allowed.ToHashSet();
        var repos = new Dictionary<long, Repo>();
        foreach (var r in Sql.Query(conn, "SELECT id, path_with_namespace, branch, default_branch FROM repos"))
        {
            if (ids.Contains(r.Long("id")))
                repos[r.Long("id")] = new Repo(r.Long("id"), r.Str("path_with_namespace"), r.Str("branch"), r.Str("branch") == r.Str("default_branch"));
        }
        // A pair's layers the filter admits, each with its confidence and files; a pair it leaves out is counted.
        var layers = new Dictionary<(long, long), List<(string Layer, string Scope, double Confidence, long Files)>>();
        var seen = new HashSet<(long, long)>();
        foreach (var e in Sql.Query(conn, "SELECT from_repo_id, to_repo_id, layer, scope, confidence, files FROM repo_edges"))
        {
            var pair = (e.Long("from_repo_id"), e.Long("to_repo_id"));
            if (!repos.TryGetValue(pair.Item1, out var from) || !from.Default || !repos.ContainsKey(pair.Item2)) continue;
            seen.Add(pair);
            if (!filter.Layers.Contains(e.Str("layer")) || e.Str("scope") == "test" && !filter.IncludeTests) continue;
            if (!layers.TryGetValue(pair, out var list)) layers[pair] = list = [];
            list.Add((e.Str("layer"), e.Str("scope"), e.Double("confidence"), e.Long("files")));
        }
        var edges = new Dictionary<(long, long), Edge>();
        foreach (var (pair, list) in layers)
        {
            var confidence = Math.Round(Math.Min(0.999, LinkKinds.AnyOf(list.Select(l => l.Confidence))), 4);
            if (confidence < filter.MinConfidence) continue;
            edges[pair] = new Edge(pair.Item1, pair.Item2, confidence, list.Sum(l => l.Files), list.Any(l => l.Scope == "main") ? "main" : "test",
                list.ToDictionary(l => l.Layer, l => l.Confidence), []);
        }
        var estatePossible = new List<Possible>();
        var readable = repos.Values.Where(r => r.Default).Select(r => r.Path.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        foreach (var l in Sql.Query(conn,
                     "SELECT from_repo_id, to_repo_id, kind, name, layer, scope, how, files, confidence, tier, evidence FROM repo_links ORDER BY confidence DESC, files DESC"))
        {
            var (from, to) = (l.Long("from_repo_id"), l.Long("to_repo_id"));
            if (!repos.ContainsKey(from) || !repos.ContainsKey(to)) continue;
            var evidence = JsonNode.Parse(l.Str("evidence")) as JsonObject;
            var tier = l.Str("tier");
            if (tier == "candidate" || tier == "weak" && !edges.ContainsKey((from, to)))
            {
                if (l.Str("scope") == "test" && !filter.IncludeTests) continue;
                // Only the repositories the caller may read are named among a name's providers.
                var candidates = (evidence?["candidates"] as JsonArray ?? []).Select(n => n?.ToString() ?? "").Where(readable.Contains).ToList();
                estatePossible.Add(new Possible(from, to, l.Str("kind"), l.Str("name"), l.Double("confidence"), tier, evidence?["why"]?.ToString(), candidates));
                continue;
            }
            if (!edges.TryGetValue((from, to), out var edge) || !filter.Layers.Contains(l.Str("layer")) || l.Str("scope") == "test" && !filter.IncludeTests) continue;
            var where = (evidence?["uses"] as JsonArray ?? []).Select(u => $"{u?["path"]}:{u?["line"]}").ToList();
            edge.Ways.Add(new Way(l.Str("kind"), l.Str("name"), l.Str("layer"), l.Str("scope"), l.Str("how"), l.Long("files"), l.Double("confidence"), tier, where));
        }
        var estate = new Estate { Repos = repos, Edges = edges, Filter = filter, Hidden = seen.Count(p => !edges.ContainsKey(p)) };
        estate.Possible.AddRange(estatePossible);
        foreach (var e in edges.Values)
        {
            if (!estate.Out.TryGetValue(e.From, out var outs)) estate.Out[e.From] = outs = [];
            outs.Add(e);
            if (!estate.In.TryGetValue(e.To, out var ins)) estate.In[e.To] = ins = [];
            ins.Add(e);
        }
        return estate;
    }

    /// <summary>
    /// A repository by its id, or its path (any case; path@branch for a branch's row), among those readable. Links are
    /// made between default branches, so a branch's row is answered by its default branch's, and note says so.
    /// </summary>
    public static Repo? Find(Estate estate, string? repo) => Find(estate, repo, out _);

    public static Repo? Find(Estate estate, string? repo, out string? note)
    {
        note = null;
        if (string.IsNullOrWhiteSpace(repo)) return null;
        var text = repo.Trim();
        Repo? hit;
        if (long.TryParse(text, out var id))
        {
            hit = estate.Repos.GetValueOrDefault(id);
        }
        else
        {
            var at = text.LastIndexOf('@');
            var (path, branch) = at > 0 ? (text[..at], text[(at + 1)..]) : (text, null);
            var hits = estate.Repos.Values.Where(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase)
                                                      && (branch is null ? r.Default : r.Branch == branch)).ToList();
            if (hits.Count == 0 && branch is null)
            {
                // The last part alone (billing-api for payments/billing-api) when only one repository has it.
                var named = estate.Repos.Values.Where(r => r.Default && r.Path.EndsWith("/" + path, StringComparison.OrdinalIgnoreCase)).ToList();
                if (named.Count == 1) return named[0];
            }
            hit = hits.FirstOrDefault();
        }
        if (hit is { Default: false } && estate.Repos.Values.FirstOrDefault(r => r.Default && r.Path == hit.Path) is { } main)
        {
            note = $"Links are made between default branches: {estate.Name(hit.Id)} is answered by {estate.Name(main.Id)}.";
            return main;
        }
        return hit;
    }

    static string Number(double value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>Each kind an edge is made by: its names, files, confidence, tier, layer, how its name matched and up to three uses.</summary>
    static JsonObject Ways(Edge e, int most = 4)
    {
        var kinds = new JsonObject();
        foreach (var g in e.Ways.GroupBy(w => w.Kind).OrderByDescending(g => g.Max(w => w.Confidence)).ThenByDescending(g => g.Sum(w => w.Files)))
        {
            var best = g.MaxBy(w => w.Confidence)!;
            kinds[g.Key] = new JsonObject
            {
                ["names"] = new JsonArray([.. g.OrderByDescending(w => w.Files).Take(most).Select(w => (JsonNode)w.Name)]),
                ["files"] = g.Sum(w => w.Files),
                ["confidence"] = best.Confidence,
                ["tier"] = best.Tier,
                ["layer"] = best.Layer,
                ["how"] = best.How,
                ["scope"] = g.Any(w => w.Scope == "main") ? "main" : "test",
                ["examples"] = new JsonArray([.. g.SelectMany(w => w.Where).Distinct().Take(3).Select(w => (JsonNode)w)]),
            };
        }
        return kinds;
    }

    /// <summary>One line for an edge: "a -> b 0.99 (package:nuget Acme.Core ×1; import:csharp Acme.Core ×37)".</summary>
    static string Line(Estate estate, Edge e) =>
        $"{estate.Name(e.From)} -> {estate.Name(e.To)} {Number(e.Confidence)} (" +
        string.Join("; ", e.Ways.GroupBy(w => w.Kind).OrderByDescending(g => g.Max(w => w.Confidence)).ThenByDescending(g => g.Sum(w => w.Files))
            .Select(g => $"{g.Key} {string.Join(", ", g.OrderByDescending(w => w.Files).Take(2).Select(w => w.Name))}{(g.Count() > 2 ? ", …" : "")} ×{g.Sum(w => w.Files)}")) + ")";

    static JsonObject PossibleJson(Estate estate, Possible p, bool uses) => new()
    {
        ["repo"] = estate.Name(uses ? p.To : p.From),
        ["direction"] = uses ? "uses" : "used_by",
        ["kind"] = p.Kind,
        ["name"] = p.Name,
        ["confidence"] = p.Confidence,
        ["tier"] = p.Tier,
        ["why"] = p.Why,
        ["candidates"] = p.Candidates.Count > 0 ? new JsonArray([.. p.Candidates.Select(c => (JsonNode)c)]) : null,
    };

    // --- repo_map ------------------------------------------------------------------------

    public const int MostPossible = 20;

    public static JsonObject RepoMap(IReadOnlyList<long> allowed, SqliteConnection conn, long repoId, int depth = 1, GraphFilter? filter = null)
    {
        var estate = Load(allowed, conn, filter);
        if (!estate.Repos.TryGetValue(repoId, out var asked)) return [];
        string? note = null;
        if (!asked.Default && estate.Repos.Values.FirstOrDefault(r => r.Default && r.Path == asked.Path) is { } main)
        {
            note = $"Links are made between default branches: {estate.Name(repoId)} is answered by {estate.Name(main.Id)}.";
            repoId = main.Id;
        }
        JsonArray Side(bool uses)
        {
            var arr = new JsonArray();
            foreach (var (id, level, via, confidence) in Walk(estate, repoId, uses, Math.Clamp(depth, 1, 6)))
            {
                var edge = uses ? estate.Edges[(via, id)] : estate.Edges[(id, via)];
                arr.Add(new JsonObject
                {
                    ["repo_id"] = id, ["path_with_namespace"] = estate.Name(id), ["depth"] = level,
                    ["via"] = level > 1 ? estate.Name(via) : null, ["files"] = edge.Files,
                    ["confidence"] = Math.Round(confidence, 4), ["tier"] = LinkKinds.Tier(confidence),
                    ["layers"] = new JsonArray([.. LinkKinds.Layers.Where(edge.ByLayer.ContainsKey).Select(l => (JsonNode)l)]),
                    ["by"] = Ways(edge),
                });
            }
            return arr;
        }
        var possible = estate.Possible.Where(p => p.From == repoId || p.To == repoId).OrderByDescending(p => p.Confidence).ToList();
        return new JsonObject
        {
            ["repo"] = new JsonObject { ["repo_id"] = repoId, ["path_with_namespace"] = estate.Name(repoId) },
            ["note"] = note,
            ["depends_on"] = Side(uses: true),
            ["depended_on_by"] = Side(uses: false),
            ["possible"] = new JsonArray([.. possible.Take(MostPossible).Select(p => (JsonNode)PossibleJson(estate, p, p.From == repoId))]),
            ["possible_truncated"] = possible.Count > MostPossible,
            ["filter"] = estate.Filter.ToJson(),
        };
    }

    /// <summary>
    /// Breadth first from a repository: what it uses (or what uses it), each with its depth, the repository it was reached
    /// through (the surest at that depth) and how sure the whole way is (the product of its links), surest first.
    /// </summary>
    public static List<(long Id, int Depth, long Via, double Confidence)> Walk(Estate estate, long start, bool uses, int depth)
    {
        var sure = new Dictionary<long, double> { [start] = 1.0 };
        var found = new List<(long, int, long, double)>();
        var frontier = new List<long> { start };
        for (var level = 1; level <= depth && frontier.Count > 0; level++)
        {
            var reached = new Dictionary<long, (long Via, double Confidence)>();
            foreach (var at in frontier)
            {
                foreach (var e in (uses ? estate.Out.GetValueOrDefault(at) : estate.In.GetValueOrDefault(at)) ?? [])
                {
                    var other = uses ? e.To : e.From;
                    if (sure.ContainsKey(other)) continue;
                    var confidence = sure[at] * e.Confidence;
                    if (!reached.TryGetValue(other, out var known) || confidence > known.Confidence) reached[other] = (at, confidence);
                }
            }
            frontier = [];
            foreach (var (other, (via, confidence)) in reached.OrderByDescending(r => r.Value.Confidence).ThenBy(r => estate.Name(r.Key), StringComparer.Ordinal))
            {
                sure[other] = confidence;
                found.Add((other, level, via, confidence));
                frontier.Add(other);
            }
        }
        return found;
    }

    // --- system_map ----------------------------------------------------------------------

    public const int MostEdges = 400;

    /// <summary>
    /// The estate's shape: its hubs (what most depends on), its layers (from what depends on nothing in the estate up),
    /// its cycles, its groups and how they use each other, and its links, one line each. Hubs, layers and cycles are of
    /// what each is built from (the build layer) unless layers are asked for: a CI template every pipeline includes is no
    /// foundation of the code. With focus, the neighbourhood of one repository within depth; with group, the
    /// repositories under one group path.
    /// </summary>
    public static JsonObject SystemMap(IReadOnlyList<long> allowed, SqliteConnection conn, string? focus = null, int depth = 2, string? group = null, GraphFilter? filter = null)
    {
        var estate = Load(allowed, conn, filter);
        var nodes = estate.Repos.Values.Where(r => r.Default).Select(r => r.Id).ToHashSet();
        string? note = null;
        if (Find(estate, focus, out var branchNote) is { } center)
        {
            var near = Walk(estate, center.Id, true, depth).Concat(Walk(estate, center.Id, false, depth)).Select(x => x.Id).Append(center.Id).ToHashSet();
            nodes.IntersectWith(near);
            note = $"The repositories within {depth} links of {estate.Name(center.Id)}, both ways." + (branchNote is null ? "" : " " + branchNote);
        }
        else if (focus is { Length: > 0 })
        {
            return new JsonObject { ["error"] = $"No repository {focus} that you may read: overview lists them." };
        }
        if (group is { Length: > 0 } g)
        {
            nodes.RemoveWhere(id => !estate.Repos[id].Path.StartsWith(g.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase));
        }
        var edges = estate.Edges.Values.Where(e => nodes.Contains(e.From) && nodes.Contains(e.To)).ToList();
        var structural = estate.Filter.LayersGiven ? edges : edges.Where(e => e.ByLayer.ContainsKey(LinkKinds.Build)).ToList();
        var usedBy = nodes.ToDictionary(id => id, id => structural.Count(e => e.To == id));
        var weight = nodes.ToDictionary(id => id, id => structural.Where(e => e.To == id).Sum(e => e.Confidence));
        var uses = nodes.ToDictionary(id => id, id => structural.Count(e => e.From == id));
        var linked = edges.SelectMany(e => new[] { e.From, e.To }).ToHashSet();

        var (layerOf, cycles) = Layers(nodes, structural);
        var hubs = new JsonArray();
        foreach (var id in nodes.Where(id => usedBy[id] > 0).OrderByDescending(id => usedBy[id]).ThenByDescending(id => weight[id])
                     .ThenBy(id => estate.Name(id), StringComparer.Ordinal).Take(15))
        {
            hubs.Add(new JsonObject
            {
                ["repo"] = estate.Name(id), ["used_by"] = usedBy[id], ["uses"] = uses[id], ["layer"] = layerOf[id],
                ["what"] = Purpose(conn, id),
            });
        }
        var layers = new JsonArray();
        foreach (var layer in nodes.GroupBy(id => layerOf[id]).OrderBy(l => l.Key))
        {
            var names = layer.Select(estate.Name).Order(StringComparer.Ordinal).ToList();
            layers.Add(new JsonObject
            {
                ["layer"] = layer.Key, ["repos"] = names.Count,
                ["names"] = new JsonArray([.. names.Take(40).Select(n => (JsonNode)n)]),
                ["more"] = names.Count > 40 ? names.Count - 40 : null,
            });
        }
        var groups = new JsonObject();
        foreach (var grp in edges.GroupBy(e => (GroupOf(estate.Repos[e.From].Path), GroupOf(estate.Repos[e.To].Path))).Where(x => x.Key.Item1 != x.Key.Item2)
                     .OrderByDescending(x => x.Count()).Take(30))
            groups[$"{grp.Key.Item1} -> {grp.Key.Item2}"] = grp.Count();
        var lines = edges.OrderByDescending(e => usedBy[e.To]).ThenByDescending(e => e.Confidence).ThenBy(e => estate.Name(e.From), StringComparer.Ordinal)
            .Take(MostEdges).Select(e => (JsonNode)Line(estate, e)).ToArray();
        return new JsonObject
        {
            ["note"] = note,
            ["repos"] = nodes.Count,
            ["links"] = edges.Count,
            ["hidden_links"] = estate.Hidden,
            ["structure_from"] = estate.Filter.LayersGiven ? "the layers asked for" : "build links (what each is built from)",
            ["hubs"] = hubs,
            ["layers"] = layers,
            ["cycles"] = new JsonArray([.. cycles.Select(c => (JsonNode)new JsonArray([.. c.Select(id => (JsonNode)estate.Name(id))]))]),
            ["between_groups"] = groups,
            ["isolated"] = new JsonArray([.. nodes.Where(id => !linked.Contains(id)).Select(estate.Name).Order(StringComparer.Ordinal).Take(60).Select(n => (JsonNode)n)]),
            ["edges"] = new JsonArray(lines),
            ["edges_truncated"] = edges.Count > MostEdges,
            ["filter"] = estate.Filter.ToJson(),
        };
    }

    static string GroupOf(string path) => path.Contains('/') ? path[..path.LastIndexOf('/')] : "";

    /// <summary>What a repository is, in its README's first sentence that says something.</summary>
    static string? Purpose(SqliteConnection conn, long repoId)
    {
        var row = Sql.One(conn,
            "SELECT content FROM files WHERE repo_id = ? AND lower(path) LIKE 'readme%' ORDER BY length(path) LIMIT 1", repoId);
        if (row is null) return null;
        foreach (var raw in row.Str("content").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length < 12 || line.StartsWith('#') || line.StartsWith('!') || line.StartsWith('[') || line.StartsWith('<') || line.StartsWith("```", StringComparison.Ordinal)) continue;
            var stop = line.IndexOf(". ", StringComparison.Ordinal);
            var sentence = stop > 0 ? line[..(stop + 1)] : line;
            return sentence.Length <= 200 ? sentence : sentence[..197] + "…";
        }
        return null;
    }

    /// <summary>Each repository's layer (0: uses nothing in the estate; n: one more than the highest it uses), with cycles counted as one.</summary>
    static (Dictionary<long, int> Layer, List<List<long>> Cycles) Layers(HashSet<long> nodes, List<Edge> edges)
    {
        var outs = nodes.ToDictionary(id => id, _ => new List<long>());
        foreach (var e in edges) outs[e.From].Add(e.To);
        // Tarjan's strongly connected components, without recursion.
        var index = new Dictionary<long, int>();
        var low = new Dictionary<long, int>();
        var onStack = new HashSet<long>();
        var stack = new Stack<long>();
        var component = new Dictionary<long, int>();
        var components = new List<List<long>>();
        var counter = 0;
        foreach (var root in nodes.Order())
        {
            if (index.ContainsKey(root)) continue;
            var work = new Stack<(long Node, int Next)>();
            work.Push((root, 0));
            index[root] = low[root] = counter++;
            stack.Push(root);
            onStack.Add(root);
            while (work.Count > 0)
            {
                var (node, next) = work.Pop();
                if (next < outs[node].Count)
                {
                    work.Push((node, next + 1));
                    var to = outs[node][next];
                    if (!index.ContainsKey(to))
                    {
                        index[to] = low[to] = counter++;
                        stack.Push(to);
                        onStack.Add(to);
                        work.Push((to, 0));
                    }
                    else if (onStack.Contains(to))
                    {
                        low[node] = Math.Min(low[node], index[to]);
                    }
                    continue;
                }
                if (work.Count > 0)
                {
                    var parent = work.Peek().Node;
                    low[parent] = Math.Min(low[parent], low[node]);
                }
                if (low[node] == index[node])
                {
                    var members = new List<long>();
                    long m;
                    do
                    {
                        m = stack.Pop();
                        onStack.Remove(m);
                        component[m] = components.Count;
                        members.Add(m);
                    } while (m != node);
                    components.Add(members);
                }
            }
        }
        // Tarjan finds components in reverse topological order: what a component uses is found before it.
        var layerOfComponent = new int[components.Count];
        for (var c = 0; c < components.Count; c++)
        {
            var highest = -1;
            foreach (var member in components[c])
                foreach (var to in outs[member])
                    if (component[to] != c) highest = Math.Max(highest, layerOfComponent[component[to]]);
            layerOfComponent[c] = highest + 1;
        }
        var layer = nodes.ToDictionary(id => id, id => layerOfComponent[component[id]]);
        return (layer, [.. components.Where(c => c.Count > 1)]);
    }


    // --- dependency_path -----------------------------------------------------------------

    /// <summary>
    /// How one repository reaches another through what each uses: the surest ways (up to three), each step with its
    /// confidence and evidence; else the other way round. With no way at the filter's confidence, the weaker ways are
    /// tried and marked weak.
    /// </summary>
    public static JsonObject DependencyPath(IReadOnlyList<long> allowed, SqliteConnection conn, string from, string to, GraphFilter? filter = null)
    {
        filter ??= GraphFilter.Default;
        foreach (var (pass, weak) in new[] { (filter, false), (filter with { MinConfidence = 0 }, true) })
        {
            if (weak && filter.MinConfidence == 0) break;
            var estate = Load(allowed, conn, pass);
            if (Find(estate, from, out var fromNote) is not { } a) return new JsonObject { ["error"] = $"No repository {from} that you may read." };
            if (Find(estate, to, out var toNote) is not { } b) return new JsonObject { ["error"] = $"No repository {to} that you may read." };
            foreach (var (start, end, direction) in new[] { (a.Id, b.Id, "uses"), (b.Id, a.Id, "used_by") })
            {
                var paths = Surest(estate, start, end, 3);
                if (paths.Count == 0) continue;
                return new JsonObject
                {
                    ["from"] = estate.Name(a.Id),
                    ["to"] = estate.Name(b.Id),
                    ["note"] = fromNote ?? toNote,
                    ["direction"] = direction == "uses" ? $"{estate.Name(a.Id)} depends on {estate.Name(b.Id)}" : $"{estate.Name(b.Id)} depends on {estate.Name(a.Id)} (not the other way)",
                    ["weak"] = weak ? true : null,
                    ["paths"] = new JsonArray([.. paths.Select(p =>
                    {
                        var steps = p.Zip(p.Skip(1)).Select(s => estate.Edges[(s.First, s.Second)]).ToList();
                        var weakest = steps.MinBy(s => s.Confidence)!;
                        return (JsonNode)new JsonObject
                        {
                            ["length"] = steps.Count,
                            ["confidence"] = Math.Round(steps.Aggregate(1.0, (c, s) => c * s.Confidence), 4),
                            ["weakest"] = $"{estate.Name(weakest.From)} -> {estate.Name(weakest.To)} {Number(weakest.Confidence)}",
                            ["steps"] = new JsonArray([.. steps.Select(s => (JsonNode)new JsonObject
                            {
                                ["from"] = estate.Name(s.From), ["to"] = estate.Name(s.To), ["confidence"] = s.Confidence, ["tier"] = s.Tier,
                                ["layers"] = new JsonArray([.. LinkKinds.Layers.Where(s.ByLayer.ContainsKey).Select(l => (JsonNode)l)]),
                                ["by"] = Ways(s, 3),
                            })]),
                        };
                    })]),
                    ["filter"] = pass.ToJson(),
                };
            }
        }
        return new JsonObject
        {
            ["from"] = from, ["to"] = to, ["paths"] = new JsonArray(),
            ["note"] = "Neither depends on the other through what the index has resolved (packages, imports, #includes, submodules, CI includes, images) among the repositories you may read, at any confidence. They may still talk at run time (a service's API, a queue): search_code for its address or name.",
        };
    }

    /// <summary>A step's cost: how unsure it is (-ln of its confidence), and a little for each hop.</summary>
    static double Cost(Edge e) => -Math.Log(Math.Max(e.Confidence, 1e-6)) + 0.05;

    /// <summary>The surest way from start to end that avoids the steps and repositories blocked (Dijkstra on Cost).</summary>
    static List<long>? Surest(Estate estate, long start, long end, HashSet<(long, long)> blockedSteps, HashSet<long> blockedRepos)
    {
        var cost = new Dictionary<long, double> { [start] = 0 };
        var parent = new Dictionary<long, long>();
        var queue = new PriorityQueue<long, double>();
        queue.Enqueue(start, 0);
        var done = new HashSet<long>();
        while (queue.TryDequeue(out var at, out var c))
        {
            if (!done.Add(at)) continue;
            if (at == end) break;
            foreach (var e in estate.Out.GetValueOrDefault(at) ?? [])
            {
                if (blockedRepos.Contains(e.To) || blockedSteps.Contains((at, e.To)) || done.Contains(e.To)) continue;
                var next = c + Cost(e);
                if (cost.TryGetValue(e.To, out var known) && known <= next) continue;
                cost[e.To] = next;
                parent[e.To] = at;
                queue.Enqueue(e.To, next);
            }
        }
        if (!done.Contains(end)) return null;
        var path = new List<long> { end };
        while (path[^1] != start) path.Add(parent[path[^1]]);
        path.Reverse();
        return path;
    }

    /// <summary>Up to k surest ways from start to end, surest first (Yen's algorithm over Surest).</summary>
    static List<List<long>> Surest(Estate estate, long start, long end, int k)
    {
        double PathCost(List<long> p) => p.Zip(p.Skip(1)).Sum(s => Cost(estate.Edges[(s.First, s.Second)]));
        if (Surest(estate, start, end, [], []) is not { } first) return [];
        var found = new List<List<long>> { first };
        var spare = new List<List<long>>();
        while (found.Count < k)
        {
            var last = found[^1];
            for (var i = 0; i < last.Count - 1; i++)
            {
                var root = last[..(i + 1)];
                var blockedSteps = found.Where(p => p.Count > i + 1 && p.Take(i + 1).SequenceEqual(root)).Select(p => (p[i], p[i + 1])).ToHashSet();
                var blockedRepos = root[..i].ToHashSet();
                if (Surest(estate, last[i], end, blockedSteps, blockedRepos) is not { } spur) continue;
                var whole = root[..i].Concat(spur).ToList();
                if (!found.Any(p => p.SequenceEqual(whole)) && !spare.Any(p => p.SequenceEqual(whole))) spare.Add(whole);
            }
            if (spare.Count == 0) break;
            var next = spare.MinBy(PathCost)!;
            spare.Remove(next);
            found.Add(next);
        }
        return found;
    }

    // --- change_impact -------------------------------------------------------------------

    public const int MostReferences = 60;

    /// <summary>
    /// What a change to a repository reaches: every repository that depends on it, directly or through others, by depth
    /// and how; with symbol, the lines in those repositories (and the repository itself) that name it, each with the way
    /// its repository reaches the change. A name's lines in repositories that do not depend on the symbol's are left out:
    /// they are another thing of the same name.
    /// </summary>
    public static JsonObject ChangeImpact(IReadOnlyList<long> allowed, SqliteConnection conn, string repo, string? symbol = null, int depth = 3, GraphFilter? filter = null)
    {
        var estate = Load(allowed, conn, filter);
        if (Find(estate, repo, out var branchNote) is not { } changed) return new JsonObject { ["error"] = $"No repository {repo} that you may read." };
        var reached = Walk(estate, changed.Id, uses: false, Math.Clamp(depth, 1, 8));
        var dependents = new JsonArray();
        foreach (var level in reached.GroupBy(r => r.Depth).OrderBy(g => g.Key))
        {
            dependents.Add(new JsonObject
            {
                ["depth"] = level.Key,
                ["repos"] = new JsonArray([.. level.OrderByDescending(r => r.Confidence).ThenBy(r => estate.Name(r.Id), StringComparer.Ordinal).Select(r => (JsonNode)new JsonObject
                {
                    ["repo"] = estate.Name(r.Id),
                    ["through"] = estate.Name(r.Via),
                    ["confidence"] = Math.Round(r.Confidence, 4),
                    ["by"] = Ways(estate.Edges[(r.Id, r.Via)], 2),
                })]),
            });
        }
        var possible = estate.Possible.Where(p => p.To == changed.Id).OrderByDescending(p => p.Confidence).ToList();
        var result = new JsonObject
        {
            ["repo"] = estate.Name(changed.Id),
            ["note"] = branchNote,
            ["dependents"] = reached.Count,
            ["by_depth"] = dependents,
            ["deeper"] = Walk(estate, changed.Id, uses: false, 50).Count > reached.Count ? "more repositories depend on it further away: raise depth" : null,
            ["possible_dependents"] = new JsonArray([.. possible.Take(MostPossible).Select(p => (JsonNode)PossibleJson(estate, p, uses: false))]),
            ["filter"] = estate.Filter.ToJson(),
        };
        if (symbol is { Length: > 0 } name)
        {
            var scope = reached.Select(r => r.Id).Append(changed.Id).ToList();
            var definitions = Queries.FindSymbol([changed.Id], conn, name);
            var via = reached.ToDictionary(r => estate.Name(r.Id), r => (r.Depth, Through: estate.Name(r.Via)));
            var refs = Queries.FindReferences(scope, conn, name, MostReferences + 1);
            result["symbol"] = name;
            result["defined"] = new JsonArray([.. definitions.Select(d => (JsonNode)new JsonObject
            {
                ["path"] = d.Str("path"), ["line"] = d.LongOrNull("line"), ["kind"] = d.StrOrNull("kind"), ["signature"] = d.StrOrNull("signature"),
            })]);
            result["references"] = new JsonArray([.. refs.Take(MostReferences).Select(r =>
            {
                var repoName = r["repo"]!.GetValue<string>();
                var copy = (JsonObject)r.DeepClone();
                if (via.TryGetValue(repoName, out var how))
                {
                    copy["depth"] = how.Depth;
                    copy["through"] = how.Through;
                }
                return (JsonNode)copy;
            })]);
            result["references_truncated"] = refs.Count > MostReferences;
            if (definitions.Count == 0)
                result["note"] = $"{estate.Name(changed.Id)} defines no symbol {name} that the index found: the lines are by name only.";
        }
        return result;
    }

    /// <summary>A repository's links for overview: whom it uses and who uses it, with how, surest first.</summary>
    public static (JsonArray Uses, JsonArray UsedBy) Neighbours(Estate estate, long repoId, int most = 10)
    {
        JsonArray Side(IEnumerable<Edge> edges, bool uses) => new([.. edges.OrderByDescending(e => e.Confidence).ThenByDescending(e => e.Files).Take(most).Select(e => (JsonNode)new JsonObject
        {
            ["path_with_namespace"] = estate.Name(uses ? e.To : e.From),
            ["weight"] = e.Files,
            ["confidence"] = e.Confidence,
            ["by"] = string.Join(", ", e.Ways.Select(w => w.Kind).Distinct()),
        })]);
        return (Side(estate.Out.GetValueOrDefault(repoId) ?? [], true), Side(estate.In.GetValueOrDefault(repoId) ?? [], false));
    }
}
