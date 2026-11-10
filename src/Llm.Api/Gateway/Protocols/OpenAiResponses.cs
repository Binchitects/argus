using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Llm.Api.Gateway.Protocols;

/// <summary>
/// OpenAI's Responses API (/v1/responses: Codex with wire_api "responses") as one chat completion, and the answer back as a
/// response: the instructions, the input's messages, function calls and their outputs (reasoning items dropped), function
/// tools and the choice of one, the longest answer, reasoning's effort; the output's reasoning, message and function_call
/// items, its usage, and the stream as its events. The gateway keeps nothing: previous_response_id and built-in tools get
/// a 400 that says so.
/// </summary>
public static class OpenAiResponses
{
    private static string? Str(JsonObject? o, string k) => o?[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static JsonObject ToChat(JsonObject r)
    {
        if (Str(r, "previous_response_id") is { Length: > 0 })
        {
            throw new AdapterException(400, "previous_response_id: this gateway keeps no responses. Send the whole input each time (store: false).");
        }
        var messages = new JsonArray();
        if (Str(r, "instructions") is { Length: > 0 } instructions)
        {
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = instructions });
        }
        switch (r["input"])
        {
            case JsonValue v when v.TryGetValue<string>(out var said):
                messages.Add(new JsonObject { ["role"] = "user", ["content"] = said });
                break;
            case JsonArray items:
                JsonObject? calling = null;
                foreach (var item in items.OfType<JsonObject>())
                {
                    var type = Str(item, "type") ?? (item["role"] is not null ? "message" : null);
                    switch (type)
                    {
                        case "message":
                            calling = null;
                            var role = Str(item, "role") is "developer" ? "system" : Str(item, "role") ?? "user";
                            messages.Add(new JsonObject { ["role"] = role, ["content"] = Content(item["content"], role) });
                            break;
                        case "function_call":
                            if (calling is null)
                            {
                                calling = new JsonObject { ["role"] = "assistant", ["content"] = null, ["tool_calls"] = new JsonArray() };
                                messages.Add(calling);
                            }
                            calling["tool_calls"]!.AsArray().Add(new JsonObject
                            {
                                ["id"] = Str(item, "call_id"), ["type"] = "function", ["function"] = new JsonObject { ["name"] = Str(item, "name"), ["arguments"] = Str(item, "arguments") ?? "{}" },
                            });
                            break;
                        case "function_call_output":
                            calling = null;
                            messages.Add(new JsonObject
                            {
                                ["role"] = "tool", ["tool_call_id"] = Str(item, "call_id"), ["content"] = item["output"] is JsonValue o && o.TryGetValue<string>(out var text) ? text : item["output"]?.ToJsonString() ?? "",
                            });
                            break;
                    }
                }
                break;
        }
        var chat = new JsonObject { ["model"] = r["model"]?.DeepClone(), ["messages"] = messages };
        if (r["max_output_tokens"] is { } most)
        {
            chat["max_tokens"] = most.DeepClone();
        }
        foreach (var k in new[] { "temperature", "top_p", "stream", "parallel_tool_calls" })
        {
            if (r[k] is { } v)
            {
                chat[k] = v.DeepClone();
            }
        }
        if (r["tools"] is JsonArray tools && tools.Count > 0)
        {
            var functions = new JsonArray();
            foreach (var t in tools.OfType<JsonObject>())
            {
                if (Str(t, "type") is not "function")
                {
                    throw new AdapterException(400, $"{Str(t, "type")} is a tool OpenAI's servers run: no server here runs it. Give the tool as a function.");
                }
                functions.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = Str(t, "name"), ["description"] = Str(t, "description"), ["parameters"] = (t["parameters"] ?? new JsonObject { ["type"] = "object" }).DeepClone() },
                });
            }
            chat["tools"] = functions;
        }
        chat["tool_choice"] = r["tool_choice"] switch
        {
            JsonValue v when v.TryGetValue<string>(out var c) => c,
            JsonObject o when Str(o, "type") == "function" => new JsonObject { ["type"] = "function", ["function"] = new JsonObject { ["name"] = Str(o, "name") } },
            _ => null,
        };
        if (chat["tool_choice"] is null)
        {
            chat.Remove("tool_choice");
        }
        if (Str(r["reasoning"] as JsonObject, "effort") is { } effort)
        {
            chat["chat_template_kwargs"] = new JsonObject { ["reasoning_effort"] = effort };
        }
        return chat;
    }

    private static JsonNode? Content(JsonNode? content, string role)
    {
        if (content is JsonValue v && v.TryGetValue<string>(out var s))
        {
            return s;
        }
        var parts = new JsonArray();
        foreach (var p in (content as JsonArray ?? []).OfType<JsonObject>())
        {
            switch (Str(p, "type"))
            {
                case "input_text" or "output_text" or "text":
                    parts.Add(new JsonObject { ["type"] = "text", ["text"] = Str(p, "text") });
                    break;
                case "input_image":
                    parts.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = Str(p, "image_url") ?? Str(p["image_url"] as JsonObject, "url") } });
                    break;
            }
        }
        // The model's own words, and a message of words only, as plain text (every engine reads it).
        return role is "assistant" or "system" || parts.All(p => Str((JsonObject)p!, "type") == "text")
            ? string.Concat(parts.Select(p => Str((JsonObject)p!, "text")))
            : parts;
    }

    public static JsonObject Usage(JsonObject? u)
    {
        int Int(JsonNode? n) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;
        var input = Int(u?["prompt_tokens"]);
        var output = Int(u?["completion_tokens"]);
        return new JsonObject
        {
            ["input_tokens"] = input, ["input_tokens_details"] = new JsonObject { ["cached_tokens"] = Int(u?["prompt_tokens_details"]?["cached_tokens"]) },
            ["output_tokens"] = output, ["output_tokens_details"] = new JsonObject { ["reasoning_tokens"] = Int(u?["completion_tokens_details"]?["reasoning_tokens"]) },
            ["total_tokens"] = input + output,
        };
    }

    private static string Id(string prefix) => prefix + Guid.NewGuid().ToString("N")[..24];

    private static JsonObject Response(string id, string model, string status, JsonArray output, JsonObject? usage, string? finish) => new()
    {
        ["id"] = id, ["object"] = "response", ["created_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ["status"] = status, ["model"] = model, ["output"] = output,
        ["incomplete_details"] = finish == "length" ? new JsonObject { ["reason"] = "max_output_tokens" } : null, ["usage"] = usage, ["error"] = null,
    };

    /// <summary>A chat completion as a response.</summary>
    public static JsonObject FromChat(JsonObject completion, string model)
    {
        var choice = completion["choices"]?[0] as JsonObject;
        var message = choice?["message"] as JsonObject;
        var output = new JsonArray();
        if (Str(message, "reasoning_content") is { Length: > 0 } thought)
        {
            output.Add(new JsonObject { ["type"] = "reasoning", ["id"] = Id("rs_"), ["summary"] = new JsonArray(new JsonObject { ["type"] = "summary_text", ["text"] = thought }) });
        }
        if (Str(message, "content") is { Length: > 0 } text)
        {
            output.Add(Message(Id("msg_"), text));
        }
        foreach (var call in (message?["tool_calls"] as JsonArray ?? []).OfType<JsonObject>())
        {
            output.Add(new JsonObject
            {
                ["type"] = "function_call", ["id"] = Id("fc_"), ["call_id"] = Str(call, "id"), ["name"] = Str(call["function"] as JsonObject, "name"),
                ["arguments"] = Str(call["function"] as JsonObject, "arguments") ?? "{}", ["status"] = "completed",
            });
        }
        var finish = Str(choice, "finish_reason");
        return Response(Id("resp_"), model, finish == "length" ? "incomplete" : "completed", output, Usage(completion["usage"] as JsonObject), finish);
    }

    private static JsonObject Message(string id, string text) => new()
    {
        ["type"] = "message", ["id"] = id, ["status"] = "completed", ["role"] = "assistant",
        ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = text, ["annotations"] = new JsonArray() }),
    };

    /// <summary>An error as a response's clients read it (OpenAI's shape).</summary>
    public static string Error(int status, string message) => new JsonObject
    {
        ["error"] = new JsonObject { ["message"] = message, ["type"] = status >= 500 ? "server_error" : "invalid_request_error", ["code"] = status.ToString(CultureInfo.InvariantCulture) },
    }.ToJsonString();

    /// <summary>
    /// A chat completion's stream as the Responses API's events: response.created and in_progress; for the words, the
    /// message item and its output_text part with each delta, then their done events; each function call as its item with
    /// its arguments' deltas; the reasoning as an item of its own; response.completed (or failed) with the whole response.
    /// </summary>
    public sealed class Events(string model) : IEventsOut
    {
        private readonly StringBuilder _line = new();
        private readonly Decoder _utf8 = Encoding.UTF8.GetDecoder();
        private readonly string _id = Id("resp_");
        private readonly JsonArray _output = [];
        private int _sequence;
        private bool _started;
        private bool _ended;
        private JsonObject? _text;
        private readonly StringBuilder _words = new();
        private readonly StringBuilder _thought = new();
        private readonly Dictionary<int, (JsonObject Item, StringBuilder Arguments, int Index)> _calls = [];
        private string? _finish;
        private JsonObject? _usage;

        private string Event(string name, JsonObject data)
        {
            data["type"] = name;
            data["sequence_number"] = _sequence++;
            return "event: " + name + "\ndata: " + data.ToJsonString() + "\n\n";
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

        private void Start(StringBuilder out_)
        {
            if (_started)
            {
                return;
            }
            _started = true;
            out_.Append(Event("response.created", new JsonObject { ["response"] = Response(_id, model, "in_progress", [], null, null) }));
            out_.Append(Event("response.in_progress", new JsonObject { ["response"] = Response(_id, model, "in_progress", [], null, null) }));
        }

        private void CloseText(StringBuilder out_)
        {
            if (_text is null)
            {
                return;
            }
            var index = _output.Count;
            var id = Str(_text, "id")!;
            var text = _words.ToString();
            out_.Append(Event("response.output_text.done", new JsonObject { ["item_id"] = id, ["output_index"] = index, ["content_index"] = 0, ["text"] = text }));
            out_.Append(Event("response.content_part.done", new JsonObject
            {
                ["item_id"] = id, ["output_index"] = index, ["content_index"] = 0, ["part"] = new JsonObject { ["type"] = "output_text", ["text"] = text, ["annotations"] = new JsonArray() },
            }));
            var done = Message(id, text);
            out_.Append(Event("response.output_item.done", new JsonObject { ["output_index"] = index, ["item"] = done.DeepClone() }));
            _output.Add(done);
            _text = null;
            _words.Clear();
        }

        public string Close() => Finish(false, null);

        private string Finish(bool failed, string? why)
        {
            if (_ended)
            {
                return "";
            }
            _ended = true;
            var out_ = new StringBuilder();
            Start(out_);
            if (_thought.Length > 0)
            {
                var reasoning = new JsonObject { ["type"] = "reasoning", ["id"] = Id("rs_"), ["summary"] = new JsonArray(new JsonObject { ["type"] = "summary_text", ["text"] = _thought.ToString() }) };
                out_.Append(Event("response.output_item.added", new JsonObject { ["output_index"] = _output.Count, ["item"] = reasoning.DeepClone() }));
                out_.Append(Event("response.output_item.done", new JsonObject { ["output_index"] = _output.Count, ["item"] = reasoning.DeepClone() }));
                _output.Insert(0, reasoning);
            }
            CloseText(out_);
            foreach (var (item, args, index) in _calls.Values.OrderBy(c => c.Index))
            {
                item["arguments"] = args.ToString();
                item["status"] = "completed";
                out_.Append(Event("response.function_call_arguments.done", new JsonObject { ["item_id"] = Str(item, "id"), ["output_index"] = index, ["arguments"] = args.ToString() }));
                out_.Append(Event("response.output_item.done", new JsonObject { ["output_index"] = index, ["item"] = item.DeepClone() }));
                _output.Add(item);
            }
            var status = failed ? "failed" : _finish == "length" ? "incomplete" : "completed";
            var response = Response(_id, model, status, (JsonArray)_output.DeepClone(), Usage(_usage), _finish);
            if (failed)
            {
                response["error"] = new JsonObject { ["code"] = "server_error", ["message"] = why ?? "The model failed." };
            }
            out_.Append(Event(failed ? "response.failed" : status == "incomplete" ? "response.incomplete" : "response.completed", new JsonObject { ["response"] = response }));
            return out_.ToString();
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
                out_.Append(Close());
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
                out_.Append(Finish(true, Str(err, "message")));
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
                    _thought.Append(thought);
                }
                if (Str(delta, "content") is { Length: > 0 } words)
                {
                    var index = _output.Count;
                    if (_text is null)
                    {
                        _text = new JsonObject { ["type"] = "message", ["id"] = Id("msg_"), ["status"] = "in_progress", ["role"] = "assistant", ["content"] = new JsonArray() };
                        out_.Append(Event("response.output_item.added", new JsonObject { ["output_index"] = index, ["item"] = _text.DeepClone() }));
                        out_.Append(Event("response.content_part.added", new JsonObject
                        {
                            ["item_id"] = Str(_text, "id"), ["output_index"] = index, ["content_index"] = 0,
                            ["part"] = new JsonObject { ["type"] = "output_text", ["text"] = "", ["annotations"] = new JsonArray() },
                        }));
                    }
                    _words.Append(words);
                    out_.Append(Event("response.output_text.delta", new JsonObject { ["item_id"] = Str(_text, "id"), ["output_index"] = index, ["content_index"] = 0, ["delta"] = words }));
                }
                foreach (var call in (delta["tool_calls"] as JsonArray ?? []).OfType<JsonObject>())
                {
                    var i = call["index"] is JsonValue iv && iv.TryGetValue<int>(out var at) ? at : 0;
                    if (!_calls.TryGetValue(i, out var c))
                    {
                        CloseText(out_);
                        var item = new JsonObject
                        {
                            ["type"] = "function_call", ["id"] = Id("fc_"), ["call_id"] = Str(call, "id") ?? Id("call_"), ["name"] = Str(call["function"] as JsonObject, "name") ?? "", ["arguments"] = "",
                            ["status"] = "in_progress",
                        };
                        c = (item, new StringBuilder(), _output.Count + _calls.Count);
                        _calls[i] = c;
                        out_.Append(Event("response.output_item.added", new JsonObject { ["output_index"] = c.Index, ["item"] = item.DeepClone() }));
                    }
                    if (Str(call["function"] as JsonObject, "arguments") is { Length: > 0 } args)
                    {
                        c.Arguments.Append(args);
                        out_.Append(Event("response.function_call_arguments.delta", new JsonObject { ["item_id"] = Str(c.Item, "id"), ["output_index"] = c.Index, ["delta"] = args }));
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
