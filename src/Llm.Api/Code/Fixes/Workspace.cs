using System.Formats.Tar;
using System.IO.Compression;
using System.Text;

namespace Llm.Api.Code.Fixes;

/// <summary>Why a file cannot be read or changed as asked, in words for the model.</summary>
public sealed class WorkspaceException(string message) : Exception(message);

/// <summary>
/// A branch's files in memory, as GitLab packed them, and what the agent changed: read, searched, replaced, written; the
/// change as a unified diff; the whole as a tar for the sandbox. Binary and very large files are there but not to edit.
/// </summary>
public sealed class Workspace
{
    public const int MostFiles = 30_000;
    public const int MostTextBytes = 1024 * 1024;

    private readonly Dictionary<string, byte[]> _original = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _changed = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, string?> Changes => _changed;

    public IEnumerable<string> Paths => _original.Keys.Concat(_changed.Keys).Distinct(StringComparer.Ordinal).Where(p => !_changed.TryGetValue(p, out var c) || c is not null).Order(StringComparer.Ordinal);

    /// <summary>The files of GitLab's archive (tar.gz), its top folder (project-sha/) taken off.</summary>
    public static Workspace FromArchive(byte[] tarGz)
    {
        var w = new Workspace();
        using var gz = new GZipStream(new MemoryStream(tarGz), CompressionMode.Decompress);
        using var tar = new TarReader(gz);
        long total = 0;
        while (tar.GetNextEntry() is { } entry)
        {
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || entry.DataStream is null)
            {
                continue;
            }
            var slash = entry.Name.IndexOf('/', StringComparison.Ordinal);
            var path = slash >= 0 ? entry.Name[(slash + 1)..] : entry.Name;
            if (path.Length == 0 || path.Contains("..", StringComparison.Ordinal))
            {
                continue;
            }
            total += entry.Length;
            if (w._original.Count >= MostFiles || total > 500L * 1024 * 1024)
            {
                throw new WorkspaceException("The branch has too many files to fix here.");
            }
            using var m = new MemoryStream();
            entry.DataStream.CopyTo(m);
            w._original[path] = m.ToArray();
        }
        return w;
    }

    /// <summary>Files given as they are (tests).</summary>
    public static Workspace FromFiles(IReadOnlyDictionary<string, string> files)
    {
        var w = new Workspace();
        foreach (var (path, text) in files)
        {
            w._original[path] = Encoding.UTF8.GetBytes(text);
        }
        return w;
    }

    private static bool Binary(byte[] b) => b.AsSpan(0, Math.Min(b.Length, 8000)).Contains((byte)0);

    public static string Clean(string path) => path.Replace('\\', '/').TrimStart('/').Trim();

    public bool Exists(string path) => _changed.TryGetValue(path, out var c) ? c is not null : _original.ContainsKey(path);

    /// <summary>A file's text as it is now; why not, when it is not there or not text.</summary>
    public string Read(string path)
    {
        path = Clean(path);
        if (_changed.TryGetValue(path, out var changed))
        {
            return changed ?? throw new WorkspaceException($"{path} was deleted.");
        }
        if (!_original.TryGetValue(path, out var bytes))
        {
            throw new WorkspaceException($"There is no file {path}. list_files shows what there is.");
        }
        if (Binary(bytes) || bytes.Length > MostTextBytes)
        {
            throw new WorkspaceException($"{path} is not a text file to read (binary, or over 1 MB).");
        }
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>The original text of a file, or null when it is new or not text.</summary>
    public string? Original(string path) =>
        _original.TryGetValue(path, out var b) && !Binary(b) && b.Length <= MostTextBytes ? Encoding.UTF8.GetString(b) : null;

    public void Write(string path, string text)
    {
        path = Clean(path);
        if (path.Length == 0 || path.Contains("..", StringComparison.Ordinal) || path.StartsWith(".git/", StringComparison.Ordinal))
        {
            throw new WorkspaceException("That is not a path in the repository.");
        }
        if (_original.TryGetValue(path, out var b) && (Binary(b) || b.Length > MostTextBytes))
        {
            throw new WorkspaceException($"{path} is not a text file to change.");
        }
        if (Encoding.UTF8.GetByteCount(text) > MostTextBytes)
        {
            throw new WorkspaceException("A file is at most 1 MB here.");
        }
        if (Original(path) == text)
        {
            _changed.Remove(path);
            return;
        }
        _changed[path] = text;
    }

    public void Delete(string path)
    {
        path = Clean(path);
        if (!Exists(path))
        {
            throw new WorkspaceException($"There is no file {path}.");
        }
        if (_original.ContainsKey(path))
        {
            _changed[path] = null;
        }
        else
        {
            _changed.Remove(path);
        }
    }

    /// <summary>Changes kept from before (another round on the same fix).</summary>
    public void Apply(IReadOnlyDictionary<string, string?> changes)
    {
        foreach (var (path, text) in changes)
        {
            if (text is null)
            {
                _changed[path] = null;
            }
            else
            {
                _changed[path] = text;
            }
        }
    }

    /// <summary>"path:line: text" for each line holding the text, at most so many.</summary>
    public List<string> Search(string text, string? under, int most = 100)
    {
        var found = new List<string>();
        foreach (var path in Paths.Where(p => under is null || p.StartsWith(Clean(under), StringComparison.Ordinal)))
        {
            string content;
            try
            {
                content = Read(path);
            }
            catch (WorkspaceException)
            {
                continue;
            }
            var lines = content.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains(text, StringComparison.Ordinal))
                {
                    found.Add($"{path}:{i + 1}: {lines[i].Trim()[..Math.Min(lines[i].Trim().Length, 200)]}");
                    if (found.Count >= most)
                    {
                        return found;
                    }
                }
            }
        }
        return found;
    }

    /// <summary>The whole, as it is now, as a tar (for the sandbox to run the tests in).</summary>
    public byte[] Tar()
    {
        using var m = new MemoryStream();
        using (var tar = new TarWriter(m, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var path in Paths)
            {
                var bytes = _changed.TryGetValue(path, out var c) && c is not null ? Encoding.UTF8.GetBytes(c) : _original[path];
                var entry = new PaxTarEntry(TarEntryType.RegularFile, path) { DataStream = new MemoryStream(bytes) };
                tar.WriteEntry(entry);
            }
        }
        return m.ToArray();
    }

    /// <summary>What changed, as a unified diff (three lines around each change).</summary>
    public string Diff()
    {
        var w = new StringBuilder();
        foreach (var (path, now) in _changed.OrderBy(c => c.Key, StringComparer.Ordinal))
        {
            var before = Original(path);
            w.Append("--- ").AppendLine(before is null ? "/dev/null" : $"a/{path}");
            w.Append("+++ ").AppendLine(now is null ? "/dev/null" : $"b/{path}");
            w.Append(LineDiff.Unified(before ?? "", now ?? ""));
        }
        return w.ToString();
    }
}

