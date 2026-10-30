using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace CodeArena;

/// <summary>
/// Hot exit: the unsaved text of the IDE's tabs, kept while the person types so a reload (or the window closing) loses
/// nothing. One file per open file in Code Arena's data folder (backups/&lt;the folder's hash&gt;/), never in the
/// repository, written whole or not at all; 5 MB a file and 50 MB a folder at most. A save, Don't Save or a revert
/// deletes it. Nothing of the text is logged.
/// </summary>
internal sealed class Backups(string dataDir, string root, Func<string, string> resolve)
{
    public const int MostBytes = 5 * 1024 * 1024;
    public const long MostTotal = 50L * 1024 * 1024;

    private readonly object _gate = new();

    private string Folder => Path.Combine(dataDir, "backups", Hash(root));

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];

    /// <summary>A path the page gave: one of the folder's (resolved, so never outside it), or one of the person's own files (user:settings.json).</summary>
    private string Checked(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new IdeError(400, "invalid", "Give the file's path.");
        }
        if (path.StartsWith("user:", StringComparison.Ordinal))
        {
            return path;
        }
        resolve(path);
        return path.Replace('\\', '/');
    }

    /// <summary>Every backup of this folder: the path, the text, the version of the file it was typed over, and when.</summary>
    public JsonArray List()
    {
        lock (_gate)
        {
            var list = new JsonArray();
            if (!Directory.Exists(Folder))
            {
                return list;
            }
            foreach (var file in Directory.EnumerateFiles(Folder, "*.json").Order(StringComparer.Ordinal))
            {
                try
                {
                    if (JsonNode.Parse(File.ReadAllText(file)) is JsonObject backup && backup.Str("path") is not null && backup.Str("text") is not null)
                    {
                        list.Add(backup);
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
                {
                    // One that cannot be read is left out.
                }
            }
            return list;
        }
    }

    /// <summary>Keeps a file's unsaved text (null text: deletes it, the file saved or its changes thrown away).</summary>
    public void Keep(string? path, string? text, string? version)
    {
        var name = Checked(path);
        var file = Path.Combine(Folder, Hash(name) + ".json");
        lock (_gate)
        {
            if (text is null)
            {
                File.Delete(file);
                return;
            }
            if (Encoding.UTF8.GetByteCount(text) > MostBytes)
            {
                throw new IdeError(413, "too_large", $"{name} is larger than 5 MB: its unsaved text is not kept.");
            }
            var others = Directory.Exists(Folder)
                ? Directory.EnumerateFiles(Folder, "*.json").Where(f => !string.Equals(f, file, StringComparison.Ordinal)).Sum(f => new FileInfo(f).Length)
                : 0;
            if (others + text.Length > MostTotal)
            {
                throw new IdeError(413, "too_large", "The unsaved text kept for this folder is past 50 MB: save some files.");
            }
            var backup = new JsonObject { ["path"] = name, ["text"] = text, ["version"] = version, ["savedAt"] = DateTimeOffset.UtcNow.ToString("O") };
            PrivateFiles.WriteAllText(file, backup.ToJsonString());
        }
    }
}
