using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Llm.Api.Chat;

/// <summary>The system message's parts, by length: the app's own words, the tools' instructions, the person's, a compaction's summary.</summary>
/// <param name="Files">An assistant's files in the system message.</param>
public sealed record SystemParts(int Base, int ToolNotes, int Person, int Summary, int Files = 0);

/// <summary>
/// What filled a request to the model, in characters by kind, for the context gauge:
/// the system prompt (the app's and the tools' notes), the person's instructions, the
/// tools' definitions, a compaction's summary, files, the person's messages, the
/// answers, and tool calls with their results. The page scales them to the prompt
/// tokens the model reported, so they add up to what it counted.
/// </summary>
public static partial class ContextParts
{
    public static JsonObject Measure(JsonObject request, SystemParts system, int imageWeight)
    {
        long you = 0, files = system.Files, answers = 0, toolResults = 0;
        foreach (var m in (request["messages"] as JsonArray ?? []).OfType<JsonObject>())
        {
            switch (m["role"]?.GetValue<string>())
            {
                case "user":
                    var (text, images) = m["content"] switch
                    {
                        JsonValue v => (v.GetValue<string>(), 0),
                        JsonArray parts => (string.Concat(parts.OfType<JsonObject>().Select(p => p["text"]?.GetValue<string>() ?? "")), parts.Count(p => p?["type"]?.GetValue<string>() == "image_url")),
                        _ => ("", 0),
                    };
                    var attached = Attachment().Matches(text).Sum(x => (long)x.Length);
                    files += attached + (long)images * imageWeight;
                    you += text.Length - attached;
                    break;
                case "assistant":
                    var said = m["content"]?.GetValue<string>()?.Length ?? 0;
                    answers += said;
                    toolResults += m["tool_calls"]?.ToJsonString().Length ?? 0;
                    break;
                case "tool":
                    toolResults += m["content"]?.GetValue<string>()?.Length ?? 0;
                    break;
            }
        }
        return new JsonObject
        {
            ["system"] = system.Base + system.ToolNotes,
            ["instructions"] = system.Person,
            ["tools"] = request["tools"]?.ToJsonString().Length ?? 0,
            ["summary"] = system.Summary,
            ["files"] = files,
            ["you"] = you,
            ["answers"] = answers,
            ["toolResults"] = toolResults,
        };
    }

    [GeneratedRegex("<attachment name=\"[^\"]*\">.*?</attachment>", RegexOptions.Singleline)]
    private static partial Regex Attachment();
}
