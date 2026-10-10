using System.Text;
using System.Text.RegularExpressions;

namespace CodeArena;

/// <summary>
/// @path in what the person types: the file goes with the message (a folder, its list), so the model need not look
/// for it; @path:12-30 (or #L12-L30) its lines, with a few around them. From the IDE, the lines chosen in its editor go
/// too (<see cref="Piece"/>). Only paths the tools may read, that exist; anything else (an email address, @decorator)
/// stays as typed. What goes with a message comes after what was typed, between <see cref="Marker"/> and
/// <see cref="End"/>: the page shows what was typed (<see cref="Typed"/>) and what went with it (<see cref="Attached"/>).
/// </summary>
internal static partial class Mentions
{
    /// <summary>The most of one file a message carries; more is cut in the middle.</summary>
    public const int MaxChars = 60_000;

    /// <summary>The most of every file a message carries together.</summary>
    public const int MaxTotal = 150_000;

    /// <summary>The most lines chosen in the editor one message carries.</summary>
    public const int MaxPieces = 8;

    /// <summary>The lines around a range of a file cited, before and after it.</summary>
    public const int Around = 3;

    /// <summary>Where what went with the message starts, after what was typed; and where it ends.</summary>
    public const string Marker = "\n\n<attached>";
    public const string End = "\n</attached>";

    /// <summary>Lines of a file chosen in the IDE's editor: the first and last (from 1), and the editor's text when it is not saved.</summary>
    public sealed record Piece(string Path, int StartLine, int EndLine, string? Text);

    // @ at the start or after a space or bracket; the path runs to the next space (quoted: "@a b.txt" in quotes).
    [GeneratedRegex("""(?<=^|[\s(\[{])@(?:"(?<q>[^"]+)"|(?<p>[^\s"'`)\]},;]+))""")]
    private static partial Regex Mention();

    // A range after a path: :12, :12-30, #L12, #L12-L30.
    [GeneratedRegex("""(?::(?<a>\d+)(?:-(?<b>\d+))?|#L(?<a>\d+)(?:-L?(?<b>\d+))?)$""")]
    private static partial Regex Range();

    // What went with a message, as the page lists it.
    [GeneratedRegex("""<(?<kind>file|folder|selection) path="(?<path>[^"]*)"(?: lines="(?<lines>\d+-\d+)")?""")]
    private static partial Regex Block();

    /// <summary>What the person typed of a message (what went with it left out).</summary>
    public static string Typed(string content) => content.IndexOf(Marker, StringComparison.Ordinal) is var at and >= 0 ? content[..at] : content;

    /// <summary>What went with a message: each file, folder or selection as path, or path:first-last.</summary>
    public static List<string> Attached(string content)
    {
        var at = content.IndexOf(Marker, StringComparison.Ordinal);
        if (at < 0)
        {
            return [];
        }
        return [.. Block().Matches(content, at).Select(m => m.Groups["lines"].Success ? Label(m.Groups["path"].Value, m.Groups["lines"].Value) : m.Groups["path"].Value).Distinct()];
    }

    /// <summary>Lines of a file as the person reads them: path:first-last, path:line for one.</summary>
    private static string Label(string path, string lines)
    {
        var dash = lines.IndexOf('-');
        return dash > 0 && lines[..dash] == lines[(dash + 1)..] ? $"{path}:{lines[..dash]}" : $"{path}:{lines}";
    }

