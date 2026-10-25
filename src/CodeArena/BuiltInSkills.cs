using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeArena;

/// <summary>
/// The skills that come with Arena (the repository's skills/ folder, built into Code Arena as into the app): written to the
/// config folder's builtin-skills/ at each start and read last, so the person's, the project's and the plugins' of the same
/// name win. Those an Arena admin turned off (as Arena says when it answers) are left out.
/// </summary>
internal static partial class BuiltInSkills
{
    public const string FolderName = "builtin-skills";

    public static string Folder(AppPaths paths) => Path.Combine(paths.ConfigDir, FolderName);

    /// <summary>The built-in skills, by name: their SKILL.md.</summary>
    public static IReadOnlyDictionary<string, string> All => _all ??= Read();
    private static Dictionary<string, string>? _all;

    private static Dictionary<string, string> Read()
    {
        var assembly = typeof(BuiltInSkills).Assembly;
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var resource in assembly.GetManifestResourceNames().Where(r => r.StartsWith("skills/", StringComparison.Ordinal) && r.EndsWith("/SKILL.md", StringComparison.Ordinal)))
        {
            using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!);
            found[resource["skills/".Length..^"/SKILL.md".Length]] = reader.ReadToEnd();
        }
        return found;
    }

    /// <summary>
    /// Writes them (those not turned off), and takes away the folders of those no longer built in or turned off; with
    /// <paramref name="on"/> false (builtInSkills false in config.json), none.
    /// </summary>
    public static void Keep(AppPaths paths, IReadOnlyCollection<string> off, bool on = true)
    {
        var root = Folder(paths);
        Directory.CreateDirectory(root);
        foreach (var (name, text) in on ? All : new Dictionary<string, string>())
        {
            var folder = Path.Combine(root, name);
            if (off.Contains(name))
            {
                continue;
            }
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, "SKILL.md");
            if (!File.Exists(file) || File.ReadAllText(file) != text)
            {
                File.WriteAllText(file, text);
            }
        }
        foreach (var gone in Directory.EnumerateDirectories(root).Where(d => !on || !All.ContainsKey(Path.GetFileName(d)) || off.Contains(Path.GetFileName(d))))
        {
            Directory.Delete(gone, recursive: true);
        }
    }

    /// <summary>
    /// A skill's instructions as the model is given them: its {{parameters}} named ‹name› and listed with what each is (the
    /// model takes them from the person's request, else their defaults), $ARGUMENTS as "what the person gave".
    /// </summary>
    public static string Filled(string skillMd, string instructions)
    {
        var parameters = ParameterLine().Matches(skillMd).Select(m => (
            Name: m.Groups["name"].Value,
            Description: m.Groups["description"].Value,
            Optional: m.Groups["required"].Value == "false",
            Default: m.Groups["default"].Success ? m.Groups["default"].Value : null)).ToList();
        var body = Variable().Replace(instructions, m => $"‹{m.Groups[1].Value}›").Replace("$ARGUMENTS", "what the person gave", StringComparison.Ordinal);
        if (parameters.Count == 0)
        {
            return body;
        }
        var sb = new StringBuilder(body).Append("\n\nParameters: take each from the person's request");
        sb.Append(parameters.Any(p => p.Default is not null) ? ", else its default:" : ":");
        foreach (var p in parameters)
        {
            sb.Append($"\n- ‹{p.Name}›: {p.Description}{(p.Default is { } d ? $" (default: {d})" : p.Optional ? " (optional)" : "")}");
        }
        return sb.ToString();
    }

    [GeneratedRegex(@"\{\{\s*([A-Za-z][A-Za-z0-9_-]{0,39})\s*\}\}")]
    private static partial Regex Variable();

    [GeneratedRegex("""\{\s*name:\s*(?<name>[A-Za-z][A-Za-z0-9_-]*)\s*,\s*description:\s*"(?<description>[^"]*)"(?:\s*,\s*required:\s*(?<required>true|false))?(?:\s*,\s*default:\s*"(?<default>[^"]*)")?\s*\}""")]
    private static partial Regex ParameterLine();
}
