using System.Text.RegularExpressions;

namespace CodeArena;

/// <summary>What is known of a file before it is read: what it is for, and whether its words may be kept at all.</summary>
[Flags]
internal enum CodeFileTags
{
    None = 0,
    Test = 1,
    Generated = 2,
    Vendored = 4,
    Docs = 8,
    /// <summary>A key, a password, an .env: its name is listed, its contents never read.</summary>
    Sensitive = 16,
}

/// <summary>A file the code index covers: its path as Code Arena shows it (relative, with /), its language and what it is for.</summary>
internal sealed record CodeFile(string Path, string Full, string? Lang, long Size, DateTime Modified, CodeFileTags Tags);

/// <summary>
/// The files the code index reads: exactly those grep reads (git's list, so .gitignore holds, else the usual walk), less
/// the ones too big, binary, or holding secrets. A secret's file is never read into the index, whatever git says of it.
/// </summary>
internal static partial class CodeFiles
{
    /// <summary>Text is indexed up to this size (the IDE's search reads as much); symbols are read up to <see cref="MostSymbolBytes"/>.</summary>
    public const long MostTextBytes = 2 * 1024 * 1024;
    public const long MostSymbolBytes = 1024 * 1024;
    public const int MostFiles = 100_000;

    /// <summary>The language a file is read as, by its name; null: text only.</summary>
    public static string? LanguageOf(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".cs" => "csharp",
        ".ts" or ".tsx" or ".mts" or ".cts" => "typescript",
        ".js" or ".jsx" or ".mjs" or ".cjs" => "javascript",
        ".py" or ".pyi" => "python",
        ".go" => "go",
        ".java" => "java",
        ".kt" or ".kts" => "kotlin",
        ".rs" => "rust",
        ".md" or ".markdown" => "markdown",
        _ => null,
    };

    private static readonly string[] SecretFolders = [".ssh", ".gnupg", ".aws", ".gcloud", "secrets"];
    private static readonly string[] SecretEnds = [".pem", ".key", ".p12", ".pfx", ".crt", ".der", ".p8", ".kdbx", ".keystore", ".jks"];
    private static readonly string[] SecretNames = [".netrc", ".npmrc", ".pypirc", ".git-credentials", ".htpasswd", "credentials", "credentials.json"];
    private static readonly string[] SecretWords = ["secret", "token", "password", "passwd", "credential", "private_key", "privatekey"];

    /// <summary>Whether a file, by its path, may hold a secret: its contents are not read into the index.</summary>
    public static bool Sensitive(string path)
    {
        var parts = path.Split('/');
        if (parts.SkipLast(1).Any(p => SecretFolders.Contains(p, StringComparer.OrdinalIgnoreCase)))
        {
            return true;
        }
        var name = parts[^1].ToLowerInvariant();
        if (name == ".env" || (name.StartsWith(".env.", StringComparison.Ordinal) && !name.EndsWith(".example", StringComparison.Ordinal)
            && !name.EndsWith(".sample", StringComparison.Ordinal) && !name.EndsWith(".template", StringComparison.Ordinal)))
        {
            return true;
        }
        if (SecretNames.Contains(name) || name.StartsWith("id_rsa", StringComparison.Ordinal) || name.StartsWith("id_ed25519", StringComparison.Ordinal)
            || name.StartsWith("id_ecdsa", StringComparison.Ordinal) || SecretEnds.Any(e => name.EndsWith(e, StringComparison.Ordinal)))
        {
            return true;
        }
        // A config named for what it holds (db-password.txt, api_token.json), not code that merely handles one (TokenCounter.cs).
        var stem = System.IO.Path.GetFileNameWithoutExtension(name);
        return LanguageOf(name) is null && SecretWords.Any(w => stem.EndsWith(w, StringComparison.Ordinal));
    }

    /// <summary>Whether text holds what looks like a secret itself: a private key, or a token of a known shape.</summary>
    public static bool HoldsSecret(string text) => text.Contains("PRIVATE KEY-----", StringComparison.Ordinal) || TokenShape().IsMatch(text);

