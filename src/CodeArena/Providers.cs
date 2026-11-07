using System.Text;
using System.Text.Json.Nodes;

namespace CodeArena;

/// <summary>
/// A provider of models beside Arena's gateway, with the person's own key: Anthropic (its Messages API), or any API that
/// speaks OpenAI's chat completions (OpenAI, DeepSeek, OpenRouter, Mistral, a company's own vLLM...). Its models are
/// offered as "name:model" (anthropic:claude-sonnet-5-5) and asked of it directly; Arena's are asked of the gateway as
/// before. When Code Arena is signed in to Arena, Arena's admins decide whether providers are used at all (Admin →
/// Settings → Code Arena: other providers); off, they are not offered.
/// </summary>
internal sealed record ProviderConfig
{
    /// <summary>Its name, the prefix of its models' names: lower-case letters, digits and -.</summary>
    public required string Name { get; init; }

    /// <summary>"anthropic" (its Messages API) or "openai" (chat completions).</summary>
    public required string Kind { get; init; }

    /// <summary>Its API's address, without /v1.</summary>
    public required string Url { get; init; }

    /// <summary>The key, kept in config.json (readable by its owner only); null: from <see cref="KeyEnv"/>.</summary>
    public string? ApiKey { get; init; }

    /// <summary>The environment variable that holds the key (ANTHROPIC_API_KEY...).</summary>
    public string? KeyEnv { get; init; }

    /// <summary>The models offered; empty: those its API lists.</summary>
    public List<string> Models { get; init; } = [];

    /// <summary>Its models' window when its API does not say (tokens).</summary>
    public int? Context { get; init; }

    public JsonObject ToJson()
    {
        var o = new JsonObject { ["kind"] = Kind, ["url"] = Url };
        if (ApiKey is not null) o["apiKey"] = ApiKey;
        if (KeyEnv is not null) o["keyEnv"] = KeyEnv;
        if (Models.Count > 0) o["models"] = new JsonArray([.. Models.Select(m => (JsonNode)m)]);
        if (Context is { } c) o["context"] = c;
        return o;
    }
}

internal static class Providers
{
    /// <summary>The providers known by name: their kind, address and key's variable (anything else needs its address).</summary>
    public static readonly IReadOnlyDictionary<string, (string Kind, string Url, string KeyEnv, string About)> Known =
        new Dictionary<string, (string, string, string, string)>(StringComparer.Ordinal)
        {
            ["anthropic"] = ("anthropic", "https://api.anthropic.com", "ANTHROPIC_API_KEY", "Anthropic: Claude (console.anthropic.com, API keys)"),
            ["openai"] = ("openai", "https://api.openai.com", "OPENAI_API_KEY", "OpenAI (platform.openai.com, API keys)"),
            ["deepseek"] = ("openai", "https://api.deepseek.com", "DEEPSEEK_API_KEY", "DeepSeek (platform.deepseek.com, API keys)"),
            ["openrouter"] = ("openai", "https://openrouter.ai/api", "OPENROUTER_API_KEY", "OpenRouter: many providers' models (openrouter.ai, keys)"),
            ["mistral"] = ("openai", "https://api.mistral.ai", "MISTRAL_API_KEY", "Mistral (console.mistral.ai, API keys)"),
        };

    public static bool ValidName(string name) => name.Length is > 0 and <= 40 && name.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-') && name[0] != '-';

    /// <summary>A provider as config.json has it ("providers": {"anthropic": {}, "local": {"kind": "openai", "url": "..."}}).</summary>
    public static ProviderConfig Parse(string name, JsonObject? o, string file)
    {
        if (!ValidName(name))
        {
            throw new InvalidOperationException($"\"providers\" in {file}: {name} is not a provider's name (lower-case letters, digits and -).");
        }
        var known = Known.TryGetValue(name, out var k) ? k : default;
        var kind = o?.Str("kind") ?? known.Kind ?? "openai";
        if (kind is not ("anthropic" or "openai"))
        {
            throw new InvalidOperationException($"\"providers\" in {file}: {name}'s kind is anthropic or openai (chat completions), not {kind}.");
        }
        var url = o?.Str("url") ?? known.Url ?? throw new InvalidOperationException($"\"providers\" in {file}: {name} needs its \"url\" (its API's address, without /v1).");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
        {
            throw new InvalidOperationException($"\"providers\" in {file}: {name}'s url is not an http(s) address: {url}.");
        }
        return new ProviderConfig
        {
            Name = name,
            Kind = kind,
            Url = url.TrimEnd('/').EndsWith("/v1", StringComparison.Ordinal) ? url.TrimEnd('/')[..^3] : url.TrimEnd('/'),
            ApiKey = o?.Str("apiKey"),
            KeyEnv = o?.Str("keyEnv") ?? (known.KeyEnv is { } e ? e : null),
            Models = (o?["models"] as JsonArray)?.Select(m => m?.ToString() ?? "").Where(m => m.Length > 0).ToList() ?? [],
            Context = o?.Int("context"),
        };
    }

