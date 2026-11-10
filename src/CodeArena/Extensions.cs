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

    private static string Unquote(string v) =>
        v.Length >= 2 && v[0] == '"' && v[^1] == '"' ? Unescape(v[1..^1]) : v.Length >= 2 && v[0] == '\'' && v[^1] == '\'' ? v[1..^1] : v;

    /// <summary>What is in double quotes as it is meant: \" is a quote and \\ a backslash, as Arena writes a skill's fields.</summary>
    public static string Unescape(string v) => v.Contains('\\', StringComparison.Ordinal) ? Regex.Replace(v, @"\\([""\\])", "$1") : v;

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

/// <summary>
/// A skill (agentskills.io's SKILL.md): instructions loaded when the task needs them, with the files beside them. Whose it
/// is: the project's, the person's (their config folder, ~/.code-arena/skills, ~/.claude/skills), Arena's (made there, or a
/// plugin's) or Code Arena's own (builtin); <see cref="Origin"/>, the folder it was read from (.claude/skills, ~/.claude/skills…).
/// </summary>
internal sealed record Skill(string Name, string Description, string Instructions, string Folder, string Source = "project", string? Origin = null)
{
    /// <summary>The instructions as the model is given them: parameters named for it to take from the request (BuiltInSkills.Filled).</summary>
    public string Filled(string? arguments = null)
    {
        var skillMd = Path.Combine(Folder, "SKILL.md");
        var instructions = arguments is null ? Instructions : Instructions.Replace("$ARGUMENTS", arguments, StringComparison.Ordinal);
        return File.Exists(skillMd) ? BuiltInSkills.Filled(File.ReadAllText(skillMd), instructions) : instructions;
    }

    /// <summary>
    /// What goes with a message that used the skill (/name and the words after it), as what went with it (Mentions' section):
    /// the page shows what was typed, the model reads the instructions after it, the words in them for $ARGUMENTS.
    /// </summary>
    public string Use(string typed, string words) => UseAll(typed, words, [this]);

    /// <summary>What was typed, with each skill's instructions after it (several used together: each where it applies).</summary>
    public static string UseAll(string typed, string words, IReadOnlyList<Skill> skills)
    {
        var said = skills.Count == 1
            ? $"The person used the skill /{skills[0].Name} ({skills[0].Description}). Follow its instructions:"
            : $"The person used {skills.Count} skills together ({string.Join(", ", skills.Select(k => "/" + k.Name))}). Follow all of their instructions, each where it applies:";
        var blocks = string.Join("\n", skills.Select(k => $"<skill name=\"{k.Name}\">\n{(skills.Count == 1 ? said + "\n\n" : "")}{k.Filled(words)}\n</skill>"));
        return typed + Mentions.Marker + (skills.Count == 1 ? "\n" + blocks : $"\n{said}\n\n{blocks}") + Mentions.End;
    }
}

/// <summary>
/// The person's and the project's own commands, sub-agents and skills: Markdown files under .arena/ (and .code-arena/) in the
/// project and in the config folder; those written for Claude Code (.claude/) and Qwen Code (.qwen/) are read too, and the
/// skills in the person's home (~/.code-arena/skills, ~/.claude/skills). The project's win over the person's of the same
/// name, and both over the skills Arena's plugins bring (PluginSkills).
/// </summary>
internal sealed class Extensions
{
    public IReadOnlyList<CustomCommand> Commands { get; private init; } = [];
    public IReadOnlyList<CustomAgent> Agents { get; private init; } = [];
    public IReadOnlyList<Skill> Skills { get; private init; } = [];
    /// <summary>Files that could not be read, with why.</summary>
    public IReadOnlyList<string> Problems { get; private init; } = [];

    private static readonly string[] ProjectFolders = [".code-arena", ".arena", ".claude", ".qwen"];

    /// <summary>
    /// The person's own folders in their home, after the config folder's: Code Arena's and Claude Code's, read as a project's
    /// own are (their commands, agents and skills), for every project.
    /// </summary>
    private static readonly string[] HomeFolders = [".code-arena", ".claude"];

