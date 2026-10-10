using System.Text.Json.Nodes;
using Argus.Util;
using Microsoft.Data.Sqlite;

namespace Argus.Store;

/// <summary>
/// Questions about the estate as a graph of repositories (repo_links): what each uses and is used by, by what (a
/// package, an import, an #include, a submodule, an image) and where; how one repository reaches another; what a change
/// reaches, repository by repository and to the lines that name a symbol. Only repositories the caller may read are
/// shown or walked through.
/// </summary>
public static class GraphQueries
{
    public sealed record Repo(long Id, string Path, string Branch, bool Default);

    /// <summary>One link between two repositories: how sure it is (the noisy-OR of its layers), and every way it is made.</summary>
    public sealed record Edge(long From, long To, double Confidence, List<(string Kind, string Name, long Files, double Confidence, string? Where)> Ways)
    {
        public long Files => Ways.Sum(w => w.Files);
        public string Tier => LinkKinds.Tier(Confidence);
    }

    public sealed class Estate
    {
        public required Dictionary<long, Repo> Repos { get; init; }
        public required Dictionary<(long From, long To), Edge> Edges { get; init; }
        public Dictionary<long, List<Edge>> Out { get; } = [];
        public Dictionary<long, List<Edge>> In { get; } = [];

        public string Name(long id) => Repos[id].Path + (Repos[id].Default ? "" : "@" + Repos[id].Branch);
    }

    /// <summary>The graph among the repositories the caller may read (default branches unless a branch's row is named).</summary>
    public static Estate Load(IReadOnlyList<long> allowed, SqliteConnection conn)
    {
        var ids = allowed.ToHashSet();
        var repos = new Dictionary<long, Repo>();
        foreach (var r in Sql.Query(conn, "SELECT id, path_with_namespace, branch, default_branch FROM repos"))
        {
            if (ids.Contains(r.Long("id")))
                repos[r.Long("id")] = new Repo(r.Long("id"), r.Str("path_with_namespace"), r.Str("branch"), r.Str("branch") == r.Str("default_branch"));
        }
        // A pair is walked when the product's own code, build, pipeline or deployment makes it likely or strong: not its
        // tests' alone, not what merely changes with it, not a name several repositories provide that nothing settled.
        var layers = new Dictionary<(long, long), List<double>>();
        foreach (var e in Sql.Query(conn, "SELECT from_repo_id, to_repo_id, layer, confidence FROM repo_edges WHERE scope = 'main'"))
        {
            var (from, to) = (e.Long("from_repo_id"), e.Long("to_repo_id"));
            if (!repos.ContainsKey(from) || !repos.ContainsKey(to) || !LinkKinds.Walkable.Contains(e.Str("layer"))) continue;
            if (!layers.TryGetValue((from, to), out var list)) layers[(from, to)] = list = [];
            list.Add(e.Double("confidence"));
        }
        var edges = new Dictionary<(long, long), Edge>();
        foreach (var (pair, confidences) in layers)
        {
            var confidence = Math.Round(Math.Min(0.999, LinkKinds.AnyOf(confidences)), 4);
            if (confidence >= LinkKinds.Walked) edges[pair] = new Edge(pair.Item1, pair.Item2, confidence, []);
        }
        foreach (var l in Sql.Query(conn,
                     "SELECT from_repo_id, to_repo_id, kind, name, layer, files, confidence, evidence FROM repo_links WHERE scope = 'main' AND tier <> 'candidate'"))
        {
            if (!edges.TryGetValue((l.Long("from_repo_id"), l.Long("to_repo_id")), out var edge) || !LinkKinds.Walkable.Contains(l.Str("layer"))) continue;
            var use = JsonNode.Parse(l.Str("evidence"))?["uses"]?.AsArray().FirstOrDefault();
            var where = use is null ? null : $"{use["path"]}:{use["line"]}";
            edge.Ways.Add((l.Str("kind"), l.Str("name"), l.Long("files"), l.Double("confidence"), where));
        }
        var estate = new Estate { Repos = repos, Edges = edges };
        foreach (var e in edges.Values)
        {
            if (!estate.Out.TryGetValue(e.From, out var outs)) estate.Out[e.From] = outs = [];
            outs.Add(e);
            if (!estate.In.TryGetValue(e.To, out var ins)) estate.In[e.To] = ins = [];
            ins.Add(e);
        }
        return estate;
    }

