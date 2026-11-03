using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeArena;

/// <summary>
/// The IDE's Skills view and its chat's / menu (and /skills in the terminal): every skill the agent reads (read again
/// first, so one added or changed since is there; Arena's asked for again), whose it is, and for the project's and the
/// person's own the file the editor opens; the commands too. A new one is made from a few lines to fill in: the project's
/// (.arena/skills, in the repository) or the person's: one of their skills in Arena when signed in to it (the web chat,
/// Code Studio and Code Arena everywhere use it), else the config folder's skills (every project on this computer).
/// </summary>
internal static partial class IdeSkills
{
    /// <summary>A skill's name as /name is typed, and its folder's.</summary>
    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,63}$")]
    private static partial Regex Name();

    /// <summary>One of the person's skills as the editor names it (user:skills/NAME/SKILL.md).</summary>
    [GeneratedRegex("^skills/([a-z0-9][a-z0-9_-]{0,63})/SKILL\\.md$")]
    private static partial Regex SkillFile();

    /// <summary>A parameter in a SKILL.md's front matter, as Arena writes it: { name: N, description: "…", required: true, default: "…" }.</summary>
    [GeneratedRegex("""^\{\s*name:\s*(?<name>[A-Za-z][A-Za-z0-9_-]*)(?:\s*,\s*description:\s*"(?<description>[^"]*)")?(?:\s*,\s*required:\s*(?<required>true|false))?(?:\s*,\s*default:\s*"(?<default>[^"]*)")?\s*\}$""")]
    private static partial Regex Parameter();

    private static readonly StringComparison PathCompare = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Whose a skill is, as the Skills view and /skills group them: one of the person's own in Arena is theirs.</summary>
    public static string Whose(Runtime rt, Skill s) => s.Source == "arena" && rt.OwnArenaSkills.Contains(s.Name) ? "person" : s.Source;

    /// <summary>The skills and the commands; with <paramref name="fresh"/> (Refresh), Arena's asked for at once.</summary>
    public static async Task<JsonObject> ListAsync(Runtime rt, bool fresh, CancellationToken ct)
    {
        var x = await rt.ReloadExtensionsAsync(fresh, ct);
        return new JsonObject
        {
            ["skills"] = new JsonArray([.. x.Skills.Select(s => (JsonNode)Listed(rt, s))]),
            ["commands"] = new JsonArray([.. x.Commands.Select(c => (JsonNode)new JsonObject { ["name"] = c.Name, ["description"] = c.Description, ["argumentHint"] = c.ArgumentHint })]),
            // Where New Skill keeps one "for you": in Arena, or in the config folder on this computer.
            ["mineInArena"] = rt.MineInArena,
        };
    }

    private static JsonObject Listed(Runtime rt, Skill s) => new()
    {
        ["name"] = s.Name,
        ["description"] = s.Description,
        ["source"] = Whose(rt, s),
        ["path"] = PathOf(rt, s),
    };

    /// <summary>
    /// Where the IDE opens a skill's SKILL.md: the project's by its path in the folder (by its whole path when the folder is
    /// inside the repository, <see cref="ProjectSkillFile"/>), the person's as one of their own files
    /// (user:skills/NAME/SKILL.md, theirs in Arena too); null for the others in Arena and Code Arena's own, not edited here.
    /// </summary>
    private static string? PathOf(Runtime rt, Skill s) => Whose(rt, s) switch
    {
        "project" => rt.Workspace.Show(Path.Combine(s.Folder, "SKILL.md")),
        "person" when Name().IsMatch(Path.GetFileName(s.Folder)) => $"user:skills/{Path.GetFileName(s.Folder)}/SKILL.md",
        _ => null,
    };

    /// <summary>
    /// Whether a whole path is the SKILL.md of one of the project's skills the agent reads, and leads where it says (no link
    /// out of the repository): an IDE opened on a folder inside the repository reads and saves it too.
    /// </summary>
    public static bool ProjectSkillFile(Runtime rt, string whole) =>
        rt.ProjectRoot.Length > 0
        && rt.Extensions.Skills.Any(s => s.Source == "project" && string.Equals(Path.GetFullPath(Path.Combine(s.Folder, "SKILL.md")), whole, PathCompare))
        && Workspace.Physical(whole) is { } leads && Workspace.Within(leads, Workspace.Physical(rt.ProjectRoot) ?? rt.ProjectRoot);

    /// <summary>The first lines of a new skill's SKILL.md: what to fill in.</summary>
    private static string Steps(string name) => $"The steps, as instructions to an assistant. $ARGUMENTS is what is typed after /{name}.";

    /// <summary>
    /// A new skill, its SKILL.md with what to fill in; refused when one of that name is there already. The person's is made
    /// in Arena when signed in to it, else in the config folder.
    /// </summary>
    public static async Task<JsonObject> CreateAsync(Runtime rt, JsonObject body, CancellationToken ct)
    {
        var name = body.Str("name")?.Trim() ?? "";
        if (!Name().IsMatch(name))
        {
            throw new IdeError(400, "invalid", "A skill's name is lowercase letters, digits, - and _, up to 64 of them: /name uses it.");
        }
        var scope = body.Str("scope");
        var root = scope switch
        {
            "project" => Path.Combine(rt.ProjectRoot, ".arena", "skills"),
            "person" => Path.Combine(rt.Env.Paths.ConfigDir, "skills"),
            _ => throw new IdeError(400, "invalid", "A skill is the project's (project) or yours, for every project (person)."),
        };
        var folder = Path.Combine(root, name);
        if (Directory.Exists(folder))
        {
            throw new IdeError(409, "exists", $"There is a skill {name} there already: open it.");
        }
        var description = body.Str("description")?.Trim().ReplaceLineEndings(" ") is { Length: > 0 } said
            ? said
            : "When to use it, in a sentence: the model reads this to choose the skill.";
        if (scope == "person" && rt.MineInArena)
        {
            // One of theirs in Arena, as skill_save keeps one: the web chat and every Code Arena have it from there.
            await ArenaAsync(rt, HttpMethod.Post, "/api/code-arena/skills", new JsonObject { ["name"] = name, ["description"] = description, ["instructions"] = Steps(name) }, ct);
            var x = await rt.ReloadExtensionsAsync(true, ct);
            return Listed(rt, x.Skills.FirstOrDefault(s => s.Name == name && s.Source == "arena") ?? new Skill(name, description, "", Path.Combine(PluginSkills.Folder(rt.Env.Paths), name), "arena"));
        }
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "SKILL.md"), $"---\nname: {name}\ndescription: {description}\n---\n{Steps(name)}\n");
        rt.ReloadExtensions();
        return Listed(rt, new Skill(name, description, "", folder, scope!));
    }

    /// <summary>
    /// The SKILL.md kept here of one of the person's own skills in Arena, which the editor opens as one of their files
    /// (user:skills/NAME/SKILL.md) when there is none of that name on this computer; null for any other name.
    /// </summary>
    public static string? InArena(Runtime rt, string? name)
    {
        if (name is null || SkillFile().Match(name) is not { Success: true } m || !rt.OwnArenaSkills.Contains(m.Groups[1].Value)
            || File.Exists(Path.Combine(rt.Env.Paths.ConfigDir, "skills", m.Groups[1].Value, "SKILL.md")))
        {
            return null;
        }
        var kept = Path.Combine(PluginSkills.Folder(rt.Env.Paths), m.Groups[1].Value, "SKILL.md");
        return File.Exists(kept) ? kept : null;
    }

    /// <summary>
    /// A version of what Arena keeps by what it says, not when it was written here: Arena's skills are written again
    /// whenever any of them changes.
    /// </summary>
    private static string Version(string text) => "arena-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];

    /// <summary>One of the person's skills in Arena, as user files are read: its SKILL.md as Arena keeps it.</summary>
    public static JsonObject ReadArena(string name, string kept)
    {
        var text = File.ReadAllText(kept);
        return new JsonObject { ["name"] = name, ["text"] = text, ["version"] = Version(text), ["exists"] = true };
    }

    /// <summary>
    /// Saves one of the person's skills to Arena: its description, title, parameters and instructions as its SKILL.md has
    /// them (its name stays), unless it changed in Arena since the editor read it at <paramref name="version"/> (null: overwrite).
    /// </summary>
    public static async Task<JsonObject> SaveArenaAsync(Runtime rt, string name, string text, string? version, CancellationToken ct)
    {
        var skill = SkillFile().Match(name).Groups[1].Value;
        var fm = FrontMatter.Parse(text);
        if (fm.Field("name") is { } named && named != skill)
        {
            throw new IdeError(400, "invalid", $"A skill's name stays /{skill}: New Skill makes one of another name.");
        }
        if (fm.Body.Length == 0)
        {
            throw new IdeError(400, "invalid", "Write the skill's steps under its front matter (the --- lines).");
        }
        var parameters = new JsonArray();
        foreach (var line in fm.List("parameters"))
        {
            if (Parameter().Match(line.Trim()) is not { Success: true } p)
            {
                throw new IdeError(400, "invalid", $"A parameter is written - {{ name: NAME, description: \"…\", required: true, default: \"…\" }}, not {Fmt.OneLine(line, 80)}.");
            }
            parameters.Add(new JsonObject
            {
                ["name"] = p.Groups["name"].Value,
                ["description"] = p.Groups["description"].Success ? p.Groups["description"].Value : null,
                ["required"] = p.Groups["required"].Value != "false",
                ["default"] = p.Groups["default"].Success ? p.Groups["default"].Value : null,
            });
        }
        // Arena's as it is now: one changed on its Skills page since the editor read it is not overwritten.
        await rt.RefreshArenaSkillsAsync(true, ct);
        var kept = InArena(rt, name) ?? throw new IdeError(404, "not_found", $"/{skill} is not among your skills in Arena any more.");
        if (version is not null && Version(File.ReadAllText(kept)) != version)
        {
            throw new IdeError(409, "changed", $"/{skill} changed in Arena since it was opened. Reload it, or save again to overwrite it.");
        }
        var change = new JsonObject { ["description"] = fm.Field("description") ?? "", ["instructions"] = fm.Body, ["parameters"] = parameters };
        if (fm.Field("title") is { } title)
        {
            change["title"] = title;
        }
        await ArenaAsync(rt, HttpMethod.Put, "/api/code-arena/skills/" + skill, change, ct);
        await rt.ReloadExtensionsAsync(true, ct);
        return new JsonObject { ["name"] = name, ["version"] = Version(File.ReadAllText(InArena(rt, name) ?? kept)) };
    }

    /// <summary>Asks Arena with the person's key; its refusal is said as it words it.</summary>
    private static async Task ArenaAsync(Runtime rt, HttpMethod method, string path, JsonObject body, CancellationToken ct)
    {
        using var req = ChatSync.Request(method, rt.Config.Url!.TrimEnd('/') + path, rt.Config.ApiKey!, body);
        using var within = CancellationTokenSource.CreateLinkedTokenSource(ct);
        within.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var res = await rt.Http.SendAsync(req, within.Token);
            if (!res.IsSuccessStatusCode)
            {
                var answer = Json.ParseObject(await res.Content.ReadAsStringAsync(within.Token));
                var status = (int)res.StatusCode;
                // Arena's 401 is about the key: the IDE's own token is fine.
                throw new IdeError(status is 401 ? 403 : status >= 500 ? 502 : status, answer?.Str("status") ?? "arena", $"Arena: {answer?.Str("error") ?? $"HTTP {status}"}");
            }
        }
        catch (Exception e) when (e is HttpRequestException || e is OperationCanceledException && !ct.IsCancellationRequested)
        {
            throw new IdeError(502, "arena", $"Arena did not answer ({Fmt.OneLine(e.Message, 120)}): try again in a moment.");
        }
    }
}
