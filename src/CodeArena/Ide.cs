using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeArena;

/// <summary>A request of the IDE's refused: its HTTP status, a code for the page, and a message for the person.</summary>
internal sealed class IdeError(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

/// <summary>What the search panel asks for: the text or pattern, and where to look.</summary>
internal sealed record SearchQuery(string Text, bool Regex = false, bool Case = false, bool Word = false, string? Include = null, string? Exclude = null);

/// <summary>
/// The IDE's files: the working directory's folders listed, files read and
/// written, made, renamed, deleted and searched. Every path goes through the
/// workspace's rules, as the agent's tools do: inside the working directory (or
/// a folder added with --add-dir), and a link inside that leads outside counts
/// as outside.
/// </summary>
internal sealed class IdeFiles(Workspace workspace)
{
    /// <summary>A file larger than this opens as "too large" rather than in the editor.</summary>
    public const int MaxTextBytes = 5 * 1024 * 1024;
    public const int MaxMatches = 2000;
    private const int MaxSearchBytes = 2 * 1024 * 1024;
    private const int MaxListed = 50_000;

    // What VS Code hides by default (files.exclude).
    private static readonly HashSet<string> Hidden = new(StringComparer.OrdinalIgnoreCase) { ".git", ".hg", ".svn", "CVS", ".DS_Store", "Thumbs.db" };
    private static readonly UTF8Encoding Strict = new(false, throwOnInvalidBytes: true);
    private static readonly StringComparison PathCompare = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public Workspace Workspace => workspace;

    /// <summary>
    /// The full path of a path the page gave: relative to the working directory
    /// (empty: the folder itself), with no .. in it, and not through a link that
    /// leads outside. An absolute path, a drive or .. is refused, even where it
    /// would end up inside: the page never sends one. ~ is a name like any other,
    /// and the name is taken exactly as given (`notes ` is not `notes`).
    /// </summary>
    public string Resolve(string? path)
    {
        if (string.IsNullOrEmpty(path) || path == ".")
        {
            return workspace.Root;
        }
        if (path.Contains('\0') || OperatingSystem.IsWindows() && path.Contains(':'))
        {
            throw new IdeError(400, "invalid", "That is not a file name.");
        }
        var parts = path.Split(Separators);
        if (Path.IsPathRooted(path) || parts.Contains(".."))
        {
            throw Outside(path);
        }
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path, workspace.Root));
        return workspace.Allowed(full) ? full : throw Outside(path);
    }

    // A .. between backslashes is refused everywhere, as Windows would read it.
    private static readonly char[] Separators = ['/', '\\'];

    private IdeError Outside(string path) => new(403, "outside", $"{path} is outside the folder Code Arena works in ({workspace.Root}).");

    /// <summary>A path the page gave, which must not be the working directory itself.</summary>
    private string ResolveEntry(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new IdeError(400, "invalid", "Give the path of a file or folder.");
        }
        var full = Resolve(path);
        if (workspace.Roots.Any(r => string.Equals(r, full, PathCompare)))
        {
            throw new IdeError(400, "invalid", "That is the working directory itself.");
        }
        return full;
    }

    public string Show(string full) => workspace.Show(full);

    /// <summary>A folder's entries: folders first, then files, by name. Links that lead outside are left out.</summary>
    public JsonObject List(string? path)
    {
        var dir = Resolve(path);
        if (!Directory.Exists(dir))
        {
            throw new IdeError(404, "not_found", $"There is no folder {Show(dir)}.");
        }
        var entries = new List<(bool Dir, string Name, JsonObject Json)>();
        foreach (var info in new DirectoryInfo(dir).EnumerateFileSystemInfos())
        {
            // What is not a link, in a folder that leads inside, is inside; a link is followed to see.
            var full = Path.TrimEndingDirectorySeparator(info.FullName);
            if (Hidden.Contains(info.Name) || info.LinkTarget is not null && !workspace.Allowed(full))
            {
                continue;
            }
            var isDir = info is DirectoryInfo;
            var entry = new JsonObject { ["name"] = info.Name, ["path"] = Show(full), ["kind"] = isDir ? "dir" : "file" };
            if (info is FileInfo file)
            {
                entry["size"] = file.Length;
            }
            if (info.LinkTarget is not null)
            {
                entry["link"] = true;
            }
            entries.Add((isDir, info.Name, entry));
        }
        var ordered = entries.OrderByDescending(e => e.Dir).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Name, StringComparer.Ordinal);
        return new JsonObject { ["path"] = Show(dir), ["entries"] = new JsonArray([.. ordered.Select(e => (JsonNode)e.Json)]) };
    }

    /// <summary>Every file of the working directory (as git sees them, so .gitignore holds), for quick open. Links that lead out are left out.</summary>
    public JsonObject All(CancellationToken ct)
    {
        var files = new List<string>();
        var truncated = false;
        foreach (var full in workspace.FilesUnder(workspace.Root, ct))
        {
            if (files.Count == MaxListed)
            {
                truncated = true;
                break;
            }
            files.Add(Show(full));
        }
        files.Sort(StringComparer.Ordinal);
        return new JsonObject { ["files"] = new JsonArray([.. files.Select(f => (JsonNode)f)]), ["truncated"] = truncated };
    }

    /// <summary>"ticks-length" of the last write: the page sends it back with a save, so a file changed meanwhile is not overwritten unseen.</summary>
    public static string Version(string full)
    {
        var info = new FileInfo(full);
        return info.Exists ? $"{info.LastWriteTimeUtc.Ticks:x}-{info.Length:x}" : "none";
    }

    /// <summary>A file's text, or why it cannot be edited here (binary, not UTF-8, too large).</summary>
    public JsonObject Read(string? path)
    {
        var full = ResolveEntry(path);
        if (Directory.Exists(full))
        {
            throw new IdeError(400, "folder", $"{Show(full)} is a folder.");
        }
        var info = new FileInfo(full);
        if (!info.Exists)
        {
            throw new IdeError(404, "not_found", $"There is no file {Show(full)}.");
        }
        if (!Files.Regular(full))
        {
            // Opening a named pipe waits for a writer: the request would never end.
            throw new IdeError(400, "special", $"{Show(full)} is not a regular file (a named pipe, a socket or a device): it does not open here.");
        }
        var result = new JsonObject { ["path"] = Show(full), ["size"] = info.Length, ["version"] = Version(full), ["text"] = null };
        if (info.Length > MaxTextBytes)
        {
            result["tooLarge"] = true;
            return result;
        }
        var bytes = File.ReadAllBytes(full);
        var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var body = bytes.AsSpan(bom ? 3 : 0);
        if (body.IndexOf((byte)0) >= 0)
        {
            result["binary"] = true;
            return result;
        }
        try
        {
            result["text"] = Strict.GetString(body);
        }
        catch (DecoderFallbackException)
        {
            result["binary"] = true;
        }
        return result;
    }

    /// <summary>Saves a file (its byte-order mark kept), unless it changed on disk since the page read it at <paramref name="version"/>.</summary>
    public JsonObject Write(string? path, string text, string? version)
    {
        var full = ResolveEntry(path);
        if (Directory.Exists(full))
        {
            throw new IdeError(400, "folder", $"{Show(full)} is a folder.");
        }
        var exists = File.Exists(full);
        if (version is not null && exists && Version(full) != version)
        {
            throw new IdeError(409, "changed", $"{Show(full)} changed on disk since it was opened. Reload it, or save again to overwrite it.");
        }
        var bom = exists && Files.ReadText(full).Bom;
        Files.WriteText(full, text, bom);
        return new JsonObject { ["path"] = Show(full), ["version"] = Version(full), ["size"] = new FileInfo(full).Length };
    }

    /// <summary>A new, empty file or folder; refused when something is there already.</summary>
    public JsonObject Create(string? path, bool folder)
    {
        var full = ResolveEntry(path);
        if (File.Exists(full) || Directory.Exists(full))
        {
            throw new IdeError(409, "exists", $"{Show(full)} exists already.");
        }
        if (folder)
        {
            Directory.CreateDirectory(full);
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            using (File.Create(full))
            {
            }
        }
        return new JsonObject { ["path"] = Show(full), ["kind"] = folder ? "dir" : "file" };
    }

    /// <summary>Moves a file or folder; refused onto something that exists (a change of case alone is allowed).</summary>
    public (string From, string To) Rename(string? from, string? to)
    {
        var source = ResolveEntry(from);
        var target = ResolveEntry(to);
        var isDir = Directory.Exists(source);
        if (!isDir && !File.Exists(source))
        {
            throw new IdeError(404, "not_found", $"There is no {Show(source)}.");
        }
        var sameEntry = string.Equals(source, target, StringComparison.OrdinalIgnoreCase);
        if (!sameEntry && (File.Exists(target) || Directory.Exists(target)))
        {
            throw new IdeError(409, "exists", $"{Show(target)} exists already.");
        }
        if (isDir && target.StartsWith(source + Path.DirectorySeparatorChar, PathCompare))
        {
            throw new IdeError(400, "invalid", "A folder cannot move into itself.");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (isDir)
        {
            Directory.Move(source, target);
        }
        else
        {
            File.Move(source, target);
        }
        return (source, target);
    }

    /// <summary>Deletes a file, or a folder with everything in it.</summary>
    public string Delete(string? path)
    {
        var full = ResolveEntry(path);
        if (Directory.Exists(full) && new DirectoryInfo(full).LinkTarget is null)
        {
            Directory.Delete(full, recursive: true);
        }
        else if (File.Exists(full) || Directory.Exists(full))
        {
            // A link: the link goes, not what it points at.
            File.Delete(full);
        }
        else
        {
            throw new IdeError(404, "not_found", $"There is no {Show(full)}.");
        }
        return full;
    }

    /// <summary>
    /// Text search across the working directory's files (git's list, so
    /// .gitignore holds), binary and large files and links that lead out skipped:
    /// per file, each match's line, column, length and a preview of its line.
    /// </summary>
    public JsonObject Search(SearchQuery q, CancellationToken ct)
    {
        var pattern = Pattern(q);
        var include = Globs(q.Include);
        var exclude = Globs(q.Exclude);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(20));
        var results = new JsonArray();
        var count = 0;
        var truncated = false;
        try
        {
            foreach (var full in workspace.FilesUnder(workspace.Root, limit.Token).OrderBy(f => f, StringComparer.Ordinal))
            {
                limit.Token.ThrowIfCancellationRequested();
                var relative = Show(full);
                if ((include.Count > 0 && !include.Any(g => Matches(g, relative))) || exclude.Any(g => Matches(g, relative)))
                {
                    continue;
                }
                var info = new FileInfo(full);
                if (!info.Exists || info.Length > MaxSearchBytes || Files.LooksBinary(full))
                {
                    continue;
                }
                string text;
                try
                {
                    text = Files.ReadText(full).Text;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    continue;
                }
                var matches = new JsonArray();
                var lines = text.Split('\n');
                for (var i = 0; i < lines.Length && !truncated; i++)
                {
                    var line = lines[i].TrimEnd('\r');
                    foreach (Match m in pattern.Matches(line))
                    {
                        if (m.Length == 0)
                        {
                            continue;
                        }
                        if (count == MaxMatches)
                        {
                            truncated = true;
                            break;
                        }
                        count++;
                        // A long line is shown from a little before its match.
                        var cut = line.Length > 300 ? Math.Max(0, m.Index - 60) : 0;
                        var preview = line.Length > 300 ? line.Substring(cut, Math.Min(300, line.Length - cut)) : line;
                        matches.Add(new JsonObject
                        {
                            ["line"] = i + 1,
                            ["column"] = m.Index + 1,
                            ["length"] = m.Length,
                            ["preview"] = preview,
                            ["start"] = m.Index - cut,
                        });
                    }
                }
                if (matches.Count > 0)
                {
                    results.Add(new JsonObject { ["path"] = relative, ["matches"] = matches });
                }
                if (truncated)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            truncated = true;
        }
        catch (RegexMatchTimeoutException)
        {
            throw new IdeError(400, "invalid", "The pattern takes too long to match: make it simpler.");
        }
        return new JsonObject { ["files"] = results, ["count"] = count, ["truncated"] = truncated };
    }

    /// <summary>What the search panel asks for as a pattern: the text (escaped, unless a regular expression), as a whole word, with case or not.</summary>
    private static Regex Pattern(SearchQuery q)
    {
        if (string.IsNullOrEmpty(q.Text))
        {
            throw new IdeError(400, "invalid", "Write what to search for.");
        }
        try
        {
            var source = q.Regex ? q.Text : Regex.Escape(q.Text);
            if (q.Word)
            {
                source = $@"\b(?:{source})\b";
            }
            return new Regex(source, RegexOptions.CultureInvariant | (q.Case ? RegexOptions.None : RegexOptions.IgnoreCase), TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException e)
        {
            throw new IdeError(400, "invalid", $"That is not a regular expression: {e.Message}");
        }
    }

    /// <summary>
    /// Replaces what the search finds, line by line as it finds it, in the files it finds it in (only <paramref name="paths"/>
    /// when given): $1 and the like stand for a regular expression's groups; a plain text's replacement is taken as written.
    /// The files keep their line endings and byte order mark. What changed, per file.
    /// </summary>
    public JsonObject Replace(SearchQuery q, string replacement, IReadOnlyCollection<string>? paths, CancellationToken ct)
    {
        var pattern = Pattern(q);
        var with = q.Regex ? replacement : replacement.Replace("$", "$$", StringComparison.Ordinal);
        var found = Search(q, ct);
        var changed = new JsonArray();
        var total = 0;
        foreach (var file in (found["files"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var relative = file.Str("path")!;
            if (paths is not null && !paths.Contains(relative))
            {
                continue;
            }
            var full = Resolve(relative);
            var (text, bom) = Files.ReadText(full);
            var count = 0;
            var lines = text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var cr = lines[i].EndsWith('\r');
                var line = cr ? lines[i][..^1] : lines[i];
                var after = pattern.Replace(line, m =>
                {
                    if (m.Length == 0)
                    {
                        return m.Value;
                    }
                    count++;
                    return m.Result(with);
                });
                lines[i] = cr ? after + "\r" : after;
            }
            if (count == 0)
            {
                continue;
            }
            Files.WriteText(full, string.Join('\n', lines), bom);
            total += count;
            changed.Add(new JsonObject { ["path"] = relative, ["replaced"] = count, ["version"] = Version(full) });
        }
        return new JsonObject { ["files"] = changed, ["count"] = total, ["truncated"] = found.Bool("truncated") == true };
    }

    /// <summary>"src/**/*.ts, docs": globs, a name alone matching at any depth, a folder matching what is in it. One not valid is refused (400).</summary>
    private static List<Regex> Globs(string? list)
    {
        try
        {
            return [.. (list ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).SelectMany(g => new[] { Files.Glob(g), Files.Glob(g.TrimEnd('/') + "/**") })];
        }
        catch (ToolError e)
        {
            throw new IdeError(400, "invalid", e.Message);
        }
    }

    private static bool Matches(Regex glob, string path)
    {
        try
        {
            return glob.IsMatch(path);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}

/// <summary>
/// The files the agent changed in this run, each with its text from before the
/// agent first changed it (null: the agent made the file): what the IDE marks,
/// diffs, and accepts or reverts. Accepting forgets the old text; reverting
/// writes it back (or deletes a file the agent made).
/// </summary>
internal sealed class AgentChanges(Workspace workspace)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, string?> _before = new(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <summary>An edit or a write by the agent. A file changed back to how it was is no longer a change.</summary>
    public void Record(FileChange change)
    {
        var full = Path.GetFullPath(change.Path, workspace.Root);
        lock (_gate)
        {
            if (!_before.TryGetValue(full, out var before))
            {
                _before[full] = before = change.Before;
            }
            if (before is not null && before == change.After)
            {
                _before.Remove(full);
            }
        }
    }

    public bool Has(string full)
    {
        lock (_gate)
        {
            return _before.ContainsKey(full);
        }
    }

    /// <summary>
    /// The changed files in the working directory (the IDE's files; a folder
    /// added with --add-dir is not among them): path, whether the agent made it or
    /// it is gone now, and the lines added and removed.
    /// </summary>
    public JsonArray List()
    {
        var list = new JsonArray();
        foreach (var (full, before) in Snapshot().Where(c => workspace.InRoot(c.Key)).OrderBy(c => c.Key, StringComparer.Ordinal))
        {
            var now = Current(full);
            if (now == before)
            {
                continue;
            }
            var (added, removed) = before is null ? (Lines(now), 0) : now is null ? (0, Lines(before)) : Diff.Count(before, now);
            list.Add(new JsonObject
            {
                ["path"] = workspace.Show(full),
                ["created"] = before is null,
                ["deleted"] = now is null,
                ["added"] = added,
                ["removed"] = removed,
            });
        }
        return list;
    }

    /// <summary>The text before the agent and the text now, for the diff editor.</summary>
    public JsonObject Texts(string full)
    {
        string? before;
        lock (_gate)
        {
            if (!_before.TryGetValue(full, out before))
            {
                throw new IdeError(404, "not_found", $"The agent has not changed {workspace.Show(full)} in this run (or it was accepted).");
            }
        }
        return new JsonObject { ["path"] = workspace.Show(full), ["original"] = before, ["modified"] = Current(full), ["version"] = IdeFiles.Version(full) };
    }

    /// <summary>Keeps the agent's change: it is no longer offered to revert. All of them when no file is given.</summary>
    public void Accept(string? full)
    {
        lock (_gate)
        {
            if (full is null)
            {
                _before.Clear();
            }
            else if (!_before.Remove(full))
            {
                throw new IdeError(404, "not_found", $"The agent has not changed {workspace.Show(full)} in this run (or it was accepted).");
            }
        }
    }

    /// <summary>Puts the file back as it was before the agent: its old text, or no file when the agent made it.</summary>
    public void Revert(string full)
    {
        string? before;
        lock (_gate)
        {
            if (!_before.TryGetValue(full, out before))
            {
                throw new IdeError(404, "not_found", $"The agent has not changed {workspace.Show(full)} in this run (or it was accepted).");
            }
            if (before is null)
            {
                if (File.Exists(full))
                {
                    File.Delete(full);
                }
            }
            else
            {
                Files.WriteText(full, before, File.Exists(full) && Files.ReadText(full).Bom);
            }
            _before.Remove(full);
        }
    }

    /// <summary>A file or folder the person moved: its changes move with it.</summary>
    public void Moved(string from, string to)
    {
        lock (_gate)
        {
            foreach (var (full, before) in _before.ToList())
            {
                var under = full.StartsWith(from + Path.DirectorySeparatorChar, StringComparison.Ordinal);
                if (full == from || under)
                {
                    _before.Remove(full);
                    _before[to + full[from.Length..]] = before;
                }
            }
        }
    }

    private List<KeyValuePair<string, string?>> Snapshot()
    {
        lock (_gate)
        {
            return [.. _before];
        }
    }

    private static int Lines(string? text) => string.IsNullOrEmpty(text) ? 0 : Diff.Lines(text).Length;

    private static string? Current(string full)
    {
        try
        {
            return File.Exists(full) ? Files.ReadText(full).Text : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
