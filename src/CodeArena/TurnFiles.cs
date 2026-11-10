using System.Text;

namespace CodeArena;

/// <summary>
/// What the agent changed in the folder by any means, not only its file tools: a command (a formatter, a checkout, an
/// install, sed), code it ran, a sub-agent, a tool of a server. At a turn's start each file's size and time are taken, and
/// what a file held then is known (git's last commit for a file it had as committed, a copy for one already changed or not
/// in git); a file that differs afterwards is the agent's change, from what it held at the turn's start. The files the person
/// saves in the IDE meanwhile are theirs, not the agent's.
/// </summary>
internal sealed class TurnFiles(Workspace workspace)
{
    /// <summary>A file larger than this is watched for its size and time only: its text before is not kept.</summary>
    public const int MostBytes = 1024 * 1024;
    /// <summary>The copies kept of files not as git has them (changed already, or not in git), at most this much in all.</summary>
    public const long MostKept = 64L * 1024 * 1024;
    /// <summary>Folders with more files than this are not watched (a turn's start would take too long).</summary>
    public const int MostFiles = 50_000;

    private readonly object _gate = new();
    private Dictionary<string, (long Size, DateTime Written)> _seen = new(StringComparer.Ordinal);
    // What a file held at the turn's start, where it is known: its text (null: not there then).
    private readonly Dictionary<string, string?> _before = new(StringComparer.Ordinal);
    // Files git has as committed at the turn's start: their text then is git's (read only when they change).
    private HashSet<string> _committed = new(StringComparer.Ordinal);
    private readonly HashSet<string> _persons = new(StringComparer.Ordinal);
    private string? _gitRoot;
    // The commit the turn began on: a commit during it (the agent's, or the turn's own) does not change what was before.
    private string? _head;
    private bool _on;

    /// <summary>A turn begins: what each file is now (the earlier turn's watch ends).</summary>
    public void Begin(CancellationToken ct)
    {
        var seen = new Dictionary<string, (long, DateTime)>(StringComparer.Ordinal);
        var files = new List<string>();
        foreach (var f in Files.Under(workspace.Root, ct))
        {
            files.Add(f);
            if (files.Count > MostFiles)
            {
                lock (_gate)
                {
                    _on = false;
                }
                return;
            }
        }
        foreach (var f in files)
        {
            if (Stat(f) is { } s)
            {
                seen[f] = s;
            }
        }
        var gitRoot = GitRoot(workspace.Root);
        var head = gitRoot is null ? null : Run(gitRoot, ["rev-parse", "--verify", "-q", "HEAD"], ct)?.Trim();
        // No commit yet: nothing is as git has it.
        var dirty = gitRoot is null || head is not { Length: > 0 } ? null : Dirty(gitRoot, ct);
        var committed = new HashSet<string>(StringComparer.Ordinal);
        var before = new Dictionary<string, string?>(StringComparer.Ordinal);
        long kept = 0;
        foreach (var (f, s) in seen)
        {
            if (dirty is not null && !dirty.Contains(f))
            {
                committed.Add(f);
            }
            else if (s.Item1 <= MostBytes && kept + s.Item1 <= MostKept && Text(f) is { } text)
            {
                before[f] = text;
                kept += s.Item1;
            }
        }
        lock (_gate)
        {
            (_seen, _committed, _gitRoot, _head, _on) = (seen, committed, gitRoot, head, true);
            _before.Clear();
            foreach (var (f, t) in before)
            {
                _before[f] = t;
            }
            _persons.Clear();
        }
    }

    /// <summary>The person saved, made, moved or deleted this file (or folder) in the IDE: its change is theirs.</summary>
    public void PersonTouched(string full)
    {
        lock (_gate)
        {
            _persons.Add(Path.TrimEndingDirectorySeparator(full));
        }
    }

    private bool Persons(string f) =>
        _persons.Contains(f) || _persons.Any(p => f.StartsWith(p + Path.DirectorySeparatorChar, StringComparison.Ordinal));

