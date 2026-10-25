using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeArena;

/// <summary>
/// The agent keeps what it worked out as a skill, to do it the same way next time: for this project, a SKILL.md in
/// .arena/skills/NAME/ (in the repository, shared with the team when committed; a better version replaces it); for the person,
/// one of their skills in Arena (on the Skills page, kept by them unless they keep what is learned on their own), which every
/// project and the web chat use. Sessions read skills as they start: a skill saved here is used from the next.
/// </summary>
internal static partial class SkillSave
{
    [GeneratedRegex("^[a-z][a-z0-9-]{1,39}$")]
    private static partial Regex Name();

    public static ToolDef Tool() => new()
    {
        Name = "skill_save",
        Kind = ToolKind.Edit,
        Description = "Keep a way of doing a task as a skill, to do it the same way next time: after working out a procedure worth repeating (several " +
            "steps: how to release, how to add an endpoint here, how to debug this kind of failure), or a better version of a skill that went wrong. " +
            "Instructions to an assistant, general enough to reuse (no one-off details). For this project (its .arena/skills/, in the repository), " +
            "or for the person (their skills in Arena, for every project and the web chat).",
        Parameters = (JsonObject)JsonNode.Parse("""
            {"type":"object","properties":{
              "name":{"type":"string","description":"Its slash name: lowercase letters, digits and dashes (e.g. release-a-version)."},
              "description":{"type":"string","description":"When to use it, in a sentence: the model reads it to choose the skill."},
              "instructions":{"type":"string","description":"The steps, as instructions to an assistant."},
              "scope":{"type":"string","enum":["project","person"],"description":"project (default): this repository's; person: the person's, everywhere."},
              "why":{"type":"string","description":"What it was learned from, or what was wrong with the version before."}},
             "required":["name","description","instructions"]}
            """)!,
        Summary = a => $"/{a.Str("name")} ({a.Str("scope") ?? "project"})",
        Run = async (a, c, ct) =>
        {
            var name = a.Str("name")?.Trim().TrimStart('/') ?? "";
            var description = a.Str("description")?.Trim() ?? "";
            var instructions = a.Str("instructions")?.Trim() ?? "";
            if (!Name().IsMatch(name))
            {
                throw new ToolError("The name is lowercase letters, digits and dashes, 2 to 40 of them (e.g. release-a-version).");
            }
            if (description.Length == 0 || instructions.Length < 40)
            {
                throw new ToolError("Say when to use it, and give its steps (at least a few lines).");
            }
            if (a.Str("scope") == "person")
            {
                if (c.Memory?.Arena is not { } arena)
                {
                    throw new ToolError("The person's skills are Arena's: this session is not signed in to Arena. Keep it for the project instead.");
                }
                var (text, error) = await arena.LearnSkillAsync(name, description, instructions, a.Str("why") ?? "Worked out in a Code Arena session", ct);
                return error ? throw new ToolError(text) : new ToolResult(text) { Display = c.Ui.Dim($"/{name}: {text}") };
            }
            var root = ProjectMemory.Root(c.Workspace.Root);
            var file = Path.Combine(root, ".arena", "skills", name, "SKILL.md");
            var replaced = File.Exists(file);
            c.BeforeWrite?.Invoke(file);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllTextAsync(file,
                $"---\nname: {name}\ndescription: {description.ReplaceLineEndings(" ")}\nlearned: {DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}, by Code Arena\n---\n\n{instructions}\n", ct);
            var shown = Path.GetRelativePath(c.Workspace.Root, file).Replace('\\', '/');
            return new ToolResult($"{(replaced ? "Replaced" : "Kept")} /{name} in {shown}: sessions use it from the next one (commit it to share it with the team).")
            {
                Display = c.Ui.Dim($"{shown} {(replaced ? "replaced" : "kept")}"),
            };
        },
    };
}
