using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeArena;

/// <summary>
/// What the page says about its editor when it runs a task or a configuration: the file open in it (relative to the
/// folder), the cursor's line, the text selected, and the person's settings (for ${config:…}).
/// </summary>
internal sealed record RunContext(string? File = null, int? LineNumber = null, string? SelectedText = null, JsonObject? Settings = null)
{
    /// <summary>The page's {file, lineNumber, selectedText, settings}; nothing when it sent none.</summary>
    public static RunContext From(JsonNode? node) => node is JsonObject o
        ? new RunContext(o.Str("file") is { Length: > 0 } file ? file : null, o.Int("lineNumber"), o.Str("selectedText"), o["settings"] as JsonObject)
        : new RunContext();
}

/// <summary>
/// VS Code's variables in tasks.json and launch.json, filled in: the folder's (${workspaceFolder} and the like), the
/// open file's, ${env:…} (never the API key), ${config:…} from the settings the page sent, ${input:…} from what the
/// person answered (one not answered yet is noted, to be asked first), and ${codeArena.buildConfiguration}. Any other
/// variable, or ${command:…}, is refused (400 bad_config) naming it; when lenient (to show a command before the folder is
/// trusted), it is left as written, and so are ${env:…} and the inputs not answered.
/// </summary>
internal sealed partial class RunVariables
{
    private readonly Workspace _workspace;
    private readonly RunContext _context;
    private readonly string? _file;
    private readonly string _build;
    private readonly IReadOnlyDictionary<string, string> _answers;
    private readonly Func<string, string?> _env;
    private readonly bool _lenient;

    /// <summary>The variables of a run; the page's file is refused (403) when it is outside the folder.</summary>
    public RunVariables(Workspace workspace, IdeFiles files, RunContext context, string buildConfiguration, IReadOnlyDictionary<string, string> answers, Func<string, string?> env, bool lenient = false)
    {
        _workspace = workspace;
        _context = context;
        _file = context.File is { } file ? files.Resolve(file) : null;
        _build = buildConfiguration;
        _answers = answers;
        _env = env;
        _lenient = lenient;
    }

    /// <summary>The inputs used and not answered yet, each once: what the page asks the person.</summary>
    public List<JsonObject> Missing { get; } = [];

    [GeneratedRegex(@"\$\{([^{}]+)\}")]
    private static partial Regex Variable();

    /// <summary>A value with the variables of each of its strings filled in; the keys named in <paramref name="asWritten"/> (at its top) are left as they are.</summary>
    public JsonNode? Resolve(JsonNode? node, IReadOnlyDictionary<string, JsonObject> inputs, string file, IReadOnlySet<string>? asWritten = null)
    {
        switch (node)
        {
            case JsonObject o:
                var copy = new JsonObject();
                foreach (var (key, value) in o)
                {
                    copy[key] = asWritten?.Contains(key) == true ? value?.DeepClone() : Resolve(value, inputs, file);
                }
                return copy;
            case JsonArray a:
                return new JsonArray([.. a.Select(v => Resolve(v, inputs, file))]);
            case JsonValue v when v.TryGetValue<string>(out var text):
                return JsonValue.Create(Resolve(text, inputs, file));
            default:
                return node?.DeepClone();
        }
    }

    /// <summary>A string with its variables filled in; <paramref name="file"/> names where it came from, for the person.</summary>
    public string Resolve(string text, IReadOnlyDictionary<string, JsonObject> inputs, string file) =>
        Variable().Replace(text, m => Value(m.Groups[1].Value, inputs, file) ?? m.Value);

    /// <summary>409 needs_input with the inputs to ask for, when some are not answered.</summary>
    public void ThrowIfMissing()
    {
        if (Missing.Count > 0)
        {
            throw new IdeError(409, "needs_input", $"Answer {string.Join(", ", Missing.Select(m => m.Str("id")))} first.",
                new JsonObject { ["inputs"] = new JsonArray([.. Missing.Select(m => (JsonNode)m.DeepClone())]) });
        }
    }

