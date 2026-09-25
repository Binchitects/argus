using Argus.Configuration;
using Argus.Store;
using Argus.Util;
using Microsoft.Data.Sqlite;

namespace Argus.Indexing;

public sealed class IndexResult
{
    public long RepoId { get; init; }
    public int Indexed { get; set; }
    public int Deleted { get; set; }
    public int Skipped { get; set; }
    public int Errors { get; set; }
    public string Sha { get; init; } = "";
    public bool TimedOut { get; set; }
    public bool SymbolsFailed { get; set; }
}

/// <summary>
/// Index one repository at one commit (argus/worker.py): delete what went away,
/// store what changed, extract symbols and their doc comments, and keep an
/// honest account of every path that could not be finished so the next pass
/// retries it -- up to a cap, after which it is recorded and given up on.
/// </summary>
public static class Worker
{
    static long RepoIdFor(SqliteConnection conn, long gitlabId) =>
        Sql.One(conn, "SELECT id FROM repos WHERE gitlab_id = ? ORDER BY (branch = default_branch) DESC, id LIMIT 1", gitlabId)!.Long("id");

    public static IndexResult IndexRepo(SqliteConnection conn, IndexConfig index, Project project, string mirrorPath, string tree,
        string newSha, string? oldSha, Func<double>? now = null, long? repoId = null)
    {
        now ??= () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        var started = now();
        var rid = repoId ?? RepoIdFor(conn, project.GitlabId);
        var result = new IndexResult { RepoId = rid, Sha = newSha };
        long Ts() => (long)now();

        if (oldSha is not null && ContractIsStale(conn, rid)) oldSha = null;
        var (fullReindex, changes) = Mirror.ChangesSince(mirrorPath, oldSha, newSha);
        var shas = Mirror.BlobShas(mirrorPath, newSha);

        foreach (var change in changes)
            if (change.Status == "D") { Writes.DeleteFile(conn, rid, change.Path); result.Deleted++; }

        if (fullReindex)
        {
            var existingPaths = Sql.Query(conn, "SELECT path FROM files WHERE repo_id = ?", rid).Select(r => r.Str("path")).ToList();
            foreach (var path in existingPaths.Where(p => !shas.ContainsKey(p)))
            {
                Writes.DeleteFile(conn, rid, path);
                result.Deleted++;
            }
        }

        var changedPaths = new HashSet<string>(changes.Where(c => c.Status is "A" or "M").Select(c => c.Path), StringComparer.Ordinal);
        var queued = Writes.PeekRetryPaths(conn, rid);
        var retryPaths = queued.Where(p => !changedPaths.Contains(p) && shas.ContainsKey(p)).ToList();
        var retrySet = new HashSet<string>(retryPaths, StringComparer.Ordinal);
        var pending = retryPaths.Select(p => new Change("M", p)).Concat(changes.Where(c => c.Status is "A" or "M")).ToList();
        var toParse = new List<string>();
        var failedPaths = new List<string>();
        var unreachedRetry = new List<string>();

        for (int position = 0; position < pending.Count; position++)
        {
            var change = pending[position];
            if (now() - started > index.RepoTimeBudgetSeconds)
            {
                result.TimedOut = true;
                unreachedRetry = pending.Skip(position).Where(c => retrySet.Contains(c.Path)).Select(c => c.Path).ToList();
                break;
            }

            if (AlreadyCurrent(conn, rid, change.Path, shas.GetValueOrDefault(change.Path, "")))
            {
                result.Skipped++;
                continue;
            }

            var absPath = Path.Combine(tree, change.Path);
            long size;
            try
            {
                // A directory (a submodule's checkout point) stats fine in Python
                // and is then skipped for having no language, not recorded as an error.
                if (Directory.Exists(absPath)) size = 0;
                else
                {
                    var info = new FileInfo(absPath);
                    if (!info.Exists) throw new FileNotFoundException($"[Errno 2] No such file or directory: '{absPath}'");
                    size = info.Length;
                }
            }
            catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
            {
                Writes.RecordError(conn, rid, change.Path, "read", exc.Message, Ts());
                result.Errors++;
                failedPaths.Add(change.Path);
                continue;
            }

            if (size > index.MaxFileBytes || Filters.DetectLang(change.Path) is null)
            {
                result.Skipped++;
                continue;
            }

            byte[] data;
            try { data = File.ReadAllBytes(absPath); }
            catch (Exception exc) when (exc is IOException or UnauthorizedAccessException or OutOfMemoryException)
            {
                Writes.RecordError(conn, rid, change.Path, "read", exc.Message, Ts());
                result.Errors++;
                failedPaths.Add(change.Path);
                continue;
            }

            if (!Filters.ShouldIndex(change.Path, data.Length, data, index.MaxFileBytes, index.ExcludeDirs))
            {
                result.Skipped++;
                continue;
            }

            try
            {
                var content = Utf8.DecodeReplace(data);
                var fileId = Writes.UpsertFile(conn, rid, change.Path, Filters.DetectLang(change.Path), data.Length,
                    shas.GetValueOrDefault(change.Path, ""), content);
                Writes.ReplaceIncludes(conn, rid, fileId, Includes.Extract(content));
            }
            catch (Exception exc)
            {
                Writes.RecordError(conn, rid, change.Path, "store", $"{exc.GetType().Name}({PyStr.Repr(exc.Message)})", Ts());
                result.Errors++;
                failedPaths.Add(change.Path);
                continue;
            }

            toParse.Add(change.Path);
            result.Indexed++;
        }

        var (uncovered, unattributable) = ApplySymbols(conn, rid, tree, toParse, result, Ts, shas);
        failedPaths.AddRange(uncovered);

        var indexedPaths = result.SymbolsFailed
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(toParse.Where(p => !uncovered.Contains(p) && !unattributable.Contains(p)), StringComparer.Ordinal);
        var gonePaths = queued.Where(p => !shas.ContainsKey(p)).ToHashSet(StringComparer.Ordinal);
        if (indexedPaths.Count > 0 || gonePaths.Count > 0)
            Writes.ClearRetryAttempts(conn, rid, indexedPaths.Union(gonePaths).ToList());

        var symbolsOnlyFailed = result.SymbolsFailed ? toParse.Where(retrySet.Contains).ToList() : [];
        var retryable = CapRetries(conn, rid, failedPaths, Ts);

        Writes.ClearRetryQueue(conn, rid);

        var requeue = retryable.Concat(unreachedRetry).Concat(symbolsOnlyFailed).Concat(unattributable).ToList();
        if (requeue.Count > 0)
        {
            var uncoveredSet = new HashSet<string>(uncovered, StringComparer.Ordinal);
            var reasons = new List<string>();
            if (retryable.Any(p => !uncoveredSet.Contains(p))) reasons.Add("per-file read/store error");
            if (retryable.Any(uncoveredSet.Contains)) reasons.Add("ctags did not process the file");
            if (symbolsOnlyFailed.Count > 0) reasons.Add("repo-wide symbol extraction failure");
            if (unattributable.Count > 0) reasons.Add("ctags exited non-zero without naming a file");
            if (unreachedRetry.Count > 0) reasons.Add("not reached before the repo time budget");
            Writes.EnqueueRetry(conn, rid, requeue, string.Join("; ", reasons), Ts());
        }

        if (!result.TimedOut && !result.SymbolsFailed) Writes.SetLastIndexed(conn, rid, newSha, Ts());
        Writes.RecordRunState(conn, rid, result.TimedOut, result.SymbolsFailed, Ts());
        if (!result.TimedOut && !result.SymbolsFailed) RecordContract(conn, rid);
        return result;
    }

