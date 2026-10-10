using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeArena;

/// <summary>A Markdown file's front matter (--- key: value ---) and its body.</summary>
internal sealed record FrontMatter(IReadOnlyDictionary<string, string> Fields, IReadOnlyDictionary<string, List<string>> Lists, string Body)
{
    public static FrontMatter Parse(string text)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lists = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        if (lines.Length < 2 || lines[0].Trim() != "---")
        {
            return new(fields, lists, text);
        }
        var end = Array.FindIndex(lines, 1, l => l.Trim() == "---");
        if (end < 0)
        {
            return new(fields, lists, text);
        }
        string? listing = null;
        for (var i = 1; i < end; i++)
        {
            var line = lines[i];
            if (listing is not null && line.TrimStart().StartsWith("- ", StringComparison.Ordinal))
            {
                lists[listing].Add(Unquote(line.TrimStart()[2..]));
                continue;
            }
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0 || char.IsWhiteSpace(line[0]))
            {
                continue;
            }
            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            listing = null;
            if (value.Length == 0)
            {
                listing = key;
                lists[key] = [];
            }
            else if (value.StartsWith('[') && value.EndsWith(']'))
            {
                lists[key] = [.. value[1..^1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Unquote)];
            }
            else
            {
                fields[key] = Unquote(value);
                // "tools: read_file, grep": a list written on one line.
                lists[key] = [.. fields[key].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
            }
        }
        return new(fields, lists, string.Join('\n', lines.Skip(end + 1)).Trim());
    }

    private static string Unquote(string v) => v.Length >= 2 && (v[0] == '"' && v[^1] == '"' || v[0] == '\'' && v[^1] == '\'') ? v[1..^1] : v;

    public string? Field(string key) => Fields.TryGetValue(key, out var v) && v.Length > 0 ? v : null;

    public List<string> List(string key) => Lists.TryGetValue(key, out var v) ? v : [];
}

/// <summary>A slash command of the person's or the project's: a prompt with $ARGUMENTS (and $1, $2...) filled in.</summary>
internal sealed record CustomCommand(string Name, string Description, string? ArgumentHint, string Prompt, string File)
{
    public string Fill(string arguments)
    {
        var words = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var text = Prompt.Replace("$ARGUMENTS", arguments, StringComparison.Ordinal);
        text = Regex.Replace(text, @"\$([1-9])", m => int.Parse(m.Groups[1].Value) is var n && n <= words.Length ? words[n - 1] : "");
        // A command that takes no $ARGUMENTS still gets what was typed after it.
        return Prompt.Contains("$ARGUMENTS", StringComparison.Ordinal) || Regex.IsMatch(Prompt, @"\$[1-9]") || arguments.Length == 0 ? text : text + "\n\n" + arguments;
    }
}

/// <summary>A sub-agent of the person's or the project's: its instructions, the read-only tools it may use, its model.</summary>
internal sealed record CustomAgent(string Name, string Description, string Prompt, IReadOnlyList<string> Tools, string? Model, string File);

/// <summary>A skill (agentskills.io's SKILL.md): instructions loaded when the task needs them, with the files beside them.</summary>
internal sealed record Skill(string Name, string Description, string Instructions, string Folder);

/// <summary>
/// The person's and the project's own commands, sub-agents and skills: Markdown files under .arena/ in the project and in
/// the config folder; those written for Claude Code (.claude/) and Qwen Code (.qwen/) are read too. The project's win over
/// the person's of the same name.
/// </summary>
internal sealed class Extensions
{
    public IReadOnlyList<CustomCommand> Commands { get; private init; } = [];
    public IReadOnlyList<CustomAgent> Agents { get; private init; } = [];
    public IReadOnlyList<Skill> Skills { get; private init; } = [];
    /// <summary>Files that could not be read, with why.</summary>
    public IReadOnlyList<string> Problems { get; private init; } = [];

    private static readonly string[] ProjectFolders = [".arena", ".claude", ".qwen"];

    /// <summary>The folders read, the project's first (they win).</summary>
    private static IEnumerable<string> Folders(AppPaths paths, string projectRoot, string kind) =>
        ProjectFolders.Select(f => Path.Combine(projectRoot, f, kind)).Append(Path.Combine(paths.ConfigDir, kind));