    /// <summary>
    /// The files changed since the turn's start (or the last look) that are not the person's: each with what it held at the
    /// turn's start (null: it was not there) and what it holds now (null: gone). Those whose text before is not known (too
    /// large, or past what is kept) are left out.
    /// </summary>
    public List<(string Full, string? Before, string? Now)> Changed(CancellationToken ct)
    {
        Dictionary<string, (long Size, DateTime Written)> seen;
        lock (_gate)
        {
            if (!_on)
            {
                return [];
            }
            seen = new(_seen, StringComparer.Ordinal);
        }
        var now = new Dictionary<string, (long, DateTime)>(StringComparer.Ordinal);
        foreach (var f in Files.Under(workspace.Root, ct).Take(MostFiles + 1))
        {
            if (Stat(f) is { } s)
            {
                now[f] = s;
            }
        }
        var changed = new List<string>();
        changed.AddRange(now.Where(n => !seen.TryGetValue(n.Key, out var was) || was != n.Value).Select(n => n.Key));
        // Gone: listed then, not now (and not there: a file git stopped listing is still a file).
        changed.AddRange(seen.Keys.Where(f => !now.ContainsKey(f) && !File.Exists(f)));
        var result = new List<(string, string?, string?)>();
        foreach (var f in changed)
        {
            string? before;
            bool known;
            lock (_gate)
            {
                if (Persons(f))
                {
                    continue;
                }
                known = _before.TryGetValue(f, out before);
                if (!known && !seen.ContainsKey(f))
                {
                    // New since the turn began.
                    (known, before) = (true, null);
                }
            }
            if (!known && _committed.Contains(f) && _gitRoot is { } root && _head is { Length: > 0 } head)
            {
                before = Committed(root, head, f, ct);
                known = before is not null;
            }
            if (!known)
            {
                continue;
            }
            var text = File.Exists(f) ? Text(f) : null;
            if (File.Exists(f) && text is null)
            {
                // Binary, or too large to diff now.
                continue;
            }
            result.Add((f, before, text));
        }
        lock (_gate)
        {
            foreach (var f in changed)
            {
                if (now.TryGetValue(f, out var s))
                {
                    _seen[f] = s;
                }
                else
                {
                    _seen.Remove(f);
                }
            }
        }
        return result;
    }

    private static (long, DateTime)? Stat(string f)
    {
        try
        {
            var info = new FileInfo(f);
            return info.Exists ? (info.Length, info.LastWriteTimeUtc) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>A file's text, when it is text (no NUL in its start) and not too large; else null.</summary>
    private static string? Text(string f)
    {
        try
        {
            var bytes = File.ReadAllBytes(f);
            if (bytes.Length > MostBytes || bytes.AsSpan(0, Math.Min(bytes.Length, 8000)).Contains((byte)0))
            {
                return null;
            }
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? GitRoot(string dir) =>
        Run(dir, ["rev-parse", "--show-toplevel"], CancellationToken.None) is { } top && top.Trim() is { Length: > 0 } t ? Path.GetFullPath(t) : null;

    /// <summary>The files git has as changed, staged or not yet added (full paths); null: git did not say.</summary>
    private static HashSet<string>? Dirty(string root, CancellationToken ct)
    {
        if (Run(root, ["status", "--porcelain=v1", "-z", "--untracked-files=all", "--ignored=no"], ct) is not { } said)
        {
            return null;
        }
        var dirty = new HashSet<string>(StringComparer.Ordinal);
        var entries = said.Split('\0');
        for (var i = 0; i < entries.Length; i++)
        {
            var e = entries[i];
            if (e.Length < 4)
            {
                continue;
            }
            dirty.Add(Path.GetFullPath(e[3..], root));
            // A rename names its source next: both are changed.
            if (e[0] is 'R' or 'C' && i + 1 < entries.Length)
            {
                dirty.Add(Path.GetFullPath(entries[++i], root));
            }
        }
        return dirty;
    }

    /// <summary>A file as a commit has it (null: not in it, or git did not say).</summary>
    private static string? Committed(string root, string commit, string full, CancellationToken ct) =>
        Run(root, ["show", commit + ":" + Path.GetRelativePath(root, full).Replace('\\', '/')], ct);

    private static string? Run(string dir, string[] args, CancellationToken ct)
    {
        try
        {
            var psi = Git.Command(dir, args);
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            using var git = System.Diagnostics.Process.Start(psi);
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
            return git.ExitCode == 0 ? output.Result : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }
}
