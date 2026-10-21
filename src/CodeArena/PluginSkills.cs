using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeArena;

/// <summary>
/// The skills Arena's plugins bring (/api/code-arena/skills, only those of plugins the person may use), kept under the
/// config folder's plugin-skills/ and read after the person's own and the project's (which win by name). Written anew
/// at each start when Arena answers; what was kept stays when it does not (offline, an older Arena).
/// </summary>
internal static partial class PluginSkills
{
    public const string FolderName = "plugin-skills";

    public static string Folder(AppPaths paths) => Path.Combine(paths.ConfigDir, FolderName);

    /// <summary>Writes Arena's answer: each skill its folder, its files in it; the folders of skills no longer brought taken away. The problems, if any.</summary>
    public static List<string> Keep(AppPaths paths, JsonObject answer)
    {
        var problems = new List<string>();
        var root = Folder(paths);
        var staging = root + ".new";
        if (Directory.Exists(staging))
        {
            Directory.Delete(staging, recursive: true);
        }
        Directory.CreateDirectory(staging);
        foreach (var skill in (answer["skills"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var name = skill.Str("name") ?? "";
            if (!SkillName().IsMatch(name))
            {
                problems.Add($"A plugin's skill \"{name}\" has a name Code Arena does not take: left out.");
                continue;
            }
            var folder = Path.Combine(staging, name);
            foreach (var (path, text) in skill["files"] as JsonObject ?? [])
            {
                // Only files inside the skill's own folder, whatever the answer says.
                var full = Path.GetFullPath(Path.Combine(folder, path));
                if (path.Length == 0 || Path.IsPathRooted(path) || path.Split('/', '\\').Any(p => p is ".." or "") || !full.StartsWith(Path.GetFullPath(folder) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                {
                    problems.Add($"The skill {name}'s file {path} is outside its folder: left out.");
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, text?.GetValue<string>() ?? "");
            }
        }
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
        Directory.Move(staging, root);
        return problems;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$")]
    private static partial Regex SkillName();
}
