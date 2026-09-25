using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using Argus.Access;
using Argus.Configuration;
using Argus.Indexing;
using Argus.Packs;
using Argus.Packs.Sources;
using Argus.Server;
using Argus.Store;
using Argus.Util;
using Microsoft.Data.Sqlite;
using ModelContextProtocol.Protocol;

namespace Argus.Cli;

/// <summary>
/// The `argus` command line (argus/cli.py): the same subcommands, options,
/// output lines and exit codes, so a hook, a cron job or the admin console
/// cannot tell which implementation it is driving.
/// </summary>
public static class Commands
{
    public const string DefaultServeHost = "127.0.0.1";
    public const int DefaultServePort = 7700;
    public const int ExitPack = 5;
    public const int ExitVerifyContradicted = 2;
    public const int ExitVerifyUnavailable = 6;
    public const int DefaultEmbedPerPass = 2000;

    static TextWriter Out => Console.Out;
    static TextWriter Err => Console.Error;
    static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    static double NowF() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
    static double Ms(double started) => Math.Round((NowF() - started) * 1000, 1, MidpointRounding.ToEven);

    public static string? Preflight()
    {
        var exe = Ctags.Which("ctags");
        if (exe is null)
            return "ctags not found on PATH. Install Universal Ctags:\n  Linux:   sudo apt install universal-ctags\n  Windows: winget install UniversalCtags.Ctags";
        string output;
        try
        {
            var psi = new ProcessStartInfo(exe, "--version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            using var p = Process.Start(psi)!;
            output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10_000);
        }
        catch (Exception exc) when (exc is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return $"could not run ctags --version: {exc.Message}";
        }
        if (!output.Contains("Universal Ctags"))
        {
            var first = output.Length > 0 ? PyStr.SplitLines(output)[0] : "?";
            return $"{exe} is not Universal Ctags (reported: {first}).\nExuberant Ctags has no --output-format=json and cannot be used.";
        }
        return null;
    }

    // --- index --------------------------------------------------------------------------

    static (bool Unhealthy, string Outcome) IndexBranch(SqliteConnection conn, ArgusConfig cfg, Project project, string branch, string mirrorDir)
    {
        var label = branch == project.DefaultBranch ? project.PathWithNamespace : $"{project.PathWithNamespace}@{branch}";
        var repoId = Writes.UpsertRepo(conn, project.GitlabId, project.PathWithNamespace, project.DefaultBranch, project.HttpUrl, branch);
        var old = Sql.One(conn, "SELECT last_indexed_sha FROM repos WHERE id = ?", repoId)!.StrOrNull("last_indexed_sha");
        var started = NowF();
        IndexResult result;
        try
        {
            var sha = Mirror.HeadSha(mirrorDir, branch);
            if (sha == old && !Worker.ContractIsStale(conn, repoId))
            {
                Writes.RecordRunState(conn, repoId, false, false, Now());
                Out.WriteLine($"{label}: up to date");
                AuditLog.IndexRepo(project.PathWithNamespace, branch, "up_to_date", Ms(started));
                return (false, "up_to_date");
            }
            var tree = Mirror.SyncWorktree(cfg.Index, project.GitlabId, mirrorDir, sha, branch);
            result = Worker.IndexRepo(conn, cfg.Index, project, mirrorDir, tree, sha, old, repoId: repoId);
        }
        catch (GitError exc)
        {
            Writes.RecordError(conn, repoId, null, "git", exc.Message, Now());
            Writes.RecordRunState(conn, repoId, false, false, Now(), exc.Message);
            Err.WriteLine($"{label}: FAILED ({exc.Message})");
            AuditLog.IndexRepo(project.PathWithNamespace, branch, "failed", Ms(started), error: exc.Message);
            return (true, "failed");
        }
        catch (Exception exc)
        {
            var repr = $"{exc.GetType().Name}({PyStr.Repr(exc.Message)})";
            Writes.RecordError(conn, repoId, null, "index", repr, Now());
            Writes.RecordRunState(conn, repoId, false, false, Now(), repr);
            Err.WriteLine($"{label}: FAILED ({repr})");
            AuditLog.IndexRepo(project.PathWithNamespace, branch, "failed", Ms(started), error: repr);
            return (true, "failed");
        }
        var flags = (result.TimedOut ? " TIMED-OUT" : "") + (result.SymbolsFailed ? " SYMBOLS-FAILED" : "");
        Out.WriteLine($"{label}: indexed={result.Indexed} deleted={result.Deleted} skipped={result.Skipped} errors={result.Errors}{flags} ({NowF() - started:0.0}s)");
        var outcome = result.TimedOut ? "timed_out" : result.SymbolsFailed ? "symbols_failed" : "ok";
        AuditLog.IndexRepo(project.PathWithNamespace, branch, outcome, Ms(started), result.Indexed, result.Deleted, result.Skipped,
            result.Errors, result.TimedOut, result.SymbolsFailed);
        return (result.TimedOut || result.SymbolsFailed, outcome);
    }