    /// <summary>A repository by its id, or its path (any case; path@branch for a branch's row), among those readable.</summary>
    public static Repo? Find(Estate estate, string? repo)
    {
        if (string.IsNullOrWhiteSpace(repo)) return null;
        var text = repo.Trim();
        if (long.TryParse(text, out var id)) return estate.Repos.GetValueOrDefault(id);
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
        return hits.FirstOrDefault();
    }

    static JsonObject Ways(Edge e, int most = 4)
    {
        var kinds = new JsonObject();
        foreach (var g in e.Ways.GroupBy(w => w.Kind).OrderByDescending(g => g.Sum(w => w.Files)))
        {
            kinds[g.Key] = new JsonObject
            {
                ["names"] = new JsonArray([.. g.OrderByDescending(w => w.Files).Take(most).Select(w => (JsonNode)w.Name)]),
                ["files"] = g.Sum(w => w.Files),
                ["confidence"] = g.Max(w => w.Confidence),
                ["example"] = g.Select(w => w.Where).FirstOrDefault(w => w is not null),
            };
        }
        return kinds;
    }

    /// <summary>One line for an edge: "a -> b 0.99 (package:nuget Acme.Core ×1; import:csharp Acme.Core ×37)".</summary>
    static string Line(Estate estate, Edge e) =>
        $"{estate.Name(e.From)} -> {estate.Name(e.To)} {e.Confidence.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)} (" +
        string.Join("; ", e.Ways.GroupBy(w => w.Kind).OrderByDescending(g => g.Sum(w => w.Files))
            .Select(g => $"{g.Key} {string.Join(", ", g.OrderByDescending(w => w.Files).Take(2).Select(w => w.Name))}{(g.Count() > 2 ? ", …" : "")} ×{g.Sum(w => w.Files)}")) + ")";

    // --- repo_map ------------------------------------------------------------------------

    public static JsonObject RepoMap(IReadOnlyList<long> allowed, SqliteConnection conn, long repoId, int depth = 1)
    {
        var estate = Load(allowed, conn);
        if (!estate.Repos.ContainsKey(repoId)) return [];
        JsonArray Side(bool uses)
        {
            var arr = new JsonArray();
            foreach (var (id, level, via) in Walk(estate, repoId, uses, Math.Clamp(depth, 1, 6)))
            {
                var edge = uses ? estate.Edges[(via, id)] : estate.Edges[(id, via)];
                arr.Add(new JsonObject
                {
                    ["repo_id"] = id, ["path_with_namespace"] = estate.Name(id), ["depth"] = level,
                    ["via"] = level > 1 ? estate.Name(via) : null, ["files"] = edge.Files,
                    ["confidence"] = edge.Confidence, ["tier"] = edge.Tier, ["by"] = Ways(edge),
                });
            }
            return arr;
        }
        return new JsonObject
        {
            ["repo"] = new JsonObject { ["repo_id"] = repoId, ["path_with_namespace"] = estate.Name(repoId) },
            ["depends_on"] = Side(uses: true),
            ["depended_on_by"] = Side(uses: false),
        };
    }

    /// <summary>Breadth first from a repository: what it uses (or what uses it), each with its depth and the repository it was reached through.</summary>
    public static List<(long Id, int Depth, long Via)> Walk(Estate estate, long start, bool uses, int depth)
    {
        var seen = new HashSet<long> { start };
        var found = new List<(long, int, long)>();
        var frontier = new List<long> { start };
        for (var level = 1; level <= depth && frontier.Count > 0; level++)
        {
            var next = new List<long>();
            foreach (var at in frontier)
            {
                var edges = uses ? estate.Out.GetValueOrDefault(at) : estate.In.GetValueOrDefault(at);
                foreach (var e in (edges ?? []).OrderByDescending(e => e.Files))
                {
                    var other = uses ? e.To : e.From;
                    if (!seen.Add(other)) continue;
                    found.Add((other, level, at));
                    next.Add(other);
                }
            }
            frontier = next;
        }
        return found;
    }

