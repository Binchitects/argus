using System.Text.Json.Nodes;

namespace CodeArena;

/// <summary>
/// The snippets files VS Code reads beside an extension's: the project's own (.vscode/*.code-snippets in the folder open
/// and in each folder added to the workspace) and the person's (snippets/*.json, one per language, and
/// snippets/*.code-snippets in the config folder, as VS Code's User/snippets). Each is sent whole with its version, for the
/// page to read; a file too large, or past the most kept, is left out and said so.
/// </summary>
internal static class IdeSnippets
{
    /// <summary>The most one snippets file holds.</summary>
    public const int MaxBytes = 1024 * 1024;

    /// <summary>The most files listed (project and person together).</summary>
    public const int MaxFiles = 100;

    /// <summary>The snippets files of these folders (the folder open first) and of the person's config folder.</summary>
    public static JsonObject List(IReadOnlyList<string> folders, Func<string, string> show, string configDir)
    {
        var files = new JsonArray();
        var problems = new JsonArray();
        void Add(string source, string full, string path, string? language)
        {
            if (files.Count >= MaxFiles)
            {
                problems.Add((JsonNode)$"{path}: only the first {MaxFiles} snippets files are read.");
                return;
            }
            try
            {
                if (new FileInfo(full).Length > MaxBytes)
                {
                    problems.Add((JsonNode)$"{path} is larger than {MaxBytes / 1024} KB: it is not read.");
                    return;
                }
                files.Add(new JsonObject
                {
                    ["source"] = source,
                    ["path"] = path,
                    ["language"] = language,
                    ["text"] = File.ReadAllText(full),
                    ["version"] = IdeFiles.Version(full),
                });
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                problems.Add((JsonNode)$"{path} could not be read: {e.Message}");
            }
        }
        foreach (var folder in folders)
        {
            foreach (var full in Sorted(Path.Combine(folder, ".vscode"), "*.code-snippets"))
            {
                Add("project", full, show(full), null);
            }
        }
        var own = Path.Combine(configDir, "snippets");
        // A .json file is for the language it is named after (python.json); a .code-snippets file says its own scope.
        foreach (var full in Sorted(own, "*.json"))
        {
            Add("user", full, $"user:snippets/{Path.GetFileName(full)}", Path.GetFileNameWithoutExtension(full));
        }
        foreach (var full in Sorted(own, "*.code-snippets"))
        {
            Add("user", full, $"user:snippets/{Path.GetFileName(full)}", null);
        }
        return new JsonObject { ["files"] = files, ["problems"] = problems };
    }

    private static IEnumerable<string> Sorted(string dir, string pattern)
    {
        if (!Directory.Exists(dir))
        {
            return [];
        }
        try
        {
            return Directory.EnumerateFiles(dir, pattern, new EnumerationOptions { IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive })
                .Order(StringComparer.Ordinal).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
