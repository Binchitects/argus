using System.Text.Json.Nodes;
using Argus.Indexing;
using Argus.Util;
using Microsoft.Data.Sqlite;

namespace Argus.Store;

public static partial class Graph
{
    /// <summary>How a link came about, by the kind of what the file used.</summary>
    public static string LinkKind(string declKind) => declKind switch
    {
        "nuget" or "npm" or "pypi" or "cargo" or "maven" => "package:" + declKind,
        "cs" or "cs-type" => "import:csharp",
        "java" or "java-package" => "import:java",
        "py" => "import:python",
        "go" => "import:go",
        "proto" => "import:proto",
        "repo" => "repository",
        "image" => "image",
        _ => declKind,
    };

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

    /// <summary>
    /// Rebuilds repo_links from what files declare (file_decls) and from resolved #includes (repo_deps): each use resolved
    /// to the one repository that provides its name. A repository's own name stays inside it; a name two repositories
    /// provide links to neither (counted as ambiguous). Returns the counts by state, kept for index_status too.
    /// </summary>
    public static Dictionary<string, long> RebuildLinks(SqliteConnection conn)
    {
        var repos = Sql.Query(conn, "SELECT id, gitlab_id, lower(path_with_namespace) AS path, branch, default_branch FROM repos")
            .Select(r => new RepoRow(r.Long("id"), r.Long("gitlab_id"), r.Str("path"), r.Str("branch") == r.Str("default_branch")))
            .ToList();
        var byId = repos.ToDictionary(r => r.Id);
        // A library is linked at its default branch: that is what others build against.
        var defaultRow = repos.Where(r => r.Default).GroupBy(r => r.GitlabId).ToDictionary(g => g.Key, g => g.First().Id);
        var byPath = repos.Where(r => r.Default).GroupBy(r => r.Path).ToDictionary(g => g.Key, g => g.First());

        // What each project provides, by kind and name (names of the default branch only).
        var provides = new Dictionary<string, Dictionary<string, HashSet<long>>>(StringComparer.Ordinal);
        var protoFiles = new List<(string Path, long Project)>();
        // A repository's own packages (its PackageIds): a .NET root of its own (Microsoft's own estate) is its to provide.
        var nugetRoots = Sql.Query(conn, "SELECT DISTINCT repo_id, name FROM file_decls WHERE role = 'provides' AND kind = 'nuget'")
            .Select(r => (r.Long("repo_id"), r.Str("name").Split('.')[0])).ToHashSet();
        using (var cmd = Sql.Command(conn,
            "SELECT d.repo_id, d.kind, d.name, f.path, f.is_vendored FROM file_decls d JOIN files f ON f.id = d.file_id WHERE d.role = 'provides'", null))
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                if (!byId.TryGetValue(reader.GetInt64(0), out var repo) || !repo.Default) continue;
                var kind = reader.GetString(1);
                var name = reader.GetString(2);
                // A vendored copy, a test's fixture or a sample provides nothing: the library is the repository it came from.
                if (reader.GetInt64(4) != 0 || NotAProvider(reader.GetString(3))) continue;
                if (kind == "cs" && ForeignCsRoots.Contains(name.Split('.')[0]) && !nugetRoots.Contains((repo.Id, name.Split('.')[0].ToLowerInvariant()))) continue;
                if (kind == "proto")
                {
                    protoFiles.Add((name, repo.GitlabId));
                    continue;
                }
                if (!provides.TryGetValue(kind, out var names)) provides[kind] = names = new(StringComparer.Ordinal);
                if (!names.TryGetValue(name, out var projects)) names[name] = projects = [];
                projects.Add(repo.GitlabId);
            }
        }

        var stats = new Dictionary<string, long>(StringComparer.Ordinal);
        void Count(string state) => stats[state] = stats.GetValueOrDefault(state) + 1;
        // (from row, to row, link kind, name) -> files, one example
        var links = new Dictionary<(long From, long To, string Kind, string Name), (HashSet<long> Files, long FileId, long Line)>();

        using (var cmd = Sql.Command(conn, "SELECT d.repo_id, d.file_id, d.kind, d.name, d.line FROM file_decls d WHERE d.role = 'uses'", null))
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                if (!byId.TryGetValue(reader.GetInt64(0), out var from)) continue;
                var fileId = reader.GetInt64(1);
                var kind = reader.GetString(2);
                var name = reader.GetString(3);
                var (state, project, matched) = Resolve(kind, name, from, provides, protoFiles, byPath);
                Count(state);
                if (state != "resolved" || !defaultRow.TryGetValue(project, out var to) || project == from.GitlabId) continue;
                var key = (from.Id, to, LinkKind(kind), matched);
                if (!links.TryGetValue(key, out var link)) links[key] = link = ([], fileId, reader.GetInt64(4));
                link.Files.Add(fileId);
            }
        }

        using var tx = conn.BeginTransaction();
        Sql.Exec(conn, "DELETE FROM repo_links");
        foreach (var ((from, to, kind, name), (files, fileId, line)) in links)
            Sql.Exec(conn, "INSERT INTO repo_links (from_repo_id, to_repo_id, kind, name, files, file_id, line) VALUES (?, ?, ?, ?, ?, ?, ?)",
                from, to, kind, name, files.Count, fileId, line);
        // #include edges, resolved file by file before this (Resolve.ResolveIncludes): one link each, its weight the files.
        Sql.Exec(conn,
            "INSERT OR REPLACE INTO repo_links (from_repo_id, to_repo_id, kind, name, files, file_id, line)" +
            " SELECT from_repo_id, to_repo_id, 'include', '#include', weight, NULL, NULL FROM repo_deps");
        stats["links"] = (long)(Sql.Scalar(conn, "SELECT COUNT(*) FROM repo_links") ?? 0L);
        stats["linked_pairs"] = (long)(Sql.Scalar(conn, "SELECT COUNT(*) FROM (SELECT DISTINCT from_repo_id, to_repo_id FROM repo_links)") ?? 0L);
        Sql.Exec(conn, "INSERT OR REPLACE INTO argus_meta (key, value) VALUES ('graph_stats', ?)",
            new JsonObject(stats.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))).ToJsonString());
        tx.Commit();
        return stats;
    }

    /// <summary>
    /// The project a use resolves to: "internal" (its own repository provides it), "resolved" (one other does), "ambiguous"
    /// (several do), "external" (none: a public package, the standard library).
    /// </summary>
    static (string State, long Project, string Matched) Resolve(string kind, string name, RepoRow from,
        Dictionary<string, Dictionary<string, HashSet<long>>> provides, List<(string Path, long Project)> protoFiles, Dictionary<string, RepoRow> byPath)
    {
        HashSet<long>? Lookup(string providerKind, string n) =>
            provides.TryGetValue(providerKind, out var names) && names.TryGetValue(n, out var p) ? p : null;

        (string, long, string) Decide(HashSet<long>? projects, string matched)
        {
            if (projects is null || projects.Count == 0) return ("external", 0, "");
            if (projects.Contains(from.GitlabId)) return ("internal", from.GitlabId, matched);
            return projects.Count == 1 ? ("resolved", projects.First(), matched) : ("ambiguous", 0, matched);
        }

        switch (kind)
        {
            case "repo":
                return byPath.TryGetValue(name, out var repo) ? Decide([repo.GitlabId], name) : ("external", 0, "");
            case "image":
            {
                // group/app, or an image under a registry's own group (registry/team/group/app).
                var hits = byPath.Values.Where(r => r.Path == name || name.EndsWith("/" + r.Path, StringComparison.Ordinal) || r.Path.EndsWith("/" + name, StringComparison.Ordinal))
                    .Select(r => r.GitlabId).ToHashSet();
                return Decide(hits, name);
            }
            case "proto":
            {
                var hits = protoFiles.Where(p => p.Path == name || p.Path.EndsWith("/" + name, StringComparison.Ordinal)).Select(p => p.Project).ToHashSet();
                return Decide(hits, name);
            }
        }
        switch (kind)
        {
            case "cs":
            case "java-package":
                return Decide(Lookup(kind == "cs" ? "cs" : "java", name), name);
            case "cs-type":
            {
                // using static A.B.Type; using X = A.B(.Type): the namespace itself, or the one that holds the type.
                if (Lookup("cs", name) is { Count: > 0 } ns) return Decide(ns, name);
                var up = name.LastIndexOf('.');
                return up > 0 ? Decide(Lookup("cs", name[..up]), name[..up]) : ("external", 0, "");
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
                    return Decide(function, holder);
                return ("external", 0, "");
            }
            case "py" when PyStdlib.Contains(name.Split('.')[0]):
                // The standard library's, whatever a repository calls its own package.
                return ("external", 0, "");
        }
        if (PrefixSeparator(kind) is not { } sep)
            return Decide(Lookup(kind, name), name);
        // The longest name provided that starts it: its own repository first at every length.
        for (var n = name; n.Length > 0; n = n.LastIndexOf(sep) is var cut and > 0 ? n[..cut] : "")
        {
            if (Lookup(kind, n) is { Count: > 0 } projects) return Decide(projects, n);
        }
        return ("external", 0, "");
    }
}
