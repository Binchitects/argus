using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Llm.Api.Gateway.Protocols;

/// <summary>A request one protocol cannot be turned into a chat completion for: its status and the words the caller gets.</summary>
public sealed class AdapterException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>
/// Anthropic's Messages API (/v1/messages: Claude Code, Anthropic's SDKs) as one chat completion, and the answer back in its
/// shapes: the system, text, pictures, tool calls and their results, tools and the choice of one, the sampling, stop
/// sequences, thinking; the message with its thinking, text and tool_use blocks, its stop reason and usage (input less
/// what the cache read, which is cache_read_input_tokens); the stream as its events. Server tools (web search) are refused
/// with a clear 400, not dropped.
/// </summary>
public static class AnthropicMessages
{
    /// <summary>The chat completion a Messages request asks for.</summary>
    public static JsonObject ToChat(JsonObject a)
    {
        var messages = new JsonArray();
        switch (a["system"])
        {
            case JsonValue v when v.TryGetValue<string>(out var s) && s.Length > 0:
                messages.Add(new JsonObject { ["role"] = "system", ["content"] = s });
                break;
            case JsonArray blocks:
                var system = string.Join("\n\n", blocks.OfType<JsonObject>().Where(b => Str(b, "type") == "text").Select(b => Str(b, "text")).OfType<string>());
                if (system.Length > 0)
                {
                    messages.Add(new JsonObject { ["role"] = "system", ["content"] = system });
                }
                break;
        }
        foreach (var m in (a["messages"] as JsonArray ?? throw new AdapterException(400, "messages: an array is required.")).OfType<JsonObject>())
        {
            var role = Str(m, "role") ?? throw new AdapterException(400, "Each message needs its role.");
            if (m["content"] is JsonValue text && text.TryGetValue<string>(out var said))
            {
                messages.Add(new JsonObject { ["role"] = role, ["content"] = said });
                continue;
            }
            var blocks = (m["content"] as JsonArray ?? []).OfType<JsonObject>().ToList();
            if (role == "assistant")
            {
                var words = string.Concat(blocks.Where(b => Str(b, "type") == "text").Select(b => Str(b, "text")));
                var calls = new JsonArray();
                foreach (var b in blocks.Where(b => Str(b, "type") == "tool_use"))
                {
                    calls.Add(new JsonObject
                    {
                        ["id"] = Str(b, "id"), ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = Str(b, "name"), ["arguments"] = (b["input"] ?? new JsonObject()).ToJsonString() },
                    });
                }
                var answer = new JsonObject { ["role"] = "assistant", ["content"] = words.Length > 0 ? words : null };
                if (calls.Count > 0)
                {
                    answer["tool_calls"] = calls;
                }
                messages.Add(answer);
                continue;
            }
            // A person's: each tool's result as a tool message first, in order, then what they wrote and showed.
            foreach (var r in blocks.Where(b => Str(b, "type") == "tool_result"))
            {
                var content = r["content"] switch
                {
                    JsonValue v when v.TryGetValue<string>(out var s) => s,
                    JsonArray parts => string.Join("\n", parts.OfType<JsonObject>().Where(p => Str(p, "type") == "text").Select(p => Str(p, "text"))),
                    _ => "",
                };
                messages.Add(new JsonObject
                {
                    ["role"] = "tool", ["tool_call_id"] = Str(r, "tool_use_id"), ["content"] = r["is_error"] is JsonValue e && e.TryGetValue<bool>(out var bad) && bad ? "Error: " + content : content,
                });
            }
            var mine = new JsonArray();
            foreach (var b in blocks)
            {
                switch (Str(b, "type"))
                {
                    case "text":
                        mine.Add(new JsonObject { ["type"] = "text", ["text"] = Str(b, "text") });
                        break;
                    case "image" when b["source"] is JsonObject src:
                        var url = Str(src, "type") == "base64" ? $"data:{Str(src, "media_type")};base64,{Str(src, "data")}" : Str(src, "url");
                        mine.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = url } });
                        break;
                }
            }
            if (mine.Count > 0)
            {
                messages.Add(new JsonObject
                {
                    ["role"] = role,
                    ["content"] = mine.Count == 1 && Str((JsonObject)mine[0]!, "type") == "text" ? mine[0]!["text"]!.DeepClone() : mine,
                });
            }
        }
        var chat = new JsonObject { ["model"] = a["model"]?.DeepClone(), ["messages"] = messages, ["max_tokens"] = a["max_tokens"]?.DeepClone() };
        foreach (var k in new[] { "temperature", "top_p", "top_k", "stream" })
        {
            if (a[k] is { } v)
            {
                chat[k] = v.DeepClone();
            }
        }
        if (a["stop_sequences"] is JsonArray stops)
        {
            chat["stop"] = stops.DeepClone();
        }
        if (a["tools"] is JsonArray tools && tools.Count > 0)
        {
            var functions = new JsonArray();
            foreach (var t in tools.OfType<JsonObject>())
            {
                if (Str(t, "type") is { } type && type != "custom")
                {
                    throw new AdapterException(400, $"{type} is a tool Anthropic's servers run: no server here runs it. Give the tool as a function with an input_schema.");
                }
                functions.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = Str(t, "name"), ["description"] = Str(t, "description"), ["parameters"] = (t["input_schema"] ?? new JsonObject { ["type"] = "object" }).DeepClone() },
                });
            }
            chat["tools"] = functions;
        }
        if (a["tool_choice"] is JsonObject choice)
        {
            chat["tool_choice"] = Str(choice, "type") switch
            {
                "any" => "required",
                "none" => "none",
                "tool" => new JsonObject { ["type"] = "function", ["function"] = new JsonObject { ["name"] = Str(choice, "name") } },
                _ => "auto",
            };
        }
        if (a["thinking"] is JsonObject thinking)
        {
            chat["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = Str(thinking, "type") == "enabled" };
        }
        return chat;
    }

    private static string? Str(JsonObject? o, string k) => o?[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>A finish reason in Anthropic's words.</summary>
    public static string StopReason(string? finish) => finish switch
    {
        "length" => "max_tokens",
        "tool_calls" or "function_call" => "tool_use",
        _ => "end_turn",
    };

    /// <summary>A chat completion as Anthropic's message.</summary>
    public static JsonObject FromChat(JsonObject completion, string model)
    {
        var message = completion["choices"]?[0]?["message"] as JsonObject;
        var content = new JsonArray();
        if (Str(message, "reasoning_content") is { Length: > 0 } thought)
        {
            content.Add(new JsonObject { ["type"] = "thinking", ["thinking"] = thought, ["signature"] = "" });
        }
        if (Str(message, "content") is { Length: > 0 } text)
        {
            content.Add(new JsonObject { ["type"] = "text", ["text"] = text });
        }
        foreach (var call in (message?["tool_calls"] as JsonArray ?? []).OfType<JsonObject>())
        {
            JsonNode? input;
            try
            {
                input = JsonNode.Parse(Str(call["function"] as JsonObject, "arguments") ?? "{}");
            }
            catch (JsonException)
            {
                input = new JsonObject();
            }
            content.Add(new JsonObject { ["type"] = "tool_use", ["id"] = Str(call, "id") ?? "toolu_" + Guid.NewGuid().ToString("N")[..20], ["name"] = Str(call["function"] as JsonObject, "name"), ["input"] = input });
        }
        return new JsonObject
        {
            ["id"] = "msg_" + Guid.NewGuid().ToString("N")[..24], ["type"] = "message", ["role"] = "assistant", ["model"] = model, ["content"] = content,
            ["stop_reason"] = StopReason(Str(completion["choices"]?[0] as JsonObject, "finish_reason")), ["stop_sequence"] = null, ["usage"] = Usage(completion["usage"] as JsonObject),
        };
    }

    /// <summary>Usage in Anthropic's words: the input less what the cache read, the cache's, the output.</summary>
    public static JsonObject Usage(JsonObject? u)
    {
        int Int(JsonNode? n) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;
        var prompt = Int(u?["prompt_tokens"]);
        var cached = Int(u?["prompt_tokens_details"]?["cached_tokens"]);
        return new JsonObject { ["input_tokens"] = Math.Max(0, prompt - cached), ["output_tokens"] = Int(u?["completion_tokens"]), ["cache_read_input_tokens"] = cached };
    }

    /// <summary>An error in Anthropic's shape.</summary>
    public static string Error(int status, string message) => new JsonObject
    {
        ["type"] = "error",
        ["error"] = new JsonObject
        {
            ["type"] = status switch
            {
                400 or 413 or 422 => "invalid_request_error",
                401 => "authentication_error",
                403 => "permission_error",
                404 => "not_found_error",
                429 => "rate_limit_error",
                503 or 529 => "overloaded_error",
                _ => "api_error",
            },
            ["message"] = message,
        },
    }.ToJsonString();

    /// <summary>
    /// A chat completion's stream turned into Anthropic's events as it comes: message_start, a block for the thinking, the
    /// words and each tool call (its arguments as input_json_delta), message_delta with the stop reason and usage, then
    /// message_stop. Fed the upstream's bytes; what to write goes back from each.
    /// </summary>
    public sealed class Events(string model)
    {
        private readonly StringBuilder _line = new();
        private readonly Decoder _utf8 = Encoding.UTF8.GetDecoder();
        private readonly string _id = "msg_" + Guid.NewGuid().ToString("N")[..24];
        private bool _started;
        private bool _ended;
        private int _index = -1;
        private string? _open;
        private readonly Dictionary<int, int> _toolBlocks = [];
        private string? _finish;
        private JsonObject? _usage;

        private static string Event(string name, JsonObject data)
        {
            data["type"] = name;
            return $"event: {name}\ndata: {data.ToJsonString()}\n\n";
        }

        public string Feed(ReadOnlySpan<byte> bytes)
        {
            var out_ = new StringBuilder();
            var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
            var n = _utf8.GetChars(bytes, chars, flush: false);
            foreach (var c in chars.AsSpan(0, n))
            {
                if (c == '\n')
                {
                    Line(_line.ToString().TrimEnd('\r'), out_);
                    _line.Clear();
                }
                else
                {
                    _line.Append(c);
                }
            }
            return out_.ToString();
        }

        /// <summary>The stream's end: the open block closed, the stop reason and usage, message_stop (once).</summary>
        public string End()
        {
            if (_ended)
            {
                return "";
            }
            _ended = true;
            var out_ = new StringBuilder();
            Start(out_);
            Close(out_);
            var usage = Usage(_usage);
            out_.Append(Event("message_delta", new JsonObject
            {
                ["delta"] = new JsonObject { ["stop_reason"] = StopReason(_finish), ["stop_sequence"] = null },
                ["usage"] = new JsonObject { ["output_tokens"] = usage["output_tokens"]!.GetValue<int>(), ["input_tokens"] = usage["input_tokens"]!.GetValue<int>(), ["cache_read_input_tokens"] = usage["cache_read_input_tokens"]!.GetValue<int>() },
            }));
            out_.Append(Event("message_stop", []));
            return out_.ToString();
        }

        private void Start(StringBuilder out_)
        {
            if (_started)
            {
                return;
            }
            _started = true;
            out_.Append(Event("message_start", new JsonObject
            {
                ["message"] = new JsonObject
                {
                    ["id"] = _id, ["type"] = "message", ["role"] = "assistant", ["model"] = model, ["content"] = new JsonArray(), ["stop_reason"] = null, ["stop_sequence"] = null,
                    ["usage"] = new JsonObject { ["input_tokens"] = 0, ["output_tokens"] = 0 },
                },
            }));
        }

        private void Close(StringBuilder out_)
        {
            if (_open is not null)
            {
                out_.Append(Event("content_block_stop", new JsonObject { ["index"] = _index }));
                _open = null;
            }
        }

        private void Open(StringBuilder out_, string kind, JsonObject block)
        {
            Close(out_);
            _index++;
            _open = kind;
            out_.Append(Event("content_block_start", new JsonObject { ["index"] = _index, ["content_block"] = block }));
        }

        private void Line(string line, StringBuilder out_)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal) || _ended)
            {
                return;
            }
            var data = line[5..].Trim();
            if (data == "[DONE]")
            {
                out_.Append(End());
                return;
            }
            JsonNode? chunk;
            try
            {
                chunk = JsonNode.Parse(data);
            }
            catch (JsonException)
            {
                return;
            }
            Start(out_);
            if (chunk?["error"] is JsonObject err)
            {
                _ended = true;
                out_.Append("event: error\ndata: ").Append(Error(500, Str(err, "message") ?? err.ToJsonString())).Append("\n\n");
                return;
            }
            if (chunk?["usage"] is JsonObject u && u["prompt_tokens"] is not null)
            {
                _usage = u;
            }
            if (chunk?["choices"] is not JsonArray { Count: > 0 } choices || choices[0] is not JsonObject choice)
            {
                return;
            }
            if (choice["delta"] is JsonObject delta)
            {
                if (Str(delta, "reasoning_content") is { Length: > 0 } thought)
                {
                    if (_open != "thinking")
                    {
                        Open(out_, "thinking", new JsonObject { ["type"] = "thinking", ["thinking"] = "" });
                    }
                    out_.Append(Event("content_block_delta", new JsonObject { ["index"] = _index, ["delta"] = new JsonObject { ["type"] = "thinking_delta", ["thinking"] = thought } }));
                }
                if (Str(delta, "content") is { Length: > 0 } words)
                {
                    if (_open != "text")
                    {
                        Open(out_, "text", new JsonObject { ["type"] = "text", ["text"] = "" });
                    }
                    out_.Append(Event("content_block_delta", new JsonObject { ["index"] = _index, ["delta"] = new JsonObject { ["type"] = "text_delta", ["text"] = words } }));
                }
                foreach (var call in (delta["tool_calls"] as JsonArray ?? []).OfType<JsonObject>())
                {
                    var i = call["index"] is JsonValue iv && iv.TryGetValue<int>(out var at) ? at : 0;
                    if (!_toolBlocks.TryGetValue(i, out var block))
                    {
                        Open(out_, "tool:" + i, new JsonObject
                        {
                            ["type"] = "tool_use", ["id"] = Str(call, "id") ?? "toolu_" + Guid.NewGuid().ToString("N")[..20], ["name"] = Str(call["function"] as JsonObject, "name") ?? "", ["input"] = new JsonObject(),
                        });
                        _toolBlocks[i] = block = _index;
                    }
                    if (Str(call["function"] as JsonObject, "arguments") is { Length: > 0 } args)
                    {
                        out_.Append(Event("content_block_delta", new JsonObject { ["index"] = block, ["delta"] = new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = args } }));
                    }
                }
            }
            if (Str(choice, "finish_reason") is { } finish)
            {
                _finish = finish;
            }
        }
    }
}
