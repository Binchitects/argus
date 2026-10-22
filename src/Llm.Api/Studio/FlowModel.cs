using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Llm.Api.Studio;

/// <summary>A field of a form (or of a run started by hand).</summary>
public sealed class FlowField
{
    public string Name { get; set; } = "";
    public string Label { get; set; } = "";
    /// <summary>text, long or choice.</summary>
    public string Kind { get; set; } = "text";
    public List<string>? Choices { get; set; }
    public bool Required { get; set; }
}

/// <summary>What starts a workflow: by hand, a schedule, a webhook, GitLab, a room's message, or a form.</summary>
public sealed class FlowTrigger
{
    public string Kind { get; set; } = "manual";
    /// <summary>A schedule: five-field cron in <see cref="TimeZone"/>.</summary>
    public string? Cron { get; set; }
    public string TimeZone { get; set; } = "UTC";
    /// <summary>GitLab: merge_request, pipeline_failed, issue.</summary>
    public List<string>? Events { get; set; }
    /// <summary>A room: its id, and the word a message starts with to run it (#triage).</summary>
    public Guid? RoomId { get; set; }
    public string? Keyword { get; set; }
    /// <summary>A form (or a run by hand): its fields.</summary>
    public List<FlowField>? Fields { get; set; }
}

/// <summary>
/// A step. ask: one question to a model; agent: the model with tools, as a chat; tool: one tool's function; condition: then
/// or else; loop: its steps for each item; approval: waits for its owner; text: words made of the others; room, email,
/// comment, file: what it gives out.
/// </summary>
public sealed class FlowStep
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "ask";
    public string? Name { get; set; }
    public string? Prompt { get; set; }
    public string? System { get; set; }
    /// <summary>ask: answer as JSON, read into {{id.json…}}.</summary>
    public bool Json { get; set; }
    /// <summary>"small": the model for small steps.</summary>
    public string? Model { get; set; }
    public List<string>? Tools { get; set; }
    public string? Tool { get; set; }
    public string? Function { get; set; }
    public Dictionary<string, string>? Arguments { get; set; }
    public string? Left { get; set; }
    /// <summary>contains, not_contains, equals, not_equals, greater, less, empty, not_empty.</summary>
    public string? Op { get; set; }
    public string? Right { get; set; }
    public List<FlowStep>? Then { get; set; }
    public List<FlowStep>? Else { get; set; }
    public string? Over { get; set; }
    public int? Most { get; set; }
    public List<FlowStep>? Steps { get; set; }
    public string? Question { get; set; }
    public Guid? RoomId { get; set; }
    public string? Text { get; set; }
    public string? Subject { get; set; }
    public string? FileName { get; set; }
}

public sealed class Flow
{
    public FlowTrigger Trigger { get; set; } = new();
    public List<FlowStep> Steps { get; set; } = [];
}

/// <summary>Flows read, checked and written; templates filled ({{input.text}}, {{s1.text}}, {{s1.json.items.0.name}}, {{item}}).</summary>
public static partial class FlowModel
{
    public static readonly string[] Triggers = ["manual", "schedule", "webhook", "gitlab", "room", "form"];
    public static readonly string[] Kinds = ["ask", "agent", "tool", "condition", "loop", "approval", "text", "room", "email", "comment", "file"];
    public static readonly string[] Ops = ["contains", "not_contains", "equals", "not_equals", "greater", "less", "empty", "not_empty"];
    public const int MostSteps = 60;
    public const int MostTurns = 50;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static Flow Read(string json) => JsonSerializer.Deserialize<Flow>(json, Json) ?? new Flow();

    public static string Write(Flow flow) => JsonSerializer.Serialize(flow, Json);

    /// <summary>Every step, inner ones too, depth first.</summary>
    public static IEnumerable<FlowStep> All(IEnumerable<FlowStep> steps)
    {
        foreach (var s in steps)
        {
            yield return s;
            foreach (var inner in All((s.Then ?? []).Concat(s.Else ?? []).Concat(s.Steps ?? [])))
            {
                yield return inner;
            }
        }
    }

