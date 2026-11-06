using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Argus.Util;
using Microsoft.Data.Sqlite;

namespace Argus.Store;

/// <summary>
/// The wiki's code search: what a person types, found in the code they may read, ranked, a page at a time.
///
/// A name comes first: the symbols it names, with what qualifies it (MyLib::TestFunc, a.b.c, obj->field) matched
/// against their scope, any case, the start of a name, and its words (test func finds TestFunc and test_func). Then
/// the file it names, then the files that hold the words, ranked by BM25 with a word in the path counting more.
/// Each typed word is a phrase to the full-text index, so "::" or "." never is its syntax. Filters narrow it by
/// repository, language, path, kind (symbol, code, doc); facets say where the matches are, so a filter can be
/// picked from them.
///
/// Like every read of the private index, the allowlist comes first and an empty one finds nothing.
/// </summary>
public static class CodeSearch
{
    public const int DefaultPerPage = 20;
    public const int MaxPerPage = 100;

    /// <summary>How far pages reach: a search with more is narrowed with the filters, not read to its end.</summary>
    public const int MaxReach = 2000;

    /// <summary>The symbols a name may match before they are ranked: more is a name too short to mean one thing.</summary>
    const int SymbolCap = 1000;

    /// <summary>The file languages that are documents rather than code.</summary>
    public static readonly HashSet<string> DocLangs = new(StringComparer.Ordinal) { "markdown", "text" };

    public static readonly string[] Kinds = ["symbol", "code", "doc"];

    public sealed record Filter(string? Repo = null, string? Lang = null, string? Path = null, string? Kind = null);

    /// <summary>A typed name: its last part, what qualifies it (its scope, joined with dots), and its words.</summary>
    public sealed record Name(string Text, string Last, string? Qualifier, string[] Words);

    static readonly Regex SeparatorRe = new(@"::|->|[.#\\/]", RegexOptions.CultureInvariant);
    static readonly Regex IdentRe = new(@"^[A-Za-z_$~][A-Za-z0-9_$]*$", RegexOptions.CultureInvariant);
    static readonly Regex WordRe = new(@"[A-Z]+(?![a-z])|[A-Z]?[a-z]+|[0-9]+", RegexOptions.CultureInvariant);
    static readonly Regex TermRe = new(@"[\p{L}\p{N}]", RegexOptions.CultureInvariant);

    /// <summary>A name's words, lower case: camel humps, snake and kebab parts, digits apart.</summary>
    public static string[] Words(string text) => [.. WordRe.Matches(text).Select(m => m.Value.ToLowerInvariant())];

    /// <summary>The name a token spells, or null when it is not one (a phrase, a number, punctuation).</summary>
    public static Name? ParseName(string token)
    {
        var parts = SeparatorRe.Split(token).Where(p => p.Length > 0).ToArray();
        if (parts.Length == 0 || !parts.All(IdentRe.IsMatch)) return null;
        return new Name(token, parts[^1], parts.Length > 1 ? string.Join(".", parts[..^1]) : null, Words(parts[^1]));
    }

    /// <summary>A scope as dots: MyLib::Widget, MyLib.Widget and MyLib/Widget read alike.</summary>
    static string Dotted(string? scope) => scope is null ? "" : SeparatorRe.Replace(scope, ".");

