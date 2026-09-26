using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Argus.Indexing;
using Argus.Util;
using Microsoft.Data.Sqlite;
using ZstdSharp;

namespace Argus.Packs;

/// <summary>A query against an installed pack failed.</summary>
public sealed class PackQueryError(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>One opened pack: name, file, connection and metadata.</summary>
public sealed class Pack(string name, string path, SqliteConnection conn, Dictionary<string, string> meta) : IDisposable
{
    public string Name { get; } = name;
    public string Path { get; } = path;
    public SqliteConnection Conn { get; } = conn;
    public Dictionary<string, string> Meta { get; } = meta;
    public string License => Meta.GetValueOrDefault("license", "");
    public string Attribution => Meta.GetValueOrDefault("attribution", "");
    public string UrlBase => Meta.GetValueOrDefault("source_repo", "");
    public void Dispose() => Conn.Dispose();
}

/// <summary>
/// Reading installed packs: exact lookup, full-text and
/// semantic search, the hybrid "find by behaviour" ranking, the contract sheet
/// for a source file, and verify-after for a draft.
/// </summary>
public static class PackStore
{
    public const int DefaultCoarse = 600;
    public const int MaxChunksPerDoc = 2;

    public static List<Pack> OpenPacks(IEnumerable<string> paths)
    {
        var opened = new List<Pack>();
        try
        {
            foreach (var raw in paths)
            {
                SqliteConnection conn;
                try { conn = PackFormat.OpenPack(raw); }
                catch (SqliteException exc) { throw new PackQueryError($"cannot open pack {raw}: {Queries_SqliteMessage(exc)}", exc); }
                var meta = PackFormat.ReadMeta(conn);
                opened.Add(new Pack(meta.GetValueOrDefault("source_name", System.IO.Path.GetFileNameWithoutExtension(raw)), raw, conn, meta));
            }
        }
        catch
        {
            ClosePacks(opened);
            throw;
        }
        return opened;
    }

    static string Queries_SqliteMessage(SqliteException exc) => Store.Queries.SqliteMessage(exc);

    public static void ClosePacks(IEnumerable<Pack> packs)
    {
        foreach (var p in packs)
        {
            try { p.Dispose(); } catch (SqliteException) { }
        }
    }

    public static List<Pack> SelectPacks(IReadOnlyList<Pack> packs, string? lang)
    {
        if (string.IsNullOrEmpty(lang)) return packs.ToList();
        var wanted = PyStr.Strip(lang).ToLowerInvariant();
        return packs.Where(p => p.Name.ToLowerInvariant() == wanted).ToList();
    }

    static List<Row> Query(Pack pack, string sql, params object?[] args)
    {
        try { return Sql.QueryList(pack.Conn, sql, args); }
        catch (SqliteException exc) { throw new PackQueryError($"query against pack {PyStr.Repr(pack.Name)} failed: {Store.Queries.SqliteMessage(exc)}", exc); }
    }

    static JsonObject Attributed(Pack pack, JsonObject row)
    {
        row["source"] = pack.Name;
        row["license"] = pack.License;
        row["attribution"] = pack.Attribution;
        return row;
    }

    static string Anchored(string? url, string? anchor)
    {
        if (string.IsNullOrEmpty(url)) return "";
        return string.IsNullOrEmpty(anchor) ? url : $"{url}#{anchor}";
    }

    [ThreadStatic] static Decompressor? _decompressor;

    public static string Decompress(byte[]? blob)
    {
        if (blob is null || blob.Length == 0) return "";
        _decompressor ??= new Decompressor();
        return Utf8.DecodeReplace(_decompressor.Unwrap(blob).ToArray());
    }

    static string S(JsonObject row, string key) => row[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : (row[key]?.ToString() ?? "");

    // --- exact lookup ---------------------------------------------------------------

    static JsonObject SymbolRow(Pack pack, Row row, double? score = null)
    {
        var obj = new JsonObject
        {
            ["name"] = PyJson.From(row["name"]),
            ["kind"] = PyJson.From(row["kind"]),
            ["namespace"] = PyJson.From(row["namespace"]),
            ["signature"] = PyJson.From(row["signature"]),
            ["title"] = PyJson.From(row["title"]),
            ["doc_path"] = PyJson.From(row["path"]),
            ["anchor"] = PyJson.From(row["anchor"]),
            ["url"] = Anchored(row.StrOrNull("url"), row.StrOrNull("anchor")),
        };
        // Key order is part of the answer: a scored row carries its score before
        // the attribution, exactly where the Python dict literal puts it.
        if (score is not null) obj["score"] = score.Value;
        return Attributed(pack, obj);
    }

    public static List<JsonObject> LookupSymbol(IReadOnlyList<Pack> packs, string name, string? lang = null, int limit = 20)
    {
        var results = new List<JsonObject>();
        foreach (var pack in SelectPacks(packs, lang))
            foreach (var row in Query(pack, """
                         SELECT s.name, s.kind, s.namespace, s.anchor, s.signature,
                                d.title, d.url, d.path
                         FROM api_symbols s JOIN docs d ON d.id = s.doc_id
                         WHERE s.name = ? OR lower(s.name) = lower(?)
                         ORDER BY (s.name = ?) DESC, s.name
                         LIMIT ?
                         """, name, name, name, (long)limit))
                results.Add(SymbolRow(pack, row));

        var sorted = results.OrderBy(r => Authority(r, name), AuthorityComparer.Instance).ToList();
        if (sorted.Count > 1)
            sorted = sorted.OrderBy(r => Authority(r, name), AuthorityComparer.Two)
                .ThenBy(r => -NamespaceBreadth(packs, r)).ToList();
        return sorted.Take(limit).ToList();
    }

    static readonly ConcurrentDictionary<(string, string), long> BreadthCache = new();

    static long NamespaceBreadth(IReadOnlyList<Pack> packs, JsonObject row)
    {
        var source = S(row, "source");
        var ns = row["namespace"] is null ? "" : S(row, "namespace");
        if (ns.Length == 0) return 0;
        return BreadthCache.GetOrAdd((source, ns), key =>
        {
            foreach (var pack in packs)
            {
                if (pack.Name != key.Item1) continue;
                try
                {
                    var rows = Query(pack, "SELECT COUNT(*) AS n FROM api_symbols WHERE namespace = ?", key.Item2);
                    return rows.Count > 0 ? rows[0].Long("n") : 0;
                }
                catch (PackQueryError) { return 0; }
            }
            return 0;
        });
    }

    /// <summary>How authoritative a hit is for the name asked about; lower sorts first.</summary>
    static (bool, bool, int, int) Authority(JsonObject row, string name)
    {
        var docPath = S(row, "doc_path");
        var stem = PyStr.AfterLast(docPath, '/');
        foreach (var suffix in new[] { ".md", ".mdx", ".rst", ".html" })
            if (stem.EndsWith(suffix, StringComparison.Ordinal)) { stem = stem[..^suffix.Length]; break; }
        return (S(row, "name") != name,
            stem.ToLowerInvariant() != PyStr.AfterLast(name, '.').ToLowerInvariant(),
            PyStr.Count(docPath, '/'),
            PyStr.Len(docPath));
    }

    sealed class AuthorityComparer(int width) : IComparer<(bool, bool, int, int)>
    {
        public static readonly AuthorityComparer Instance = new(4);
        public static readonly AuthorityComparer Two = new(2);

        public int Compare((bool, bool, int, int) a, (bool, bool, int, int) b)
        {
            int c = a.Item1.CompareTo(b.Item1);
            if (c != 0 || width == 1) return c;
            c = a.Item2.CompareTo(b.Item2);
            if (c != 0 || width == 2) return c;
            c = a.Item3.CompareTo(b.Item3);
            if (c != 0 || width == 3) return c;
            return a.Item4.CompareTo(b.Item4);
        }
    }

    // --- full text ------------------------------------------------------------------

    static readonly Regex FtsTerm = new(@"\w[\w.:+#-]*", RegexOptions.CultureInvariant);

    public static string FtsMatch(string? raw) =>
        string.Join(" ", FtsTerm.Matches(raw ?? "").Select(m => "\"" + m.Value.Replace("\"", "\"\"") + "\""));

    public static List<JsonObject> SearchText(IReadOnlyList<Pack> packs, string query, string? lang = null, int limit = 20)
    {
        var match = FtsMatch(query);
        if (match.Length == 0) return [];
        var results = new List<(double Rank, JsonObject Row)>();
        foreach (var pack in SelectPacks(packs, lang))
        {
            List<Row> rows;
            try
            {
                rows = Sql.Query(pack.Conn, """
                    SELECT d.id, d.title, d.url, d.path, d.content,
                           bm25(docs_fts) AS rank
                    FROM docs_fts JOIN docs d ON d.id = docs_fts.rowid
                    WHERE docs_fts MATCH ?
                    ORDER BY rank
                    LIMIT ?
                    """, match, (long)limit);
            }
            catch (SqliteException exc)
            {
                throw new PackQueryError($"invalid search query {PyStr.Repr(query)}: {Store.Queries.SqliteMessage(exc)}", exc);
            }
            foreach (var row in rows)
                results.Add((row.Double("rank"), Attributed(pack, new JsonObject
                {
                    ["title"] = PyJson.From(row["title"]),
                    ["doc_path"] = PyJson.From(row["path"]),
                    ["url"] = PyJson.From(row["url"]),
                    ["excerpt"] = Excerpt(row.Bytes("content"), query),
                    ["score"] = -row.Double("rank"),
                })));
        }
        return results.OrderBy(r => r.Rank).Take(limit).Select(r => r.Row).ToList();
    }

    static string Excerpt(byte[]? blob, string query, int width = 320)
    {
        var text = Decompress(blob);
        if (text.Length == 0) return "";
        var terms = PyStr.SplitWhitespace(query.Replace('"', ' ')).Where(PyStr.IsAlnum).ToList();
        var lowered = text.ToLowerInvariant();
        int position = -1;
        foreach (var term in terms)
        {
            position = lowered.IndexOf(term.ToLowerInvariant(), StringComparison.Ordinal);
            if (position >= 0) break;
        }
        if (position < 0) return PyStr.Strip(PyStr.Prefix(text, width));
        int start = Math.Max(0, position - width / 3);
        return PyStr.Strip(PyStr.Slice(text, start, width));
    }

    // --- semantic -------------------------------------------------------------------

    static readonly Regex IdentRe = new(
        @"\b(?:[A-Za-z]+_[A-Za-z0-9_]+|(?:[A-Z][a-z0-9]+){2,}[A-Za-z0-9]*|[A-Za-z][A-Za-z0-9]*\.(?:h|lib|dll)|/[A-Za-z][A-Za-z0-9:]*)\b",
        RegexOptions.CultureInvariant);

    public static List<string> QueryTerms(string? text)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var output = new List<string>();
        foreach (Match m in IdentRe.Matches(text ?? ""))
            if (seen.Add(m.Value)) output.Add(m.Value.ToLowerInvariant());
        return output;
    }

    static List<(double, JsonObject)> Mentioning(List<(double, JsonObject)> scored, List<string> terms)
    {
        if (terms.Count == 0) return scored;
        return scored.Where(p => terms.Any(t =>
            S(p.Item2, "text").ToLowerInvariant().Contains(t, StringComparison.Ordinal)
            || S(p.Item2, "title").ToLowerInvariant().Contains(t, StringComparison.Ordinal)
            || S(p.Item2, "doc_path").ToLowerInvariant().Contains(t, StringComparison.Ordinal))).ToList();
    }

    static List<(long, double)> CoarseAndRescore(Pack pack, IReadOnlyList<double> queryVec, int coarse)
    {
        var candidates = Query(pack, "SELECT chunk_id FROM vec_bin WHERE embedding MATCH vec_bit(?) AND k = ?",
            Quantize.ToBits(queryVec), (long)coarse).Select(r => r.Long("chunk_id")).ToList();
        if (candidates.Count == 0) return [];
        var vectors = Query(pack, $"SELECT chunk_id, embedding FROM vec_i8 WHERE chunk_id IN ({Sql.Marks(candidates.Count)})",
            candidates.Cast<object?>().ToArray());
        return Quantize.Rescore(queryVec, vectors.Select(r => (r.Long("chunk_id"), r.Bytes("embedding") ?? [])).ToList());
    }

    public static List<JsonObject> SearchDocs(IReadOnlyList<Pack> packs, IReadOnlyList<double> queryVec, string? lang = null,
        int limit = 10, int coarse = DefaultCoarse, string queryText = "")
    {
        var selected = SelectPacks(packs, lang);
        foreach (var pack in selected) PackFormat.RequireCompatible(pack.Meta, Embed.Model, Embed.Dim);
        var scored = new List<(double, JsonObject)>();
        foreach (var pack in selected)
        {
            var ranked = CoarseAndRescore(pack, queryVec, coarse);
            var top = ranked.Take(limit * MaxChunksPerDoc * 3).ToList();
            if (top.Count == 0) continue;
            var byId = new Dictionary<long, double>();
            foreach (var (id, score) in top) byId[id] = score;
            var rows = Query(pack, $"""
                SELECT c.id, c.heading_path, c.anchor, c.start_line, c.text,
                       d.title, d.url, d.path
                FROM chunks c JOIN docs d ON d.id = c.doc_id
                WHERE c.id IN ({Sql.Marks(byId.Count)})
                """, byId.Keys.Cast<object?>().ToArray());
            foreach (var row in rows)
            {
                var score = byId[row.Long("id")];
                scored.Add((score, Attributed(pack, new JsonObject
                {
                    ["title"] = PyJson.From(row["title"]),
                    ["doc_path"] = PyJson.From(row["path"]),
                    ["heading_path"] = PyJson.From(row["heading_path"]),
                    ["anchor"] = PyJson.From(row["anchor"]),
                    ["start_line"] = PyJson.From(row["start_line"]),
                    ["url"] = Anchored(row.StrOrNull("url"), row.StrOrNull("anchor")),
                    ["text"] = Decompress(row.Bytes("text")),
                    ["score"] = score,
                })));
            }
        }
        scored = scored.OrderBy(p => -p.Item1).ToList();
        scored = Mentioning(scored, QueryTerms(queryText));
        var perDoc = new Dictionary<(string, string), int>();
        var output = new List<JsonObject>();
        foreach (var (_, row) in scored)
        {
            var key = (S(row, "source"), S(row, "doc_path"));
            var seen = perDoc.GetValueOrDefault(key, 0);
            if (seen >= MaxChunksPerDoc) continue;
            perDoc[key] = seen + 1;
            output.Add(row);
            if (output.Count >= limit) break;
        }
        return output;
    }

    public static JsonObject? GetDoc(IReadOnlyList<Pack> packs, string docPath, string? source = null, int maxChars = 60000)
    {
        foreach (var pack in packs)
        {
            if (!string.IsNullOrEmpty(source) && pack.Name.ToLowerInvariant() != source.ToLowerInvariant()) continue;
            var rows = Query(pack, "SELECT path, title, url, lang, content, content_len FROM docs WHERE path = ?", docPath);
            if (rows.Count == 0) continue;
            var row = rows[0];
            var text = Decompress(row.Bytes("content"));
            int len = PyStr.Len(text);
            return Attributed(pack, new JsonObject
            {
                ["doc_path"] = PyJson.From(row["path"]),
                ["title"] = PyJson.From(row["title"]),
                ["url"] = PyJson.From(row["url"]),
                ["lang"] = PyJson.From(row["lang"]),
                ["text"] = PyStr.Prefix(text, maxChars),
                ["truncated"] = len > maxChars,
                ["full_length"] = len,
            });
        }
        return null;
    }

    // --- verify-after -----------------------------------------------------------------

    static readonly Regex Sentences = new(@"(?<=[.!?])\s+|\n+", RegexOptions.CultureInvariant);

    static readonly (string Field, Regex Shape)[] FieldShapes =
    [
        ("header", new Regex(@"\b[a-z0-9_]+\.h\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
        ("library", new Regex(@"\b[a-z0-9_]+\.lib\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
        ("dll", new Regex(@"\b[a-z0-9_]+\.dll\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
        ("irql", new Regex(@"\b[A-Z]+_LEVEL\b", RegexOptions.CultureInvariant)),
    ];

    static readonly Regex IdentifierRe = new(
        @"\b(?:[A-Z][a-z0-9]+){2,}[A-Za-z0-9_]*\b|\b[A-Z][A-Z0-9]{2,}(?:_[A-Z0-9]+)+\b|\b[A-Z][a-z]+-[A-Z][a-z]+\b|\bC[0-9]{4,5}\b",
        RegexOptions.CultureInvariant);

    /// <summary>Python's re.split with a zero-width-capable pattern (no captured groups here).</summary>
    static List<string> ReSplit(Regex re, string text)
    {
        var parts = new List<string>();
        int last = 0;
        foreach (Match m in re.Matches(text))
        {
            parts.Add(text.Substring(last, m.Index - last));
            last = m.Index + m.Length;
        }
        parts.Add(text.Substring(last));
        return parts;
    }

    public static List<JsonObject> VerifyText(IReadOnlyList<Pack> packs, string text, int limit = 40)
    {
        var resolved = new List<(string Name, JsonObject Hit)>();
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in IdentifierRe.Matches(text))
        {
            var name = m.Value;
            if (!seenNames.Add(name)) continue;
            if (name.Length < 4) continue;
            var hits = LookupSymbol(packs, name, limit: 1);
            if (hits.Count > 0) resolved.Add((name, hits[0]));
            if (resolved.Count >= limit) break;
        }

        var known = new HashSet<string>(resolved.Select(r => r.Name), StringComparer.Ordinal);
        var seen = new List<(string Name, JsonObject Finding)>();
        foreach (var (name, hit) in resolved)
        {
            var near = known.Count == 1 ? text : string.Join(" ", ReSplit(Sentences, text).Where(s => s.Contains(name, StringComparison.Ordinal)));
            var documented = new List<(string K, string V)>();
            var signature = hit["signature"] is null ? "None" : S(hit, "signature");
            // The contract ends at the " -- " marker the adapters put before the
            // API's description; without the cut the last field carried the prose,
            // and `documented` is the string a model is told to copy verbatim.
            int marker = signature.IndexOf(" -- ", StringComparison.Ordinal);
            var contract = marker < 0 ? signature : signature[..marker];
            foreach (var part in contract.Split(';'))
            {
                int colon = part.IndexOf(':');
                if (colon < 0) continue;
                documented.Add((part.Substring(0, colon), PyStr.Strip(part.Substring(colon + 1))));
            }
            // dict() keeps the LAST value for a repeated key, in first-seen order.
            var docDict = new List<(string K, string V)>();
            foreach (var (k, v) in documented)
            {
                int idx = docDict.FindIndex(d => d.K == k);
                if (idx >= 0) docDict[idx] = (k, v); else docDict.Add((k, v));
            }
            var fields = new JsonArray();
            var corrections = new JsonArray();
            foreach (var (field, shape) in FieldShapes)
            {
                var value = docDict.FirstOrDefault(d => PyStr.Strip(d.K).ToLowerInvariant() == field).V ?? "";
                if (value.Length == 0) continue;
                // As the draft spelled them, so a correction can quote them back.
                var said = shape.Matches(near).Select(x => x.Value).Distinct(StringComparer.Ordinal).ToList();
                var stated = said.Select(x => x.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
                var wanted = shape.Matches(value).Select(x => x.Value.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
                if (wanted.Count == 0) continue;
                var status = wanted.Overlaps(stated) ? "confirmed" : stated.Count > 0 ? "contradicted" : "unstated";
                var entry = new JsonObject { ["field"] = field, ["documented"] = value, ["status"] = status };
                if (status == "contradicted") entry["stated"] = string.Join(", ", said);
                fields.Add(entry);
                if (status == "contradicted") corrections.Add(entry.DeepClone());
            }
            if (fields.Count > 0)
            {
                var finding = new JsonObject
                {
                    ["name"] = hit["name"]?.DeepClone() ?? name,
                    ["source"] = hit["source"]?.DeepClone(),
                    ["url"] = hit["url"]?.DeepClone(),
                    ["doc_path"] = hit["doc_path"]?.DeepClone(),
                    ["fields"] = fields,
                    ["corrections"] = corrections,
                };
                int idx = seen.FindIndex(s => s.Name == name);
                if (idx >= 0) seen[idx] = (name, finding); else seen.Add((name, finding));
            }
        }
        return seen.Select(s => s.Finding).ToList();
    }

    // --- find by behaviour ----------------------------------------------------------

    static readonly HashSet<string> Stopwords = new(PyStr.SplitWhitespace("""
        a an the and or of to in on for from with by is are be as at it its this that
        which what when where how do does use used using return returns value values
        one all any if then than into out about over can may must should will would
        command cmdlet function specifies specified given only also such more most
        """), StringComparer.Ordinal);

    public static List<string> QueryTermsForSymbols(string query)
    {
        var words = PyStr.SplitWhitespace(query).Select(w => PyStr.Strip(w, ".,;:!?()[]{}\"'`").ToLowerInvariant());
        return words.Where(w => PyStr.Len(w) > 2 && !Stopwords.Contains(w)).ToList();
    }

    const double NameWeight = 0.0;
    const double CoverageBonus = 0.35;
    const int PackCap = 3;
    const double SemanticWeight = 1.0;
    const int ChunkCap = 3;

    public static List<JsonObject> SearchSymbols(IReadOnlyList<Pack> packs, string query, string? lang = null, int limit = 10)
    {
        var terms = QueryTermsForSymbols(query);
        if (terms.Count == 0) return [];
        var candidates = new List<(Pack Pack, Row Row)>();
        foreach (var pack in SelectPacks(packs, lang))
        {
            var where = string.Join(" OR ", terms.Select(_ => "lower(s.signature) LIKE ? OR lower(s.name) LIKE ?"));
            var args = new List<object?>();
            foreach (var t in terms) { args.Add($"%{t}%"); args.Add($"%{t}%"); }
            candidates.AddRange(Query(pack, $"""
                SELECT s.name, s.kind, s.namespace, s.anchor, s.signature,
                       d.title, d.url, d.path
                FROM api_symbols s JOIN docs d ON d.id = s.doc_id
                WHERE s.signature != '' AND ({where})
                """, args.ToArray()).Select(r => (pack, r)));
        }
        if (candidates.Count == 0) return [];

        var nameFrequency = terms.Distinct().ToDictionary(t => t, _ => 0);
        var textFrequency = terms.Distinct().ToDictionary(t => t, _ => 0);
        foreach (var (_, row) in candidates)
        {
            var nameLow = SqlLower(row.Str("name"));
            var textLow = SqlLower(row.Str("signature"));
            foreach (var t in terms)
            {
                if (nameLow.Contains(t, StringComparison.Ordinal)) nameFrequency[t]++;
                if (textLow.Contains(t, StringComparison.Ordinal)) textFrequency[t]++;
            }
        }

        var scored = new List<(double, JsonObject)>();
        foreach (var (pack, row) in candidates)
        {
            var nameLow = row.Str("name").ToLowerInvariant();
            var textLow = row.Str("signature").ToLowerInvariant();
            double score = 0;
            int matchedTerms = 0;
            foreach (var t in terms)
            {
                double best = 0;
                if (textLow.Contains(t, StringComparison.Ordinal)) best = 1.0 / (1.0 + textFrequency[t] / 50.0);
                if (nameLow.Contains(t, StringComparison.Ordinal)) best = Math.Max(best, NameWeight / (1.0 + nameFrequency[t] / 50.0));
                if (best != 0) { score += best; matchedTerms++; }
            }
            if (score == 0) continue;
            score *= 1.0 + CoverageBonus * (matchedTerms - 1);
            scored.Add((score, SymbolRow(pack, row, Math.Round(score, 4, MidpointRounding.ToEven))));
        }
        return Capped(scored.OrderBy(p => -p.Item1).ToList(), limit);
    }

    /// <summary>Python's str.lower() for the frequency pass, which runs over text SQLite already matched.</summary>
    static string SqlLower(string s) => s.ToLowerInvariant();

    static List<JsonObject> Capped(List<(double, JsonObject)> scored, int limit)
    {
        var taken = new List<JsonObject>();
        var overflow = new List<JsonObject>();
        var perPack = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (_, row) in scored)
        {
            var source = S(row, "source");
            if (perPack.GetValueOrDefault(source, 0) < PackCap)
            {
                perPack[source] = perPack.GetValueOrDefault(source, 0) + 1;
                taken.Add(row);
            }
            else overflow.Add(row);
            if (taken.Count >= limit) return taken.Take(limit).ToList();
        }
        return taken.Concat(overflow).Take(limit).ToList();
    }

    public static List<JsonObject> SearchSymbolsHybrid(IReadOnlyList<Pack> packs, string query, IReadOnlyList<double>? queryVec = null,
        string? lang = null, int limit = 10, int coarse = DefaultCoarse)
    {
        var lexical = SearchSymbols(packs, query, lang, limit * 3);
        var best = new Dictionary<(string, string), (double Score, JsonObject Row)>();
        var order = new List<(string, string)>();
        var origin = new Dictionary<(string, string), long?>();

        void Keep(JsonObject row, double score, long? chunkId = null)
        {
            var key = (S(row, "source"), S(row, "name"));
            if (!best.TryGetValue(key, out var existing) || score > existing.Score)
            {
                var copy = (JsonObject)row.DeepClone();
                copy["score"] = Math.Round(score, 4, MidpointRounding.ToEven);
                if (!best.ContainsKey(key)) order.Add(key);
                best[key] = (score, copy);
                origin[key] = chunkId;
            }
        }

        var terms = QueryTermsForSymbols(query);
        int termCount = Math.Max(terms.Count, 1);
        foreach (var row in lexical)
        {
            double raw = row["score"] is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0.0;
            Keep(row, Math.Min(raw / termCount, 1.0));
        }

        if (queryVec is not null)
        {
            foreach (var pack in SelectPacks(packs, lang))
            {
                try { PackFormat.RequireCompatible(pack.Meta, Embed.Model, Embed.Dim); }
                catch (PackMismatch) { continue; }
                foreach (var (chunkId, score) in CoarseAndRescore(pack, queryVec, coarse).Take(limit))
                    foreach (var row in SymbolsForChunk(pack, chunkId, terms))
                        Keep(row, SemanticWeight * score, chunkId);
            }
        }

        var ranked = order.Select(k => (Key: k, best[k].Score, best[k].Row)).OrderBy(x => -x.Score).ToList();
        var taken = new List<JsonObject>();
        var overflow = new List<JsonObject>();
        var perChunk = new Dictionary<long, int>();
        foreach (var (key, _, row) in ranked)
        {
            var chunkId = origin.GetValueOrDefault(key);
            if (chunkId is null || perChunk.GetValueOrDefault(chunkId.Value, 0) < ChunkCap)
            {
                if (chunkId is not null) perChunk[chunkId.Value] = perChunk.GetValueOrDefault(chunkId.Value, 0) + 1;
                taken.Add(row);
            }
            else overflow.Add(row);
            if (taken.Count >= limit) return taken.Take(limit).ToList();
        }
        return taken.Concat(overflow).Take(limit).ToList();
    }

    static List<JsonObject> SymbolsForChunk(Pack pack, long chunkId, IReadOnlyList<string> terms)
    {
        var textRows = Query(pack, "SELECT text FROM chunks WHERE id = ?", chunkId);
        var text = textRows.Count > 0 ? Decompress(textRows[0].Bytes("text")) : "";
        var hits = terms.Count > 0
            ? string.Join(" + ", Enumerable.Repeat("(instr(lower(s.name) || ' ' || lower(s.signature), ?) > 0)", terms.Count))
            : "0";
        var args = new List<object?> { chunkId, text };
        args.AddRange(terms);
        return Query(pack, $"""
            SELECT s.name, s.kind, s.namespace, s.anchor, s.signature,
                   d.title, d.url, d.path
            FROM chunks c
            JOIN api_symbols s ON s.doc_id = c.doc_id
            JOIN docs d ON d.id = c.doc_id
            WHERE c.id = ?
            ORDER BY (s.name != '' AND instr(?, s.name) > 0) DESC,
                     ({hits}) DESC, s.name
            LIMIT 8
            """, args.ToArray()).Select(r => SymbolRow(pack, r)).ToList();
    }

    // --- contract sheet -------------------------------------------------------------

    static readonly Regex SourceApiRe = new(@"\b(?:[A-Z][a-z0-9]+){2,}[A-Za-z0-9_]*\b|\b[A-Z][a-z]+-[A-Z][a-z]+\b", RegexOptions.CultureInvariant);

    static readonly HashSet<string> ContractNoise = new(StringComparer.Ordinal)
    {
        "ContainingRecord", "InitializeListHead", "InsertHeadList", "RemoveEntryList", "RemoveHeadList",
        "IsListEmpty", "InsertTailList", "ListEntry", "InterlockedIncrement", "InterlockedDecrement", "InterlockedExchange",
    };

    public static List<JsonObject> ApiContracts(IReadOnlyList<Pack> packs, string source, int limit = 40)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Match m in SourceApiRe.Matches(source ?? ""))
        {
            var name = m.Value;
            if (name.Length < 6 || ContractNoise.Contains(name)) continue;
            counts[name] = counts.GetValueOrDefault(name, 0) + 1;
        }
        var output = new List<JsonObject>();
        foreach (var name in counts.Keys.OrderBy(n => -counts[n]).ThenBy(n => n, StringComparer.Ordinal))
        {
            var hits = LookupSymbol(packs, name, limit: 1);
            if (hits.Count == 0) continue;
            var row = (JsonObject)hits[0].DeepClone();
            row["mentions"] = counts[name];
            output.Add(row);
            if (output.Count >= limit) break;
        }
        return output;
    }
}
