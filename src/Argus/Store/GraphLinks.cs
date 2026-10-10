using System.Text.Json.Nodes;
using Argus.Indexing;
using Argus.Util;
using Microsoft.Data.Sqlite;

namespace Argus.Store;

public static partial class Graph
{
    /// <summary>A link's kind, for what a file used (its layer and how sure it is: LinkKinds).</summary>
    public static string LinkKind(string declKind, string form = "") => LinkKinds.Of(declKind, form).Kind;

    /// <summary>
    /// The kinds whose names are dotted or slashed paths: a use matches the longest name a repository provides that starts it
    /// (a Python import names a module in a package; a Go import, a package in a module). C# and Java name a namespace or
    /// a package exactly: walking up them would let a repository that declares a root (System, com.acme) capture them all.
    /// </summary>
    static char? PrefixSeparator(string kind) => kind switch
    {
        "py" => '.',
        "go" => '/',
        _ => null,
    };

    /// <summary>
    /// Namespace roots that are a platform's (.NET's): a repository declaring one (a polyfill's namespace System) provides
    /// nothing to the estate under it, unless its own package carries that root.
    /// </summary>
    static readonly string[] ForeignCsRoots = ["System", "Microsoft", "Windows", "Internal", "Mono"];

    /// <summary>Python's standard library, by its top-level modules: a repository's package with one of these names is never what an import of it means.</summary>
    static readonly HashSet<string> PyStdlib = new(StringComparer.Ordinal)
    {
        "abc", "argparse", "array", "ast", "asyncio", "base64", "bisect", "builtins", "bz2", "calendar", "cmath", "codecs", "collections",
        "concurrent", "configparser", "contextlib", "contextvars", "copy", "csv", "ctypes", "dataclasses", "datetime", "decimal", "difflib",
        "dis", "email", "enum", "errno", "faulthandler", "fnmatch", "fractions", "functools", "gc", "getpass", "gettext", "glob", "gzip",
        "hashlib", "heapq", "hmac", "html", "http", "imaplib", "importlib", "inspect", "io", "ipaddress", "itertools", "json", "keyword",
        "linecache", "locale", "logging", "lzma", "mailbox", "math", "mimetypes", "multiprocessing", "netrc", "numbers", "operator", "os",
        "pathlib", "pdb", "pickle", "pkgutil", "platform", "plistlib", "pprint", "profile", "pstats", "queue", "quopri", "random", "re",
        "reprlib", "sched", "secrets", "select", "selectors", "shelve", "shlex", "shutil", "signal", "site", "smtplib", "socket",
        "socketserver", "sqlite3", "ssl", "stat", "statistics", "string", "struct", "subprocess", "sys", "sysconfig", "tarfile", "tempfile",
        "textwrap", "threading", "time", "timeit", "tkinter", "token", "tokenize", "tomllib", "trace", "traceback", "types", "typing",
        "unicodedata", "unittest", "urllib", "uuid", "venv", "warnings", "wave", "weakref", "webbrowser", "wsgiref", "xml", "xmlrpc",
        "zipfile", "zipimport", "zlib", "zoneinfo",
    };

    /// <summary>Folders whose Python modules are never a library of the estate's own (every repository has them).</summary>
    static readonly HashSet<string> GenericModules = new(StringComparer.Ordinal) { "tests", "test", "docs", "examples", "scripts", "tools", "setup", "conftest" };

    /// <summary>
    /// The Python modules a repository's files are, by their dotted names: a package's (a folder with __init__.py) from the
    /// top of its chain of package folders (python/acme/money/__init__.py is acme.money), each module in it under it
    /// (acme.money.round), and a module beside no package at the repository's top (or in src/, lib/, python/) by its name.
    /// </summary>
    public static IEnumerable<string> PythonModules(IReadOnlyCollection<string> files) => PythonModulesAt(files).Select(m => m.Module);