    static List<string> CapRetries(SqliteConnection conn, long repoId, List<string> failedPaths, Func<long> ts)
    {
        if (failedPaths.Count == 0) return [];
        var attempts = Writes.BumpRetryAttempts(conn, repoId, failedPaths);
        var retryable = new List<string>();
        foreach (var path in failedPaths)
        {
            if (attempts.GetValueOrDefault(path, 0) >= Writes.MaxRetryAttempts)
                Writes.RecordError(conn, repoId, path, "retry-exhausted",
                    $"giving up after {Writes.MaxRetryAttempts} failed attempts; no longer queued for retry", ts());
            else retryable.Add(path);
        }
        return retryable;
    }

    /// <summary>What files.symbols_sha holds for a file whose symbols are current.</summary>
    public static string SymbolsStamp(string blobSha) => $"{Ctags.SymbolContractVersion}:{blobSha}";

    public static string ContractKey(long repoId) => $"symbol_contract:{repoId}";

    public static bool ContractIsStale(SqliteConnection conn, long repoId)
    {
        var row = Sql.One(conn, "SELECT value FROM argus_meta WHERE key = ?", ContractKey(repoId));
        return row is null || row.Str("value") != Ctags.SymbolContractVersion;
    }

    public static void RecordContract(SqliteConnection conn, long repoId) =>
        Sql.Exec(conn,
            "INSERT INTO argus_meta (key, value) VALUES (?, ?) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
            ContractKey(repoId), Ctags.SymbolContractVersion);