    /// <summary>
    /// The message with the files it names, and the lines chosen in the editor, after it; what went, for the person to
    /// see (path, or path:first-last).
    /// </summary>
    public static (string Text, List<string> Files) Expand(string input, Workspace workspace, IReadOnlyList<Piece>? pieces = null)
    {
        var files = new List<string>();
        var sb = new StringBuilder();
        var total = 0;
        foreach (var piece in (pieces ?? []).Take(MaxPieces))
        {
            if (total >= MaxTotal || Selection(piece, workspace) is not { } block)
            {
                continue;
            }
            total += block.Text.Length;
            sb.Append(block.Text);
            files.Add(block.Label);
        }
        foreach (Match m in Mention().Matches(input))
        {
            var path = m.Groups["q"].Success ? m.Groups["q"].Value : m.Groups["p"].Value.TrimEnd('.', ':', '!', '?');
            (int First, int Last)? lines = null;
            if (!m.Groups["q"].Success && Range().Match(path) is { Success: true } r && Exists(workspace, path[..r.Index]))
            {
                var a = int.Parse(r.Groups["a"].Value);
                var b = r.Groups["b"].Success ? int.Parse(r.Groups["b"].Value) : a;
                lines = (Math.Max(1, Math.Min(a, b)), Math.Max(a, b));
                path = path[..r.Index];
            }
            var label = lines is { } l ? $"{path}:{l.First}-{l.Last}" : path;
            if (path.Length == 0 || files.Contains(label))
            {
                continue;
            }
            string full;
            try
            {
                full = workspace.Resolve(path);
            }
            catch (ToolError)
            {
                continue;
            }
            if (Directory.Exists(full))
            {
                var entries = new DirectoryInfo(full).EnumerateFileSystemInfos().Where(e => e.Name != ".git").OrderBy(e => e.Name, StringComparer.Ordinal).Take(200)
                    .Select(e => e is DirectoryInfo ? e.Name + "/" : e.Name);
                sb.Append($"\n<folder path=\"{path}\">\n{string.Join('\n', entries)}\n</folder>");
                files.Add(path);
                continue;
            }
            if (!File.Exists(full) || total >= MaxTotal)
            {
                continue;
            }
            string text;
            try
            {
                var bytes = File.ReadAllBytes(full);
                if (bytes.Take(8000).Contains((byte)0))
                {
                    sb.Append($"\n<file path=\"{path}\">(binary, {bytes.Length:N0} bytes: not shown)</file>");
                    files.Add(path);
                    continue;
                }
                text = Encoding.UTF8.GetString(bytes);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            var all = Lines(text);
            string attr;
            if (lines is { } range)
            {
                // The lines cited (past the end: its last), a few around them, numbered as read_file numbers them.
                var first = Math.Min(range.First, Math.Max(1, all.Length));
                var last = Math.Max(first, Math.Min(range.Last, all.Length));
                text = Numbered(all, Math.Max(1, first - Around), Math.Min(all.Length, last + Around));
                attr = $" lines=\"{first}-{last}\"";
                label = Label(path, $"{first}-{last}");
            }
            else
            {
                text = Numbered(all, 1, all.Length);
                attr = "";
            }
            var room = Math.Min(MaxChars, MaxTotal - total);
            if (text.Length > room)
            {
                text = text[..(room / 2)] + $"\n… ({text.Length - room:N0} characters cut: read_file reads them) …\n" + text[^(room / 2)..];
            }
            total += text.Length;
            sb.Append($"\n<file path=\"{path}\"{attr}>\n{text}\n</file>");
            files.Add(label);
        }
        return (sb.Length == 0 ? input : input + Marker + sb + End, files);
    }

    /// <summary>Lines chosen in the editor, as a block: the editor's text when unsaved, else the file's lines on disk; null: nothing to send.</summary>
    private static (string Text, string Label)? Selection(Piece piece, Workspace workspace)
    {
        if (piece.StartLine < 1 || piece.EndLine < piece.StartLine)
        {
            return null;
        }
        string full;
        try
        {
            full = workspace.Resolve(piece.Path);
        }
        catch (ToolError)
        {
            return null;
        }
        string[] lines;
        int first;
        if (piece.Text is { } typed)
        {
            lines = Lines(typed.Length > MaxChars ? typed[..MaxChars] : typed);
            first = piece.StartLine;
        }
        else
        {
            if (!File.Exists(full))
            {
                return null;
            }
            try
            {
                var all = Lines(File.ReadAllText(full));
                var last = Math.Min(piece.EndLine, all.Length);
                if (piece.StartLine > last)
                {
                    return null;
                }
                lines = all[(piece.StartLine - 1)..last];
                first = piece.StartLine;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
        var end = first + lines.Length - 1;
        var numbered = string.Join('\n', lines.Select((l, i) => $"{(first + i).ToString().PadLeft(6)}\t{l}"));
        if (numbered.Length > MaxChars)
        {
            numbered = numbered[..MaxChars] + "\n… (cut: read_file reads the rest) …";
        }
        var unsaved = piece.Text is null ? "" : " unsaved=\"true\"";
        return ($"\n<selection path=\"{piece.Path}\" lines=\"{first}-{end}\"{unsaved}>\n{numbered}\n</selection>", Label(piece.Path, $"{first}-{end}"));
    }

    private static bool Exists(Workspace workspace, string path)
    {
        try
        {
            return File.Exists(workspace.Resolve(path));
        }
        catch (ToolError)
        {
            return false;
        }
    }

    private static string[] Lines(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        return text.EndsWith('\n') ? lines[..^1] : lines;
    }

    /// <summary>Lines first to last (from 1) as read_file numbers them.</summary>
    private static string Numbered(string[] lines, int first, int last) =>
        string.Join('\n', Enumerable.Range(first, Math.Max(0, last - first + 1)).Select(n => $"{n.ToString().PadLeft(6)}\t{lines[n - 1]}"));
}