    /// <summary>Each Python module a repository's files make, with the file that makes it.</summary>
    public static IEnumerable<(string Module, string File)> PythonModulesAt(IReadOnlyCollection<string> files)
    {
        var packages = files.Where(f => f.EndsWith("/__init__.py", StringComparison.Ordinal) || f == "__init__.py")
            .Select(f => f.Contains('/') ? f[..f.LastIndexOf('/')] : "").ToHashSet(StringComparer.Ordinal);
        string? Dotted(string dir)
        {
            if (!packages.Contains(dir) || dir.Length == 0) return null;
            var root = dir;
            while (root.Contains('/') && packages.Contains(root[..root.LastIndexOf('/')])) root = root[..root.LastIndexOf('/')];
            var top = root.Contains('/') ? root[..(root.LastIndexOf('/') + 1)] : "";
            var name = dir[top.Length..].Replace('/', '.');
            return GenericModules.Contains(name.Split('.')[0]) ? null : name;
        }
        foreach (var f in files)
        {
            var slash = f.LastIndexOf('/');
            var dir = slash < 0 ? "" : f[..slash];
            var stem = Path.GetFileNameWithoutExtension(f);
            if (Dotted(dir) is { } package)
            {
                yield return (stem == "__init__" ? package : package + "." + stem, f);
            }
            else if (stem != "__init__" && dir is "" or "src" or "lib" or "python" && !GenericModules.Contains(stem) && stem is not ("setup" or "conftest" or "manage" or "main"))
            {
                yield return (stem, f);
            }
        }
    }

    /// <summary>Folders whose files declare nothing for the estate: tests, fixtures, examples, samples and templates are not what others build against.</summary>
    static bool NotAProvider(string path)
    {
        foreach (var part in path.Split('/')[..^1])
        {
            if (part.ToLowerInvariant() is "test" or "tests" or "testing" or "__tests__" or "fixtures" or "testdata" or "examples" or "example"
                or "samples" or "sample" or "templates" or "demo" or "demos" or "benchmarks")
            {
                return true;
            }
        }
        return false;
    }

    sealed record RepoRow(long Id, long GitlabId, string Path, bool Default);