    static int PruneMissingBranches(SqliteConnection conn, Project project, List<string> keep)
    {
        var marks = keep.Count > 0 ? Sql.Marks(keep.Count) : "NULL";
        var n = Sql.ExecList(conn, $"DELETE FROM repos WHERE gitlab_id = ? AND branch NOT IN ({marks})",
            new object?[] { project.GitlabId }.Concat(keep).ToArray());
        if (n > 0) Out.WriteLine($"{project.PathWithNamespace}: dropped {n} branch(es) no longer indexed");
        return n;
    }

    public static int Index(ArgusConfig cfg, string? only, bool resetRetries = false, bool allowPartial = false)
    {
        var started = NowF();
        int GiveUp(int code, string reason)
        {
            AuditLog.IndexEnd(code, Ms(started), 0, 0, 0, reason);
            return code;
        }
        if (!allowPartial)
        {
            EnumerationHealth health;
            try { health = GitLab.CheckEnumeration(cfg.GitLab); }
            catch (Exception exc) when (exc is GitLabError or HttpRequestException or TaskCanceledException or CredentialError)
            {
                Err.WriteLine($"could not verify GitLab enumeration: {PythonName(exc)}: {exc.Message}");
                return GiveUp(3, "gitlab_unreachable");
            }
            if (!health.Ok)
            {
                Err.WriteLine(health.Problem);
                Err.WriteLine("\nRe-run with --allow-partial-enumeration to index anyway.");
                return GiveUp(3, "enumeration_incomplete");
            }
        }
        if (Preflight() is { } problem)
        {
            Err.WriteLine(problem);
            return GiveUp(4, "preflight_failed");
        }
        using var conn = Db.Open(cfg.Index.DbPath);
        var projects = GitLab.ListProjects(cfg.GitLab);
        if (only is not null) projects = projects.Where(p => p.PathWithNamespace == only).ToList();
        if (resetRetries)
        {
            if (only is not null)
            {
                if (projects.Count == 0) Out.WriteLine($"repo '{only}' not found in projects from GitLab");
                else
                {
                    var cleared = Sql.Exec(conn, "DELETE FROM retry_attempts WHERE repo_id IN (SELECT id FROM repos WHERE path_with_namespace = ?)", only);
                    Out.WriteLine($"reset retry counters for '{only}' ({cleared} rows)");
                }
            }
            else
            {
                var cleared = Sql.Exec(conn, "DELETE FROM retry_attempts");
                Out.WriteLine(cleared > 0 ? $"reset {cleared} retry counter entries" : "no retry counters to reset");
            }
        }
        if (projects.Count == 0)
        {
            Out.WriteLine("no repos matched");
            return GiveUp(0, "no_repos_matched");
        }
        AuditLog.IndexStart(cfg.Index.Branches, allowPartial, projects.Count);
        var runStarted = NowF();
        bool anyUnhealthy = false;
        int failed = 0, upToDate = 0, empty = 0;
        foreach (var project in projects)
        {
            string mirrorDir;
            List<string> branches;
            try
            {
                mirrorDir = Mirror.EnsureMirror(cfg.Index, project, project.HttpUrl, Credentials.GitPassword(cfg.GitLab), cfg.GitLab);
                branches = Mirror.SelectBranches(Mirror.ListBranches(mirrorDir), cfg.Index.Branches, project.DefaultBranch);
            }
            catch (GitError exc)
            {
                anyUnhealthy = true;
                var repoId = Writes.UpsertRepo(conn, project.GitlabId, project.PathWithNamespace, project.DefaultBranch, project.HttpUrl, project.DefaultBranch);
                Writes.RecordError(conn, repoId, null, "git", exc.Message, Now());
                Writes.RecordRunState(conn, repoId, false, false, Now(), exc.Message);
                Err.WriteLine($"{project.PathWithNamespace}: FAILED ({exc.Message})");
                failed++;
                AuditLog.IndexRepo(project.PathWithNamespace, project.DefaultBranch, "mirror_failed", error: exc.Message);
                continue;
            }
            if (branches.Count == 0)
            {
                Out.WriteLine($"{project.PathWithNamespace}: no branches (empty repository) -- nothing to index");
                AuditLog.IndexRepo(project.PathWithNamespace, project.DefaultBranch, "no_branches");
                empty++;
                continue;
            }
            foreach (var branch in branches)
            {
                var (unhealthy, outcome) = IndexBranch(conn, cfg, project, branch, mirrorDir);
                if (unhealthy) anyUnhealthy = true;
                if (outcome == "failed") failed++;
                else if (outcome == "up_to_date") upToDate++;
            }
            PruneMissingBranches(conn, project, branches);
        }
        Dictionary<string, long> counts;
        int edges;
        try
        {
            counts = Resolve.ResolveIncludes(conn);
            edges = Graph.RebuildRepoDeps(conn);
        }
        catch (Exception exc)
        {
            Err.WriteLine($"resolve/rebuild failed: {exc.GetType().Name}({PyStr.Repr(exc.Message)})");
            AuditLog.IndexEnd(4, Ms(runStarted), projects.Count, failed, upToDate);
            return 4;
        }
        Out.WriteLine($"includes: {counts.GetValueOrDefault("resolved")} resolved, {counts.GetValueOrDefault("external")} external, " +
                      $"{counts.GetValueOrDefault("ambiguous")} ambiguous, {counts.GetValueOrDefault("not_found")} not found");
        Out.WriteLine($"repo graph: {edges} cross-repo edges");
        if (empty > 0)
            Out.WriteLine($"repos: {projects.Count} seen, {empty} empty (nothing to index), {projects.Count - empty} indexed");
        var embedded = EmbedAfterIndex(cfg, EmbedPerPass() is var limit && limit > 0 ? limit : null);
        var rc = anyUnhealthy ? 1 : 0;
        AuditLog.IndexEnd(rc, Ms(runStarted), projects.Count, failed, upToDate, empty: empty, embedded: embedded);
        return rc;
    }

