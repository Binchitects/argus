using System.Text.Json.Nodes;

namespace CodeArena;

/// <summary>
/// What changes on disk in the working directory, by anyone: the person's own editor, a command, git. Each change is a
/// version; the IDE's page asks for the paths changed since the version it has (waiting while there are none), and
/// reloads what it shows of them: an open file not edited there, the Explorer's folders, Source Control and Changes.
/// What nobody reads there is left out (.git's insides, node_modules, the session's own .code-arena); .git's HEAD and
/// index say Source Control changed. Where the system will not watch (too many folders for inotify), the page reloads
/// when it comes back into view instead.
/// </summary>
internal sealed class DiskWatch : IDisposable
{
    /// <summary>Changes kept for pages behind: past this, the oldest go, and a page behind them reloads everything.</summary>
    private const int MostKept = 4000;

    /// <summary>A burst (a build, a checkout) is answered as one: the answer waits this long after its first change.</summary>
    private static readonly TimeSpan Gather = TimeSpan.FromMilliseconds(150);

    private static readonly HashSet<string> Unread = new(StringComparer.Ordinal) { "node_modules", ".code-arena" };

    private readonly string _root;
    private readonly FileSystemWatcher? _watcher;
    private readonly Lock _gate = new();
    private readonly List<(long Version, string Path, string Kind)> _changes = [];
    // The version being gathered: the closed one a page has starts at 1 (0 asks where to start).
    private long _version = 2;
    /// <summary>Changes of versions up to this one may be missing (dropped, or lost by the system): a page before it reloads all.</summary>
    private long _forgotten;
    private TaskCompletionSource _next = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public DiskWatch(string root)
    {
        _root = Path.GetFullPath(root);
        try
        {
            _watcher = new FileSystemWatcher(_root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
            };
            _watcher.Changed += (_, e) => Note(e.FullPath, "changed");
            _watcher.Created += (_, e) => Note(e.FullPath, "created");
            _watcher.Deleted += (_, e) => Note(e.FullPath, "deleted");
            _watcher.Renamed += (_, e) =>
            {
                Note(e.OldFullPath, "deleted");
                Note(e.FullPath, "created");
            };
            // The system's buffer overflowed: some changes are not known, so every page reloads all it shows.
            _watcher.Error += (_, _) => Lost();
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception e) when (e is IOException or ArgumentException or PlatformNotSupportedException or UnauthorizedAccessException)
        {
            _watcher?.Dispose();
            _watcher = null;
        }
    }

    /// <summary>Whether changes are watched (the page reloads on coming back into view either way).</summary>
    public bool Watching => _watcher is not null;

    /// <summary>The path as the page names files (relative, with /), "" for Source Control's own, or null when nobody reads it.</summary>
    internal static string? PathFor(string root, string full)
    {
        var rel = Path.GetRelativePath(root, full).Replace('\\', '/');
        if (rel == "." || rel.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(rel))
        {
            return null;
        }
        var parts = rel.Split('/');
        if (parts[0] == ".git")
        {
            // Only what moves Source Control: a commit, a checkout, a staged file.
            return parts.Length == 2 && parts[1] is "HEAD" or "index" ? "" : null;
        }
        return parts.Any(Unread.Contains) ? null : rel;
    }

    private void Note(string full, string kind)
    {
        if (PathFor(_root, full) is not { } path)
        {
            return;
        }
        TaskCompletionSource? wake = null;
        lock (_gate)
        {
            var first = _changes.Count == 0 || _changes[^1].Version < _version;
            _changes.Add((_version, path, kind));
            if (_changes.Count > MostKept)
            {
                var drop = _changes.Count - MostKept;
                _forgotten = Math.Max(_forgotten, _changes[drop - 1].Version);
                _changes.RemoveRange(0, drop);
            }
            if (first)
            {
                wake = _next;
            }
        }
        if (wake is not null)
        {
            // The burst gathers a moment, then this version is closed and the pages waiting are answered.
            _ = Task.Delay(Gather).ContinueWith(_ => Close(), TaskScheduler.Default);
        }
    }

    private void Lost()
    {
        lock (_gate)
        {
            // The version gathered now is the first whole one again.
            _forgotten = _version;
            _changes.Clear();
        }
        Close();
    }

    private void Close()
    {
        TaskCompletionSource done;
        lock (_gate)
        {
            done = _next;
            _version++;
            _next = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        done.TrySetResult();
    }

    /// <summary>
    /// The changes after version <paramref name="since"/>, waiting up to <paramref name="wait"/> while there are none (0: the
    /// version now, to start from). "all" when the page is behind what is kept: it reloads everything it shows.
    /// </summary>
    public async Task<JsonObject> SinceAsync(long since, TimeSpan wait, CancellationToken ct)
    {
        Task next;
        lock (_gate)
        {
            if (since <= 0 || since < _version - 1 || since < _forgotten)
            {
                return Answer(since);
            }
            next = _next.Task;
        }
        try
        {
            await next.WaitAsync(wait, ct);
        }
        catch (TimeoutException)
        {
            // Nothing changed meanwhile: the page asks again.
        }
        lock (_gate)
        {
            return Answer(since);
        }
    }

    private JsonObject Answer(long since)
    {
        // The version a page has next: the one being gathered now is answered once it closes.
        var closed = _version - 1;
        if (since <= 0)
        {
            return new JsonObject { ["version"] = closed, ["watching"] = Watching, ["changes"] = new JsonArray() };
        }
        if (since < _forgotten)
        {
            return new JsonObject { ["version"] = closed, ["watching"] = Watching, ["all"] = true, ["changes"] = new JsonArray() };
        }
        // One entry a path, its last kind: made then changed is made, changed then deleted is deleted.
        var last = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (version, path, kind) in _changes)
        {
            if (version > since && version <= closed)
            {
                last[path] = last.TryGetValue(path, out var was) && was == "created" && kind == "changed" ? "created" : kind;
            }
        }
        return new JsonObject
        {
            ["version"] = closed,
            ["watching"] = Watching,
            ["changes"] = new JsonArray([.. last.Select(p => (JsonNode)new JsonObject { ["path"] = p.Key, ["kind"] = p.Value })]),
        };
    }

    public void Dispose() => _watcher?.Dispose();
}