    /// <summary>A path relative to a repository's own (as git reads a relative submodule URL against the repository's remote).</summary>
    static string Relative(string repoPath, string relative)
    {
        var parts = repoPath.Split('/').ToList();
        foreach (var step in relative.Split('/'))
        {
            if (step == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
            else if (step is not ("." or "")) parts.Add(step);
        }
        return string.Join('/', parts).ToLowerInvariant();
    }

    /// <summary>What a use resolved to: its state, the projects (one when resolved, several when ambiguous), the name matched and how.</summary>
    sealed record Match(string State, HashSet<long> Projects, string Matched, string How)
    {
        public static readonly Match External = new("external", [], "", "");
    }

    /// <summary>One way one repository uses another, as it is gathered from the files that make it.</summary>
    sealed class Evidence
    {
        public required string DeclKind { get; init; }
        public required string Layer { get; init; }
        public required string How { get; set; }
        public required int Providers { get; init; }
        public required double Prior { get; set; }
        public HashSet<long> MainFiles { get; } = [];
        public HashSet<long> TestFiles { get; } = [];
        public List<(string Path, long Line)> Uses { get; } = [];
        public (string Path, long Line)? Provider { get; set; }
        public List<string>? Candidates { get; set; }
        public bool Candidate { get; set; }
        public string? Why { get; set; }

        public void Use(long fileId, string path, long line, string scope)
        {
            (scope == "test" ? TestFiles : MainFiles).Add(fileId);
            if (Uses.Count < 3 && !Uses.Contains((path, line))) Uses.Add((path, line));
        }
    }

    /// <summary>A use several repositories provide, kept to settle once every unique one is known.</summary>
    sealed record Pending(RepoRow From, long FileId, string Path, long Line, string Scope, string DeclKind, string Form, Match Match);

    /// <summary>
    /// Rebuilds the links between repositories from what their files declare (file_decls) and from resolved #includes
    /// (repo_deps), at their default branches (what others build against). Each use resolves to the repository that
    /// provides its name; its own repository's name stays inside it. A way of using another is kept with its layer, its
    /// scope (main, or test when only tests use it), how its name matched, how many repositories provide it, a confidence
    /// and a tier, the files that make it and where. A name several repositories provide is settled by what else the user
    /// has (a package or submodule of one of them, the same file's other uses, its other links); else it is kept as a
    /// candidate of each, never walked. repo_edges sums each pair per layer (the noisy-OR of its kinds), and file_links says
    /// which files make each link. Returns the counts by state, kept for index_status too.
    /// </summary>
    public static Dictionary<string, long> RebuildLinks(SqliteConnection conn)
    {
        var repos = Sql.Query(conn, "SELECT id, gitlab_id, lower(path_with_namespace) AS path, branch, default_branch FROM repos ORDER BY id")
            .Select(r => new RepoRow(r.Long("id"), r.Long("gitlab_id"), r.Str("path"), r.Str("branch") == r.Str("default_branch")))
            .ToList();
        var byId = repos.ToDictionary(r => r.Id);
        // A library is linked at its default branch: that is what others build against.
        var defaultRow = repos.Where(r => r.Default).GroupBy(r => r.GitlabId).ToDictionary(g => g.Key, g => g.First().Id);
        var byPath = repos.Where(r => r.Default).GroupBy(r => r.Path).ToDictionary(g => g.Key, g => g.First());
        var pathOfProject = byPath.Values.ToDictionary(r => r.GitlabId, r => r.Path);

        // What each project provides, by kind and name (default branches, the product's own files only), and one place each says so.
        var provides = new Dictionary<string, Dictionary<string, HashSet<long>>>(StringComparer.Ordinal);
        var providedAt = new Dictionary<(string Kind, string Name, long Project), (string Path, long Line)>();
        var protoFiles = new List<(string Path, long Project)>();
        var generated = new List<(string Kind, string Name, long Project, string Path, long Line)>();
        var generatedPy = new HashSet<string>(StringComparer.Ordinal);
        var pyFiles = new Dictionary<long, List<string>>();
        var aliases = new Dictionary<long, HashSet<string>>();
        // A repository's own packages (its PackageIds): a .NET root of its own (Microsoft's own estate) is its to provide.
        var nugetRoots = Sql.Query(conn, "SELECT DISTINCT repo_id, name FROM file_decls WHERE role = 'provides' AND kind = 'nuget'")
            .Select(r => (r.Long("repo_id"), r.Str("name").Split('.')[0])).ToHashSet();
        void Provide(string kind, string name, long project, string path, long line)
        {
            if (!provides.TryGetValue(kind, out var names)) provides[kind] = names = new(StringComparer.Ordinal);
            if (!names.TryGetValue(name, out var projects)) names[name] = projects = [];
            projects.Add(project);
            providedAt.TryAdd((kind, name, project), (path, line));
        }
        using (var cmd = Sql.Command(conn,
            "SELECT d.repo_id, d.kind, d.name, f.path, f.is_vendored, d.line, d.scope, d.origin FROM file_decls d JOIN files f ON f.id = d.file_id WHERE d.role = 'provides'", null))
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                if (!byId.TryGetValue(reader.GetInt64(0), out var repo) || !repo.Default) continue;
                var kind = reader.GetString(1);
                var name = reader.GetString(2);
                var path = reader.GetString(3);
                if (kind == "ts-alias")
                {
                    // A path alias is the repository's own code under a package's name: never another's to provide.
                    if (!aliases.TryGetValue(repo.Id, out var own)) aliases[repo.Id] = own = new(StringComparer.Ordinal);
                    own.Add(name);
                    continue;
                }
                // A vendored copy, a test's file, a fixture or a sample: the library is elsewhere.
                if (reader.GetInt64(4) != 0 || NotAProvider(path) || reader.GetString(6) == "test" || reader.GetString(7) == "sample") continue;
                if (reader.GetString(7) == "generated" && kind != "proto")
                {
                    // Generated code provides a name only when no hand-written file does (its source is the provider).
                    if (kind == "py-file") generatedPy.Add(name);
                    generated.Add((kind, name, repo.GitlabId, path, reader.GetInt64(5)));
                    continue;
                }
                if (kind == "cs" && ForeignCsRoots.Contains(name.Split('.')[0]) && !nugetRoots.Contains((repo.Id, name.Split('.')[0].ToLowerInvariant()))) continue;
                if (kind == "proto")
                {
                    protoFiles.Add((name, repo.GitlabId));
                    providedAt.TryAdd(("proto", name, repo.GitlabId), (path, 1));
                    continue;
                }
                if (kind == "py-file")
                {
                    if (!pyFiles.TryGetValue(repo.GitlabId, out var list)) pyFiles[repo.GitlabId] = list = [];
                    list.Add(name);
                    continue;
                }
                Provide(kind, name, repo.GitlabId, path, reader.GetInt64(5));
            }
        }
        // Python modules, named from each repository's import roots (its generated files' among them, as generated).
        foreach (var g in generated.Where(g => g.Kind == "py-file"))
        {
            if (!pyFiles.TryGetValue(g.Project, out var list)) pyFiles[g.Project] = list = [];
            list.Add(g.Name);
        }
        generated.RemoveAll(g => g.Kind == "py-file");
        foreach (var (project, files) in pyFiles)
        {
            foreach (var (module, file) in PythonModulesAt(files))
            {
                if (generatedPy.Contains(file)) generated.Add(("py", module, project, file, 1));
                else Provide("py", module, project, file, 1);
            }
        }
        foreach (var g in generated.Where(g => !(provides.TryGetValue(g.Kind, out var names) && names.ContainsKey(g.Name))).ToList())
            Provide(g.Kind, g.Name, g.Project, g.Path, g.Line);
        // A top-level module name alone (config, utils) links only to a repository the user declares a dependency on.
        var pypiUses = Sql.Query(conn, "SELECT DISTINCT repo_id, name FROM file_decls WHERE role = 'uses' AND kind = 'pypi'")
            .GroupBy(r => r.Long("repo_id")).ToDictionary(g => g.Key, g => g.Select(r => r.Str("name")).ToHashSet(StringComparer.Ordinal));
        var pypiOf = new Dictionary<long, HashSet<string>>();
        if (provides.TryGetValue("pypi", out var distributions))
            foreach (var (dist, projects) in distributions)
                foreach (var project in projects)
                {
                    if (!pypiOf.TryGetValue(project, out var set)) pypiOf[project] = set = new(StringComparer.Ordinal);
                    set.Add(dist);
                }

