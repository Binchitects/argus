using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeArena;

/// <summary>
/// The folders the IDE can open and add (Open Folder, Add Folder to Workspace): the ones opened lately, kept in the data
/// folder (recent-folders.json, the last 20), and a folder's own folders for the page's picker. Only names and paths of
/// folders are listed, never a file's content; a folder that cannot be read lists as empty, with why.
/// </summary>
internal static class Folders
{
    public const string FileName = "recent-folders.json";
    private const int Kept = 20;
    /// <summary>The most folders one folder lists in the picker.</summary>
    private const int Listed = 2000;

    /// <summary>The folders opened lately, the last first (those that still exist).</summary>
    public static List<string> Recent(string dataDir)
    {
        try
        {
            var list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(Path.Combine(dataDir, FileName))) ?? [];
            return [.. list.Where(Directory.Exists)];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    /// <summary>The folder opened now, first in the recent ones.</summary>
    public static void Remember(string dataDir, string folder)
    {
        var list = Recent(dataDir).Where(f => !Same(f, folder)).Prepend(folder).Take(Kept).ToList();
        try
        {
            Directory.CreateDirectory(dataDir);
            var file = Path.Combine(dataDir, FileName);
            File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(list));
            File.Move(file + ".tmp", file, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Not remembered: the picker lists fewer.
        }
    }

    /// <summary>
    /// A folder the person typed or chose, as a full path: ~ is their home, a relative path is from the folder open now.
    /// Refused (with why) when it is not a folder.
    /// </summary>
    public static string Resolve(string? path, string from, Func<string, string?> env)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0'))
        {
            throw new IdeError(400, "invalid", "Choose a folder.");
        }
        var home = Home(env);
        var expanded = path == "~" ? home : path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal) ? Path.Combine(home, path[2..]) : path;
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(expanded, from));
        // A drive's root keeps its separator (C:\), as Windows needs it.
        if (full.Length == 2 && full[1] == ':')
        {
            full += Path.DirectorySeparatorChar;
        }
        return Directory.Exists(full) ? full : throw new IdeError(404, "not_found", $"{full} is not a folder.");
    }

    /// <summary>
    /// A folder's folders, by name (the hidden ones last), with its parent and, on Windows, the drives: what the picker
    /// shows. Files are not listed.
    /// </summary>
    public static JsonObject List(string folder, string current, string dataDir, Func<string, string?> env)
    {
        var names = new List<string>();
        string? why = null;
        try
        {
            foreach (var dir in new DirectoryInfo(folder).EnumerateDirectories("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 }))
            {
                names.Add(dir.Name);
                if (names.Count >= Listed)
                {
                    why = $"Only the first {Listed} folders are listed: type the name.";
                    break;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            why = $"Its folders cannot be read: {e.Message}";
        }
        names.Sort((a, b) => a.StartsWith('.') != b.StartsWith('.') ? a.StartsWith('.') ? 1 : -1 : string.Compare(a, b, StringComparison.OrdinalIgnoreCase));
        var parent = Directory.GetParent(folder)?.FullName;
        return new JsonObject
        {
            ["path"] = folder,
            ["parent"] = parent,
            ["folders"] = new JsonArray([.. names.Select(n => (JsonNode)n)]),
            ["why"] = why,
            ["current"] = current,
            ["home"] = Home(env),
            ["separator"] = Path.DirectorySeparatorChar.ToString(),
            ["drives"] = OperatingSystem.IsWindows() ? new JsonArray([.. DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => (JsonNode)d.Name)]) : new JsonArray(),
            ["recent"] = new JsonArray([.. Recent(dataDir).Select(f => (JsonNode)f)]),
        };
    }

    private static string Home(Func<string, string?> env) =>
        (OperatingSystem.IsWindows() ? env("USERPROFILE") : env("HOME")) is { Length: > 0 } home ? home : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static bool Same(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
