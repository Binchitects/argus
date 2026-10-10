using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeArena;

/// <summary>
/// Kept rules for tool calls: <c>tool</c> or <c>tool(pattern)</c>, the pattern matched (with <c>*</c> for anything) against
/// the call's command (run_shell) or path (the file tools, relative to the working directory). A deny refuses whatever the
/// mode; an allow runs without asking (a command Laya flags still asks). The person's config.json has both; a project's
/// .arena/settings.json may only deny: a repository someone cloned must not let itself run commands unasked.
/// </summary>
internal sealed partial class PermissionRules
{
    public List<string> Allow { get; } = [];
    public List<string> Deny { get; } = [];
    /// <summary>The project's denials (.arena/settings.json), apart: they are not the person's to change here.</summary>
    public List<string> ProjectDeny { get; } = [];

    /// <summary>The person's rules (config.json) and the project's denials (.arena/settings.json at its root).</summary>
    public static PermissionRules Of(Config config, string projectRoot)
    {
        var rules = new PermissionRules();
        rules.Allow.AddRange(config.Allow);
        rules.Deny.AddRange(config.Deny);
        rules.ProjectDeny.AddRange(List(ProjectSettings(projectRoot)?["deny"]));
        return rules;
    }

    public static PermissionRules From(JsonNode? personal, JsonNode? project)
    {
        var rules = new PermissionRules();
        rules.Allow.AddRange(List(personal?["allow"]));
        rules.Deny.AddRange(List(personal?["deny"]));
        rules.ProjectDeny.AddRange(List(project?["deny"]));
        return rules;
    }

    private static IEnumerable<string> List(JsonNode? node) =>
        (node as JsonArray ?? []).Select(n => n?.ToString().Trim() ?? "").Where(Valid);

    /// <summary>Whether a rule is written as one: a tool's name, and a pattern in brackets if any.</summary>
    public static bool Valid(string rule) => RuleShape().IsMatch(rule);

    [GeneratedRegex(@"^[A-Za-z0-9_\-.*]+(\(.*\))?$")]
    private static partial Regex RuleShape();

    /// <summary>The project's .arena/settings.json "permissions", if any.</summary>
    public static JsonNode? ProjectSettings(string root)
    {
        var file = Path.Combine(root, ".arena", "settings.json");
        try
        {
            return File.Exists(file) ? Json.ParseObject(File.ReadAllText(file))?["permissions"] : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>The denial that refuses this call, or null.</summary>
    public string? Denied(ToolDef tool, JsonObject args, Workspace workspace) =>
        Deny.Concat(ProjectDeny).FirstOrDefault(r => Matches(r, tool, args, workspace));

    /// <summary>The allowance that lets this call run unasked, or null.</summary>
    public string? Allowed(ToolDef tool, JsonObject args, Workspace workspace) =>
        Allow.FirstOrDefault(r => Matches(r, tool, args, workspace));

    internal static bool Matches(string rule, ToolDef tool, JsonObject args, Workspace workspace)
    {
        var open = rule.IndexOf('(', StringComparison.Ordinal);
        var name = open < 0 ? rule : rule[..open];
        if (!Glob(name, tool.Name))
        {
            return false;
        }
        if (open < 0)
        {
            return true;
        }
        var pattern = rule[(open + 1)..^1];
        var subject = Subject(tool, args, workspace);
        // A command is matched as a whole and command by command (a && b, a; b, a | b): "rm *" denies "make && rm -rf x".
        return subject is not null && (Glob(pattern, subject) || (tool.Kind == ToolKind.Shell && Parts(subject).Any(p => Glob(pattern, p))));
    }

    /// <summary>What a pattern is matched against: the command, or the path as the working directory sees it.</summary>
    private static string? Subject(ToolDef tool, JsonObject args, Workspace workspace)
    {
        if (tool.Kind == ToolKind.Shell)
        {
            return args.Str("command")?.Trim();
        }
        if (args.Str("path") is { } path)
        {
            try
            {
                return Path.GetRelativePath(workspace.Root, workspace.Resolve(path)).Replace('\\', '/');
            }
            catch (ToolError)
            {
                return path;
            }
        }
        return args.Str("pattern") ?? args.Str("query");
    }

    private static IEnumerable<string> Parts(string command) =>
        Regex.Split(command, @"\s*(?:&&|\|\||;|\||\n)\s*").Select(p => p.Trim()).Where(p => p.Length > 0);

    /// <summary>* is anything (also /), the rest as it is, the whole subject.</summary>
    internal static bool Glob(string pattern, string subject) =>
        Regex.IsMatch(subject, "^" + string.Join(".*", pattern.Split('*').Select(Regex.Escape)) + "$", RegexOptions.Singleline);

    public JsonObject ToJson() => new()
    {
        ["allow"] = new JsonArray([.. Allow.Select(r => (JsonNode)r)]),
        ["deny"] = new JsonArray([.. Deny.Select(r => (JsonNode)r)]),
    };
}