    /// <summary>Tokens of known shapes: AWS's, GitHub's, GitLab's, Slack's, an OpenAI-style sk-.</summary>
    [GeneratedRegex(@"(?<![A-Za-z0-9])(AKIA[0-9A-Z]{16}|gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,}|glpat-[A-Za-z0-9_-]{20,}|xox[abprs]-[A-Za-z0-9-]{10,}|sk-[A-Za-z0-9_-]{20,})")]
    public static partial Regex TokenShape();

    /// <summary>What a file is for, by its path and its first lines.</summary>
    public static CodeFileTags TagsOf(string path, string? head = null)
    {
        var tags = CodeFileTags.None;
        var name = path.Split('/')[^1];
        if (path.StartsWith("tests/", StringComparison.Ordinal) || path.Contains("/tests/", StringComparison.Ordinal) || path.Contains("/test/", StringComparison.Ordinal)
            || path.Contains("/__tests__/", StringComparison.Ordinal) || name.EndsWith("Tests.cs", StringComparison.Ordinal) || name.EndsWith("Test.cs", StringComparison.Ordinal)
            || TestName().IsMatch(name) || name.StartsWith("test_", StringComparison.Ordinal) || name.EndsWith("_test.go", StringComparison.Ordinal)
            || name.EndsWith("_test.py", StringComparison.Ordinal))
        {
            tags |= CodeFileTags.Test;
        }
        if (name.EndsWith(".Designer.cs", StringComparison.Ordinal) || name.EndsWith(".g.cs", StringComparison.Ordinal) || name.EndsWith(".g.i.cs", StringComparison.Ordinal)
            || name.EndsWith("ModelSnapshot.cs", StringComparison.Ordinal) || name.EndsWith(".min.js", StringComparison.Ordinal) || name.EndsWith(".pb.go", StringComparison.Ordinal)
            || (head is not null && (head.Contains("<auto-generated", StringComparison.Ordinal) || head.Contains("@generated", StringComparison.Ordinal)
                || head.Contains("Code generated", StringComparison.Ordinal))))
        {
            tags |= CodeFileTags.Generated;
        }
        if (path.StartsWith("vendor/", StringComparison.Ordinal) || path.Contains("/vendor/", StringComparison.Ordinal) || path.StartsWith("third_party/", StringComparison.Ordinal)
            || path.Contains("/third_party/", StringComparison.Ordinal) || path.Contains("/node_modules/", StringComparison.Ordinal))
        {
            tags |= CodeFileTags.Vendored;
        }
        if (LanguageOf(path) == "markdown" || path.StartsWith("docs/", StringComparison.Ordinal) || path.Contains("/docs/", StringComparison.Ordinal) || name.EndsWith(".rst", StringComparison.Ordinal)
            || name.EndsWith(".txt", StringComparison.Ordinal))
        {
            tags |= CodeFileTags.Docs;
        }
        if (Sensitive(path))
        {
            tags |= CodeFileTags.Sensitive;
        }
        return tags;
    }

    [GeneratedRegex(@"\.(test|spec)\.[cm]?[jt]sx?$")]
    private static partial Regex TestName();

    /// <summary>
    /// The files under the working folder (and each folder added to it) the index covers, as grep sees them: binary files,
    /// those past <see cref="MostTextBytes"/>, and links that lead outside are left out; at most <see cref="MostFiles"/>.
    /// </summary>
    public static List<CodeFile> List(Workspace workspace, IEnumerable<string> roots, CancellationToken ct)
    {
        var files = new List<CodeFile>();
        foreach (var root in roots)
        {
            foreach (var full in workspace.FilesUnder(root, ct))
            {
                ct.ThrowIfCancellationRequested();
                FileInfo info;
                try
                {
                    info = new FileInfo(full);
                    if (!info.Exists || info.Length > MostTextBytes)
                    {
                        continue;
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    continue;
                }
                var path = workspace.Show(full).Replace('\\', '/');
                var tags = TagsOf(path);
                // A secret's file is listed (so the index can say it was skipped) without being opened, binary or not.
                if (!tags.HasFlag(CodeFileTags.Sensitive) && Files.LooksBinary(full))
                {
                    continue;
                }
                files.Add(new CodeFile(path, full, LanguageOf(path), info.Length, info.LastWriteTimeUtc, tags));
                if (files.Count >= MostFiles)
                {
                    return files;
                }
            }
        }
        return files;
    }
}
