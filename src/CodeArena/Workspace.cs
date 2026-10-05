using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace CodeArena;

/// <summary>A tool's input the model got wrong, or a refusal: said back to the model as the tool's result.</summary>
internal sealed class ToolError(string message) : Exception(message);

/// <summary>
/// The directories the tools may touch: the working directory, and those allowed
/// besides it (--add-dir, allowedPaths). A link inside that leads outside counts
/// as outside, where it leads found as the system finds it, link by link.
/// </summary>
internal sealed class Workspace
{
    private static readonly StringComparison Compare = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static readonly char[] Separators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];
    private const int MaxLinks = 40;

    /// <summary>Where the roots lead (one may be reached through a link, as /tmp is on macOS).</summary>
    private readonly string[] _physical;

    public Workspace(string root, IEnumerable<string>? allowed = null)
    {
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        Roots = [Root, .. (allowed ?? []).Select(a => Path.TrimEndingDirectorySeparator(Path.GetFullPath(Home(a), Root))).Distinct()];
        _physical = [.. Roots.Select(r => Physical(r) ?? r)];
    }

    public string Root { get; }
    public IReadOnlyList<string> Roots { get; }

    /// <summary>
    /// The full path of a path the model gave (relative to the working directory),
    /// if it is allowed. Spaces around it are dropped, unless something has that
    /// very name.
    /// </summary>
    public string Resolve(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Root;
        }
        var full = Full(path);
        if (path.Trim() != path && !Path.Exists(full))
        {
            full = Full(path.Trim());
        }
        if (!Allowed(full))
        {
            var others = Roots.Count > 1 ? " or " + string.Join(", ", Roots.Skip(1)) : "";
            throw new ToolError($"{path} is outside the working directory ({Root}{others}). The person can allow more with --add-dir.");
        }
        return full;
    }

    // Without a trailing separator: "./" is the folder itself, not something in it.
    private string Full(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(Home(path), Root));

    public bool Inside(string full) => Roots.Any(r => Within(full, r));

    /// <summary>Whether a full path is in the working directory itself (not a folder allowed besides it).</summary>
    public bool InRoot(string full) => Within(full, Root);

    /// <summary>
    /// Whether a full path may be used: inside a root by name, and inside one
    /// where it leads. A link to a file not there yet counts by where it points
    /// (writing through it would make the file there), and so does a link inside
    /// by name only, through a link of its own (inner → /etc, x → inner/passwd).
    /// A loop of links, or more than 40 on the way, counts as outside.
    /// </summary>
    public bool Allowed(string full) =>
        Inside(full) && Physical(full) is { } leads && _physical.Any(r => Within(leads, r));

    /// <summary>
    /// Every file under a folder that may be used, as full paths: git's list (so
    /// .gitignore holds), or a walk that skips the usual build and tool folders.
    /// Links that lead outside are left out, and so is what is in a folder reached
    /// through one. Each folder is looked at once per call.
    /// </summary>
    public IEnumerable<string> FilesUnder(string dir, CancellationToken ct)
    {
        var folders = new Dictionary<string, bool>(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        return Files.Under(dir, ct).Where(full =>
        {
            var parent = Path.GetDirectoryName(full) ?? Root;
            if (!folders.TryGetValue(parent, out var ok))
            {
                folders[parent] = ok = Allowed(parent);
            }
            // What is not a link, in a folder that leads inside, is inside.
            return ok && (new FileInfo(full).LinkTarget is null || Allowed(full));
        });
    }

    /// <summary>Whether a full path is the folder, or in it, by name.</summary>
    internal static bool Within(string full, string root) =>
        full.Equals(root, Compare) || full.StartsWith(root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar, Compare);

    /// <summary>
    /// Where a full path leads, found as the system finds it: one part at a time
    /// from the top, each link replaced by its target where it is met, and a ..
    /// taken from where the parts before it lead, not by name (with d → / and
    /// x → d/../etc/hostname, x is /etc/hostname, not hostname in the working
    /// directory). What is not there yet is taken as written. Null for a loop of
    /// links, or more than 40 of them. Windows applies a .. in a link's target by
    /// name, from where the link is: so is it here, there.
    /// </summary>
    internal static string? Physical(string full)
    {
        var current = Path.GetPathRoot(full) ?? "";
        var pending = new Stack<string>();
        Push(pending, full[current.Length..]);
        var links = 0;
        while (pending.TryPop(out var part))
        {
            if (part == ".")
            {
                continue;
            }
            if (part == "..")
            {
                current = Path.GetDirectoryName(current) ?? current;
                continue;
            }
            var next = Path.Join(current, part);
            string? target;
            try
            {
                // The link's own (lstat): null for a plain file or folder, and for nothing there.
                target = (Directory.Exists(next) ? (FileSystemInfo)new DirectoryInfo(next) : new FileInfo(next)).LinkTarget;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                target = null; // not to be looked into: the system cannot go through it either
            }
            if (target is null)
            {
                if (!Path.Exists(next))
                {
                    // Nothing there yet, so no link further on: the rest as written.
                    return Path.GetFullPath(string.Join(Path.DirectorySeparatorChar, [part, .. pending]), current);
                }
                current = next;
                continue;
            }
            if (++links > MaxLinks)
            {
                return null;
            }
            if (OperatingSystem.IsWindows())
            {
                target = Path.GetFullPath(target, current);
            }
            if (Path.IsPathRooted(target))
            {
                current = Path.GetPathRoot(Path.GetFullPath(target, current))!;
                target = target[(Path.GetPathRoot(target)?.Length ?? 0)..];
            }
            Push(pending, target);
        }
        return current;
    }

    private static void Push(Stack<string> pending, string path)
    {
        var parts = path.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        for (var i = parts.Length - 1; i >= 0; i--)
        {
            pending.Push(parts[i]);
        }
    }

    /// <summary>A path as the person reads it: relative to the working directory when inside it, with / between parts.</summary>
    public string Show(string full)
    {
        if (!Within(full, Root))
        {
            return full;
        }
        var relative = Path.GetRelativePath(Root, full);
        // \ separates parts on Windows only: elsewhere it is part of a name.
        return relative == "." ? "." : OperatingSystem.IsWindows() ? relative.Replace('\\', '/') : relative;
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
            // Reading the index runs core.fsmonitor unless told not to: Git.Command does.
            var psi = Git.Command(dir, ["ls-files", "-z", "--cached", "--others", "--exclude-standard"]);
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
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

    /// <summary>
    /// Whether a file is not text to read or search: the start of it holds a NUL
    /// byte, or it is not a regular file (a named pipe would keep the read
    /// waiting). One that cannot be read is left to the read, which says why.
    /// </summary>
    public static bool LooksBinary(string file)
    {
        if (!Regular(file))
        {
            return true;
        }
        try
        {
            using var stream = File.OpenRead(file);
            Span<byte> head = stackalloc byte[8000];
            var n = stream.Read(head);
            return head[..n].Contains((byte)0);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether a path is a file a read gets to the end of: not a named pipe, a
    /// socket or a terminal, where opening or reading waits for a writer, maybe
    /// for ever. It is opened without waiting and asked whether it can seek,
    /// which a pipe or a terminal cannot. One that cannot be opened (not there,
    /// not readable) is left to the read, which says why.
    /// </summary>
    public static bool Regular(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return true; // its named pipes are in \\.\pipe\, not among the files
        }
        var mac = OperatingSystem.IsMacOS();
        // O_RDONLY | O_NONBLOCK | O_NOCTTY | O_CLOEXEC
        var fd = Unix.Open(path, mac ? 0x4 | 0x20000 | 0x1000000 : 0x800 | 0x100 | 0x80000);
        if (fd < 0)
        {
            return Marshal.GetLastPInvokeError() != 6; // ENXIO: a socket
        }
        try
        {
            return Unix.Seek(fd, 0, 1) >= 0; // SEEK_CUR; ESPIPE for a pipe or a terminal
        }
        finally
        {
            Unix.Close(fd);
        }
    }

    private static class Unix
    {
        [DllImport("libc", EntryPoint = "open", SetLastError = true)]
        public static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

        [DllImport("libc", EntryPoint = "lseek", SetLastError = true)]
        public static extern long Seek(int fd, long offset, int whence);

        [DllImport("libc", EntryPoint = "close")]
        public static extern int Close(int fd);
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
        try
        {
            return new Regex(sb.ToString(), options, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException)
        {
            // [], [z-a], or a { not closed (src/{a,b while it is typed).
            throw new ToolError($"{pattern} is not a valid glob: a [set] or a {{group}} in it is empty, reversed or not closed.");
        }
    }

    /// <summary>A file's text, and whether it began with a byte-order mark (kept when it is written back). A named pipe or a device is refused, not waited on.</summary>
    public static (string Text, bool Bom) ReadText(string file)
    {
        if (!Regular(file))
        {
            throw new IOException($"{Path.GetFileName(file)} is not a regular file (a named pipe, a socket or a device): it is not read.");
        }
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

/// <summary>
/// git as Code Arena runs it to read, without asking: so that what a repository
/// says in its own settings starts no program. A .git/config or .gitattributes
/// that came in an archive, or that the agent wrote in auto-edit, is not the
/// person's say-so. So: no fsmonitor hook when the index is read; no hook of
/// .git/hooks (status and diff write the index back, which runs
/// post-index-change); no clean, smudge or process filter, which status, diff
/// and blame run over the files, and no merge driver, which log and show run
/// to re-merge a merge (--remerge-diff); when asked, the repository's own are
/// named and switched off; no signature checker; no submodule visited, whose
/// settings are its own; no bare repository found on the way up; and no way to
/// reach another machine (a partial clone fetches what it lacks, through ssh, a
/// credential helper or a remote helper). Text conversion and external diff
/// programs are the command's own options: the git tool turns them off.
/// </summary>
internal static class Git
{
    // A file: the hooks would be in it, where nothing can be. On Windows /dev/null is a folder any user may make, so this program's own file.
    private static readonly string NoHooks = OperatingSystem.IsWindows() ? Environment.ProcessPath ?? "NUL" : "/dev/null";

    private static readonly string[] Settings =
    [
        "core.fsmonitor=",
        "core.hooksPath=" + NoHooks,
        "core.quotepath=off",
        "diff.autoRefreshIndex=false",
        "diff.ignoreSubmodules=all",
        "diff.submodule=short",
        "status.submoduleSummary=false",
        "submodule.recurse=false",
        "log.showSignature=false",
        "gpg.program=",
        "gpg.openpgp.program=",
        "gpg.x509.program=",
        "gpg.ssh.program=",
        // A folder of the working directory that looks like a repository's insides (HEAD, objects, refs), from an archive: not a repository.
        "safe.bareRepository=explicit",
    ];

    /// <summary>
    /// git with these arguments in this folder, guarded as above;
    /// <paramref name="off"/>: settings that switch off what the repository's
    /// own name (Inspect).
    /// </summary>
    public static ProcessStartInfo Command(string dir, IEnumerable<string> args, IEnumerable<string>? off = null)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, UseShellExecute = false };
        foreach (var setting in Settings.Concat(off ?? []))
        {
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(setting);
        }
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }
        Guard(psi.Environment);
        return psi;
    }

    private static void Guard(IDictionary<string, string?> env)
    {
        // No protocol at all, whatever protocol.*.allow the repository says; and no fetching of what a partial clone lacks.
        env["GIT_ALLOW_PROTOCOL"] = "";
        env["GIT_NO_LAZY_FETCH"] = "1";
        // status leaves the index as it is.
        env["GIT_OPTIONAL_LOCKS"] = "0";
        env["GIT_TERMINAL_PROMPT"] = "0";
    }

    /// <summary>What the git tool knows of a folder's repository before it runs git there.</summary>
    /// <param name="Top">The folder holding its .git, which is its work tree.</param>
    /// <param name="Off">Settings that switch off every filter and merge driver its settings name.</param>
    /// <param name="IgnoreRevs">The files blame.ignoreRevsFile names, as full paths (git reads them from the top).</param>
    public sealed record Repository(string Top, List<string> Off, List<string> IgnoreRevs);

    /// <summary>
    /// The repository git reads from this folder, when it reads only that. Its
    /// work tree is the folder holding its .git (not where core.worktree says,
    /// which could put it around the person's home, an index naming
    /// ~/.ssh/id_rsa). Its own folder (.git, or where a .git file points, and
    /// its common folder) is in that folder, or is one git made for it
    /// elsewhere, which names it back: a worktree of another checkout
    /// (worktrees/NAME/gitdir there), or a submodule (its folder in the
    /// parent's .git, whose core.worktree is this one). A .git file, a
    /// commondir or core.worktree the repository's own files set could
    /// otherwise send git to another repository of the person's: a ToolError,
    /// as is no repository at all (git's own words). The person's GIT_DIR (or
    /// GIT_WORK_TREE) is theirs.
    /// </summary>
    public static Repository Inspect(string dir)
    {
        var top = SystemPrompt.GitRoot(dir);
        if (!new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR" }.Any(name => Environment.GetEnvironmentVariable(name) is { Length: > 0 }))
        {
            var (code, output, error) = Run(dir, ["rev-parse", "--show-toplevel", "--absolute-git-dir", "--git-common-dir"]);
            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (code != 0 || lines.Length != 3)
            {
                throw new ToolError($"git: {(error.Trim() is { Length: > 0 } said ? said : "this is not a repository with a work tree.")}");
            }
            var work = Real(lines[0]);
            var gitDir = Real(lines[1]);
            var common = Real(Path.GetFullPath(lines[2], dir));
            if (top is null || !Same(work, Real(top)))
            {
                throw new ToolError($"The repository's settings put its work tree at {lines[0]}, not {top ?? "the folder that holds its .git"} (core.worktree): git does not read it here.");
            }
            var own = Workspace.Within(gitDir, work) && Workspace.Within(common, work)
                // A worktree of another checkout: worktrees/NAME in that checkout's .git, whose gitdir file names this .git.
                || (Same(Path.GetDirectoryName(gitDir) ?? "", Path.Join(common, "worktrees"))
                    && ReadLine(Path.Join(gitDir, "gitdir")) is { } back && Same(Real(Path.GetFullPath(back, gitDir)), Real(Path.Join(top, ".git"))))
                // A submodule: its folder in the parent's .git, whose own settings make this folder its work tree.
                || (Same(gitDir, common)
                    && Run(dir, ["config", "--file", Path.Join(gitDir, "config"), "--get", "core.worktree"]) is (0, var worktree, _)
                    && Same(Real(Path.GetFullPath(worktree.Trim(), gitDir)), work));
            if (!own)
            {
                throw new ToolError($"This repository's .git leads to {lines[1]}, the folder of another repository: git does not read it here.");
            }
        }
        var (off, ignoreRevs) = Named(dir, top ?? dir);
        return new Repository(top ?? dir, off, ignoreRevs);
    }

    /// <summary>
    /// From the settings (git config reads them, and runs nothing): filter.NAME.clean,
    /// smudge and process, and merge.NAME.driver, emptied for every name there
    /// (an empty name too, which merge= or filter= in .gitattributes picks); and
    /// the files blame.ignoreRevsFile names.
    /// </summary>
    private static (List<string> Off, List<string> IgnoreRevs) Named(string dir, string top)
    {
        var (_, output, _) = Run(dir, ["config", "-z", "--get-regexp", @"^(filter|merge)\.|^blame\.ignorerevsfile$"]);
        var off = new List<string>();
        var ignoreRevs = new List<string>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        // KEY\nVALUE, or KEY alone for a bare true; exit code 1 and nothing: none.
        foreach (var entry in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var newline = entry.IndexOf('\n');
            var key = newline < 0 ? entry : entry[..newline];
            if (key == "blame.ignorerevsfile")
            {
                // A path: ~/ is the home folder, and anything else git would expand is taken as outside. An empty one names no file.
                var value = newline < 0 ? "" : entry[(newline + 1)..];
                if (value.Length == 0)
                {
                    continue;
                }
                ignoreRevs.Add(value.StartsWith("~/", StringComparison.Ordinal) ? Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), value[2..])
                    : value.StartsWith('~') || value.StartsWith("%(", StringComparison.Ordinal) ? Path.GetPathRoot(top) ?? "/"
                    : Path.GetFullPath(value, top));
                continue;
            }
            // filter.NAME.VARIABLE or merge.NAME.VARIABLE, where the name may hold dots, or be empty; merge.VARIABLE has none.
            var section = key.StartsWith("filter.", StringComparison.Ordinal) ? "filter." : "merge.";
            var dot = key.LastIndexOf('.');
            if (dot < section.Length || !names.Add(key[..dot]))
            {
                continue;
            }
            var name = key[section.Length..dot];
            if (name.Contains('=') || name.Contains('\n'))
            {
                throw new ToolError($"The repository's settings name a {section.TrimEnd('.')} that cannot be switched off from git's command line ({name}): git does not read it here.");
            }
            off.AddRange(section == "filter."
                ? [$"filter.{name}.clean=", $"filter.{name}.smudge=", $"filter.{name}.process=", $"filter.{name}.required=false"]
                : [$"merge.{name}.driver="]);
        }
        return (off, ignoreRevs);
    }

    /// <summary>A guarded git's exit code, output and errors; git that does not answer within 15 s is stopped.</summary>
    private static (int Code, string Output, string Error) Run(string dir, string[] args)
    {
        var psi = Command(dir, args);
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.StandardOutputEncoding = Encoding.UTF8;
        psi.StandardErrorEncoding = Encoding.UTF8;
        using var git = Process.Start(psi) ?? throw new IOException("git did not start.");
        var output = git.StandardOutput.ReadToEndAsync();
        var error = git.StandardError.ReadToEndAsync();
        if (!git.WaitForExit(15_000))
        {
            git.Kill(entireProcessTree: true);
            throw new IOException($"git {args[0]} did not answer within 15 s.");
        }
        return (git.ExitCode, output.Result, error.Result);
    }

    private static string? ReadLine(string file)
    {
        try
        {
            return File.ReadAllText(file).Trim() is { Length: > 0 } line ? line : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Real(string path) => Workspace.Physical(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))) ?? path;

    private static bool Same(string a, string b) => Workspace.Within(a, b) && Workspace.Within(b, a);
}
