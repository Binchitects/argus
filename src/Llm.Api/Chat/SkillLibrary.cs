using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Llm.Api.Plugins;
using Llm.Core.Chat;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Chat;

/// <summary>A skill a plugin brings: one of its Markdown files, with a front matter that names it.</summary>
public sealed record PluginSkillFile(string Name, string Title, string Description, string Instructions, IReadOnlyList<SkillParameter> Parameters);

/// <summary>
/// A skill's parameter: its name (as {{name}} in the instructions), what it is, whether it must be given, and what it is
/// when it is not.
/// </summary>
public sealed record SkillParameter(string Name, string? Description = null, bool Required = true, string? Default = null);

/// <summary>A skill used: its name, and its parameters' values (by name); <see cref="Extra"/>, what was typed after it ($ARGUMENTS).</summary>
public sealed record SkillUse(string? Name, IReadOnlyDictionary<string, string>? Args = null, string? Extra = null);

/// <summary>
/// A skill as a question carries it: which, what it was given, and its instructions as filled in then; <see cref="TookWords"/>,
/// whether they hold the person's words already ($ARGUMENTS).
/// </summary>
public sealed record SkillCall(string Name, string Title, IReadOnlyDictionary<string, string> Args, string Instructions, bool TookWords = false);

/// <summary>
/// The skills' rules: a slash name, a title, a description of when it is for, instructions with {{parameters}} and
/// $ARGUMENTS, and the parameters themselves (declared, or found in the instructions); the skills plugins bring, read
/// from their files and kept with their tool row; and a skill used, its instructions filled in for the model.
/// </summary>
public static partial class SkillLibrary
{
    public const int MaxName = 40;
    public const int MaxTitle = 100;
    public const int MaxDescription = 1000;
    public const int MaxText = 20_000;
    public const int MaxParameters = 20;
    public const string Arguments = "$ARGUMENTS";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    /// <summary>The {{variables}} of instructions, in the order they first appear: "{{file}}" is "file".</summary>
    public static List<string> Variables(string text) =>
        [.. Variable().Matches(text).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal)];

    /// <summary>A skill's parameters: those it declares, then each {{variable}} of its instructions it does not (required).</summary>
    public static List<SkillParameter> ParametersOf(Skill skill)
    {
        var declared = Read(skill.Parameters);
        return [.. declared, .. Variables(skill.Instructions).Where(v => declared.All(d => d.Name != v)).Select(v => new SkillParameter(v))];
    }

    public static List<SkillParameter> Read(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<SkillParameter>>(json, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string Write(IEnumerable<SkillParameter> parameters) => JsonSerializer.Serialize(parameters, Json);

    /// <summary>Why these parameters will not do (names as {{name}} takes them, once each, at most <see cref="MaxParameters"/>), or null.</summary>
    public static string? ParametersProblem(IReadOnlyList<SkillParameter> parameters)
    {
        if (parameters.Count > MaxParameters)
        {
            return $"A skill takes at most {MaxParameters} parameters.";
        }
        foreach (var p in parameters)
        {
            if (!ParameterName().IsMatch(p.Name ?? ""))
            {
                return $"\"{p.Name}\" is not a parameter's name: a letter, then letters, digits, _ or -, up to 40.";
            }
            if ((p.Description?.Length ?? 0) > 300 || (p.Default?.Length ?? 0) > 2000)
            {
                return $"{p.Name}: a description of up to 300 characters, a default of up to 2,000.";
            }
        }
        return parameters.GroupBy(p => p.Name).FirstOrDefault(g => g.Count() > 1) is { } twice ? $"The parameter {twice.Key} is there twice." : null;
    }

    /// <summary>The text with its variables filled in from <paramref name="values"/>; a variable not given is left empty.</summary>
    public static string Fill(string text, IReadOnlyDictionary<string, string> values) =>
        Variable().Replace(text, m => values.GetValueOrDefault(m.Groups[1].Value, ""));

    /// <summary>
    /// A skill used: its parameters' values (a default for one not given; a required one missing is the problem), its
    /// instructions filled in with them and with what was typed after it ($ARGUMENTS, or added at the end when the
    /// instructions do not name it).
    /// </summary>
    public static (SkillCall? Call, string? Problem) Use(Skill skill, IReadOnlyDictionary<string, string>? given, string? extra)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in ParametersOf(skill))
        {
            var value = given?.GetValueOrDefault(p.Name)?.Trim();
            if (string.IsNullOrEmpty(value))
            {
                value = p.Default;
            }
            if (string.IsNullOrEmpty(value) && p.Required)
            {
                return (null, $"/{skill.Name} needs {p.Name}{(p.Description is { Length: > 0 } d ? $" ({d})" : "")}.");
            }
            values[p.Name] = value ?? "";
        }
        var text = Fill(skill.Instructions, values);
        var took = text.Contains(Arguments, StringComparison.Ordinal);
        return (new SkillCall(skill.Name, skill.Title, values, took ? text.Replace(Arguments, extra?.Trim() ?? "", StringComparison.Ordinal) : text, took), null);
    }

    /// <summary>
    /// What the model reads for a question that used a skill: the skill and what it was given, its instructions, then the
    /// person's own words unless the instructions took them (none: the skill is the whole question).
    /// </summary>
    public static string ForModel(SkillCall call, string words)
    {
        var given = call.Args.Count > 0 ? " with " + string.Join("; ", call.Args.Select(a => $"{a.Key}: {a.Value}")) : "";
        var asked = !call.TookWords && words.Trim().Length > 0 ? $"\n\nTheir words: {words.Trim()}" : "";
        return $"The person used the skill /{call.Name} ({call.Title}){given}. Follow its instructions:\n\n<skill name=\"{call.Name}\">\n{call.Instructions}\n</skill>{asked}";
    }

    public static string Keep(SkillCall call) => JsonSerializer.Serialize(call, Json);

    public static SkillCall? Kept(string? json)
    {
        if (json is null)
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<SkillCall>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Why a slash name will not do, or null.</summary>
    public static string? NameProblem(string name) => SlashName().IsMatch(name)
        ? null
        : $"\"/{name}\" is not a slash name: lowercase letters, digits, - and _, up to {MaxName} (like /review).";

    /// <summary>
    /// A plugin's skill for the chat, from its SKILL.md (its name and description read already): the front matter's title
    /// and parameters, then its instructions; null when its name is not a slash name or it says nothing.
    /// <code>
    /// ---
    /// name: triage
    /// title: Triage an issue
    /// description: Sort a new issue by product area and severity.
    /// parameters: [{ name: issue, description: the issue's number }]
    /// ---
    /// Triage issue #{{issue}} in {{project}}…
    /// </code>
    /// </summary>
    public static PluginSkillFile? FromSkill(string folder, string skillMd, string name, string description)
    {
        if (NameProblem(name) is not null)
        {
            return null;
        }
        var text = skillMd.Replace("\r\n", "\n", StringComparison.Ordinal);
        var front = new JsonObject();
        if (text.StartsWith("---\n", StringComparison.Ordinal) && text.IndexOf("\n---", 3, StringComparison.Ordinal) is var end and > 0)
        {
            try
            {
                front = Tools.OpenApi.Parse(text[4..end]);
            }
            catch (Tools.OpenApi.SpecException)
            {
                // A front matter only Code Arena reads: its name and description were read line by line.
            }
            text = text[(end + 4)..].TrimStart('-');
        }
        var body = text.Trim();
        if (body.Length is 0 or > MaxText)
        {
            return null;
        }
        var title = (front["title"] as JsonValue)?.ToString().Trim() is { Length: > 0 } t ? t[..Math.Min(MaxTitle, t.Length)] : name;
        var parameters = front["parameters"] is JsonArray a ? Read(a.ToJsonString()) : [];
        if (ParametersProblem(parameters) is { } bad)
        {
            throw new PluginCatalog.PluginException($"{folder}/SKILL.md: {bad}");
        }
        return new PluginSkillFile(name, title, description.Length > MaxDescription ? description[..MaxDescription] : description, body, parameters);
    }

    /// <summary>A plugin's skills for the chat: those of its skills whose names are slash names.</summary>
    public static List<PluginSkillFile> ChatSkillsOf(PluginPackage package) => [.. package.Skills.Select(s => s.Chat).OfType<PluginSkillFile>()];

    /// <summary>A plugin installed or updated: its skills are the package's now, for everyone who may use its tool.</summary>
    public static async Task SyncAsync(AppDbContext db, McpServer server, PluginPackage package, CancellationToken ct)
    {
        await db.Skills.Where(p => p.ServerId == server.Id).ExecuteDeleteAsync(ct);
        db.Skills.AddRange(ChatSkillsOf(package).Select(p => new Skill
        {
            Name = p.Name, Title = p.Title, Description = p.Description, Instructions = p.Instructions, Parameters = Write(p.Parameters), Sharing = SkillSharing.Company,
            ServerId = server.Id,
        }));
        await db.SaveChangesAsync(ct);
    }

    [GeneratedRegex(@"\{\{\s*([A-Za-z][A-Za-z0-9_-]{0,39})\s*\}\}")]
    private static partial Regex Variable();

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,39}$")]
    private static partial Regex SlashName();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_-]{0,39}$")]
    private static partial Regex ParameterName();
}
