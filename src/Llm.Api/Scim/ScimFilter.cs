using System.Text;
using System.Text.Json;

namespace Llm.Api.Scim;

/// <summary>One comparison of a SCIM filter: an attribute path (lower case, schema prefix removed), an operator, a value.</summary>
public sealed record ScimCondition(string Path, string Op, string? Value);

/// <summary>A PATCH path: the attribute, a sub-attribute (name.givenName), and a value filter (members[value eq "..."]).</summary>
public sealed record ScimPath(string Attribute, string? Sub, ScimCondition? Filter);

/// <summary>
/// What identity providers send (RFC 7644 3.4.2.2 and 3.5.2): comparisons joined by
/// "and", value paths in brackets. Attribute names ignore case. Anything else
/// ("or", "not", grouping) is not understood: the caller answers invalidFilter.
/// </summary>
public static class ScimFilter
{
    public const string UserSchema = "urn:ietf:params:scim:schemas:core:2.0:User";
    public const string GroupSchema = "urn:ietf:params:scim:schemas:core:2.0:Group";

    /// <summary>The conditions, all of which must hold; null when the filter is not understood.</summary>
    public static List<ScimCondition>? Parse(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return [];
        }
        var conditions = new List<ScimCondition>();
        var i = 0;
        var text = filter.Trim();
        while (true)
        {
            Skip(text, ref i);
            var attribute = Word(text, ref i);
            if (attribute.Length == 0)
            {
                return null;
            }
            Skip(text, ref i);
            // members[value eq "id"]: a condition on a multi-valued attribute's values.
            if (attribute.IndexOf('[', StringComparison.Ordinal) is var open and > 0 && attribute.EndsWith(']') && (i >= text.Length || AtAnd(text, i)))
            {
                var inner = Parse(attribute[(open + 1)..^1]);
                if (inner is not { Count: 1 })
                {
                    return null;
                }
                var prefix = Normalise(attribute[..open]);
                conditions.Add(inner[0] with { Path = prefix + "." + inner[0].Path });
            }
            else
            {
                var op = Word(text, ref i).ToLowerInvariant();
                if (op.Length == 0)
                {
                    return null;
                }
                string? value = null;
                if (op != "pr")
                {
                    Skip(text, ref i);
                    if (Value(text, ref i, out value) is false)
                    {
                        return null;
                    }
                }
                conditions.Add(new ScimCondition(Normalise(attribute), op, value));
            }
            Skip(text, ref i);
            if (i >= text.Length)
            {
                return conditions;
            }
            if (!AtAnd(text, i))
            {
                return null;
            }
            i += 3;
        }
    }

    /// <summary>A PATCH operation's path; null when it is not one.</summary>
    public static ScimPath? ParsePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        path = StripSchema(path.Trim());
        ScimCondition? filter = null;
        string? sub = null;
        string attribute;
        if (path.IndexOf('[', StringComparison.Ordinal) is var open and > 0)
        {
            var close = path.LastIndexOf(']');
            if (close < open)
            {
                return null;
            }
            var inner = Parse(path[(open + 1)..close]);
            if (inner is not { Count: 1 })
            {
                return null;
            }
            filter = inner[0];
            attribute = path[..open];
            var rest = path[(close + 1)..];
            sub = rest.StartsWith('.') ? rest[1..] : null;
        }
        else
        {
            var dot = path.IndexOf('.', StringComparison.Ordinal);
            attribute = dot > 0 ? path[..dot] : path;
            sub = dot > 0 ? path[(dot + 1)..] : null;
        }
        return new ScimPath(attribute.ToLowerInvariant(), sub?.ToLowerInvariant(), filter);
    }

    /// <summary>emails[type eq "work"].value -> emails.value; the schema prefix goes; lower case.</summary>
    private static string Normalise(string attribute)
    {
        attribute = StripSchema(attribute);
        var sb = new StringBuilder();
        var depth = 0;
        foreach (var c in attribute)
        {
            depth += c == '[' ? 1 : c == ']' ? -1 : 0;
            if (depth == 0 && c != ']')
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }
        return sb.ToString();
    }

    private static string StripSchema(string path)
    {
        foreach (var schema in new[] { UserSchema, GroupSchema })
        {
            if (path.StartsWith(schema + ":", StringComparison.OrdinalIgnoreCase))
            {
                return path[(schema.Length + 1)..];
            }
        }
        return path;
    }

    private static bool AtAnd(string text, int i) =>
        i + 4 <= text.Length && text.AsSpan(i, 3).Equals("and", StringComparison.OrdinalIgnoreCase) && char.IsWhiteSpace(text[i + 3]);

    private static void Skip(string text, ref int i)
    {
        while (i < text.Length && char.IsWhiteSpace(text[i]))
        {
            i++;
        }
    }

    /// <summary>Up to the next space outside brackets and quotes.</summary>
    private static string Word(string text, ref int i)
    {
        var start = i;
        var depth = 0;
        var quoted = false;
        for (; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                continue;
            }
            if (c == '"')
            {
                quoted = true;
            }
            else if (c == '[')
            {
                depth++;
            }
            else if (c == ']')
            {
                depth--;
            }
            else if (char.IsWhiteSpace(c) && depth == 0)
            {
                break;
            }
        }
        return text[start..Math.Min(i, text.Length)];
    }

    /// <summary>A JSON string, or true, false, null or a number as text.</summary>
    private static bool Value(string text, ref int i, out string? value)
    {
        value = null;
        if (i >= text.Length)
        {
            return false;
        }
        if (text[i] != '"')
        {
            var word = Word(text, ref i);
            value = word == "null" ? null : word;
            return word.Length > 0;
        }
        var start = i++;
        for (; i < text.Length; i++)
        {
            if (text[i] == '\\')
            {
                i++;
            }
            else if (text[i] == '"')
            {
                i++;
                try
                {
                    value = JsonSerializer.Deserialize<string>(text[start..i]);
                    return true;
                }
                catch (JsonException)
                {
                    return false;
                }
            }
        }
        return false;
    }
}