    /// <summary>The folders read, the project's first (they win), whose each is, and how it is shown.</summary>
    private static IEnumerable<(string Dir, string Source, string Origin)> Folders(AppPaths paths, string projectRoot, string kind) =>
        ProjectFolders.Select(f => (Path.Combine(projectRoot, f, kind), "project", $"{f}/{kind}")).Append((Path.Combine(paths.ConfigDir, kind), "person", "config"))
            .Concat(paths.Home is { Length: > 0 } home ? HomeFolders.Select(f => (Path.Combine(home, f, kind), "person", $"~/{f}/{kind}")) : [])
            // The skills Arena's plugins bring come after the project's and the person's own, and Arena's own last: those of the same name win.
            .Concat(kind == "skills" ? [(PluginSkills.Folder(paths), "arena", "arena"), (BuiltInSkills.Folder(paths), "builtin", "builtin")] : []);

    public static Extensions Load(AppPaths paths, string projectRoot)
    {
        var problems = new List<string>();
        var commands = new Dictionary<string, CustomCommand>(StringComparer.OrdinalIgnoreCase);
        foreach (var (dir, _, _) in Folders(paths, projectRoot, "commands").Where(f => Directory.Exists(f.Dir)))
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
        foreach (var (dir, _, _) in Folders(paths, projectRoot, "agents").Where(f => Directory.Exists(f.Dir)))
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
        foreach (var (dir, source, origin) in Folders(paths, projectRoot, "skills").Where(f => Directory.Exists(f.Dir)))
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
                    skills[name] = new Skill(name, fm.Field("description") ?? FirstLine(fm.Body), fm.Body, folder, source, origin);
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

    /// <summary>
    /// The message a typed /name args stands for: a command's prompt, or what was typed with a skill's instructions after it
    /// (<see cref="Skill.Use"/>); null when no command or skill has that name. A command wins over a skill of its name.
    /// </summary>
    public string? Expand(string input)
    {
        if (!input.StartsWith('/'))
        {
            return null;
        }
        var parts = input[1..].Split([' ', '\n'], 2);
        var words = parts.Length > 1 ? parts[1].Trim() : "";
        if (Commands.FirstOrDefault(c => string.Equals(c.Name, parts[0], StringComparison.OrdinalIgnoreCase)) is { } command)
        {
            return command.Fill(words);
        }
        // Skills one after another at the start (/review /tests the parser): all of them, with the words after them.
        var (skills, rest) = SkillsTyped(input);
        return skills.Count > 0 ? Skill.UseAll(input, rest, skills) : null;
    }

    /// <summary>The most skills one message may use.</summary>
    public const int MostSkills = 5;

    /// <summary>
    /// The skills a message starts with, one after another (each /name once, at most <see cref="MostSkills"/>), and the words
    /// after them; none when it starts with no skill (or with a command, which goes first).
    /// </summary>
    public (List<Skill> Skills, string Words) SkillsTyped(string input)
    {
        var skills = new List<Skill>();
        var at = 0;
        while (at < input.Length && input[at] == '/' && skills.Count < MostSkills)
        {
            var end = input.IndexOfAny([' ', '\n'], at);
            var name = (end < 0 ? input[(at + 1)..] : input[(at + 1)..end]);
            if (skills.Count == 0 && Commands.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
                || Skills.FirstOrDefault(k => string.Equals(k.Name, name, StringComparison.OrdinalIgnoreCase)) is not { } skill)
            {
                break;
            }
            if (!skills.Contains(skill))
            {
                skills.Add(skill);
            }
            at = end < 0 ? input.Length : end;
            while (at < input.Length && input[at] is ' ' or '\n')
            {
                at++;
            }
        }
        return (skills, skills.Count == 0 ? "" : input[at..].Trim());
    }

    /// <summary>The skill a typed /name args uses, or null (none of that name, or a command of that name goes first).</summary>
    public Skill? SkillTyped(string input)
    {
        if (!input.StartsWith('/'))
        {
            return null;
        }
        var name = input[1..].Split([' ', '\n'], 2)[0];
        return Commands.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
            ? null
            : Skills.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
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
            var sb = new StringBuilder(skill.Filled());
            if (files.Count > 0)
            {
                sb.Append("\n\nFiles of this skill (skill with file reads one): ").Append(string.Join(", ", files));
            }
            return Task.FromResult(new ToolResult(sb.ToString()) { Display = c.Ui.Dim($"skill {skill.Name} loaded") });
        },
    };
}
