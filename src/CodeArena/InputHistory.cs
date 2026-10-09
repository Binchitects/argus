using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace CodeArena;

/// <summary>
/// What the person sent in one folder, kept as a shell keeps its history: a file
/// per folder under the data folder's history/ (JSON Lines, readable by its owner
/// only), the newest last. The terminal's ↑ and the IDE's chat both read it, and
/// both add to it, so a message sent in one is there in the other.
/// </summary>
internal sealed class InputHistory(string dir, string folder, int max = InputHistory.Max)
{
    /// <summary>The entries kept per folder; the file is cut back to these once it holds twice as many.</summary>
    public const int Max = 1000;

    private static readonly object Gate = new();

    public string Dir { get; } = dir;
    public string File { get; } = Path.Combine(dir, Key(folder) + ".jsonl");

    /// <summary>A folder's file name: a hash of its full path, so any path makes a safe name (case aside where paths ignore it).</summary>
    public static string Key(string folder)
    {
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            path = path.ToLowerInvariant();
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(path)))[..16];
    }

    /// <summary>The entries, oldest first; read from the file each time, so another code-arena in this folder is heard.</summary>
    public List<string> Entries() => [.. Lines().Select(Text).OfType<string>()];

    /// <summary>Keeps a message as the newest entry; the same text twice in a row is kept once.</summary>
    public void Add(string text)
    {
        text = text.Trim();
        if (text.Length == 0)
        {
            return;
        }
        lock (Gate)
        {
            try
            {
                var lines = Lines();
                if (lines.Select(Text).LastOrDefault(t => t is not null) == text)
                {
                    return;
                }
                PrivateFiles.EnsureDirectory(Dir);
                if (lines.Count + 1 >= 2 * max)
                {
                    var kept = lines.Select(Text).OfType<string>().TakeLast(max - 1).Append(text);
                    PrivateFiles.WriteAllText(File, string.Concat(kept.Select(t => Line(t) + "\n")));
                    return;
                }
                // After a line cut short (the program stopped mid-write), on a line of its own.
                PrivateFiles.AppendLine(File, (EndsMidLine() ? "\n" : "") + Line(text));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A history that cannot be written does not stop the message.
            }
        }
    }

    private List<string> Lines()
    {
        try
        {
            if (!System.IO.File.Exists(File))
            {
                return [];
            }
            using var reader = new StreamReader(new FileStream(File, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            var lines = new List<string>();
            while (reader.ReadLine() is { } line)
            {
                lines.Add(line);
            }
            return lines;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // No history is better than no prompt.
            return [];
        }
    }

    private bool EndsMidLine()
    {
        if (!System.IO.File.Exists(File))
        {
            return false;
        }
        using var stream = new FileStream(File, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length == 0)
        {
            return false;
        }
        stream.Seek(-1, SeekOrigin.End);
        return stream.ReadByte() != '\n';
    }

    /// <summary>An entry's text; null for a line cut short.</summary>
    private static string? Text(string line) => Json.ParseObject(line).Str("text") is { Length: > 0 } text ? text : null;

    private static string Line(string text) => Json.Line(new JsonObject { ["text"] = text, ["at"] = DateTimeOffset.Now.ToString("o") });
}
