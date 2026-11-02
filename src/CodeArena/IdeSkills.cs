using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeArena;

/// <summary>
/// The IDE's Skills view and its chat's / menu: every skill the agent reads (read again first, so one added or changed
/// since is there), whose it is, and for the project's and the person's own the file the editor opens; the commands too. A
/// new one is made from a few lines to fill in: the project's (.arena/skills, in the repository) or the person's (the
/// config folder's skills, for every project).
/// </summary>
internal static partial class IdeSkills
{
    /// <summary>A skill's name as /name is typed, and its folder's.</summary>
    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,63}$")]
    private static partial Regex Name();

    public static JsonObject List(Runtime rt)
    {
        var x = rt.ReloadExtensions();
        return new JsonObject
        {
            ["skills"] = new JsonArray([.. x.Skills.Select(s => (JsonNode)Json(rt, s))]),
            ["commands"] = new JsonArray([.. x.Commands.Select(c => (JsonNode)new JsonObject { ["name"] = c.Name, ["description"] = c.Description, ["argumentHint"] = c.ArgumentHint })]),
        };
    }

    private static JsonObject Json(Runtime rt, Skill s) => new()
    {
        ["name"] = s.Name,
        ["description"] = s.Description,
        ["source"] = s.Source,
        ["path"] = PathOf(rt, s),
    };

    /// <summary>
    /// Where the IDE opens a skill's SKILL.md: the project's by its path in the folder, the person's as one of their own
    /// files (user:skills/NAME/SKILL.md); null for Arena's and Code Arena's own, which are not edited here.
    /// </summary>
    private static string? PathOf(Runtime rt, Skill s)
    {
        if (s.Source == "project" && rt.Workspace.Show(Path.Combine(s.Folder, "SKILL.md")) is var shown && !Path.IsPathRooted(shown))
        {
            return shown;
        }
        return s.Source == "person" && Name().IsMatch(Path.GetFileName(s.Folder)) ? $"user:skills/{Path.GetFileName(s.Folder)}/SKILL.md" : null;
    }

    /// <summary>A new skill, its SKILL.md with what to fill in; refused when one of that name is there already.</summary>
    public static JsonObject Create(Runtime rt, JsonObject body)
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
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "SKILL.md"),
            $"---\nname: {name}\ndescription: {description}\n---\nThe steps, as instructions to an assistant. $ARGUMENTS is what is typed after /{name}.\n");
        rt.ReloadExtensions();
        return Json(rt, new Skill(name, description, "", folder, scope!));
    }
}
