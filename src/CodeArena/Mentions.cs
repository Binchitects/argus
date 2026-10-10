using System.Text;
using System.Text.RegularExpressions;

namespace CodeArena;

/// <summary>
/// @path in what the person types: the file goes with the message (a folder, its list), so the model need not look
/// for it. Only paths the tools may read, that exist; anything else (an email address, @decorator) stays as typed.
/// </summary>
internal static partial class Mentions
{
    /// <summary>The most of one file a message carries; more is cut in the middle.</summary>
    public const int MaxChars = 60_000;

    /// <summary>The most of every file a message carries together.</summary>
    public const int MaxTotal = 150_000;

    // @ at the start or after a space or bracket; the path runs to the next space (quoted: "@a b.txt" in quotes).
    [GeneratedRegex("""(?<=^|[\s(\[{])@(?:"(?<q>[^"]+)"|(?<p>[^\s"'`)\]},;]+))""")]
    private static partial Regex Mention();

    /// <summary>The message with the files it names after it; the paths found, for the person to see.</summary>
    public static (string Text, List<string> Files) Expand(string input, Workspace workspace)
    {
        var files = new List<string>();
        var sb = new StringBuilder();
        var total = 0;
        foreach (Match m in Mention().Matches(input))
        {
            var path = m.Groups["q"].Success ? m.Groups["q"].Value : m.Groups["p"].Value.TrimEnd('.', ':', '!', '?');
            if (path.Length == 0 || files.Contains(path))
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
                sb.Append($"\n\n<folder path=\"{path}\">\n{string.Join('\n', entries)}\n</folder>");
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
                    sb.Append($"\n\n<file path=\"{path}\">(binary, {bytes.Length:N0} bytes: not shown)</file>");
                    files.Add(path);
                    continue;
                }
                text = Encoding.UTF8.GetString(bytes);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            var room = Math.Min(MaxChars, MaxTotal - total);
            if (text.Length > room)
            {
                text = text[..(room / 2)] + $"\n… ({text.Length - room:N0} characters cut: read_file reads them) …\n" + text[^(room / 2)..];
            }
            total += text.Length;
            sb.Append($"\n\n<file path=\"{path}\">\n{text}\n</file>");
            files.Add(path);
        }
        return (sb.Length == 0 ? input : input + sb, files);
    }
}