    static bool Qualifies(string? scope, string qualifier)
    {
        var s = Dotted(scope);
        return s.Equals(qualifier, StringComparison.OrdinalIgnoreCase) || s.EndsWith("." + qualifier, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The query as full-text: each typed word a phrase, so its punctuation is never syntax.</summary>
    public static string? FtsQuery(IEnumerable<string> tokens)
    {
        var phrases = tokens.Where(t => TermRe.IsMatch(t)).Select(t => "\"" + t.Replace("\"", "\"\"") + "\"").ToList();
        return phrases.Count > 0 ? string.Join(" ", phrases) : null;
    }

    static string Like(string value) => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    /// <summary>How well a symbol matches the name typed, 0 for not at all, and why. Another scope than the one typed costs 30.</summary>
    public static (int Score, string How) Rank(Name typed, string name, string? scope)
    {
        bool single = !typed.Text.Contains(' ');
        bool several = typed.Words.Length > 1;
        var own = Words(name);
        var (score, how) =
            single && name == typed.Last ? (typed.Qualifier is null ? 95 : 100, typed.Qualifier is null ? "name" : "name and scope")
            : name.Equals(typed.Last, StringComparison.OrdinalIgnoreCase) ? (85, single ? "name, another case" : "same words")
            : several && own.SequenceEqual(typed.Words) ? (85, "same words")
            : name.StartsWith(typed.Last, StringComparison.OrdinalIgnoreCase) ? (50, "start of a name")
            : several && InOrder(typed.Words, own) ? (40, "words of a name")
            : (0, "");
        if (score == 0 || typed.Qualifier is null || Qualifies(scope, typed.Qualifier)) return (score, how);
        return (score - 30, how + ", another scope");
    }

    /// <summary>Each typed word starts a word of the name, in the order typed.</summary>
    static bool InOrder(string[] typed, string[] own)
    {
        int at = 0;
        foreach (var w in typed)
        {
            while (at < own.Length && !own[at].StartsWith(w, StringComparison.Ordinal)) at++;
            if (at == own.Length) return false;
            at++;
        }
        return true;
    }

    sealed record Hit(int Score, string How, string Kind, Row Row);

    /// <summary>One page of matches, with the whole count and where the matches are.</summary>
    public static JsonObject Run(IReadOnlyList<long> allowed, SqliteConnection conn, string query, Filter filter, int page = 1, int perPage = DefaultPerPage)
    {
        query = PyStr.Strip(query ?? "");
        perPage = Math.Clamp(perPage, 1, MaxPerPage);
        page = Math.Max(1, page);
        var kind = filter.Kind is { Length: > 0 } k && Kinds.Contains(k) ? k : null;
        var tokens = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var ids = allowed.ToList();
        var empty = new JsonObject
        {
            ["query"] = query, ["page"] = page, ["per_page"] = perPage, ["total"] = 0L, ["reach"] = MaxReach,
            ["rows"] = new JsonArray(), ["facets"] = Facets([], new()),
        };
        if (ids.Count == 0 || tokens.Length == 0) return empty;

        // Everything that matches, whatever the kind and repository and language picked: the facets say where it all is.
        var heads = Symbols(ids, conn, tokens, filter).Concat(FileNamed(ids, conn, tokens, filter))
            .OrderByDescending(h => h.Score).ThenByDescending(h => h.Row.Long("is_public"))
            .ThenBy(h => h.Row.Str("path_with_namespace"), StringComparer.Ordinal).ThenBy(h => h.Row.Str("path"), StringComparer.Ordinal)
            .ThenBy(h => h.Row.Long("line")).ToList();
        var fts = FtsQuery(tokens);
        var groups = fts is null ? [] : TextGroups(ids, conn, fts, filter);
        bool In(string repo, string? lang, string what) =>
            (filter.Repo is not { Length: > 0 } r || repo == r) && (filter.Lang is not { Length: > 0 } l || lang == l) && (kind is null || what == kind);
        var headsIn = heads.Where(h => In(h.Row.Str("path_with_namespace"), h.Row.StrOrNull("lang"), h.Kind)).ToList();
        var textTotal = groups.Where(g => In(g.Repo, g.Lang, KindOf(g.Lang))).Sum(g => g.N);
        var total = headsIn.Count + textTotal;

        int offset = (page - 1) * perPage;
        var rows = new JsonArray();
        if (offset < Math.Min(total, MaxReach))
        {
            int take = Math.Min(perPage, MaxReach - offset);
            foreach (var h in headsIn.Skip(offset).Take(take)) rows.Add(Json(h));
            int textOffset = Math.Max(0, offset - headsIn.Count);
            int textTake = take - rows.Count;
            if (textTake > 0 && fts is not null && textTotal > 0 && kind is not "symbol")
                foreach (var h in Text(ids, conn, fts, filter, kind, textOffset, textTake, tokens)) rows.Add(Json(h));
        }
        return new JsonObject
        {
            ["query"] = query, ["page"] = page, ["per_page"] = perPage, ["total"] = total, ["reach"] = MaxReach,
            ["rows"] = rows, ["facets"] = Facets(heads, groups, filter),
        };
    }

    // --- names ------------------------------------------------------------------------------

    static IEnumerable<Hit> Symbols(List<long> ids, SqliteConnection conn, string[] tokens, Filter filter)
    {
        Name? typed;
        if (tokens.Length == 1) typed = ParseName(tokens[0]);
        else
        {
            // Several words name a symbol by its words: "test func" is TestFunc, test_func and testFunction.
            var words = tokens.SelectMany(Words).ToArray();
            if (words.Length == 0 || tokens.Length > 4 || !tokens.All(t => IdentRe.IsMatch(t))) yield break;
            typed = new Name(string.Join(" ", tokens), string.Concat(tokens), null, words);
        }
        if (typed is null) yield break;

        // The name in any case first, a seek that is never cut short; then its start and its words, capped.
        var asks = new List<(string Where, object?[] Args)> { ("s.name = ? COLLATE NOCASE", [typed.Last]) };
        if (typed.Last.Length >= 3)
            asks.Add(("s.name > ? COLLATE NOCASE AND s.name < ? COLLATE NOCASE", [typed.Last, typed.Last + "\uFFFF"]));
        if (typed.Words.Length > 1)
            asks.Add(("s.name LIKE ? ESCAPE '\\'", ["%" + string.Join("%", typed.Words.Select(Like)) + "%"]));

        var (pathWhere, pathArgs) = PathWhere(filter.Path);
        var seen = new HashSet<long>();
        foreach (var (where, args) in asks)
            foreach (var chunk in Queries.Chunks(ids, args.Length + pathArgs.Length + 1))
                foreach (var row in Sql.QueryList(conn,
                             "SELECT s.id, s.repo_id, r.path_with_namespace, r.branch, f.path, f.lang, s.name, s.kind," +
                             "       s.line, s.end_line, s.signature, s.scope, s.is_public, s.doc" +
                             "  FROM symbols s" +
                             "  JOIN files f ON f.id = s.file_id" +
                             "  JOIN repos r ON r.id = s.repo_id" +
                             $" WHERE s.repo_id IN ({Sql.Marks(chunk.Count)}) AND {where}{pathWhere}" +
                             " LIMIT ?",
                             [.. chunk.Cast<object?>(), .. args, .. pathArgs, SymbolCap]))
                {
                    if (!seen.Add(row.Long("id"))) continue;
                    var (score, how) = Rank(typed, row.Str("name"), row.StrOrNull("scope"));
                    if (score > 0) yield return new Hit(score + (row.Long("is_public") != 0 ? 2 : 0), how, "symbol", row);
                }
    }

    /// <summary>A file the query names: util.h, src/net/socket.c.</summary>
    static IEnumerable<Hit> FileNamed(List<long> ids, SqliteConnection conn, string[] tokens, Filter filter)
    {
        if (tokens.Length != 1 || !tokens[0].Contains('.') && !tokens[0].Contains('/')) yield break;
        var token = tokens[0].Trim('/');
        var basename = PyStr.AfterLast(token, '/');
        if (basename.Length == 0) yield break;
        var (pathWhere, pathArgs) = PathWhere(filter.Path);
        foreach (var chunk in Queries.Chunks(ids, 1 + pathArgs.Length))
            foreach (var row in Sql.QueryList(conn,
                         "SELECT f.id, f.repo_id, r.path_with_namespace, r.branch, f.path, f.lang, 1 AS line, 0 AS is_public" +
                         "  FROM files f JOIN repos r ON r.id = f.repo_id" +
                         $" WHERE f.repo_id IN ({Sql.Marks(chunk.Count)}) AND f.basename = ?{pathWhere}",
                         [.. chunk.Cast<object?>(), basename, .. pathArgs]))
            {
                var path = row.Str("path");
                if (path != token && !path.EndsWith("/" + token, StringComparison.Ordinal)) continue;
                yield return new Hit(90, "file name", KindOf(row.StrOrNull("lang")), row);
            }
    }

    // --- words ------------------------------------------------------------------------------

    sealed record Group(string Repo, string? Lang, long N);

    static (string Sql, object?[] Args) PathWhere(string? path)
    {
        if (path is not { Length: > 0 } p) return ("", []);
        return p.IndexOfAny(['*', '?', '[']) >= 0
            ? (" AND f.path GLOB ?", [p])
            : (" AND f.path LIKE ? ESCAPE '\\'", ["%" + Like(p) + "%"]);
    }

    static string KindOf(string? lang) => lang is not null && DocLangs.Contains(lang) ? "doc" : "code";

    static string KindWhere(string? kind) => kind switch
    {
        "doc" => " AND f.lang IN ('markdown', 'text')",
        "code" => " AND (f.lang IS NULL OR f.lang NOT IN ('markdown', 'text'))",
        _ => "",
    };

    static object?[] TextArgs(string fts, List<long> chunk, object?[] pathArgs) => [fts, .. chunk.Cast<object?>(), .. pathArgs];

    /// <summary>Where the files that hold the words are: a count per repository and language, of every match.</summary>
    static List<Group> TextGroups(List<long> ids, SqliteConnection conn, string fts, Filter filter)
    {
        var (pathWhere, pathArgs) = PathWhere(filter.Path);
        var counts = new Dictionary<(string, string?), long>();
        foreach (var chunk in Queries.Chunks(ids, 1 + pathArgs.Length))
            foreach (var row in Fts(conn,
                         "SELECT r.path_with_namespace AS repo, f.lang, COUNT(*) AS n" +
                         "  FROM files_fts" +
                         "  JOIN files f ON f.id = files_fts.rowid" +
                         "  JOIN repos r ON r.id = f.repo_id" +
                         $" WHERE files_fts MATCH ? AND f.repo_id IN ({Sql.Marks(chunk.Count)}){pathWhere}" +
                         " GROUP BY r.path_with_namespace, f.lang",
                         TextArgs(fts, chunk, pathArgs)))
            {
                var key = (row.Str("repo"), row.StrOrNull("lang"));
                counts[key] = counts.GetValueOrDefault(key) + row.Long("n");
            }
        return [.. counts.Select(c => new Group(c.Key.Item1, c.Key.Item2, c.Value))];
    }

    /// <summary>The files that hold the words, best first: BM25, a word in the path weighing four times one in the text.</summary>
    static IEnumerable<Hit> Text(List<long> ids, SqliteConnection conn, string fts, Filter filter, string? kind, int offset, int take, string[] tokens)
    {
        var (pathWhere, pathArgs) = PathWhere(filter.Path);
        var where = pathWhere + KindWhere(kind);
        var extra = new List<object?>();
        if (filter.Repo is { Length: > 0 } repo) { where += " AND r.path_with_namespace = ?"; extra.Add(repo); }
        if (filter.Lang is { Length: > 0 } lang) { where += " AND f.lang = ?"; extra.Add(lang); }
        var rows = new List<Row>();
        foreach (var chunk in Queries.Chunks(ids, 2 + pathArgs.Length + extra.Count))
            rows.AddRange(Fts(conn,
                "SELECT f.id, f.repo_id, r.path_with_namespace, r.branch, f.path, f.lang," +
                "       bm25(files_fts, 4.0, 1.0) AS rank" +
                "  FROM files_fts" +
                "  JOIN files f ON f.id = files_fts.rowid" +
                "  JOIN repos r ON r.id = f.repo_id" +
                $" WHERE files_fts MATCH ? AND f.repo_id IN ({Sql.Marks(chunk.Count)}){where}" +
                " ORDER BY rank, f.id LIMIT ?",
                [.. TextArgs(fts, chunk, [.. pathArgs, .. extra]), offset + take]));
        var picked = rows.OrderBy(r => r.Double("rank")).ThenBy(r => r.Long("id")).Skip(offset).Take(take).ToList();
        if (picked.Count == 0) yield break;
        var content = Sql.QueryList(conn, $"SELECT id, content FROM files WHERE id IN ({Sql.Marks(picked.Count)})", [.. picked.Select(r => (object?)r.Long("id"))])
            .ToDictionary(r => r.Long("id"), r => r.Str("content"));
        foreach (var row in picked)
        {
            var (line, snippet) = Where(content.GetValueOrDefault(row.Long("id")) ?? "", tokens);
            row["line"] = line;
            row["snippet"] = snippet;
            yield return new Hit(0, "words", KindOf(row.StrOrNull("lang")), row);
        }
    }

    static List<Row> Fts(SqliteConnection conn, string sql, object?[] args)
    {
        try { return Sql.QueryList(conn, sql, args); }
        catch (SqliteException exc) when (exc.Message.Contains("fts5", StringComparison.OrdinalIgnoreCase))
        {
            // Each word is a quoted phrase, so this is a word the index cannot read, not syntax a person typed.
            throw new QueryError($"Those words cannot be searched ({Queries.SqliteMessage(exc)}): try other words.", exc);
        }
    }

    /// <summary>
    /// The first line that holds the query as typed, else all its words, else any: its number, and it with a line
    /// either side.
    /// </summary>
    public static (int Line, string Snippet) Where(string content, string[] tokens)
    {
        var lines = content.Split('\n');
        var whole = string.Join(" ", tokens);
        var words = tokens.SelectMany(t => Regex.Split(t, @"[^\p{L}\p{N}]+")).Where(w => w.Length > 0).ToArray();
        int Find(Func<string, bool> holds)
        {
            for (int i = 0; i < lines.Length; i++) if (holds(lines[i])) return i;
            return -1;
        }
        bool Has(string line, string w) => line.Contains(w, StringComparison.OrdinalIgnoreCase);
        var at = Find(l => Has(l, whole));
        if (at < 0) at = Find(l => words.All(w => Has(l, w)));
        if (at < 0) at = Find(l => words.Any(w => Has(l, w)));
        if (at < 0) at = 0;
        var from = Math.Max(0, at - 1);
        var to = Math.Min(lines.Length - 1, at + 1);
        var text = string.Join("\n", lines[from..(to + 1)].Select(l => l.TrimEnd('\r')));
        return (at + 1, text.Length > 400 ? text[..400] + "…" : text);
    }

    // --- the answer -------------------------------------------------------------------------

    static JsonObject Json(Hit h)
    {
        var r = h.Row;
        var o = new JsonObject
        {
            ["kind"] = h.Kind,
            ["how"] = h.How,
            ["score"] = h.Kind == "symbol" || h.How == "file name" ? h.Score : Math.Round(-r.Double("rank"), 3),
            ["repo_id"] = r.Long("repo_id"),
            ["repo"] = r.Str("path_with_namespace"),
            ["branch"] = r.Str("branch"),
            ["path"] = r.Str("path"),
            ["lang"] = r.StrOrNull("lang"),
            ["line"] = r.LongOrNull("line"),
        };
        if (h.Kind == "symbol")
        {
            o["name"] = r.Str("name");
            o["symbol_kind"] = r.Str("kind");
            o["scope"] = r.StrOrNull("scope");
            o["end_line"] = r.LongOrNull("end_line");
            o["signature"] = r.StrOrNull("signature");
            o["doc"] = r.StrOrNull("doc");
            o["snippet"] = r.StrOrNull("signature") is { Length: > 0 } sig ? $"{r.Str("name")}{sig}" : r.Str("name");
        }
        else o["snippet"] = r.Has("snippet") ? r.StrOrNull("snippet") : null;
        return o;
    }

    static JsonObject Facets(List<Hit> heads, List<Group> groups, Filter? filter = null)
    {
        filter ??= new Filter();
        var repos = new Dictionary<string, long>(StringComparer.Ordinal);
        var langs = new Dictionary<string, long>(StringComparer.Ordinal);
        var kinds = Kinds.ToDictionary(k => k, _ => 0L, StringComparer.Ordinal);
        void Count(string repo, string? lang, string k, long n)
        {
            // Each facet counts what the other filters leave, so it says what picking it would show.
            bool repoOk = filter.Repo is not { Length: > 0 } r || repo == r;
            bool langOk = filter.Lang is not { Length: > 0 } l || lang == l;
            bool kindOk = filter.Kind is not { Length: > 0 } kf || k == kf;
            if (langOk && kindOk) repos[repo] = repos.GetValueOrDefault(repo) + n;
            if (repoOk && kindOk && lang is not null) langs[lang] = langs.GetValueOrDefault(lang) + n;
            if (repoOk && langOk) kinds[k] += n;
        }
        foreach (var h in heads) Count(h.Row.Str("path_with_namespace"), h.Row.StrOrNull("lang"), h.Kind, 1);
        foreach (var g in groups) Count(g.Repo, g.Lang, KindOf(g.Lang), g.N);
        static JsonArray Top(Dictionary<string, long> d, string key) =>
            new([.. d.Where(p => p.Value > 0).OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).Take(50)
                .Select(p => (JsonNode?)new JsonObject { [key] = p.Key, ["n"] = p.Value })]);
        return new JsonObject
        {
            ["repos"] = Top(repos, "repo"),
            ["langs"] = Top(langs, "lang"),
            ["kinds"] = new JsonObject([.. kinds.Select(p => KeyValuePair.Create(p.Key, (JsonNode?)JsonValue.Create(p.Value)))]),
        };
    }
}