    private string? Value(string name, IReadOnlyDictionary<string, JsonObject> inputs, string file)
    {
        var colon = name.IndexOf(':', StringComparison.Ordinal);
        var (kind, arg) = colon > 0 ? (name[..colon], name[(colon + 1)..]) : (name, "");
        var root = _workspace.Root;
        return kind switch
        {
            "workspaceFolder" or "workspaceRoot" or "fileWorkspaceFolder" or "cwd" => root,
            "workspaceFolderBasename" or "workspaceRootFolderName" => Path.GetFileName(root),
            "userHome" => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "pathSeparator" or "/" => new string(Path.DirectorySeparatorChar, 1),
            "file" => File(name, f => f),
            "relativeFile" => File(name, f => Path.GetRelativePath(root, f)),
            "relativeFileDirname" => File(name, f => Path.GetDirectoryName(Path.GetRelativePath(root, f)) is { Length: > 0 } d ? d : "."),
            "fileBasename" => File(name, Path.GetFileName),
            "fileBasenameNoExtension" => File(name, Path.GetFileNameWithoutExtension),
            "fileExtname" => File(name, Path.GetExtension),
            "fileDirname" => File(name, f => Path.GetDirectoryName(f) ?? root),
            "fileDirnameBasename" => File(name, f => Path.GetFileName(Path.GetDirectoryName(f)) ?? ""),
            "lineNumber" => File(name, _ => _context.LineNumber?.ToString(CultureInfo.InvariantCulture)),
            "selectedText" => _context.SelectedText ?? "",
            "codeArena.buildConfiguration" => _build,
            // The person's key stays with Code Arena, whatever a task asks for; a command shown before the folder is
            // trusted shows ${env:…} as written, not what may be a secret.
            "env" => string.Equals(arg, "ARENA_API_KEY", StringComparison.OrdinalIgnoreCase) ? "" : _lenient ? null : _env(arg) ?? "",
            "config" => Setting(name, arg, file),
            "input" => Input(name, arg, inputs, file),
            "command" => Refuse(file, $"${{{name}}} runs a command of VS Code's: not run here."),
            _ => Refuse(file, $"${{{name}}} is not a variable Code Arena knows."),
        };
    }

    private string? File(string name, Func<string, string?> part)
    {
        if (_file is null)
        {
            return Refuse(null, $"${{{name}}} needs a file open in the editor.");
        }
        return part(_file) ?? Refuse(null, $"${{{name}}} needs the editor's cursor.");
    }

    /// <summary>A setting the page sent, by its dotted key (flat, or nested object by object).</summary>
    private string? Setting(string name, string key, string file)
    {
        JsonNode? found = null;
        if (_context.Settings is { } settings)
        {
            found = settings[key];
            if (found is null)
            {
                JsonNode? node = settings;
                foreach (var part in key.Split('.'))
                {
                    node = node is JsonObject o ? o[part] : null;
                }
                found = node;
            }
        }
        switch (found)
        {
            case JsonValue v when v.TryGetValue<string>(out var text):
                return text;
            case JsonValue v:
                return v.ToJsonString();
            case null when key == "codeArena.buildConfiguration":
                return _build;
            case null:
                return Refuse(file, $"${{{name}}}: the setting {key} is not set.");
            default:
                return Refuse(file, $"${{{name}}}: the setting {key} is not one value.");
        }
    }

    private string? Input(string name, string id, IReadOnlyDictionary<string, JsonObject> inputs, string file)
    {
        if (_answers.TryGetValue(id, out var answer))
        {
            return answer;
        }
        if (!inputs.TryGetValue(id, out var input))
        {
            return Refuse(file, $"${{{name}}}: {file} has no input {id}.");
        }
        var type = input.Str("type");
        if (type is not ("promptString" or "pickString"))
        {
            return Refuse(file, $"${{{name}}} is an input of type {type ?? "none"}: only promptString and pickString are asked.");
        }
        if (!Missing.Any(m => m.Str("id") == id))
        {
            Missing.Add(new JsonObject
            {
                ["id"] = id,
                ["type"] = type,
                ["description"] = input.Str("description"),
                ["default"] = input.Str("default"),
                ["options"] = type == "pickString" ? new JsonArray([.. Options(input["options"])]) : null,
                ["password"] = input.Bool("password") == true,
            });
        }
        // Asked first: a run with it missing does not start.
        return _lenient ? null : "";
    }

    /// <summary>A pickString's options as {label, value}: VS Code takes a string or {label, value} for each.</summary>
    private static IEnumerable<JsonNode> Options(JsonNode? options)
    {
        foreach (var option in options as JsonArray ?? [])
        {
            if (option is JsonValue v && v.TryGetValue<string>(out var text))
            {
                yield return new JsonObject { ["label"] = text, ["value"] = text };
            }
            else if (option.Str("value") is { } value)
            {
                yield return new JsonObject { ["label"] = option.Str("label") ?? value, ["value"] = value };
            }
        }
    }

    private string? Refuse(string? file, string message) => _lenient ? null : throw BadConfig(file, null, message);

    /// <summary>400 bad_config: what is wrong, and in which file and line when known.</summary>
    public static IdeError BadConfig(string? file, int? line, string message) =>
        new(400, "bad_config", message, new JsonObject { ["file"] = file, ["line"] = line });

    /// <summary>tasks.json's or launch.json's inputs, by id.</summary>
    public static Dictionary<string, JsonObject> Inputs(JsonNode? inputs)
    {
        var all = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var input in inputs as JsonArray ?? [])
        {
            if (input is JsonObject o && o.Str("id") is { Length: > 0 } id)
            {
                all.TryAdd(id, o);
            }
        }
        return all;
    }

    /// <summary>What the person answered, as the page sent it: {id: value}.</summary>
    public static Dictionary<string, string> Answers(JsonNode? answers)
    {
        var all = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (id, value) in answers as JsonObject ?? [])
        {
            if (value is JsonValue v)
            {
                all[id] = v.TryGetValue<string>(out var text) ? text : v.ToJsonString();
            }
        }
        return all;
    }
}