        var stats = new Dictionary<string, long>(StringComparer.Ordinal);
        void Count(string state) => stats[state] = stats.GetValueOrDefault(state) + 1;
        var evidence = new Dictionary<(long From, long To, string Kind, string Name), Evidence>();
        var pending = new List<Pending>();
        // Which projects each file's uses resolved to uniquely: what settles a name its other uses agree on.
        var perFile = new Dictionary<long, HashSet<long>>();
        var memo = new Dictionary<(string Kind, string Name, long From), Match>();

        // A name several provide that nothing settled is a candidate of each, kept apart: it is never walked.
        var candidateEvidence = new Dictionary<(long From, long To, string Kind, string Name), Evidence>();
        Evidence Add(RepoRow from, long to, string declKind, string form, string matched, string how, int providers, bool candidate = false, string? settled = null)
        {
            var (kind, layer) = LinkKinds.Of(declKind, form);
            var key = (from.Id, defaultRow[to], kind, matched);
            var store = candidate ? candidateEvidence : evidence;
            var prior = LinkKinds.Prior(kind, how, settled);
            how = settled ?? how;
            if (!store.TryGetValue(key, out var ev))
            {
                store[key] = ev = new Evidence { DeclKind = declKind, Layer = layer, How = how, Providers = providers, Prior = prior, Candidate = candidate };
                ev.Provider = providedAt.TryGetValue((declKind is "cs-type" ? "cs" : declKind is "java-package" ? "java" : declKind, matched, to), out var p) ? p : null;
            }
            else if (prior > ev.Prior)
            {
                // The surest way any of its files made it.
                ev.How = how;
                ev.Prior = prior;
            }
            return ev;
        }

