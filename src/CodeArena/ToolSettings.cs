using System.Text.Json.Nodes;

namespace CodeArena;

/// <summary>
/// One tool's settings, as the web's tool customisation has them: on or off, how its calls ask (ask: every call; allow:
/// none; default: by its kind and the mode), its description for the model in place of its own, arguments it always gets
/// (fixed: the model is not offered them) and defaults for those the model leaves out.
/// </summary>
internal sealed record ToolChoice(bool? Enabled = null, string? Approval = null, string? Description = null, JsonObject? Defaults = null, JsonObject? Fixed = null)
{
    public static readonly string[] Approvals = ["default", "ask", "allow"];

    public static ToolChoice From(JsonNode? node) => node is JsonObject o
        ? new ToolChoice(o.Bool("enabled"), o.Str("approval") is { } a && Approvals.Contains(a) && a != "default" ? a : null, o.Str("description") is { Length: > 0 } d ? d : null,
            o["defaults"] is JsonObject { Count: > 0 } defaults ? defaults.Clone() : null, o["fixed"] is JsonObject { Count: > 0 } f ? f.Clone() : null)
        : new ToolChoice();

    public bool Empty => Enabled is null && Approval is null && Description is null && Defaults is null && Fixed is null;

    public JsonObject Json()
    {
        var o = new JsonObject();
        if (Enabled is { } e)
        {
            o["enabled"] = e;
        }
        if (Approval is { } a)
        {
            o["approval"] = a;
        }
        if (Description is { } d)
        {
            o["description"] = d;
        }
        if (Defaults is { } defaults)
        {
            o["defaults"] = defaults.Clone();
        }
        if (Fixed is { } f)
        {
            o["fixed"] = f.Clone();
        }
        return o;
    }
}

/// <summary>
/// The tools' settings, as the web's tool customisation (Admin → Tools) has them, for Code Arena's tools: the built-in ones,
/// Arena's, the MCP servers' and the person's and the project's own. Kept for the person (config.json "toolSettings", every
/// project), for the person in this project (on this computer, never in the project), and by the project
/// (.arena/settings.json "tools", committed: it may only turn a tool off or make it ask, never let one run unasked). The
/// person's for this project win over theirs for every project. Arena's tools are as Arena's admins set them (those turned
/// off are not served, those that ask first ask here too): the person can make them ask, not run unasked.
/// </summary>
internal sealed class ToolSettings
{
    private readonly object _gate = new();
    private Dictionary<string, ToolChoice> _person = [];
    private Dictionary<string, ToolChoice> _here = [];
    private Dictionary<string, ToolChoice> _project = [];
    private int _version;

    /// <summary>One more each time the settings change: the tool box applies them anew.</summary>
    public int Version => Volatile.Read(ref _version);

    /// <summary>Where they are kept: the config file, this project's state on this computer, the project's settings file.</summary>
    public string? ConfigFile { get; init; }
    public ProjectToolState? Here { get; init; }
    public string? ProjectRoot { get; init; }

    /// <summary>Told when the settings changed (the IDE's page shows them anew).</summary>
    public Action? Changed { get; set; }

    private static Dictionary<string, ToolChoice> Read(JsonObject? o) =>
        (o ?? []).Where(p => p.Value is JsonObject).ToDictionary(p => p.Key, p => ToolChoice.From(p.Value), StringComparer.Ordinal);

    /// <summary>Reads them all again: from the config file, this project's state, the project's settings file.</summary>
    public void Reload(JsonObject? person)
    {
        var project = ProjectRoot is { Length: > 0 } root ? ProjectFile(root) : null;
        lock (_gate)
        {
            _person = Read(person);
            _here = Read(Here?.Tools);
            // The project's may only turn off or make ask: whatever else it says is not read.
            _project = Read(project).ToDictionary(p => p.Key, p => new ToolChoice(p.Value.Enabled == false ? false : null, p.Value.Approval == "ask" ? "ask" : null), StringComparer.Ordinal);
            _version++;
        }
    }

