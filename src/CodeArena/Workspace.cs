using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace CodeArena;

/// <summary>A tool's input the model got wrong, or a refusal: said back to the model as the tool's result.</summary>
internal sealed class ToolError(string message) : Exception(message);

/// <summary>
/// The directories the tools may touch: the working directory, and those allowed
/// besides it (--add-dir, allowedPaths). A link inside that leads outside counts
/// as outside.
/// </summary>
internal sealed class Workspace
{
    private static readonly StringComparison Compare = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public Workspace(string root, IEnumerable<string>? allowed = null)
    {
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        Roots = [Root, .. (allowed ?? []).Select(a => Path.TrimEndingDirectorySeparator(Path.GetFullPath(Home(a), Root))).Distinct()];
    }

    public string Root { get; }
    public IReadOnlyList<string> Roots { get; }

    /// <summary>The full path of a path the model gave (relative to the working directory), if it is allowed.</summary>
    public string Resolve(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Root;
        }
        var full = Path.GetFullPath(Home(path.Trim()), Root);
        if (!Inside(full) || LeadsOutside(full))
        {
            var others = Roots.Count > 1 ? " or " + string.Join(", ", Roots.Skip(1)) : "";
            throw new ToolError($"{path} is outside the working directory ({Root}{others}). The person can allow more with --add-dir.");
        }
        return full;
    }

    public bool Inside(string full) => Roots.Any(r => Within(full, r));

    private static bool Within(string full, string root) =>
        full.Equals(root, Compare) || full.StartsWith(root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar, Compare);

    /// <summary>Whether a link on the way from its root to the path points outside every root.</summary>
    private bool LeadsOutside(string full)
    {
        var root = Roots.FirstOrDefault(r => Within(full, r));
        if (root is null)
        {
            return true;
        }
        var current = root;
        foreach (var part in Path.GetRelativePath(root, full).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".")
            {
                continue;
            }
            current = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (!info.Exists || info.LinkTarget is null)
            {
                continue;
            }
            var target = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
            if (target is not null && !Inside(Path.GetFullPath(target)))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>A path as the person reads it: relative to the working directory when inside it, with / between parts.</summary>
    public string Show(string full)
    {
        if (!Within(full, Root))
        {
            return full;
        }
        var relative = Path.GetRelativePath(Root, full);
        return relative == "." ? "." : relative.Replace('\\', '/');
    }

    private static string Home(string path) =>
        path == "~" || path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.Length > 2 ? path[2..] : "")
            : path;
}

/// <summary>Finding files: the ones git sees (so .gitignore holds), or a walk that skips the usual build and tool folders.</summary>
internal static class Files
{
    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".hg", ".svn", "node_modules", "bin", "obj", ".vs", ".idea", ".venv", "venv", "__pycache__",
        ".mypy_cache", ".pytest_cache", ".gradle", "target", "dist", ".next", ".cache", ".terraform",
    };

    /// <summary>Every file under a folder, as full paths.</summary>
    public static IEnumerable<string> Under(string dir, CancellationToken ct)
    {
        if (GitListed(dir, ct) is { } listed)
        {
            return listed;
        }
        return Walk(dir, ct);
    }

    private static List<string>? GitListed(string dir, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = dir,
                StandardOutputEncoding = Encoding.UTF8,
            };
            foreach (var arg in new[] { "-c", "core.quotepath=off", "ls-files", "-z", "--cached", "--others", "--exclude-standard" })
            {
                psi.ArgumentList.Add(arg);
            }
            using var git = Process.Start(psi);
            if (git is null)
            {
                return null;
            }
            var output = git.StandardOutput.ReadToEndAsync(ct);
            _ = git.StandardError.ReadToEndAsync(ct);
            if (!git.WaitForExit(15_000))
            {
                git.Kill(entireProcessTree: true);
                return null;
            }
            if (git.ExitCode != 0)
            {
                return null;
            }
            return [.. output.Result.Split('\0', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => Path.GetFullPath(p, dir))
                .Distinct()
                .Where(File.Exists)];
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null; // no git here
        }
    }

    private static IEnumerable<string> Walk(string dir, CancellationToken ct)
    {
        var pending = new Stack<string>();
        pending.Push(dir);
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var current = pending.Pop();
            IEnumerable<string> files, dirs;
            try
            {
                files = Directory.EnumerateFiles(current).ToList();
                dirs = Directory.EnumerateDirectories(current).ToList();
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException)
            {
                continue;
            }
            foreach (var file in files)
            {
                yield return file;
            }
            foreach (var sub in dirs.OrderDescending(StringComparer.Ordinal))
            {
                var info = new DirectoryInfo(sub);
                if (!Skipped.Contains(info.Name) && info.LinkTarget is null)
                {
                    pending.Push(sub);
                }
            }
        }
    }

    /// <summary>Whether the start of a file holds a NUL byte: binary, not text to read or search.</summary>
    public static bool LooksBinary(string file)
    {
        try
        {
            using var stream = File.OpenRead(file);
            Span<byte> head = stackalloc byte[8000];
            var n = stream.Read(head);
            return head[..n].Contains((byte)0);
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// A glob as a regex over paths with /: ** crosses folders, * and ? stay
    /// within one, {a,b} and [abc] as usual. A pattern without / matches file
    /// names at any depth.
    /// </summary>
    public static Regex Glob(string pattern)
    {
        var p = pattern.Replace('\\', '/').TrimStart('/');
        if (p.StartsWith("./", StringComparison.Ordinal))
        {
            p = p[2..];
        }
        if (!p.Contains('/'))
        {
            p = "**/" + p;
        }
        var sb = new StringBuilder("^");
        var braces = 0;
        for (var i = 0; i < p.Length; i++)
        {
            var c = p[i];
            switch (c)
            {
                case '*' when i + 1 < p.Length && p[i + 1] == '*':
                    var slash = i + 2 < p.Length && p[i + 2] == '/';
                    sb.Append(slash ? "(?:.*/)?" : ".*");
                    i += slash ? 2 : 1;
                    break;
                case '*':
                    sb.Append("[^/]*");
                    break;
                case '?':
                    sb.Append("[^/]");
                    break;
                case '{':
                    braces++;
                    sb.Append("(?:");
                    break;
                case '}' when braces > 0:
                    braces--;
                    sb.Append(')');
                    break;
                case ',' when braces > 0:
                    sb.Append('|');
                    break;
                case '[':
                    var end = p.IndexOf(']', i + 1);
                    if (end < 0)
                    {
                        sb.Append(@"\[");
                        break;
                    }
                    var set = p[(i + 1)..end];
                    sb.Append('[').Append(set.StartsWith('!') ? "^" + set[1..] : set).Append(']');
                    i = end;
                    break;
                default:
                    sb.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }
        sb.Append('$');
        var options = RegexOptions.CultureInvariant | (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? RegexOptions.IgnoreCase : RegexOptions.None);
        return new Regex(sb.ToString(), options, TimeSpan.FromSeconds(1));
    }

    /// <summary>A file's text, and whether it began with a byte-order mark (kept when it is written back).</summary>
    public static (string Text, bool Bom) ReadText(string file)
    {
        var bytes = File.ReadAllBytes(file);
        var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        return (Encoding.UTF8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0)), bom);
    }

    public static void WriteText(string file, string text, bool bom)
    {
        var dir = Path.GetDirectoryName(file);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        File.WriteAllText(file, text, new UTF8Encoding(bom));
    }

    public static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / 1024.0 / 1024.0:0.#} MB",
    };
}
