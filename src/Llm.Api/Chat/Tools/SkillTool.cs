using System.Text;
using System.Text.Json.Nodes;
using Llm.Api.Access;
using Llm.Core.Data;

namespace Llm.Api.Chat.Tools;

/// <summary>
/// Skills the model uses on its own, as Claude Code's: the person's skills that say when they are for (their own, their
/// groups', the company's, their plugins') are listed with their descriptions, and when a question fits one the model
/// loads it with use_skill, its parameters filled from the question, and follows its instructions.
/// </summary>
public sealed class SkillTool(AccessService access, AppDbContext db) : IChatTool
{
    public const string ToolId = "skills";
    public const string Function = "use_skill";

    /// <summary>The most skills listed to the model: their descriptions are in every answer's instructions.</summary>
    public const int MostListed = 50;

    public string Id => ToolId;
    public string Title => "Skills";
    public string Description => "Uses your skills on its own when a question fits one (Workspace → Skills): those that say when they are for and are not kept for / only.";
    public string Icon => "sparkles";

    public Task<string?> UnavailableAsync(CancellationToken ct) => Task.FromResult<string?>(null);

    public async Task<IToolRun> StartAsync(ToolContext context, CancellationToken ct)
    {
        var skills = (await SkillEndpoints.UsableAsync(context.User, access, db, ct))
            .Where(s => s.AutoUse && s.Description.Length > 0).DistinctBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Take(MostListed).ToList();
        if (skills.Count == 0)
        {
            return new LocalRun([], null, (function, _, _) => Task.FromResult(new ToolResult($"There is no function {function}.", IsError: true)));
        }
        var list = new StringBuilder("You have skills: instructions for kinds of work, written for this person and their company. When a question fits a skill's ")
            .Append("description, call use_skill with its name (and its parameters, from the question) before you answer, then follow what it returns. ")
            .Append("Do not use one that does not fit; never say you have skills unless asked.\n<skills>\n");
        foreach (var s in skills)
        {
            var parameters = SkillLibrary.ParametersOf(s);
            list.Append("- ").Append(s.Name).Append(": ").Append(s.Description.ReplaceLineEndings(" "));
            if (parameters.Count > 0)
            {
                list.Append(" (parameters: ").AppendJoin(", ", parameters.Select(p => p.Required && p.Default is null ? p.Name : p.Name + "?")).Append(')');
            }
            list.Append('\n');
        }
        list.Append("</skills>");
        return new LocalRun(
            [
                Schema.Function(Function, "Loads one of your skills by its name, its parameters filled in, and returns its instructions to follow.",
                    new JsonObject
                    {
                        ["name"] = Schema.Text("The skill's name, as listed."),
                        ["parameters"] = new JsonObject
                        {
                            ["type"] = "object", ["description"] = "Its parameters' values, by name, from the question.",
                            ["additionalProperties"] = new JsonObject { ["type"] = "string" },
                        },
                        ["request"] = Schema.Text("What the person asked, in a line: the skill's $ARGUMENTS."),
                    }, "name"),
            ],
            list.ToString(),
            (function, args, _) => Task.FromResult(function == Function ? Use(skills, args) : new ToolResult($"There is no function {function}.", IsError: true)));
    }

    private static ToolResult Use(List<Core.Chat.Skill> skills, JsonObject args)
    {
        var name = Schema.Str(args, "name")?.Trim().TrimStart('/') ?? "";
        if (skills.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) is not { } skill)
        {
            return new ToolResult($"There is no skill {name}: use one of {string.Join(", ", skills.Select(s => s.Name))}.", IsError: true);
        }
        var given = (args["parameters"] as JsonObject ?? []).Where(p => p.Value is JsonValue).ToDictionary(p => p.Key, p => p.Value!.ToString(), StringComparer.Ordinal);
        var (call, problem) = SkillLibrary.Use(skill, given, Schema.Str(args, "request"));
        if (call is null)
        {
            return new ToolResult(problem + " Ask the person for it, or call use_skill again with it.", IsError: true);
        }
        return new ToolResult($"<skill name=\"{call.Name}\">\n{call.Instructions}\n</skill>\nFollow these instructions for the person's question.")
        {
            Details = new JsonObject { ["skill"] = new JsonObject { ["name"] = call.Name, ["title"] = call.Title, ["args"] = new JsonObject([.. call.Args.Select(a => KeyValuePair.Create(a.Key, (JsonNode?)a.Value))]) } },
        };
    }
}