/// <summary>A file of the project's .vscode folder read as VS Code reads it: JSON with comments and trailing commas, what is wrong with it said with its line.</summary>
internal static partial class VsCodeJson
{
    /// <summary>The most of such a file read.</summary>
    public const int MaxBytes = 1024 * 1024;

    /// <summary>"windows", "osx" or "linux": the key of a part for this system only.</summary>
    public static string ThisSystem => OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "osx" : "linux";

    /// <summary>The file's object and text; null when it is not there, or wrong (said in <paramref name="errors"/>).</summary>
    public static (JsonObject? Json, string Text) Read(Workspace workspace, string name, List<JsonObject> errors)
    {
        var shown = $".vscode/{name}";
        var path = Path.Combine(workspace.Root, ".vscode", name);
        if (!File.Exists(path))
        {
            return (null, "");
        }
        if (!workspace.Allowed(path))
        {
            errors.Add(Error(shown, null, $"{shown} leads outside the folder: not read."));
            return (null, "");
        }
        string text;
        try
        {
            if (ReadSmall(path) is not { } small)
            {
                errors.Add(Error(shown, null, $"{shown} is larger than {MaxBytes / 1024 / 1024} MB: not read."));
                return (null, "");
            }
            text = small;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            errors.Add(Error(shown, null, $"{shown} could not be read: {e.Message}"));
            return (null, "");
        }
        var node = Json.ParseJsonc(text, out var error);
        if (error is not null)
        {
            var m = LineError().Match(error);
            errors.Add(Error(shown, m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null, m.Success ? m.Groups[2].Value : error));
            return (null, text);
        }
        if (node is not JsonObject json)
        {
            errors.Add(Error(shown, 1, $"{shown} holds no object."));
            return (null, text);
        }
        return (json, text);
    }

    [GeneratedRegex(@"^line (\d+): (.*)$", RegexOptions.Singleline)]
    private static partial Regex LineError();

    /// <summary>
    /// A file's text when it holds at most <paramref name="max"/> bytes; null when it holds more. Read no further than
    /// that, as a link to a device (/dev/zero) says it holds nothing and never ends. Throws IOException or
    /// UnauthorizedAccessException when it cannot be read.
    /// </summary>
    public static string? ReadSmall(string path, int max = MaxBytes)
    {
        var length = new FileInfo(path).Length;
        if (length > max)
        {
            return null;
        }
        using var reader = new StreamReader(path);
        // UTF-8 never has more characters than bytes: one more than the bytes it said is more than it holds.
        var chars = new char[length + 1];
        var read = reader.ReadBlock(chars, 0, chars.Length);
        return read > length ? null : new string(chars, 0, read);
    }

    /// <summary>What is wrong in a file: {file, line, message}.</summary>
    public static JsonObject Error(string? file, int? line, string message) => new() { ["file"] = file, ["line"] = line, ["message"] = message };

    /// <summary>The line (from 1) where a key first has this text as its value, or (with no value) first appears; null when not found.</summary>
    public static int? LineOf(string text, string key, string? value = null)
    {
        var pattern = Regex.Escape(Json.Line(JsonValue.Create(key))) + @"\s*:" + (value is null ? "" : @"\s*" + Regex.Escape(Json.Line(JsonValue.Create(value))));
        var m = Regex.Match(text, pattern, RegexOptions.None, TimeSpan.FromSeconds(1));
        return m.Success ? text.AsSpan(0, m.Index).Count('\n') + 1 : null;
    }

    /// <summary>An object with its part for this system (windows, linux, osx) merged over it, and those parts dropped.</summary>
    public static JsonObject ForThisSystem(JsonObject o)
    {
        var merged = Merge(o, o[ThisSystem] as JsonObject);
        merged.Remove("windows");
        merged.Remove("linux");
        merged.Remove("osx");
        return merged;
    }

    /// <summary>A copy of one object with another's keys over it, objects merged key by key and the rest replaced.</summary>
    public static JsonObject Merge(JsonObject? under, JsonObject? over)
    {
        var merged = under?.Clone() ?? [];
        foreach (var (key, value) in over ?? [])
        {
            merged[key] = merged[key] is JsonObject a && value is JsonObject b ? Merge(a, b) : value?.DeepClone();
        }
        return merged;
    }

    /// <summary>A list of strings as VS Code writes one: an array (other values as their JSON), or one string.</summary>
    public static List<string> Strings(JsonNode? node) => node switch
    {
        JsonArray a => [.. a.Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? s : x?.ToJsonString()).OfType<string>()],
        JsonValue v when v.TryGetValue<string>(out var s) => [s],
        _ => [],
    };

    /// <summary>Variables as an object holds them ({name: value}; null unsets one).</summary>
    public static Dictionary<string, string?> Variables(JsonNode? node)
    {
        var vars = new Dictionary<string, string?>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var (name, value) in node as JsonObject ?? [])
        {
            vars[name] = value switch
            {
                null => null,
                JsonValue v when v.TryGetValue<string>(out var s) => s,
                _ => value.ToJsonString(),
            };
        }
        return vars;
    }
}
