using System.Text.RegularExpressions;
using Argus.Util;
using Microsoft.Data.Sqlite;

namespace Argus.Store;

/// <summary>
/// What each repository is for, in a line, kept with the graph (repo_purpose): its README's first sentence that says
/// something, else its manifest's description (package.json, a .csproj, pyproject.toml, Cargo.toml, pom.xml ...), else
/// what its code is (a C# service of 120 files ...). Written again after each pass, except one an admin or its owners
/// put, which stays until they take it back.
/// </summary>
public static partial class Purposes
{
    public const string Owner = "owner", Readme = "readme", Manifest = "manifest", Code = "code";
    public const int MaxChars = 200;

    static readonly Dictionary<string, string> LanguageNames = new(StringComparer.Ordinal)
    {
        ["csharp"] = "C#", ["c"] = "C", ["cpp"] = "C++", ["python"] = "Python", ["go"] = "Go", ["java"] = "Java", ["kotlin"] = "Kotlin",
        ["typescript"] = "TypeScript", ["javascript"] = "JavaScript", ["rust"] = "Rust", ["ruby"] = "Ruby", ["php"] = "PHP", ["scala"] = "Scala",
        ["swift"] = "Swift", ["shell"] = "shell", ["proto"] = "Protocol Buffers", ["terraform"] = "Terraform", ["yaml"] = "YAML",
    };

    /// <summary>The line for a repository's index row, and where it came from; null when nothing says.</summary>
    public static (string Text, string Source)? Describe(SqliteConnection conn, long repoId) =>
        FromReadme(conn, repoId) is { } readme ? (readme, Readme)
        : FromManifest(conn, repoId) is { } manifest ? (manifest, Manifest)
        : FromCode(conn, repoId) is { } code ? (code, Code)
        : null;

    /// <summary>Its line, by its GitLab id; null when there is none.</summary>
    public static string? Of(SqliteConnection conn, long gitlabId) =>
        Sql.One(conn, "SELECT purpose FROM repo_purpose WHERE gitlab_id = ?", gitlabId)?.Str("purpose");

    /// <summary>Its line, by an index row of it (any branch).</summary>
    public static string? OfRepo(SqliteConnection conn, long repoId) =>
        Sql.One(conn, "SELECT p.purpose FROM repo_purpose p JOIN repos r ON r.gitlab_id = p.gitlab_id WHERE r.id = ?", repoId)?.Str("purpose");

    /// <summary>Every line with its source, by GitLab id.</summary>
    public static Dictionary<long, (string Text, string Source)> All(SqliteConnection conn) =>
        Sql.Query(conn, "SELECT gitlab_id, purpose, source FROM repo_purpose").ToDictionary(r => r.Long("gitlab_id"), r => (r.Str("purpose"), r.Str("source")));

    /// <summary>Each indexed repository's line written again from its default branch, except those its owners put. Returns how many have one.</summary>
    public static int Refresh(SqliteConnection conn, long now)
    {
        var owned = Sql.Query(conn, "SELECT gitlab_id FROM repo_purpose WHERE source = ?", Owner).Select(r => r.Long("gitlab_id")).ToHashSet();
        var described = owned.Count;
        foreach (var row in Sql.Query(conn, "SELECT id, gitlab_id FROM repos WHERE branch = default_branch").ToList())
        {
            var gitlabId = row.Long("gitlab_id");
            if (owned.Contains(gitlabId)) continue;
            if (Describe(conn, row.Long("id")) is { } line)
            {
                Sql.Exec(conn,
                    "INSERT INTO repo_purpose (gitlab_id, purpose, source, updated_at) VALUES (?, ?, ?, ?) " +
                    "ON CONFLICT(gitlab_id) DO UPDATE SET purpose = excluded.purpose, source = excluded.source, updated_at = excluded.updated_at " +
                    "WHERE repo_purpose.purpose <> excluded.purpose OR repo_purpose.source <> excluded.source",
                    gitlabId, line.Text, line.Source, now);
                described++;
            }
            else
            {
                Sql.Exec(conn, "DELETE FROM repo_purpose WHERE gitlab_id = ? AND source <> ?", gitlabId, Owner);
            }
        }
        return described;
    }