        using (var cmd = Sql.Command(conn,
            "SELECT d.repo_id, d.file_id, d.kind, d.name, d.line, d.form, d.scope, f.path FROM file_decls d JOIN files f ON f.id = d.file_id WHERE d.role = 'uses'", null))
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                // The graph is between default branches: another branch's uses are that branch's own question.
                if (!byId.TryGetValue(reader.GetInt64(0), out var from) || !from.Default) continue;
                var fileId = reader.GetInt64(1);
                var kind = reader.GetString(2);
                var name = reader.GetString(3);
                var line = reader.GetInt64(4);
                var form = reader.GetString(5);
                var scope = reader.GetString(6);
                var path = reader.GetString(7);
                if (!memo.TryGetValue((kind, name, from.Id), out var match))
                {
                    match = kind == "npm" && aliases.TryGetValue(from.Id, out var own) && own.Contains(name)
                        ? new Match("internal", [from.GitlabId], name, "exact")
                        : Resolve(kind, name, from, provides, protoFiles, byPath);
                    if (match.State is "resolved" or "ambiguous" && kind == "py" && !match.Matched.Contains('.'))
                    {
                        // A bare top-level name links only to a repository whose distribution the user declares.
                        var confirmed = match.Projects.Where(p => pypiUses.TryGetValue(from.Id, out var declared)
                            && pypiOf.TryGetValue(p, out var offered) && declared.Overlaps(offered)).ToHashSet();
                        match = confirmed.Count switch
                        {
                            0 => match with { State = "unconfirmed" },
                            1 => match with { State = "resolved", Projects = confirmed },
                            _ => match with { Projects = confirmed },
                        };
                    }
                    memo[(kind, name, from.Id)] = match;
                }
                Count(match.State);
                if (scope == "test") Count("test");
                switch (match.State)
                {
                    case "resolved" when defaultRow.ContainsKey(match.Projects.First()):
                    {
                        var to = match.Projects.First();
                        Add(from, to, kind, form, match.Matched, match.How, 1).Use(fileId, path, line, scope);
                        if (!perFile.TryGetValue(fileId, out var tied)) perFile[fileId] = tied = [];
                        tied.Add(to);
                        break;
                    }
                    case "ambiguous":
                        pending.Add(new Pending(from, fileId, path, line, scope, kind, form, match));
                        break;
                }
            }
        }

        // #include edges, resolved file by file before this (Resolve.ResolveIncludes), between default branches.
        foreach (var d in Sql.Query(conn, "SELECT from_repo_id, to_repo_id, weight FROM repo_deps"))
        {
            if (!byId.TryGetValue(d.Long("from_repo_id"), out var from) || !from.Default) continue;
            if (!byId.TryGetValue(d.Long("to_repo_id"), out var target) || !defaultRow.ContainsKey(target.GitlabId) || target.GitlabId == from.GitlabId) continue;
            var ev = Add(from, target.GitlabId, "include", "", "#include", "include", 1);
            ev.Why = $"{d.Long("weight")} #include line(s) resolved file by file";
            for (var i = 0; i < d.Long("weight"); i++) ev.MainFiles.Add(-(i + 1));
        }

        // A name several repositories provide: settled by what else the user has of one of them, else a candidate of each.
        var manifest = new Dictionary<long, HashSet<long>>();
        var graph = new Dictionary<long, HashSet<long>>();
        foreach (var ((from, to, kind, _), ev) in evidence)
        {
            var project = byId[to].GitlabId;
            if (!graph.TryGetValue(from, out var g)) graph[from] = g = [];
            if (ev.Prior >= LinkKinds.Walked) g.Add(project);
            if (ev.MainFiles.Count > 0 && (kind.StartsWith("package:", StringComparison.Ordinal) || kind == "repository" || kind == "ci:include"))
            {
                if (!manifest.TryGetValue(from, out var m)) manifest[from] = m = [];
                m.Add(project);
            }
        }
        var unsettled = new List<(Pending Use, HashSet<long> Candidates)>();
        var settledNames = new Dictionary<(long From, string Kind, string Name), HashSet<long>>();
        foreach (var p in pending)
        {
            var candidates = p.Match.Projects.Where(defaultRow.ContainsKey).ToHashSet();
            (long To, string How)? pick = null;
            foreach (var (how, tied) in new[] { ("settled:manifest", manifest.GetValueOrDefault(p.From.Id)), ("settled:file", perFile.GetValueOrDefault(p.FileId)), ("settled:graph", graph.GetValueOrDefault(p.From.Id)) })
            {
                if (tied is null) continue;
                var both = candidates.Where(tied.Contains).ToList();
                if (both.Count == 1)
                {
                    pick = (both[0], how);
                    break;
                }
                if (both.Count > 1) candidates = [.. both];
            }
            if (pick is not { } settled)
            {
                unsettled.Add((p, candidates));
                continue;
            }
            Add(p.From, settled.To, p.DeclKind, p.Form, p.Match.Matched, p.Match.How, p.Match.Projects.Count, settled: settled.How).Use(p.FileId, p.Path, p.Line, p.Scope);
            var name = (p.From.Id, LinkKinds.Of(p.DeclKind, p.Form).Kind, p.Match.Matched);
            if (!settledNames.TryGetValue(name, out var to)) settledNames[name] = to = [];
            to.Add(settled.To);
            Count(settled.How);
        }
        foreach (var (p, candidates) in unsettled)
        {
            var kind = LinkKinds.Of(p.DeclKind, p.Form).Kind;
            // The same name another of its files settled: the repository's own answer for it.
            if (settledNames.TryGetValue((p.From.Id, kind, p.Match.Matched), out var named) && named.Count == 1 && candidates.Contains(named.First()))
            {
                evidence[(p.From.Id, defaultRow[named.First()], kind, p.Match.Matched)].Use(p.FileId, p.Path, p.Line, p.Scope);
                Count("settled:name");
                continue;
            }
            Count("candidate");
            foreach (var to in candidates)
            {
                // A provider another of its files settled the name to already has the surer link.
                if (evidence.ContainsKey((p.From.Id, defaultRow[to], kind, p.Match.Matched))) continue;
                var ev = Add(p.From, to, p.DeclKind, p.Form, p.Match.Matched, p.Match.How, p.Match.Projects.Count, candidate: true);
                ev.Candidates = [.. p.Match.Projects.Where(pathOfProject.ContainsKey).Select(x => pathOfProject[x]).Order(StringComparer.Ordinal)];
                ev.Use(p.FileId, p.Path, p.Line, p.Scope);
            }
        }

        using var tx = conn.BeginTransaction();
        Sql.Exec(conn, "DELETE FROM repo_links");
        Sql.Exec(conn, "DELETE FROM repo_edges");
        Sql.Exec(conn, "DELETE FROM file_links");
        var edges = new Dictionary<(long From, long To, string Layer), List<(string Kind, double Confidence, string Scope, HashSet<long> Files)>>();
        using (var insert = Sql.Command(conn,
            "INSERT INTO repo_links (from_repo_id, to_repo_id, kind, name, layer, scope, how, providers, confidence, tier, files, evidence) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
            new object?[12]))
        using (var fileLink = Sql.Command(conn, "INSERT OR IGNORE INTO file_links (file_id, to_repo_id, kind, name, confidence) VALUES (?, ?, ?, ?, ?)", new object?[5]))
        {
            foreach (var ((from, to, kind, name), ev) in evidence.Concat(candidateEvidence.Where(c => !evidence.ContainsKey(c.Key))))
            {
                // A name several provide: a candidate (prior / k). Else its prior, less for a code link one file makes.
                var main = ev.MainFiles.Count > 0;
                var files = main ? ev.MainFiles : ev.TestFiles;
                var confidence = ev.Candidate
                    ? ev.Prior / Math.Max(2, ev.Providers)
                    : Math.Min(0.999, ev.Prior * (LinkKinds.FromCode(kind) && kind != "include" && files.Count == 1 ? LinkKinds.OneFile : 1));
                var tier = ev.Candidate ? "candidate" : LinkKinds.Tier(confidence);
                // No count of the providers: a reader may not see them all (GraphQueries says how many it may).
                var why = ev.Why ?? (ev.Candidate
                    ? $"{name} is provided by several repositories; nothing the user has settles which"
                    : ev.How.StartsWith("settled:", StringComparison.Ordinal)
                        ? $"{name} is provided by several repositories; {ev.How[8..] switch { "manifest" => "a package or submodule of this one", "file" => "the same file's other uses", "name" => "the same name in its other files", _ => "the user's other links" }} settles it"
                        : $"{ev.MainFiles.Count + ev.TestFiles.Count} file(s) use {name}");
                var json = new JsonObject
                {
                    ["why"] = why,
                    ["uses"] = new JsonArray([.. ev.Uses.Select(u => (JsonNode)new JsonObject { ["path"] = u.Path, ["line"] = u.Line })]),
                    ["provider"] = ev.Provider is { } at ? new JsonObject { ["path"] = at.Path, ["line"] = at.Line } : null,
                    ["candidates"] = ev.Candidates is { } c ? new JsonArray([.. c.Select(x => (JsonNode)x)]) : null,
                };
                object?[] values = [from, to, kind, name, ev.Layer, main ? "main" : "test", ev.How, ev.Providers, Math.Round(confidence, 4), tier, (long)files.Count, json.ToJsonString()];
                for (var i = 0; i < values.Length; i++) insert.Parameters[i].Value = values[i] ?? DBNull.Value;
                insert.ExecuteNonQuery();
                if (ev.Candidate) continue;
                if (!edges.TryGetValue((from, to, ev.Layer), out var ways)) edges[(from, to, ev.Layer)] = ways = [];
                ways.Add((kind, confidence, main ? "main" : "test", files));
                // Which files make it, by the name they use: change_impact searches a symbol only in them.
                if (LinkKinds.FromCode(kind) || kind.StartsWith("package:", StringComparison.Ordinal))
                    foreach (var file in ev.MainFiles.Concat(ev.TestFiles).Where(f => f > 0))
                    {
                        object?[] row = [file, to, ev.DeclKind, name, Math.Round(confidence, 4)];
                        for (var i = 0; i < row.Length; i++) fileLink.Parameters[i].Value = row[i];
                        fileLink.ExecuteNonQuery();
                    }
            }
        }
        using (var insertEdge = Sql.Command(conn,
            "INSERT INTO repo_edges (from_repo_id, to_repo_id, layer, scope, confidence, tier, files, kinds) VALUES (?, ?, ?, ?, ?, ?, ?, ?)", new object?[8]))
        {
            foreach (var ((from, to, layer), ways) in edges)
            {
                // The product's when any way is; then its confidence is the product's ways', the best of each kind, as one.
                var scope = ways.Any(w => w.Scope == "main") ? "main" : "test";
                var counted = ways.Where(w => w.Scope == scope).ToList();
                var byKind = counted.GroupBy(w => w.Kind).Select(g => (Kind: g.Key, Best: g.Max(w => w.Confidence))).OrderByDescending(k => k.Best).ToList();
                var confidence = Math.Round(Math.Min(0.999, LinkKinds.AnyOf(byKind.Select(k => k.Best))), 4);
                var files = counted.SelectMany(w => w.Files).ToHashSet().Count;
                object?[] row = [from, to, layer, scope, confidence, LinkKinds.Tier(confidence), (long)files, string.Join(",", byKind.Select(k => k.Kind))];
                for (var i = 0; i < row.Length; i++) insertEdge.Parameters[i].Value = row[i];
                insertEdge.ExecuteNonQuery();
                stats[$"{LinkKinds.Tier(confidence)}_edges"] = stats.GetValueOrDefault($"{LinkKinds.Tier(confidence)}_edges") + 1;
            }
        }
        stats["links"] = (long)(Sql.Scalar(conn, "SELECT COUNT(*) FROM repo_links WHERE tier <> 'candidate'") ?? 0L);
        stats["candidate_links"] = (long)(Sql.Scalar(conn, "SELECT COUNT(*) FROM repo_links WHERE tier = 'candidate'") ?? 0L);
        stats["linked_pairs"] = (long)(Sql.Scalar(conn,
            "SELECT COUNT(*) FROM (SELECT DISTINCT from_repo_id, to_repo_id FROM repo_edges WHERE confidence >= ? AND layer <> 'history')", LinkKinds.Walked) ?? 0L);
        Sql.Exec(conn, "INSERT OR REPLACE INTO argus_meta (key, value) VALUES ('graph_stats', ?)",
            new JsonObject(stats.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))).ToJsonString());
        tx.Commit();
        return stats;
    }

    /// <summary>
    /// What a use resolves to: "internal" (its own repository provides it), "resolved" (one other does), "ambiguous"
    /// (several do), "external" (none: a public package, the standard library); the name matched, and how.
    /// </summary>
    static Match Resolve(string kind, string name, RepoRow from,
        Dictionary<string, Dictionary<string, HashSet<long>>> provides, List<(string Path, long Project)> protoFiles, Dictionary<string, RepoRow> byPath)
    {
        HashSet<long>? Lookup(string providerKind, string n) =>
            provides.TryGetValue(providerKind, out var names) && names.TryGetValue(n, out var p) ? p : null;

        Match Decide(HashSet<long>? projects, string matched, string how = "exact")
        {
            if (projects is null || projects.Count == 0) return Match.External;
            if (projects.Contains(from.GitlabId)) return new("internal", [from.GitlabId], matched, how);
            return new(projects.Count == 1 ? "resolved" : "ambiguous", projects, matched, how);
        }

        switch (kind)
        {
            case "repo":
            {
                // A relative submodule URL is from this repository's own path (platform/auth + ../core/protos is platform/core/protos).
                var path = name.StartsWith("rel:", StringComparison.Ordinal) ? Relative(from.Path, name[4..]) : name;
                return byPath.TryGetValue(path, out var repo) ? Decide([repo.GitlabId], path) : Match.External;
            }
            case "image":
            {
                if (name.StartsWith("*/", StringComparison.Ordinal))
                {
                    // A private registry's flat name: the repository with that name, when only one has it.
                    var flat = name[2..];
                    return Decide(byPath.Values.Where(r => r.Path.EndsWith("/" + flat, StringComparison.Ordinal)).Select(r => r.GitlabId).ToHashSet(), name, "flat");
                }
                // group/app; an image under a registry's own group (registry/team/group/app); a project's sub-image as GitLab's
                // registry names them (group/app/api). A longer repository path is not one: a mirror of the image is not it.
                var hits = byPath.Values.Where(r => r.Path == name || name.EndsWith("/" + r.Path, StringComparison.Ordinal) || name.StartsWith(r.Path + "/", StringComparison.Ordinal))
                    .ToList();
                // The project itself rather than a group above it that is also a project.
                if (hits.Count > 1 && hits.MaxBy(r => r.Path.Length) is { } longest && (name == longest.Path || name.StartsWith(longest.Path + "/", StringComparison.Ordinal)))
                    hits = [longest];
                var sub = hits.Count == 1 && name.StartsWith(hits[0].Path + "/", StringComparison.Ordinal);
                return Decide(hits.Select(r => r.GitlabId).ToHashSet(), name, sub ? "sub" : "registry");
            }
            case "proto":
            {
                var exact = protoFiles.Where(p => p.Path == name).Select(p => p.Project).ToHashSet();
                if (exact.Count > 0) return Decide(exact, name);
                return Decide(protoFiles.Where(p => p.Path.EndsWith("/" + name, StringComparison.Ordinal)).Select(p => p.Project).ToHashSet(), name, "suffix");
            }
            case "cs":
            case "java-package":
                return Decide(Lookup(kind == "cs" ? "cs" : "java", name), name);
            case "cs-type":
            {
                // using static A.B.Type; using X = A.B(.Type): the namespace itself, or the one that holds the type.
                if (Lookup("cs", name) is { Count: > 0 } ns) return Decide(ns, name);
                var up = name.LastIndexOf('.');
                return up > 0 ? Decide(Lookup("cs", name[..up]), name[..up], "type") : Match.External;
            }
            case "java":
            {
                // import a.b.Type(.Nested): the package before the types (capitalised); a Kotlin top-level function
                // (a.b.round) is one lower-case segment past its package.
                var parts = name.Split('.');
                var cut = parts.Length;
                while (cut > 1 && parts[cut - 1].Length > 0 && char.IsUpper(parts[cut - 1][0])) cut--;
                var package = string.Join('.', parts[..cut]);
                if (Lookup("java", package) is { Count: > 0 } exact) return Decide(exact, package);
                if (cut == parts.Length && cut > 1 && string.Join('.', parts[..(cut - 1)]) is var holder && Lookup("java", holder) is { Count: > 0 } function)
                    return Decide(function, holder, "member");
                return Match.External;
            }
            case "py" when PyStdlib.Contains(name.Split('.')[0]):
                // The standard library's, whatever a repository calls its own package.
                return Match.External;
        }
        if (PrefixSeparator(kind) is not { } sep)
            return Decide(Lookup(kind, name), name);
        // The longest name provided that starts it: its own repository first at every length.
        for (var n = name; n.Length > 0; n = n.LastIndexOf(sep) is var cut and > 0 ? n[..cut] : "")
        {
            if (Lookup(kind, n) is { Count: > 0 } projects)
                return Decide(projects, n, kind == "go" ? "module" : n == name ? "exact" : !n.Contains('.') ? "top" : "prefix");
        }
        return Match.External;
    }
}