    public static Extensions Load(AppPaths paths, string projectRoot)
    {
        var problems = new List<string>();
        var commands = new Dictionary<string, CustomCommand>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in Folders(paths, projectRoot, "commands").Where(Directory.Exists))
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.md", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                // commands/git/commit.md is /git:commit.
                var name = Path.GetRelativePath(dir, file)[..^3].Replace(Path.DirectorySeparatorChar, ':').Replace('/', ':').ToLowerInvariant();
                if (!Valid(name) || commands.ContainsKey(name))
                {
                    continue;
                }
                if (Read(file, problems) is { } fm && fm.Body.Length > 0)
                {
                    commands[name] = new CustomCommand(name, fm.Field("description") ?? FirstLine(fm.Body), fm.Field("argument-hint"), fm.Body, file);
                }
            }
        }
        var agents = new Dictionary<string, CustomAgent>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in Folders(paths, projectRoot, "agents").Where(Directory.Exists))
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.md").Order(StringComparer.Ordinal))
            {
                if (Read(file, problems) is not { } fm || fm.Body.Length == 0)
                {
                    continue;
                }
                var name = (fm.Field("name") ?? Path.GetFileNameWithoutExtension(file)).ToLowerInvariant();
                if (Valid(name) && !agents.ContainsKey(name))
                {
                    agents[name] = new CustomAgent(name, fm.Field("description") ?? FirstLine(fm.Body), fm.Body, fm.List("tools"), fm.Field("model"), file);
                }
            }
        }
        var skills = new Dictionary<string, Skill>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in Folders(paths, projectRoot, "skills").Where(Directory.Exists))
        {
            foreach (var folder in Directory.EnumerateDirectories(dir).Order(StringComparer.Ordinal))
            {
                var file = Path.Combine(folder, "SKILL.md");
                if (!File.Exists(file) || Read(file, problems) is not { } fm || fm.Body.Length == 0)
                {
                    continue;
                }
                var name = (fm.Field("name") ?? Path.GetFileName(folder)).ToLowerInvariant();
                if (Valid(name) && !skills.ContainsKey(name))
                {
                    skills[name] = new Skill(name, fm.Field("description") ?? FirstLine(fm.Body), fm.Body, folder);
                }
            }
        }
        return new Extensions { Commands = [.. commands.Values], Agents = [.. agents.Values], Skills = [.. skills.Values], Problems = problems };
    }

    private static bool Valid(string name) => name.Length is > 0 and <= 64 && name.All(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or ':' or '.');

    private static string FirstLine(string body) => Fmt.OneLine(body.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.TrimStart('#', ' ') ?? "", 120);

    private static FrontMatter? Read(string file, List<string> problems)
    {
        try
        {
            var info = new FileInfo(file);
            if (info.Length > 200_000)
            {
                problems.Add($"{file}: over 200 KB, not read");
                return null;
            }
            return FrontMatter.Parse(File.ReadAllText(file));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            problems.Add($"{file}: {e.Message}");
            return null;
        }
    }

    /// <summary>The prompt a typed /name args stands for; null when no command of that name exists.</summary>
    public string? Expand(string input)
    {
        if (!input.StartsWith('/'))
        {
            return null;
        }
        var parts = input[1..].Split([' ', '\n'], 2);
        return Commands.FirstOrDefault(c => string.Equals(c.Name, parts[0], StringComparison.OrdinalIgnoreCase)) is { } command
            ? command.Fill(parts.Length > 1 ? parts[1].Trim() : "")
            : null;
    }

    /// <summary>The skill tool: a skill's instructions, or one of the files beside them.</summary>
    public ToolDef SkillTool() => new()
    {
        Name = "skill",
        Kind = ToolKind.Read,
        Description = "Load a skill (the list is in your instructions): its instructions for the task, and the names of the files beside them; with file, one of those files.",
        Parameters = (JsonObject)JsonNode.Parse($$$"""
            {"type":"object","properties":{
              "name":{"type":"string","enum":[{{{string.Join(",", Skills.Select(s => JsonValue.Create(s.Name)!.ToJsonString()))}}}]},
              "file":{"type":"string","description":"A file of the skill's folder, by its path in it."}},
             "required":["name"]}
            """)!,
        Summary = a => a.Str("name") + (a.Str("file") is { } f ? " " + f : ""),
        Run = (a, c, ct) =>
        {
            var skill = Skills.FirstOrDefault(s => string.Equals(s.Name, a.Str("name"), StringComparison.OrdinalIgnoreCase))
                ?? throw new ToolError($"There is no skill {a.Str("name")}: {string.Join(", ", Skills.Select(s => s.Name))}.");
            var folder = Path.GetFullPath(skill.Folder);
            var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(folder, f).Replace('\\', '/')).Where(f => f != "SKILL.md").Order(StringComparer.Ordinal).Take(200).ToList();
            if (a.Str("file") is { Length: > 0 } name)
            {
                var full = Path.GetFullPath(name, folder);
                if (!full.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(full))
                {
                    throw new ToolError($"The skill {skill.Name} has no file {name}. Its files: {string.Join(", ", files)}.");
                }
                var text = File.ReadAllText(full);
                return Task.FromResult(new ToolResult(text.Length <= 60_000 ? text : text[..60_000] + "\n… (cut)") { Display = c.Ui.Dim($"{skill.Name}/{name}") });
            }
            var sb = new StringBuilder(skill.Instructions);
            if (files.Count > 0)
            {
                sb.Append("\n\nFiles of this skill (skill with file reads one): ").Append(string.Join(", ", files));
            }
            return Task.FromResult(new ToolResult(sb.ToString()) { Display = c.Ui.Dim($"skill {skill.Name} loaded") });
        },
    };
}
