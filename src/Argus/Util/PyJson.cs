using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Argus.Util;

/// <summary>
/// JSON written byte-for-byte the way Argus has always written it (Python's json module
/// conventions), because a model reads these strings and a changed byte is a changed prompt.
///
/// Two writers, because Python has two and a client sees both:
/// <list type="bullet">
/// <item><see cref="Indented"/> is what FastMCP puts in a tool result's text
/// content (pydantic's <c>to_json(indent=2)</c>): non-ASCII left as-is, floats
/// always carrying a decimal point.</item>
/// <item><see cref="Dumps"/> is <c>json.dumps</c> with its defaults, which is what
/// the audit table and the audit log hold: ", " and ": " separators and
/// <c>ensure_ascii</c>.</item>
/// </list>
/// Matching them is not cosmetic. An agent that has seen "1.0" learns the field is
/// a float; an audit row compared across implementations must compare equal.
/// </summary>
public static class PyJson
{
    public static string Indented(JsonNode? node)
    {
        var sb = new StringBuilder();
        WriteIndented(sb, node, 0, ensureAscii: false, pydantic: true);
        return sb.ToString();
    }

    /// <summary><c>json.dumps(obj, indent=2)</c>: the indented layout with ensure_ascii on.</summary>
    public static string IndentedDumps(JsonNode? node)
    {
        var sb = new StringBuilder();
        WriteIndented(sb, node, 0, ensureAscii: true, pydantic: false);
        return sb.ToString();
    }

    static void WriteIndented(StringBuilder sb, JsonNode? node, int depth, bool ensureAscii, bool pydantic)
    {
        switch (node)
        {
            case null:
                sb.Append("null");
                return;
            case JsonObject obj:
                if (obj.Count == 0) { sb.Append("{}"); return; }
                sb.Append("{\n");
                int i = 0;
                foreach (var (key, value) in obj)
                {
                    Indent(sb, depth + 1);
                    WriteString(sb, key, ensureAscii);
                    sb.Append(": ");
                    WriteIndented(sb, value, depth + 1, ensureAscii, pydantic);
                    if (++i < obj.Count) sb.Append(',');
                    sb.Append('\n');
                }
                Indent(sb, depth);
                sb.Append('}');
                return;
            case JsonArray arr:
                if (arr.Count == 0) { sb.Append("[]"); return; }
                sb.Append("[\n");
                for (int k = 0; k < arr.Count; k++)
                {
                    Indent(sb, depth + 1);
                    WriteIndented(sb, arr[k], depth + 1, ensureAscii, pydantic);
                    if (k + 1 < arr.Count) sb.Append(',');
                    sb.Append('\n');
                }
                Indent(sb, depth);
                sb.Append(']');
                return;
            case JsonValue val:
                WriteScalar(sb, val, ensureAscii, pydantic);
                return;
        }
    }

    static void Indent(StringBuilder sb, int depth) => sb.Append(' ', depth * 2);

    /// <summary><c>json.dumps(obj)</c> with Python's default separators and ensure_ascii.</summary>
    public static string Dumps(JsonNode? node, bool ensureAscii = true)
    {
        var sb = new StringBuilder();
        WriteCompact(sb, node, ensureAscii);
        return sb.ToString();
    }

    static void WriteCompact(StringBuilder sb, JsonNode? node, bool ensureAscii)
    {
        switch (node)
        {
            case null:
                sb.Append("null");
                return;
            case JsonObject obj:
            {
                sb.Append('{');
                int i = 0;
                foreach (var (key, value) in obj)
                {
                    if (i++ > 0) sb.Append(", ");
                    WriteString(sb, key, ensureAscii);
                    sb.Append(": ");
                    WriteCompact(sb, value, ensureAscii);
                }
                sb.Append('}');
                return;
            }
            case JsonArray arr:
            {
                sb.Append('[');
                for (int k = 0; k < arr.Count; k++)
                {
                    if (k > 0) sb.Append(", ");
                    WriteCompact(sb, arr[k], ensureAscii);
                }
                sb.Append(']');
                return;
            }
            case JsonValue val:
                WriteScalar(sb, val, ensureAscii);
                return;
        }
    }

    static void WriteScalar(StringBuilder sb, JsonValue val, bool ensureAscii, bool pydantic = false)
    {
        if (val.TryGetValue<string>(out var s)) { WriteString(sb, s, ensureAscii); return; }
        if (val.TryGetValue<bool>(out var b)) { sb.Append(b ? "true" : "false"); return; }
        if (val.TryGetValue<long>(out var l)) { sb.Append(l.ToString(CultureInfo.InvariantCulture)); return; }
        if (val.TryGetValue<int>(out var n)) { sb.Append(n.ToString(CultureInfo.InvariantCulture)); return; }
        if (val.TryGetValue<double>(out var d)) { sb.Append(FormatFloat(d, pydantic)); return; }
        if (val.TryGetValue<float>(out var f)) { sb.Append(FormatFloat(f, pydantic)); return; }
        if (val.TryGetValue<JsonElement>(out var el))
        {
            switch (el.ValueKind)
            {
                case JsonValueKind.String: WriteString(sb, el.GetString()!, ensureAscii); return;
                case JsonValueKind.True: sb.Append("true"); return;
                case JsonValueKind.False: sb.Append("false"); return;
                case JsonValueKind.Null: sb.Append("null"); return;
                case JsonValueKind.Number:
                    if (el.TryGetInt64(out var li)) sb.Append(li.ToString(CultureInfo.InvariantCulture));
                    else sb.Append(FormatFloat(el.GetDouble(), pydantic));
                    return;
                default:
                    WriteCompact(sb, JsonNode.Parse(el.GetRawText()), ensureAscii);
                    return;
            }
        }
        sb.Append(val.ToJsonString());
    }