    /// <summary>The line its owners put (kept over the passes); null or empty gives it back to the README and the code.</summary>
    public static void Set(SqliteConnection conn, long gitlabId, string? text, long now)
    {
        var line = Clean(text ?? "");
        if (line.Length == 0)
        {
            Sql.Exec(conn, "DELETE FROM repo_purpose WHERE gitlab_id = ?", gitlabId);
            if (Sql.One(conn, "SELECT id FROM repos WHERE gitlab_id = ? AND branch = default_branch", gitlabId) is { } row && Describe(conn, row.Long("id")) is { } found)
                Sql.Exec(conn, "INSERT INTO repo_purpose (gitlab_id, purpose, source, updated_at) VALUES (?, ?, ?, ?)", gitlabId, found.Text, found.Source, now);
            return;
        }
        Sql.Exec(conn,
            "INSERT INTO repo_purpose (gitlab_id, purpose, source, updated_at) VALUES (?, ?, ?, ?) " +
            "ON CONFLICT(gitlab_id) DO UPDATE SET purpose = excluded.purpose, source = excluded.source, updated_at = excluded.updated_at",
            gitlabId, line, Owner, now);
    }

    /// <summary>Markdown taken out ([text](url) as text, `code`, emphasis), spaces made one, cut to <see cref="MaxChars"/>.</summary>
    public static string Clean(string text)
    {
        var plain = MarkdownLink().Replace(text, "$1");
        plain = plain.Replace("**", "", StringComparison.Ordinal).Replace("__", "", StringComparison.Ordinal).Replace("`", "", StringComparison.Ordinal);
        plain = Spaces().Replace(plain, " ").Trim();
        return plain.Length <= MaxChars ? plain : plain[..(MaxChars - 1)].TrimEnd() + "…";
    }