    /// <summary>The provider's key: config.json's, else its environment variable's; null: none.</summary>
    public static string? KeyOf(ProviderConfig p, Func<string, string?> env) =>
        p.ApiKey is { Length: > 0 } key ? key : p.KeyEnv is { } name && env(name) is { Length: > 0 } fromEnv ? fromEnv : null;

    /// <summary>The provider and its own name for a model named "provider:model"; null: Arena's.</summary>
    public static (string Provider, string Model)? Split(string model, IEnumerable<string> providers)
    {
        var colon = model.IndexOf(':');
        if (colon <= 0)
        {
            return null;
        }
        var name = model[..colon];
        return providers.Contains(name, StringComparer.Ordinal) ? (name, model[(colon + 1)..]) : null;
    }

    // ---------------------------------------------------------------------------- Anthropic's Messages API

    /// <summary>The chat-completions request as Anthropic's Messages API takes it (system, messages with content blocks, tools).</summary>
    public static JsonObject ToAnthropic(JsonObject body)
    {
        var system = new StringBuilder();
        var messages = new JsonArray();
        JsonObject? last = null;
        void Add(string role, JsonNode block)
        {
            // Anthropic wants turns that alternate: blocks of the same role in a row go into one message.
            if (last is not null && last.Str("role") == role)
            {
                ((JsonArray)last["content"]!).Add(block);
                return;
            }
            last = new JsonObject { ["role"] = role, ["content"] = new JsonArray(block) };
            messages.Add(last);
        }
        foreach (var m in (body["messages"] as JsonArray ?? []).OfType<JsonObject>())
        {
            switch (m.Str("role"))
            {
                case "system" or "developer":
                    if (Text(m["content"]) is { Length: > 0 } s)
                    {
                        system.Append(system.Length > 0 ? "\n\n" : "").Append(s);
                    }
                    break;
                case "tool":
                    Add("user", new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = m.Str("tool_call_id") ?? "", ["content"] = Text(m["content"]) ?? "" });
                    break;
                case "assistant":
                    if (Text(m["content"]) is { Length: > 0 } said)
                    {
                        Add("assistant", new JsonObject { ["type"] = "text", ["text"] = said });
                    }
                    foreach (var call in (m["tool_calls"] as JsonArray ?? []).OfType<JsonObject>())
                    {
                        var fn = call["function"] as JsonObject;
                        var input = Json.ParseObject(fn?.Str("arguments") is { Length: > 0 } a ? a : "{}") ?? new JsonObject();
                        Add("assistant", new JsonObject { ["type"] = "tool_use", ["id"] = call.Str("id") ?? "", ["name"] = fn?.Str("name") ?? "", ["input"] = input });
                    }
                    break;
                default:
                    foreach (var block in UserBlocks(m["content"]))
                    {
                        Add("user", block);
                    }
                    break;
            }
        }
        // A turn must hold something: an assistant message with nothing (a call dropped) says so.
        foreach (var msg in messages.OfType<JsonObject>().Where(x => ((JsonArray)x["content"]!).Count == 0))
        {
            ((JsonArray)msg["content"]!).Add(new JsonObject { ["type"] = "text", ["text"] = "(nothing)" });
        }
        var max = body.Int("max_tokens") ?? body.Int("max_completion_tokens") ?? 16_000;
        var request = new JsonObject
        {
            ["model"] = body.Str("model"),
            ["max_tokens"] = max,
            ["messages"] = messages,
            ["stream"] = true,
        };
        if (system.Length > 0)
        {
            // Code Arena's words and the instructions, the same each turn: cached by Anthropic.
            request["system"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = system.ToString(), ["cache_control"] = new JsonObject { ["type"] = "ephemeral" } });
        }
        if (body["tools"] is JsonArray { Count: > 0 } tools)
        {
            request["tools"] = new JsonArray([.. tools.OfType<JsonObject>().Select(t => t["function"] as JsonObject).OfType<JsonObject>().Select(f => (JsonNode)new JsonObject
            {
                ["name"] = f.Str("name"),
                ["description"] = f.Str("description") ?? "",
                ["input_schema"] = f["parameters"]?.DeepClone() ?? new JsonObject { ["type"] = "object" },
            })]);
        }
        // Thinking as the session asks for it: a budget below the answer's room.
        if (body.Str("reasoning_effort") is { } effort && effort != "none")
        {
            var budget = effort switch { "low" => 2048, "medium" => 8192, "high" => 16384, _ => 32768 };
            if (max <= budget + 1024)
            {
                request["max_tokens"] = budget + 8192;
            }
            request["thinking"] = new JsonObject { ["type"] = "enabled", ["budget_tokens"] = budget };
        }
        if (body["temperature"] is JsonValue temp && request["thinking"] is null)
        {
            request["temperature"] = temp.DeepClone();
        }
        return request;
    }

    private static string? Text(JsonNode? content) => content switch
    {
        JsonValue v => v.ToString(),
        JsonArray parts => string.Join("\n", parts.OfType<JsonObject>().Where(p => p.Str("type") == "text").Select(p => p.Str("text"))),
        _ => null,
    };

    private static IEnumerable<JsonNode> UserBlocks(JsonNode? content)
    {
        if (content is not JsonArray parts)
        {
            yield return new JsonObject { ["type"] = "text", ["text"] = Text(content) is { Length: > 0 } t ? t : "(nothing)" };
            yield break;
        }
        foreach (var p in parts.OfType<JsonObject>())
        {
            if (p.Str("type") == "text")
            {
                yield return new JsonObject { ["type"] = "text", ["text"] = p.Str("text") ?? "" };
            }
            else if (p.Str("type") == "image_url" && (p["image_url"] as JsonObject)?.Str("url") is { } url && url.StartsWith("data:", StringComparison.Ordinal)
                     && url.IndexOf(";base64,", StringComparison.Ordinal) is var at and > 5)
            {
                yield return new JsonObject
                {
                    ["type"] = "image",
                    ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = url[5..at], ["data"] = url[(at + 8)..] },
                };
            }
        }
    }

    /// <summary>Anthropic's stream read event by event, into what chat completions would have said.</summary>
    public sealed class AnthropicStream
    {
        private readonly StringBuilder _text = new();
        private readonly StringBuilder _thinking = new();
        private readonly List<(string Id, string Name, StringBuilder Args)> _calls = [];
        private readonly Dictionary<int, int> _callAt = [];
        private long _input, _cached, _output;

        public string? Finish { get; private set; }

        public bool Ended { get; private set; }

        public string Text => _text.ToString();

        public string Thinking => _thinking.ToString();

        /// <summary>One event's data; what it adds to the answer's text and thinking.</summary>
        public (string? Text, string? Thinking) Read(string data)
        {
            var e = Json.ParseObject(data);
            switch (e?.Str("type"))
            {
                case "message_start":
                    var u = (e["message"] as JsonObject)?["usage"] as JsonObject;
                    _input = (u?.Long("input_tokens") ?? 0) + (u?.Long("cache_creation_input_tokens") ?? 0) + (u?.Long("cache_read_input_tokens") ?? 0);
                    _cached = u?.Long("cache_read_input_tokens") ?? 0;
                    break;
                case "content_block_start" when e["content_block"] is JsonObject block && block.Str("type") == "tool_use":
                    _callAt[e.Int("index") ?? 0] = _calls.Count;
                    _calls.Add((block.Str("id") ?? $"call_{_calls.Count}", block.Str("name") ?? "", new StringBuilder()));
                    break;
                case "content_block_delta" when e["delta"] is JsonObject delta:
                    switch (delta.Str("type"))
                    {
                        case "text_delta":
                            var t = delta.Str("text") ?? "";
                            _text.Append(t);
                            return (t, null);
                        case "thinking_delta":
                            var th = delta.Str("thinking") ?? "";
                            _thinking.Append(th);
                            return (null, th);
                        case "input_json_delta" when _callAt.TryGetValue(e.Int("index") ?? 0, out var at):
                            _calls[at].Args.Append(delta.Str("partial_json") ?? "");
                            break;
                    }
                    break;
                case "message_delta":
                    Finish = (e["delta"] as JsonObject)?.Str("stop_reason") switch
                    {
                        "tool_use" => "tool_calls",
                        "max_tokens" => "length",
                        null => Finish,
                        _ => "stop",
                    };
                    _output = (e["usage"] as JsonObject)?.Long("output_tokens") ?? _output;
                    break;
                case "message_stop":
                    Ended = true;
                    break;
                case "error":
                    throw new GatewayException((e["error"] as JsonObject)?.Str("message") ?? "Anthropic stopped the answer.",
                        (e["error"] as JsonObject)?.Str("type") == "overloaded_error" ? 529 : null);
            }
            return (null, null);
        }

        public JsonArray? ToolCalls => _calls.Count == 0 ? null : new JsonArray([.. _calls.Select(c => (JsonNode)new JsonObject
        {
            ["id"] = c.Id,
            ["type"] = "function",
            ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.Args.Length > 0 ? c.Args.ToString() : "{}" },
        })]);

        public TokenUsage Usage => new(_input, _cached, _output);
    }
}