    /// <summary>Python's <c>repr(float)</c>: shortest round-trip, exponent outside [1e-4, 1e16), two-digit exponent.</summary>
    public static string Float(double d) => FormatFloat(d, pydantic: false);

    /// <summary>
    /// pydantic-core's float text, which FastMCP's text content uses: the same
    /// digits as repr, but positional down to 1e-5 and an unpadded exponent
    /// ("1e-6", "1e+16"). An agent sees this text, so it is matched too.
    /// </summary>
    public static string PydanticFloat(double d) => FormatFloat(d, pydantic: true);

    static string FormatFloat(double d, bool pydantic)
    {
        if (double.IsNaN(d)) return "NaN";
        if (double.IsPositiveInfinity(d)) return "Infinity";
        if (double.IsNegativeInfinity(d)) return "-Infinity";
        if (d == 0) return BitConverter.DoubleToInt64Bits(d) < 0 ? "-0.0" : "0.0";
        // "E16" gives 17 significant digits; "R" gives the shortest round-trip
        // digits, which is what both Python and pydantic print. Decompose "R"
        // into sign, digits and decimal exponent, then lay it out per the rules.
        var r = d.ToString("R", CultureInfo.InvariantCulture);
        bool negative = r.StartsWith('-');
        if (negative) r = r[1..];
        int exp10 = 0;
        int e = r.IndexOfAny(['E', 'e']);
        if (e >= 0)
        {
            exp10 = int.Parse(r[(e + 1)..], CultureInfo.InvariantCulture);
            r = r[..e];
        }
        int dot = r.IndexOf('.');
        string intPart = dot < 0 ? r : r[..dot];
        string fracPart = dot < 0 ? "" : r[(dot + 1)..];
        var digits = (intPart + fracPart).TrimStart('0');
        int leadingZeros = (intPart + fracPart).Length - (intPart + fracPart).TrimStart('0').Length;
        // Position of the decimal point relative to the start of `digits`.
        int point = intPart.Length - leadingZeros + exp10;
        digits = digits.TrimEnd('0');
        if (digits.Length == 0) digits = "0";
        int sciExp = point - 1;
        double abs = Math.Abs(d);
        bool scientific = abs >= 1e16 || abs < (pydantic ? 1e-5 : 1e-4);
        string body;
        if (scientific)
        {
            var mantissa = digits.Length == 1 ? digits : digits[0] + "." + digits[1..];
            var sign = sciExp < 0 ? "-" : "+";
            var magnitude = Math.Abs(sciExp).ToString(pydantic ? "0" : "00", CultureInfo.InvariantCulture);
            body = $"{mantissa}e{sign}{magnitude}";
        }
        else if (point <= 0)
            body = "0." + new string('0', -point) + digits;
        else if (point >= digits.Length)
            body = digits + new string('0', point - digits.Length) + ".0";
        else
            body = digits[..point] + "." + digits[point..];
        return negative ? "-" + body : body;
    }

    static void WriteString(StringBuilder sb, string s, bool ensureAscii)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 0x20 || (ensureAscii && c > 0x7e))
                        sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }

    /// <summary>Build a JsonNode from a CLR value (rows, lists, dictionaries, scalars).</summary>
    public static JsonNode? From(object? value) => value switch
    {
        null => null,
        JsonNode n => n,
        string s => JsonValue.Create(s),
        bool b => JsonValue.Create(b),
        long l => JsonValue.Create(l),
        int i => JsonValue.Create((long)i),
        double d => JsonValue.Create(d),
        float f => JsonValue.Create((double)f),
        byte[] bytes => JsonValue.Create(Convert.ToBase64String(bytes)),
        Row r => r.ToJson(),
        IEnumerable<KeyValuePair<string, object?>> kv => Obj(kv),
        System.Collections.IEnumerable e => Arr(e),
        _ => JsonValue.Create(value.ToString()),
    };

    static JsonObject Obj(IEnumerable<KeyValuePair<string, object?>> kv)
    {
        var o = new JsonObject();
        foreach (var (k, v) in kv) o[k] = From(v);
        return o;
    }

    static JsonArray Arr(System.Collections.IEnumerable e)
    {
        var a = new JsonArray();
        foreach (var item in e) a.Add(From(item));
        return a;
    }
}
