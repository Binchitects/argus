using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Llm.Core.Chat;

/// <summary>
/// A tool call as models get it wrong, mended where what was meant is plain: arguments that are not quite JSON (in a
/// code fence, with trailing commas, Python's True and None, single quotes, a raw newline in a string, the closing brace
/// missing, the object sent as a string, the call's own name and arguments around them), a tool named as another harness
/// names it (Bash, Read, str_replace, functions.read_file), and arguments named or typed not as the schema says
/// (file_path for path, "12" for 12). What cannot be mended is said with the tool's parameters, so the next call is right.
/// Shared by Code Arena's agent and Arena's web chat.
/// </summary>
public static partial class ToolCallRepair
{
    /// <summary>Arguments read as the model wrote them, else mended; null when they cannot be read. <paramref name="fix"/>: what was mended, or null.</summary>
    public static JsonObject? Arguments(string? raw, out string? fix)
    {
        fix = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }
        if (Parse(raw) is JsonObject strict)
        {
            return Unwrap(strict, ref fix);
        }
        if (Parse(raw) is JsonValue v && v.TryGetValue<string>(out var inner) && Parse(inner) is JsonObject twice)
        {
            fix = "the arguments were a JSON string holding the object";
            return Unwrap(twice, ref fix);
        }
        var text = raw.Trim();
        var fixes = new List<string>();
        if (Fence().Match(text) is { Success: true } fence)
        {
            text = fence.Groups[1].Value.Trim();
            fixes.Add("a code fence");
        }
        var open = text.IndexOf('{');
        if (open > 0)
        {
            text = text[open..];
            fixes.Add("words before the object");
        }
        if (open < 0)
        {
            return null;
        }
        text = Mend(text, fixes);
        if (Parse(text) is JsonObject mended)
        {
            fix = "mended: " + string.Join(", ", fixes.Distinct());
            return Unwrap(mended, ref fix);
        }
        return null;
    }

    /// <summary>{"name":…,"arguments":{…}} (the call around its arguments) as the arguments.</summary>
    private static JsonObject Unwrap(JsonObject args, ref string? fix)
    {
        if (args.Count == 2 && args["name"] is JsonValue && args["arguments"] is { } inner)
        {
            var found = inner is JsonObject o ? o : inner is JsonValue s && s.TryGetValue<string>(out var str) ? Parse(str) as JsonObject : null;
            if (found is not null)
            {
                fix = (fix is null ? "" : fix + "; ") + "the call's name and arguments were sent as its arguments";
                return (JsonObject)found.DeepClone();
            }
        }
        return args;
    }

    /// <summary>
    /// Walks the text once: single-quoted strings to double, raw control characters in strings escaped, Python's literals
    /// outside strings to JSON's, trailing commas left out, and what is still open at the end closed.
    /// </summary>
    private static string Mend(string text, List<string> fixes)
    {
        var sb = new StringBuilder(text.Length + 8);
        var stack = new Stack<char>();
        char? quote = null;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quote is { } q)
            {
                if (ch == '\\' && i + 1 < text.Length)
                {
                    // \' in a single-quoted string is a plain '.
                    if (q == '\'' && text[i + 1] == '\'')
                    {
                        sb.Append('\'');
                    }
                    else
                    {
                        sb.Append(ch).Append(text[i + 1]);
                    }
                    i++;
                    continue;
                }
                if (ch == q)
                {
                    sb.Append('"');
                    quote = null;
                    continue;
                }
                if (ch == '"' && q == '\'')
                {
                    sb.Append("\\\"");
                    continue;
                }
                if (ch < ' ')
                {
                    sb.Append(ch switch { '\n' => "\\n", '\r' => "\\r", '\t' => "\\t", _ => "\\u" + ((int)ch).ToString("x4", CultureInfo.InvariantCulture) });
                    fixes.Add("a raw line break in a string");
                    continue;
                }
                sb.Append(ch);
                continue;
            }
            switch (ch)
            {
                case '"':
                    quote = '"';
                    sb.Append('"');
                    break;
                case '\'':
                    quote = '\'';
                    sb.Append('"');
                    fixes.Add("single quotes");
                    break;
                case '{' or '[':
                    stack.Push(ch == '{' ? '}' : ']');
                    sb.Append(ch);
                    break;
                case '}' or ']':
                    // A comma before the close: left out.
                    var end = sb.Length - 1;
                    while (end >= 0 && char.IsWhiteSpace(sb[end]))
                    {
                        end--;
                    }
                    if (end >= 0 && sb[end] == ',')
                    {
                        sb.Remove(end, 1);
                        fixes.Add("a trailing comma");
                    }
                    if (stack.Count > 0)
                    {
                        stack.Pop();
                    }
                    sb.Append(ch);
                    if (stack.Count == 0)
                    {
                        // The object ends here: what follows is not its (words after it, a second object).
                        if (text[(i + 1)..].Trim().Length > 0)
                        {
                            fixes.Add("words after the object");
                        }
                        return sb.ToString();
                    }
                    break;
                default:
                    if (char.IsLetter(ch) && (i == 0 || !char.IsLetterOrDigit(text[i - 1])) && Literal().Match(text, i) is { Success: true } lit && lit.Index == i)
                    {
                        var json = lit.Value switch { "True" => "true", "False" => "false", "None" => "null", _ => lit.Value };
                        if (json != lit.Value)
                        {
                            fixes.Add("Python's True, False or None");
                        }
                        sb.Append(json);
                        i += lit.Length - 1;
                        break;
                    }
                    sb.Append(ch);
                    break;
            }
        }
        if (quote is not null)
        {
            sb.Append('"');
        }
        if (stack.Count > 0)
        {
            fixes.Add("the closing " + (stack.Count == 1 ? "brace" : "braces"));
            var trimmed = sb.ToString().TrimEnd().TrimEnd(',');
            sb.Clear().Append(trimmed);
            while (stack.Count > 0)
            {
                sb.Append(stack.Pop());
            }
        }
        return sb.ToString();
    }

    /// <summary>Other harnesses' names for the same tools (Claude Code's, OpenAI's, Qwen Code's), and the usual guesses.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Aliases = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["run_shell"] = ["bash", "shell", "sh", "run", "exec", "execute", "run_command", "execute_command", "terminal", "cmd", "run_terminal_cmd", "powershell", "command"],
        ["read_file"] = ["read", "cat", "view", "open", "open_file", "view_file", "readfile", "file_read", "get_file", "read_text_file"],
        ["write_file"] = ["write", "create_file", "create", "save_file", "writefile", "file_write", "write_to_file"],
        ["edit_file"] = ["edit", "str_replace", "str_replace_editor", "replace", "replace_in_file", "apply_edit", "modify_file", "update_file", "multiedit", "search_replace"],
        ["list_dir"] = ["ls", "list", "list_files", "list_directory", "dir", "listdir", "list_folder"],
        ["grep"] = ["search", "rg", "ripgrep", "search_files", "find_in_files", "search_code", "grep_search", "codebase_search"],
        ["glob"] = ["find", "find_files", "file_search", "glob_files"],
    };

    /// <summary>
    /// The tool a call's name means among <paramref name="names"/>: as named; without a prefix (functions., default_api.,
    /// tools.); any case, - for _, CamelCase for snake_case; another harness's name for it; one letter or two off a single
    /// name. Null when none is plain.
    /// </summary>
    public static string? ToolName(string name, IReadOnlyCollection<string> names)
    {
        if (names.Contains(name))
        {
            return name;
        }
        var bare = Prefix().Replace(name.Trim(), "");
        bare = Junk().Replace(bare, "");
        if (names.FirstOrDefault(n => Same(n, bare)) is { } same)
        {
            return same;
        }
        foreach (var (tool, others) in Aliases)
        {
            if (names.Contains(tool) && others.Any(o => Same(o, bare)))
            {
                return tool;
            }
        }
        var key = Key(bare);
        if (key.Length < 4)
        {
            return null;
        }
        var near = names.Select(n => (Name: n, Distance: Distance(Key(n), key))).Where(x => x.Distance <= 2).OrderBy(x => x.Distance).ToList();
        return near.Count == 1 || (near.Count > 1 && near[0].Distance < near[1].Distance) ? near[0].Name : null;
    }

    private static bool Same(string a, string b) => Key(a) == Key(b);

    /// <summary>A name to compare by: lower case, CamelCase split, - and spaces as _.</summary>
    private static string Key(string name) => Camel().Replace(name.Trim(), "$1_$2").Replace('-', '_').Replace(' ', '_').ToLowerInvariant();

    /// <summary>The usual other names of arguments (file_path for path), each mapped where the schema has the one and not the other.</summary>
    private static readonly Dictionary<string, string[]> ArgumentAliases = new(StringComparer.Ordinal)
    {
        ["path"] = ["file_path", "filepath", "filename", "file_name", "file", "dir", "directory", "folder", "target_file", "absolute_path", "relative_path", "target"],
        ["old_string"] = ["old_str", "old", "old_text", "search", "find", "original", "from", "oldString", "old_content"],
        ["new_string"] = ["new_str", "new", "new_text", "replace", "replacement", "to", "newString", "new_content"],
        ["content"] = ["contents", "text", "data", "body", "file_text", "code", "file_content"],
        ["command"] = ["cmd", "commands", "script", "shell_command", "bash_command"],
        ["pattern"] = ["regex", "query", "search", "expression", "glob_pattern", "search_pattern", "name"],
        ["offset"] = ["start_line", "line", "from", "from_line", "start", "line_start", "first_line"],
        ["limit"] = ["lines", "count", "num_lines", "max_lines", "line_count", "length", "n"],
        ["file"] = ["path", "file_path", "filename", "name"],
    };

    /// <summary>
    /// Arguments fitted to a schema: names mended (an alias, any case, a letter off) where the schema has the name and the
    /// call has not; values typed as it says ("12" for an integer, "true" for a boolean, a number for a string, an enum's
    /// case); what is still missing or wrong said in <see cref="Fitted.Problem"/>, with the parameters.
    /// </summary>
    public static Fitted Fit(string tool, JsonObject args, JsonObject? schema)
    {
        var props = schema?["properties"] as JsonObject;
        if (props is null || props.Count == 0)
        {
            return new Fitted(args, [], null);
        }
        var fixes = new List<string>();
        var fitted = new JsonObject();
        foreach (var (name, value) in args)
        {
            var target = name;
            if (!props.ContainsKey(name))
            {
                target = ArgumentFor(name, props, args) ?? name;
                if (target != name)
                {
                    if (fitted.ContainsKey(target))
                    {
                        continue;
                    }
                    fixes.Add($"{name} → {target}");
                }
            }
            fitted[target] = value?.DeepClone();
        }
        var problems = new List<string>();
        foreach (var (name, value) in fitted.ToList())
        {
            if (props[name] is not JsonObject prop || value is null)
            {
                continue;
            }
            var (typed, change, problem) = Typed(name, value, prop);
            if (change is not null)
            {
                fitted[name] = typed;
                fixes.Add(change);
            }
            if (problem is not null)
            {
                problems.Add(problem);
            }
        }
        var missing = (schema!["required"] as JsonArray ?? []).Select(r => r?.GetValue<string>()).OfType<string>().Where(r => fitted[r] is null).ToList();
        if (missing.Count > 0)
        {
            problems.Insert(0, $"{string.Join(", ", missing)} {(missing.Count == 1 ? "is" : "are")} required");
        }
        var unknown = fitted.Select(kv => kv.Key).Where(k => !props.ContainsKey(k)).ToList();
        if (unknown.Count > 0 && schema["additionalProperties"] is JsonValue extra && extra.TryGetValue<bool>(out var allowed) && !allowed)
        {
            foreach (var k in unknown)
            {
                fitted.Remove(k);
            }
            fixes.Add($"left out {string.Join(", ", unknown)} (not its parameters)");
        }
        var said = problems.Count == 0 ? null
            : $"The call to {tool} was not run: {string.Join("; ", problems)}. {Hint(tool, schema)}";
        return new Fitted(fitted, fixes, said);
    }

    /// <summary>A call's arguments fitted to its tool's schema: what was mended, and what could not be (null: it can run).</summary>
    public sealed record Fitted(JsonObject Args, List<string> Fixes, string? Problem);

    /// <summary>A tool's parameters in a line each (name, type, required, what it is), for a model that called it wrong.</summary>
    public static string Hint(string tool, JsonObject? schema)
    {
        var props = schema?["properties"] as JsonObject;
        if (props is null || props.Count == 0)
        {
            return $"{tool} takes no arguments.";
        }
        var required = (schema!["required"] as JsonArray ?? []).Select(r => r?.GetValue<string>()).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var sb = new StringBuilder($"{tool} takes a JSON object:");
        foreach (var (name, value) in props)
        {
            var prop = value as JsonObject;
            var type = prop?["type"] is JsonValue t && t.TryGetValue<string>(out var ts) ? ts : "any";
            var choices = prop?["enum"] is JsonArray e ? " one of " + string.Join(", ", e.Select(x => x?.ToJsonString())) : "";
            var about = prop?["description"] is JsonValue d && d.TryGetValue<string>(out var ds) ? ": " + (ds.Length > 140 ? ds[..137] + "…" : ds) : "";
            sb.Append(CultureInfo.InvariantCulture, $"\n- {name} ({type}{(required.Contains(name) ? ", required" : "")}{choices}){about}");
        }
        var example = new JsonObject();
        foreach (var name in required)
        {
            var type = props[name]?["type"] is JsonValue t && t.TryGetValue<string>(out var ts) ? ts : "string";
            example[name] = type switch { "integer" or "number" => 1, "boolean" => true, "array" => new JsonArray(), "object" => new JsonObject(), _ => "…" };
        }
        if (example.Count > 0)
        {
            sb.Append("\nFor example: ").Append(example.ToJsonString(Plain));
        }
        return sb.ToString();
    }

    private static readonly JsonSerializerOptions Plain = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The schema's name an argument means: an alias of one the call does not give, the same in another case or with _ and -, or one letter or two off.</summary>
    private static string? ArgumentFor(string name, JsonObject props, JsonObject args)
    {
        var free = props.Select(p => p.Key).Where(p => !args.ContainsKey(p)).ToList();
        if (free.FirstOrDefault(p => Key(p).Replace("_", "", StringComparison.Ordinal) == Key(name).Replace("_", "", StringComparison.Ordinal)) is { } same)
        {
            return same;
        }
        var aliased = free.Where(p => ArgumentAliases.TryGetValue(p, out var others) && others.Contains(name, StringComparer.OrdinalIgnoreCase)).ToList();
        if (aliased.Count == 1)
        {
            return aliased[0];
        }
        var key = Key(name);
        var near = free.Select(p => (Name: p, Distance: Distance(Key(p), key))).Where(x => x.Distance <= (key.Length >= 6 ? 2 : 1)).OrderBy(x => x.Distance).ToList();
        return near.Count == 1 ? near[0].Name : null;
    }

    /// <summary>A value as the schema types it: the value, what was changed (null: nothing), and what is wrong (null: nothing).</summary>
    private static (JsonNode? Value, string? Change, string? Problem) Typed(string name, JsonNode value, JsonObject prop)
    {
        var type = prop["type"] is JsonValue t && t.TryGetValue<string>(out var ts) ? ts : null;
        var kind = value.GetValueKind();
        switch (type)
        {
            case "integer" or "number" when kind == JsonValueKind.String:
                var s = value.GetValue<string>().Trim();
                if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && (type == "number" || d == Math.Floor(d)))
                {
                    return (type == "integer" ? JsonValue.Create((long)d) : JsonValue.Create(d), $"{name} \"{s}\" → {s}", null);
                }
                return (value, null, $"{name} must be {(type == "integer" ? "a whole number" : "a number")}, not \"{Cut(s)}\"");
            case "integer" when kind == JsonValueKind.Number && value.GetValue<double>() is var n && n != Math.Floor(n):
                return (JsonValue.Create((long)Math.Round(n)), $"{name} {n} → {Math.Round(n)}", null);
            case "boolean" when kind == JsonValueKind.String:
                var b = value.GetValue<string>().Trim().ToLowerInvariant();
                return b switch
                {
                    "true" or "yes" or "1" or "on" => (JsonValue.Create(true), $"{name} \"{b}\" → true", null),
                    "false" or "no" or "0" or "off" or "" => (JsonValue.Create(false), $"{name} \"{b}\" → false", null),
                    _ => (value, null, $"{name} must be true or false, not \"{Cut(b)}\""),
                };
            case "boolean" when kind == JsonValueKind.Number:
                return (JsonValue.Create(value.GetValue<double>() != 0), $"{name} {value.ToJsonString()} → {(value.GetValue<double>() != 0 ? "true" : "false")}", null);
            case "string" when kind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False:
                return (JsonValue.Create(kind == JsonValueKind.Number ? value.ToJsonString() : kind == JsonValueKind.True ? "true" : "false"), $"{name} as text", null);
            case "string" when kind is JsonValueKind.Object or JsonValueKind.Array:
                // A file's content given as the JSON it holds: written as that JSON.
                return (JsonValue.Create(value.ToJsonString(Indented)), $"{name} as JSON text", null);
            case "array" when kind == JsonValueKind.String:
                var text = value.GetValue<string>();
                if (Parse(text) is JsonArray list)
                {
                    return (list, $"{name} from a JSON string", null);
                }
                return (new JsonArray(JsonValue.Create(text)), $"{name} as a list of one", null);
            case "object" when kind == JsonValueKind.String && Parse(value.GetValue<string>()) is JsonObject inner:
                return (inner, $"{name} from a JSON string", null);
        }
        if (prop["enum"] is JsonArray choices && kind == JsonValueKind.String)
        {
            var given = value.GetValue<string>();
            var options = choices.Select(c => c is JsonValue v && v.TryGetValue<string>(out var o) ? o : null).OfType<string>().ToList();
            if (options.Count > 0 && !options.Contains(given))
            {
                return options.FirstOrDefault(o => string.Equals(o, given.Trim(), StringComparison.OrdinalIgnoreCase)) is { } match
                    ? (JsonValue.Create(match), $"{name} \"{given}\" → \"{match}\"", null)
                    : (value, null, $"{name} must be one of {string.Join(", ", options)}, not \"{Cut(given)}\"");
            }
        }
        return (value, null, null);
    }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static string Cut(string s) => s.Length > 60 ? s[..57] + "…" : s;

    private static JsonNode? Parse(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            return Node(doc.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A tree of the JSON; a key written twice keeps its last value.</summary>
    private static JsonNode? Node(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => e.EnumerateObject().Aggregate(new JsonObject(), (o, p) =>
        {
            o[p.Name] = Node(p.Value);
            return o;
        }),
        JsonValueKind.Array => new JsonArray([.. e.EnumerateArray().Select(Node)]),
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => JsonValue.Create(e.Clone()),
    };

    /// <summary>Levenshtein's distance between two names.</summary>
    private static int Distance(string a, string b)
    {
        if (Math.Abs(a.Length - b.Length) > 2)
        {
            return 3;
        }
        var previous = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var current = new int[b.Length + 1];
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            }
            previous = current;
        }
        return previous[b.Length];
    }

    [GeneratedRegex(@"^```[\w-]*\s*\n?([\s\S]*?)\n?```\s*$")]
    private static partial Regex Fence();

    [GeneratedRegex(@"True|False|None|true|false|null")]
    private static partial Regex Literal();

    [GeneratedRegex(@"^(?:functions|function|default_api|tools|tool|api)[.:]+", RegexOptions.IgnoreCase)]
    private static partial Regex Prefix();

    /// <summary>What a model leaves around a name: quotes, a chat template's marks, brackets.</summary>
    [GeneratedRegex(@"^[\s""'`<\[(]+|[\s""'`>\])]+$|<\|[^|]*\|>")]
    private static partial Regex Junk();

    [GeneratedRegex(@"([a-z0-9])([A-Z])")]
    private static partial Regex Camel();
}