    /// <summary>The Python exception class name an operator would have seen for this failure.</summary>
    static string PythonName(Exception exc) => exc switch
    {
        HttpRequestException => "ConnectError",
        TaskCanceledException => "ReadTimeout",
        _ => exc.GetType().Name,
    };

    public static int IndexRepeatedly(ArgusConfig cfg, string? only, bool resetRetries, bool allowPartial, int interval, int? maxPasses = null)
    {
        int last = 0, passes = 0;
        while (maxPasses is null || passes < maxPasses)
        {
            var sw = Stopwatch.StartNew();
            try { last = Index(cfg, only, resetRetries, allowPartial); }
            catch (Exception exc) when (exc is GitLabError or GitError or IOException)
            {
                Err.WriteLine($"indexing pass failed: {exc.Message}");
                last = 4;
            }
            passes++;
            if (maxPasses is not null && passes >= maxPasses) break;
            Out.WriteLine($"pass finished with exit {last} in {sw.Elapsed.TotalSeconds:0.0}s; next in {interval}s");
            Out.Flush();
            Thread.Sleep(TimeSpan.FromSeconds(interval));
        }
        return last;
    }

    static int EmbedPerPass() =>
        int.TryParse(Environment.GetEnvironmentVariable("ARGUS_EMBED_PER_PASS"), out var v) ? v : DefaultEmbedPerPass;

    static int EmbedAfterIndex(ArgusConfig cfg, long? limit)
    {
        try
        {
            using var conn = Db.Open(cfg.Index.DbPath);
            return Semantic.BuildSymbolEmbeddings(conn, limit: limit);
        }
        catch (EmbeddingUnavailable exc)
        {
            Err.WriteLine($"indexed, but embedding is unavailable: {exc.Message}");
            return 0;
        }
        catch (Exception exc)
        {
            Err.WriteLine($"indexed, but embedding failed: {exc.GetType().Name}: {exc.Message}");
            return 0;
        }
    }