    /// <summary>Why the flow cannot be kept, or null: its trigger's settings, each step's kind and what it needs, ids unique.</summary>
    public static string? Check(Flow flow)
    {
        var t = flow.Trigger;
        if (!Triggers.Contains(t.Kind))
        {
            return $"A trigger is one of: {string.Join(", ", Triggers)}.";
        }
        if (t.Kind == "schedule" && Schedules.Cron.Parse(t.Cron).Problem is { } cron)
        {
            return $"The schedule: {cron}";
        }
        if (t.Kind == "room" && (t.RoomId is null || string.IsNullOrWhiteSpace(t.Keyword)))
        {
            return "A room trigger names its room and the word a message starts with to run it.";
        }
        if (t.Kind == "gitlab" && (t.Events is not { Count: > 0 } || t.Events.Any(e => !Schedules.Triggers.GitLabEvents.Contains(e))))
        {
            return $"A GitLab trigger takes some of: {string.Join(", ", Schedules.Triggers.GitLabEvents)}.";
        }
        foreach (var f in t.Fields ?? [])
        {
            if (!FieldName().IsMatch(f.Name) || f.Name == "text" || f.Kind is not ("text" or "long" or "choice") || (f.Kind == "choice" && f.Choices is not { Count: > 0 }))
            {
                return $"A field needs a name of letters and digits (not \"text\"), and is text, long or choice (with choices): {f.Name}.";
            }
        }
        var all = All(flow.Steps).ToList();
        if (all.Count == 0)
        {
            return "A workflow needs a step.";
        }
        if (all.Count > MostSteps)
        {
            return $"A workflow has at most {MostSteps} steps.";
        }
        if (all.Select(s => s.Id).Distinct().Count() != all.Count || all.Any(s => !StepId().IsMatch(s.Id) || s.Id is "input" or "item" or "index" or "now"))
        {
            return "Each step has its own id: letters and digits, starting with a letter (s1, summary).";
        }
        foreach (var s in all)
        {
            var name = s.Name ?? s.Id;
            var problem = s.Kind switch
            {
                _ when !Kinds.Contains(s.Kind) => $"A step is one of: {string.Join(", ", Kinds)}.",
                "ask" or "agent" when string.IsNullOrWhiteSpace(s.Prompt) => "it needs a prompt",
                "tool" when string.IsNullOrWhiteSpace(s.Tool) || string.IsNullOrWhiteSpace(s.Function) => "it needs a tool and its function",
                "condition" when !Ops.Contains(s.Op) => $"its test is one of: {string.Join(", ", Ops)}",
                "loop" when string.IsNullOrWhiteSpace(s.Over) || s.Steps is not { Count: > 0 } => "it needs what to go over and steps to run for each",
                "room" when s.RoomId is null || string.IsNullOrWhiteSpace(s.Text) => "it needs its room and what to say",
                "text" or "email" or "comment" or "file" when string.IsNullOrWhiteSpace(s.Text) => "it needs its text",
                _ => null,
            };
            if (problem is not null)
            {
                return problem.StartsWith("A step", StringComparison.Ordinal) ? problem : $"Step {name}: {problem}.";
            }
        }
        return null;
    }

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]{0,39}$")]
    private static partial Regex StepId();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]{0,39}$")]
    private static partial Regex FieldName();

    [GeneratedRegex(@"\{\{\s*([A-Za-z][A-Za-z0-9_]*(?:\.[A-Za-z0-9_]+)*)\s*\}\}")]
    private static partial Regex Placeholder();

    /// <summary>The value at a path ({{s1.json.items.0.name}}) in the run's values, or null.</summary>
    public static JsonNode? Find(IReadOnlyDictionary<string, JsonNode?> values, string path)
    {
        var parts = path.Split('.');
        if (!values.TryGetValue(parts[0], out var node))
        {
            return null;
        }
        foreach (var part in parts.Skip(1))
        {
            node = node switch
            {
                JsonObject o => o[part],
                JsonArray a when int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var i) && i < a.Count => a[i],
                _ => null,
            };
            if (node is null)
            {
                return null;
            }
        }
        return node;
    }

    /// <summary>A value as words: text as it is, anything else as JSON.</summary>
    public static string Show(JsonNode? node) => node switch
    {
        null => "",
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        _ => node.ToJsonString(Json),
    };

    /// <summary>A template with its {{paths}} filled from the run's values; an unknown one is left empty.</summary>
    public static string Fill(string? template, IReadOnlyDictionary<string, JsonNode?> values) =>
        template is null ? "" : Placeholder().Replace(template, m => Show(Find(values, m.Groups[1].Value)));

    /// <summary>What a loop goes over: a {{path}} to a JSON list, JSON text of a list, else one item a line.</summary>
    public static List<JsonNode?> Items(string? over, IReadOnlyDictionary<string, JsonNode?> values)
    {
        var text = over?.Trim() ?? "";
        if (Placeholder().Match(text) is { Success: true } only && only.Length == text.Length && Find(values, only.Groups[1].Value) is JsonArray list)
        {
            return [.. list.Select(i => i?.DeepClone())];
        }
        var filled = Fill(text, values).Trim();
        if (filled.StartsWith('['))
        {
            try
            {
                if (JsonNode.Parse(filled) is JsonArray parsed)
                {
                    return [.. parsed.Select(i => i?.DeepClone())];
                }
            }
            catch (JsonException)
            {
            }
        }
        return [.. filled.Split('\n').Select(l => l.Trim().TrimStart('-', '*', '•').Trim()).Where(l => l.Length > 0).Select(l => (JsonNode?)JsonValue.Create(l))];
    }

    /// <summary>A condition's test.</summary>
    public static bool Test(string left, string? op, string right) => op switch
    {
        "contains" => left.Contains(right, StringComparison.OrdinalIgnoreCase),
        "not_contains" => !left.Contains(right, StringComparison.OrdinalIgnoreCase),
        "equals" => string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase),
        "not_equals" => !string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase),
        "greater" => Number(left) is { } a && Number(right) is { } b && a > b,
        "less" => Number(left) is { } a && Number(right) is { } b && a < b,
        "empty" => left.Trim().Length == 0,
        "not_empty" => left.Trim().Length > 0,
        _ => false,
    };

    private static double? Number(string s) => double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    /// <summary>JSON in a model's answer (in a code fence or not), or null.</summary>
    public static JsonNode? JsonIn(string text)
    {
        var start = text.IndexOfAny(['{', '[']);
        var end = Math.Max(text.LastIndexOf('}'), text.LastIndexOf(']'));
        if (start < 0 || end <= start)
        {
            return null;
        }
        try
        {
            return JsonNode.Parse(text[start..(end + 1)], null, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The steps' outline, for a run's summary: "ask s1 → condition s2 (then: room s3)".</summary>
    public static string Outline(IEnumerable<FlowStep> steps)
    {
        var w = new StringBuilder();
        foreach (var s in steps)
        {
            if (w.Length > 0)
            {
                w.Append(" → ");
            }
            w.Append(s.Kind).Append(' ').Append(s.Id);
        }
        return w.ToString();
    }
}
