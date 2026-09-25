using System.Diagnostics;
using System.Text.Json.Nodes;
using Argus.Access;
using Argus.Configuration;
using Argus.Indexing;
using Argus.Packs;
using Argus.Store;
using Argus.Util;
using Microsoft.Data.Sqlite;

namespace Argus.Server;

/// <summary>No documentation packs could be read; the message tells the agent not to retry.</summary>
public sealed class DocsUnavailable(string message, Exception? inner = null) : ToolError(message, inner);

/// <summary>The index could not be read; the message tells the agent not to retry.</summary>
public sealed class IndexUnavailable(string message, Exception? inner = null) : ToolError(message, inner);

/// <summary>A branch was asked for that no allowed repository has indexed.</summary>
public sealed class UnknownBranch(string message) : QueryError(message);

/// <summary>
/// Nothing the caller may read matched, but something they may NOT read did.
/// A LookupError in Python, so it passes through the storage wrapper untouched.
/// </summary>
public sealed class AccessNotice(string message) : ToolError(message);

/// <summary>A lookup found nothing the caller may see (Python's LookupError).</summary>
public sealed class NotFoundError(string message) : ToolError(message);

/// <summary>
/// The seventeen MCP tools (argus/mcpsrv/tools.py). Every private-code tool is
/// scoped to the caller's allowlist, audited in the sidecar database and on the
/// log stream, and turns a storage failure into "do not retry" rather than a
/// stack trace; the documentation tools read the installed packs.
/// </summary>
public sealed class Tools
{
    readonly ArgusConfig _cfg;
    readonly MemberDirectory? _notices;

    public Tools(ArgusConfig cfg)
    {
        _cfg = cfg;
        _notices = Environment.GetEnvironmentVariable("ARGUS_ACCESS_NOTICES") != "0" ? new MemberDirectory(cfg.GitLab) : null;
    }

    public MemberDirectory? Notices => _notices;
    string DbPath => _cfg.Index.DbPath;
    string PacksDir => _cfg.PacksDir;

    const string IndexGone = "The index is unavailable; do not retry this query.";

    T RunReadonly<T>(Func<SqliteConnection, T> fn)
    {
        SqliteConnection conn;
        try { conn = Db.ConnectReadonly(DbPath); }
        catch (Exception exc) { throw new IndexUnavailable(IndexGone, exc); }
        try { return fn(conn); }
        catch (QueryError) { throw; }
        catch (AccessNotice) { throw; }
        catch (NotFoundError) { throw; }
        catch (Exception exc) { throw new IndexUnavailable(IndexGone, exc); }
        finally { conn.Dispose(); }
    }

    T RunPacks<T>(Func<List<Pack>, T> fn)
    {
        var paths = Registry.PackFiles(PacksDir);
        if (paths.Count == 0)
            throw new DocsUnavailable(
                "No documentation packs are installed on this server, so there is nothing to look up. " +
                "Do not retry -- this is a server configuration matter for the operator, not a query you can rephrase.");
        List<Pack> opened;
        try { opened = PackStore.OpenPacks(paths); }
        catch (Exception exc) { throw new DocsUnavailable("The installed documentation packs could not be opened; do not retry this query.", exc); }
        try { return fn(opened); }
        catch (DocsUnavailable) { throw; }
        catch (PackQueryError exc) { throw new ToolError(exc.Message, exc); }
        catch (Exception exc) { throw new DocsUnavailable("The documentation packs are unavailable; do not retry this query.", exc); }
        finally { PackStore.ClosePacks(opened); }
    }

    // --- audit ------------------------------------------------------------------------