    public static int EmbedCommand(ArgusConfig cfg, int? limit)
    {
        using var conn = Db.Open(cfg.Index.DbPath);
        try
        {
            var stale = Semantic.StaleCount(conn);
            if (stale > 0)
                Out.WriteLine($"{stale:N0} vectors were built with a different embedding model or dimension and are NOT usable. " +
                              $"Delete them to rebuild: DELETE FROM symbol_embeddings WHERE model <> '{Embed.Model}';");
            var written = Semantic.BuildSymbolEmbeddings(conn, limit: limit is > 0 ? limit : null,
                progress: (done, total) => { Out.WriteLine($"  embedded {done:N0} of {total:N0}"); Out.Flush(); });
            Out.WriteLine(written > 0 ? $"embedded {written:N0} symbols" : "nothing to embed; every public symbol already has a vector");
            return 0;
        }
        catch (EmbeddingUnavailable exc)
        {
            Err.WriteLine($"embedding unavailable: {exc.Message}");
            return ExitPack;
        }
    }

    // --- operations ------------------------------------------------------------------------

    public static int Backup(ArgusConfig cfg, string outDir, string? configPath)
    {
        Directory.CreateDirectory(outDir);
        var indexOut = Path.Combine(outDir, "index.db");
        if (!File.Exists(cfg.Index.DbPath))
        {
            Err.WriteLine($"no index at {cfg.Index.DbPath}");
            return 4;
        }
        string? Vacuum(string source, string target)
        {
            try
            {
                if (File.Exists(target)) File.Delete(target);
                Db.Init();
                using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = source, Pooling = false }.ToString());
                c.Open();
                Sql.Exec(c, "VACUUM INTO ?", target);
                return null;
            }
            catch (SqliteException exc) { return Queries.SqliteMessage(exc); }
        }
        if (Vacuum(cfg.Index.DbPath, indexOut) is { } err1)
        {
            Err.WriteLine($"backup failed: {err1}");
            return 4;
        }
        var auditSrc = Db.AuditDbPath(cfg.Index.DbPath);
        var auditOut = Path.Combine(outDir, Path.GetFileName(auditSrc));
        long auditRows = 0;
        if (File.Exists(auditSrc))
        {
            if (Vacuum(auditSrc, auditOut) is { } err2)
            {
                Err.WriteLine($"audit backup failed: {err2}");
                return 4;
            }
            using var verify = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = auditOut, Pooling = false }.ToString());
            verify.Open();
            auditRows = Convert.ToInt64(Sql.Scalar(verify, "SELECT COUNT(*) FROM audit"));
        }
        string status;
        long repoRows;
        using (var check = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = indexOut, Pooling = false }.ToString()))
        {
            check.Open();
            status = Sql.Scalar(check, "PRAGMA integrity_check")?.ToString() ?? "";
            repoRows = Convert.ToInt64(Sql.Scalar(check, "SELECT COUNT(*) FROM repos"));
        }
        if (status != "ok")
        {
            Err.WriteLine($"backup failed integrity check: {status}");
            return 4;
        }
        bool copiedConfig = false;
        if (configPath is not null && File.Exists(configPath))
        {
            File.Copy(configPath, Path.Combine(outDir, "config.yaml"), overwrite: true);
            copiedConfig = true;
        }
        int copiedPacks = 0;
        if (Directory.Exists(cfg.PacksDir))
        {
            var target = Path.Combine(outDir, "packs");
            Directory.CreateDirectory(target);
            foreach (var pack in Directory.EnumerateFiles(cfg.PacksDir, "*.arguspack"))
            {
                File.Copy(pack, Path.Combine(target, Path.GetFileName(pack)), overwrite: true);
                copiedPacks++;
            }
        }
        var sizeMb = new FileInfo(indexOut).Length / (1024.0 * 1024.0);
        Out.WriteLine($"index.db      {sizeMb:0.0} MB  ({repoRows} repos, integrity ok)");
        Out.WriteLine($"audit rows    {auditRows}  <- the only data no rebuild recovers");
        Out.WriteLine($"config.yaml   {(copiedConfig ? "copied" : "NOT FOUND")}");
        Out.WriteLine($"packs         {copiedPacks}");
        Out.WriteLine($"written to {outDir}");
        Out.WriteLine("mirrors/ and trees/ deliberately excluded: re-fetchable from GitLab");
        return 0;
    }

    public static int KpiCommand(ArgusConfig cfg, bool asJson)
    {
        JsonObject data;
        using (var conn = Db.Open(cfg.Index.DbPath)) data = Kpi.Collect(conn, cfg.Index.DbPath);
        if (asJson)
        {
            var sorted = new JsonObject();
            foreach (var key in data.Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal)) sorted[key] = data[key]!?.DeepClone();
            Out.WriteLine(PyJson.Dumps(sorted));
            return 0;
        }
        foreach (var (key, value) in data)
        {
            if (key == "collected_at") continue;
            var marker = Kpi.LowerIsBetter.Contains(key) ? "  (lower is better)" : "";
            var text = value is null ? "None" : value is JsonValue v && v.TryGetValue<double>(out var d) && !v.TryGetValue<long>(out _) ? PyJson.Float(d) : value.ToJsonString();
            Out.WriteLine($"  {key,-26} {text,12}{marker}");
        }
        return 0;
    }

    public static int Status(ArgusConfig cfg)
    {
        using var conn = Db.Open(cfg.Index.DbPath);
        var all = Sql.Query(conn, "SELECT id FROM repos").Select(r => r.Long("id")).ToList();
        var rows = Queries.IndexStatus(all, conn);
        if (rows.Count == 0)
        {
            Out.WriteLine("no repos indexed");
            return 0;
        }
        foreach (var row in rows)
        {
            var at = row.LongOrNull("last_indexed_at");
            var when = at is { } t ? DateTimeOffset.FromUnixTimeSeconds(t).ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "never";
            var sha = row.StrOrNull("last_indexed_sha") is { Length: > 0 } s ? PyStr.Prefix(s, 8) : "-";
            var flags = (row.Long("last_run_timed_out") != 0 ? " TIMED-OUT" : "") + (row.Long("last_run_symbols_failed") != 0 ? " SYMBOLS-FAILED" : "");
            if (row.StrOrNull("last_run_error") is { Length: > 0 } e) flags += $" RUN-FAILED({PyStr.Prefix(e, 80)})";
            Out.WriteLine($"{row.Str("path_with_namespace"),-40} sha={sha} at={when} files={row.Long("files")} symbols={row.Long("symbols")} errors={row.Long("errors")} queued_retries={row.Long("queued_retries")}{flags}");
        }
        return 0;
    }

    public static int ResolveCommand(ArgusConfig cfg)
    {
        Dictionary<string, long> counts;
        int edges;
        using var conn = Db.Open(cfg.Index.DbPath);
        try
        {
            counts = Resolve.ResolveIncludes(conn);
            edges = Graph.RebuildRepoDeps(conn);
        }
        catch (Exception exc)
        {
            Err.WriteLine($"resolve/rebuild failed: {exc.GetType().Name}({PyStr.Repr(exc.Message)})");
            return 4;
        }
        foreach (var state in new[] { "resolved", "external", "ambiguous", "not_found" })
            Out.WriteLine($"{state,-12} {counts.GetValueOrDefault(state)}");
        Out.WriteLine($"{"edges",-12} {edges}");
        return 0;
    }

    public static int FlushAcl(ArgusConfig cfg, string? user)
    {
        using var conn = Db.Open(cfg.Index.DbPath);
        if (user is not null)
        {
            var n = Sql.Exec(conn, "DELETE FROM acl_cache WHERE username = ?", user);
            Out.WriteLine(n == 0 ? $"user '{user}' not in acl cache; nothing cleared" : $"cleared acl cache for '{user}' ({n} rows)");
        }
        else
        {
            var n = Sql.Exec(conn, "DELETE FROM acl_cache");
            Out.WriteLine(n > 0 ? $"cleared {n} acl cache entries" : "no acl cache entries to clear");
        }
        return 0;
    }

    /// <summary>
    /// Exit 0 when the server answers /healthz with 200. For a container
    /// healthcheck: the image ships no curl and no Python, so the probe is the
    /// program itself.
    /// </summary>
    public static int Healthcheck(string url)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var resp = client.GetAsync(url).GetAwaiter().GetResult();
            return (int)resp.StatusCode == 200 ? 0 : 1;
        }
        catch (Exception exc) when (exc is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            Err.WriteLine($"unhealthy: {exc.Message}");
            return 1;
        }
    }

    // --- serve ---------------------------------------------------------------------------

    public static int Serve(ArgusConfig cfg, string host, int port, IReadOnlyList<string>? allowedHosts)
    {
        var app = ArgusServer.Create(cfg, allowedHosts);
        var address = host.Contains(':') && !host.StartsWith('[') ? $"[{host}]" : host;
        app.Urls.Add($"http://{address}:{port}");
        Out.WriteLine($"argus serving MCP on http://{address}:{port}/mcp");
        Out.Flush();
        app.Run();
        return 0;
    }

    public static int ServeStdio(ArgusConfig cfg)
    {
        var token = (Environment.GetEnvironmentVariable("ARGUS_TOKEN") ?? "").Trim();
        if (token.Length == 0)
        {
            Err.WriteLine("ARGUS_TOKEN is not set. stdio has no request headers, so the credential must come from the environment.");
            return 2;
        }
        Identity identity;
        using (var conn = Db.Open(cfg.Index.DbPath))
        {
            try { identity = Acl.Resolve(conn, cfg.GitLab, token); }
            catch (AclDenied exc)
            {
                Err.WriteLine($"credential rejected: {exc.Message}");
                return 2;
            }
        }
        Err.WriteLine($"argus stdio: {identity.Username} ({identity.AllowedRepoIds.Count} repos)");
        McpHandlers.StdioIdentity = identity;
        McpHandlers.Configure(new Tools(cfg), null);
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services
            .AddMcpServer(o =>
            {
                o.ServerInfo = new Implementation { Name = "argus", Version = Metrics.Version };
                o.ServerInstructions = ToolCatalog.ServerInstructions;
            })
            .WithStdioServerTransport()
            .WithListToolsHandler(McpHandlers.ListTools)
            .WithCallToolHandler(McpHandlers.CallTool);
        builder.Build().Run();
        return 0;
    }

    // --- packs ----------------------------------------------------------------------------

    static string PacksDirFor(Parsed a)
    {
        if (a.Get("packs_dir") is { Length: > 0 } dir) return dir;
        if (a.Get("config") is { Length: > 0 } cfgPath) return ArgusConfig.Load(cfgPath).PacksDir;
        throw new ConfigError("pass --packs-dir or --config to say where packs live");
    }

    static string Describe(InstalledPack pack)
    {
        var flag = pack.Compatible ? "" : "  [INCOMPATIBLE]";
        var sizeMb = pack.SizeBytes / (1024.0 * 1024.0);
        return $"{pack.Name,-16} {pack.Version,-10} {pack.EmbeddingModel,-20} {sizeMb,8:0.0} MB  {pack.License}{flag}";
    }

    public static int PackBuild(Parsed a)
    {
        var sourceName = a.Req("source");
        if (!SourceCatalog.Sources.TryGetValue(sourceName, out var make))
        {
            Err.WriteLine($"unknown source {PyStr.Repr(sourceName)}; known: {string.Join(", ", SourceCatalog.Sources.Keys.OrderBy(k => k, StringComparer.Ordinal))}");
            return ExitPack;
        }
        var source = make();
        var workDir = a.Req("work_dir");
        string? commit = null;
        if (a.Flag("fetch"))
        {
            var origin = source.ArchiveUrl.Length > 0 ? source.ArchiveUrl : $"{source.RepoUrl} ({source.Branch})";
            Out.WriteLine($"fetching {origin} into {workDir} ...");
            commit = PackBuilder.FetchSource(source, workDir);
        }
        Out.WriteLine($"building {source.Name} pack from {workDir} ...");
        string output;
        try
        {
            output = PackBuilder.BuildPack(source, workDir, a.Req("out"), a.Req("version"),
                sourceCommit: string.IsNullOrEmpty(commit) ? a.Get("commit") : commit);
        }
        catch (EmbeddingUnavailable exc)
        {
            Err.WriteLine($"embedding failed: {exc.Message}");
            Err.WriteLine("is ollama running, and has the model been pulled?");
            return ExitPack;
        }
        Dictionary<string, string> meta;
        using (var conn = PackFormat.OpenPack(output)) meta = PackFormat.ReadMeta(conn);
        Out.WriteLine($"wrote {output} ({new FileInfo(output).Length / (1024.0 * 1024.0):0.0} MB)");
        Out.WriteLine($"  docs {meta.GetValueOrDefault("doc_count")}  chunks {meta.GetValueOrDefault("chunk_count")}  symbols {meta.GetValueOrDefault("symbol_count")} (unresolved {meta.GetValueOrDefault("unresolved_symbol_count")})");
        return 0;
    }

    public static int PackList(Parsed a)
    {
        var packs = Registry.ListInstalled(PacksDirFor(a));
        if (packs.Count == 0)
        {
            Out.WriteLine("no packs installed");
            return 0;
        }
        Out.WriteLine($"{"NAME",-16} {"VERSION",-10} {"MODEL",-20} {"SIZE",11}  LICENSE");
        foreach (var p in packs) Out.WriteLine(Describe(p));
        return 0;
    }

    public static int PackInstall(Parsed a)
    {
        var installed = Registry.Install(a.Pos("source")!, PacksDirFor(a), a.Get("sha256"));
        Out.WriteLine($"installed {installed.Name} {installed.Version} -> {installed.Path}");
        if (!installed.Compatible)
        {
            Err.WriteLine($"warning: {installed.IncompatibleReason}");
            Err.WriteLine("lookup and text search still work; semantic search does not.");
        }
        return 0;
    }

    public static int PackInfo(Parsed a)
    {
        var dest = PacksDirFor(a);
        var name = a.Pos("name")!;
        var pack = Registry.ListInstalled(dest).FirstOrDefault(p => p.Name == name);
        if (pack is null)
        {
            Err.WriteLine($"no installed pack named {PyStr.Repr(name)} in {dest}");
            return ExitPack;
        }
        Dictionary<string, string> meta;
        using (var conn = PackFormat.OpenPack(pack.Path)) meta = PackFormat.ReadMeta(conn);
        Out.WriteLine($"name          {pack.Name}");
        Out.WriteLine($"version       {pack.Version}");
        Out.WriteLine($"path          {pack.Path}");
        Out.WriteLine($"size          {pack.SizeBytes / (1024.0 * 1024.0):0.0} MB");
        Out.WriteLine($"model         {pack.EmbeddingModel} ({pack.EmbeddingDim}d)");
        Out.WriteLine($"compatible    {(pack.Compatible ? "yes" : "no")}");
        if (!pack.Compatible) Out.WriteLine($"              {pack.IncompatibleReason}");
        Out.WriteLine($"source        {meta.GetValueOrDefault("source_repo", "")}");
        Out.WriteLine($"branch        {meta.GetValueOrDefault("source_branch", "")}");
        Out.WriteLine($"commit        {pack.SourceCommit}");
        Out.WriteLine($"docs          {meta.GetValueOrDefault("doc_count", "?")}");
        Out.WriteLine($"chunks        {meta.GetValueOrDefault("chunk_count", "?")}");
        Out.WriteLine($"symbols       {meta.GetValueOrDefault("symbol_count", "?")}");
        Out.WriteLine();
        Out.WriteLine($"license       {pack.License}");
        Out.WriteLine($"license url   {meta.GetValueOrDefault("license_url", "")}");
        Out.WriteLine("attribution");
        Out.WriteLine($"  {pack.Attribution}");
        return 0;
    }

    public static int PackRemove(Parsed a)
    {
        var dest = PacksDirFor(a);
        var name = a.Pos("name")!;
        if (Registry.Remove(name, dest))
        {
            Out.WriteLine($"removed {name}");
            return 0;
        }
        Err.WriteLine($"no installed pack named {PyStr.Repr(name)} in {dest}");
        return ExitPack;
    }

    public static int PackUpdate(Parsed a)
    {
        var dest = PacksDirFor(a);
        var available = Registry.FetchIndex(a.Req("index_url")).GroupBy(e => e.Name).ToDictionary(g => g.Key, g => g.Last());
        var installed = Registry.ListInstalled(dest);
        var name = a.Pos("name");
        if (!string.IsNullOrEmpty(name))
        {
            installed = installed.Where(p => p.Name == name).ToList();
            if (installed.Count == 0)
            {
                Err.WriteLine($"no installed pack named {PyStr.Repr(name)} in {dest}");
                return ExitPack;
            }
        }
        int updated = 0;
        foreach (var pack in installed)
        {
            if (!available.TryGetValue(pack.Name, out var entry)) { Out.WriteLine($"{pack.Name}: not in the index, leaving alone"); continue; }
            if (entry.Version == pack.Version) { Out.WriteLine($"{pack.Name}: {pack.Version} is current"); continue; }
            Out.WriteLine($"{pack.Name}: {pack.Version} -> {entry.Version}, downloading ...");
            Registry.Install(entry.Url, dest, entry.Sha256);
            updated++;
        }
        Out.WriteLine($"{updated} pack(s) updated");
        return 0;
    }

    public static int PackIndex(Parsed a)
    {
        var dest = PacksDirFor(a);
        var output = a.Req("out");
        var name = a.Pos("name");
        JsonObject index;
        try { index = Registry.WriteIndex(dest, a.Req("base_url"), name); }
        catch (IOException exc)
        {
            Err.WriteLine($"could not read {dest}: {exc.Message}");
            return ExitPack;
        }
        var packs = (JsonArray)index["packs"]!;
        if (packs.Count == 0) Err.WriteLine($"no packs found in {dest}" + (name is not null ? $" named {PyStr.Repr(name)}" : ""));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, PyJson.IndentedDumps(index) + "\n");
        Out.WriteLine($"wrote {output}: {packs.Count} pack(s) from {dest}");
        foreach (var entry in packs)
            Out.WriteLine($"  {entry!["name"]} {entry["version"]}  {entry["size_bytes"]!.GetValue<long>() / (1024.0 * 1024.0):0.0} MB  {entry["url"]}");
        foreach (var skipped in (JsonArray)index["skipped"]!)
            Err.WriteLine($"  SKIPPED {skipped!["file"]}: {skipped["reason"]}");
        return 0;
    }

    // --- verify ----------------------------------------------------------------------------

    public static int Verify(Parsed a)
    {
        ArgusConfig cfg;
        try { cfg = ArgusConfig.Load(a.Req("config")); }
        catch (Exception exc) when (exc is ConfigError or IOException)
        {
            Err.WriteLine($"config error: {exc.Message}");
            return ExitVerifyUnavailable;
        }
        var text = a.Get("text") ?? "";
        if (a.Get("text_file") is { } file)
        {
            try { text = file == "-" ? Console.In.ReadToEnd() : File.ReadAllText(file); }
            catch (IOException exc)
            {
                Err.WriteLine($"could not read {file}: {exc.Message}");
                return ExitVerifyUnavailable;
            }
        }
        if (PyStr.Strip(text).Length == 0)
        {
            Err.WriteLine("nothing to verify");
            return 0;
        }
        var paths = Registry.PackFiles(cfg.PacksDir);
        if (paths.Count == 0)
        {
            Err.WriteLine($"no documentation packs installed in {cfg.PacksDir}; cannot check this draft");
            return ExitVerifyUnavailable;
        }
        List<Pack> opened;
        try { opened = PackStore.OpenPacks(paths); }
        catch (Exception exc)
        {
            Err.WriteLine($"could not open the installed packs: {exc.Message}");
            return ExitVerifyUnavailable;
        }
        List<JsonObject> findings;
        try { findings = PackStore.VerifyText(opened, text, a.Int("limit") ?? 40); }
        catch (Exception exc)
        {
            Err.WriteLine($"could not check the draft: {exc.Message}");
            return ExitVerifyUnavailable;
        }
        finally { PackStore.ClosePacks(opened); }

        var contradicted = findings.Where(f => (f["status"]?.ToString() ?? "").ToLowerInvariant() == "contradicted").ToList();
        if (a.Flag("json"))
            Out.WriteLine(PyJson.Dumps(new JsonObject
            {
                ["contradicted"] = new JsonArray(contradicted.Select(f => (JsonNode?)f.DeepClone()).ToArray()),
                ["findings"] = new JsonArray(findings.Select(f => (JsonNode?)f.DeepClone()).ToArray()),
            }));
        if (contradicted.Count == 0)
        {
            if (!a.Flag("quiet")) Err.WriteLine($"verified against {paths.Count} pack(s): no contradictions");
            return 0;
        }
        var lines = new List<string>
        {
            $"The documentation contradicts {contradicted.Count} claim(s) in your draft. Correct these, or say the requirement is not documented -- do not restate them from memory:",
        };
        foreach (var f in contradicted.Take(10))
        {
            string Pick(params string[] keys) => keys.Select(k => f[k]?.ToString()).FirstOrDefault(v => !string.IsNullOrEmpty(v)) ?? "?";
            var source = keys(f, "source", "doc_path");
            lines.Add($"  - {Pick("symbol", "name")} {Pick("field")}: you said {PyStr.Repr(Pick("stated", "claim"))}; the documentation says {PyStr.Repr(Pick("documented", "value"))}" +
                      (source.Length > 0 ? $" [{source}]" : ""));
        }
        Err.WriteLine(string.Join("\n", lines));
        return ExitVerifyContradicted;

        static string keys(JsonObject f, params string[] names) =>
            names.Select(k => f[k]?.ToString()).FirstOrDefault(v => !string.IsNullOrEmpty(v)) ?? "";
    }
}
