using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Argus.Util;
using ModelContextProtocol.Protocol;

namespace Argus.Server;

/// <summary>One MCP tool as the Python server publishes it: name, description, schemas.</summary>
public sealed record ToolSpec(string Name, string Description, string InputSchemaJson, string OutputSchemaJson)
{
    public JsonObject InputSchema => (JsonObject)JsonNode.Parse(InputSchemaJson)!;
    public JsonObject? OutputSchema => JsonNode.Parse(OutputSchemaJson) as JsonObject;

    /// <summary>A list-returning tool wraps its structured output as {"result": [...]}.</summary>
    public bool WrapsResult => OutputSchema?["properties"]?["result"] is not null;
}

/// <summary>A tool call failed; the message becomes "Error executing tool NAME: MESSAGE".</summary>
public class ToolError(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Argument validation and result shaping, matching FastMCP (the Python MCP SDK)
/// on the wire: the same coercions pydantic applies in lax mode, the same
/// validation error text, one text block per list item with the item as
/// indented JSON, and structured content alongside.
/// </summary>
public static class ToolRuntime
{
    public sealed record Args(JsonObject Raw, Dictionary<string, JsonNode?> Values)
    {
        public string Str(string name) => Values[name]!.GetValue<string>();
        public string? OptStr(string name) => Values.TryGetValue(name, out var v) && v is not null ? v.GetValue<string>() : null;
        public long Int(string name) => Values[name]!.GetValue<long>();
    }

    /// <summary>Validate <paramref name="arguments"/> against <paramref name="spec"/>'s input schema.</summary>
    public static Args Validate(ToolSpec spec, IDictionary<string, JsonElement>? arguments)
    {
        var raw = new JsonObject();
        if (arguments is not null)
            foreach (var (k, v) in arguments) raw[k] = JsonNode.Parse(v.GetRawText());

        var schema = spec.InputSchema;
        var props = schema["properties"] as JsonObject ?? [];
        var required = (schema["required"] as JsonArray ?? []).Select(n => n!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        var values = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        var errors = new List<string>();

        foreach (var (name, propNode) in props)
        {
            var prop = (JsonObject)propNode!;
            if (!raw.TryGetPropertyValue(name, out var value))
            {
                if (required.Contains(name))
                {
                    errors.Add($"{name}\n  Field required [type=missing, input_value={PyRepr(raw)}, input_type=dict]\n    For further information visit https://errors.pydantic.dev/2.13/v/missing");
                    continue;
                }
                values[name] = prop["default"]?.DeepClone();
                continue;
            }
            bool nullable = prop["anyOf"] is JsonArray any && any.Any(a => a?["type"]?.GetValue<string>() == "null");
            var type = prop["type"]?.GetValue<string>()
                       ?? (prop["anyOf"] as JsonArray)?.Select(a => a?["type"]?.GetValue<string>()).FirstOrDefault(t => t != "null");
            if (value is null)
            {
                if (nullable) { values[name] = null; continue; }
                errors.Add(TypeError(name, type, value));
                continue;
            }
            switch (type)
            {
                case "string":
                    if (value is JsonValue sv && sv.GetValueKind() == JsonValueKind.String) values[name] = value.DeepClone();
                    else errors.Add(TypeError(name, type, value));
                    break;
                case "integer":
                    if (CoerceInt(value) is { } n) values[name] = JsonValue.Create(n);
                    else errors.Add(TypeError(name, type, value));
                    break;
                default:
                    values[name] = value.DeepClone();
                    break;
            }
        }
        if (errors.Count > 0)
        {
            var noun = errors.Count == 1 ? "error" : "errors";
            throw new ToolError($"{errors.Count} validation {noun} for {spec.Name}Arguments\n" + string.Join("\n", errors));
        }
        return new Args(raw, values);
    }

    /// <summary>pydantic's lax int: an integral number, or a string of one.</summary>
    static long? CoerceInt(JsonNode value)
    {
        if (value is not JsonValue v) return null;
        switch (v.GetValueKind())
        {
            case JsonValueKind.Number:
                var el = v.GetValue<JsonElement>();
                if (el.TryGetInt64(out var l)) return l;
                var d = el.GetDouble();
                return d == Math.Floor(d) && !double.IsInfinity(d) ? (long)d : null;
            case JsonValueKind.String:
                return long.TryParse(PyStr.Strip(v.GetValue<string>()).Replace("_", ""), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var p) ? p : null;
            case JsonValueKind.True: return 1;
            case JsonValueKind.False: return 0;
            default: return null;
        }
    }

    static string PyType(JsonNode? v) => v switch
    {
        null => "NoneType",
        JsonObject => "dict",
        JsonArray => "list",
        JsonValue jv => jv.GetValueKind() switch
        {
            JsonValueKind.String => "str",
            JsonValueKind.True or JsonValueKind.False => "bool",
            JsonValueKind.Number => jv.GetValue<JsonElement>().TryGetInt64(out _) ? "int" : "float",
            _ => "object",
        },
        _ => "object",
    };

    static string TypeError(string name, string? type, JsonNode? value) => type switch
    {
        "integer" when value is JsonValue v && v.GetValueKind() == JsonValueKind.String =>
            $"{name}\n  Input should be a valid integer, unable to parse string as an integer [type=int_parsing, input_value={PyRepr(value)}, input_type=str]\n    For further information visit https://errors.pydantic.dev/2.13/v/int_parsing",
        "integer" =>
            $"{name}\n  Input should be a valid integer [type=int_type, input_value={PyRepr(value)}, input_type={PyType(value)}]\n    For further information visit https://errors.pydantic.dev/2.13/v/int_type",
        _ =>
            $"{name}\n  Input should be a valid string [type=string_type, input_value={PyRepr(value)}, input_type={PyType(value)}]\n    For further information visit https://errors.pydantic.dev/2.13/v/string_type",
    };

    /// <summary>Python's repr() of a JSON value, as pydantic quotes the input in its errors.</summary>
    public static string PyRepr(JsonNode? node) => node switch
    {
        null => "None",
        JsonObject o => "{" + string.Join(", ", o.Select(kv => $"{PyStr.Repr(kv.Key)}: {PyRepr(kv.Value)}")) + "}",
        JsonArray a => "[" + string.Join(", ", a.Select(PyRepr)) + "]",
        JsonValue v => v.GetValueKind() switch
        {
            JsonValueKind.String => PyStr.Repr(v.GetValue<string>()),
            JsonValueKind.True => "True",
            JsonValueKind.False => "False",
            JsonValueKind.Number => v.GetValue<JsonElement>().TryGetInt64(out var l) ? l.ToString(CultureInfo.InvariantCulture) : PyJson.Float(v.GetValue<JsonElement>().GetDouble()),
            _ => v.ToJsonString(),
        },
        _ => node.ToJsonString(),
    };

    /// <summary>Shape a successful result the way FastMCP does.</summary>
    public static CallToolResult Success(ToolSpec spec, JsonNode? result)
    {
        var content = new List<ContentBlock>();
        switch (result)
        {
            case null:
                break;
            case JsonArray arr:
                foreach (var item in arr) content.Add(new TextContentBlock { Text = PyJson.Indented(item) });
                break;
            default:
                content.Add(new TextContentBlock { Text = PyJson.Indented(result) });
                break;
        }
        JsonNode structured = spec.WrapsResult ? new JsonObject { ["result"] = result?.DeepClone() } : result?.DeepClone() ?? new JsonObject();
        return new CallToolResult
        {
            Content = content,
            StructuredContent = JsonSerializer.SerializeToElement(structured),
            IsError = false,
        };
    }

    public static CallToolResult Failure(string toolName, string message) => new()
    {
        Content = [new TextContentBlock { Text = $"Error executing tool {toolName}: {message}" }],
        IsError = true,
    };

    public static Tool ToProtocol(ToolSpec spec, string? description = null) => new()
    {
        Name = spec.Name,
        Description = description ?? spec.Description,
        InputSchema = JsonSerializer.SerializeToElement(spec.InputSchema),
        OutputSchema = spec.OutputSchema is { } o ? JsonSerializer.SerializeToElement(o) : null,
    };
}