    void RecordAudit(Identity identity, string tool, JsonObject args)
    {
        try
        {
            using var conn = Db.ConnectAudit(DbPath);
            Writes.RecordAudit(conn, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), identity.UserId, identity.Username, tool,
                PyJson.Dumps(args), "[" + string.Join(", ", identity.AllowedRepoIds) + "]");
        }
        catch (Exception exc)
        {
            Console.Error.WriteLine($"failed to record audit row for tool={tool}: {exc.Message}");
        }
    }

    public JsonNode? WithAudit(Identity identity, string tool, JsonObject args, Func<JsonNode?> call)
    {
        var sw = Stopwatch.StartNew();
        Exception? error = null;
        try { return call(); }
        catch (Exception exc) { error = exc; throw; }
        finally
        {
            RecordAudit(identity, tool, args);
            AuditLog.ToolCall(tool, identity.Username, identity.UserId, args, identity.AllowedRepoIds.Count, sw.Elapsed.TotalMilliseconds, error);
        }
    }

    // --- scoping and access notices ---------------------------------------------------------

    static List<long> Scoped(SqliteConnection conn, Identity identity, string? branch)
    {
        var scoped = Queries.ScopeToBranch(identity.AllowedRepoIds, conn, branch);
        if (branch is not null && scoped.Count == 0 && identity.AllowedRepoIds.Count > 0)
        {
            var available = Queries.BranchesAvailable(identity.AllowedRepoIds, conn);
            throw new UnknownBranch($"branch {PyStr.Repr(branch)} is not indexed. Indexed branches: " +
                                    (available.Count > 0 ? string.Join(", ", available) : "(none)"));
        }
        return scoped;
    }

    List<(string, long, int)> DeniedMatches(SqliteConnection conn, Identity identity, string? branch, Func<List<long>, IEnumerable<JsonObject>> run)
    {
        var allowed = new HashSet<long>(identity.AllowedRepoIds);
        var denied = Sql.Query(conn, "SELECT id FROM repos").Select(r => r.Long("id")).Where(id => !allowed.Contains(id)).ToList();
        if (denied.Count == 0) return [];
        var scoped = Queries.ScopeToBranch(denied, conn, branch);
        if (scoped.Count == 0) return [];
        var rows = run(scoped).ToList();
        if (rows.Count == 0) return [];
        var meta = new Dictionary<long, (string Path, long Gid)>();
        foreach (var r in Sql.QueryList(conn, $"SELECT id, path_with_namespace, gitlab_id FROM repos WHERE id IN ({Sql.Marks(scoped.Count)})",
                     scoped.Cast<object?>().ToArray()))
            meta[r.Long("id")] = (r.Str("path_with_namespace"), r.Long("gitlab_id"));
        var byPath = new Dictionary<string, (string, long)>(StringComparer.Ordinal);
        foreach (var (path, gid) in meta.Values) byPath[path] = (path, gid);
        var counts = new Dictionary<(string, long), int>();
        var order = new List<(string, long)>();
        foreach (var row in rows)
        {
            (string, long)? key = null;
            if (row["repo_id"] is JsonValue idv && idv.TryGetValue<long>(out var id))
            {
                if (meta.TryGetValue(id, out var m)) key = m;
            }
            else
            {
                var name = row["repo"]?.ToString() ?? row["path_with_namespace"]?.ToString();
                if (name is not null && byPath.TryGetValue(name, out var m)) key = m;
            }
            if (key is { } k)
            {
                if (!counts.ContainsKey(k)) order.Add(k);
                counts[k] = counts.GetValueOrDefault(k, 0) + 1;
            }
        }
        return order.Select(k => (k.Item1, k.Item2, counts[k]))
            .OrderBy(t => -t.Item3).ThenBy(t => t.Item1, StringComparer.Ordinal).ToList();
    }

    void RaiseIfOnlyDenied(SqliteConnection conn, Identity identity, string? branch, Func<List<long>, IEnumerable<JsonObject>> run)
    {
        if (_notices is null) return;
        var denied = DeniedMatches(conn, identity, branch, run);
        if (denied.Count > 0) throw new AccessNotice(People.NoAccessMessage(denied, _notices));
    }

    void RaiseIfRepoDenied(SqliteConnection conn, Identity identity, long repoId)
    {
        if (_notices is null || identity.AllowedRepoIds.Contains(repoId)) return;
        var row = Sql.One(conn, "SELECT path_with_namespace, gitlab_id FROM repos WHERE id = ?", repoId);
        if (row is not null)
            throw new AccessNotice(People.NoAccessMessage([(row.Str("path_with_namespace"), row.Long("gitlab_id"), 1)], _notices));
    }

    static JsonArray Arr(IEnumerable<JsonObject> rows)
    {
        var a = new JsonArray();
        foreach (var r in rows) a.Add(r);
        return a;
    }

    static IEnumerable<JsonObject> Objs(IEnumerable<Row> rows) => rows.Select(r => r.ToJson());

    // --- private code -------------------------------------------------------------------

    public JsonNode CodeContracts(Identity identity, string source, string? branch, int limit = 40) => RunReadonly(conn =>
    {
        var rows = Queries.SymbolContracts(Scoped(conn, identity, branch), conn, source, limit);
        if (rows.Count == 0) RaiseIfOnlyDenied(conn, identity, branch, ids => Queries.SymbolContracts(ids, conn, source, limit));
        return (JsonNode)Arr(rows);
    });

    public JsonNode SemanticSearch(Identity identity, string query, string? branch, int limit = 10)
    {
        var text = PyStr.Strip(query ?? "");
        if (text.Length == 0) return new JsonArray();
        double[] vector;
        try { vector = Embed.EmbedBatch([text])[0]; }
        catch (EmbeddingUnavailable exc) { throw new ToolError(exc.Message, exc); }
        return RunReadonly(conn =>
        {
            PackFormat.LoadVecExtension(conn);
            var rows = Queries.SemanticSearch(Scoped(conn, identity, branch), conn, vector, limit);
            if (rows.Count == 0) RaiseIfOnlyDenied(conn, identity, branch, ids => Queries.SemanticSearch(ids, conn, vector, limit));
            return (JsonNode)Arr(rows);
        });
    }

    public JsonNode FindSymbol(Identity identity, string name, string? kind, string? branch) => RunReadonly(conn =>
    {
        var rows = Queries.FindSymbol(Scoped(conn, identity, branch), conn, name, kind);
        if (rows.Count == 0) RaiseIfOnlyDenied(conn, identity, branch, ids => Objs(Queries.FindSymbol(ids, conn, name, kind)));
        return (JsonNode)Arr(Objs(rows));
    });

    public JsonNode FindReferences(Identity identity, string name, string? branch) => RunReadonly(conn =>
    {
        var scoped = Scoped(conn, identity, branch);
        var rows = Queries.FindReferences(scoped, conn, name);
        if (rows.Count == 0)
        {
            RaiseIfOnlyDenied(conn, identity, branch, ids => Queries.FindReferences(ids, conn, name));
            return (JsonNode)new JsonArray();
        }
        var byNamespace = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var s in Queries.IndexStatus(scoped, conn)) byNamespace[s.Str("path_with_namespace")] = s.Long("repo_id");
        foreach (var row in rows)
            row["repo_id"] = byNamespace.TryGetValue(row["repo"]!.GetValue<string>(), out var id) ? JsonValue.Create(id) : null;
        return (JsonNode)Arr(rows);
    });

    const string DanglingRegexSuggestion = ", or use regex=True.";

    public JsonNode SearchCode(Identity identity, string query, string? branch)
    {
        try
        {
            return RunReadonly(conn =>
            {
                var rows = Queries.SearchCode(Scoped(conn, identity, branch), conn, query);
                if (rows.Count == 0) RaiseIfOnlyDenied(conn, identity, branch, ids => Objs(Queries.SearchCode(ids, conn, query)));
                return (JsonNode)Arr(Objs(rows));
            });
        }
        catch (UnknownBranch) { throw; }
        catch (QueryError exc) { throw new QueryError(exc.Message.Replace(DanglingRegexSuggestion, "."), exc); }
    }

    public JsonNode GetFile(Identity identity, long repoId, string path)
    {
        var result = RunReadonly(conn =>
        {
            RaiseIfRepoDenied(conn, identity, repoId);
            return Queries.GetFile(identity.AllowedRepoIds, conn, repoId, path);
        });
        return result ?? throw new NotFoundError(
            $"No file at repo_id={repoId}, path={PyStr.Repr(path)}. Either that repo id is not one you have access to, " +
            "or that path does not exist in it -- call index_status to see which repo ids you can use.");
    }

    public JsonNode IndexStatus(Identity identity) =>
        RunReadonly(conn => (JsonNode)Arr(Objs(Queries.IndexStatus(identity.AllowedRepoIds, conn))));

    public JsonNode Overview(Identity identity, string? repo) =>
        RunReadonly(conn => (JsonNode)Queries.RepoOverview(identity.AllowedRepoIds, conn, repo));

    public JsonNode RepoMap(Identity identity, long repoId) => RunReadonly(conn =>
    {
        RaiseIfRepoDenied(conn, identity, repoId);
        return (JsonNode)Queries.RepoMap(identity.AllowedRepoIds, conn, repoId);
    });

    public JsonNode WhichRepo(Identity identity, string description, string? branch) => RunReadonly(conn =>
    {
        var rows = Queries.WhichRepo(Scoped(conn, identity, branch), conn, description);
        if (rows.Count == 0) RaiseIfOnlyDenied(conn, identity, branch, ids => Queries.WhichRepo(ids, conn, description));
        return (JsonNode)Arr(rows);
    });

    public JsonNode ImpactOf(Identity identity, long repoId, string path, long maxDepth) => RunReadonly(conn =>
    {
        RaiseIfRepoDenied(conn, identity, repoId);
        return (JsonNode)Queries.ImpactOf(identity.AllowedRepoIds, conn, repoId, path, maxDepth);
    });

    // --- documentation packs ------------------------------------------------------------------

    static List<JsonObject> FlagWidened(List<JsonObject> rows, string? ignored)
    {
        if (!string.IsNullOrEmpty(ignored))
            foreach (var r in rows) r["lang_filter_ignored"] = ignored;
        return rows;
    }

    public JsonNode DocsLookup(string name, string? lang, int limit = 20) => RunPacks(opened =>
    {
        var hits = PackStore.LookupSymbol(opened, name, lang, limit);
        if (hits.Count > 0 || string.IsNullOrEmpty(lang)) return (JsonNode)Arr(hits);
        if (opened.Select(p => (p.Name ?? "").ToLowerInvariant()).Contains(lang.ToLowerInvariant())) return Arr(hits);
        var widened = PackStore.LookupSymbol(opened, name, null, limit);
        foreach (var hit in widened) hit["lang_filter_ignored"] = lang;
        return Arr(widened);
    });

    public JsonNode DocsFind(string description, string? lang, int limit = 10) => RunPacks(opened =>
    {
        var scoped = lang;
        string? widenedFrom = null;
        if (!string.IsNullOrEmpty(lang) && !opened.Select(p => (p.Name ?? "").ToLowerInvariant()).Contains(PyStr.Strip(lang).ToLowerInvariant()))
            (scoped, widenedFrom) = (null, lang);
        double[] queryVec;
        try { queryVec = Embed.EmbedBatch([description])[0]; }
        catch (EmbeddingUnavailable exc)
        {
            Console.Error.WriteLine($"embedder unavailable, docs_find degraded to lexical: {exc.Message}");
            var lexical = PackStore.SearchSymbolsHybrid(opened, description, null, scoped, limit);
            foreach (var row in lexical)
            {
                row["retrieval"] = "lexical";
                row["note"] = "Semantic matching was unavailable, so these are keyword matches over symbol descriptions. " +
                              "Documentation rarely uses the asker's vocabulary, so a miss here is weaker evidence than usual that the API does not exist.";
            }
            return (JsonNode)Arr(FlagWidened(lexical, widenedFrom));
        }
        return Arr(FlagWidened(PackStore.SearchSymbolsHybrid(opened, description, queryVec, scoped, limit), widenedFrom));
    });

    public JsonNode DocsContracts(string source, int limit = 40) =>
        RunPacks(opened => (JsonNode)Arr(PackStore.ApiContracts(opened, source, limit)));

    public JsonNode DocsVerify(string text, int limit = 40) =>
        RunPacks(opened => (JsonNode)Arr(PackStore.VerifyText(opened, text, limit)));

    public JsonNode? DocsGet(string docPath, string? source, int maxChars = 60000) =>
        RunPacks(opened => (JsonNode?)PackStore.GetDoc(opened, docPath, source, maxChars));

    public JsonNode DocsSearch(string query, string? lang, int limit = 10) => RunPacks(opened =>
    {
        var selected = PackStore.SelectPacks(opened, lang);
        List<double[]> vectors;
        try { vectors = Embed.EmbedBatch([query]); }
        catch (EmbeddingUnavailable exc)
        {
            Console.Error.WriteLine($"embedder unavailable, falling back to lexical: {exc.Message}");
            var rows = PackStore.SearchText(opened, query, lang, limit);
            foreach (var row in rows)
            {
                row["retrieval"] = "lexical";
                row["note"] = "Semantic search was unavailable, so these are keyword matches. Treat them as less precise than usual.";
            }
            return (JsonNode)Arr(rows);
        }
        var mismatched = new List<string>();
        foreach (var pack in selected)
        {
            try { PackFormat.RequireCompatible(pack.Meta, Embed.Model, Embed.Dim); }
            catch (PackMismatch) { mismatched.Add($"{pack.Name} (built with {pack.Meta.GetValueOrDefault("embedding_model", "an unknown model")})"); }
        }
        if (mismatched.Count > 0)
            throw new DocsUnavailable(
                $"Semantic search is unavailable: {string.Join(", ", mismatched)} was built with a different embedding model than this server uses ({Embed.Model}), " +
                "so its vectors are not comparable. Use docs_lookup with an exact API name instead -- that does not depend on embeddings. " +
                "The operator must rebuild or remove the pack to restore semantic search.");
        var found = PackStore.SearchDocs(opened, vectors[0], lang, limit, queryText: query);
        foreach (var row in found) row["retrieval"] = "semantic";
        return (JsonNode)Arr(found);
    });

    /// <summary>docs_find's description names the installed sources, which are the only useful values for lang.</summary>
    public string DocsFindDescription(string baseDescription)
    {
        List<Pack> opened;
        try { opened = PackStore.OpenPacks(Registry.PackFiles(PacksDir)); }
        catch (Exception exc)
        {
            Console.Error.WriteLine($"could not read pack names for the docs_find description: {exc.Message}");
            return baseDescription;
        }
        try
        {
            var names = opened.Select(p => p.Name).Where(n => !string.IsNullOrEmpty(n)).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList();
            if (names.Count == 0) return baseDescription;
            return $"{baseDescription} Installed sources, and the only accepted values for lang: {string.Join(", ", names)}.";
        }
        finally { PackStore.ClosePacks(opened); }
    }

    // --- dispatch -------------------------------------------------------------------------

    /// <summary>Run one validated call. <paramref name="identity"/> is null only for the documentation tools.</summary>
    public JsonNode? Dispatch(string tool, ToolRuntime.Args a, Func<Identity> identity)
    {
        switch (tool)
        {
            case "docs_find": return DocsFind(a.Str("description"), a.OptStr("lang"));
            case "docs_contracts": return DocsContracts(a.Str("source"));
            case "docs_verify": return DocsVerify(a.Str("text"));
            case "docs_get": return DocsGet(a.Str("doc_path"), a.OptStr("source"));
            case "docs_lookup": return DocsLookup(a.Str("name"), a.OptStr("lang"));
            case "docs_search": return DocsSearch(a.Str("query"), a.OptStr("lang"));
        }
        var who = identity();
        return tool switch
        {
            "impact_of" => WithAudit(who, tool,
                new JsonObject { ["repo_id"] = a.Int("repo_id"), ["path"] = a.Str("path"), ["max_depth"] = a.Int("max_depth") },
                () => ImpactOf(who, a.Int("repo_id"), a.Str("path"), a.Int("max_depth"))),
            "code_contracts" => WithAudit(who, tool,
                new JsonObject { ["source_chars"] = PyStr.Len(a.OptStr("source") ?? ""), ["branch"] = a.OptStr("branch") },
                () => CodeContracts(who, a.Str("source"), a.OptStr("branch"))),
            "semantic_search" => WithAudit(who, tool,
                new JsonObject { ["query"] = a.Str("query"), ["branch"] = a.OptStr("branch"), ["limit"] = a.Int("limit") },
                () => SemanticSearch(who, a.Str("query"), a.OptStr("branch"), (int)a.Int("limit"))),
            "find_symbol" => WithAudit(who, tool,
                new JsonObject { ["name"] = a.Str("name"), ["kind"] = a.OptStr("kind"), ["branch"] = a.OptStr("branch") },
                () => FindSymbol(who, a.Str("name"), a.OptStr("kind"), a.OptStr("branch"))),
            "find_references" => WithAudit(who, tool,
                new JsonObject { ["name"] = a.Str("name"), ["branch"] = a.OptStr("branch") },
                () => FindReferences(who, a.Str("name"), a.OptStr("branch"))),
            "search_code" => WithAudit(who, tool,
                new JsonObject { ["query"] = a.Str("query"), ["branch"] = a.OptStr("branch") },
                () => SearchCode(who, a.Str("query"), a.OptStr("branch"))),
            "get_file" => WithAudit(who, tool,
                new JsonObject { ["repo_id"] = a.Int("repo_id"), ["path"] = a.Str("path") },
                () => GetFile(who, a.Int("repo_id"), a.Str("path"))),
            "index_status" => WithAudit(who, tool, [], () => IndexStatus(who)),
            "overview" => WithAudit(who, tool, new JsonObject { ["repo"] = a.OptStr("repo") }, () => Overview(who, a.OptStr("repo"))),
            "repo_map" => WithAudit(who, tool, new JsonObject { ["repo_id"] = a.Int("repo_id") }, () => RepoMap(who, a.Int("repo_id"))),
            "which_repo" => WithAudit(who, tool,
                new JsonObject { ["description"] = PyStr.Prefix(a.Str("description"), 200), ["branch"] = a.OptStr("branch") },
                () => WhichRepo(who, a.Str("description"), a.OptStr("branch"))),
            _ => throw new ToolError($"Unknown tool: {tool}"),
        };
    }
}