    /// <summary>The first sentence of the top README that says something: not a heading, badge, image, table, code or HTML.</summary>
    public static string? FromReadmeText(string content)
    {
        var lines = content.Replace("\r\n", "\n").Split('\n');
        var i = 0;
        // YAML front matter.
        if (lines.Length > 0 && lines[0].Trim() == "---")
        {
            for (i = 1; i < lines.Length && lines[i].Trim() != "---"; i++) { }
            i++;
        }
        var fenced = false;
        var comment = false;
        for (; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith("```", StringComparison.Ordinal) || line.StartsWith("~~~", StringComparison.Ordinal)) { fenced = !fenced; continue; }
            if (fenced) continue;
            if (comment) { comment = !line.Contains("-->", StringComparison.Ordinal); continue; }
            if (line.StartsWith("<!--", StringComparison.Ordinal)) { comment = !line.Contains("-->", StringComparison.Ordinal); continue; }
            if (line.Length < 12 || line.StartsWith('#') || line.StartsWith('!') || line.StartsWith('<') || line.StartsWith('|') || line.StartsWith('=')
                || line.StartsWith("[!", StringComparison.Ordinal) || line.StartsWith("[![", StringComparison.Ordinal) || line.StartsWith(".. ", StringComparison.Ordinal)
                || line.All(c => c is '-' or '=' or '~' or '*' or '_' or ' ')) continue;
            // A line that is only links (a badge row, a table of contents).
            var text = Clean(line.TrimStart('>', ' ', '-', '*'));
            if (text.Length < 12 || !text.Contains(' ', StringComparison.Ordinal) || text.StartsWith("Table of contents", StringComparison.OrdinalIgnoreCase)) continue;
            if (i + 1 < lines.Length && lines[i + 1].Trim() is { Length: > 2 } under && under.All(c => c is '=' or '-')) continue;
            var stop = SentenceEnd().Match(text);
            return Clean(stop.Success ? text[..(stop.Index + 1)] : text);
        }
        return null;
    }

    static string? FromReadme(SqliteConnection conn, long repoId)
    {
        var row = Sql.One(conn,
            "SELECT content FROM files WHERE repo_id = ? AND lower(path) LIKE 'readme%' AND path NOT LIKE '%/%' ORDER BY length(path), path LIMIT 1", repoId);
        return row is null ? null : FromReadmeText(row.Str("content"));
    }

    /// <summary>A package's own description: package.json, a .csproj (or Directory.Build.props), pyproject.toml, setup.cfg, setup.py, Cargo.toml, pom.xml, composer.json.</summary>
    static string? FromManifest(SqliteConnection conn, long repoId)
    {
        var rows = Sql.Query(conn,
            "SELECT path, content FROM files WHERE repo_id = ? AND is_vendored = 0 AND path NOT LIKE '%/%/%/%' AND (" +
            " path IN ('package.json', 'pyproject.toml', 'setup.cfg', 'setup.py', 'Cargo.toml', 'pom.xml', 'composer.json', 'Directory.Build.props')" +
            " OR path LIKE '%.csproj') ORDER BY (path LIKE '%/%'), length(path), path LIMIT 12", repoId);
        foreach (var row in rows)
        {
            var content = row.Str("content");
            var match = Path.GetFileName(row.Str("path")) switch
            {
                "package.json" or "composer.json" => JsonDescription().Match(content),
                "pyproject.toml" or "Cargo.toml" or "setup.cfg" => TomlDescription().Match(content),
                "setup.py" => PyDescription().Match(content),
                "pom.xml" => PomDescription().Match(content),
                _ => MsBuildDescription().Match(content),
            };
            if (match.Success && Clean(match.Groups[1].Value) is { Length: >= 8 } line && line.Contains(' ', StringComparison.Ordinal)) return line;
        }
        return null;
    }

    /// <summary>What its code is: its main language, a service or a library, how many files, its top folders.</summary>
    static string? FromCode(SqliteConnection conn, long repoId)
    {
        // Its main language: the one with the most files among programming languages, else among any.
        var langs = Sql.Query(conn,
                "SELECT lang, COUNT(*) AS n FROM files WHERE repo_id = ? AND is_vendored = 0 AND lang IS NOT NULL AND lang NOT IN ('markdown', 'text', 'json', 'yaml', 'xml') " +
                "GROUP BY lang ORDER BY n DESC, lang", repoId)
            .OrderBy(r => LanguageNames.ContainsKey(r.Str("lang")) ? 0 : 1).ToList();
        var files = Sql.One(conn, "SELECT COUNT(*) AS n FROM files WHERE repo_id = ? AND is_vendored = 0", repoId)?.Long("n") ?? 0;
        if (files == 0) return null;
        var paths = Sql.Query(conn, "SELECT path FROM files WHERE repo_id = ? AND is_vendored = 0", repoId).Select(r => r.Str("path")).ToList();
        var service = paths.Any(p => Path.GetFileName(p) is "Dockerfile" or "Program.cs" or "main.go" or "__main__.py" or "manage.py" or "main.rs" or "server.js" or "server.ts"
            || p.EndsWith("Application.java", StringComparison.Ordinal));
        var folders = paths.Where(p => p.Contains('/', StringComparison.Ordinal)).GroupBy(p => p[..p.IndexOf('/', StringComparison.Ordinal)])
            .OrderByDescending(g => g.Count()).Take(3).Select(g => g.Key + "/").ToList();
        var lang = langs.FirstOrDefault()?.Str("lang");
        var name = lang is null ? "" : LanguageNames.GetValueOrDefault(lang, lang) + " ";
        var what = service ? "service" : "library";
        return Clean($"A {name}{what} of {files} files" + (folders.Count > 0 ? $" ({string.Join(", ", folders)})." : "."));
    }

    [GeneratedRegex(@"\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex MarkdownLink();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"[.!?](\s|$)")]
    private static partial Regex SentenceEnd();

    [GeneratedRegex("\"description\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"")]
    private static partial Regex JsonDescription();

    [GeneratedRegex("(?m)^\\s*description\\s*=\\s*[\"']?([^\"'\\n]+)")]
    private static partial Regex TomlDescription();

    [GeneratedRegex("description\\s*=\\s*[\"']([^\"']+)[\"']")]
    private static partial Regex PyDescription();

    [GeneratedRegex("<description>\\s*([^<]+?)\\s*</description>", RegexOptions.IgnoreCase)]
    private static partial Regex PomDescription();

    [GeneratedRegex("<(?:Package)?Description>\\s*([^<]+?)\\s*</(?:Package)?Description>")]
    private static partial Regex MsBuildDescription();
}