/// <summary>A line diff (the longest common lines), as unified diff hunks.</summary>
public static class LineDiff
{
    private const int Context = 3;

    private static string[] Lines(string text) => text.Length == 0 ? [] : text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n');

    public static string Unified(string before, string after)
    {
        var a = Lines(before);
        var b = Lines(after);
        // Common ends first: most changes are small, so the table is small.
        var start = 0;
        while (start < a.Length && start < b.Length && a[start] == b[start])
        {
            start++;
        }
        var endA = a.Length;
        var endB = b.Length;
        while (endA > start && endB > start && a[endA - 1] == b[endB - 1])
        {
            endA--;
            endB--;
        }
        var ops = new List<(char Kind, string Line, int A, int B)>();
        for (var i = 0; i < start; i++)
        {
            ops.Add((' ', a[i], i, i));
        }
        var n = endA - start;
        var m = endB - start;
        if ((long)n * m > 4_000_000)
        {
            for (var i = start; i < endA; i++)
            {
                ops.Add(('-', a[i], i, -1));
            }
            for (var j = start; j < endB; j++)
            {
                ops.Add(('+', b[j], -1, j));
            }
        }
        else
        {
            var table = new int[n + 1, m + 1];
            for (var i = n - 1; i >= 0; i--)
            {
                for (var j = m - 1; j >= 0; j--)
                {
                    table[i, j] = a[start + i] == b[start + j] ? table[i + 1, j + 1] + 1 : Math.Max(table[i + 1, j], table[i, j + 1]);
                }
            }
            int x = 0, y = 0;
            while (x < n || y < m)
            {
                if (x < n && y < m && a[start + x] == b[start + y])
                {
                    ops.Add((' ', a[start + x], start + x, start + y));
                    x++;
                    y++;
                }
                // What goes before what comes, as diffs show it.
                else if (x < n && (y >= m || table[x + 1, y] >= table[x, y + 1]))
                {
                    ops.Add(('-', a[start + x], start + x, -1));
                    x++;
                }
                else
                {
                    ops.Add(('+', b[start + y], -1, start + y));
                    y++;
                }
            }
        }
        for (int i = endA, j = endB; i < a.Length; i++, j++)
        {
            ops.Add((' ', a[i], i, j));
        }
        return Hunks(ops);
    }

    private static string Hunks(List<(char Kind, string Line, int A, int B)> ops)
    {
        var w = new StringBuilder();
        var i = 0;
        while (i < ops.Count)
        {
            if (ops[i].Kind == ' ')
            {
                i++;
                continue;
            }
            var from = Math.Max(0, i - Context);
            var to = i;
            // Take the changes in, and the context between them while it is short.
            while (to < ops.Count)
            {
                if (ops[to].Kind != ' ')
                {
                    to++;
                    continue;
                }
                var run = to;
                while (run < ops.Count && ops[run].Kind == ' ')
                {
                    run++;
                }
                if (run < ops.Count && run - to <= 2 * Context)
                {
                    to = run;
                    continue;
                }
                to = Math.Min(ops.Count, to + Context);
                break;
            }
            var part = ops.GetRange(from, to - from);
            var aStart = part.FirstOrDefault(o => o.A >= 0).A;
            var bStart = part.FirstOrDefault(o => o.B >= 0).B;
            var aCount = part.Count(o => o.Kind != '+');
            var bCount = part.Count(o => o.Kind != '-');
            w.Append(System.Globalization.CultureInfo.InvariantCulture, $"@@ -{(aCount == 0 ? aStart : aStart + 1)},{aCount} +{(bCount == 0 ? bStart : bStart + 1)},{bCount} @@\n");
            foreach (var o in part)
            {
                w.Append(o.Kind).Append(o.Line).Append('\n');
            }
            i = to;
        }
        return w.ToString();
    }
}
