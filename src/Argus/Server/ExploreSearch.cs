using System.Text.Json.Nodes;
using Argus.Indexing;
using Argus.Packs;
using Argus.Store;
using Argus.Util;
using Microsoft.Data.Sqlite;

namespace Argus.Server;

/// <summary>A search the operator asked for could not run as given (bad syntax, no packs): said to them, not logged as a failure.</summary>
public sealed class ExploreError(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The operator's searches in Explore: where a name is used in the code, code
/// text, and the documentation packs, and opening a file or a document. The same
/// queries the tools run, over every repository (the admin token sees all; the
/// tools see what their caller may), so "the tool found nothing" can be checked
/// against what is there.
/// </summary>
public static class ExploreSearch
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;

    static int Clamp(int? limit) => limit is null or < 1 ? DefaultLimit : Math.Min(limit.Value, MaxLimit);

    /// <summary>Every repository, or those of one path (all its branches).</summary>
    static List<long> Repos(SqliteConnection conn, string repo) =>
        [.. (repo.Length > 0
                ? Sql.Query(conn, "SELECT id FROM repos WHERE path_with_namespace = ?", repo)
                : Sql.Query(conn, "SELECT id FROM repos"))
            .Select(r => r.Long("id"))];

    /// <summary>Lines that use <paramref name="name"/> as a whole word, the definitions marked.</summary>
    public static JsonObject References(SqliteConnection conn, string name, string repo, int? limit)
    {
        var lim = Clamp(limit);
        if (PyStr.Strip(name).Length == 0) return Page([], lim);
        return Page(Queries.FindReferences(Repos(conn, repo), conn, PyStr.Strip(name), lim + 1), lim);
    }

    /// <summary>Files whose text matches, with a snippet (full-text syntax: words, "a phrase", prefix*).</summary>
    public static JsonObject Code(SqliteConnection conn, string query, string repo, int? limit)
    {
        var lim = Clamp(limit);
        if (PyStr.Strip(query).Length == 0) return Page([], lim);
        try
        {
            return Page([.. Queries.SearchCode(Repos(conn, repo), conn, PyStr.Strip(query), lim + 1).Select(r => r.ToJson())], lim);
        }
        catch (QueryError exc)
        {
            // The tools' wording points a model at regex=True; here a person types words.
            var detail = exc.Message.IndexOf('(') is var open and >= 0 && exc.Message.IndexOf(')', open) is var close and > 0 ? exc.Message[(open + 1)..close] : exc.Message;
            throw new ExploreError($"That search syntax is not valid ({detail}). Use plain words, \"a phrase\" in double quotes, or word* for a prefix.", exc);
        }
    }

    public static JsonObject? File(SqliteConnection conn, long repoId, string path) =>
        Queries.GetFile(Repos(conn, ""), conn, repoId, path, maxBytes: 262144);

    /// <summary>
    /// The documentation packs: "text" (words in the pages), "name" (an API by its exact
    /// name) or "meaning" (by embedding; only packs built with this server's model).
    /// </summary>
    public static JsonObject Docs(string packsDir, string query, string mode, string source, int? limit)
    {
        var lim = Clamp(limit);
        query = PyStr.Strip(query);
        return WithPacks(packsDir, opened =>
        {
            var sources = new JsonArray([.. opened.Select(p => (JsonNode?)p.Name).Distinct()]);
            if (query.Length == 0) return new JsonObject { ["rows"] = new JsonArray(), ["capped"] = false, ["sources"] = sources };
            var lang = source.Length > 0 ? source : null;
            List<JsonObject> rows;
            try
            {
                rows = mode switch
                {
                    "name" => PackStore.LookupSymbol(opened, query, lang, lim + 1),
                    "meaning" => Meaning(opened, query, lang, lim + 1),
                    _ => PackStore.SearchText(opened, query, lang, lim + 1),
                };
            }
            catch (PackQueryError exc)
            {
                throw new ExploreError(exc.Message, exc);
            }
            var page = Page(rows, lim);
            page["sources"] = sources;
            return page;
        });
    }

    public static JsonObject? Doc(string packsDir, string path, string source) =>
        WithPacks(packsDir, opened => PackStore.GetDoc(opened, path, source.Length > 0 ? source : null, 200000));

    static List<JsonObject> Meaning(List<Pack> opened, string query, string? lang, int limit)
    {
        var comparable = opened.Where(p =>
        {
            try { PackFormat.RequireCompatible(p.Meta, Embed.Model, Embed.Dim); return true; }
            catch (PackMismatch) { return false; }
        }).ToList();
        if (comparable.Count == 0) throw new ExploreError($"No pack was built with this server's embedding model ({Embed.Model}): search by text or name.");
        List<double[]> vectors;
        try { vectors = Embed.EmbedBatch([query]); }
        catch (EmbeddingUnavailable exc) { throw new ExploreError($"The embedding server is unavailable ({exc.Message}): search by text or name.", exc); }
        return PackStore.SearchDocs(comparable, vectors[0], lang, limit, queryText: query);
    }

    static T WithPacks<T>(string packsDir, Func<List<Pack>, T> fn)
    {
        var paths = Registry.PackFiles(packsDir);
        if (paths.Count == 0) throw new ExploreError("No documentation packs are loaded: load one under Packs.");
        List<Pack> opened;
        try { opened = PackStore.OpenPacks(paths); }
        catch (PackQueryError exc) { throw new ExploreError(exc.Message, exc); }
        try { return fn(opened); }
        finally { PackStore.ClosePacks(opened); }
    }

    static JsonObject Page(List<JsonObject> rows, int limit) => new()
    {
        ["rows"] = new JsonArray([.. rows.Take(limit).Select(r => (JsonNode?)r)]),
        ["capped"] = rows.Count > limit,
    };
}
