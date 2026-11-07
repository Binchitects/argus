using System.Text.Json.Nodes;

namespace CodeArena.Tests;

/// <summary>Models of other providers with the person's own key, beside Arena's: Anthropic's Messages API, OpenAI-compatible APIs, and Arena's admins' say.</summary>
public sealed class ProvidersTests : IDisposable
{
    private readonly FakeGateway _gateway = new();
    // Another provider of the person's: an OpenAI-compatible API (DeepSeek, a company's vLLM...), with its own models.
    private readonly FakeGateway _other = new() { Models = ["deep-chat"] };
    private readonly FakeMcp _mcp = new();

    public void Dispose()
    {
        _gateway.Dispose();
        _other.Dispose();
        _mcp.Dispose();
    }

    private Harness WithProvider() => new(_gateway, _mcp, c => c["providers"] = new JsonObject
    {
        ["local"] = new JsonObject { ["kind"] = "openai", ["url"] = _other.Url, ["apiKey"] = FakeGateway.Key },
    });

    [Fact]
    public async Task A_providers_model_is_asked_of_it_with_its_own_name_and_none_of_Arena_s_fields_when_Arena_s_admins_allow_it()
    {
        _mcp.Chats.ChatConfig = new JsonObject { ["model"] = "model-a", ["codeArenaProviders"] = true };
        _other.Answer = _ => Reply.Say("From the other provider.");
        using var h = WithProvider();
        Assert.Equal(0, await h.Run("", "-p", "hello", "--model", "local:deep-chat"));
        Assert.Contains("From the other provider.", h.Out);
        var sent = Assert.Single(_other.Requests);
        Assert.Equal("deep-chat", sent.Str("model"));
        Assert.Null(sent["metadata"]);
        Assert.DoesNotContain(_gateway.Requests, r => r.Str("model")?.Contains("deep-chat", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task Signed_in_to_Arena_whose_admins_have_not_allowed_them_a_providers_models_are_not_offered()
    {
        _mcp.Chats.ChatConfig = new JsonObject { ["model"] = "model-a" };
        using var h = WithProvider();
        Assert.Equal(1, await h.Run("", "-p", "hello", "--model", "local:deep-chat"));
        Assert.Contains("do not let Code Arena use other providers", h.Out + h.Err);
        Assert.Empty(_other.Requests);
        // Arena's own models are as they were.
        Assert.Equal(0, await h.Run("", "-p", "hello", "--model", "model-a"));
    }

    [Fact]
    public void A_known_provider_needs_only_its_name_and_any_other_its_address()
    {
        var anthropic = Providers.Parse("anthropic", null, "config.json");
        Assert.Equal(("anthropic", "https://api.anthropic.com", "ANTHROPIC_API_KEY"), (anthropic.Kind, anthropic.Url, anthropic.KeyEnv));
        Assert.Equal("openai", Providers.Parse("deepseek", null, "config.json").Kind);
        Assert.Equal("http://gpu:8000", Providers.Parse("local", new JsonObject { ["url"] = "http://gpu:8000/v1/" }, "config.json").Url);
        Assert.Throws<InvalidOperationException>(() => Providers.Parse("local", null, "config.json"));
        Assert.Throws<InvalidOperationException>(() => Providers.Parse("Bad Name", null, "config.json"));
        Assert.Throws<InvalidOperationException>(() => Providers.Parse("x", new JsonObject { ["url"] = "http://h", ["kind"] = "gemini" }, "config.json"));
        Assert.Equal("sk-from-env", Providers.KeyOf(anthropic, name => name == "ANTHROPIC_API_KEY" ? "sk-from-env" : null));
        Assert.Equal(("anthropic", "claude-x:latest"), Providers.Split("anthropic:claude-x:latest", ["anthropic"]));
        Assert.Null(Providers.Split("model-a", ["anthropic"]));
        Assert.Null(Providers.Split("other:model", ["anthropic"]));
    }

    [Fact]
    public void A_request_goes_to_Anthropic_as_its_Messages_API_takes_it()
    {
        var body = JsonNode.Parse("""
            {"model":"claude-x","max_tokens":4000,"reasoning_effort":"medium","temperature":0.2,
             "messages":[
               {"role":"system","content":"You are Code Arena."},
               {"role":"user","content":[{"type":"text","text":"What is this?"},{"type":"image_url","image_url":{"url":"data:image/png;base64,AAAA"}}]},
               {"role":"assistant","content":"Let me read it.","tool_calls":[
                 {"id":"c1","type":"function","function":{"name":"read_file","arguments":"{\"path\":\"a.cs\"}"}},
                 {"id":"c2","type":"function","function":{"name":"grep","arguments":"{\"pattern\":\"x\"}"}}]},
               {"role":"tool","tool_call_id":"c1","content":"class A {}"},
               {"role":"tool","tool_call_id":"c2","content":"a.cs:1"},
               {"role":"user","content":"Go on."}],
             "tools":[{"type":"function","function":{"name":"read_file","description":"Read a file.","parameters":{"type":"object","properties":{"path":{"type":"string"}}}}}]}
            """)!.AsObject();
        var sent = Providers.ToAnthropic(body);
        Assert.Equal("You are Code Arena.", sent["system"]![0]!["text"]!.GetValue<string>());
        var messages = sent["messages"]!.AsArray();
        Assert.Equal(["user", "assistant", "user"], messages.Select(m => m!["role"]!.GetValue<string>()));
        Assert.Equal("image", messages[0]!["content"]![1]!["type"]!.GetValue<string>());
        Assert.Equal("image/png", messages[0]!["content"]![1]!["source"]!["media_type"]!.GetValue<string>());
        Assert.Equal(["text", "tool_use", "tool_use"], messages[1]!["content"]!.AsArray().Select(b => b!["type"]!.GetValue<string>()));
        Assert.Equal("a.cs", messages[1]!["content"]![1]!["input"]!["path"]!.GetValue<string>());
        // Both results, then what the person said, in one turn of theirs.
        Assert.Equal(["tool_result", "tool_result", "text"], messages[2]!["content"]!.AsArray().Select(b => b!["type"]!.GetValue<string>()));
        Assert.Equal("c2", messages[2]!["content"]![1]!["tool_use_id"]!.GetValue<string>());
        Assert.Equal("read_file", sent["tools"]![0]!["name"]!.GetValue<string>());
        Assert.NotNull(sent["tools"]![0]!["input_schema"]!["properties"]);
        // Thinking within the answer's room; no temperature with it.
        Assert.Equal(8192, sent["thinking"]!["budget_tokens"]!.GetValue<int>());
        Assert.True(sent["max_tokens"]!.GetValue<int>() > 8192);
        Assert.Null(sent["temperature"]);
    }

    [Fact]
    public void Anthropic_s_stream_is_read_into_its_text_thinking_calls_usage_and_end()
    {
        var stream = new Providers.AnthropicStream();
        string[] events =
        [
            """{"type":"message_start","message":{"usage":{"input_tokens":10,"cache_read_input_tokens":90,"output_tokens":1}}}""",
            """{"type":"content_block_start","index":0,"content_block":{"type":"thinking"}}""",
            """{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"Hmm."}}""",
            """{"type":"content_block_start","index":1,"content_block":{"type":"text","text":""}}""",
            """{"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"Reading "}}""",
            """{"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"it."}}""",
            """{"type":"content_block_start","index":2,"content_block":{"type":"tool_use","id":"toolu_1","name":"read_file","input":{}}}""",
            """{"type":"content_block_delta","index":2,"delta":{"type":"input_json_delta","partial_json":"{\"path\":"}}""",
            """{"type":"content_block_delta","index":2,"delta":{"type":"input_json_delta","partial_json":"\"a.cs\"}"}}""",
            """{"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":42}}""",
            """{"type":"message_stop"}""",
        ];
        var shown = events.Select(stream.Read).ToList();
        Assert.Equal("Reading it.", stream.Text);
        Assert.Equal("Hmm.", stream.Thinking);
        Assert.Equal("Reading ", shown[4].Text);
        Assert.Equal("tool_calls", stream.Finish);
        Assert.True(stream.Ended);
        var call = Assert.Single(stream.ToolCalls!);
        Assert.Equal(("toolu_1", "read_file", """{"path":"a.cs"}"""), (call!["id"]!.GetValue<string>(), call["function"]!["name"]!.GetValue<string>(), call["function"]!["arguments"]!.GetValue<string>()));
        Assert.Equal(new TokenUsage(100, 90, 42), stream.Usage);
        Assert.Throws<GatewayException>(() => stream.Read("""{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}"""));
    }
}