    /// <summary>The project's .arena/settings.json "tools", if any.</summary>
    public static JsonObject? ProjectFile(string root)
    {
        var file = Path.Combine(root, ".arena", "settings.json");
        try
        {
            return File.Exists(file) ? Json.ParseObject(File.ReadAllText(file))?["tools"] as JsonObject : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>The settings of a tool at each level: the person's for every project, theirs here, the project's.</summary>
    public (ToolChoice Person, ToolChoice Here, ToolChoice Project) Of(string name)
    {
        lock (_gate)
        {
            return (_person.GetValueOrDefault(name) ?? new(), _here.GetValueOrDefault(name) ?? new(), _project.GetValueOrDefault(name) ?? new());
        }
    }

    /// <summary>Why the person may not let it run unasked, or null: one that asks every time, or Arena's that asks first.</summary>
    public static string? AllowLocked(ToolDef t) => t.AlwaysAsks ? "It asks every time: it stops what the person let run."
        : t.Kind == ToolKind.Remote && t.Server == Runtime.ArenaName && !t.Trusted ? "Arena's admins set it to ask before each call."
        : null;

    /// <summary>The settings in force: the person's here over theirs everywhere; the project's off and ask over both.</summary>
    public ToolChoice Effective(ToolDef t)
    {
        var (person, here, project) = Of(t.Name);
        var enabled = project.Enabled == false ? false : here.Enabled ?? person.Enabled;
        var approval = project.Approval ?? here.Approval ?? person.Approval;
        if (approval == "allow" && AllowLocked(t) is not null)
        {
            approval = null;
        }
        static JsonObject? Merge(JsonObject? a, JsonObject? b)
        {
            if (a is null || b is null)
            {
                return (a ?? b)?.Clone();
            }
            var merged = a.Clone();
            foreach (var (k, v) in b)
            {
                merged[k] = v?.DeepClone();
            }
            return merged;
        }
        return new ToolChoice(enabled, approval, here.Description ?? person.Description, Merge(person.Defaults, here.Defaults), Merge(person.Fixed, here.Fixed));
    }

    /// <summary>The tool as its settings make it (null: turned off); itself when none apply.</summary>
    public ToolDef? Apply(ToolDef t)
    {
        var c = Effective(t);
        if (c.Enabled == false)
        {
            return null;
        }
        if (c.Empty || c is { Enabled: true, Approval: null, Description: null, Defaults: null, Fixed: null })
        {
            return t;
        }
        var copy = t.Copy();
        copy.Approval = c.Approval;
        if (c.Description is { } description)
        {
            copy.Description = description;
        }
        var properties = t.Parameters["properties"] as JsonObject;
        var fixedArgs = (c.Fixed ?? []).Where(p => properties?.ContainsKey(p.Key) == true).ToDictionary(p => p.Key, p => p.Value);
        var defaults = (c.Defaults ?? []).Where(p => properties?.ContainsKey(p.Key) == true && !fixedArgs.ContainsKey(p.Key)).ToDictionary(p => p.Key, p => p.Value);
        if (fixedArgs.Count + defaults.Count > 0)
        {
            // A fixed argument is not offered to the model; a default is said in its description, and fills one left out.
            var schema = t.Parameters.Clone();
            var props = (JsonObject)schema["properties"]!;
            foreach (var key in fixedArgs.Keys)
            {
                props.Remove(key);
            }
            if (schema["required"] is JsonArray required)
            {
                // Nor is one with a default required of the model: left out, it is filled.
                schema["required"] = new JsonArray([.. required.Where(r => !fixedArgs.ContainsKey(r?.ToString() ?? "") && !defaults.ContainsKey(r?.ToString() ?? "")).Select(r => r?.DeepClone())]);
            }
            foreach (var (key, value) in defaults)
            {
                if (props[key] is JsonObject p)
                {
                    p["description"] = ((p.Str("description") ?? "").TrimEnd('.') + $" (default: {value?.ToJsonString()})").Trim();
                }
            }
            copy.Parameters = schema;
            var run = t.Run;
            copy.Run = (args, context, ct) =>
            {
                var given = args.Clone();
                foreach (var (key, value) in defaults)
                {
                    if (given[key] is null)
                    {
                        given[key] = value?.DeepClone();
                    }
                }
                foreach (var (key, value) in fixedArgs)
                {
                    given[key] = value?.DeepClone();
                }
                return run(given, context, ct);
            };
        }
        return copy;
    }

    /// <summary>
    /// Saves a tool's settings at a level: "person" (config.json, every project) or "here" (this project, on this computer);
    /// none (empty) forgets them. Returns why not, or null. The project's own are its file's, changed in the repository.
    /// </summary>
    public string? Save(string level, string name, ToolChoice choice, ToolDef? tool)
    {
        if (choice.Approval == "allow" && tool is not null && AllowLocked(tool) is { } locked)
        {
            return $"{name} cannot run unasked: {locked}";
        }
        var properties = tool?.Own?.Parameters["properties"] as JsonObject ?? tool?.Parameters["properties"] as JsonObject;
        if ((choice.Fixed ?? []).Concat(choice.Defaults ?? []).FirstOrDefault(p => properties?.ContainsKey(p.Key) != true) is { Key: { } unknown })
        {
            return $"{name} has no argument {unknown}.";
        }
        if (choice.Description is { Length: > 4000 })
        {
            return "A tool's description is at most 4,000 characters.";
        }
        try
        {
            if (level == "person")
            {
                var file = ConfigFile ?? throw new InvalidOperationException("No config file here.");
                var config = Config.Load(file);
                config.ToolSettings = With(config.ToolSettings, name, choice);
                config.Save(file);
                lock (_gate)
                {
                    _person = Read(config.ToolSettings);
                    _version++;
                }
            }
            else if (level == "here" && Here is { } here)
            {
                var tools = With(here.Tools, name, choice);
                here.SetTools(tools);
                lock (_gate)
                {
                    _here = Read(tools);
                    _version++;
                }
            }
            else
            {
                return "Tool settings are kept for you (person: every project) or for you in this project (here).";
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return $"Not saved: {e.Message}";
        }
        Changed?.Invoke();
        return null;
    }

    private static JsonObject With(JsonObject? all, string name, ToolChoice choice)
    {
        var o = all?.Clone() ?? [];
        o.Remove(name);
        if (!choice.Empty)
        {
            o[name] = choice.Json();
        }
        return o;
    }
}
