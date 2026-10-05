using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeArena;

/// <summary>Reading and writing JSON as nodes: no serializer, no reflection.</summary>
internal static class Json
{
    /// <summary>Readable output: non-ASCII text and HTML characters as they are.</summary>
    public static readonly JsonSerializerOptions Relaxed = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The same, indented: for files people read.</summary>
    public static readonly JsonSerializerOptions Indented = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, WriteIndented = true };

    /// <summary>One line of JSON.</summary>
    public static string Line(JsonNode node) => node.ToJsonString(Relaxed);

    /// <summary>A string property, or null when missing or not a string.</summary>
    public static string? Str(this JsonNode? node, string key) =>
        node is JsonObject o && o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>A whole-number property, or null (numbers sent as text count too).</summary>
    public static long? Long(this JsonNode? node, string key) => node is JsonObject o ? Number(o[key]) : null;

    /// <summary>A whole-number property, or null.</summary>
    public static int? Int(this JsonNode? node, string key) => node.Long(key) is { } n ? (int)Math.Clamp(n, int.MinValue, int.MaxValue) : null;

    /// <summary>A true/false property, or null.</summary>
    public static bool? Bool(this JsonNode? node, string key)
    {
        if (node is not JsonObject o || o[key] is not JsonValue v)
        {
            return null;
        }
        if (v.TryGetValue<bool>(out var b))
        {
            return b;
        }
        return v.TryGetValue<string>(out var s) && bool.TryParse(s, out b) ? b : null;
    }

    /// <summary>A decimal property, or null.</summary>
    public static decimal? Decimal(this JsonNode? node, string key)
    {
        if (node is not JsonObject o || o[key] is not JsonValue v)
        {
            return null;
        }
        if (v.TryGetValue<decimal>(out var d))
        {
            return d;
        }
        if (v.TryGetValue<double>(out var x))
        {
            return (decimal)x;
        }
        return null;
    }

    /// <summary>A number in any of the shapes a JsonValue holds it.</summary>
    public static long? Number(JsonNode? node)
    {
        if (node is not JsonValue v)
        {
            return null;
        }
        if (v.TryGetValue<long>(out var l))
        {
            return l;
        }
        if (v.TryGetValue<int>(out var i))
        {
            return i;
        }
        if (v.TryGetValue<double>(out var d))
        {
            return (long)d;
        }
        return v.TryGetValue<string>(out var s) && long.TryParse(s, out l) ? l : null;
    }

    /// <summary>The text parsed as an object, or null when it is not one.</summary>
    public static JsonObject? ParseObject(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        try
        {
            return JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The text parsed, or null when it is not JSON.</summary>
    public static JsonNode? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A copy of an object.</summary>
    public static JsonObject Clone(this JsonObject o) => (JsonObject)o.DeepClone();
}
