using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeArena;

/// <summary>
/// VS Code's multi-root workspace file (NAME.code-workspace): JSON with comments, its "folders" (each a "path",
/// relative to the file or whole, or a file: "uri", with a "name" to show), its "settings", and whatever else (launch,
/// tasks, extensions), kept as written. The IDE runs on one of its folders (the one open, else the first there) and adds
/// the others to its workspace, as Add Folder does; the file stays the list: what it says is what is added.
/// </summary>
internal static class CodeWorkspace
{
    /// <summary>The key in a folder's state of the workspace file it was opened with.</summary>
    public const string Key = "workspaceFile";

    public const string Extension = ".code-workspace";

    /// <summary>The most a workspace file holds.</summary>
    public const int MaxBytes = 1024 * 1024;

    /// <summary>A folder of the file's: its full path, the name it shows (null: the folder's own), and whether it is there.</summary>
    public sealed record Folder(string Full, string? Name, bool Exists);

    private static readonly JsonDocumentOptions Lenient = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>A workspace file chosen or named: its full path, refused when it is not one (or not there).</summary>
    public static string Resolve(string? path, string from)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0'))
        {
            throw new IdeError(400, "invalid", "Choose a .code-workspace file.");
        }
        var full = Path.GetFullPath(path, from);
        if (!full.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
        {
            throw new IdeError(400, "invalid", $"{full} is not a workspace file (NAME{Extension}).");
        }
        return File.Exists(full) ? full : throw new IdeError(404, "not_found", $"There is no {full}.");
    }

    /// <summary>A workspace file's text, read (at most 1 MB).</summary>
    public static string Text(string file)
    {
        if (new FileInfo(file).Length > MaxBytes)
        {
            throw new IdeError(413, "too_large", $"{Path.GetFileName(file)} is larger than {MaxBytes / 1024} KB.");
        }
        return File.ReadAllText(file);
    }

    /// <summary>Its folders and settings, read from its text; what is wrong in it, said (a folder of another kind, one not there).</summary>
    public static (List<Folder> Folders, JsonObject Settings, List<string> Problems) Read(string file, string text)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(text, documentOptions: Lenient);
        }
        catch (JsonException e)
        {
            throw new IdeError(400, "invalid", $"{Path.GetFileName(file)} is not JSON: {e.Message}");
        }
        if (root is not JsonObject all || all["folders"] is not JsonArray list)
        {
            throw new IdeError(400, "invalid", $"{Path.GetFileName(file)} has no \"folders\" list: a workspace file is {{ \"folders\": [{{ \"path\": \".\" }}] }}.");
        }
        var dir = Path.GetDirectoryName(file)!;
        var folders = new List<Folder>();
        var problems = new List<string>();
        foreach (var entry in list)
        {
            string? path = null;
            if (entry?.Str("path") is { Length: > 0 } p)
            {
                path = p;
            }
            else if (entry?.Str("uri") is { Length: > 0 } uri)
            {
                if (Uri.TryCreate(uri, UriKind.Absolute, out var u) && u.IsFile)
                {
                    path = u.LocalPath;
                }
                else
                {
                    problems.Add($"{uri}: only folders on this computer (file:) are opened here.");
                    continue;
                }
            }
            if (path is null)
            {
                problems.Add("A folder with no \"path\" is left out.");
                continue;
            }
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path, dir));
            if (folders.Any(f => Same(f.Full, full)))
            {
                continue;
            }
            var exists = Directory.Exists(full);
            if (!exists)
            {
                problems.Add($"{full} is not there: it is left out.");
            }
            folders.Add(new Folder(full, entry?.Str("name") is { Length: > 0 } name ? name : null, exists));
        }
        return (folders, all["settings"] as JsonObject ?? [], problems);
    }

    /// <summary>A new workspace file's text: these folders (relative to it where they are under its folder, else whole), no settings yet.</summary>
    public static string Make(string file, IEnumerable<string> folders)
    {
        var dir = Path.GetDirectoryName(file)!;
        var list = new JsonArray([.. folders.Select(f => (JsonNode)new JsonObject { ["path"] = Relative(dir, f) })]);
        return new JsonObject { ["folders"] = list, ["settings"] = new JsonObject() }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).Replace("\r\n", "\n") + "\n";
    }

    /// <summary>A folder as a workspace file names it: from the file's folder with / (".", "../lib"), whole when it is on another drive.</summary>
    public static string Relative(string dir, string folder)
    {
        var rel = Path.GetRelativePath(dir, folder);
        return Path.IsPathRooted(rel) ? folder.Replace('\\', '/') : rel.Replace('\\', '/');
    }

    /// <summary>Writes the file whole (or not at all), unless it changed since the page read it at <paramref name="version"/>.</summary>
    public static void Write(string file, string text, string? version)
    {
        if (Encoding.UTF8.GetByteCount(text) > MaxBytes)
        {
            throw new IdeError(413, "too_large", $"A workspace file holds at most {MaxBytes / 1024} KB.");
        }
        if (version is not null && IdeFiles.Version(file) != version)
        {
            throw new IdeError(409, "changed", $"{Path.GetFileName(file)} changed since it was read. Read it again.");
        }
        var temp = $"{file}.{Environment.ProcessId}.tmp";
        File.WriteAllText(temp, text);
        File.Move(temp, file, overwrite: true);
    }

    public static bool Same(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