    // --- system_map ----------------------------------------------------------------------

    public const int MostEdges = 400;

    /// <summary>
    /// The estate's shape: its hubs (what most depends on), its layers (from what depends on nothing in the estate up),
    /// its cycles, its groups and how they use each other, and its links, one line each. With focus, the neighbourhood of
    /// one repository within depth; with group, the repositories under one group path.
    /// </summary>
    public static JsonObject SystemMap(IReadOnlyList<long> allowed, SqliteConnection conn, string? focus = null, int depth = 2, string? group = null)
    {
        var estate = Load(allowed, conn);
        var nodes = estate.Repos.Values.Where(r => r.Default).Select(r => r.Id).ToHashSet();
        string? note = null;
        if (Find(estate, focus) is { } center)
        {
            var near = Walk(estate, center.Id, true, depth).Concat(Walk(estate, center.Id, false, depth)).Select(x => x.Id).Append(center.Id).ToHashSet();
            nodes.IntersectWith(near);
            note = $"The repositories within {depth} links of {estate.Name(center.Id)}, both ways.";
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
        var usedBy = nodes.ToDictionary(id => id, id => edges.Count(e => e.To == id));
        var uses = nodes.ToDictionary(id => id, id => edges.Count(e => e.From == id));

        var (layerOf, cycles) = Layers(nodes, edges);
        var hubs = new JsonArray();
        foreach (var id in nodes.Where(id => usedBy[id] > 0).OrderByDescending(id => usedBy[id]).ThenBy(id => estate.Name(id), StringComparer.Ordinal).Take(15))
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
        var lines = edges.OrderByDescending(e => usedBy[e.To]).ThenBy(e => estate.Name(e.From), StringComparer.Ordinal)
            .Take(MostEdges).Select(e => (JsonNode)Line(estate, e)).ToArray();
        return new JsonObject
        {
            ["note"] = note,
            ["repos"] = nodes.Count,
            ["links"] = edges.Count,
            ["hubs"] = hubs,
            ["layers"] = layers,
            ["cycles"] = new JsonArray([.. cycles.Select(c => (JsonNode)new JsonArray([.. c.Select(id => (JsonNode)estate.Name(id))]))]),
            ["between_groups"] = groups,
            ["isolated"] = new JsonArray([.. nodes.Where(id => usedBy[id] == 0 && uses[id] == 0).Select(estate.Name).Order(StringComparer.Ordinal).Take(60).Select(n => (JsonNode)n)]),
            ["edges"] = new JsonArray(lines),
            ["edges_truncated"] = edges.Count > MostEdges,
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

    /// <summary>How one repository reaches another through what each uses: the shortest ways (up to three), each step with its evidence; else the other way round.</summary>
    public static JsonObject DependencyPath(IReadOnlyList<long> allowed, SqliteConnection conn, string from, string to)
    {
        var estate = Load(allowed, conn);
        if (Find(estate, from) is not { } a) return new JsonObject { ["error"] = $"No repository {from} that you may read." };
        if (Find(estate, to) is not { } b) return new JsonObject { ["error"] = $"No repository {to} that you may read." };
        foreach (var (start, end, direction) in new[] { (a.Id, b.Id, "uses"), (b.Id, a.Id, "used_by") })
        {
            var paths = Shortest(estate, start, end, 3);
            if (paths.Count == 0) continue;
            return new JsonObject
            {
                ["from"] = estate.Name(a.Id),
                ["to"] = estate.Name(b.Id),
                ["direction"] = direction == "uses" ? $"{estate.Name(a.Id)} depends on {estate.Name(b.Id)}" : $"{estate.Name(b.Id)} depends on {estate.Name(a.Id)} (not the other way)",
                ["paths"] = new JsonArray([.. paths.Select(p => (JsonNode)new JsonObject
                {
                    ["length"] = p.Count - 1,
                    ["steps"] = new JsonArray([.. p.Zip(p.Skip(1)).Select(s => (JsonNode)new JsonObject
                    {
                        ["from"] = estate.Name(s.First), ["to"] = estate.Name(s.Second), ["by"] = Ways(estate.Edges[(s.First, s.Second)], 3),
                    })]),
                })]),
            };
        }
        return new JsonObject
        {
            ["from"] = estate.Name(a.Id), ["to"] = estate.Name(b.Id), ["paths"] = new JsonArray(),
            ["note"] = "Neither depends on the other through what the index has resolved (packages, imports, #includes, submodules, CI includes, images) among the repositories you may read. They may still talk at run time (a service's API, a queue): search_code for its address or name.",
        };
    }

    /// <summary>Up to k shortest paths from start to end (breadth first, every shortest parent kept).</summary>
    static List<List<long>> Shortest(Estate estate, long start, long end, int k)
    {
        var parents = new Dictionary<long, List<long>> { [start] = [] };
        var distance = new Dictionary<long, int> { [start] = 0 };
        var queue = new Queue<long>();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            var at = queue.Dequeue();
            if (at == end) break;
            foreach (var e in estate.Out.GetValueOrDefault(at) ?? [])
            {
                if (!distance.TryGetValue(e.To, out var d))
                {
                    distance[e.To] = distance[at] + 1;
                    parents[e.To] = [at];
                    queue.Enqueue(e.To);
                }
                else if (d == distance[at] + 1)
                {
                    parents[e.To].Add(at);
                }
            }
        }
        if (!distance.ContainsKey(end)) return [];
        var found = new List<List<long>>();
        void Back(long node, List<long> suffix)
        {
            if (found.Count >= k) return;
            var path = suffix.Prepend(node).ToList();
            if (node == start)
            {
                found.Add(path);
                return;
            }
            foreach (var p in parents[node]) Back(p, path);
        }
        Back(end, []);
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
    public static JsonObject ChangeImpact(IReadOnlyList<long> allowed, SqliteConnection conn, string repo, string? symbol = null, int depth = 3)
    {
        var estate = Load(allowed, conn);
        if (Find(estate, repo) is not { } changed) return new JsonObject { ["error"] = $"No repository {repo} that you may read." };
        var reached = Walk(estate, changed.Id, uses: false, Math.Clamp(depth, 1, 8));
        var dependents = new JsonArray();
        foreach (var level in reached.GroupBy(r => r.Depth).OrderBy(g => g.Key))
        {
            dependents.Add(new JsonObject
            {
                ["depth"] = level.Key,
                ["repos"] = new JsonArray([.. level.OrderBy(r => estate.Name(r.Id), StringComparer.Ordinal).Select(r => (JsonNode)new JsonObject
                {
                    ["repo"] = estate.Name(r.Id),
                    ["through"] = estate.Name(r.Via),
                    ["by"] = Ways(estate.Edges[(r.Id, r.Via)], 2),
                })]),
            });
        }
        var result = new JsonObject
        {
            ["repo"] = estate.Name(changed.Id),
            ["dependents"] = reached.Count,
            ["by_depth"] = dependents,
            ["deeper"] = Walk(estate, changed.Id, uses: false, 50).Count > reached.Count ? "more repositories depend on it further away: raise depth" : null,
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

    /// <summary>A repository's links for overview: whom it uses and who uses it, with how, most files first.</summary>
    public static (JsonArray Uses, JsonArray UsedBy) Neighbours(Estate estate, long repoId, int most = 10)
    {
        JsonArray Side(IEnumerable<Edge> edges, bool uses) => new([.. edges.OrderByDescending(e => e.Files).Take(most).Select(e => (JsonNode)new JsonObject
        {
            ["path_with_namespace"] = estate.Name(uses ? e.To : e.From),
            ["weight"] = e.Files,
            ["by"] = string.Join(", ", e.Ways.Select(w => w.Kind).Distinct()),
        })]);
        return (Side(estate.Out.GetValueOrDefault(repoId) ?? [], true), Side(estate.In.GetValueOrDefault(repoId) ?? [], false));
    }
}