    static bool AlreadyCurrent(SqliteConnection conn, long repoId, string path, string blobSha)
    {
        if (blobSha.Length == 0) return false;
        var row = Sql.One(conn, "SELECT blob_sha, symbols_sha FROM files WHERE repo_id = ? AND path = ?", repoId, path);
        if (row is null) return false;
        return row.StrOrNull("blob_sha") == blobSha && row.StrOrNull("symbols_sha") == SymbolsStamp(blobSha);
    }

    static List<Writes.SymbolRow> WithDocs(string tree, string path, List<Writes.SymbolRow> symbols)
    {
        if (symbols.Count == 0) return symbols;
        string text;
        try { text = Utf8.DecodeReplace(File.ReadAllBytes(Path.Combine(tree, path))); }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException) { return symbols; }
        var lines = PyStr.SplitLines(text);
        foreach (var s in symbols)
        {
            try { s.Doc = DocComments.ForSymbol(lines, s.Line, s.Language); }
            catch (Exception) { s.Doc = ""; }
        }
        return symbols;
    }

    static (List<string> Uncovered, List<string> Unattributable) ApplySymbols(SqliteConnection conn, long repoId, string tree,
        List<string> paths, IndexResult result, Func<long> ts, Dictionary<string, string> shas)
    {
        if (paths.Count == 0) return ([], []);
        SymbolBatch batch;
        try { batch = Ctags.ExtractSymbols(tree, paths); }
        catch (CtagsUnavailable exc)
        {
            Writes.RecordError(conn, repoId, null, "ctags", exc.Message, ts());
            result.Errors++;
            result.SymbolsFailed = true;
            Writes.ClearSymbolsForPaths(conn, repoId, paths);
            return ([], []);
        }

        var uncovered = new List<string>();
        var unattributable = new List<string>();
        foreach (var path in paths)
        {
            var row = Sql.One(conn, "SELECT id FROM files WHERE repo_id = ? AND path = ?", repoId, path);
            if (row is null) continue;
            if (!batch.Covered.Contains(path))
            {
                if (batch.Unattributable.Contains(path)) unattributable.Add(path);
                else
                {
                    Writes.RecordError(conn, repoId, path, "symbols",
                        batch.Uncovered.GetValueOrDefault(path, "ctags did not process this file"), ts());
                    result.Errors++;
                    uncovered.Add(path);
                }
                continue;
            }
            var symbols = WithDocs(tree, path, batch.Symbols.GetValueOrDefault(path) ?? []);
            Writes.ReplaceSymbols(conn, repoId, row.Long("id"), symbols, SymbolsStamp(shas.GetValueOrDefault(path, "")));
        }
        Writes.ClearSymbolsForPaths(conn, repoId, uncovered.Concat(unattributable).ToList());
        return (uncovered, unattributable);
    }
}

/// <summary>bytes.decode("utf-8", errors="replace"), which is what Python stores.</summary>
public static class Utf8
{
    static readonly System.Text.Encoding Strict = new System.Text.UTF8Encoding(false, false);

    public static string DecodeReplace(byte[] data) => Strict.GetString(data);
}
